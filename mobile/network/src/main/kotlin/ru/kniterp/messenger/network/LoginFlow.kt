package ru.kniterp.messenger.network

import java.io.IOException
import java.time.OffsetDateTime

enum class LoginStep { PHONE, CODE, PIN, ACTIVATED }

/** What the login UI may show. Every server code maps to one of these; raw codes and trace ids never reach the user. */
enum class LoginError {
    PHONE_INVALID, REGION_NOT_ALLOWED, NOT_MOBILE, RESEND_TOO_EARLY, RATE_LIMITED, SMS_UNAVAILABLE,
    CODE_INVALID, CHALLENGE_GONE, PIN_INVALID, PIN_LOCKED,
    /** The device keys cannot be used for this activation; the app must create a fresh profile and keys. */
    DEVICE_REJECTED,
    NETWORK, SERVER,
}

/** UI state. It holds no code, PIN, token or key. The phone is the user's own input, shown back to them. */
data class LoginView(
    val step: LoginStep = LoginStep.PHONE,
    val phone: String = "",
    val codeLength: Int = 6,
    val resendAtMillis: Long? = null,
    val attemptsLeft: Int? = null,
    val retryAtMillis: Long? = null,
    val error: LoginError? = null,
    val newAccount: Boolean? = null,
) {
    override fun toString() = "LoginView(step=$step, error=$error)"
}

/**
 * Screen logic for SMS login with optional PIN. Synchronous and UI-free: Android calls it on IO and renders [LoginView].
 * State survives process death through [LoginSteps.pending], so a restart resumes at the code or PIN step.
 */
class LoginFlow(private val steps: LoginSteps, private val clock: () -> Long = System::currentTimeMillis) {
    private var view = restore()

    fun view(): LoginView = view

    private fun restore(): LoginView {
        val pending = steps.pending() ?: return LoginView()
        val challenge = pending.challenge ?: return LoginView(phone = pending.phone)
        if (millis(challenge.expiresAt) <= clock()) { steps.abandon(); return LoginView(phone = pending.phone, error = LoginError.CHALLENGE_GONE) }
        return LoginView(if (pending.pinRequired) LoginStep.PIN else LoginStep.CODE, pending.phone, challenge.codeLength,
            challenge.resendAfter?.let(::millis))
    }

    fun submitPhone(country: Country, typed: String): LoginView {
        val phone = try { PhoneInput.e164(country, typed) } catch (_: IllegalArgumentException) {
            return set(view.copy(step = LoginStep.PHONE, error = LoginError.PHONE_INVALID))
        }
        return request(phone)
    }

    /** "Send again": allowed after resendAfter; the server enforces the same 60 s gate. */
    fun resend(): LoginView {
        if (view.step != LoginStep.CODE) return view
        view.resendAtMillis?.let { if (it > clock()) return set(view.copy(error = LoginError.RESEND_TOO_EARLY, retryAtMillis = it)) }
        return request(view.phone)
    }

    fun submitCode(code: String): LoginView {
        if (view.step != LoginStep.CODE) return view
        if (code.length != view.codeLength || !code.all { it in '0'..'9' }) return set(view.copy(error = LoginError.CODE_INVALID))
        return attempt { finish(steps.verifyCode(code)) }
    }

    fun submitPin(pin: String): LoginView {
        if (view.step != LoginStep.PIN) return view
        view.retryAtMillis?.let { if (view.error == LoginError.PIN_LOCKED && it > clock()) return view }
        if (pin.length != 6 || !pin.all { it in '0'..'9' }) return set(view.copy(error = LoginError.PIN_INVALID, attemptsLeft = null))
        return attempt { finish(steps.verifyPin(pin)) }
    }

    /** Back to the number: the current challenge is dropped, a later request creates a new one. */
    fun changeNumber(): LoginView {
        if (view.step == LoginStep.ACTIVATED) return view
        steps.abandon()
        return set(LoginView(phone = view.phone))
    }

    private fun request(phone: String): LoginView = attempt {
        val challenge = steps.requestCode(phone)
        LoginView(LoginStep.CODE, phone, challenge.codeLength, challenge.resendAfter?.let(::millis))
    }

    private fun finish(result: LoginResult): LoginView = when (result) {
        is LoginResult.Activated -> LoginView(LoginStep.ACTIVATED, view.phone, newAccount = result.session.newAccount)
        LoginResult.PinRequired -> view.copy(step = LoginStep.PIN, error = null, attemptsLeft = null, retryAtMillis = null)
    }

    private fun attempt(action: () -> LoginView): LoginView = try { set(action()) } catch (failure: ApiFailure) {
        set(failed(failure))
    } catch (_: IOException) {
        set(view.copy(error = LoginError.NETWORK)) // Nothing changes: the same request is safe to repeat.
    }

    private fun failed(f: ApiFailure): LoginView {
        val retryAt = f.retryAfterSeconds?.let { clock() + it * 1000 }
        return when (f.code) {
            "PHONE_INVALID" -> view.copy(error = LoginError.PHONE_INVALID)
            "PHONE_REGION_NOT_ALLOWED" -> view.copy(error = LoginError.REGION_NOT_ALLOWED)
            "PHONE_NOT_MOBILE" -> view.copy(error = LoginError.NOT_MOBILE)
            "RESEND_TOO_EARLY" -> view.copy(error = LoginError.RESEND_TOO_EARLY, retryAtMillis = retryAt)
            "RATE_LIMITED", "SMS_BUSY" -> view.copy(error = LoginError.RATE_LIMITED, retryAtMillis = retryAt)
            "SMS_UNAVAILABLE", "SMS_NOT_SENT" -> view.copy(error = LoginError.SMS_UNAVAILABLE, retryAtMillis = retryAt)
            "CODE_INVALID" -> if (f.attemptsRemaining == 0) { steps.abandon(); LoginView(phone = view.phone, error = LoginError.CHALLENGE_GONE) }
                else view.copy(error = LoginError.CODE_INVALID, attemptsLeft = f.attemptsRemaining)
            "PIN_INVALID" -> view.copy(error = LoginError.PIN_INVALID, attemptsLeft = f.attemptsRemaining, retryAtMillis = retryAt)
            "PIN_LOCKED" -> view.copy(error = LoginError.PIN_LOCKED, retryAtMillis = retryAt)
            "CODE_REQUIRED" -> view.copy(step = LoginStep.CODE, error = null)
            // The challenge can no longer succeed: drop it so the next request is a NEW one, not a replay.
            "CHALLENGE_LOCKED", "CHALLENGE_EXPIRED", "CHALLENGE_SUPERSEDED", "CHALLENGE_USED", "CHALLENGE_NOT_FOUND",
            "CHALLENGE_NOT_SENT", "CHALLENGE_MISMATCH" -> { steps.abandon(); LoginView(phone = view.phone, error = LoginError.CHALLENGE_GONE) }
            "DEVICE_PROOF_INVALID", "DEVICE_ALREADY_EXISTS", "CONCURRENT_ACTIVATION", "INVALID_IDENTITY_KEY", "INVALID_AUTH_KEY" -> {
                steps.abandon(); LoginView(phone = view.phone, error = LoginError.DEVICE_REJECTED)
            }
            else -> view.copy(error = LoginError.SERVER)
        }
    }

    private fun set(value: LoginView): LoginView { view = value; return value }
    private fun millis(iso: String) = OffsetDateTime.parse(iso).toInstant().toEpochMilli()
}
