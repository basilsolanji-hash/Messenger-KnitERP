package ru.kniterp.messenger

import android.graphics.Bitmap
import androidx.compose.ui.graphics.asAndroidBitmap
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.ext.junit.runners.AndroidJUnit4
import java.io.File
import org.junit.Assert.*
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import ru.kniterp.messenger.core.Wire
import ru.kniterp.messenger.network.EnrollmentRequest

/** Public enrollment can be produced on the phone without a server or private key export. */
@RunWith(AndroidJUnit4::class)
class NetworkSetupTest {
    @get:Rule val compose = createAndroidComposeRule<MainActivity>()

    @Test fun exportsOnlyPhonePublicEnrollmentAndRejectsCleartextEndpoint() {
        compose.waitUntil(60_000) { compose.onAllNodesWithText("Тестовый собеседник").fetchSemanticsNodes().isNotEmpty() }
        compose.onNodeWithText("Настройки").performClick()
        compose.onNode(hasScrollToNodeAction()).performScrollToNode(hasText("Закрытый тест сервера"))
        compose.onNodeWithText("Закрытый тест сервера").performClick()
        compose.waitUntil(60_000) {
            compose.onAllNodesWithText("Создать публичный запрос").fetchSemanticsNodes().isNotEmpty() &&
                compose.onAllNodes(hasSetTextAction()).fetchSemanticsNodes().isNotEmpty()
        }
        compose.onNode(hasSetTextAction() and hasText("Тестовое имя")).performTextInput("Synthetic Android 26")
        compose.onNodeWithText("Создать публичный запрос").performClick()
        // The request appears below the button in a lazy list; with the keyboard open it may not be composed yet.
        compose.waitUntil(30_000) {
            runCatching { compose.onNode(hasScrollToNodeAction()).performScrollToNode(hasText("\"identityKey\"", substring = true)) }.isSuccess
        }
        val json = compose.onNode(hasText("\"identityKey\"", substring = true)).fetchSemanticsNode()
            .config[androidx.compose.ui.semantics.SemanticsProperties.Text].joinToString("") { it.text }
        val request = Wire.json.decodeFromString<EnrollmentRequest>(json)
        assertEquals("Synthetic Android 26", request.displayName)
        assertEquals(33, Wire.bytes(request.identityKey).size)
        assertFalse(json.contains("accessToken")); assertFalse(json.contains("private", ignoreCase = true))
        File(compose.activity.cacheDir, "compose-network.png").outputStream().use {
            check(compose.onRoot().captureToImage().asAndroidBitmap().compress(Bitmap.CompressFormat.PNG, 100, it))
        }
        compose.onNode(hasScrollToNodeAction()).performScrollToNode(hasText("JSON сессии от оператора (секрет)"))
        val invalid = """{"server":{"origin":"http://example.com","pins":["invalid","invalid"]},"accessToken":"synthetic-token-never-use-in-production"}"""
        compose.onNode(hasSetTextAction() and hasText("JSON сессии от оператора (секрет)")).performTextInput(invalid)
        compose.onNode(hasScrollToNodeAction()).performScrollToNode(hasText("Подключить сессию"))
        compose.onNodeWithText("Подключить сессию").assertIsNotEnabled()
    }
}
