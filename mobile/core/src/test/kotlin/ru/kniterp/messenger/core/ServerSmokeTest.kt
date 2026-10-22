package ru.kniterp.messenger.core

import java.net.HttpURLConnection
import java.net.URI
import java.util.UUID
import kotlinx.serialization.Serializable
import kotlin.test.*

/** Actual HTTP -> ASP.NET -> PostgreSQL -> recipient -> ACK contract test.
 * Synthetic clients only; provision key and tokens never appear in test logs or sources.
 */
class ServerSmokeTest {
    @Test fun encryptedOfflineExchangeThroughServer() {
        val base = URI(requireNotNull(System.getenv("MESSENGER_TEST_URL")))
        require(base.scheme == "http" && base.host == "127.0.0.1" && base.port > 0 && base.rawUserInfo == null)
        val provisioning = requireNotNull(System.getenv("DEV_PROVISIONING_KEY"))
        fun provision(): Pair<SignalClient, DevSession> {
            val client = SignalClient(TestVault()).apply { initialize(4) }
            val request = DevProvision("Synthetic integration client", UUID.randomUUID().toString(), client.registrationId(), client.identityPublic())
            val response = call(base, "/api/v1/dev/accounts", Wire.json.encodeToString(request), provisioning = provisioning)
            val session = Wire.json.decodeFromString<DevSession>(response)
            client.bind(Device(session.accountId, session.deviceId, session.generation))
            call(base, "/api/v1/keys/bundles", Wire.json.encodeToString(client.publicBundle()), session.accessToken)
            return client to session
        }
        val (alice, aliceSession) = provision(); val (bob, bobSession) = provision()
        val claimId = UUID.randomUUID().toString()
        val body = Wire.json.encodeToString(DevClaim(bobSession.accountId, claimId))
        val claimedJson = call(base, "/api/v1/keys/claim", body, aliceSession.accessToken)
        assertEquals(claimedJson, call(base, "/api/v1/keys/claim", body, aliceSession.accessToken))
        alice.establish(bobSession.accountId, Wire.json.decodeFromString<ClaimedBundle>(claimedJson))
        val privateText = "Тест E2EE: беларускі, қазақша, o‘zbekcha, русский 🧶"
        val envelope = alice.prepare(bobSession.accountId, privateText)
        val wireJson = Wire.json.encodeToString(envelope)
        assertFalse(wireJson.contains(privateText))
        val acceptance = call(base, "/api/v1/messages", wireJson, aliceSession.accessToken)
        assertEquals(acceptance, call(base, "/api/v1/messages", wireJson, aliceSession.accessToken))
        alice.accepted(envelope.clientMessageId)
        // Recipient was offline during send. A later inbox read must still contain ciphertext.
        val page = Wire.json.decodeFromString<InboxPage>(call(base, "/api/v1/inbox", token = bobSession.accessToken))
        assertEquals(1, page.items.size)
        val item = page.items.single()
        assertEquals(envelope.ciphertext, item.ciphertext)
        assertEquals(privateText, bob.receive(item).text)
        // Lost ACK: same server envelope decrypts idempotently without advancing twice.
        assertEquals(privateText, bob.receive(item).text)
        assertEquals(1, bob.history().size)
        call(base, "/api/v1/inbox/ack", Wire.json.encodeToString(DevAck(listOf(item.sequence))), bobSession.accessToken)
        val afterAck = Wire.json.decodeFromString<InboxPage>(call(base, "/api/v1/inbox", token = bobSession.accessToken))
        assertTrue(afterAck.items.isEmpty())
        val reply = bob.prepare(aliceSession.accountId, "Принято 🔒")
        call(base, "/api/v1/messages", Wire.json.encodeToString(reply), bobSession.accessToken)
        val incoming = Wire.json.decodeFromString<InboxPage>(call(base, "/api/v1/inbox", token = aliceSession.accessToken)).items.single()
        assertEquals("Принято 🔒", alice.receive(incoming).text)
        assertEquals(alice.safetyNumber(bobSession.accountId), bob.safetyNumber(aliceSession.accountId))
        call(base, "/api/v1/inbox/ack", Wire.json.encodeToString(DevAck(listOf(incoming.sequence))), aliceSession.accessToken)
    }

    private fun call(base: URI, path: String, body: String? = null, token: String? = null, provisioning: String? = null): String {
        val c = base.resolve(path).toURL().openConnection() as HttpURLConnection
        try {
            c.connectTimeout = 10_000; c.readTimeout = 30_000; c.instanceFollowRedirects = false
            c.requestMethod = if (body == null) "GET" else "POST"
            c.setRequestProperty("Accept", "application/json")
            if (token != null) c.setRequestProperty("Authorization", "Bearer $token")
            if (provisioning != null) c.setRequestProperty("X-Dev-Provisioning-Token", provisioning)
            if (body != null) {
                c.doOutput = true; c.setRequestProperty("Content-Type", "application/json")
                c.outputStream.use { it.write(body.toByteArray(Charsets.UTF_8)) }
            }
            check(c.responseCode in 200..299) { "Development API returned HTTP ${c.responseCode}" }
            return c.inputStream.use { input ->
                val bytes = input.readNBytes(1_048_577)
                require(bytes.size <= 1_048_576)
                bytes.toString(Charsets.UTF_8)
            }
        } finally { c.disconnect() }
    }
}
@Serializable private data class DevProvision(val displayName: String, val deviceId: String, val registrationId: Int, val identityKey: String)
@Serializable private data class DevSession(val accountId: String, val deviceId: String, val generation: Int, val accessToken: String, val expiresAt: String) {
    override fun toString() = "DevSession(<redacted>)"
}
@Serializable private data class DevClaim(val accountId: String, val claimId: String)
@Serializable private data class DevAck(val sequences: List<Long>)
