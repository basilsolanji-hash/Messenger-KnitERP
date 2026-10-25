package ru.kniterp.messenger

import android.app.Application
import android.content.pm.ApplicationInfo
import androidx.annotation.VisibleForTesting
import androidx.lifecycle.AndroidViewModel
import java.util.UUID
import javax.net.ssl.SSLException
import javax.net.ssl.X509TrustManager
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.serialization.Serializable
import ru.kniterp.messenger.core.*
import ru.kniterp.messenger.network.*
import ru.kniterp.messenger.storage.*

data class AuthUiState(val loading: Boolean = true, val busy: Boolean = false, val origin: String = "",
    val login: LoginView = LoginView(), val accountId: String = "", val ended: String? = null,
    val ready: Boolean = false, val notice: Int? = null, val error: Int? = null,
    val peer: String = "", val messages: List<ChatMessage> = emptyList(), val pendingIds: Set<String> = emptySet(),
    val safetyNumber: String = "", val queuedId: String = "",
    /** Public challenge id: the test-stand operator reads the Fake SMS code by it. Not a secret without the operator key. */
    val challengeId: String = "",
    /** Outgoing messages held after the peer moved to another device: shown as undelivered, never re-sent silently. */
    val heldIds: Set<String> = emptySet())

/** The screen never owns a session or closes it on rotation/navigation. */
class AuthModel(application: Application) : AndroidViewModel(application) {
    private val owner = AuthOwners.get(application)
    val state: StateFlow<AuthUiState> = owner.state
    fun start() = owner.start()
    fun configureServer(json: String) = owner.configureServer(json)
    fun submitPhone(country: Country, typed: String) = owner.login { it.submitPhone(country, typed) }
    fun resend() = owner.login { it.resend() }
    fun submitCode(code: String) = owner.login { it.submitCode(code) }
    fun submitPin(pin: String) = owner.login { it.submitPin(pin) }
    fun changeNumber() = owner.login { it.changeNumber() }
    fun setPin(pin: String, currentPin: String?) = owner.setPin(pin, currentPin)
    fun disablePin(pin: String) = owner.disablePin(pin)
    fun logout() = owner.logout()
    fun retryActivation() = owner.retryActivation()
    fun newLogin() = owner.newLogin()
    fun selectPeer(peer: String) = owner.selectPeer(peer)
    fun synchronize() = owner.synchronize()
    fun send(text: String) = owner.send(text)
}

/** Exactly one controller and UserSession per active profile in this application process. */
internal object AuthOwners {
    private var instance: AuthController? = null
    @Synchronized fun get(application: Application): AuthController =
        instance ?: AuthController(application).also { instance = it }
    /** Instrumentation only: forget the in-memory owner as process death would. Durable state is untouched. */
    @VisibleForTesting @Synchronized fun simulateProcessDeath() { instance?.close(); instance = null }
}

@Serializable private data class ActivationProfile(val deviceId: String, val server: ServerEndpoint, val prepared: Boolean = false)

/**
 * A replaced trust root is honoured only by a debuggable build; release builds have no way to set one (see TestSeams).
 * Pins and hostname verification apply either way.
 */
internal fun testTrust(debuggable: Boolean, candidate: X509TrustManager?): X509TrustManager? = candidate.takeIf { debuggable }

