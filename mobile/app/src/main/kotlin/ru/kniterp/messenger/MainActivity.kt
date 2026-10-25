package ru.kniterp.messenger

import android.os.Bundle
import android.view.WindowManager
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.appcompat.app.AppCompatActivity
import androidx.appcompat.app.AppCompatDelegate
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Email
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.os.LocaleListCompat
import androidx.lifecycle.viewmodel.compose.viewModel

class MainActivity : AppCompatActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // Codes, PINs, operator JSON and message text: no screenshots, screen recording or recent-apps thumbnail.
        window.setFlags(WindowManager.LayoutParams.FLAG_SECURE, WindowManager.LayoutParams.FLAG_SECURE)
        enableEdgeToEdge()
        setContent { OsMessenger() }
    }
}

private val Teal = Color(0xFF087F76)
private val Ink = Color(0xFF172E39)
private val Mist = Color(0xFFF2F6F5)
private val scheme = lightColorScheme(primary = Teal, onPrimary = Color.White, secondary = Teal,
    secondaryContainer = Color(0xFFE4F4ED), onSecondaryContainer = Teal,
    background = Color.White, surface = Color.White, onSurface = Ink, surfaceVariant = Mist)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun OsMessenger(model: DemoModel = viewModel()) {
    val state by model.state.collectAsState()
    val context = LocalContext.current
    LaunchedEffect(Unit) { model.start(context.getString(R.string.demo_welcome)) }
    var screen by rememberSaveable { mutableStateOf("chats") }
    var showSafety by rememberSaveable { mutableStateOf(false) }
    BackHandler(screen == "conversation" || screen == "server" || screen == "login") { screen = if (screen == "server" || screen == "login") "settings" else "chats" }
    MaterialTheme(colorScheme = scheme) {
        Scaffold(
            topBar = {
                if (screen == "conversation") {
                    TopAppBar(title = {
                        Column {
                            Text(stringResource(R.string.test_chat), style = MaterialTheme.typography.titleMedium)
                            Text(stringResource(R.string.local_demo), style = MaterialTheme.typography.labelSmall, color = Teal)
                        }
                    }, navigationIcon = { IconButton(onClick = { screen = "chats" }) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, stringResource(R.string.back))
                    } }, actions = { IconButton(onClick = { showSafety = true }, enabled = state.safetyNumber.isNotEmpty()) {
                        Icon(Icons.Default.Lock, stringResource(R.string.security), tint = Teal)
                    } })
                } else {
                    TopAppBar(title = { Brand() })
                }
            },
            bottomBar = {
                if (screen != "conversation") NavigationBar(containerColor = Color.White, tonalElevation = 0.dp) {
                    NavigationBarItem(screen == "chats", { screen = "chats" },
                        icon = { Icon(Icons.Default.Email, null) }, label = { Text(stringResource(R.string.chats)) })
                    NavigationBarItem(screen == "settings", { screen = "settings" },
                        icon = { Icon(Icons.Default.Settings, null) }, label = { Text(stringResource(R.string.settings)) })
                }
            },
        ) { padding ->
            Column(Modifier.fillMaxSize().padding(padding)) {
                if (screen == "settings") Settings(openServer = { screen = "server" }, openLogin = { screen = "login" })
                else if (screen == "server") NetworkScreen()
                else if (screen == "login") LoginScreen()
                else if (state.loading) {
                    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(16.dp)) {
                            CircularProgressIndicator()
                            Text(stringResource(R.string.loading))
                        }
                    }
                } else {
                    state.error?.let { Text(stringResource(it), Modifier.fillMaxWidth().background(Color(0xFFFFEDE9)).padding(16.dp), color = Color(0xFF8F2817)) }
                    when (screen) {
                        "conversation" -> Conversation(state) { text -> model.send(text, context.getString(R.string.demo_reply)) }
                        else -> Chats(state) { screen = "conversation" }
                    }
                }
            }
        }
        if (showSafety) AlertDialog(onDismissRequest = { showSafety = false },
            icon = { Icon(Icons.Default.Lock, null, tint = Teal) },
            title = { Text(stringResource(R.string.security)) },
            text = { Column(verticalArrangement = Arrangement.spacedBy(20.dp)) {
                Text(stringResource(R.string.security_description))
                Text(state.safetyNumber.chunked(5).chunked(4).joinToString("\n") { it.joinToString(" ") },
                    fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.titleMedium)
            } }, confirmButton = { TextButton(onClick = { showSafety = false }) { Text(stringResource(R.string.close)) } })
    }
}

@Composable private fun Brand() {
    Column {
        Text(stringResource(R.string.brand_name), fontWeight = FontWeight.Bold,
            style = MaterialTheme.typography.headlineMedium)
        Text(stringResource(R.string.messenger), color = Teal,
            style = MaterialTheme.typography.labelMedium)
    }
}

