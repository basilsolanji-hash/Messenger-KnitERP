package ru.kniterp.messenger.storage

import androidx.test.ext.junit.runners.AndroidJUnit4
import java.security.KeyFactory
import java.security.KeyStore
import java.security.Signature
import java.security.interfaces.ECPublicKey
import java.security.spec.X509EncodedKeySpec
import java.time.Instant
import java.util.UUID
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import ru.kniterp.messenger.network.DeviceProofPayload

@RunWith(AndroidJUnit4::class)
class DeviceAuthKeyTest {
    @Test fun keystoreKeySurvivesReopenAndSignsServerFormatWithoutExportingPrivateKey() {
        val deviceId = UUID.randomUUID()
        val alias = "kniterp.messenger.auth.v1.$deviceId"
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        try {
            val key = DeviceAuthKey.create(deviceId.toString())
            val publicBytes = key.publicKeySpki()
            assertArrayEquals(publicBytes, DeviceAuthKey.open(deviceId.toString()).publicKeySpki())
            assertNull(store.getKey(alias, null).encoded)
            val publicKey = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(publicBytes)) as ECPublicKey
            assertEquals(256, publicKey.params.curve.field.fieldSize)
            val payload = DeviceProofPayload.activation(UUID.randomUUID(), ByteArray(32) { it.toByte() },
                Instant.parse("2026-10-22T10:20:00Z"), "+79001234567", deviceId, 4242,
                byteArrayOf(5) + ByteArray(32) { 17 }, publicBytes)
            val signature = DeviceAuthKey.open(deviceId.toString()).sign(payload)
            assertTrue(signature.size in 1..80)
            fun verifies(bytes: ByteArray) = Signature.getInstance("SHA256withECDSA").run {
                initVerify(publicKey); update(bytes); verify(signature)
            }
            assertTrue(verifies(payload))
            assertFalse(verifies(payload + byteArrayOf(0)))
            try { DeviceAuthKey.create(deviceId.toString()); fail("Must not overwrite an existing auth key") }
            catch (_: IllegalStateException) { }
            store.deleteEntry(alias)
            try { DeviceAuthKey.open(deviceId.toString()); fail("Missing auth key must fail closed") }
            catch (_: IllegalStateException) { }
        } finally { store.deleteEntry(alias) }
    }
}
