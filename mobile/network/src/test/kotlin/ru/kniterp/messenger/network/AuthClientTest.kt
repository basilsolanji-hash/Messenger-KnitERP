package ru.kniterp.messenger.network

import java.io.File
import java.io.IOException
import java.net.Proxy
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.security.spec.PKCS8EncodedKeySpec
import java.security.spec.X509EncodedKeySpec
import java.time.Instant
import java.util.UUID
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLException
import kotlin.test.*
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import mockwebserver3.Dispatcher
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import mockwebserver3.RecordedRequest
import mockwebserver3.SocketEffect
import okhttp3.CertificatePinner
import okhttp3.OkHttpClient
import okhttp3.Protocol
import okhttp3.tls.HandshakeCertificates
import okhttp3.tls.HeldCertificate
import ru.kniterp.messenger.core.*

/** JVM signer with the same java.security API and DER output as the AndroidKeyStore-backed signer. */
class SoftwareSigner : DeviceSigner {
    private val pair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
    override val authKey: ByteArray = pair.public.encoded
    override fun sign(payload: ByteArray): ByteArray =
        Signature.getInstance("SHA256withECDSA").run { initSign(pair.private); update(payload); sign() }
    fun verifies(payload: ByteArray, signature: ByteArray) =
        Signature.getInstance("SHA256withECDSA").run { initVerify(pair.public); update(payload); verify(signature) }
}

class MemoryAuthStore : AuthStore {
    @Volatile var state = AuthState()
    override fun load() = state
    override fun save(state: AuthState) { this.state = state }
}

class AuthClientTest {
    private val phone = "+79001234567"
    private val device = LocalDevice(UUID.randomUUID().toString(), 4242, Wire.base64(ByteArray(33) { if (it == 0) 5 else 17 }))
    private val signer = SoftwareSigner()
    private val store = MemoryAuthStore()
    private val requests = ConcurrentLinkedQueue<RecordedRequest>()

    private class Routes(val handlers: MutableMap<String, ArrayDeque<MockResponse>>, val sink: ConcurrentLinkedQueue<RecordedRequest>) : Dispatcher() {
        override fun dispatch(request: RecordedRequest): MockResponse {
            sink.add(request)
            val queue = handlers[request.url.encodedPath] ?: return MockResponse.Builder().code(404).build()
            return synchronized(queue) { if (queue.size > 1) queue.removeFirst() else queue.first() }
        }
    }
    private val routes = mutableMapOf<String, ArrayDeque<MockResponse>>()
    private fun on(path: String, vararg responses: MockResponse) { routes["/api/v1/$path"] = ArrayDeque(responses.toList()) }
    private fun json(body: String, code: Int = 200) = MockResponse.Builder().code(code).body(body).build()
    private fun problem(code: String, status: Int) = json("""{"status":$status,"code":"$code","traceId":"x"}""", status)
    private val lost get() = MockResponse.Builder().code(200).body("{}").onResponseStart(SocketEffect.CloseSocket()).build()
    private fun sent(path: String) = requests.filter { it.url.encodedPath == "/api/v1/$path" }
    private fun body(request: RecordedRequest) = Wire.json.parseToJsonElement(request.body!!.utf8()).jsonObject

    private fun fixture(block: (ServerEndpoint, OkHttpClient) -> Unit) {
        val certificate = HeldCertificate.Builder().addSubjectAlternativeName("127.0.0.1").build()
        val backup = HeldCertificate.Builder().build()
        val trust = HandshakeCertificates.Builder().addTrustedCertificate(certificate.certificate).build()
        MockWebServer().use { server ->
            server.useHttps(HandshakeCertificates.Builder().heldCertificate(certificate).build().sslSocketFactory())
            server.dispatcher = Routes(routes, requests)
            server.protocols = listOf(Protocol.HTTP_1_1) // Socket effects model a dropped TCP connection.
            server.start()
            val endpoint = ServerEndpoint(server.url("/").newBuilder().host("127.0.0.1").build().toString(),
                listOf(CertificatePinner.pin(certificate.certificate), CertificatePinner.pin(backup.certificate)))
            val base = OkHttpClient.Builder().sslSocketFactory(trust.sslSocketFactory(), trust.trustManager).proxy(Proxy.NO_PROXY).build()
            try { block(endpoint, base) } finally { base.connectionPool.evictAll(); base.dispatcher.executorService.shutdown() }
        }
    }

