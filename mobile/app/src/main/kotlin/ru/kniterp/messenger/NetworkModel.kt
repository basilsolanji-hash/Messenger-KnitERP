package ru.kniterp.messenger

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import ru.kniterp.messenger.core.*
import ru.kniterp.messenger.network.*
import ru.kniterp.messenger.storage.AppDatabase
import ru.kniterp.messenger.storage.RoomVault
import java.util.UUID
import javax.net.ssl.SSLException

data class NetworkState(val loading: Boolean = true, val busy: Boolean = false, val active: Boolean = false,
    val origin: String = "", val accountId: String = "", val request: String = "", val peer: String = "",
    val messages: List<ChatMessage> = emptyList(), val pendingIds: Set<String> = emptySet(),
    val safetyNumber: String = "", val queuedId: String = "", val error: Int? = null)

class NetworkModel(application: Application) : AndroidViewModel(application) {
    private val mutable = MutableStateFlow(NetworkState())
    val state: StateFlow<NetworkState> = mutable
    private lateinit var vault: RoomVault
    private lateinit var enrollment: Enrollment
    private var api: PinnedApi? = null
    private var delivery: Delivery? = null
    private var saved: SavedConnection? = null
    private var started = false

    fun start() {
        if (started) return
        started = true
        operation {
            vault = RoomVault(AppDatabase.get(getApplication()), "network-phone-v1")
            enrollment = Enrollment(vault).apply { initialize() }
            saved = enrollment.saved()
            saved?.let { install(it) }
        }
    }
    fun exportRequest(name: String) = operation {
        mutable.value = mutable.value.copy(request = Wire.json.encodeToString(enrollment.request(name)))
    }
    fun importConnection(json: String) = operation {
        require(json.toByteArray(Charsets.UTF_8).size <= 16384)
        val config = Wire.json.decodeFromString<TestConnection>(json)
        // Validate the locked origin/pins BEFORE sending a credential to any host.
        enrollment.validateTarget(config)
        val identity = PinnedApi.connect(config).use { it.me() }
        val value = enrollment.accept(config, identity)
        saved = value
        install(value)
        val bundle = enrollment.signal.publicBundle()
        if (bundle.preKeys.isNotEmpty() && bundle.pqPreKeys.isNotEmpty()) requireNotNull(api).publish(bundle)
    }
    private fun install(value: SavedConnection) {
        require(enrollment.signal.localDevice() == value.identity.device())
        api?.close()
        api = PinnedApi.connect(value.connection, value.expiresAtMillis())
        delivery = Delivery(vault, requireNotNull(api))
    }
    fun selectPeer(peer: String) = operation {
        Wire.id(peer)
        require(peer != enrollment.signal.localDevice().accountId)
        mutable.value = mutable.value.copy(peer = peer)
    }
    fun synchronize() = operation {
        val report = requireNotNull(delivery).synchronize(maxPages = 3, maxOutgoing = 20)
        if (report.rejectedSequences.isNotEmpty()) mutable.value = mutable.value.copy(error = R.string.net_rejected)
    }
    fun send(text: String) = operation {
        val peer = mutable.value.peer
        val item = requireNotNull(delivery).queue(peer, text, UUID.randomUUID().toString())
        // Clear the UI draft only AFTER the local encrypted outbox has committed.
        mutable.value = mutable.value.copy(queuedId = item.clientMessageId)
        snapshot()
        val report = requireNotNull(delivery).synchronize(maxPages = 3, maxOutgoing = 20)
        if (report.rejectedSequences.isNotEmpty()) mutable.value = mutable.value.copy(error = R.string.net_rejected)
    }
    private fun snapshot() {
        if (!::enrollment.isInitialized) return
        val signal = enrollment.signal
        val peer = mutable.value.peer
        mutable.value = mutable.value.copy(loading = false,
            active = saved?.let { it.expiresAtMillis() > System.currentTimeMillis() + 1000 } == true,
            origin = saved?.connection?.server?.canonicalOrigin().orEmpty(),
            accountId = signal.deviceOrNull()?.accountId.orEmpty(), messages = signal.history(),
            pendingIds = signal.pendingIds(),
            safetyNumber = if (peer.isNotEmpty() && signal.hasPeer(peer)) signal.safetyNumber(peer) else "")
    }
    private fun operation(action: () -> Unit) {
        if (mutable.value.busy) return
        mutable.value = mutable.value.copy(busy = true, error = null)
        viewModelScope.launch {
            try { withContext(Dispatchers.IO) { action(); snapshot() } }
            catch (failure: Exception) {
                if (failure is kotlinx.coroutines.CancellationException) throw failure
                val message = when (failure) {
                    is ExpiredSession -> R.string.net_expired
                    is ApiFailure -> if (failure.status == 401) R.string.net_expired else R.string.net_server_error
                    is IdentityChanged, is DeviceChanged -> R.string.net_identity_changed
                    is SSLException, is java.io.IOException -> R.string.net_network_error
                    is IllegalArgumentException, is kotlinx.serialization.SerializationException -> R.string.net_bad_config
                    else -> R.string.net_operation_error
                }
                withContext(Dispatchers.IO) { runCatching { snapshot() } }
                mutable.value = mutable.value.copy(loading = false, error = message,
                    active = if (failure is ExpiredSession || (failure is ApiFailure && failure.status == 401)) false else mutable.value.active)
            } finally { mutable.value = mutable.value.copy(busy = false) }
        }
    }
    override fun onCleared() { api?.close(); super.onCleared() }
}
