package ru.kniterp.messenger.core

/** Implementations must serialize callers, commit all writes atomically, and roll back on ANY failure.
 * Records and adapters must never escape the callback. Android uses one SQLCipher/Room transaction.
 */
interface Vault {
    fun <T> transaction(block: (Records) -> T): T
}

interface Records {
    fun get(bucket: String, key: String): ByteArray?
    fun put(bucket: String, key: String, value: ByteArray)
    fun delete(bucket: String, key: String)
    fun keys(bucket: String): List<String>
}

internal inline fun <reified T> Records.read(bucket: String, key: String): T? =
    get(bucket, key)?.let { Wire.json.decodeFromString<T>(it.toString(Charsets.UTF_8)) }

internal inline fun <reified T> Records.write(bucket: String, key: String, value: T) =
    put(bucket, key, Wire.json.encodeToString(value).toByteArray(Charsets.UTF_8))
