package ru.kniterp.messenger.live

import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import ru.kniterp.messenger.R
import ru.kniterp.messenger.network.LoginError
import ru.kniterp.messenger.network.LoginStep

/** Wrong, exhausted and expired codes, the 60 s resend gate with Retry-After, PIN management and PIN lock. */
@RunWith(AndroidJUnit4::class)
class LiveAuthErrorsTest {
    @Before fun live() = Live.assumeConfigured()

    @Test fun codeErrorsResendGateExpiryPinChangesAndPinLock() {
        val started = System.currentTimeMillis()
        // Challenge left unanswered until it expires (5 minutes).
        val late = Phone().apply { start(); configure() }
        assertEquals(LoginStep.CODE, late.request(Live.number()).login.step)
        val lateCode = late.code()

        // PIN set, refused without/with a wrong current PIN, changed, disabled and set again.
        val pinNumber = Live.number()
        val owner = Phone(); owner.register(pinNumber)
        assertEquals(R.string.auth_err_pin_simple, owner.act { setPin("111111", null) }.error)
        assertEquals(R.string.auth_pin_saved, owner.act { setPin("482913", null) }.notice)
        assertEquals(R.string.auth_err_pin_required, owner.act { setPin("591736", null) }.error)
        assertEquals(R.string.auth_err_pin, owner.act { setPin("591736", "000002") }.error)
        assertEquals(R.string.auth_pin_saved, owner.act { setPin("591736", "482913") }.notice)
        assertEquals(R.string.auth_pin_disabled, owner.act { disablePin("591736") }.notice)
        assertEquals(R.string.auth_pin_saved, owner.act { setPin("482913", null) }.notice)

        // Wrong code counts down, the fifth failure closes the challenge.
        val exhausted = Phone().apply { start(); configure() }
        exhausted.request(Live.number())
        val wrong = Live.wrong(exhausted.code())
        val left = mutableListOf<Int?>()
        repeat(6) {
            val view = exhausted.submitCode(wrong).login
            if (view.error == LoginError.CHALLENGE_GONE) return@repeat
            assertEquals(LoginError.CODE_INVALID, view.error)
            left += view.attemptsLeft
        }
        assertEquals(listOf(4, 3, 2, 1), left)
        assertEquals(LoginError.CHALLENGE_GONE, exhausted.state.login.error)
        assertEquals(LoginStep.PHONE, exhausted.state.login.step)

        // Resend: the local 60 s gate makes no request; a new request is refused by the server with Retry-After.
        val number = Live.number()
        val resend = Phone().apply { start(); configure() }
        val first = resend.request(number).login
        val firstAt = System.currentTimeMillis()
        assertEquals(LoginStep.CODE, first.step)
        assertTrue(first.resendAtMillis!! - System.currentTimeMillis() in 30_000..61_000)
        assertEquals(LoginError.RESEND_TOO_EARLY, resend.act { login { it.resend() } }.login.error)
        resend.act { login { it.changeNumber() } }
        val refused = resend.request(number).login
        assertEquals(LoginError.RESEND_TOO_EARLY, refused.error)
        assertTrue(refused.retryAtMillis!! - System.currentTimeMillis() in 1_000..61_000)

        sleepUntil(firstAt + 62_000)
        assertEquals(LoginStep.CODE, resend.request(number).login.step) // Allowed after the gate.
        assertNull(resend.state.login.error)

        // Transfer attempt with wrong PINs: lock with a retry time; the owner's phone keeps working.
        val thief = Phone().apply { start(); configure() }
        thief.request(pinNumber)
        assertEquals(LoginStep.PIN, thief.submitCode(thief.code()).login.step)
        var locked = thief.state.login
        for (attempt in 1..6) {
            locked = thief.submitPin("00000$attempt").login
            if (locked.error == LoginError.PIN_LOCKED) break
            assertEquals(LoginError.PIN_INVALID, locked.error)
        }
        assertEquals(LoginError.PIN_LOCKED, locked.error)
        assertTrue(locked.retryAtMillis!! - System.currentTimeMillis() > 10 * 60_000)
        assertEquals(locked, thief.submitPin("482913").login) // Refused locally while locked: no request.
        assertNull(owner.sync().error)
        assertTrue(owner.state.ready)

        sleepUntil(started + 5 * 60_000 + 15_000)
        val expired = late.submitCode(lateCode).login
        assertEquals(LoginError.CHALLENGE_GONE, expired.error)
        assertEquals(LoginStep.PHONE, expired.step)
        listOf(late, owner, exhausted, resend, thief).forEach { it.close() }
    }

    private fun sleepUntil(deadline: Long) {
        val wait = deadline - System.currentTimeMillis()
        if (wait > 0) Thread.sleep(wait)
    }
}
