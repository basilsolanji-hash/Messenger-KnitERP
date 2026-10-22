package ru.kniterp.messenger.storage

import androidx.room.Room
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import net.zetetic.database.sqlcipher.SupportOpenHelperFactory
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import ru.kniterp.messenger.core.*
import java.security.SecureRandom
import java.util.UUID

@RunWith(AndroidJUnit4::class)
class EncryptedVaultTest {
    @Test fun encryptedRoomRollsBackAndSurvivesReopen() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val firstKey = DatabaseKey.loadOrCreate(context)
        val reopenedKey = DatabaseKey.loadOrCreate(context)
        try {
            assertEquals(32, firstKey.size)
            assertTrue(java.security.MessageDigest.isEqual(firstKey, reopenedKey))
            assertEquals(60L, java.io.File(context.noBackupFilesDir, "database-key-v1.bin").length())
        } finally { firstKey.fill(0); reopenedKey.fill(0) }
        val name = "instrumentation-${UUID.randomUUID()}.db"
        val password = ByteArray(32).also(SecureRandom()::nextBytes)
        System.loadLibrary("sqlcipher")
        fun open() = Room.databaseBuilder(context, VaultDatabase::class.java, name)
            .openHelperFactory(SupportOpenHelperFactory(password)).build()
        var db = open()
        try {
            val a = SignalClient(RoomVault(db, "a")); val b = SignalClient(RoomVault(db, "b"))
            a.initialize(2); b.initialize(2)
            a.bind(Device(UUID.randomUUID().toString(), UUID.randomUUID().toString(), 1))
            b.bind(Device(UUID.randomUUID().toString(), UUID.randomUUID().toString(), 1))
            val d = b.localDevice(); val p = b.publicBundle()
            a.establish(d.accountId, ClaimedBundle(d.accountId, d.deviceId, d.generation, 1,
                b.registrationId(), b.identityPublic(), p.signedPreKeyId, p.signedPreKey, p.signature, p.preKeys.first(), p.pqPreKeys.first()))
            val failCommit = object : Vault {
                override fun <T> transaction(block: (Records) -> T): T = RoomVault(db, "a").transaction { records ->
                    block(records)
                    throw IllegalStateException("Injected failure before SQL COMMIT")
                }
            }
            try { SignalClient(failCommit).prepare(d.accountId, "ROLLBACK"); fail("Expected rollback") }
            catch (_: IllegalStateException) { }
            assertTrue(a.pending().isEmpty())
            assertTrue(a.history().isEmpty())
            val packet = a.prepare(d.accountId, "ROOM_SQLCIPHER_PRIVATE_MARKER")
            val sender = a.localDevice()
            val incoming = Incoming(1, packet.clientMessageId, sender.accountId, sender.deviceId, 1,
                packet.messageType, packet.ciphertext, "local-test", "local-test")
            assertEquals("ROOM_SQLCIPHER_PRIVATE_MARKER", b.receive(incoming).text)
            db.close()
            val bytes = context.getDatabasePath(name).readBytes()
            assertFalse(bytes.take(16).toByteArray().contentEquals("SQLite format 3\u0000".toByteArray()))
            assertFalse(bytes.toString(Charsets.ISO_8859_1).contains("ROOM_SQLCIPHER_PRIVATE_MARKER"))
            db = open()
            val restarted = SignalClient(RoomVault(db, "b"))
            assertEquals(1, restarted.history().size)
            assertEquals("ROOM_SQLCIPHER_PRIVATE_MARKER", restarted.receive(incoming).text)
            assertEquals(1, restarted.history().size)
        } finally { db.close(); context.deleteDatabase(name); password.fill(0) }
    }
}
