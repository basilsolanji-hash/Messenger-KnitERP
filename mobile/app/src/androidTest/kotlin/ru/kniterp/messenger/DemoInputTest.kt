package ru.kniterp.messenger

import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.graphics.asAndroidBitmap
import android.graphics.Bitmap
import java.io.File
import androidx.test.ext.junit.runners.AndroidJUnit4
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
        for ((label, title) in listOf("Беларуская" to "Налады", "Қазақша" to "Баптаулар",
                "O‘zbekcha" to "Sozlamalar", "Русский" to "Настройки")) {
            compose.onNodeWithText(label).performClick()
            compose.waitUntil(30_000) {
                compose.onAllNodesWithText(title).fetchSemanticsNodes().isNotEmpty()
            }
        }
        capture("compose-languages.png")
    }

    private fun capture(name: String) {
        File(compose.activity.cacheDir, name).outputStream().use {
            check(compose.onRoot().captureToImage().asAndroidBitmap().compress(Bitmap.CompressFormat.PNG, 100, it))
        }
    }
}
