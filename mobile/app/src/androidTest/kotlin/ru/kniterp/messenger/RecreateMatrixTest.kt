package ru.kniterp.messenger

import android.content.pm.ActivityInfo
import android.util.Log
import android.view.WindowManager
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import java.util.concurrent.TimeUnit
import org.junit.After
import org.junit.Rule
import org.junit.Test
import org.junit.rules.RuleChain
import org.junit.rules.Timeout
import org.junit.runner.RunWith

/**
 * Diagnostic matrix for the hang/crash after recreation on the login screen: which ingredient is needed —
 * recreate() vs rotation, a focused field with keyboard, FLAG_SECURE. Each variant fails within 150 s.
 */
@RunWith(AndroidJUnit4::class)
class RecreateMatrixTest {
    private val compose = createEmptyComposeRule()
    @get:Rule val rules: RuleChain = RuleChain.outerRule(Timeout(150, TimeUnit.SECONDS)).around(compose)
    private var scenario: ActivityScenario<MainActivity>? = null
    private lateinit var activity: MainActivity

    @After fun stop() { scenario?.close() }

    private fun s(id: Int) = activity.getString(id)
    private fun log(message: String) = Log.i("RecreateDiag", message)

    private fun openLogin() {
        scenario = ActivityScenario.launch(MainActivity::class.java).also { it.onActivity { a -> activity = a } }
        compose.waitUntil(60_000) {
            runCatching {
                if (compose.onAllNodes(hasText(s(R.string.auth_title))).fetchSemanticsNodes().isEmpty())
                    compose.onNodeWithText(s(R.string.settings)).tap()
                compose.onNode(hasScrollToNodeAction()).performScrollToNode(hasText(s(R.string.auth_title)))
                compose.onNodeWithText(s(R.string.auth_title)).tap()
            }
            compose.onAllNodes(hasSetTextAction()).fetchSemanticsNodes().isNotEmpty()
        }
    }

    private fun cycle(name: String, rotate: Boolean, focus: Boolean, secure: Boolean) {
        openLogin()
        if (!secure) scenario!!.onActivity { it.window.clearFlags(WindowManager.LayoutParams.FLAG_SECURE) }
        repeat(8) { round ->
            log("$name round $round start")
            if (focus) compose.onAllNodes(hasSetTextAction())[0].performTextReplacement("9001234$round")
            if (rotate) scenario!!.onActivity {
                it.requestedOrientation = if (round % 2 == 0) ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE
                    else ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
            } else scenario!!.recreate()
            if (!secure) scenario!!.onActivity { it.window.clearFlags(WindowManager.LayoutParams.FLAG_SECURE) }
            log("$name round $round waiting for idle")
            compose.waitForIdle()
            compose.waitUntil(30_000) { compose.onAllNodes(hasSetTextAction()).fetchSemanticsNodes().isNotEmpty() }
            log("$name round $round done")
        }
    }

    @Test fun a_recreateWithFocus() = cycle("recreate+focus", rotate = false, focus = true, secure = true)
    @Test fun b_rotateWithoutFocus() = cycle("rotate", rotate = true, focus = false, secure = true)
    @Test fun c_rotateWithFocus() = cycle("rotate+focus", rotate = true, focus = true, secure = true)
    @Test fun d_rotateWithFocusNoSecureFlag() = cycle("rotate+focus-secure", rotate = true, focus = true, secure = false)
}
