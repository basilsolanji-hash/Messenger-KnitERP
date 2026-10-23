package ru.kniterp.messenger

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import java.util.UUID
import javax.net.ssl.SSLException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import ru.kniterp.messenger.core.*
import ru.kniterp.messenger.network.*
import ru.kniterp.messenger.storage.AppDatabase
import ru.kniterp.messenger.storage.KeystoreSigner
import ru.kniterp.messenger.storage.RoomVault

/** Holds no code, PIN or token: those live only in the call that uses them and in SQLCipher. */
data class AuthUiState(val loading: Boolean = true, val busy: Boolean = false, val origin: String = "",
    val login: LoginView = LoginView(), val accountId: String = "", val ended: String? = null,
    val notice: Int? = null, val error: Int? = null)

/**
 * SMS login for the network profile. Every activation gets its OWN vault profile (deviceId, Signal keys, Keystore key):
 * a profile bound to one device is never rebound. Ended profiles stay on the phone with their history, read-only.
 */
class AuthModel(application: Application) : AndroidViewModel(application) {
    private val mutable = MutableStateFlow(AuthUiState())
    val state: StateFlow<AuthUiState> = mutable
    private val db by lazy { AppDatabase.get(getApplication()) }
    private val registry by lazy { RoomVault(db, "auth-registry") }
    private var server: ServerEndpoint? = null
    private var profile: Profile? = null
    private var previousEnded: String? = null // Why the last activation ended; shown on the fresh login screen.
    private var started = false

    private class Profile(val vault: RoomVault, val device: LocalDevice, val signer: DeviceSigner, val store: VaultAuthStore,
                          val auth: AuthApi) {
        val signal = SignalClient(vault)
        val flow = LoginFlow(Login(auth, store, signer, device))
        val session = UserSession(auth, store, signer, device)
    }

    fun start() {
        if (started) return
        started = true
        operation {
            server = registry.transaction { it.get("auth", "server") }?.let { Wire.json.decodeFromString<ServerEndpoint>(it.decodeToString()) }
            server?.let { profile = openOrCreate(it) }
        }
    }

    /** The server origin and SPKI pins are public configuration. Once an account is active they cannot change. */
    fun configureServer(json: String) = operation {
        require(json.toByteArray(Charsets.UTF_8).size <= 4096)
        val endpoint = Wire.json.decodeFromString<ServerEndpoint>(json).also { it.url() }
        require(profile?.store?.load()?.session == null) { "Server is pinned while an account is active" }
        registry.transaction { it.put("auth", "server", Wire.json.encodeToString(endpoint).toByteArray()) }
        server = endpoint
        profile?.auth?.close()
        profile = openOrCreate(endpoint)
    }

    fun submitPhone(country: Country, typed: String) = login { it.submitPhone(country, typed) }
    fun resend() = login { it.resend() }
    fun submitCode(code: String) = login { it.submitCode(code) }
    fun submitPin(pin: String) = login { it.submitPin(pin) }
    fun changeNumber() = login { it.changeNumber() }

    fun setPin(pin: String, currentPin: String?) = operation(R.string.auth_pin_saved) { requireNotNull(profile).session.setPin(pin, currentPin) }
    fun disablePin(currentPin: String) = operation(R.string.auth_pin_disabled) { requireNotNull(profile).session.disablePin(currentPin) }
    fun logout() = operation {
        requireNotNull(profile).session.logout()
        rotate()
    }

    private fun login(step: (LoginFlow) -> LoginView) = operation {
        val current = requireNotNull(profile)
        val view = step(current.flow)
        when {
            view.step == LoginStep.ACTIVATED -> activated(current)
            // Keys the server will not accept for this activation: start over with a fresh profile, keep the number.
            view.error == LoginError.DEVICE_REJECTED -> rotate()
        }
    }

    private fun activated(current: Profile) {
        val session = requireNotNull(current.store.load().session)
        current.signal.bind(Device(session.accountId, session.deviceId, session.generation))
        // The session refreshes itself with a signed challenge; no token passes through UI state.
        PinnedApi.session(requireNotNull(server), current.session).use { it.publish(current.signal.publicBundle()) }
    }

    private fun openOrCreate(endpoint: ServerEndpoint): Profile {
        val existing = registry.transaction { it.get("auth", "current")?.decodeToString() }
        if (existing != null) {
            val vault = RoomVault(db, "auth-$existing")
            val store = VaultAuthStore(vault)
            val ended = store.load().ended
            if (ended == null) {
                val signal = SignalClient(vault)
                return Profile(vault, LocalDevice(existing, signal.registrationId(), signal.identityPublic()),
                    KeystoreSigner.open(existing), store, AuthApi.connect(endpoint))
            }
            previousEnded = ended
        }
        return create(endpoint)
    }

    private fun create(endpoint: ServerEndpoint): Profile {
        val deviceId = UUID.randomUUID().toString()
        val vault = RoomVault(db, "auth-$deviceId")
        val signal = SignalClient(vault).apply { initialize() }
        val signer = KeystoreSigner.create(deviceId)
        registry.transaction { it.put("auth", "current", deviceId.toByteArray()) }
        return Profile(vault, LocalDevice(deviceId, signal.registrationId(), signal.identityPublic()), signer,
            VaultAuthStore(vault), AuthApi.connect(endpoint))
    }

    private fun rotate() {
        profile?.store?.load()?.ended?.let { previousEnded = it }
        profile?.auth?.close()
        profile = create(requireNotNull(server))
    }

    private fun snapshot() {
        val current = profile
        val stored = current?.store?.load()
        mutable.value = mutable.value.copy(loading = false, origin = server?.canonicalOrigin().orEmpty(),
            login = current?.flow?.view() ?: LoginView(), accountId = stored?.session?.accountId.orEmpty(),
            ended = if (stored?.session != null) null else previousEnded)
    }

    private fun operation(notice: Int? = null, action: () -> Unit) {
        if (mutable.value.busy) return
        mutable.value = mutable.value.copy(busy = true, error = null, notice = null)
        viewModelScope.launch {
            try {
                withContext(Dispatchers.IO) { action(); snapshot() }
                mutable.value = mutable.value.copy(notice = notice)
            } catch (failure: Exception) {
                if (failure is kotlinx.coroutines.CancellationException) throw failure
                val message = when (failure) {
                    is SessionEnded -> null // Shown through state.ended.
                    is ApiFailure -> when (failure.code) {
                        "PIN_INVALID" -> R.string.auth_err_pin
                        "PIN_LOCKED" -> R.string.auth_err_pin_locked
                        "PIN_REQUIRED" -> R.string.auth_err_pin_required
                        "PIN_TOO_SIMPLE", "PIN_FORMAT" -> R.string.auth_err_pin_simple
                        "REAUTH_REQUIRED" -> R.string.auth_err_reauth
                        else -> R.string.auth_err_server
                    }
                    is SSLException, is java.io.IOException -> R.string.auth_err_network
                    is IllegalArgumentException, is kotlinx.serialization.SerializationException -> R.string.net_bad_config
                    else -> R.string.net_operation_error
                }
                // Revoked, reused or expired: this activation is over. Continue on a fresh profile; history stays.
                if (failure is SessionEnded) withContext(Dispatchers.IO) { runCatching { rotate() } }
                withContext(Dispatchers.IO) { runCatching { snapshot() } }
                mutable.value = mutable.value.copy(loading = false, error = message)
            } finally { mutable.value = mutable.value.copy(busy = false) }
        }
    }

    override fun onCleared() { profile?.auth?.close(); super.onCleared() }
}
