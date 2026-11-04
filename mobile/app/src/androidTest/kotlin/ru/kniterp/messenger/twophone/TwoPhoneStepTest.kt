package ru.kniterp.messenger.twophone

import android.os.Bundle
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import java.util.Base64
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import ru.kniterp.messenger.live.Live
import ru.kniterp.messenger.live.Phone
import ru.kniterp.messenger.network.LoginStep

/**
 * One step of a conversation between two emulators (Android 8 and current Android), each with its own installed app,
 * Keystore, SQLCipher and libsignal, over pinned HTTPS to the same real API. The workflow `two-phones.yml` runs the
 * steps in order and passes account ids between devices; every step is a new app process, so each later step also
 * proves that the activation, keys and history survive a process restart.
 *
 * Excluded from the regular instrumentation run (`notPackage`); without `twoPhoneStep` it fails rather than skips.
 */
@RunWith(AndroidJUnit4::class)
class TwoPhoneStepTest {
    private val args = InstrumentationRegistry.getArguments()
    private fun arg(name: String) = requireNotNull(args.getString(name)) { "Missing instrumentation argument $name" }
    private fun text() = Base64.getUrlDecoder().decode(arg("textB64")).decodeToString()
    private fun report(key: String, value: String) =
        InstrumentationRegistry.getInstrumentation().sendStatus(0, Bundle().apply { putString(key, value) })

    @Test fun step() {
        Live.assumeConfigured()
        when (val step = arg("twoPhoneStep")) {
            "register" -> Phone(REGISTRY).use { report("twoPhoneAccount", it.register()) }
            "send" -> resumed().use { phone ->
                phone.open(arg("peer"))
                assertNull(phone.send(text()).error)
                assertTrue("Sent text must leave the outbox", phone.state.pendingIds.isEmpty())
            }
            "expect" -> resumed().use { phone ->
                val peer = arg("peer"); val expected = text()
                phone.open(peer)
                var found = 0
                for (attempt in 1..10) {
                    assertNull(phone.sync().error)
                    found = phone.incoming(peer).count { it == expected }
                    if (found > 0) break
                    Thread.sleep(2_000)
                }
                assertEquals("Expected exactly one copy of the text from $peer", 1, found)
                assertEquals(60, phone.state.safetyNumber.length)
                report("twoPhoneSafety", phone.state.safetyNumber)
            }
            else -> fail("Unknown step $step")
        }
    }

    /** The activation stored by the earlier step, restored in this new process with a signed refresh. */
    private fun resumed(): Phone {
        val phone = Phone(REGISTRY)
        val state = phone.start()
        assertEquals(LoginStep.ACTIVATED, state.login.step)
        assertTrue("Restored activation must be ready", state.ready)
        return phone
    }

    private companion object { const val REGISTRY = "two-phone" }
}
