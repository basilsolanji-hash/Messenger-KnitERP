package ru.kniterp.messenger.live

import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import ru.kniterp.messenger.R
import ru.kniterp.messenger.network.LoginStep

/**
 * Two synthetic phones on one emulator, both through the production controller, Keystore, SQLCipher and libsignal,
 * over pinned HTTPS to the real API and PostgreSQL. Faults are injected around the transport, never inside crypto.
 */
@RunWith(AndroidJUnit4::class)
class LiveChatTest {
    @Before fun live() = Live.assumeConfigured()

    @Test fun textBothWaysOfflineQueueLostResponsesAndRestartWithoutDuplicates() {
        val a = Phone(); val b = Phone()
        val aId = a.register(); val bId = b.register()
        a.open(bId); b.open(aId)

        assertNull(a.send("Привет 1 — тест 0123456789").error)
        assertNull(b.sync().error)
        assertEquals(listOf("Привет 1 — тест 0123456789"), b.incoming(aId))
        assertNull(b.send("Ответ 1 қазақша o‘zbekcha").error)
        assertNull(a.sync().error)
        assertEquals(listOf("Ответ 1 қазақша o‘zbekcha"), a.incoming(bId))
        assertEquals(60, a.state.safetyNumber.length)
        assertEquals(a.state.safetyNumber, b.state.safetyNumber)

        // Offline: the text is encrypted and committed to the outbox, then sent once the network returns.
        a.faults.offline = true
        assertEquals(R.string.auth_err_network, a.send("Офлайн 2").error)
        assertTrue(a.outgoing("Офлайн 2").id in a.state.pendingIds)
        b.sync()
        assertFalse("Офлайн 2" in b.incoming(aId))
        a.faults.offline = false
        assertNull(a.sync().error)
        assertTrue(a.state.pendingIds.isEmpty())
        b.sync()
        assertEquals(1, b.incoming(aId).count { it == "Офлайн 2" })

        // The server accepted the send but the answer was lost: the retry repeats the same ciphertext, no duplicate.
        a.faults.loseAfter += "send"
        assertEquals(R.string.auth_err_network, a.send("Потерян ответ 3").error)
        assertTrue(a.outgoing("Потерян ответ 3").id in a.state.pendingIds)
        assertNull(a.sync().error)
        assertTrue(a.state.pendingIds.isEmpty())
        b.sync(); b.sync()
        assertEquals(1, b.incoming(aId).count { it == "Потерян ответ 3" })

        // ACK processed, its answer lost; then an ACK that never left: redelivery adds no second history entry.
        a.send("ACK потерян 4")
        b.faults.loseAfter += "ack"
        assertEquals(R.string.auth_err_network, b.sync().error)
        assertNull(b.sync().error)
        assertEquals(1, b.incoming(aId).count { it == "ACK потерян 4" })
        a.send("ACK не отправлен 5")
        b.faults.failBefore += "ack"
        assertEquals(R.string.auth_err_network, b.sync().error)
        assertNull(b.sync().error)
        assertEquals(1, b.incoming(aId).count { it == "ACK не отправлен 5" })

        // Process death: a new controller on the same registry restores the activation, history and keys, and
        // obtains a new access token through a signed refresh challenge.
        val device = a.controller.deviceId
        val history = a.state.messages
        a.close()
        val restarted = Phone(a.registry)
        val resumed = restarted.start()
        assertEquals(LoginStep.ACTIVATED, resumed.login.step)
        assertTrue(resumed.ready)
        assertEquals(aId, resumed.accountId)
        assertEquals(device, restarted.controller.deviceId)
        assertEquals(history, resumed.messages)
        assertTrue(resumed.pendingIds.isEmpty())
        restarted.open(bId)
        assertNull(restarted.send("После перезапуска 6").error)
        b.sync()
        assertEquals(1, b.incoming(aId).count { it == "После перезапуска 6" })
        b.send("Ответ после перезапуска 7")
        assertNull(restarted.sync().error)
        assertEquals(1, restarted.incoming(bId).count { it == "Ответ после перезапуска 7" })
        restarted.close(); b.close()
    }

    @Test fun interruptedPublicationRetriesWithTheSameDeviceAndKeys() {
        val phone = Phone()
        phone.start(); phone.configure()
        phone.request(Live.number())
        val device = phone.controller.deviceId
        phone.faults.loseAfter += "publish" // Server stored the bundle; the answer was lost.
        val pending = phone.submitCode(phone.code())
        assertEquals(LoginStep.ACTIVATED, pending.login.step)
        assertFalse(pending.ready)
        assertEquals(R.string.auth_err_network, pending.error)
        val ready = phone.act { retryActivation() }
        assertTrue(ready.ready)
        assertNull(ready.error)
        assertEquals(device, phone.controller.deviceId)
        phone.close()
        // A restart keeps the same activation instead of preparing a new device.
        Phone(phone.registry).apply { assertTrue(start().ready); assertEquals(device, controller.deviceId); close() }
    }
}
