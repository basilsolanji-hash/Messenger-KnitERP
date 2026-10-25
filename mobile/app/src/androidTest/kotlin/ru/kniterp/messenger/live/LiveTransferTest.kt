package ru.kniterp.messenger.live

import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import ru.kniterp.messenger.R
import ru.kniterp.messenger.network.LoginError
import ru.kniterp.messenger.network.LoginStep

/** Moving a number to a new phone: PIN step, the old phone is revoked, and the peer refuses the new device loudly. */
@RunWith(AndroidJUnit4::class)
class LiveTransferTest {
    @Before fun live() = Live.assumeConfigured()

    @Test fun transferWithPinRevokesOldPhoneAndPeerDoesNotSilentlyAcceptNewDevice() {
        val number = Live.number()
        val old = Phone(); val peer = Phone()
        val account = old.register(number); val peerId = peer.register()
        old.open(peerId); peer.open(account)
        old.send("До переноса 1"); peer.sync()
        assertEquals(listOf("До переноса 1"), peer.incoming(account))
        assertEquals(R.string.auth_pin_saved, old.act { setPin("482913", null) }.notice)
        val oldDevice = old.controller.deviceId

        Thread.sleep(61_000) // The server's 60 s resend gate per number is not bypassed for tests.
        val moved = Phone()
        moved.start(); moved.configure()
        assertEquals(LoginStep.CODE, moved.request(number).login.step)
        assertEquals(LoginStep.PIN, moved.submitCode(moved.code()).login.step)
        val wrong = moved.submitPin("000001").login
        assertEquals(LoginError.PIN_INVALID, wrong.error)
        assertNotNull(wrong.attemptsLeft)
        assertNull(old.sync().error) // A wrong PIN changed nothing for the old phone.
        val done = moved.submitPin("482913")
        assertEquals(LoginStep.ACTIVATED, done.login.step)
        assertEquals(false, done.login.newAccount)
        assertEquals(account, done.accountId)
        assertTrue(done.ready)

        // The old phone learns it was replaced on its next request and gets a fresh, unbound profile.
        val revoked = old.sync()
        assertEquals("SESSION_REVOKED", revoked.ended)
        assertEquals(LoginStep.PHONE, revoked.login.step)
        assertEquals("", revoked.accountId)
        assertNotEquals(oldDevice, old.controller.deviceId)

        // The peer still trusts the old device: the new one is rejected and shown, never accepted silently.
        moved.open(peerId)
        assertNull(moved.send("С нового телефона 2").error)
        assertEquals(R.string.net_rejected, peer.sync().error)
        assertFalse("С нового телефона 2" in peer.incoming(account))
        val held = peer.send("Старому устройству 3")
        assertEquals(R.string.net_identity_changed, held.error)
        assertTrue(peer.outgoing("Старому устройству 3").id in held.pendingIds)
        moved.sync()
        assertFalse("Старому устройству 3" in moved.incoming(peerId))
        assertEquals(R.string.net_identity_changed, peer.sync().error) // Still refused on every attempt.
        old.close(); peer.close(); moved.close()
    }
}
