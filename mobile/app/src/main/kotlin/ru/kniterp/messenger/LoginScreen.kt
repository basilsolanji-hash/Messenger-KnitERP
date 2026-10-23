package ru.kniterp.messenger

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import kotlinx.coroutines.delay
import ru.kniterp.messenger.network.Country
import ru.kniterp.messenger.network.LoginError
import ru.kniterp.messenger.network.LoginStep

/** SMS login, PIN and account actions. Codes and PINs live only in local composition state and are cleared on submit. */
@Composable fun LoginScreen(model: AuthModel = viewModel()) {
    val state by model.state.collectAsState()
    val view = state.login
    var serverJson by remember { mutableStateOf("") }
    var country by remember { mutableStateOf(Country.RU) }
    var phone by remember { mutableStateOf("") }
    var code by remember { mutableStateOf("") }
    var pin by remember { mutableStateOf("") }
    var currentPin by remember { mutableStateOf("") }
    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
    LaunchedEffect(Unit) { model.start() }
    LaunchedEffect(Unit) { while (true) { delay(1000); now = System.currentTimeMillis() } }
    val digits = KeyboardOptions(keyboardType = KeyboardType.NumberPassword, autoCorrectEnabled = false)
    val enabled = !state.loading && !state.busy

    LazyColumn(Modifier.fillMaxSize().imePadding(), contentPadding = PaddingValues(20.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
        item { Text(stringResource(R.string.auth_title), style = MaterialTheme.typography.headlineSmall) }
        if (state.loading) item { CircularProgressIndicator() }
        state.ended?.let { item { Text(stringResource(endedText(it)), color = MaterialTheme.colorScheme.error) } }
        state.error?.let { item { Text(stringResource(it), color = MaterialTheme.colorScheme.error) } }
        view.error?.let { error -> item { Text(errorText(error, view.attemptsLeft, view.retryAtMillis, now), color = MaterialTheme.colorScheme.error) } }
        state.notice?.let { item { Text(stringResource(it)) } }

        if (state.origin.isEmpty() || (view.step == LoginStep.PHONE && state.accountId.isEmpty())) {
            if (state.origin.isNotEmpty()) item { Text(stringResource(R.string.net_target) + " " + state.origin, style = MaterialTheme.typography.bodySmall) }
            item { OutlinedTextField(serverJson, { if (it.length <= 4096) serverJson = it }, label = { Text(stringResource(R.string.auth_server)) },
                modifier = Modifier.fillMaxWidth(), maxLines = 4, enabled = enabled) }
            item { OutlinedButton(onClick = { model.configureServer(serverJson); serverJson = "" }, enabled = enabled && serverJson.isNotBlank()) {
                Text(stringResource(R.string.auth_server_save))
            } }
        }
        if (state.origin.isEmpty()) return@LazyColumn

        when (view.step) {
            LoginStep.PHONE -> {
                item { Text(stringResource(R.string.auth_notice), style = MaterialTheme.typography.bodySmall) }
                item {
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        Country.entries.forEach { c ->
                            FilterChip(country == c, { country = c }, label = { Text(stringResource(countryName(c))) }, enabled = enabled)
                        }
                    }
                }
                item { OutlinedTextField(phone, { if (it.length <= 24) phone = it }, label = { Text(stringResource(R.string.auth_phone)) },
                    prefix = { Text("+" + country.callingCode + " ") }, singleLine = true, modifier = Modifier.fillMaxWidth(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Phone, autoCorrectEnabled = false), enabled = enabled) }
                item { Text(stringResource(R.string.auth_transfer_warning), style = MaterialTheme.typography.bodySmall) }
                item { Button(onClick = { model.submitPhone(country, phone) }, enabled = enabled && phone.isNotBlank()) {
                    Text(stringResource(R.string.auth_get_code))
                } }
            }
            LoginStep.CODE -> {
                item { Text(view.phone, fontFamily = FontFamily.Monospace) }
                item { OutlinedTextField(code, { if (it.length <= view.codeLength && it.all(Char::isDigit)) code = it },
                    label = { Text(stringResource(R.string.auth_code)) }, singleLine = true, keyboardOptions = digits,
                    modifier = Modifier.fillMaxWidth(), enabled = enabled) }
                item { Button(onClick = { val value = code; code = ""; model.submitCode(value) }, enabled = enabled && code.length == view.codeLength) {
                    Text(stringResource(R.string.auth_confirm))
                } }
                item {
                    val wait = view.resendAtMillis?.let { ((it - now + 999) / 1000).coerceAtLeast(0) } ?: 0
                    TextButton(onClick = model::resend, enabled = enabled && wait == 0L) {
                        Text(if (wait > 0) stringResource(R.string.auth_resend_in, wait.toInt()) else stringResource(R.string.auth_resend))
                    }
                }
                item { TextButton(onClick = model::changeNumber, enabled = enabled) { Text(stringResource(R.string.auth_change_number)) } }
            }
            LoginStep.PIN -> {
                item { Text(stringResource(R.string.auth_pin_title), style = MaterialTheme.typography.titleMedium) }
                item { Text(stringResource(R.string.auth_pin_no_recovery), style = MaterialTheme.typography.bodySmall) }
                item { OutlinedTextField(pin, { if (it.length <= 6 && it.all(Char::isDigit)) pin = it }, label = { Text(stringResource(R.string.auth_pin_hint)) },
                    visualTransformation = PasswordVisualTransformation(), singleLine = true, keyboardOptions = digits,
                    modifier = Modifier.fillMaxWidth(), enabled = enabled) }
                item {
                    val locked = view.error == LoginError.PIN_LOCKED && (view.retryAtMillis ?: 0) > now
                    Button(onClick = { val value = pin; pin = ""; model.submitPin(value) }, enabled = enabled && !locked && pin.length == 6) {
                        Text(stringResource(R.string.auth_confirm))
                    }
                }
                item { TextButton(onClick = model::changeNumber, enabled = enabled) { Text(stringResource(R.string.auth_change_number)) } }
            }
            LoginStep.ACTIVATED -> {
                item { Text(stringResource(if (view.newAccount == false) R.string.auth_done_transfer else R.string.auth_done_new),
                    style = MaterialTheme.typography.titleMedium) }
                item { Text(stringResource(R.string.auth_account) + "\n" + state.accountId, fontFamily = FontFamily.Monospace) }
                item { Text(stringResource(R.string.auth_offer_pin), style = MaterialTheme.typography.bodySmall) }
                item { OutlinedTextField(currentPin, { if (it.length <= 6 && it.all(Char::isDigit)) currentPin = it },
                    label = { Text(stringResource(R.string.auth_current_pin)) }, visualTransformation = PasswordVisualTransformation(),
                    singleLine = true, keyboardOptions = digits, modifier = Modifier.fillMaxWidth(), enabled = enabled) }
                item { OutlinedTextField(pin, { if (it.length <= 6 && it.all(Char::isDigit)) pin = it },
                    label = { Text(stringResource(R.string.auth_new_pin)) }, visualTransformation = PasswordVisualTransformation(),
                    singleLine = true, keyboardOptions = digits, modifier = Modifier.fillMaxWidth(), enabled = enabled) }
                item {
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        Button(onClick = { val p = pin; val c = currentPin.ifEmpty { null }; pin = ""; currentPin = ""; model.setPin(p, c) },
                            enabled = enabled && pin.length == 6) { Text(stringResource(R.string.auth_set_pin)) }
                        OutlinedButton(onClick = { val c = currentPin; currentPin = ""; model.disablePin(c) },
                            enabled = enabled && currentPin.length == 6) { Text(stringResource(R.string.auth_disable_pin)) }
                    }
                }
                item { HorizontalDivider() }
                item { OutlinedButton(onClick = model::logout, enabled = enabled) { Text(stringResource(R.string.auth_logout)) } }
            }
        }
    }
}

