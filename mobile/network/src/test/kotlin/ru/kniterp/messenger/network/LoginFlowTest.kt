package ru.kniterp.messenger.network

import java.io.IOException
import java.time.Instant
import java.util.UUID
import kotlin.test.*

class LoginFlowTest {
    private var now = 1_800_000_000_000L
    private val calls = mutableListOf<String>()
    private val answers = ArrayDeque<() -> Any>()
    private var pending: PendingLogin? = null

    private val steps = object : LoginSteps {
        override fun pending() = pending
        override fun abandon() { calls += "abandon"; pending = null }
        override fun requestCode(phone: String): Challenge { calls += "request:$phone"; return next() as Challenge }
        override fun verifyCode(code: String): LoginResult { calls += "code"; return next() as LoginResult }
        override fun verifyPin(pin: String): LoginResult { calls += "pin"; return next() as LoginResult }
    }
    private fun next(): Any = answers.removeFirst()()
    private fun answer(value: Any) = answers.add { value }
    private fun fail(code: String, status: Int = 400, attempts: Int? = null, retry: Long? = null) =
        answers.add { throw ApiFailure(status, code, attempts, retry) }
    private fun iso(millis: Long) = Instant.ofEpochMilli(millis).toString()
    private fun challenge() = Challenge(UUID.randomUUID().toString(), "AA==", iso(now + 300_000), iso(now + 60_000), 6, 5)
    private fun activated(newAccount: Boolean) = LoginResult.Activated(IssuedSession(UUID.randomUUID().toString(),
        UUID.randomUUID().toString(), 1, "a", iso(now + 900_000), "r", iso(now + 86_400_000), newAccount))
    private fun flow() = LoginFlow(steps) { now }

    @Test fun localValidationNeverReachesNetwork() {
        val f = flow()
        assertEquals(LoginError.PHONE_INVALID, f.submitPhone(Country.RU, "12345").error)
        assertEquals(LoginStep.PHONE, f.submitCode("123456").step)
        assertTrue(calls.isEmpty())
    }

    @Test fun phoneThenCodeActivatesNewAccount() {
        val f = flow()
        answer(challenge())
        val code = f.submitPhone(Country.KZ, "701 123 4567")
        assertEquals(LoginStep.CODE, code.step)
        assertEquals("+77011234567", code.phone)
        assertEquals(now + 60_000, code.resendAtMillis)
        assertEquals(LoginError.CODE_INVALID, f.submitCode("12a456").error) // Rejected locally.
        answer(activated(true))
        val done = f.submitCode("123456")
        assertEquals(LoginStep.ACTIVATED, done.step)
        assertEquals(true, done.newAccount)
        assertEquals(listOf("request:+77011234567", "code"), calls)
    }

    @Test fun resendWaitsForServerTimeThenRequestsAgain() {
        val f = flow()
        answer(challenge()); f.submitPhone(Country.RU, "900 123 45 67")
        assertEquals(LoginError.RESEND_TOO_EARLY, f.resend().error)
        assertEquals(1, calls.size)
        now += 61_000
        answer(challenge())
        assertNull(f.resend().error)
        assertEquals(2, calls.count { it.startsWith("request") })
    }

    @Test fun wrongCodesCountDownAndExhaustionDropsChallenge() {
        val f = flow()
        answer(challenge()); f.submitPhone(Country.RU, "9001234567")
        fail("CODE_INVALID", attempts = 3)
        assertEquals(3, f.submitCode("000000").attemptsLeft)
        fail("CODE_INVALID", attempts = 0)
        val gone = f.submitCode("000001")
        assertEquals(LoginStep.PHONE, gone.step)
        assertEquals(LoginError.CHALLENGE_GONE, gone.error)
        assertEquals("abandon", calls.last()) // The next request must be a new one, not a replay.
    }

    @Test fun deadChallengeCodesReturnToPhoneAndAbandon() {
        for (code in listOf("CHALLENGE_EXPIRED", "CHALLENGE_LOCKED", "CHALLENGE_SUPERSEDED", "CHALLENGE_USED", "CHALLENGE_MISMATCH")) {
            calls.clear()
            val f = flow()
            answer(challenge()); f.submitPhone(Country.RU, "9001234567")
            fail(code, 409)
            assertEquals(LoginView(phone = "+79001234567", error = LoginError.CHALLENGE_GONE), f.submitCode("123456"))
            assertEquals("abandon", calls.last())
        }
    }

    @Test fun pinStepLockoutAndSuccess() {
        val f = flow()
        answer(challenge()); f.submitPhone(Country.BY, "29 123 45 67")
        answer(LoginResult.PinRequired)
        assertEquals(LoginStep.PIN, f.submitCode("123456").step)
        fail("PIN_INVALID", 403, attempts = 1)
        assertEquals(1, f.submitPin("000000").attemptsLeft)
        fail("PIN_LOCKED", 429, retry = 900)
        val locked = f.submitPin("000001")
        assertEquals(LoginError.PIN_LOCKED, locked.error)
        assertEquals(now + 900_000, locked.retryAtMillis)
        val before = calls.size
        f.submitPin("482913")
        assertEquals(before, calls.size) // No request while the server lock is known to be active.
        now += 900_001
        answer(activated(false))
        val done = f.submitPin("482913")
        assertEquals(LoginStep.ACTIVATED, done.step)
        assertEquals(false, done.newAccount)
    }

    @Test fun networkFailureKeepsStepForIdenticalRetry() {
        val f = flow()
        answers.add { throw IOException("reset") }
        val failed = f.submitPhone(Country.UZ, "90 123 45 67")
        assertEquals(LoginStep.PHONE, failed.step)
        assertEquals(LoginError.NETWORK, failed.error)
        assertFalse("abandon" in calls)
    }

    @Test fun rejectedDeviceKeysAskForFreshProfile() {
        val f = flow()
        fail("DEVICE_ALREADY_EXISTS", 409)
        assertEquals(LoginError.DEVICE_REJECTED, f.submitPhone(Country.RU, "9001234567").error)
        answer(challenge()); f.submitPhone(Country.RU, "9001234567")
        fail("DEVICE_PROOF_INVALID", 403)
        assertEquals(LoginError.DEVICE_REJECTED, f.submitCode("123456").error)
        fail("SOMETHING_NEW", 500)
        assertEquals(LoginError.SERVER, f.submitPhone(Country.RU, "9001234567").error)
    }

    @Test fun restartResumesAtSavedStepOrExpires() {
        val c = challenge()
        pending = PendingLogin("r", "+79001234567", c, pinRequired = false)
        assertEquals(LoginStep.CODE, flow().view().step)
        pending = PendingLogin("r", "+79001234567", c, pinRequired = true)
        assertEquals(LoginStep.PIN, flow().view().step)
        now += 300_001
        val expired = flow().view()
        assertEquals(LoginError.CHALLENGE_GONE, expired.error)
        assertNull(pending)
        assertFalse(expired.toString().contains("9001234567"))
    }
}
