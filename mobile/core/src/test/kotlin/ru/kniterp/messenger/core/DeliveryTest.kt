package ru.kniterp.messenger.core

import java.io.IOException
import kotlin.test.*

class DeliveryTest {
    private class Server(val a: Participant, val b: Participant) {
        val packets = mutableListOf<Incoming>()
        val sent = mutableListOf<Outgoing>()
        val claims = mutableListOf<ClaimRequest>()
        val acks = mutableListOf<List<Long>>()
        private val accepted = mutableMapOf<String, Accepted>()
        var loseSend = false
        var loseAck = false
        var loseClaim = false
        var wrongAcceptance = false
        fun api(actor: Participant) = object : MessageApi {
            override fun me(): RemoteMe = error("Not used")
            override fun publish(bundle: PublishBundle) = Unit
            override fun claim(request: ClaimRequest): ClaimedBundle {
                claims += request
                if (loseClaim) { loseClaim = false; throw IOException("Lost claim response") }
                return (if (request.accountId == a.device.accountId) a else b).bundle()
            }
            override fun send(message: Outgoing): Accepted {
                sent += message
                val response = accepted.getOrPut(message.clientMessageId) {
                    val sequence = (accepted.size + 1).toLong()
                    packets += message.incoming(actor.device, sequence)
                    Accepted(message.clientMessageId, sequence, "2026-10-22T00:00:00Z", "2026-11-21T00:00:00Z")
                }
                if (loseSend) { loseSend = false; throw IOException("Lost acceptance") }
                return if (wrongAcceptance) response.copy(clientMessageId = java.util.UUID.randomUUID().toString()) else response
            }
            override fun inbox(cursor: String?): InboxPage {
                val items = packets.filter { it.senderAccountId != actor.device.accountId && it.sequence > (cursor?.toLong() ?: 0) }
                return InboxPage(items, items.lastOrNull()?.sequence?.toString())
            }
            override fun ack(request: AckRequest) {
                acks += request.sequences
                if (loseAck) { loseAck = false; throw IOException("Lost ACK") }
                packets.removeAll { it.sequence in request.sequences }
            }
        }
    }
    @Test fun claimAndSendSurviveLostResponsesAndRestartWithoutNewCiphertext() {
        val a = participant(); val b = participant(); val server = Server(a, b)
        val d = Delivery(a.vault, server.api(a))
        server.loseClaim = true
        assertFailsWith<IOException> { d.queue(b.device.accountId, "not queued yet") }
        assertTrue(a.client.history().isEmpty())
        Delivery(a.vault, server.api(a)).queue(b.device.accountId, "012345 қазақша")
        assertEquals(server.claims[0].claimId, server.claims[1].claimId)
        val original = a.client.pending().single()
        server.loseSend = true
        assertFailsWith<IOException> { d.synchronize() }
        assertEquals(original, a.client.pending().single())
        assertEquals(1, Delivery(a.vault, server.api(a)).synchronize().accepted)
        assertEquals(server.sent[0], server.sent[1])
        assertEquals(1, server.packets.size)
        assertEquals(1, Delivery(b.vault, server.api(b)).synchronize().received)
        assertEquals("012345 қазақша", b.client.history().single().text)
    }
    @Test fun lostAckRedeliversCommittedMessageWithoutSecondHistoryEntry() {
        val a = participant(); val b = participant(); val server = Server(a, b)
        val sender = Delivery(a.vault, server.api(a)); sender.queue(b.device.accountId, "stored"); sender.synchronize()
        server.loseAck = true
        assertFailsWith<IOException> { Delivery(b.vault, server.api(b)).synchronize() }
        assertEquals(1, b.client.history().size)
        Delivery(b.vault, server.api(b)).synchronize()
        assertEquals(1, b.client.history().size)
        assertTrue(server.packets.isEmpty())
    }
    @Test fun databaseFailureNeverAcknowledgesAndNextAttemptCanDecrypt() {
        val a = participant(); val b = participant(); val server = Server(a, b)
        val sender = Delivery(a.vault, server.api(a)); sender.queue(b.device.accountId, "commit first"); sender.synchronize()
        var fail = true
        val crashing = object : Vault {
            override fun <T> transaction(block: (Records) -> T): T = b.vault.transaction { records ->
                var writes = false
                val tracked = object : Records by records {
                    override fun put(bucket: String, key: String, value: ByteArray) { writes = true; records.put(bucket, key, value) }
                    override fun delete(bucket: String, key: String) { writes = true; records.delete(bucket, key) }
                }
                val result = block(tracked)
                if (writes && fail) { fail = false; error("Storage failure before COMMIT") }
                result
            }
        }
        assertFailsWith<IllegalStateException> { Delivery(crashing, server.api(b)).synchronize() }
        assertTrue(server.acks.isEmpty()); assertTrue(b.client.history().isEmpty())
        Delivery(b.vault, server.api(b)).synchronize()
        assertEquals("commit first", b.client.history().single().text)
    }
    @Test fun corruptEnvelopeIsNotAcknowledgedButValidNeighbourIsDelivered() {
        val a = participant(); val b = participant(); val server = Server(a, b)
        val sender = Delivery(a.vault, server.api(a))
        val corrupt = sender.queue(b.device.accountId, "corrupt")
        sender.queue(b.device.accountId, "valid"); sender.synchronize()
        val index = server.packets.indexOfFirst { it.clientMessageId == corrupt.clientMessageId }
        val first = server.packets[index]
        server.packets[index] = first.withTamperedBody()
        val report = Delivery(b.vault, server.api(b)).synchronize()
        assertEquals(listOf(first.sequence), report.rejectedSequences)
        assertEquals("valid", b.client.history().single().text)
        assertFalse(server.acks.flatten().contains(first.sequence))
    }
    @Test fun mismatchedAcceptanceCannotDiscardOutbox() {
        val a = participant(); val b = participant(); val server = Server(a, b)
        val d = Delivery(a.vault, server.api(a)); d.queue(b.device.accountId, "keep")
        server.wrongAcceptance = true
        assertFailsWith<IllegalArgumentException> { d.synchronize() }
        assertEquals(1, a.client.pending().size)
    }
}
