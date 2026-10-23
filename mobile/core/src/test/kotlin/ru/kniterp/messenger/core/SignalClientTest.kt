package ru.kniterp.messenger.core

import java.util.UUID
import kotlin.test.*
import org.signal.libsignal.protocol.InvalidKeyException

class SignalClientTest {
    @Test fun `PQ handshake and bidirectional encrypted exchange survive client reconstruction`() {
        val alice = participant(); val bob = participant()
        alice.client.establish(bob.device.accountId, bob.bundle())
        val text = "Привет! Заказ №42 готов 🧶"
        val outgoing = alice.client.prepare(bob.device.accountId, text)
        assertEquals(3, outgoing.messageType)
        assertFalse(outgoing.ciphertext.contains(text))
        assertFalse(Wire.bytes(outgoing.ciphertext).toString(Charsets.ISO_8859_1).contains("Заказ"))
        val restartedBob = SignalClient(TestVault(bob.vault.snapshot()))
        assertEquals(text, restartedBob.receive(outgoing.incoming(alice.device)).text)
        val reply = restartedBob.prepare(alice.device.accountId, "Принято")
        assertEquals(2, reply.messageType)
        val restartedAlice = SignalClient(TestVault(alice.vault.snapshot()))
        assertEquals("Принято", restartedAlice.receive(reply.incoming(bob.device)).text)
        assertEquals(2, restartedAlice.history().size)
    }

    @Test fun `network retry reuses exact ciphertext and cannot change message content`() {
        val a = participant(); val b = participant()
        a.client.establish(b.device.accountId, b.bundle())
        val id = UUID.randomUUID().toString()
        val first = a.client.prepare(b.device.accountId, "Повтор", id)
        val c = SignalClient(TestVault(a.vault.snapshot()))
        assertEquals(first, c.prepare(b.device.accountId, "Повтор", id))
        assertEquals(listOf(first), c.pending())
        assertFailsWith<MessageConflict> { c.prepare(b.device.accountId, "Другой текст", id) }
        c.accepted(id)
        assertTrue(c.pending().isEmpty())
        assertEquals(first, c.prepare(b.device.accountId, "Повтор", id))
        assertEquals(1, c.history().size)
    }

    @Test fun `crash before sending commit rolls back ratchet outbox and history`() {
        val a = participant(); val b = participant()
        a.client.establish(b.device.accountId, b.bundle())
        val before = a.vault.snapshot()
        a.vault.failNextCommit = true
        assertFailsWith<IllegalStateException> { a.client.prepare(b.device.accountId, "Не сохранять") }
        assertSnapshotEquals(before, a.vault.snapshot())
        val retry = SignalClient(a.vault).prepare(b.device.accountId, "Теперь сохранить")
        assertEquals("Теперь сохранить", b.client.receive(retry.incoming(a.device)).text)
    }

    @Test fun `crash before receiving commit preserves prekeys and permits redelivery`() {
        val a = participant(); val b = participant()
        a.client.establish(b.device.accountId, b.bundle())
        val envelope = a.client.prepare(b.device.accountId, "Проверка COMMIT").incoming(a.device)
        val before = b.vault.snapshot()
        b.vault.failNextCommit = true
        assertFailsWith<IllegalStateException> { b.client.receive(envelope) }
        assertSnapshotEquals(before, b.vault.snapshot())
        assertEquals("Проверка COMMIT", SignalClient(b.vault).receive(envelope).text)
        assertEquals(7, b.client.publicBundle().preKeys.size)
        assertEquals(7, b.client.publicBundle().pqPreKeys.size)
    }

    @Test fun `lost ACK and repeated delivery never duplicate plaintext history`() {
        val a = participant(); val b = participant()
        a.client.establish(b.device.accountId, b.bundle())
        val envelope = a.client.prepare(b.device.accountId, "Один раз").incoming(a.device)
        val first = b.client.receive(envelope)
        val restart = SignalClient(TestVault(b.vault.snapshot()))
        assertEquals(first, restart.receive(envelope))
        assertEquals(first, restart.receive(envelope.copy(sequence = 42)))
        assertEquals(1, restart.history().size)
        assertFailsWith<MessageConflict> { restart.receive(envelope.copy(ciphertext = envelope.ciphertext.dropLast(4) + "AAAA")) }
    }

    @Test fun `tampered ciphertext fails without consuming one-time keys or changing state`() {
        val a = participant(); val b = participant()
        a.client.establish(b.device.accountId, b.bundle())
        val envelope = a.client.prepare(b.device.accountId, "Неповрежденное").incoming(a.device)
        val corrupted = envelope.withTamperedBody()
        val before = b.vault.snapshot()
        assertFails { b.client.receive(corrupted) }
        assertSnapshotEquals(before, b.vault.snapshot())
        assertEquals("Неповрежденное", b.client.receive(envelope).text)
    }

