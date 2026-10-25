package ru.kniterp.messenger

import android.content.pm.ActivityInfo
import android.graphics.Bitmap
import android.view.inputmethod.InputMethodManager
import androidx.compose.ui.graphics.asAndroidBitmap
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import java.io.ByteArrayOutputStream
import org.junit.After
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import ru.kniterp.messenger.core.Wire
import ru.kniterp.messenger.network.ServerEndpoint

/**
 * Activity recreation and rotation while a text field on the login screen holds focus and the keyboard, then a
 * simulated process death. No server is contacted. The process must survive every destroy.
 */
@RunWith(AndroidJUnit4::class)
class RecreateTest {
    @get:Rule val compose = createEmptyComposeRule()
    private var scenario: ActivityScenario<MainActivity>? = null
    private lateinit var activity: MainActivity

    @After fun stop() { scenario?.close() }

    private fun launch() {
        scenario = ActivityScenario.launch(MainActivity::class.java).also { it.onActivity { a -> activity = a } }
        waitFor(hasText(activity.getString(R.string.settings)))
    }
    private fun refresh() = scenario!!.onActivity { activity = it }
    private fun s(id: Int) = activity.getString(id)
    private fun waitFor(matcher: SemanticsMatcher) =
        compose.waitUntil(60_000) { compose.onAllNodes(matcher).fetchSemanticsNodes().isNotEmpty() }
    private fun shown(matcher: SemanticsMatcher) =
        compose.waitUntil(60_000) { runCatching { compose.onNode(hasScrollToNodeAction()).performScrollToNode(matcher) }.isSuccess }
    private val origin = "https://recreate.example/"
    /** Retried: a keyboard left over from the previous activity can swallow the first click. */
    private fun openLogin() = compose.waitUntil(90_000) {
        runCatching {
            if (compose.onAllNodes(hasText(s(R.string.auth_title))).fetchSemanticsNodes().isEmpty())
                compose.onNodeWithText(s(R.string.settings)).tap()
            compose.onNode(hasScrollToNodeAction()).performScrollToNode(hasText(s(R.string.auth_title)))
            compose.onNodeWithText(s(R.string.auth_title)).tap()
        }
        compose.waitForIdle()
        compose.onAllNodes(hasSetTextAction()).fetchSemanticsNodes().isNotEmpty()
    }
    /** On Android 8 a just-opened keyboard covers the next button and swallows the click. */
    private fun hideKeyboard() = scenario!!.onActivity {
        it.getSystemService(InputMethodManager::class.java).hideSoftInputFromWindow(it.window.decorView.windowToken, 0)
    }
    private fun phoneField() = hasSetTextAction() and hasText(s(R.string.auth_phone))

    @Test fun loginScreenSurvivesRecreationRotationAndProcessDeath() {
        launch(); openLogin()
        val pins = listOf(Wire.base64(ByteArray(32) { 7 }), Wire.base64(ByteArray(32) { 8 })).map { "sha256/$it" }
        val server = hasSetTextAction() and hasText(s(R.string.auth_server))
        shown(server)
        compose.onNode(server).performTextInput(Wire.json.encodeToString(ServerEndpoint(origin, pins)))
        hideKeyboard()
        shown(hasText(s(R.string.auth_server_save)) and isEnabled())
        compose.onNodeWithText(s(R.string.auth_server_save)).tap()
        val target = hasText(s(R.string.net_target) + " " + origin)
        shown(target)
        val owner = AuthOwners.get(activity.application)
        val device = owner.deviceId

        repeat(12) { round ->
            // Scroll and type as one retried step: the list may recompose right after recreation.
            compose.waitUntil(60_000) {
                runCatching {
                    compose.onNode(hasScrollToNodeAction()).performScrollToNode(phoneField())
                    compose.onNode(phoneField()).performTextReplacement("90012345$round") // Focus and keyboard active.
                }.isSuccess
            }
            if (round % 4 == 0) ByteArrayOutputStream().use {
                check(compose.onRoot().captureToImage().asAndroidBitmap().compress(Bitmap.CompressFormat.PNG, 100, it))
            }
            when (round % 3) {
                0 -> scenario!!.recreate()
                1 -> scenario!!.onActivity { it.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE }
                else -> scenario!!.onActivity { it.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_PORTRAIT }
            }
            compose.waitForIdle(); refresh()
            shown(target)
            assertSame("Recreation must reuse the application owner", owner, AuthOwners.get(activity.application))
            assertEquals(device, owner.deviceId)
        }

        // Process death: the in-memory owner is gone, the unactivated profile comes back from SQLCipher/Keystore.
        scenario!!.close(); scenario = null
        AuthOwners.simulateProcessDeath()
        launch(); openLogin()
        shown(target)
        assertNotSame(owner, AuthOwners.get(activity.application))
        assertEquals(device, AuthOwners.get(activity.application).deviceId)
        compose.waitUntil(60_000) {
            runCatching {
                compose.onNode(hasScrollToNodeAction()).performScrollToNode(phoneField())
                compose.onNode(phoneField()).assert(!hasText("90012345", substring = true)) // The typed number was not saved.
            }.isSuccess
        }
    }
}
