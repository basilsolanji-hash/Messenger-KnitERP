package ru.kniterp.messenger

import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.graphics.asAndroidBitmap
import android.graphics.Bitmap
import java.io.File
import android.os.Handler
import android.os.Looper
import androidx.appcompat.app.AppCompatDelegate
import androidx.core.os.LocaleListCompat
import androidx.test.runner.lifecycle.ActivityLifecycleMonitorRegistry
import androidx.test.runner.lifecycle.Stage
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.fail
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/** Exercises the real text field and send button, including trailing digits and Unicode. */
@RunWith(AndroidJUnit4::class)
class DemoInputTest {
    @get:Rule val compose = createAndroidComposeRule<MainActivity>()

    @Test fun textFieldAndSendPreserveTrailingDigitsAndUnicode() {
        compose.waitUntil(60_000) {
            compose.onAllNodesWithText("Тестовый собеседник").fetchSemanticsNodes().isNotEmpty()
        }
        capture("compose-main.png")
        compose.onNodeWithText("Тестовый собеседник").performClick()
        val body = "knitERP_test_01 — Тест 0123456789"
        val field = compose.onNode(hasSetTextAction())
        field.performTextInput(body)
        field.assertTextEquals(body)
        compose.onNodeWithContentDescription("Отправить").performClick()
        compose.waitUntil(30_000) {
            compose.onAllNodesWithText(body).fetchSemanticsNodes().isNotEmpty()
        }
        compose.onNodeWithText(body).assertIsDisplayed()
        compose.onNodeWithText("Получено и расшифровано. История сохранена на устройстве 🔒").assertIsDisplayed()
        capture("compose-chat.png")
        compose.onNodeWithContentDescription("Назад").performClick()
        compose.onNodeWithText("Настройки").performClick()
        // A language switch recreates the activity with a new configuration. After that the Compose test library
        // does not reliably report idle on these emulators (see RotationTest), so the first switch goes through the
        // real button and the result of every switch is read on the main thread.
        compose.onNodeWithText("Беларуская").tap()
        awaitTitle("Налады")
        for ((tag, title) in listOf("kk" to "Баптаулар", "uz-Latn" to "Sozlamalar", "ru" to "Настройки")) {
            Handler(Looper.getMainLooper()).post { AppCompatDelegate.setApplicationLocales(LocaleListCompat.forLanguageTags(tag)) }
            awaitTitle(title)
        }
    }

    /** The resumed activity's resources give [title] for the settings label: the new language is applied. */
    private fun awaitTitle(title: String) {
        val deadline = System.currentTimeMillis() + 30_000
        var seen = ""
        while (System.currentTimeMillis() < deadline) {
            val read = CountDownLatch(1)
            Handler(Looper.getMainLooper()).post {
                seen = ActivityLifecycleMonitorRegistry.getInstance().getActivitiesInStage(Stage.RESUMED)
                    .singleOrNull()?.getString(R.string.settings).orEmpty()
                read.countDown()
            }
            read.await(10, TimeUnit.SECONDS)
            if (seen == title) return
            Thread.sleep(250)
        }
        fail("Language not applied: expected $title, saw '$seen'")
    }

    private fun capture(name: String) {
        File(compose.activity.cacheDir, name).outputStream().use {
            check(compose.onRoot().captureToImage().asAndroidBitmap().compress(Bitmap.CompressFormat.PNG, 100, it))
        }
    }
}
