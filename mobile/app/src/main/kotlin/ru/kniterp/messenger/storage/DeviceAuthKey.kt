package ru.kniterp.messenger.storage

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.PrivateKey
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import ru.kniterp.messenger.core.Wire
import ru.kniterp.messenger.network.DeviceSigner

/** A separate non-exportable P-256 key for one activation, independent of Signal.
 * Missing or invalidated keys fail closed. Opening an existing activation never replaces its key.
 */
internal class DeviceAuthKey private constructor(private val alias: String) : DeviceSigner {
    companion object {
        private fun alias(deviceId: String) = "kniterp.messenger.auth.v1.${Wire.id(deviceId)}"
        private fun store() = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }

        @Synchronized fun create(deviceId: String): DeviceAuthKey {
            val alias = alias(deviceId)
            check(!store().containsAlias(alias)) { "Device auth key already exists" }
            val spec = KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_SIGN)
                .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
                .setDigests(KeyProperties.DIGEST_SHA256)
                .build()
            KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore").apply {
                initialize(spec)
            }.generateKeyPair()
            return DeviceAuthKey(alias)
        }

        fun open(deviceId: String): DeviceAuthKey {
            val alias = alias(deviceId)
            check(store().containsAlias(alias)) { "Device auth key is unavailable" }
            return DeviceAuthKey(alias)
        }
    }

    fun publicKeySpki(): ByteArray = checkNotNull(store().getCertificate(alias)) {
        "Device auth public key is unavailable"
    }.publicKey.encoded.copyOf()

    override val authKey: ByteArray get() = publicKeySpki()

    override fun sign(payload: ByteArray): ByteArray {
        require(payload.isNotEmpty() && payload.size <= 2048)
        val key = store().getKey(alias, null) as? PrivateKey
        checkNotNull(key) { "Device auth private key is unavailable" }
        return Signature.getInstance("SHA256withECDSA").run {
            initSign(key)
            update(payload)
            sign()
        } // Android returns ASN.1 DER, as required by the server.
    }
}
