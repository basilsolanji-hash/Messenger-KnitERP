package ru.kniterp.messenger.network

import java.time.Instant
import java.util.UUID
import kotlin.test.*
import ru.kniterp.messenger.core.*

class EnrollmentTest {
    private fun config() = TestConnection(ServerEndpoint("https://example.com/",
        listOf("sha256/" + Wire.base64(ByteArray(32)), "sha256/" + Wire.base64(ByteArray(32) { 1 }))), "test-token-012345678901234567890123456789")
    @Test fun requestSurvivesRestartAndOnlyMatchingDeviceKeysCanBind() {
        val vault = MemoryVault(); val e = Enrollment(vault).apply { initialize() }
        val request = e.request("Synthetic")
        assertEquals(request, Enrollment(vault).request("Synthetic"))
        val me = RemoteMe(UUID.randomUUID().toString(), request.deviceId, 1, request.displayName,
            request.registrationId, request.identityKey, Instant.now().plusSeconds(899).toString())
        assertFailsWith<IllegalArgumentException> { e.accept(config(), me.copy(deviceId = UUID.randomUUID().toString())) }
        assertFailsWith<IllegalArgumentException> { e.accept(config(), me.copy(identityKey = Wire.base64(ByteArray(33)))) }
        assertNull(e.signal.deviceOrNull()); assertNull(e.saved())
        val saved = e.accept(config(), me)
        assertEquals(saved, Enrollment(vault).saved())
        assertEquals(me.device(), e.signal.localDevice())
        assertFalse(saved.toString().contains(config().accessToken))
    }
    @Test fun serverChangeIsRejectedBeforeAnyRequestAndBindingIsImmutable() {
        val e = Enrollment(MemoryVault()).apply { initialize() }; val r = e.request("Synthetic")
        val me = RemoteMe(UUID.randomUUID().toString(), r.deviceId, 1, r.displayName, r.registrationId,
            r.identityKey, Instant.now().plusSeconds(899).toString())
        e.accept(config(), me)
        assertFailsWith<IllegalArgumentException> { e.validateTarget(config().copy(server = config().server.copy(origin = "https://other.example/"))) }
        assertFailsWith<IllegalArgumentException> { e.accept(config(), me.copy(accountId = UUID.randomUUID().toString())) }
        assertEquals(me.device(), e.signal.localDevice())
        assertEquals(config(), e.saved()?.connection)
    }
}
