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
    /** Server-side block of [request] for this account: no delivery in either direction afterwards. */
    fun block(request: BlockRequest): Unit = throw UnsupportedOperationException("Blocking is not available")
}
@Serializable data class BlockRequest(val accountId: String)
/** [heldIds]: outgoing messages held after a refusal (see [Delivery]); they stay pending with the same ciphertext. */
/** [dropped]: packets that failed authentication [MAX_REJECTS] times and were acknowledged so they stop blocking the inbox. */
data class DeliveryReport(val accepted: Int, val received: Int, val rejectedSequences: List<Long>,
                          val heldIds: List<String> = emptyList(), val dropped: Int = 0)

/** Network calls never hold a database transaction. A cursor is NOT persisted:
 * unacknowledged messages must be retried after a crash. Each instance is serialized.
 */
internal const val HELD = "delivery-held"
private const val CHANGE_CLAIMS = "delivery-change-claims"
private const val ACCEPTED = "peers-accepted"
private const val REJECTS = "delivery-rejects"
/** Attempts before a packet that can never decrypt is acknowledged and dropped. */
const val MAX_REJECTS = 3
private const val BLOCKED = "peers-blocked"

class Delivery(private val vault: Vault, private val api: MessageApi,
               /** True for a send refusal that a retry cannot fix (the recipient moved to another device). Such a
                * message is held durably and not sent again; the inbox and other outgoing messages still flow.
                * [acceptPeerChange] re-encrypts it for the confirmed new device and releases it. */
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
    /**
     * The peer's CURRENT device from the server (a fresh claim) as a preview; nothing is trusted yet. The claim id is kept
     * until [acceptPeerChange], so a retry after a lost answer gets the same bundle and spends no further prekeys.
     */
    @Synchronized fun checkPeerChange(peer: String): PeerChange {
        Wire.id(peer)
        return signal.previewChange(peer, changeBundle(peer)).also { if (!it.changed) forgetPeerChange(peer) }
    }
    /** The user closed the check without accepting: the next check claims the peer's then-current device. */
    @Synchronized fun forgetPeerChange(peer: String) {
        Wire.id(peer)
        vault.transaction { it.delete(CHANGE_CLAIMS, peer) }
    }
    /**
     * After the user compared [confirmedSafetyNumber] with the peer: pins the new device, re-encrypts this peer's pending
     * and held messages for it and releases them for the next [synchronize]. Returns how many were re-encrypted.
     */
    @Synchronized fun acceptPeerChange(peer: String, confirmedSafetyNumber: String): Int {
        Wire.id(peer)
        val bundle = changeBundle(peer)
        val ids = signal.acceptChange(peer, bundle, confirmedSafetyNumber) // Also releases their held marks atomically.
        vault.transaction { r -> r.delete(CHANGE_CLAIMS, peer) }
        return ids.size
    }
    private fun changeBundle(peer: String): ClaimedBundle {
        val claimId = vault.transaction { r ->
            r.read<String>(CHANGE_CLAIMS, peer) ?: UUID.randomUUID().toString().also { r.write(CHANGE_CLAIMS, peer, it) }
        }
        return api.claim(ClaimRequest(peer, claimId))
    }

    /**
     * Message requests (PRODUCT_SPEC section 18): a peer's texts are shown only after this user opened the chat, wrote
     * to the peer or accepted the request. Others stay hidden (stored encrypted, acknowledged, without preview).
     */
    fun acceptedPeers(): Set<String> = vault.transaction { it.keys(ACCEPTED).toSet() } +
        signal.history().filter { it.outgoing }.map { it.peerAccountId }
    fun blockedPeers(): Set<String> = vault.transaction { it.keys(BLOCKED).toSet() }
    @Synchronized fun acceptPeer(peer: String) {
        Wire.id(peer)
        vault.transaction { r -> if (r.get(BLOCKED, peer) == null) r.put(ACCEPTED, peer, byteArrayOf(1)) }
    }
    /** Server block first; the local mark only after the server confirmed it. */
    @Synchronized fun blockPeer(peer: String) {
        Wire.id(peer)
        require(peer != signal.localDevice().accountId)
        api.block(BlockRequest(peer))
        vault.transaction { r -> r.put(BLOCKED, peer, byteArrayOf(1)); r.delete(ACCEPTED, peer) }
    }

    /** Counts a failed authentication of [item]; true once it has failed [MAX_REJECTS] times (then it is acknowledged). */
    private fun permanent(item: Incoming, rejected: MutableList<Long>): Boolean {
        val count = vault.transaction { r ->
            val key = item.sequence.toString()
            ((r.get(REJECTS, key)?.decodeToString()?.toIntOrNull() ?: 0) + 1).also { r.put(REJECTS, key, it.toString().toByteArray()) }
        }
        if (count < MAX_REJECTS) rejected += item.sequence
        return count >= MAX_REJECTS
    }

    /** Outgoing messages held after a refusal; they are never resent automatically. */
    fun heldIds(): Set<String> = vault.transaction { it.keys(HELD).toSet() }

    @Synchronized fun synchronize(maxPages: Int = 10, maxOutgoing: Int = 100): DeliveryReport {
        require(maxPages in 1..100 && maxOutgoing in 1..100)
        var accepted = 0
        val parked = heldIds()
        val heldIds = signal.pendingIds().filter { it in parked }.toMutableList()
        for (message in signal.pending().filterNot { it.clientMessageId in parked }.take(maxOutgoing)) {
            val response = try { api.send(message) } catch (failure: Exception) {
                if (!held(failure)) throw failure
                vault.transaction { it.put(HELD, message.clientMessageId, byteArrayOf(1)) }
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
        var dropped = 0
        repeat(maxPages) {
            val page = api.inbox(cursor)
            require(page.items.size <= 100 && page.items.all { it.sequence > 0 && sequences.add(it.sequence) }) { "Invalid inbox page" }
            if (page.items.isEmpty()) return DeliveryReport(accepted, received, rejected, heldIds, dropped)
            val committed = mutableListOf<Long>()
            for (item in page.items) {
                try { signal.receive(item) }
                // A new peer device or identity can become valid after the user accepts it: kept, never acknowledged.
                catch (_: IdentityChanged) { rejected += item.sequence; continue }
                catch (_: DeviceChanged) { rejected += item.sequence; continue }
                // Packets that can never authenticate: kept for MAX_REJECTS synchronizations, then acknowledged and
                // dropped, so forged or corrupt packets cannot pile up at the head of the inbox and block new messages.
                catch (_: EnvelopeMismatch) { if (permanent(item, rejected)) { committed += item.sequence; dropped++ }; continue }
                catch (_: MessageConflict) { if (permanent(item, rejected)) { committed += item.sequence; dropped++ }; continue }
                catch (_: org.signal.libsignal.protocol.InvalidMessageException) { if (permanent(item, rejected)) { committed += item.sequence; dropped++ }; continue }
                catch (_: org.signal.libsignal.protocol.InvalidKeyIdException) { if (permanent(item, rejected)) { committed += item.sequence; dropped++ }; continue }
                // Storage/unknown failures abort WITHOUT ACK, never disguised as a bad packet.
                committed += item.sequence
                received++
            }
            if (committed.isNotEmpty()) {
                api.ack(AckRequest(committed))
                vault.transaction { r -> committed.forEach { r.delete(REJECTS, it.toString()) } }
            }
            val next = page.nextCursor ?: return DeliveryReport(accepted, received, rejected, heldIds, dropped)
            require(next.length <= 8192 && visited.add(next)) { "Invalid inbox cursor" }
            cursor = next
        }
        return DeliveryReport(accepted, received, rejected, heldIds, dropped)
    }
}
