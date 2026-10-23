package ru.kniterp.messenger.storage

import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.security.keystore.StrongBoxUnavailableException
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.PrivateKey
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import ru.kniterp.messenger.core.Wire
import ru.kniterp.messenger.network.DeviceSigner

/** Non-exportable P-256 proof-of-possession key, one per activation (deviceId). Not the Signal identity key. */
internal class KeystoreSigner private constructor(private val alias: String) : DeviceSigner {
    companion object {
        private fun keyStore() = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        private fun alias(deviceId: String) = "kniterp.messenger.device-auth.v1." + Wire.id(deviceId)

        fun create(deviceId: String): KeystoreSigner {
            val alias = alias(deviceId)
            check(!keyStore().containsAlias(alias)) { "Device key already exists" }
            fun generate(strongBox: Boolean) = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore").apply {
                initialize(KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_SIGN)
                    .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
                    .setDigests(KeyProperties.DIGEST_SHA256)
                    .apply { if (strongBox) setIsStrongBoxBacked(true) }
                    .build())
            }.generateKeyPair()
            // StrongBox when the phone has it; its absence is not a login error.
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                try { generate(strongBox = true) } catch (_: StrongBoxUnavailableException) { generate(strongBox = false) }
            } else generate(strongBox = false)
            return KeystoreSigner(alias)
        }

        /** Fails closed if the key is gone: the activation cannot be proven any more and needs a fresh login. */
        fun open(deviceId: String): KeystoreSigner = alias(deviceId).also { check(keyStore().containsAlias(it)) { "Device key is unavailable" } }
            .let(::KeystoreSigner)
    }

    override val authKey: ByteArray get() = keyStore().getCertificate(alias).publicKey.encoded

    override fun sign(payload: ByteArray): ByteArray = Signature.getInstance("SHA256withECDSA").run {
        initSign(keyStore().getKey(alias, null) as PrivateKey)
        update(payload)
        sign()
    }
}
