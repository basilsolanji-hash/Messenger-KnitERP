package ru.kniterp.messenger.zz

import android.util.Log
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.After
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import ru.kniterp.messenger.MainActivity
import ru.kniterp.messenger.tap

/**
 * Reproduction for the crash on activity destroy (Compose "Must run runDetachLifecycle() …" / SlotTable index):
 * open a screen whose lazy list holds a focused text field with the keyboard up, then destroy the activity.
 * Last in the run because a crash ends the process.
 */
@RunWith(AndroidJUnit4::class)
class DestroyWithFocusStressTest {
    @get:Rule val compose = createEmptyComposeRule()
    private var scenario: ActivityScenario<MainActivity>? = null

    @After fun stop() { scenario?.close() }

    private fun cycles(screen: String, field: String) = repeat(20) { round ->
        Log.i("RecreateDiag", "destroy-with-focus $screen round $round")
        val activity = ActivityScenario.launch(MainActivity::class.java).also { scenario = it }
        compose.waitUntil(60_000) {
            runCatching {
                if (compose.onAllNodesWithText(screen).fetchSemanticsNodes().isEmpty())
                    compose.onNodeWithText("Настройки").tap()
                compose.onNode(hasScrollToNodeAction()).performScrollToNode(hasText(screen))
                compose.onNodeWithText(screen).tap()
            }
            compose.onAllNodes(hasSetTextAction() and hasText(field)).fetchSemanticsNodes().isNotEmpty()
        }
        compose.onNode(hasSetTextAction() and hasText(field)).performTextReplacement("Synthetic $round")
        activity.close() // Destroy with focus and keyboard.
        scenario = null
    }

    @Test fun a_networkScreen() = cycles("Закрытый тест сервера", "Тестовое имя")
    @Test fun b_loginScreen() = cycles("Вход по номеру телефона", "Номер телефона")
}
