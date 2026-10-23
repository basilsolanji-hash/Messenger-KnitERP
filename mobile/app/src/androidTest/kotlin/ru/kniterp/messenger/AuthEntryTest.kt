package ru.kniterp.messenger

import android.graphics.Bitmap
import androidx.compose.ui.graphics.asAndroidBitmap
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.test.ext.junit.runners.AndroidJUnit4
import java.io.File
import org.junit.Assert.assertSame
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import ru.kniterp.messenger.core.Wire
import ru.kniterp.messenger.network.ServerEndpoint

/** Native registration entry, with public configuration only. This test does not contact a server or send SMS. */
@RunWith(AndroidJUnit4::class)
class AuthEntryTest {
    @get:Rule val compose = createAndroidComposeRule<MainActivity>()

    @Test fun loginUsesOneApplicationOwnerAndRejectsBadPhoneBeforeHttp() {
        val context = compose.activity
        assertSame(AuthModel(context.application).state, AuthModel(context.application).state)
        compose.waitUntil(60_000) { compose.onAllNodesWithText(context.getString(R.string.settings)).fetchSemanticsNodes().isNotEmpty() }
        compose.onNodeWithText(context.getString(R.string.settings)).performClick()
        compose.onNode(hasScrollToNodeAction()).performScrollToNode(hasText(context.getString(R.string.auth_title)))
        compose.onNodeWithText(context.getString(R.string.auth_title)).performClick()
        compose.waitUntil(30_000) { compose.onAllNodes(hasSetTextAction()).fetchSemanticsNodes().isNotEmpty() }
        val pins = listOf(Wire.base64(ByteArray(32)), Wire.base64(ByteArray(32) { 1 })).map { "sha256/$it" }
        val config = Wire.json.encodeToString(ServerEndpoint("https://example.com", pins))
        compose.onNode(hasSetTextAction() and hasText(context.getString(R.string.auth_server))).performTextInput(config)
        compose.onNodeWithText(context.getString(R.string.auth_server_save)).performClick()
        compose.waitUntil(60_000) { compose.onAllNodesWithText(context.getString(R.string.auth_get_code)).fetchSemanticsNodes().isNotEmpty() }
        compose.onNode(hasScrollToNodeAction()).performScrollToNode(hasText(context.getString(R.string.auth_phone)))
        compose.onNode(hasSetTextAction() and hasText(context.getString(R.string.auth_phone))).performTextInput("12345")
        compose.onNode(hasScrollToNodeAction()).performScrollToNode(hasText(context.getString(R.string.auth_get_code)))
        compose.onNodeWithText(context.getString(R.string.auth_get_code)).performClick()
        compose.waitUntil(20_000) { compose.onAllNodesWithText(context.getString(R.string.auth_err_phone)).fetchSemanticsNodes().isNotEmpty() }
        compose.onNode(hasScrollToNodeAction()).performScrollToNode(hasText(context.getString(R.string.auth_country_uz)))
        File(context.cacheDir, "os-auth-entry.png").outputStream().use {
            check(compose.onRoot().captureToImage().asAndroidBitmap().compress(Bitmap.CompressFormat.PNG, 100, it))
        }
        compose.activityRule.scenario.recreate()
        compose.waitUntil(30_000) { compose.onAllNodesWithText(context.getString(R.string.auth_get_code)).fetchSemanticsNodes().isNotEmpty() }
        // Recreated screens attach to the existing owner. No separate session/refresh controller is constructed.
        assertSame(AuthModel(context.application).state, AuthModel(compose.activity.application).state)
    }
}
