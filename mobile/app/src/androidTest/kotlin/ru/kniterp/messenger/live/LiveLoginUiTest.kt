package ru.kniterp.messenger.live

import android.view.WindowManager
import androidx.compose.ui.semantics.SemanticsProperties
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import ru.kniterp.messenger.AuthOwners
import ru.kniterp.messenger.MainActivity
import ru.kniterp.messenger.R

/** The real screens against the live API: SMS code from the operator, wrong code, PIN, rotation, process death, logout. */
@RunWith(AndroidJUnit4::class)
class LiveLoginUiTest {
    @get:Rule val compose = createEmptyComposeRule()
    private var scenario: ActivityScenario<MainActivity>? = null
    private lateinit var activity: MainActivity

    @Before fun live() {
        Live.assumeConfigured()
        AuthOwners.simulateProcessDeath()
        AuthOwners.testTrust = Live.trust
    }
    @After fun stop() { scenario?.close(); AuthOwners.simulateProcessDeath(); AuthOwners.testTrust = null }

    private fun launch() {
        scenario?.close()
        scenario = ActivityScenario.launch(MainActivity::class.java).also { it.onActivity { a -> activity = a } }
        waitText(R.string.settings)
    }
    private fun s(id: Int, vararg args: Any) = activity.getString(id, *args)
    private fun waitText(id: Int, timeout: Long = 60_000) = waitFor(hasText(activity.getString(id), substring = true), timeout)
    private fun waitFor(matcher: SemanticsMatcher, timeout: Long = 60_000) =
        compose.waitUntil(timeout) { compose.onAllNodes(matcher).fetchSemanticsNodes().isNotEmpty() }
    private fun list() = compose.onNode(hasScrollToNodeAction())
    private fun scrollTo(matcher: SemanticsMatcher) = list().performScrollToNode(matcher)
    private fun click(id: Int) { scrollTo(hasText(s(id))); compose.onNodeWithText(s(id)).performClick() }
    private fun field(label: Int) = hasSetTextAction() and hasText(s(label))
    private fun type(label: Int, text: String) { scrollTo(field(label)); compose.onNode(field(label)).performTextInput(text) }
    private fun openLogin() {
        compose.onNodeWithText(s(R.string.settings)).performClick()
        click(R.string.auth_title)
        waitFor(hasSetTextAction())
    }
    /** The login list is lazy: an item off-screen is not composed, so scroll until it exists. */
    private fun shown(matcher: SemanticsMatcher, timeout: Long = 90_000) =
        compose.waitUntil(timeout) { runCatching { scrollTo(matcher) }.isSuccess }
    private fun shown(id: Int) = shown(hasText(s(id), substring = true))
    private fun ready() = shown(R.string.auth_offer_pin)
    private fun origin() = shown(hasText(s(R.string.net_target) + " " + Live.endpoint.canonicalOrigin()))

    @Test fun loginWithOperatorCodePinRotationProcessDeathAndLogout() {
        launch(); openLogin()
        assertNotEquals("Screens with codes and PINs are not capturable", 0,
            activity.window.attributes.flags and WindowManager.LayoutParams.FLAG_SECURE)
        type(R.string.auth_server, Live.json)
        click(R.string.auth_server_save)
        origin()
        type(R.string.auth_phone, Live.number())
        click(R.string.auth_get_code)

        shown(hasTestTag("challengeId"))
        val tagText = compose.onNodeWithTag("challengeId").fetchSemanticsNode().config[SemanticsProperties.Text].joinToString("") { it.text }
        val challenge = tagText.lines().last()
        val code = Live.code(challenge)
        // The 60 s resend gate is shown as a countdown on a disabled button.
        val countdown = hasText(s(R.string.auth_resend_in, 0).substringBefore("0"), substring = true)
        shown(countdown)
        compose.onNode(countdown).assertIsNotEnabled()

        // A typed but unsent code is not kept in saved state: it is gone after recreation.
        type(R.string.auth_code, code.take(3))
        scenario!!.recreate(); scenario!!.onActivity { activity = it }
        shown(hasTestTag("challengeId"))
        scrollTo(field(R.string.auth_code))
        compose.onNode(field(R.string.auth_code)).assert(!hasText(code.take(3), substring = true))

        type(R.string.auth_code, Live.wrong(code)); click(R.string.auth_confirm)
        shown(hasText(s(R.string.auth_attempts_left, 4), substring = true))
        type(R.string.auth_code, code); click(R.string.auth_confirm)
        ready()
        val account = AuthOwners.get(Live.application).state.value.accountId
        val device = AuthOwners.get(Live.application).deviceId
        shown(hasText(account, substring = true))

        type(R.string.auth_new_pin, "482913"); click(R.string.auth_set_pin)
        shown(R.string.auth_pin_saved)

        // Rotation and a second screen keep the same owner and session.
        scenario!!.onActivity { it.requestedOrientation = android.content.pm.ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE }
        scenario!!.onActivity { activity = it }
        ready()
        scenario!!.onActivity { it.requestedOrientation = android.content.pm.ActivityInfo.SCREEN_ORIENTATION_PORTRAIT }
        assertEquals(device, AuthOwners.get(Live.application).deviceId)

        // Process death: a new owner restores from SQLCipher and refreshes with the Keystore-signed challenge.
        scenario!!.close(); scenario = null
        AuthOwners.simulateProcessDeath()
        launch(); openLogin(); ready()
        assertEquals(account, AuthOwners.get(Live.application).state.value.accountId)
        assertEquals(device, AuthOwners.get(Live.application).deviceId)

        type(R.string.auth_current_pin, "482913"); click(R.string.auth_disable_pin)
        shown(R.string.auth_pin_disabled)

        click(R.string.auth_logout)
        shown(R.string.auth_ended_logout)
        origin()
        assertNotEquals(device, AuthOwners.get(Live.application).deviceId) // Next activation gets a new profile.
        assertEquals("", AuthOwners.get(Live.application).state.value.accountId)

        // Neither the code, the PIN nor a token reached this process's log or the clipboard.
        val log = Runtime.getRuntime().exec(arrayOf("logcat", "-d", "-v", "raw")).inputStream.bufferedReader().readText()
        for (secret in listOf(code, "482913", "rt1.", "Bearer ")) assertFalse("Secret in logcat", log.contains(secret))
        var clip = ""
        scenario!!.onActivity {
            clip = it.getSystemService(android.content.ClipboardManager::class.java).primaryClip?.getItemAt(0)?.text?.toString().orEmpty()
        }
        assertFalse(clip.contains(code) || clip.contains("482913"))
    }
}
