package ru.kniterp.messenger.network

import java.net.Proxy
import java.time.Instant
import java.util.UUID
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLException
import kotlin.test.*
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import okhttp3.CertificatePinner
import okhttp3.OkHttpClient
import okhttp3.tls.HandshakeCertificates
import okhttp3.tls.HeldCertificate
import ru.kniterp.messenger.core.*

class PinnedApiTest {
    private val token = "synthetic-token-never-log-this-0123456789"
    private fun fixture(block: (MockWebServer, TestConnection, OkHttpClient) -> Unit) {
        val certificate = HeldCertificate.Builder().addSubjectAlternativeName("localhost").addSubjectAlternativeName("127.0.0.1").build()
        val backup = HeldCertificate.Builder().build()
        val serverTls = HandshakeCertificates.Builder().heldCertificate(certificate).build()
        val trust = HandshakeCertificates.Builder().addTrustedCertificate(certificate.certificate).build()
        MockWebServer().use { server ->
            server.useHttps(serverTls.sslSocketFactory()); server.start()
            val config = TestConnection(ServerEndpoint(server.url("/").newBuilder().host("127.0.0.1").build().toString(),
                listOf(CertificatePinner.pin(certificate.certificate), CertificatePinner.pin(backup.certificate))), token)
            val base = OkHttpClient.Builder().sslSocketFactory(trust.sslSocketFactory(), trust.trustManager).proxy(Proxy.NO_PROXY).build()
            try { block(server, config, base) }
            finally { base.connectionPool.evictAll(); base.dispatcher.executorService.shutdown() }
        }
    }
    private fun identity() = RemoteMe(UUID.randomUUID().toString(), UUID.randomUUID().toString(), 1,
        "Synthetic", 7, "public", Instant.now().plusSeconds(900).toString())
    @Test fun matchingPinSendsOnlyHeaderAndParsesIdentity() = fixture { server, config, base ->
        val me = identity(); server.enqueue(MockResponse.Builder().code(200).body(Wire.json.encodeToString(me)).build())
        PinnedApi.configured(config, null, base) { 0 }.use { assertEquals(me, it.me()) }
        val request = requireNotNull(server.takeRequest(1, TimeUnit.SECONDS))
        assertEquals("Bearer $token", request.headers["Authorization"])
        assertEquals("/api/v1/me", request.url.encodedPath)
        assertFalse(request.url.toString().contains(token))
        assertFalse(config.toString().contains(token))
    }
    @Test fun wrongPinFailsBeforeAuthorizationCanBeSent() = fixture { server, config, base ->
        val bad = config.copy(server = config.server.copy(pins = listOf(
            CertificatePinner.pin(HeldCertificate.Builder().build().certificate),
            CertificatePinner.pin(HeldCertificate.Builder().build().certificate))))
        PinnedApi.configured(bad, null, base) { 0 }.use { assertFailsWith<SSLException> { it.me() } }
        assertEquals(0, server.requestCount)
    }
    @Test fun pinDoesNotBypassPlatformCertificateTrust() = fixture { server, config, _ ->
        PinnedApi.connect(config).use { assertFailsWith<SSLException> { it.me() } }
        assertEquals(0, server.requestCount)
    }
    /** The instrumentation trust seam replaces roots only: pins and hostname verification still decide. */
    @Test fun testTrustAnchorKeepsPinsAndHostnameChecks() {
        val root = HeldCertificate.Builder().certificateAuthority(0).build()
        val trust = HandshakeCertificates.Builder().addTrustedCertificate(root.certificate).build().trustManager
        val tokens = object : AccessTokens { override fun current() = token; override fun rejected(token: String) = false }
        val backup = CertificatePinner.pin(HeldCertificate.Builder().build().certificate)
        val ip = HeldCertificate.Builder().addSubjectAlternativeName("127.0.0.1").signedBy(root).build()
        val named = HeldCertificate.Builder().addSubjectAlternativeName("localhost").signedBy(root).build()
        MockWebServer().use { good -> MockWebServer().use { misnamed ->
            good.useHttps(HandshakeCertificates.Builder().heldCertificate(ip, root.certificate).build().sslSocketFactory())
            misnamed.useHttps(HandshakeCertificates.Builder().heldCertificate(named, root.certificate).build().sslSocketFactory())
            good.start(java.net.InetAddress.getByName("127.0.0.1"), 0)
            misnamed.start(java.net.InetAddress.getByName("127.0.0.1"), 0)
            fun origin(server: MockWebServer) = "https://127.0.0.1:${server.port}/"
            val pinned = ServerEndpoint(origin(good), listOf(CertificatePinner.pin(ip.certificate), backup))
            val me = identity(); good.enqueue(MockResponse.Builder().code(200).body(Wire.json.encodeToString(me)).build())
            PinnedApi.session(pinned, tokens, trust).use { assertEquals(me, it.me()) }
            val unpinned = pinned.copy(pins = listOf(CertificatePinner.pin(HeldCertificate.Builder().build().certificate), backup))
            PinnedApi.session(unpinned, tokens, trust).use { assertFailsWith<SSLException> { it.me() } }
            AuthApi.connect(unpinned, trust).use { assertFailsWith<SSLException> { it.refreshChallenge(UUID.randomUUID().toString()) } }
            PinnedApi.session(pinned, tokens).use { assertFailsWith<SSLException> { it.me() } } // No seam: platform roots.
            // Pinned and CA-signed, but issued for another name: hostname verification still refuses it.
            val wrongName = ServerEndpoint(origin(misnamed), listOf(CertificatePinner.pin(named.certificate), backup))
            PinnedApi.session(wrongName, tokens, trust).use { assertFailsWith<SSLException> { it.me() } }
            assertEquals(1, good.requestCount)
            assertEquals(0, misnamed.requestCount)
        } }
    }
    @Test fun redirectsCannotSendTokenToAnotherOrigin() = fixture { server, config, base ->
        MockWebServer().use { destination ->
            destination.start()
            server.enqueue(MockResponse.Builder().code(302).addHeader("Location", destination.url("/stolen")).build())
            PinnedApi.configured(config, null, base) { 0 }.use { assertEquals(302, assertFailsWith<ApiFailure> { it.me() }.status) }
            assertEquals(0, destination.requestCount)
        }
    }
    @Test fun oversizedResponseIsBoundedBeforeJsonParsing() = fixture { server, config, base ->
        server.enqueue(MockResponse.Builder().code(200).body(" ".repeat(PinnedApi.MAX_RESPONSE + 1)).build())
        PinnedApi.configured(config, null, base) { 0 }.use {
            assertEquals("API response too large", assertFailsWith<IllegalArgumentException> { it.inbox(null) }.message)
        }
    }
    @Test fun expiredSessionDoesNotMakeARequest() = fixture { server, config, base ->
        PinnedApi.configured(config, 1, base) { 2 }.use { assertFailsWith<ExpiredSession> { it.me() } }
        assertEquals(0, server.requestCount)
    }
    @Test fun endpointsRequireOriginOnlyAndCurrentPlusBackupPins() {
        val pins = listOf(Wire.base64(ByteArray(32)), Wire.base64(ByteArray(32) { 1 })).map { "sha256/$it" }
        for (url in listOf("http://example.com/", "https://user:pass@example.com/", "https://example.com/path", "https://example.com/?q=1", "https://example.com/#x"))
            assertFailsWith<IllegalArgumentException> { ServerEndpoint(url, pins).url() }
        assertFailsWith<IllegalArgumentException> { ServerEndpoint("https://example.com/", listOf(pins[0], pins[0])).url() }
    }
}
