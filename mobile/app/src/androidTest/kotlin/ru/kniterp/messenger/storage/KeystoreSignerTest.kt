package ru.kniterp.messenger.storage

import androidx.test.ext.junit.runners.AndroidJUnit4
import java.security.KeyFactory
import java.security.KeyStore
import java.security.PrivateKey
import java.security.Signature
import java.security.spec.X509EncodedKeySpec
import java.time.Instant
import java.util.UUID
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import ru.kniterp.messenger.core.Wire
import ru.kniterp.messenger.network.Challenge
import ru.kniterp.messenger.network.DeviceProof
import ru.kniterp.messenger.network.LocalDevice

@RunWith(AndroidJUnit4::class)
class KeystoreSignerTest {
    @Test fun keystoreKeySignsDerVerifiableByItsPublishedSpkiAndIsNotExportable() {
        val deviceId = UUID.randomUUID().toString()
        val signer = KeystoreSigner.create(deviceId)
        val device = LocalDevice(deviceId, 4242, Wire.base64(ByteArray(33) { if (it == 0) 5 else 17 }))
        val challenge = Challenge(UUID.randomUUID().toString(), Wire.base64(ByteArray(32)), Instant.now().plusSeconds(300).toString(), null, 6, 5)
        val payload = DeviceProof.activation(challenge, "+79001234567", device, signer.authKey)
        val signature = signer.sign(payload)
        assertEquals(0x30, signature[0].toInt()) // DER, the format the server checks.
        val publicKey = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(signer.authKey))
        assertTrue(Signature.getInstance("SHA256withECDSA").run { initVerify(publicKey); update(payload); verify(signature) })
        val private = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
            .getKey("kniterp.messenger.device-auth.v1.$deviceId", null) as PrivateKey
        assertNull(private.encoded) // Keystore keys never expose their private bytes.
        assertArrayEquals(signer.authKey, KeystoreSigner.open(deviceId).authKey)
        assertThrows(IllegalStateException::class.java) { KeystoreSigner.create(deviceId) }
        assertThrows(IllegalStateException::class.java) { KeystoreSigner.open(UUID.randomUUID().toString()) }
    }
}
