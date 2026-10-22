package ru.kniterp.messenger.network

import ru.kniterp.messenger.core.*

/** Test-only transactional store. Production Android always uses SQLCipher/Room. */
internal class MemoryVault : Vault {
    private var data = emptyMap<Pair<String, String>, ByteArray>()
    @Synchronized override fun <T> transaction(block: (Records) -> T): T {
        val staged = data.mapValues { it.value.copyOf() }.toMutableMap()
        var active = true
        val records = object : Records {
            override fun get(bucket: String, key: String): ByteArray? { check(active); return staged[bucket to key]?.copyOf() }
            override fun put(bucket: String, key: String, value: ByteArray) { check(active); staged[bucket to key] = value.copyOf() }
            override fun delete(bucket: String, key: String) { check(active); staged.remove(bucket to key) }
            override fun keys(bucket: String): List<String> { check(active); return staged.keys.filter { it.first == bucket }.map { it.second }.sorted() }
        }
        try { val result = block(records); data = staged; return result }
        finally { active = false }
    }
}
