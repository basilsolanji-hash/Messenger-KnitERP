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