    @Test fun `signed ECC and PQ prekey tampering is rejected before identity is pinned`() {
        val a = participant(); val b = participant(); val bundle = b.bundle()
        val before = a.vault.snapshot()
        fun changed(signature: String) = Wire.bytes(signature).also { it[0] = (it[0].toInt() xor 1).toByte() }.let(Wire::base64)
        assertFailsWith<InvalidKeyException> { a.client.establish(b.device.accountId, bundle.copy(signature = changed(bundle.signature))) }
        assertSnapshotEquals(before, a.vault.snapshot())
        assertFailsWith<InvalidKeyException> { a.client.establish(b.device.accountId,
            bundle.copy(pqPreKey = bundle.pqPreKey.copy(signature = changed(bundle.pqPreKey.signature)))) }
        assertSnapshotEquals(before, a.vault.snapshot())
    }

    @Test fun `changed identity blocks outgoing and incoming without silent trust reset`() {
        val a = participant(); val b = participant()
        a.client.establish(b.device.accountId, b.bundle())
        b.client.receive(a.client.prepare(b.device.accountId, "Первый контакт").incoming(a.device))
        val impostor = participant(b.device.accountId)
        val replacement = impostor.bundle().copy(deviceId = b.device.deviceId)
        val before = a.vault.snapshot()
        assertFailsWith<IdentityChanged> { a.client.establish(b.device.accountId, replacement) }
        assertSnapshotEquals(before, a.vault.snapshot())
        impostor.client.establish(a.device.accountId, a.bundle())
        val forged = impostor.client.prepare(a.device.accountId, "Подмена").incoming(b.device)
        assertFails { a.client.receive(forged) }
        assertSnapshotEquals(before, a.vault.snapshot())
    }

    @Test fun `changed device and substituted account bundle are blocked`() {
        val a = participant(); val b = participant(); val c = participant()
        a.client.establish(b.device.accountId, b.bundle())
        assertFailsWith<DeviceChanged> { a.client.establish(b.device.accountId, b.bundle().copy(generation = 2)) }
        assertFailsWith<IllegalArgumentException> { a.client.establish(b.device.accountId, c.bundle()) }
    }

    @Test fun `authenticated routing and message id reject server envelope relabeling`() {
        val a = participant(); val b = participant()
        a.client.establish(b.device.accountId, b.bundle())
        val envelope = a.client.prepare(b.device.accountId, "Мой идентификатор").incoming(a.device)
        val before = b.vault.snapshot()
        assertFailsWith<EnvelopeMismatch> { b.client.receive(envelope.copy(clientMessageId = UUID.randomUUID().toString())) }
        assertSnapshotEquals(before, b.vault.snapshot())
        assertFailsWith<EnvelopeMismatch> { b.client.receive(envelope.copy(senderDeviceId = UUID.randomUUID().toString())) }
        assertSnapshotEquals(before, b.vault.snapshot())
        assertEquals("Мой идентификатор", b.client.receive(envelope).text)
    }

    @Test fun `out of order messages decrypt without skipped-key loss`() {
        val a = participant(); val b = participant()
        a.client.establish(b.device.accountId, b.bundle())
        b.client.receive(a.client.prepare(b.device.accountId, "Начало").incoming(a.device))
        a.client.receive(b.client.prepare(a.device.accountId, "Ответ").incoming(b.device))
        val packets = (1..4).map { a.client.prepare(b.device.accountId, "Сообщение $it").incoming(a.device, it.toLong() + 1) }
        for (i in listOf(3, 0, 2, 1)) assertEquals("Сообщение ${i + 1}", b.client.receive(packets[i]).text)
    }

    @Test fun `safety numbers are symmetric and QR detects a third identity`() {
        val a = participant(); val b = participant(); val c = participant()
        a.client.establish(b.device.accountId, b.bundle())
        b.client.receive(a.client.prepare(b.device.accountId, "Проверить код").incoming(a.device))
        val number = a.client.safetyNumber(b.device.accountId)
        assertEquals(60, number.length)
        assertTrue(number.all(Char::isDigit))
        assertEquals(number, b.client.safetyNumber(a.device.accountId))
        assertTrue(a.client.matchesSafetyQr(b.device.accountId, b.client.safetyQr(a.device.accountId)))
        c.client.establish(a.device.accountId, a.bundle())
        assertFalse(a.client.matchesSafetyQr(b.device.accountId, c.client.safetyQr(a.device.accountId)))
    }

    private fun assertSnapshotEquals(a: Map<Pair<String, String>, ByteArray>, b: Map<Pair<String, String>, ByteArray>) {
        assertEquals(a.keys, b.keys)
        for (key in a.keys) assertContentEquals(a.getValue(key), b.getValue(key), "State changed at $key")
    }
}
