package ru.kniterp.messenger.network

import java.io.ByteArrayOutputStream
import java.io.Closeable
import java.time.OffsetDateTime
import java.util.concurrent.TimeUnit
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.*
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.RequestBody.Companion.toRequestBody
import ru.kniterp.messenger.core.*

@Serializable data class ServerEndpoint(val origin: String, val pins: List<String>) {
    fun canonicalOrigin(): String = url().toString()
    internal fun url(): HttpUrl = origin.toHttpUrl().also {
        require(it.scheme == "https" && it.username.isEmpty() && it.password.isEmpty() &&
            it.encodedPath == "/" && it.query == null && it.fragment == null) { "Expected an HTTPS origin" }
        require(pins.size in 2..5 && pins.distinct().size == pins.size)
        require(pins.all { p -> p.startsWith("sha256/") && p.length == 51 &&
            Wire.bytes(p.substring(7)).let { bytes -> bytes.size == 32 && Wire.base64(bytes) == p.substring(7) } })
    }
}
@Serializable data class TestConnection(val server: ServerEndpoint, val accessToken: String) {
    init { require(accessToken.length in 32..8192 && accessToken.all { it.code in 33..126 }) }
    override fun toString() = "TestConnection(<redacted>)"
}
@Serializable data class SavedConnection(val connection: TestConnection, val identity: RemoteMe) {
    override fun toString() = "SavedConnection(<redacted>)"
    fun expiresAtMillis(): Long = OffsetDateTime.parse(identity.expiresAt).toInstant().toEpochMilli()
}
class ApiFailure(val status: Int, val code: String) : IllegalStateException("API $status $code")
class ExpiredSession : IllegalStateException("Test session expired")

/** Platform trust + hostname validation + exact-host SPKI pins, before Authorization is sent. */
class PinnedApi internal constructor(
    private val base: HttpUrl, private val token: String, private val client: OkHttpClient,
    private val expiresAtMillis: Long?, private val clock: () -> Long,
) : MessageApi, Closeable {
    companion object {
        const val MAX_RESPONSE = 1_048_576
        fun connect(config: TestConnection, expiresAtMillis: Long? = null): PinnedApi =
            configured(config, expiresAtMillis, OkHttpClient(), System::currentTimeMillis)
        internal fun configured(config: TestConnection, expiresAtMillis: Long?, baseClient: OkHttpClient, clock: () -> Long): PinnedApi {
            val url = config.server.url()
            val pins = CertificatePinner.Builder().add(url.host, *config.server.pins.toTypedArray()).build()
            val client = baseClient.newBuilder().certificatePinner(pins)
                .connectionSpecs(listOf(ConnectionSpec.MODERN_TLS))
                .followRedirects(false).followSslRedirects(false).retryOnConnectionFailure(false)
                .connectTimeout(10, TimeUnit.SECONDS).readTimeout(30, TimeUnit.SECONDS).callTimeout(40, TimeUnit.SECONDS).build()
            return PinnedApi(url, config.accessToken, client, expiresAtMillis, clock)
        }
    }
    override fun me(): RemoteMe = Wire.json.decodeFromString(call("me"))
    override fun publish(bundle: PublishBundle) { call("keys/bundles", Wire.json.encodeToString(bundle), true) }
    override fun claim(request: ClaimRequest): ClaimedBundle = Wire.json.decodeFromString(call("keys/claim", Wire.json.encodeToString(request)))
    override fun send(message: Outgoing): Accepted = Wire.json.decodeFromString(call("messages", Wire.json.encodeToString(message)))
    override fun inbox(cursor: String?): InboxPage = Wire.json.decodeFromString(call("inbox", cursor = cursor))
    override fun ack(request: AckRequest) { call("inbox/ack", Wire.json.encodeToString(request), true) }
    private fun call(path: String, json: String? = null, empty: Boolean = false, cursor: String? = null): String {
        if (expiresAtMillis != null && clock() + 1000 >= expiresAtMillis) throw ExpiredSession()
        val url = base.newBuilder().addPathSegments("api/v1/$path").apply {
            if (cursor != null) { require(cursor.length <= 8192); addQueryParameter("cursor", cursor) }
        }.build()
        val request = Request.Builder().url(url).header("Authorization", "Bearer $token").header("Accept", "application/json")
            .apply { if (json != null) post(json.toRequestBody("application/json; charset=utf-8".toMediaType())) }.build()
        client.newCall(request).execute().use { response ->
            val output = ByteArrayOutputStream()
            response.body?.byteStream()?.use { input ->
                val buffer = ByteArray(8192)
                while (true) {
                    val count = input.read(buffer)
                    if (count < 0) break
                    require(output.size() + count <= MAX_RESPONSE) { "API response too large" }
                    output.write(buffer, 0, count)
                }
            }
            val body = output.toByteArray().decodeToString(throwOnInvalidSequence = true)
            if (response.code !in 200..299) {
                val code = runCatching { Wire.json.parseToJsonElement(body).jsonObject["code"]?.jsonPrimitive?.content }
                    .getOrNull()?.takeIf { it.matches(Regex("[A-Z0-9_]{1,64}")) } ?: "HTTP_ERROR"
                throw ApiFailure(response.code, code)
            }
            require(response.code == if (empty) 204 else 200) { "Unexpected API status" }
            if (empty) require(body.isEmpty())
            return body
        }
    }
    override fun close() { client.dispatcher.cancelAll(); client.connectionPool.evictAll(); client.dispatcher.executorService.shutdown() }
}
