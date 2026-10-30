package ru.kniterp.messenger

import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.test.SemanticsNodeInteraction
import androidx.compose.ui.test.performSemanticsAction

/**
 * Invokes the node's click action instead of injecting a touch. On Android 8 the keyboard (or its closing animation)
 * can take a touch aimed at a button just below a text field; this keeps the tests about the app's logic.
 */
fun SemanticsNodeInteraction.tap(): SemanticsNodeInteraction = performSemanticsAction(SemanticsActions.OnClick)
