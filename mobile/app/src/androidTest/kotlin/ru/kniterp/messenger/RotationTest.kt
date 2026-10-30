package ru.kniterp.messenger

import android.app.Activity
import android.content.pm.ActivityInfo
import android.content.res.Configuration
import android.os.Handler
import android.os.Looper
import android.view.Choreographer
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.runner.lifecycle.ActivityLifecycleMonitorRegistry
import androidx.test.runner.lifecycle.Stage
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference
import org.junit.After
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Rotation on the login screen with a focused field and the keyboard open. After a requestedOrientation change the
 * Compose test library never reports idle on these emulators (the app keeps drawing), so every check after the first
 * rotation is made directly on the main thread, without Compose test waits.
 */
@RunWith(AndroidJUnit4::class)
class RotationTest {
    @get:Rule val compose = createEmptyComposeRule()
    private var scenario: ActivityScenario<MainActivity>? = null

    @After fun stop() { scenario?.close() }

    private class Probe(val frame: Boolean, val resumed: List<Activity>, val focused: Boolean, val orientation: Int)

    /** One frame delivered, the resumed activities, window focus and orientation — read on the main thread. */
    private fun probe(): Probe {
        val frame = CountDownLatch(1)
        val read = CountDownLatch(1)
        val result = AtomicReference<Triple<List<Activity>, Boolean, Int>>()
        Handler(Looper.getMainLooper()).post {
            Choreographer.getInstance().postFrameCallback { frame.countDown() }
            val resumed = ActivityLifecycleMonitorRegistry.getInstance().getActivitiesInStage(Stage.RESUMED).toList()
            result.set(Triple(resumed, resumed.singleOrNull()?.hasWindowFocus() == true,
                resumed.singleOrNull()?.resources?.configuration?.orientation ?: -1))
            read.countDown()
        }
        assertTrue("Main thread did not answer", read.await(10, TimeUnit.SECONDS))
        val (resumed, focused, orientation) = result.get()
        return Probe(frame.await(10, TimeUnit.SECONDS), resumed, focused, orientation)
    }

    @Test fun rotationWithKeyboardKeepsTheAppAliveAndTheSameOwner() {
        scenario = ActivityScenario.launch(MainActivity::class.java)
        lateinit var activity: MainActivity
        scenario!!.onActivity { activity = it }
        compose.waitUntil(60_000) {
            runCatching {
                if (compose.onAllNodes(hasText(activity.getString(R.string.auth_title))).fetchSemanticsNodes().isEmpty())
                    compose.onNodeWithText(activity.getString(R.string.settings)).tap()
                compose.onNode(hasScrollToNodeAction()).performScrollToNode(hasText(activity.getString(R.string.auth_title)))
                compose.onNodeWithText(activity.getString(R.string.auth_title)).tap()
            }
            compose.onAllNodes(hasSetTextAction()).fetchSemanticsNodes().isNotEmpty()
        }
        compose.onAllNodes(hasSetTextAction())[0].performTextReplacement("9001234") // Focus and keyboard.
        val owner = AuthOwners.get(activity.application)
        val device = owner.deviceId
        var previous: Activity = activity

        repeat(8) { round ->
            val landscape = round % 2 == 0
            Handler(Looper.getMainLooper()).post {
                previous.requestedOrientation = if (landscape) ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE
                    else ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
            }
            val expected = if (landscape) Configuration.ORIENTATION_LANDSCAPE else Configuration.ORIENTATION_PORTRAIT
            var probe = probe()
            val deadline = System.currentTimeMillis() + 20_000
            while ((probe.resumed.singleOrNull() === previous || probe.orientation != expected || !probe.focused) &&
                System.currentTimeMillis() < deadline) { Thread.sleep(250); probe = probe() }
            assertTrue("Round $round: no frame after rotation", probe.frame)
            assertEquals("Round $round: exactly one resumed activity", 1, probe.resumed.size)
            assertNotSame("Round $round: the activity was recreated", previous, probe.resumed.single())
            assertEquals("Round $round: orientation", expected, probe.orientation)
            assertTrue("Round $round: window focus", probe.focused)
            assertSame("Round $round: same application owner", owner, AuthOwners.get(activity.application))
            assertEquals("Round $round: same activation", device, owner.deviceId)
            previous = probe.resumed.single()
        }
    }
}
