package ru.kniterp.messenger

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import ru.kniterp.messenger.core.*
import ru.kniterp.messenger.storage.RoomVault
import ru.kniterp.messenger.storage.VaultDatabase
import java.util.UUID

data class DemoState(
    val loading: Boolean = true, val busy: Boolean = false, val messages: List<ChatMessage> = emptyList(),
    val safetyNumber: String = "", val error: Int? = null,
)

/** Two independent LOCAL profiles. No server or remote person is impersonated by this demo. */
class DemoModel(application: Application) : AndroidViewModel(application) {
    private val mutable = MutableStateFlow(DemoState())
    val state: StateFlow<DemoState> = mutable
    private lateinit var db: VaultDatabase
    private lateinit var alice: SignalClient
    private lateinit var bob: SignalClient

    private var started = false
    fun start(welcome: String) {
        if (started) return
        started = true
        viewModelScope.launch { load(welcome) }
    }
    private suspend fun load(welcome: String) {
        try {
            val value = withContext(Dispatchers.IO) {
                db = VaultDatabase.open(getApplication())
                alice = client("demo-local")
                bob = client("demo-peer")
                // Retry saved envelopes after an interrupted process before preparing new ones.
                deliver(alice, bob); deliver(bob, alice)
                if (alice.history().isEmpty()) {
                    bob.establish(alice.localDevice().accountId, bundle(alice))
                    bob.prepare(alice.localDevice().accountId, welcome)
                    deliver(bob, alice)
                }
                DemoState(loading = false, messages = alice.history(), safetyNumber = alice.safetyNumber(bob.localDevice().accountId))
            }
            mutable.value = value
        } catch (_: Exception) {
            mutable.value = DemoState(loading = false,
                error = R.string.storage_error)
        }
    }
    private fun client(profile: String): SignalClient = SignalClient(RoomVault(db, profile)).apply {
        initialize()
        if (deviceOrNull() == null) bind(Device(UUID.randomUUID().toString(), UUID.randomUUID().toString(), 1))
    }
    private fun bundle(client: SignalClient): ClaimedBundle {
        val device = client.localDevice(); val b = client.publicBundle()
        return ClaimedBundle(device.accountId, device.deviceId, device.generation, 1, client.registrationId(), client.identityPublic(),
            b.signedPreKeyId, b.signedPreKey, b.signature, b.preKeys.first(), b.pqPreKeys.first())
    }
    private fun deliver(from: SignalClient, to: SignalClient) {
        val sender = from.localDevice()
        for (outgoing in from.pending()) {
            to.receive(Incoming(1, outgoing.clientMessageId, sender.accountId, sender.deviceId, sender.generation,
                outgoing.messageType, outgoing.ciphertext, "local-demo", "local-demo"))
            from.accepted(outgoing.clientMessageId)
        }
    }
    fun send(text: String, automaticReply: String) {
        if (mutable.value.loading || mutable.value.busy || mutable.value.error != null || text.isBlank()) return
        mutable.value = mutable.value.copy(busy = true)
        viewModelScope.launch {
            try {
                val messages = withContext(Dispatchers.IO) {
                    deliver(alice, bob); deliver(bob, alice)
                    alice.prepare(bob.localDevice().accountId, text)
                    deliver(alice, bob)
                    bob.prepare(alice.localDevice().accountId, automaticReply)
                    deliver(bob, alice)
                    alice.history()
                }
                mutable.value = mutable.value.copy(busy = false, messages = messages)
            } catch (_: Exception) {
                mutable.value = mutable.value.copy(busy = false,
                    error = R.string.send_error)
            }
        }
    }
    override fun onCleared() { if (::db.isInitialized) db.close(); super.onCleared() }
}
