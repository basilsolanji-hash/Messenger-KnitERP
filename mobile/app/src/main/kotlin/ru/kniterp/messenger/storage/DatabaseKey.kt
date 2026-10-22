package ru.kniterp.messenger.storage

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.AtomicFile
import java.io.File
import java.security.KeyStore
import java.security.SecureRandom
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** Random SQLCipher passphrase wrapped by a non-exportable Keystore AES key.
 * Lost/invalidated key fails closed; there is no plaintext fallback or automatic database reset.
 */
internal object DatabaseKey {
    private const val ALIAS = "kniterp.messenger.database.v1"
    @Synchronized fun loadOrCreate(context: Context): ByteArray {
        val wrapped = AtomicFile(File(context.noBackupFilesDir, "database-key-v1.bin"))
        val keyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        val exists = wrapped.baseFile.exists() || File(wrapped.baseFile.path + ".bak").exists()
        val key = if (keyStore.containsAlias(ALIAS)) keyStore.getKey(ALIAS, null) as SecretKey else {
            check(!exists && !context.getDatabasePath(VaultDatabase.NAME).exists()) { "Database encryption key is unavailable" }
            KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").apply {
                init(KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .setKeySize(256).setRandomizedEncryptionRequired(true).build())
            }.generateKey()
        }
        val aad = "${context.packageName}:${VaultDatabase.NAME}:v1".toByteArray(Charsets.UTF_8)
        if (exists) {
            val blob = wrapped.readFully()
            check(blob.size == 60) { "Invalid wrapped database key" }
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, key, GCMParameterSpec(128, blob.copyOfRange(0, 12)))
            cipher.updateAAD(aad)
            return cipher.doFinal(blob, 12, blob.size - 12).also { check(it.size == 32) }
        }
        check(!context.getDatabasePath(VaultDatabase.NAME).exists()) { "Wrapped database key is missing" }
        val passphrase = ByteArray(32).also(SecureRandom()::nextBytes)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, key)
        cipher.updateAAD(aad)
        val blob = cipher.iv + cipher.doFinal(passphrase)
        val stream = wrapped.startWrite()
        try { stream.write(blob); wrapped.finishWrite(stream) }
        catch (failure: Throwable) { wrapped.failWrite(stream); passphrase.fill(0); throw failure }
        return passphrase
    }
}
