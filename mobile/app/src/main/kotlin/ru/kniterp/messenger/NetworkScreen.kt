package ru.kniterp.messenger

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import ru.kniterp.messenger.core.SignalClient
import ru.kniterp.messenger.core.Wire
import ru.kniterp.messenger.network.TestConnection

/** Explicit operator-assisted test mode. Does not masquerade as SMS registration. */
@Composable fun NetworkScreen(model: NetworkModel = viewModel()) {
    val state by model.state.collectAsState()
    val context = LocalContext.current
    var name by remember { mutableStateOf("") }
    var config by remember { mutableStateOf("") } // Never put bearer credentials in savedInstanceState.
    var peer by remember { mutableStateOf("") }
    var draft by remember { mutableStateOf("") }
    var showSafety by remember { mutableStateOf(false) }
    val target = remember(config) {
        runCatching { Wire.json.decodeFromString<TestConnection>(config).server.canonicalOrigin() }.getOrNull()
    }
    LaunchedEffect(Unit) { model.start() }
    LaunchedEffect(state.queuedId) { if (state.queuedId.isNotEmpty()) draft = "" }
    fun copyPublic(value: String) {
        (context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager)
            .setPrimaryClip(ClipData.newPlainText("knitERP", value))
    }
    LazyColumn(Modifier.fillMaxSize().imePadding(), contentPadding = PaddingValues(20.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp)) {
        item { Text(stringResource(R.string.net_title), style = MaterialTheme.typography.headlineSmall) }
        item { Text(stringResource(R.string.net_notice), style = MaterialTheme.typography.bodySmall) }
        if (state.loading) item { CircularProgressIndicator() }
        state.error?.let { error -> item { Text(stringResource(error), color = MaterialTheme.colorScheme.error) } }
        item { Text(stringResource(R.string.net_step_request), style = MaterialTheme.typography.titleMedium) }
        item { OutlinedTextField(name, { if (it.codePointCount(0, it.length) <= 64) name = it },
            label = { Text(stringResource(R.string.net_name)) }, modifier = Modifier.fillMaxWidth(), enabled = !state.busy) }
        item { Button(onClick = { model.exportRequest(name) }, enabled = !state.loading && !state.busy && name.isNotBlank()) {
            Text(stringResource(R.string.net_create_request))
        } }
        if (state.request.isNotEmpty()) item {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(stringResource(R.string.net_public_request), style = MaterialTheme.typography.bodySmall)
                Text(state.request, fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.bodySmall)
                OutlinedButton(onClick = { copyPublic(state.request) }) { Text(stringResource(R.string.net_copy_request)) }
            }
        }
        item { HorizontalDivider() }
        item { Text(stringResource(R.string.net_step_session), style = MaterialTheme.typography.titleMedium) }
        item { OutlinedTextField(config, { if (it.toByteArray(Charsets.UTF_8).size <= 16384) config = it },
            label = { Text(stringResource(R.string.net_config)) }, visualTransformation = PasswordVisualTransformation(),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
            modifier = Modifier.fillMaxWidth(), maxLines = 4, enabled = !state.busy) }
        target?.let { item { Text(stringResource(R.string.net_target) + " " + it) } }
        item { Button(onClick = { val value = config; config = ""; model.importConnection(value) },
            enabled = !state.loading && !state.busy && target != null) { Text(stringResource(R.string.net_connect)) } }
        if (state.accountId.isNotEmpty()) {
            item { Text(state.origin, style = MaterialTheme.typography.bodyMedium) }
            item { Text(stringResource(R.string.net_my_id) + "\n" + state.accountId, fontFamily = FontFamily.Monospace) }
            item { OutlinedButton(onClick = { copyPublic(state.accountId) }) { Text(stringResource(R.string.net_copy_id)) } }
            item { Text(stringResource(if (state.active) R.string.net_active else R.string.net_expired)) }
            item { Text(stringResource(R.string.net_manual), style = MaterialTheme.typography.bodySmall) }
            item { OutlinedButton(onClick = model::synchronize, enabled = state.active && !state.busy) {
                Text(stringResource(R.string.net_sync))
            } }
            item { OutlinedTextField(peer, { if (it.length <= 36) peer = it.trim() },
                label = { Text(stringResource(R.string.net_peer)) }, modifier = Modifier.fillMaxWidth(), enabled = !state.busy) }
            item { Button(onClick = { model.selectPeer(peer) }, enabled = peer.isNotBlank() && !state.busy) {
                Text(stringResource(R.string.net_open))
            } }
            items(state.messages.map { it.peerAccountId }.distinct()) { id ->
                TextButton(onClick = { peer = id; model.selectPeer(id) }, enabled = !state.busy) { Text(id) }
            }
        }
        if (state.peer.isNotEmpty()) {
            item { HorizontalDivider(); Text(state.peer, style = MaterialTheme.typography.labelMedium) }
            if (state.safetyNumber.isNotEmpty()) item { TextButton(onClick = { showSafety = true }) {
                Text(stringResource(R.string.security))
            } }
            items(state.messages.filter { it.peerAccountId == state.peer }, key = { "${it.outgoing}:${it.id}" }) { message ->
                Surface(color = if (message.outgoing) MaterialTheme.colorScheme.secondaryContainer else MaterialTheme.colorScheme.surfaceVariant,
                    shape = MaterialTheme.shapes.medium) {
                    Column(Modifier.fillMaxWidth().padding(14.dp), verticalArrangement = Arrangement.spacedBy(5.dp)) {
                        Text(message.text)
                        if (message.outgoing) Text(stringResource(if (message.id in state.pendingIds) R.string.net_queued else R.string.net_accepted),
                            style = MaterialTheme.typography.labelSmall)
                    }
                }
            }
            item { OutlinedTextField(draft, { if (it.toByteArray(Charsets.UTF_8).size <= SignalClient.MAX_TEXT_BYTES) draft = it },
                label = { Text(stringResource(R.string.message_hint)) }, maxLines = 5,
                modifier = Modifier.fillMaxWidth(), enabled = !state.busy) }
            item { Button(onClick = { model.send(draft) }, enabled = !state.busy && draft.isNotBlank()) {
                Text(stringResource(R.string.send))
            } }
        }
        if (state.busy) item { LinearProgressIndicator(Modifier.fillMaxWidth()) }
    }
    if (showSafety) AlertDialog(onDismissRequest = { showSafety = false },
        title = { Text(stringResource(R.string.security)) },
        text = { Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text(stringResource(R.string.net_safety))
            Text(state.safetyNumber.chunked(5).chunked(4).joinToString("\n") { it.joinToString(" ") }, fontFamily = FontFamily.Monospace)
        } }, confirmButton = { TextButton(onClick = { showSafety = false }) { Text(stringResource(R.string.close)) } })
}
