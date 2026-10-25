package ru.kniterp.messenger.zz

import android.view.WindowManager
import androidx.compose.ui.graphics.asAndroidBitmap
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.After
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import ru.kniterp.messenger.MainActivity
import ru.kniterp.messenger.R
import ru.kniterp.messenger.tap

/**
 * Experiment, last in the run because a crash ends the process: Compose's captureToImage() immediately followed by
 * recreate() on the login screen, with and without FLAG_SECURE. Both app crashes on destroy seen in CI followed
 * exactly this sequence; recreate() alone (RecreateTest) did not crash.
 */
@RunWith(AndroidJUnit4::class)
class CaptureRecreateExperimentTest {
    @get:Rule val compose = createEmptyComposeRule()
    private var scenario: ActivityScenario<MainActivity>? = null
    private lateinit var activity: MainActivity

    @After fun stop() { scenario?.close() }

    private fun cycle(secure: Boolean) {
        scenario = ActivityScenario.launch(MainActivity::class.java).also { it.onActivity { a -> activity = a } }
        compose.waitUntil(60_000) {
            runCatching {
                if (compose.onAllNodes(hasText(activity.getString(R.string.auth_title))).fetchSemanticsNodes().isEmpty())
                    compose.onNodeWithText(activity.getString(R.string.settings)).tap()
                compose.onNode(hasScrollToNodeAction()).performScrollToNode(hasText(activity.getString(R.string.auth_title)))
                compose.onNodeWithText(activity.getString(R.string.auth_title)).tap()
            }
            compose.onAllNodes(hasSetTextAction()).fetchSemanticsNodes().isNotEmpty()
        }
        repeat(10) {
            if (!secure) scenario!!.onActivity { it.window.clearFlags(WindowManager.LayoutParams.FLAG_SECURE) }
            check(compose.onRoot().captureToImage().asAndroidBitmap().width > 0)
            scenario!!.recreate()
            compose.waitUntil(30_000) { compose.onAllNodes(hasSetTextAction()).fetchSemanticsNodes().isNotEmpty() }
        }
    }

    @Test fun a_withoutSecureFlag() = cycle(secure = false)
    @Test fun b_withSecureFlag() = cycle(secure = true)
}
