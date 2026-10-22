package ru.kniterp.messenger.network

import java.net.Proxy
import java.net.URI
import java.security.SecureRandom
import java.util.UUID
import kotlin.test.*
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.OkHttpClient
import ru.kniterp.messenger.core.Wire

/**
 * Real ASP.NET API with the fake SMS provider; only this test uses a loopback HTTP origin.
 * The server checks number and device proof BEFORE the code, so CODE_INVALID for a wrong code proves that the
 * Kotlin payload and signature were accepted, without reading the SMS code.
 */
class ServerSmokeTestAuth {
    private val random = SecureRandom()
    private fun device() = LocalDevice(UUID.randomUUID().toString(), 1 + random.nextInt(0x3ffe),
        Wire.base64(ByteArray(33).also { random.nextBytes(it); it[0] = 5 }))
    private fun phone() = "+7912" + (0 until 7).joinToString("") { random.nextInt(10).toString() }

    @Test fun serverAcceptsKotlinDeviceProofAndRejectsSubstitutions() {
        val base = URI(requireNotNull(System.getenv("MESSENGER_TEST_URL")))
        require(base.scheme == "http" && base.host == "127.0.0.1" && base.port > 0 && base.rawUserInfo == null)
        val transport = OkHttpClient.Builder().proxy(Proxy.NO_PROXY).followRedirects(false).retryOnConnectionFailure(false).build()
        AuthApi(base.toString().toHttpUrl(), transport).use { api ->
            val device = device()
            val signer = SoftwareSigner()
            val store = MemoryAuthStore()
            val number = phone()
            val login = Login(api, store, signer, device)
            val challenge = login.requestCode(number)
            assertEquals(6, challenge.codeLength)
            // Retry of the same request id returns the same challenge, so no second SMS is sent.
            store.save(store.state.copy(pendingLogin = store.state.pendingLogin!!.copy(challenge = null)))
            assertEquals(challenge.challengeId, login.requestCode(number).challengeId)

            val wrongCode = if (random.nextBoolean()) "000000" else "999999"
            val accepted = assertFailsWith<ApiFailure> { login.verifyCode(wrongCode) }
            assertEquals("CODE_INVALID", accepted.code) // Proof verified by the server's ECDSA check.
            assertEquals(4, accepted.attemptsRemaining)

            val foreign = assertFailsWith<ApiFailure> { Login(api, store, SoftwareSigner(), device).verifyCode(wrongCode) }
            assertEquals("DEVICE_PROOF_INVALID", foreign.code)
            val otherDevice = assertFailsWith<ApiFailure> { Login(api, store, signer, device()).verifyCode(wrongCode) }
            assertEquals("DEVICE_PROOF_INVALID", otherDevice.code)
            store.save(store.state.copy(pendingLogin = store.state.pendingLogin!!.copy(phone = phone())))
            assertEquals("CHALLENGE_MISMATCH", assertFailsWith<ApiFailure> { login.verifyCode(wrongCode) }.code)
            // Five failures in total: the challenge is closed even for a correct proof.
            store.save(store.state.copy(pendingLogin = store.state.pendingLogin!!.copy(phone = number)))
            assertEquals("CODE_INVALID", assertFailsWith<ApiFailure> { login.verifyCode(wrongCode) }.code)
            assertEquals("CHALLENGE_LOCKED", assertFailsWith<ApiFailure> { login.verifyCode(wrongCode) }.code)

            assertEquals("RESEND_TOO_EARLY", assertFailsWith<ApiFailure> { login.requestCode(number) }.code)
            assertEquals("SESSION_REVOKED", assertFailsWith<ApiFailure> { api.refreshChallenge(device.deviceId) }.code)
        }
        transport.connectionPool.evictAll(); transport.dispatcher.executorService.shutdown()
    }
}
