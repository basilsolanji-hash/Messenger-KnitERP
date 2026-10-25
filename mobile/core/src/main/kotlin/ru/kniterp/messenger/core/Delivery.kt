package ru.kniterp.messenger.core

import java.util.UUID
import kotlinx.serialization.Serializable

@Serializable data class RemoteMe(val accountId: String, val deviceId: String, val generation: Int,
    val displayName: String, val registrationId: Int, val identityKey: String, val expiresAt: String) {
    fun device() = Device(accountId, deviceId, generation)
}
@Serializable data class ClaimRequest(val accountId: String, val claimId: String)
@Serializable data class AckRequest(val sequences: List<Long>)

/** Synchronous boundary. Android invokes it on IO; adapters bound and authenticate responses. */
interface MessageApi {
    fun me(): RemoteMe
    fun publish(bundle: PublishBundle)
    fun claim(request: ClaimRequest): ClaimedBundle
    fun send(message: Outgoing): Accepted
    fun inbox(cursor: String?): InboxPage
    fun ack(request: AckRequest)
}
/** [heldIds]: outgoing messages the server refused for now (see [Delivery]); they stay pending with the same ciphertext. */
data class DeliveryReport(val accepted: Int, val received: Int, val rejectedSequences: List<Long>,
                          val heldIds: List<String> = emptyList())

/** Network calls never hold a database transaction. A cursor is NOT persisted:
 * unacknowledged messages must be retried after a crash. Each instance is serialized.
 */
class Delivery(private val vault: Vault, private val api: MessageApi,
               /** True for a send refusal that a retry now cannot fix (the recipient moved to another device).
                * Such a message is held instead of aborting, so the inbox and other outgoing messages still flow. */
               private val held: (Exception) -> Boolean = { false }) {
    val signal = SignalClient(vault)
    @Synchronized fun establish(peer: String) {
        Wire.id(peer)
        require(peer != signal.localDevice().accountId)
        if (signal.hasPeer(peer)) return
        val claimId = vault.transaction { r ->
            r.read<String>("delivery-claims", peer) ?: UUID.randomUUID().toString().also { r.write("delivery-claims", peer, it) }
        }
        signal.establish(peer, api.claim(ClaimRequest(peer, claimId)))
    }
    @Synchronized fun queue(peer: String, text: String, id: String = UUID.randomUUID().toString()): Outgoing {
        establish(peer)
        return signal.prepare(peer, text, id)
    }
    @Synchronized fun synchronize(maxPages: Int = 10, maxOutgoing: Int = 100): DeliveryReport {
        require(maxPages in 1..100 && maxOutgoing in 1..100)
        var accepted = 0
        val heldIds = mutableListOf<String>()
        for (message in signal.pending(maxOutgoing)) {
            val response = try { api.send(message) } catch (failure: Exception) {
                if (!held(failure)) throw failure
                heldIds += message.clientMessageId
                continue
            }
            require(response.clientMessageId == message.clientMessageId && response.sequence > 0) { "Invalid acceptance" }
            signal.accepted(message.clientMessageId)
            accepted++
        }
        var cursor: String? = null
        val visited = mutableSetOf<String>()
        val sequences = mutableSetOf<Long>()
        val rejected = mutableListOf<Long>()
        var received = 0
        repeat(maxPages) {
            val page = api.inbox(cursor)
            require(page.items.size <= 100 && page.items.all { it.sequence > 0 && sequences.add(it.sequence) }) { "Invalid inbox page" }
            if (page.items.isEmpty()) return DeliveryReport(accepted, received, rejected, heldIds)
            val committed = mutableListOf<Long>()
            for (item in page.items) {
                try { signal.receive(item) }
                catch (_: IdentityChanged) { rejected += item.sequence; continue }
                catch (_: DeviceChanged) { rejected += item.sequence; continue }
                catch (_: EnvelopeMismatch) { rejected += item.sequence; continue }
                catch (_: MessageConflict) { rejected += item.sequence; continue }
                catch (_: org.signal.libsignal.protocol.InvalidMessageException) { rejected += item.sequence; continue }
                catch (_: org.signal.libsignal.protocol.InvalidKeyIdException) { rejected += item.sequence; continue }
                // Storage/unknown failures abort WITHOUT ACK, never disguised as a bad packet.
                committed += item.sequence
                received++
            }
            if (committed.isNotEmpty()) api.ack(AckRequest(committed))
            val next = page.nextCursor ?: return DeliveryReport(accepted, received, rejected, heldIds)
            require(next.length <= 8192 && visited.add(next)) { "Invalid inbox cursor" }
            cursor = next
        }
        return DeliveryReport(accepted, received, rejected, heldIds)
    }
}
