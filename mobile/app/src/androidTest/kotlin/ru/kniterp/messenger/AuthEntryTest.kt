package ru.kniterp.messenger

import android.graphics.Bitmap
import android.view.inputmethod.InputMethodManager
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

    /** On Android 8 a just-opened keyboard covers the next button and takes the click. */
    private fun hideKeyboard() = compose.runOnUiThread {
        compose.activity.getSystemService(InputMethodManager::class.java)
            .hideSoftInputFromWindow(compose.activity.window.decorView.windowToken, 0)
    }

    @Test fun loginUsesOneApplicationOwnerAndRejectsBadPhoneBeforeHttp() {
        val context = compose.activity
        assertSame(AuthModel(context.application).state, AuthModel(context.application).state)
        compose.waitUntil(60_000) { compose.onAllNodesWithText(context.getString(R.string.settings)).fetchSemanticsNodes().isNotEmpty() }
        compose.onNodeWithText(context.getString(R.string.settings)).tap()
        compose.onNode(hasScrollToNodeAction()).performScrollToNode(hasText(context.getString(R.string.auth_title)))
        compose.onNodeWithText(context.getString(R.string.auth_title)).tap()
        compose.waitUntil(30_000) { compose.onAllNodes(hasSetTextAction()).fetchSemanticsNodes().isNotEmpty() }
        val pins = listOf(Wire.base64(ByteArray(32)), Wire.base64(ByteArray(32) { 1 })).map { "sha256/$it" }
        val config = Wire.json.encodeToString(ServerEndpoint("https://example.com", pins))
        compose.onNode(hasSetTextAction() and hasText(context.getString(R.string.auth_server))).performTextInput(config)
        hideKeyboard()
        compose.onNodeWithText(context.getString(R.string.auth_server_save)).tap()
        // The list is lazy: off-screen items are not composed, so wait for the saved origin at the top, then scroll.
        val target = context.getString(R.string.net_target) + " https://example.com/"
        compose.waitUntil(60_000) { compose.onAllNodesWithText(target).fetchSemanticsNodes().isNotEmpty() }
        compose.onNode(hasScrollToNodeAction()).performScrollToNode(hasText(context.getString(R.string.auth_phone)))
        compose.onNode(hasSetTextAction() and hasText(context.getString(R.string.auth_phone))).performTextInput("12345")
        compose.onNode(hasScrollToNodeAction()).performScrollToNode(hasText(context.getString(R.string.auth_get_code)))
        hideKeyboard()
        compose.onNodeWithText(context.getString(R.string.auth_get_code)).tap()
        compose.waitUntil(20_000) { compose.onAllNodesWithText(context.getString(R.string.auth_err_phone)).fetchSemanticsNodes().isNotEmpty() }
        compose.onNode(hasScrollToNodeAction()).performScrollToNode(hasText(context.getString(R.string.auth_country_uz)))
        compose.activityRule.scenario.recreate()
        compose.waitUntil(30_000) { compose.onAllNodesWithText(context.getString(R.string.settings)).fetchSemanticsNodes().isNotEmpty() }
        // Captured after, not right before, recreate(): see CaptureRecreateExperimentTest.
        File(context.cacheDir, "os-auth-entry.png").outputStream().use {
            check(compose.onRoot().captureToImage().asAndroidBitmap().compress(Bitmap.CompressFormat.PNG, 100, it))
        }
        // Recreated screens attach to the existing owner. No separate session/refresh controller is constructed.
        assertSame(AuthModel(context.application).state, AuthModel(compose.activity.application).state)
    }
}
