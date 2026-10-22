package ru.kniterp.messenger.network

import java.net.Proxy
import java.net.URI
import java.security.SecureRandom
import java.util.UUID
import kotlin.test.*
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.RequestBody.Companion.toRequestBody
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

    /** Full lifecycle on the real API: register, refresh, PIN, transfer to a second phone, old phone revoked, logout. */
    @Test fun loginRefreshPinTransferAndLogoutAgainstRealServer() {
        val base = URI(requireNotNull(System.getenv("MESSENGER_TEST_URL")))
        require(base.scheme == "http" && base.host == "127.0.0.1" && base.port > 0 && base.rawUserInfo == null)
        val operator = requireNotNull(System.getenv("DEV_PROVISIONING_KEY"))
        val transport = OkHttpClient.Builder().proxy(Proxy.NO_PROXY).followRedirects(false).retryOnConnectionFailure(false).build()
        val url = base.toString().toHttpUrl()
        fun code(challenge: Challenge): String {
            val request = okhttp3.Request.Builder().url(url.newBuilder().addPathSegments("api/v1/dev/sms/code").build())
                .header("X-Dev-Provisioning-Token", operator)
                .post("""{"challengeId":"${challenge.challengeId}"}""".toRequestBody("application/json".toMediaType())).build()
            return transport.newCall(request).execute().use { r ->
                assertEquals(200, r.code)
                Wire.json.parseToJsonElement(r.body!!.string()).jsonObject["code"]!!.jsonPrimitive.content
            }
        }
        val number = phone()
        AuthApi(url, transport).use { api ->
            val oldDevice = device(); val oldSigner = SoftwareSigner(); val oldStore = MemoryAuthStore()
            val login = Login(api, oldStore, oldSigner, oldDevice)
            val registered = assertIs<LoginResult.Activated>(login.verifyCode(code(login.requestCode(number)))).session
            assertTrue(registered.newAccount)
            val oldSession = UserSession(api, oldStore, oldSigner, oldDevice).apply { install(registered) }
            val oldApi = PinnedApi(url, oldSession, transport, null, System::currentTimeMillis)
            assertEquals(oldDevice.deviceId, oldApi.me().deviceId)

            // A new process without the access token refreshes with a signed challenge.
            val restarted = UserSession(api, oldStore, oldSigner, oldDevice)
            val refreshedToken = restarted.current()
            assertNotEquals(registered.accessToken, refreshedToken)
            assertNotEquals(registered.refreshToken, oldStore.state.session!!.refreshToken)
            val api2 = PinnedApi(url, restarted, transport, null, System::currentTimeMillis)
            assertEquals(1, api2.me().generation)
            restarted.setPin("482913", null)

            Thread.sleep(61_000) // Server-side 60 s resend gate per number; it is not bypassed for tests.
            val newDevice = device(); val newSigner = SoftwareSigner(); val newStore = MemoryAuthStore()
            val transfer = Login(api, newStore, newSigner, newDevice)
            assertEquals(LoginResult.PinRequired, transfer.verifyCode(code(transfer.requestCode(number))))
            assertEquals("PIN_INVALID", assertFailsWith<ApiFailure> { transfer.verifyPin("000001") }.code)
            assertEquals(1, api2.me().generation) // A wrong PIN changed nothing for the old phone.
            val moved = assertIs<LoginResult.Activated>(transfer.verifyPin("482913")).session
            assertFalse(moved.newAccount)
            assertEquals(2, moved.generation)
            assertEquals(registered.accountId, moved.accountId)

            // Old phone: the 401 triggers one refresh attempt, which the server refuses for the replaced device.
            assertEquals("SESSION_REVOKED", assertFailsWith<SessionEnded> { api2.me() }.code)
            assertEquals(AuthState(ended = "SESSION_REVOKED"), oldStore.state)

            val newSession = UserSession(api, newStore, newSigner, newDevice).apply { install(moved) }
            val newApi = PinnedApi(url, newSession, transport, null, System::currentTimeMillis)
            assertEquals(2, newApi.me().generation)
            newSession.logout()
            assertEquals(AuthState(ended = "LOGOUT"), newStore.state)
            assertEquals("LOGOUT", assertFailsWith<SessionEnded> { newApi.me() }.code)
        }
        transport.connectionPool.evictAll(); transport.dispatcher.executorService.shutdown()
    }
}
