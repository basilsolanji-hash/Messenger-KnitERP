package ru.kniterp.messenger.network

import java.time.OffsetDateTime
import java.util.UUID
import kotlinx.serialization.Serializable
import ru.kniterp.messenger.core.Vault
import ru.kniterp.messenger.core.Wire

@Serializable data class PendingLogin(val requestId: String, val phone: String, val challenge: Challenge? = null, val pinRequired: Boolean = false) {
    override fun toString() = "PendingLogin(pinRequired=$pinRequired, <redacted>)"
}
/** A refresh request already signed and possibly already processed by the server. Replayed byte-for-byte after a lost response. */
@Serializable data class PendingRefresh(val request: RefreshRequest, val tokenId: String, val createdAtMillis: Long) {
    override fun toString() = "PendingRefresh(<redacted>)"
}
@Serializable data class StoredSession(val accountId: String, val deviceId: String, val generation: Int,
    val refreshToken: String, val refreshExpiresAt: String, val pendingRefresh: PendingRefresh? = null) {
    override fun toString() = "StoredSession(accountId=$accountId, deviceId=$deviceId, generation=$generation, <redacted>)"
    val tokenId: String get() = refreshToken.split('.').let { require(it.size == 3 && it[0] == "rt1"); Wire.id(it[1]) }
    companion object {
        fun of(issued: IssuedSession) = StoredSession(issued.accountId, issued.deviceId, issued.generation, issued.refreshToken, issued.refreshExpiresAt)
    }
}
/** Whole auth state in ONE record, so every transition is a single atomic write. [ended] keeps the server's reason. */
@Serializable data class AuthState(val pendingLogin: PendingLogin? = null, val session: StoredSession? = null, val ended: String? = null) {
    override fun toString() = "AuthState(login=${pendingLogin != null}, session=${session != null}, ended=$ended)"
}

interface AuthStore {
    fun load(): AuthState
    fun save(state: AuthState)
}

/** Stores auth state inside this phone profile's SQLCipher vault, next to its Signal keys. */
class VaultAuthStore(private val vault: Vault) : AuthStore {
    override fun load(): AuthState = vault.transaction { r ->
        r.get("auth", "state")?.let { Wire.json.decodeFromString<AuthState>(it.toString(Charsets.UTF_8)) } ?: AuthState()
    }
    override fun save(state: AuthState) = vault.transaction { it.put("auth", "state", Wire.json.encodeToString(state).toByteArray(Charsets.UTF_8)) }
}

class SessionEnded(val code: String) : IllegalStateException("Session ended: $code")

sealed interface LoginResult {
    data class Activated(val session: IssuedSession) : LoginResult
    data object PinRequired : LoginResult
}

/** SMS login and PIN step for one activation (one deviceId, one key set). Network calls never hold a vault transaction. */
class Login(private val api: AuthApi, private val store: AuthStore, private val signer: DeviceSigner, private val device: LocalDevice) {
    /** Retries after a lost response reuse the saved requestId: the server returns the same challenge and sends no new SMS. */
    fun requestCode(phone: String): Challenge {
        val state = store.load()
        val prior = state.pendingLogin?.takeIf { it.phone == phone && it.challenge == null }
        val pending = prior ?: PendingLogin(UUID.randomUUID().toString(), phone)
        if (prior == null) store.save(state.copy(pendingLogin = pending, ended = null))
        val challenge = api.requestCode(SmsChallengeRequest(pending.requestId, phone, device.deviceId, device.registrationId,
            device.identityKey, Wire.base64(signer.authKey)))
        store.save(store.load().copy(pendingLogin = pending.copy(challenge = challenge)))
        return challenge
    }

    fun verifyCode(code: String): LoginResult {
        require(code.length == 6 && code.all { it in '0'..'9' }) { "CODE_FORMAT" }
        val pending = requireNotNull(store.load().pendingLogin?.takeIf { it.challenge != null }) { "NO_CHALLENGE" }
        val activation = api.verifyCode(SmsVerifyRequest(pending.challenge!!.challengeId, pending.phone, code, proof(pending)))
        return finish(pending, activation)
    }

    fun verifyPin(pin: String): LoginResult {
        require(pin.length == 6 && pin.all { it in '0'..'9' }) { "PIN_FORMAT" }
        val pending = requireNotNull(store.load().pendingLogin?.takeIf { it.pinRequired && it.challenge != null }) { "NO_PIN_STEP" }
        return finish(pending, api.verifyPin(PinVerifyRequest(pending.challenge!!.challengeId, pending.phone, pin, proof(pending))))
    }

    private fun proof(pending: PendingLogin) =
        Wire.base64(signer.sign(DeviceProof.activation(pending.challenge!!, pending.phone, device, signer.authKey)))

