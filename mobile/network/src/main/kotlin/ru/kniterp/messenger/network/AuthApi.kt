package ru.kniterp.messenger.network

import java.io.Closeable
import javax.net.ssl.X509TrustManager
import kotlinx.serialization.Serializable
import okhttp3.HttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import ru.kniterp.messenger.core.Wire

// Wire contract: docs/API_AUTH.md. Byte fields are standard base64 strings, as elsewhere in the client.
@Serializable data class SmsChallengeRequest(val requestId: String, val phone: String, val deviceId: String,
    val registrationId: Int, val identityKey: String, val authKey: String) {
    override fun toString() = "SmsChallengeRequest(<redacted>)"
}
@Serializable data class Challenge(val challengeId: String, val nonce: String, val expiresAt: String,
    val resendAfter: String?, val codeLength: Int, val maxAttempts: Int)
@Serializable data class SmsVerifyRequest(val challengeId: String, val phone: String, val code: String, val signature: String) {
    override fun toString() = "SmsVerifyRequest(<redacted>)"
}
@Serializable data class PinVerifyRequest(val challengeId: String, val phone: String, val pin: String, val signature: String) {
    override fun toString() = "PinVerifyRequest(<redacted>)"
}
@Serializable data class IssuedSession(val accountId: String, val deviceId: String, val generation: Int, val accessToken: String,
    val expiresAt: String, val refreshToken: String, val refreshExpiresAt: String, val newAccount: Boolean) {
    override fun toString() = "IssuedSession(accountId=$accountId, deviceId=$deviceId, generation=$generation, <tokens redacted>)"
}
@Serializable data class Activation(val status: String, val session: IssuedSession?, val recoveryAvailable: Boolean)
@Serializable data class RefreshChallengeRequest(val deviceId: String)
@Serializable data class RefreshRequest(val refreshToken: String, val challengeId: String, val signature: String) {
    override fun toString() = "RefreshRequest(<redacted>)"
}
@Serializable data class SetPinRequest(val pin: String, val currentPin: String?) {
    override fun toString() = "SetPinRequest(<redacted>)"
}
@Serializable data class DisablePinRequest(val currentPin: String) {
    override fun toString() = "DisablePinRequest(<redacted>)"
}

/** Public auth endpoints. Anonymous calls never carry Authorization; the rest take the user session's tokens. */
class AuthApi internal constructor(private val base: HttpUrl, private val client: OkHttpClient) : Closeable {
    companion object {
        /** [trust] is for instrumentation against a CI-generated CA only; see [baseClient]. Production passes null. */
        fun connect(server: ServerEndpoint, trust: X509TrustManager? = null): AuthApi = configured(server, baseClient(trust))
        internal fun configured(server: ServerEndpoint, baseClient: OkHttpClient) = AuthApi(server.url(), pinnedClient(server, baseClient))
    }

    fun requestCode(request: SmsChallengeRequest): Challenge = Wire.json.decodeFromString(post("auth/sms/challenges", Wire.json.encodeToString(request)))
    fun verifyCode(request: SmsVerifyRequest): Activation = Wire.json.decodeFromString(post("auth/sms/verify", Wire.json.encodeToString(request)))
    fun verifyPin(request: PinVerifyRequest): Activation = Wire.json.decodeFromString(post("auth/pin/verify", Wire.json.encodeToString(request)))
    fun refreshChallenge(deviceId: String): Challenge =
        Wire.json.decodeFromString(post("auth/refresh/challenges", Wire.json.encodeToString(RefreshChallengeRequest(deviceId))))
    fun refresh(request: RefreshRequest): IssuedSession = Wire.json.decodeFromString(post("auth/refresh", Wire.json.encodeToString(request)))
    fun logout(accessToken: String) { post("auth/logout", "{}", bearer = accessToken, empty = true) }
    fun setPin(accessToken: String, request: SetPinRequest) {
        post("account/pin", Wire.json.encodeToString(request), bearer = accessToken, empty = true, method = "PUT")
    }
    fun disablePin(accessToken: String, request: DisablePinRequest) {
        post("account/pin/disable", Wire.json.encodeToString(request), bearer = accessToken, empty = true)
    }

    private fun post(path: String, json: String, bearer: String? = null, empty: Boolean = false, method: String = "POST"): String {
        val url = base.newBuilder().addPathSegments("api/v1/$path").build()
        val request = Request.Builder().url(url).header("Accept", "application/json")
            .method(method, json.toRequestBody("application/json; charset=utf-8".toMediaType()))
            .apply { if (bearer != null) header("Authorization", "Bearer $bearer") }.build()
        client.newCall(request).execute().use { response ->
            val body = readBounded(response)
            require(response.code == if (empty) 204 else 200) { "Unexpected API status" }
            if (empty) require(body.isEmpty())
            return body
        }
    }

    override fun close() { client.dispatcher.cancelAll(); client.connectionPool.evictAll(); client.dispatcher.executorService.shutdown() }
}