    private fun challenge(minutes: Long = 5) = Challenge(UUID.randomUUID().toString(), Wire.base64(ByteArray(32) { it.toByte() }),
        Instant.now().plusSeconds(minutes * 60).toString(), Instant.now().plusSeconds(60).toString(), 6, 5)
    private fun issued(token: String = "access-" + UUID.randomUUID(), refresh: String = "rt1.${UUID.randomUUID()}." + "A".repeat(43),
                       expires: Instant = Instant.now().plusSeconds(900), account: String = ACCOUNT) =
        IssuedSession(account, device.deviceId, 1, token, expires.toString(), refresh, Instant.now().plusSeconds(86400).toString(), true)
    private fun activated(session: IssuedSession) = Wire.json.encodeToString(Activation("ACTIVATED", session, false))
    private companion object { val ACCOUNT = UUID.randomUUID().toString() }

    private fun repoFile(path: String): File {
        var dir: File? = File(System.getProperty("user.dir")).absoluteFile
        while (dir != null && !File(dir, path).exists()) dir = dir.parentFile
        return File(requireNotNull(dir) { "Run tests inside the repository" }, path)
    }

    @Test fun payloadIsByteExactWithPublishedFixtureAndServerAcceptsJavaSignatures() {
        val f = Wire.json.parseToJsonElement(repoFile("docs/fixtures/device-proof-v1.json").readText()).jsonObject
        val input = f["input"]!!.jsonObject
        fun s(o: kotlinx.serialization.json.JsonObject, k: String) = o[k]!!.jsonPrimitive.content
        val authKey = Wire.bytes(s(input, "authKeySpkiBase64"))
        val payload = DeviceProof.activation(Challenge(s(input, "challengeId"), s(input, "nonceBase64"), s(input, "expiresAt"), null, 6, 5),
            s(input, "phone"), LocalDevice(s(input, "deviceId"), s(input, "registrationId").toInt(), s(input, "identityKeyBase64")), authKey)
        assertEquals(s(f, "payload"), payload.decodeToString())
        assertEquals(s(f, "payloadSha256Hex"), MessageDigest.getInstance("SHA-256").digest(payload).joinToString("") { "%02x".format(it) })
        val publicKey = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(authKey))
        fun accepts(signature: ByteArray): Boolean =
            Signature.getInstance("SHA256withECDSA").run { initVerify(publicKey); update(payload); verify(signature) }
        assertTrue(accepts(Wire.bytes(s(f, "signatureDerBase64")))) // Made by OpenSSL, checked by the server's unit test too.
        val privateKey = KeyFactory.getInstance("EC").generatePrivate(PKCS8EncodedKeySpec(Wire.bytes(s(f, "testOnlyPrivateKeyPkcs8Base64"))))
        val ours = Signature.getInstance("SHA256withECDSA").run { initSign(privateKey); update(payload); sign() }
        assertEquals(0x30, ours[0].toInt()) // DER SEQUENCE, the format the server requires.
        assertTrue(accepts(ours))
    }

    @Test fun refreshPayloadBindsAccountAndTokenAndNotPhone() {
        val c = challenge(2)
        val token = UUID.randomUUID().toString()
        val text = DeviceProof.refresh(c, ACCOUNT, token, device, signer.authKey).decodeToString()
        assertTrue(text.contains("\nop:refresh\n") && text.contains("\nphone:-\n") && text.contains("\naccount:$ACCOUNT\n") && text.contains("\ntoken:$token\n"))
        assertNotEquals(text, DeviceProof.activation(c, phone, device, signer.authKey).decodeToString())
    }

    @Test fun phoneInputBuildsE164PerCountry() {
        assertEquals("+79001234567", PhoneInput.e164(Country.RU, "8 (900) 123-45-67"))
        assertEquals("+79001234567", PhoneInput.e164(Country.RU, "+7 900 123 45 67"))
        assertEquals("+77011234567", PhoneInput.e164(Country.KZ, "701 123 4567"))
        assertEquals("+375291234567", PhoneInput.e164(Country.BY, "8 029 123-45-67"))
        assertEquals("+998901234567", PhoneInput.e164(Country.UZ, "90 123 45 67"))
        for (bad in listOf("12345", "900 123 45 6a", "900123456789"))
            assertFailsWith<IllegalArgumentException> { PhoneInput.e164(Country.RU, bad) }
    }

    @Test fun lostCodeRequestIsRetriedWithSameRequestIdAndNoAuthorization() = fixture { endpoint, base ->
        val c = challenge()
        on("auth/sms/challenges", lost, json(Wire.json.encodeToString(c)))
        AuthApi.configured(endpoint, base).use { api ->
            val login = Login(api, store, signer, device)
            assertFailsWith<IOException> { login.requestCode(phone) }
            assertEquals(c, login.requestCode(phone))
        }
        val calls = sent("auth/sms/challenges")
        assertEquals(2, calls.size)
        assertEquals(body(calls[0]), body(calls[1]))
        assertTrue(calls.all { it.headers["Authorization"] == null })
        assertEquals(Wire.base64(signer.authKey), body(calls[0])["authKey"]!!.jsonPrimitive.content)
        // A deliberate resend after a challenge arrived is a new request id.
        AuthApi.configured(endpoint, base).use { Login(it, store, signer, device).requestCode(phone) }
        assertNotEquals(body(calls[0])["requestId"], body(sent("auth/sms/challenges")[2])["requestId"])
    }

    @Test fun codeThenPinActivatesWithValidProofsAndClearsPendingLogin() = fixture { endpoint, base ->
        val c = challenge()
        val session = issued()
        on("auth/sms/challenges", json(Wire.json.encodeToString(c)))
        on("auth/sms/verify", json(Wire.json.encodeToString(Activation("PIN_REQUIRED", null, false))))
        on("auth/pin/verify", problem("PIN_INVALID", 403), json(activated(session)))
        AuthApi.configured(endpoint, base).use { api ->
            val login = Login(api, store, signer, device)
            login.requestCode(phone)
            assertEquals(LoginResult.PinRequired, login.verifyCode("123456"))
            val wrong = assertFailsWith<ApiFailure> { login.verifyPin("000001") }
            assertEquals("PIN_INVALID", wrong.code)
            assertEquals(LoginResult.Activated(session), login.verifyPin("482913"))
        }
        val expected = DeviceProof.activation(c, phone, device, signer.authKey)
        for (request in sent("auth/sms/verify") + sent("auth/pin/verify")) {
            assertTrue(signer.verifies(expected, Wire.bytes(body(request)["signature"]!!.jsonPrimitive.content)))
            assertEquals(phone, body(request)["phone"]!!.jsonPrimitive.content)
        }
        assertNull(store.state.pendingLogin)
        assertEquals(session.refreshToken, store.state.session!!.refreshToken)
        assertFalse(store.state.toString().contains(session.refreshToken) || store.state.toString().contains(phone))
    }

    @Test fun concurrentCallersShareOneRefresh() = fixture { endpoint, base ->
        val fresh = issued(token = "access-fresh-" + UUID.randomUUID())
        on("auth/refresh/challenges", json(Wire.json.encodeToString(challenge(2))))
        on("auth/refresh", json(Wire.json.encodeToString(fresh)))
        val first = issued(expires = Instant.now().plusSeconds(30)) // Inside the early-refresh window.
        store.save(AuthState(session = StoredSession.of(first)))
        AuthApi.configured(endpoint, base).use { api ->
            val session = UserSession(api, store, signer, device).apply { install(first) }
            val pool = Executors.newFixedThreadPool(10)
            val start = CountDownLatch(1)
            val results = (1..10).map { pool.submit<String> { start.await(); session.current() } }
            start.countDown()
            assertEquals(setOf(fresh.accessToken), results.map { it.get(10, TimeUnit.SECONDS) }.toSet())
            pool.shutdown()
            assertEquals(1, session.refreshes)
        }
        assertEquals(1, sent("auth/refresh/challenges").size)
        assertEquals(1, sent("auth/refresh").size)
        val request = body(sent("auth/refresh").single())
        assertEquals(first.refreshToken, request["refreshToken"]!!.jsonPrimitive.content)
        assertEquals(fresh.refreshToken, store.state.session!!.refreshToken)
        assertNull(store.state.session!!.pendingRefresh)
    }

    @Test fun unauthorizedApiCallRefreshesOnceAndRetries() = fixture { endpoint, base ->
        val old = issued()
        val fresh = issued(token = "access-fresh-" + UUID.randomUUID())
        val me = RemoteMe(ACCOUNT, device.deviceId, 1, "", 4242, device.identityKey, fresh.expiresAt)
        on("me", problem("UNAUTHORIZED", 401), json(Wire.json.encodeToString(me)))
        on("auth/refresh/challenges", json(Wire.json.encodeToString(challenge(2))))
        on("auth/refresh", json(Wire.json.encodeToString(fresh)))
        store.save(AuthState(session = StoredSession.of(old)))
        AuthApi.configured(endpoint, base).use { api ->
            val session = UserSession(api, store, signer, device).apply { install(old) }
            PinnedApi.session(endpoint, session, base).use { assertEquals(me, it.me()) }
        }
        assertEquals(listOf("Bearer ${old.accessToken}", "Bearer ${fresh.accessToken}"), sent("me").map { it.headers["Authorization"] })
        assertTrue(sent("auth/refresh").all { it.headers["Authorization"] == null })
    }

    @Test fun lostRefreshResponseReplaysTheIdenticalSignedRequest() = fixture { endpoint, base ->
        val fresh = issued()
        on("auth/refresh/challenges", json(Wire.json.encodeToString(challenge(2))))
        on("auth/refresh", lost, json(Wire.json.encodeToString(fresh)))
        val old = issued(expires = Instant.now().minusSeconds(1))
        store.save(AuthState(session = StoredSession.of(old)))
        AuthApi.configured(endpoint, base).use { api ->
            val session = UserSession(api, store, signer, device).apply { install(old) }
            assertFailsWith<IOException> { session.current() }
            assertNotNull(store.state.session!!.pendingRefresh) // Survives a process restart.
            val restarted = UserSession(api, store, signer, device)
            assertEquals(fresh.accessToken, restarted.current())
        }
        assertEquals(1, sent("auth/refresh/challenges").size)
        val (a, b) = sent("auth/refresh")
        assertEquals(a.body, b.body)
    }

    @Test fun expiredChallengeGetsFreshOneButReuseEndsSessionForGood() = fixture { endpoint, base ->
        on("auth/refresh/challenges", json(Wire.json.encodeToString(challenge(2))))
        on("auth/refresh", problem("CHALLENGE_EXPIRED", 410), problem("REFRESH_REUSED", 401))
        val old = issued(expires = Instant.now().minusSeconds(1))
        store.save(AuthState(session = StoredSession.of(old)))
        AuthApi.configured(endpoint, base).use { api ->
            val session = UserSession(api, store, signer, device).apply { install(old) }
            assertEquals("REFRESH_REUSED", assertFailsWith<SessionEnded> { session.current() }.code)
            assertEquals(AuthState(ended = "REFRESH_REUSED"), store.state)
            val before = requests.size
            assertEquals("REFRESH_REUSED", assertFailsWith<SessionEnded> { session.current() }.code)
            assertEquals(before, requests.size) // No further network use after the session ended.
        }
        assertEquals(2, sent("auth/refresh/challenges").size)
    }

    @Test fun revokedAfterTransferEndsSession() = fixture { endpoint, base ->
        on("auth/refresh/challenges", problem("SESSION_REVOKED", 401))
        val old = issued(expires = Instant.now().minusSeconds(1))
        store.save(AuthState(session = StoredSession.of(old)))
        AuthApi.configured(endpoint, base).use { api ->
            // The refresh-challenge endpoint already refuses a replaced device: the session ends, nothing is retried.
            assertEquals("SESSION_REVOKED", assertFailsWith<SessionEnded> { UserSession(api, store, signer, device).current() }.code)
        }
        assertEquals(AuthState(ended = "SESSION_REVOKED"), store.state)
        assertEquals(0, sent("auth/refresh").size)
    }

    @Test fun pinChangesUseBearerAndLogoutEndsLocallyEvenOffline() = fixture { endpoint, base ->
        val current = issued()
        on("account/pin", MockResponse.Builder().code(204).build())
        on("auth/logout", lost)
        store.save(AuthState(session = StoredSession.of(current)))
        AuthApi.configured(endpoint, base).use { api ->
            val session = UserSession(api, store, signer, device).apply { install(current) }
            session.setPin("482913", null)
            session.logout()
        }
        assertEquals("PUT", sent("account/pin").single().method)
        assertEquals("Bearer ${current.accessToken}", sent("account/pin").single().headers["Authorization"])
        assertEquals("Bearer ${current.accessToken}", sent("auth/logout").single().headers["Authorization"])
        assertEquals(AuthState(ended = "LOGOUT"), store.state)
    }

    @Test fun logoutIsDurableBeforeAnyHttpRequest() = fixture { endpoint, base ->
        val current = issued()
        on("auth/logout", MockResponse.Builder().code(204).build())
        store.save(AuthState(session = StoredSession.of(current)))
        var endedCommitted = false
        val journal = object : AuthStore {
            override fun load() = store.load()
            override fun save(state: AuthState) {
                if (state.ended == "LOGOUT") {
                    assertTrue(sent("auth/logout").isEmpty(), "Local logout must precede HTTP")
                    endedCommitted = true
                }
                store.save(state)
            }
        }
        AuthApi.configured(endpoint, base).use { api ->
            UserSession(api, journal, signer, device).apply { install(current); logout() }
        }
        assertTrue(endedCommitted)
        assertEquals(1, sent("auth/logout").size)
        assertEquals("LOGOUT", store.load().ended)
    }

    @Test fun logoutAfterRestartDoesNotSpendOrRotateRefreshToken() = fixture { endpoint, base ->
        store.save(AuthState(session = StoredSession.of(issued())))
        AuthApi.configured(endpoint, base).use { api -> UserSession(api, store, signer, device).logout() }
        assertTrue(requests.isEmpty())
        assertEquals(AuthState(ended = "LOGOUT"), store.load())
    }

    @Test fun failedLocalLogoutCommitDoesNotRevokeOrPretendToClearSession() = fixture { endpoint, base ->
        val current = issued()
        store.save(AuthState(session = StoredSession.of(current)))
        val journal = object : AuthStore {
            override fun load() = store.load()
            override fun save(state: AuthState) {
                if (state.ended == "LOGOUT") throw IOException("storage unavailable")
                store.save(state)
            }
        }
        AuthApi.configured(endpoint, base).use { api ->
            val session = UserSession(api, journal, signer, device).apply { install(current) }
            assertFailsWith<IOException> { session.logout() }
            assertEquals(current.accessToken, session.current())
        }
        assertTrue(requests.isEmpty())
        assertEquals(StoredSession.of(current), store.load().session)
    }

    @Test fun authCallsKeepPinsAndRefuseRedirects() = fixture { endpoint, base ->
        val bad = endpoint.copy(pins = listOf(CertificatePinner.pin(HeldCertificate.Builder().build().certificate),
            CertificatePinner.pin(HeldCertificate.Builder().build().certificate)))
        AuthApi.configured(bad, base).use { assertFailsWith<SSLException> { Login(it, store, signer, device).requestCode(phone) } }
        assertEquals(0, requests.size)
        MockWebServer().use { other ->
            other.start()
            on("auth/sms/challenges", MockResponse.Builder().code(307).addHeader("Location", other.url("/steal")).build())
            AuthApi.configured(endpoint, base).use {
                assertEquals(307, assertFailsWith<ApiFailure> { Login(it, MemoryAuthStore(), signer, device).requestCode(phone) }.status)
            }
            assertEquals(0, other.requestCount)
        }
    }

    @Test fun problemDetailsCarryAttemptsAndRetryAfter() = fixture { endpoint, base ->
        on("auth/sms/challenges", json("""{"status":429,"code":"RESEND_TOO_EARLY","traceId":"t","retryAfterSeconds":42}""", 429))
        AuthApi.configured(endpoint, base).use { api ->
            val failure = assertFailsWith<ApiFailure> { Login(api, store, signer, device).requestCode(phone) }
            assertEquals("RESEND_TOO_EARLY", failure.code)
            assertEquals(42L, failure.retryAfterSeconds)
        }
    }

    @Test fun lostRefreshDoesNotSwitchToANewChallengeWhenRecoveryWindowExpires() = fixture { endpoint, base ->
        on("auth/refresh/challenges", json(Wire.json.encodeToString(challenge(2))))
        on("auth/refresh", lost)
        val old = issued(expires = Instant.now().minusSeconds(1))
        store.save(AuthState(session = StoredSession.of(old)))
        var now = System.currentTimeMillis()
        AuthApi.configured(endpoint, base).use { api ->
            assertFailsWith<IOException> { UserSession(api, store, signer, device) { now }.current() }
            now += 110_000
            val before = requests.size
            assertEquals("REFRESH_RECOVERY_EXPIRED", assertFailsWith<SessionEnded> {
                UserSession(api, store, signer, device) { now }.current()
            }.code)
            assertEquals(before, requests.size)
        }
        assertEquals(1, sent("auth/refresh/challenges").size)
        assertEquals(AuthState(ended = "REFRESH_RECOVERY_EXPIRED"), store.state)
    }

    @Test fun backwardsClockCannotExtendReplayWindow() = fixture { endpoint, base ->
        on("auth/refresh/challenges", json(Wire.json.encodeToString(challenge(2))))
        on("auth/refresh", lost)
        store.save(AuthState(session = StoredSession.of(issued())))
        var now = System.currentTimeMillis()
        AuthApi.configured(endpoint, base).use { api ->
            assertFailsWith<IOException> { UserSession(api, store, signer, device) { now }.current() }
            now -= 1
            val before = requests.size
            assertEquals("REFRESH_RECOVERY_EXPIRED", assertFailsWith<SessionEnded> {
                UserSession(api, store, signer, device) { now }.current()
            }.code)
            assertEquals(before, requests.size)
        }
    }

    @Test fun refreshRetryBudgetSurvivesProcessRestarts() = fixture { endpoint, base ->
        on("auth/refresh/challenges", json(Wire.json.encodeToString(challenge(2))))
        on("auth/refresh", lost)
        store.save(AuthState(session = StoredSession.of(issued())))
        AuthApi.configured(endpoint, base).use { api ->
            repeat(2) { assertFailsWith<IOException> { UserSession(api, store, signer, device).current() } }
            val before = requests.size
            assertEquals("REFRESH_RETRY_EXHAUSTED", assertFailsWith<SessionEnded> {
                UserSession(api, store, signer, device).current()
            }.code)
            assertEquals(before, requests.size)
        }
        assertEquals(1, sent("auth/refresh/challenges").size)
        val calls = sent("auth/refresh")
        assertEquals(2, calls.size); assertEquals(calls[0].body, calls[1].body)
    }

    @Test fun missingChallengeCannotSilentlyReusePossiblyConsumedToken() = fixture { endpoint, base ->
        on("auth/refresh/challenges", json(Wire.json.encodeToString(challenge(2))))
        on("auth/refresh", problem("CHALLENGE_NOT_FOUND", 404))
        store.save(AuthState(session = StoredSession.of(issued())))
        AuthApi.configured(endpoint, base).use { api ->
            assertEquals("REFRESH_OUTCOME_UNKNOWN", assertFailsWith<SessionEnded> {
                UserSession(api, store, signer, device).current()
            }.code)
        }
        assertEquals(1, sent("auth/refresh/challenges").size)
        assertEquals(1, sent("auth/refresh").size)
    }

    @Test fun malformedSuccessfulResponseRetainsExactRecoveryRequest() = fixture { endpoint, base ->
        val fresh = issued()
        on("auth/refresh/challenges", json(Wire.json.encodeToString(challenge(2))))
        on("auth/refresh", json(Wire.json.encodeToString(fresh.copy(expiresAt = "invalid-time"))),
            json(Wire.json.encodeToString(fresh)))
        store.save(AuthState(session = StoredSession.of(issued())))
        AuthApi.configured(endpoint, base).use { api ->
            assertFailsWith<java.time.format.DateTimeParseException> { UserSession(api, store, signer, device).current() }
            val pending = requireNotNull(store.state.session?.pendingRefresh)
            assertEquals(1, pending.attempts)
            assertEquals(fresh.accessToken, UserSession(api, store, signer, device).current())
        }
        val calls = sent("auth/refresh"); assertEquals(calls[0].body, calls[1].body)
        assertEquals(1, sent("auth/refresh/challenges").size)
        assertNull(store.state.session!!.pendingRefresh)
    }

    @Test fun failedCommitDoesNotExposeNewAccessOrDiscardRecoveryRequest() = fixture { endpoint, base ->
        val old = issued(); val fresh = issued()
        on("auth/refresh/challenges", json(Wire.json.encodeToString(challenge(2))))
        on("auth/refresh", json(Wire.json.encodeToString(fresh)))
        store.save(AuthState(session = StoredSession.of(old)))
        var fail = true
        val disk = object : AuthStore {
            override fun load() = store.load()
            override fun save(state: AuthState) {
                if (fail && state.session?.refreshToken == fresh.refreshToken) throw IOException("Synthetic commit failure")
                store.save(state)
            }
        }
        AuthApi.configured(endpoint, base).use { api ->
            assertFailsWith<IOException> { UserSession(api, disk, signer, device).current() }
            assertEquals(old.refreshToken, store.state.session!!.refreshToken)
            assertNotNull(store.state.session!!.pendingRefresh)
            fail = false
            assertEquals(fresh.accessToken, UserSession(api, disk, signer, device).current())
        }
        val calls = sent("auth/refresh"); assertEquals(calls[0].body, calls[1].body)
        assertEquals(1, sent("auth/refresh/challenges").size)
    }

    @Test fun refreshCannotChangeGenerationOrInstallUndurableAccess() = fixture { endpoint, base ->
        val old = issued(); val changed = issued().copy(generation = 2)
        on("auth/refresh/challenges", json(Wire.json.encodeToString(challenge(2))))
        on("auth/refresh", json(Wire.json.encodeToString(changed)))
        store.save(AuthState(session = StoredSession.of(old)))
        AuthApi.configured(endpoint, base).use { api ->
            assertFailsWith<IllegalArgumentException> { UserSession(api, store, signer, device).current() }
            assertEquals(1, store.state.session!!.generation)
            assertNotNull(store.state.session!!.pendingRefresh)
            assertFailsWith<IllegalArgumentException> { UserSession(api, store, signer, device).install(changed) }
        }
    }

    @Test fun malformedProblemFieldsAreSanitizedAndRetryAfterHeaderIsSupported() = fixture { endpoint, base ->
        on("auth/sms/challenges", MockResponse.Builder().code(429).addHeader("Retry-After", "42")
            .body("""{"code":{"secret":"do-not-reflect"},"attemptsRemaining":[],"retryAfterSeconds":{}}""").build())
        AuthApi.configured(endpoint, base).use { api ->
            val failure = assertFailsWith<ApiFailure> { Login(api, store, signer, device).requestCode(phone) }
            assertEquals("HTTP_ERROR", failure.code); assertNull(failure.attemptsRemaining)
            assertEquals(42L, failure.retryAfterSeconds); assertFalse(failure.toString().contains("do-not-reflect"))
        }
        assertFalse(SmsChallengeRequest(UUID.randomUUID().toString(), phone, device.deviceId,
            device.registrationId, device.identityKey, Wire.base64(signer.authKey)).toString().contains(phone))
    }

    @Test fun phoneFormattingCannotReinterpretForeignPrefixOrUnicodeDigits() {
        for (bad in listOf("+9001234567", "++7 9001234567", "900+1234567", "٩٠٠١٢٣٤٥٦٧", "9001234567\n")) {
            if (bad.endsWith("\n")) continue // Surrounding whitespace is deliberately trimmed.
            assertFailsWith<IllegalArgumentException> { PhoneInput.e164(Country.RU, bad) }
        }
        assertFailsWith<IllegalArgumentException> { PhoneInput.e164(Country.UZ, "+901234567") }
    }

    @Test fun kotlinProofUsesTheSameStrictBuilderAsAndroid() {
        val c = challenge()
        assertFailsWith<IllegalArgumentException> { DeviceProof.activation(c.copy(nonce = Wire.base64(ByteArray(31))), phone, device, signer.authKey) }
        assertFailsWith<IllegalArgumentException> { DeviceProof.activation(c, phone + "\nop:refresh", device, signer.authKey) }
        assertFailsWith<IllegalArgumentException> { DeviceProof.activation(c, phone, device, ByteArray(257)) }
    }

}