/** [registryName] and [wrap] exist for instrumentation: a second synthetic phone and injected transport failures. */
internal class AuthController(private val application: Application, registryName: String = "auth-registry",
                              private val wrap: (MessageApi) -> MessageApi = { it }) {
    private val trust = testTrust((application.applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE) != 0, TestSeams.trust)
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val mutable = MutableStateFlow(AuthUiState())
    val state: StateFlow<AuthUiState> = mutable
    private val db by lazy { AppDatabase.get(application) }
    private val registry by lazy { RoomVault(db, registryName) }
    private var server: ServerEndpoint? = null
    private var profile: Profile? = null
    private var previousEnded: String? = null
    private var started = false

    private inner class Profile(val vault: RoomVault, val device: LocalDevice, signer: DeviceSigner,
                          val endpoint: ServerEndpoint) {
        val store = VaultAuthStore(vault)
        val signal = SignalClient(vault)
        val auth = AuthApi.connect(endpoint, trust)
        val flow = LoginFlow(Login(auth, store, signer, device))
        val session = UserSession(auth, store, signer, device)
        val api = PinnedApi.session(endpoint, session, trust)
        private val messages = wrap(api)
        val activation = ActivationCompletion(vault, store, device, messages)
        // A recipient on a new device is held, not retried blindly, and must not block the inbox.
        val delivery = Delivery(vault, messages) { it is ApiFailure && it.code == "RECIPIENT_DEVICE_CHANGED" }
        fun close() { api.close(); auth.close() }
    }

    /** Current activation's device id; a new activation always gets a new one. */
    @VisibleForTesting val deviceId: String? get() = profile?.device?.deviceId

    fun close() { scope.cancel(); profile?.close() }

    @Synchronized fun start(): Job? {
        if (started) return null
        started = true
        return operation {
            val saved = record()
            server = saved?.server
            if (saved != null) {
                val ended = VaultAuthStore(vault(saved.deviceId)).load().ended
                if (ended == null) profile = open(saved)
                else { previousEnded = ended; create(saved.server) }
                profile?.takeIf { it.store.load().session != null }?.activation?.complete()
            }
        }
    }

    fun configureServer(json: String) = operation {
        require(json.toByteArray().size <= 4096)
        val endpoint = Wire.json.decodeFromString<ServerEndpoint>(json).also { it.canonicalOrigin() }
        val prior = record()
        // Check durable state even when opening its Keystore key previously failed.
        require(prior == null || VaultAuthStore(vault(prior.deviceId)).load().session == null) { "Account already active" }
        if (prior != null && sameServer(prior.server, endpoint) && profile != null) return@operation
        profile?.close(); profile = null
        create(endpoint) // A different origin/pin set always gets a fresh unbound profile and no old challenge.
    }

    fun login(step: (LoginFlow) -> LoginView) = operation {
        val current = requireNotNull(profile)
        val view = step(current.flow)
        when {
            view.step == LoginStep.ACTIVATED -> current.activation.complete()
            view.error == LoginError.DEVICE_REJECTED -> {
                current.store.save(AuthState(ended = "DEVICE_PROOF_INVALID")); rotate()
            }
        }
    }
    fun retryActivation() = operation { requireNotNull(profile).activation.complete() }
    fun setPin(pin: String, old: String?) = operation(R.string.auth_pin_saved) { active().session.setPin(pin, old) }
    fun disablePin(pin: String) = operation(R.string.auth_pin_disabled) { active().session.disablePin(pin) }
    fun logout() = operation { requireNotNull(profile).session.logout(); rotate() }
    fun newLogin() = operation {
        // Explicit recovery creates a fresh activation; it never repairs/replaces keys in an old vault.
        val saved = requireNotNull(record())
        VaultAuthStore(vault(saved.deviceId)).save(AuthState(ended = "LOCAL_RECOVERY"))
        server = saved.server; rotate()
    }
    fun selectPeer(peer: String) = operation {
        val current = active()
        Wire.id(peer); require(peer != current.signal.localDevice().accountId)
        mutable.value = mutable.value.copy(peer = peer)
    }
    fun synchronize() = operation { report(active().delivery.synchronize(maxPages = 3, maxOutgoing = 20)) }
    fun send(text: String) = operation {
        val current = active()
        val item = current.delivery.queue(mutable.value.peer, text)
        mutable.value = mutable.value.copy(queuedId = item.clientMessageId) // Clear only after encrypted commit.
        snapshot()
        report(current.delivery.synchronize(maxPages = 3, maxOutgoing = 20))
    }
    /** A changed peer device or identity is shown, never accepted silently; affected messages stay unaccepted. */
    private fun report(report: DeliveryReport) {
        val error = when {
            report.heldIds.isNotEmpty() -> R.string.net_identity_changed
            report.rejectedSequences.isNotEmpty() -> R.string.net_rejected
            else -> return
        }
        mutable.value = mutable.value.copy(error = error)
    }
    private fun active() = requireNotNull(profile).also { check(it.activation.ready()) { "Activation incomplete" } }

    private fun record(): ActivationProfile? = registry.transaction {
        it.get("auth", "profile")?.decodeToString()?.let { json -> Wire.json.decodeFromString<ActivationProfile>(json) }
    }
    private fun save(record: ActivationProfile) = registry.transaction {
        it.put("auth", "profile", Wire.json.encodeToString(record).toByteArray())
    }
    private fun vault(id: String) = RoomVault(db, "auth-${Wire.id(id)}")
    private fun sameServer(a: ServerEndpoint, b: ServerEndpoint) =
        a.canonicalOrigin() == b.canonicalOrigin() && a.pins.toSet() == b.pins.toSet()

    private fun open(record: ActivationProfile): Profile {
        record.server.canonicalOrigin()
        val vault = vault(record.deviceId)
        val signal = SignalClient(vault)
        val signer = if (record.prepared) DeviceAuthKey.open(record.deviceId) else {
            // The preparation journal is committed BEFORE creating any keys or sending HTTP.
            // Only this never-activated stage may finish an interrupted key creation.
            signal.initialize()
            if (DeviceAuthKey.exists(record.deviceId)) DeviceAuthKey.open(record.deviceId) else DeviceAuthKey.create(record.deviceId)
        }
        val local = LocalDevice(record.deviceId, signal.registrationId(), signal.identityPublic())
        if (!record.prepared) save(record.copy(prepared = true))
        return Profile(vault, local, signer, record.server)
    }
    private fun create(endpoint: ServerEndpoint) {
        val fresh = ActivationProfile(UUID.randomUUID().toString(), endpoint)
        save(fresh)
        server = endpoint
        profile = open(fresh)
        mutable.value = mutable.value.copy(peer = "", messages = emptyList(), pendingIds = emptySet(), safetyNumber = "", queuedId = "")
    }
    private fun rotate() {
        profile?.store?.load()?.ended?.let { previousEnded = it }
        profile?.close(); profile = null
        create(requireNotNull(server))
    }
    private fun snapshot() {
        val current = profile
        val stored = current?.store?.load()
        val ready = current?.activation?.ready() == true
        val peer = mutable.value.peer
        mutable.value = mutable.value.copy(loading = false, origin = server?.canonicalOrigin().orEmpty(),
            login = current?.flow?.view() ?: LoginView(), accountId = stored?.session?.accountId.orEmpty(),
            ready = ready, ended = if (stored?.session != null) null else previousEnded,
            challengeId = stored?.pendingLogin?.challenge?.challengeId.orEmpty(),
            heldIds = current?.delivery?.heldIds().orEmpty(),
            messages = current?.signal?.history().orEmpty(), pendingIds = current?.signal?.pendingIds().orEmpty(),
            safetyNumber = if (ready && peer.isNotEmpty() && current!!.signal.hasPeer(peer)) current.signal.safetyNumber(peer) else "")
    }

    /** One operation at a time; a call while busy is dropped and returns null. */
    @Synchronized private fun operation(notice: Int? = null, action: () -> Unit): Job? {
        if (mutable.value.busy) return null
        mutable.value = mutable.value.copy(busy = true, error = null, notice = null)
        return scope.launch {
            try { action(); snapshot(); mutable.value = mutable.value.copy(notice = notice) }
            catch (failure: Exception) {
                if (failure is CancellationException) throw failure
                val message = when (failure) {
                    is SessionEnded -> null
                    is ApiFailure -> when (failure.code) {
                        "PIN_INVALID" -> R.string.auth_err_pin
                        "PIN_LOCKED" -> R.string.auth_err_pin_locked
                        "PIN_REQUIRED" -> R.string.auth_err_pin_required
                        "PIN_TOO_SIMPLE", "PIN_FORMAT" -> R.string.auth_err_pin_simple
                        "REAUTH_REQUIRED" -> R.string.auth_err_reauth
                        "RECIPIENT_DEVICE_CHANGED" -> R.string.net_identity_changed
                        else -> R.string.auth_err_server
                    }
                    is SSLException, is java.io.IOException -> R.string.auth_err_network
                    is IdentityChanged, is DeviceChanged -> R.string.net_identity_changed
                    is IllegalArgumentException, is kotlinx.serialization.SerializationException -> R.string.net_bad_config
                    else -> R.string.net_operation_error
                }
                if (failure is SessionEnded) runCatching { rotate() }
                runCatching { snapshot() }
                mutable.value = mutable.value.copy(loading = false, error = message)
            } finally { mutable.value = mutable.value.copy(busy = false) }
        }
    }
}
