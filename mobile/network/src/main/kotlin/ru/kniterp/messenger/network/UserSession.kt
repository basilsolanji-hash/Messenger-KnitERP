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
@Serializable data class PendingRefresh(val request: RefreshRequest, val tokenId: String, val createdAtMillis: Long, val attempts: Int = 1) {
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
    @Synchronized fun requestCode(phone: String): Challenge {
        require(phone.matches(Regex("\\+[1-9][0-9]{1,14}"))) { "PHONE_INVALID" }
        val state = store.load()
        val prior = state.pendingLogin?.takeIf { it.phone == phone && it.challenge == null }
        val pending = prior ?: PendingLogin(UUID.randomUUID().toString(), phone)
        if (prior == null) store.save(state.copy(pendingLogin = pending, ended = null))
        val challenge = api.requestCode(SmsChallengeRequest(pending.requestId, phone, device.deviceId, device.registrationId,
            device.identityKey, Wire.base64(signer.authKey)))
        store.save(store.load().copy(pendingLogin = pending.copy(challenge = challenge)))
        return challenge
    }

    @Synchronized fun verifyCode(code: String): LoginResult {
        require(code.length == 6 && code.all { it in '0'..'9' }) { "CODE_FORMAT" }
        val pending = requireNotNull(store.load().pendingLogin?.takeIf { it.challenge != null }) { "NO_CHALLENGE" }
        val activation = api.verifyCode(SmsVerifyRequest(pending.challenge!!.challengeId, pending.phone, code, proof(pending)))
        return finish(pending, activation)
    }

    @Synchronized fun verifyPin(pin: String): LoginResult {
        require(pin.length == 6 && pin.all { it in '0'..'9' }) { "PIN_FORMAT" }
        val pending = requireNotNull(store.load().pendingLogin?.takeIf { it.pinRequired && it.challenge != null }) { "NO_PIN_STEP" }
        return finish(pending, api.verifyPin(PinVerifyRequest(pending.challenge!!.challengeId, pending.phone, pin, proof(pending))))
    }

    private fun proof(pending: PendingLogin) =
        Wire.base64(signer.sign(DeviceProof.activation(pending.challenge!!, pending.phone, device, signer.authKey)))

    private fun finish(pending: PendingLogin, activation: Activation): LoginResult = when (activation.status) {
        "ACTIVATED" -> {
            val issued = requireNotNull(activation.session)
            validateIssued(issued, device)
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
        val AMBIGUOUS_CHALLENGE = setOf("CHALLENGE_LOCKED", "CHALLENGE_NOT_FOUND", "CHALLENGE_USED")
    }
    private var access: Pair<String, Long>? = null
    internal var refreshes = 0
        private set

    @Synchronized fun install(issued: IssuedSession) {
        validateIssued(issued, device)
        require(store.load().session == StoredSession.of(issued)) { "Session is not durably installed" }
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

    private fun end(code: String): Nothing {
        access = null
        store.save(AuthState(ended = code))
        throw SessionEnded(code)
    }

    private fun refresh(): String {
        val state = store.load()
        val session = state.session ?: throw SessionEnded(state.ended ?: "NO_SESSION")
        require(session.deviceId == device.deviceId)
        var pending = session.pendingRefresh
        repeat(2) { pass ->
            pending?.let {
                // Never silently replace a request which might have consumed this refresh token.
                if (it.tokenId != session.tokenId || it.request.refreshToken != session.refreshToken)
                    end("REFRESH_OUTCOME_UNKNOWN")
                val age = clock() - it.createdAtMillis
                if (age !in 0 until REPLAY_MILLIS) end("REFRESH_RECOVERY_EXPIRED")
                if (it.attempts !in 0..1) end("REFRESH_RETRY_EXHAUSTED")
            }
            val issued = try {
                val prepared = pending ?: PendingRefresh(newRequest(session), session.tokenId, clock(), attempts = 0)
                val attempt = prepared.copy(attempts = prepared.attempts + 1)
                // Persist both bytes and attempt budget BEFORE sending, including across process restarts.
                store.save(state.copy(session = session.copy(pendingRefresh = attempt)))
                pending = attempt
                refreshes++
                api.refresh(attempt.request)
            } catch (e: ApiFailure) {
                when {
                    e.code in ENDED -> end(e.code)
                    e.code in AMBIGUOUS_CHALLENGE -> end("REFRESH_OUTCOME_UNKNOWN")
                    e.code == "CHALLENGE_EXPIRED" -> {
                        // The v1 server returns this only after proving this token is still unconsumed.
                        if (pass != 0) end("REFRESH_OUTCOME_UNKNOWN")
                        store.save(state.copy(session = session.copy(pendingRefresh = null)))
                        pending = null
                        return@repeat
                    }
                    else -> throw e
                }
            } // IO or storage failures retain the exact durable request and its budget.
            validateIssued(issued, device)
            require(issued.accountId == session.accountId && issued.generation == session.generation) { "Refresh changed identity" }
            val expiry = millis(issued.expiresAt) // Validate before committing away the recovery journal.
            store.save(AuthState(session = StoredSession.of(issued)))
            access = issued.accessToken to expiry
            return issued.accessToken
        }
        error("Refresh did not complete")
    }

    private fun newRequest(session: StoredSession): RefreshRequest {
        val challenge = api.refreshChallenge(device.deviceId)
        val payload = DeviceProof.refresh(challenge, session.accountId, session.tokenId, device, signer.authKey)
        return RefreshRequest(session.refreshToken, challenge.challengeId, Wire.base64(signer.sign(payload)))
    }

    private fun millis(iso: String) = OffsetDateTime.parse(iso).toInstant().toEpochMilli()
}

/** Validate server data before retiring the durable recovery journal. No token values enter errors. */
private fun validateIssued(issued: IssuedSession, device: LocalDevice) {
    Wire.id(issued.accountId)
    require(issued.deviceId == device.deviceId && issued.generation > 0) { "Invalid session binding" }
    require(issued.accessToken.length in 32..8192 && issued.accessToken.all { it.code in 33..126 }) { "Invalid access token" }
    val parts = issued.refreshToken.split('.')
    require(parts.size == 3 && parts[0] == "rt1") { "Invalid refresh token" }
    Wire.id(parts[1])
    require(parts[2].matches(Regex("[A-Za-z0-9_-]{43}"))) { "Invalid refresh token" }
    val bytes = java.util.Base64.getUrlDecoder().decode(parts[2])
    require(bytes.size == 32 && java.util.Base64.getUrlEncoder().withoutPadding().encodeToString(bytes) == parts[2]) { "Invalid refresh token" }
    val accessExpiry = OffsetDateTime.parse(issued.expiresAt)
    require(OffsetDateTime.parse(issued.refreshExpiresAt).toInstant() > accessExpiry.toInstant()) { "Invalid session expiry" }
}
