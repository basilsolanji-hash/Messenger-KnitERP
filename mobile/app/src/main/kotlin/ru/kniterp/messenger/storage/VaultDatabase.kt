package ru.kniterp.messenger.storage

import android.content.Context
import androidx.room.*
import java.util.concurrent.Callable
import net.zetetic.database.sqlcipher.SupportOpenHelperFactory
import ru.kniterp.messenger.core.Records
import ru.kniterp.messenger.core.Vault

@Entity(tableName = "vault", primaryKeys = ["profile", "bucket", "recordKey"])
data class VaultRow(val profile: String, val bucket: String, val recordKey: String, val value: ByteArray)

@Dao
interface VaultDao {
    @Query("SELECT value FROM vault WHERE profile=:profile AND bucket=:bucket AND recordKey=:key")
    fun get(profile: String, bucket: String, key: String): ByteArray?
    @Insert(onConflict = OnConflictStrategy.REPLACE) fun put(row: VaultRow)
    @Query("DELETE FROM vault WHERE profile=:profile AND bucket=:bucket AND recordKey=:key")
    fun delete(profile: String, bucket: String, key: String)
    @Query("SELECT recordKey FROM vault WHERE profile=:profile AND bucket=:bucket ORDER BY recordKey")
    fun keys(profile: String, bucket: String): List<String>
}

@Database(entities = [VaultRow::class], version = 1, exportSchema = true)
abstract class VaultDatabase : RoomDatabase() {
    abstract fun records(): VaultDao
    companion object {
        const val NAME = "messenger-v1.db"
        fun open(context: Context): VaultDatabase {
            System.loadLibrary("sqlcipher")
            val passphrase = DatabaseKey.loadOrCreate(context)
            // The factory keeps the passphrase for connection reopening; lifetime is the database.
            return Room.databaseBuilder(context.applicationContext, VaultDatabase::class.java, NAME)
                .openHelperFactory(SupportOpenHelperFactory(passphrase))
                .setJournalMode(JournalMode.TRUNCATE)
                .build()
        }
    }
}

class RoomVault(private val db: VaultDatabase, private val profile: String) : Vault {
    override fun <T> transaction(block: (Records) -> T): T = db.runInTransaction(Callable {
        val dao = db.records()
        var active = true
        val records = object : Records {
            override fun get(bucket: String, key: String): ByteArray? { check(active); return dao.get(profile, bucket, key) }
            override fun put(bucket: String, key: String, value: ByteArray) { check(active); dao.put(VaultRow(profile, bucket, key, value)) }
            override fun delete(bucket: String, key: String) { check(active); dao.delete(profile, bucket, key) }
            override fun keys(bucket: String): List<String> { check(active); return dao.keys(profile, bucket) }
        }
        try { block(records) } finally { active = false }
    })
}