private fun countryName(c: Country) = when (c) {
    Country.RU -> R.string.auth_country_ru
    Country.BY -> R.string.auth_country_by
    Country.KZ -> R.string.auth_country_kz
    Country.UZ -> R.string.auth_country_uz
}

private fun endedText(code: String) = when (code) {
    "SESSION_REVOKED" -> R.string.auth_ended_revoked
    "SESSION_EXPIRED" -> R.string.auth_ended_expired
    "LOGOUT" -> R.string.auth_ended_logout
    else -> R.string.auth_ended_security
}

@Composable private fun errorText(error: LoginError, attempts: Int?, retryAt: Long?, now: Long): String {
    val minutes = retryAt?.let { ((it - now + 59_999) / 60_000).coerceAtLeast(1).toInt() } ?: 1
    val seconds = retryAt?.let { ((it - now + 999) / 1000).coerceAtLeast(1).toInt() } ?: 60
    val base = when (error) {
        LoginError.PHONE_INVALID -> stringResource(R.string.auth_err_phone)
        LoginError.REGION_NOT_ALLOWED -> stringResource(R.string.auth_err_region)
        LoginError.NOT_MOBILE -> stringResource(R.string.auth_err_not_mobile)
        LoginError.RESEND_TOO_EARLY -> stringResource(R.string.auth_resend_in, seconds)
        LoginError.RATE_LIMITED -> stringResource(R.string.auth_err_rate, minutes)
        LoginError.SMS_UNAVAILABLE -> stringResource(R.string.auth_err_sms)
        LoginError.CODE_INVALID -> stringResource(R.string.auth_err_code)
        LoginError.CHALLENGE_GONE -> stringResource(R.string.auth_err_gone)
        LoginError.PIN_INVALID -> stringResource(R.string.auth_err_pin)
        LoginError.PIN_LOCKED -> stringResource(R.string.auth_err_pin_locked) + " " + stringResource(R.string.auth_locked_for, minutes)
        LoginError.DEVICE_REJECTED -> stringResource(R.string.auth_err_device)
        LoginError.NETWORK -> stringResource(R.string.auth_err_network)
        LoginError.SERVER -> stringResource(R.string.auth_err_server)
    }
    return if (attempts != null) base + " " + stringResource(R.string.auth_attempts_left, attempts) else base
}