@Composable private fun Chats(state: DemoState, open: () -> Unit) {
    Column(Modifier.fillMaxSize()) {
        Text(stringResource(R.string.chats), Modifier.padding(horizontal = 20.dp, vertical = 14.dp),
            style = MaterialTheme.typography.headlineLarge, fontWeight = FontWeight.Bold)
        Surface(color = Mist, shape = RoundedCornerShape(16.dp), modifier = Modifier.padding(horizontal = 20.dp)) {
            Row(Modifier.padding(16.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Icon(Icons.Default.Info, null, tint = Teal)
                Text(stringResource(R.string.demo_description), style = MaterialTheme.typography.bodySmall)
            }
        }
        Row(Modifier.fillMaxWidth().clickable(enabled = state.error == null, onClick = open).padding(20.dp),
            verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(14.dp)) {
            Box(Modifier.size(54.dp).background(Color(0xFFDFF2EB), CircleShape), contentAlignment = Alignment.Center) {
                Icon(Icons.Default.Person, null, tint = Teal, modifier = Modifier.size(30.dp))
            }
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(5.dp)) {
                Text(stringResource(R.string.test_chat), style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                Text(state.messages.lastOrNull()?.text ?: stringResource(R.string.local_demo), maxLines = 1,
                    overflow = TextOverflow.Ellipsis, color = Color(0xFF64777D), style = MaterialTheme.typography.bodyMedium)
            }
            Icon(Icons.Default.Lock, null, tint = Teal, modifier = Modifier.size(17.dp))
        }
        HorizontalDivider(color = Mist)
    }
}

@Composable private fun Conversation(state: DemoState, send: (String) -> Unit) {
    var draft by remember { mutableStateOf("") }
    val list = rememberLazyListState()
    LaunchedEffect(state.messages.size) { if (state.messages.isNotEmpty()) list.animateScrollToItem(state.messages.lastIndex) }
    Column(Modifier.fillMaxSize().background(Mist).imePadding()) {
        LazyColumn(Modifier.weight(1f).fillMaxWidth(), state = list,
            contentPadding = PaddingValues(horizontal = 16.dp, vertical = 20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            items(state.messages, key = { "${it.outgoing}:${it.id}" }) { message ->
                Row(Modifier.fillMaxWidth(), horizontalArrangement = if (message.outgoing) Arrangement.End else Arrangement.Start) {
                    Surface(shape = RoundedCornerShape(18.dp, 18.dp, if (message.outgoing) 4.dp else 18.dp, if (message.outgoing) 18.dp else 4.dp),
                        color = if (message.outgoing) Color(0xFFD9F2E6) else Color.White, modifier = Modifier.widthIn(max = 300.dp)) {
                        Column(Modifier.padding(13.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                            Text(message.text, style = MaterialTheme.typography.bodyLarge)
                            if (message.outgoing) Icon(Icons.Default.Check, stringResource(R.string.local_delivery),
                                tint = Teal, modifier = Modifier.size(15.dp).align(Alignment.End))
                        }
                    }
                }
            }
        }
        Row(Modifier.fillMaxWidth().background(Color.White).padding(12.dp), verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            OutlinedTextField(draft, { if (it.toByteArray(Charsets.UTF_8).size <= SignalClientLimit) draft = it },
                Modifier.weight(1f), placeholder = { Text(stringResource(R.string.message_hint)) }, maxLines = 4,
                shape = RoundedCornerShape(24.dp), enabled = !state.busy && state.error == null)
            FilledIconButton(onClick = { val text = draft; draft = ""; send(text) },
                enabled = draft.isNotBlank() && !state.busy && state.error == null, modifier = Modifier.size(48.dp)) {
                if (state.busy) CircularProgressIndicator(Modifier.size(22.dp), strokeWidth = 2.dp)
                else Icon(Icons.AutoMirrored.Filled.Send, stringResource(R.string.send))
            }
        }
    }
}
private const val SignalClientLimit = 16 * 1024

@Composable private fun Settings(openServer: () -> Unit, openLogin: () -> Unit) {
    val selected = AppCompatDelegate.getApplicationLocales().toLanguageTags()
    val options = listOf("" to stringResource(R.string.system_language), "ru" to "Русский", "be" to "Беларуская",
        "kk" to "Қазақша", "uz-Latn" to "O‘zbekcha")
    LazyColumn(contentPadding = PaddingValues(20.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        item { Text(stringResource(R.string.settings), style = MaterialTheme.typography.headlineLarge, fontWeight = FontWeight.Bold) }
        item { Text(stringResource(R.string.language), Modifier.padding(top = 12.dp, bottom = 6.dp), color = Teal, fontWeight = FontWeight.SemiBold) }
        items(options) { (tag, label) ->
            Surface(shape = RoundedCornerShape(12.dp), color = if (selected == tag) Color(0xFFE4F4ED) else Mist,
                modifier = Modifier.fillMaxWidth().clickable { AppCompatDelegate.setApplicationLocales(LocaleListCompat.forLanguageTags(tag)) }) {
                Row(Modifier.padding(horizontal = 14.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text(label, Modifier.weight(1f))
                    RadioButton(selected == tag, onClick = null)
                }
            }
        }
        item { HorizontalDivider(Modifier.padding(vertical = 16.dp)) }
        item { Text(stringResource(R.string.privacy), color = Teal, fontWeight = FontWeight.SemiBold) }
        item { Text(stringResource(R.string.storage_protection)) }
        item { Text(stringResource(R.string.backup_off)) }
        item { Button(onClick = openLogin) { Text(stringResource(R.string.auth_title)) } }
        item { TextButton(onClick = openServer) { Text(stringResource(R.string.net_title)) } }
        item { Text(stringResource(R.string.development_build), Modifier.padding(top = 24.dp), style = MaterialTheme.typography.labelMedium) }
        item { Text(stringResource(R.string.no_remote_chats), color = Color(0xFF64777D), style = MaterialTheme.typography.bodySmall) }
    }
}