    private fun finish(pending: PendingLogin, activation: Activation): LoginResult = when (activation.status) {
        "ACTIVATED" -> {
            val issued = requireNotNull(activation.session)
            require(issued.deviceId == device.deviceId) { "Session belongs to another device" }
            store.save(AuthState(session = StoredSession.of(issued)))
            LoginResult.Activated(issued)
        }
        "PIN_REQUIRED" -> { store.save(store.load().copy(pendingLogin = pending.copy(pinRequired = true))); LoginResult.PinRequired }
        else -> throw IllegalStateException("Unexpected activation status")
    }
}

/**
 * Access tokens for one activated phone. Refresh is single-flight: callers serialize on this object, and a 401 for a token
 * that was already replaced does not trigger a second refresh. A lost refresh response is retried with the identical
 * signed request; a new challenge with an already-sent token would look like theft to the server and revoke the session.
 */
class UserSession(private val api: AuthApi, private val store: AuthStore, private val signer: DeviceSigner,
                  private val device: LocalDevice, private val clock: () -> Long = System::currentTimeMillis) : AccessTokens {
    private companion object {
        const val EARLY_MILLIS = 60_000L
        const val REPLAY_MILLIS = 110_000L // Server accepts the identical request for 2 minutes after processing it.
        val ENDED = setOf("REFRESH_REUSED", "REFRESH_INVALID", "SESSION_REVOKED", "SESSION_EXPIRED", "DEVICE_PROOF_INVALID")
        val STALE_CHALLENGE = setOf("CHALLENGE_EXPIRED", "CHALLENGE_LOCKED", "CHALLENGE_NOT_FOUND", "CHALLENGE_USED")
    }
    private var access: Pair<String, Long>? = null
    internal var refreshes = 0
        private set

    @Synchronized fun install(issued: IssuedSession) {
        require(issued.deviceId == device.deviceId)
        access = issued.accessToken to millis(issued.expiresAt)
    }

    @Synchronized override fun current(): String {
        access?.let { (token, expires) -> if (expires - clock() > EARLY_MILLIS) return token }
        return refresh()
    }

    @Synchronized override fun rejected(token: String): Boolean {
        if (access?.first == token) access = null
        return true
    }

    fun setPin(pin: String, currentPin: String?) = withAccess { api.setPin(it, SetPinRequest(pin, currentPin)) }
    fun disablePin(currentPin: String) = withAccess { api.disablePin(it, DisablePinRequest(currentPin)) }

    @Synchronized private fun withAccess(block: (String) -> Unit) {
        val token = current()
        try { block(token) } catch (e: ApiFailure) {
            if (e.status != 401) throw e
            rejected(token)
            block(current())
        }
    }

    @Synchronized fun logout() {
        val token = access?.first ?: runCatching { current() }.getOrNull()
        if (token != null) runCatching { api.logout(token) } // The local session ends even if the server is unreachable.
        access = null
        store.save(AuthState(ended = "LOGOUT"))
    }

    private fun refresh(): String {
        val state = store.load()
        val session = state.session ?: throw SessionEnded(state.ended ?: "NO_SESSION")
        require(session.deviceId == device.deviceId)
        var pending = session.pendingRefresh?.takeIf { it.tokenId == session.tokenId && clock() - it.createdAtMillis < REPLAY_MILLIS }
        repeat(2) {
            val issued = try {
                val request = pending?.request ?: newRequest(session).also {
                    pending = PendingRefresh(it, session.tokenId, clock())
                    store.save(state.copy(session = session.copy(pendingRefresh = pending))) // Persist BEFORE sending.
                }
                refreshes++
                api.refresh(request)
            } catch (e: ApiFailure) {
                when (e.code) {
                    in ENDED -> { access = null; store.save(AuthState(ended = e.code)); throw SessionEnded(e.code) }
                    in STALE_CHALLENGE -> { pending = null; return@repeat } // Token was not consumed: a fresh challenge is safe.
                    else -> throw e
                }
            } // An IOException propagates and leaves the pending request stored for an identical retry.
            require(issued.deviceId == device.deviceId && issued.accountId == session.accountId)
            store.save(AuthState(session = StoredSession.of(issued))) // New pair is durable before first use.
            access = issued.accessToken to millis(issued.expiresAt)
            return issued.accessToken
        }
        throw IllegalStateException("Refresh challenge kept expiring")
    }

    private fun newRequest(session: StoredSession): RefreshRequest {
        val challenge = api.refreshChallenge(device.deviceId)
        val payload = DeviceProof.refresh(challenge, session.accountId, session.tokenId, device, signer.authKey)
        return RefreshRequest(session.refreshToken, challenge.challengeId, Wire.base64(signer.sign(payload)))
    }

    private fun millis(iso: String) = OffsetDateTime.parse(iso).toInstant().toEpochMilli()
}
