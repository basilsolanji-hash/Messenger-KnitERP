package ru.kniterp.messenger.network

import java.io.IOException
import java.net.HttpURLConnection
import java.net.Proxy
import java.net.URI
import kotlinx.serialization.Serializable
import kotlin.test.*
import okhttp3.OkHttpClient
import okhttp3.HttpUrl.Companion.toHttpUrl
import ru.kniterp.messenger.core.*

/** Real API and PostgreSQL; only this test constructor permits a loopback HTTP origin. */
class ServerSmokeTest {
    @Test fun typedApiAndPersistentDeliveryExchangeThroughServer() {
        val base = URI(requireNotNull(System.getenv("MESSENGER_TEST_URL")))
        require(base.scheme == "http" && base.host == "127.0.0.1" && base.port > 0 && base.rawUserInfo == null)
        val secret = requireNotNull(System.getenv("DEV_PROVISIONING_KEY"))
        val transport = OkHttpClient.Builder().proxy(Proxy.NO_PROXY).followRedirects(false).retryOnConnectionFailure(false).build()
        fun session(path: String, request: EnrollmentRequest, status: Int = 200): TestSession? {
            val connection = base.resolve(path).toURL().openConnection(Proxy.NO_PROXY) as HttpURLConnection
            return try {
                connection.instanceFollowRedirects = false; connection.connectTimeout = 10000; connection.readTimeout = 30000
                connection.requestMethod = "POST"; connection.doOutput = true
                connection.setRequestProperty("X-Dev-Provisioning-Token", secret)
                connection.setRequestProperty("Content-Type", "application/json")
                connection.outputStream.use { it.write(Wire.json.encodeToString(request).toByteArray()) }
                assertEquals(status, connection.responseCode)
                if (status == 200) connection.inputStream.use { input ->
                    val data = input.readNBytes(1048577); require(data.size <= 1048576)
                    Wire.json.decodeFromString<TestSession>(data.decodeToString())
                } else null
            } finally { connection.disconnect() }
        }
        fun api(token: String) = PinnedApi(base.toString().toHttpUrl(), token, transport, null, System::currentTimeMillis)
        fun phone(): Phone {
            val vault = MemoryVault()
            val enrollment = Enrollment(vault).apply { initialize() }
            val request = enrollment.request("Synthetic HTTP integration")
            val issued = requireNotNull(session("/api/v1/dev/accounts", request))
            val api = api(issued.accessToken)
            val me = api.me()
            assertEquals(request.identityKey, me.identityKey); assertEquals(request.registrationId, me.registrationId)
            assertEquals(issued.accountId, me.accountId); assertEquals(request.deviceId, me.deviceId)
            assertEquals(issued.expiresAt, me.expiresAt)
            enrollment.signal.bind(me.device()); api.publish(enrollment.signal.publicBundle())
            return Phone(vault, enrollment, request, issued, api)
        }
        try {
            val alice = phone(); val bob = phone()
            val sender = Delivery(alice.vault, alice.api)
            val privateText = "HTTP E2EE: беларускі, қазақша, o‘zbekcha, русский 🧶"
            val message = sender.queue(bob.session.accountId, privateText)
            assertFalse(Wire.json.encodeToString(message).contains(privateText))
            val accepted = alice.api.send(message)
            assertEquals(accepted, alice.api.send(message))
            // Simulate a lost acceptance: coordinator still has the immutable packet in its outbox.
            assertEquals(1, alice.enrollment.signal.pending().size)
            assertEquals(1, sender.synchronize().accepted)
            assertTrue(alice.enrollment.signal.pending().isEmpty())
            var loseAck = true
            val lossy = object : MessageApi by bob.api {
                override fun ack(request: AckRequest) {
                    if (loseAck) { loseAck = false; throw IOException("Synthetic lost ACK") }
                    bob.api.ack(request)
                }
            }
            assertFailsWith<IOException> { Delivery(bob.vault, lossy).synchronize() }
            assertEquals(privateText, bob.enrollment.signal.history().single().text)
            assertEquals(1, bob.api.inbox(null).items.size)
            // Restart the controller; re-delivery is deduplicated from the committed Signal/Room state.
            val receiver = Delivery(bob.vault, bob.api)
            receiver.synchronize()
            assertEquals(1, bob.enrollment.signal.history().size)
            assertTrue(bob.api.inbox(null).items.isEmpty())
            receiver.queue(alice.session.accountId, "Принято 🔒")
            receiver.synchronize(); sender.synchronize()
            assertEquals("Принято 🔒", alice.enrollment.signal.history().single { !it.outgoing }.text)
            assertEquals(alice.enrollment.signal.safetyNumber(bob.session.accountId),
                bob.enrollment.signal.safetyNumber(alice.session.accountId))
            val renewed = requireNotNull(session("/api/v1/dev/accounts/${alice.session.accountId}/session", alice.request))
            assertEquals(alice.session.deviceId, renewed.deviceId)
            assertEquals(alice.request.identityKey, api(renewed.accessToken).me().identityKey)
            session("/api/v1/dev/accounts/${alice.session.accountId}/session",
                alice.request.copy(identityKey = bob.request.identityKey), 409)
        } finally {
            transport.dispatcher.cancelAll(); transport.connectionPool.evictAll(); transport.dispatcher.executorService.shutdown()
        }
    }
    private data class Phone(val vault: MemoryVault, val enrollment: Enrollment, val request: EnrollmentRequest,
        val session: TestSession, val api: PinnedApi)
}
@Serializable private data class TestSession(val accountId: String, val deviceId: String, val generation: Int,
    val accessToken: String, val expiresAt: String) {
    override fun toString() = "TestSession(<redacted>)"
}
