package ru.kniterp.messenger.core

import java.util.Base64
import java.util.UUID
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

object Wire {
    val json = Json { ignoreUnknownKeys = false; encodeDefaults = true }
    fun base64(bytes: ByteArray): String = Base64.getEncoder().encodeToString(bytes)
    fun bytes(value: String): ByteArray = Base64.getDecoder().decode(value)
    fun id(value: String): String = UUID.fromString(value).toString().also {
        require(it == value && it != ZERO) { "Expected a nonzero canonical UUID" }
    }
    private const val ZERO = "00000000-0000-0000-0000-000000000000"
}

@Serializable
data class Device(val accountId: String, val deviceId: String, val generation: Int) {
    init { Wire.id(accountId); Wire.id(deviceId); require(generation > 0) }
}

@Serializable data class PublicPreKey(val id: Int, val publicKey: String)
@Serializable data class PublicPqPreKey(val id: Int, val publicKey: String, val signature: String)
@Serializable data class PublishBundle(
    val signedPreKeyId: Int, val signedPreKey: String, val signature: String,
    val preKeys: List<PublicPreKey>, val pqPreKeys: List<PublicPqPreKey>,
)
@Serializable data class ClaimedBundle(
    val accountId: String, val deviceId: String, val generation: Int, val signalDeviceId: Int,
    val registrationId: Int, val identityKey: String, val signedPreKeyId: Int,
    val signedPreKey: String, val signature: String, val preKey: PublicPreKey, val pqPreKey: PublicPqPreKey,
) {
    fun device() = Device(accountId, deviceId, generation)
}

@Serializable data class Outgoing(
    val clientMessageId: String, val recipientAccountId: String, val recipientDeviceId: String,
    val recipientGeneration: Int, val messageType: Int, val ciphertext: String,
)
@Serializable data class Incoming(
    val sequence: Long, val clientMessageId: String, val senderAccountId: String,
    val senderDeviceId: String, val senderGeneration: Int, val messageType: Int, val ciphertext: String,
    val acceptedAt: String, val expiresAt: String,
) {
    fun sender() = Device(senderAccountId, senderDeviceId, senderGeneration)
}
@Serializable data class InboxPage(val items: List<Incoming>, val nextCursor: String?)
@Serializable data class Accepted(val clientMessageId: String, val sequence: Long, val acceptedAt: String, val expiresAt: String)

@Serializable internal data class Content(
    val version: Int = 1, val id: String, val sender: Device, val recipient: Device, val text: String,
)
@Serializable data class ChatMessage(
    val id: String, val peerAccountId: String, val outgoing: Boolean, val text: String, val ordinal: Long,
) {
    override fun toString() = "ChatMessage(id=$id, outgoing=$outgoing, text=<redacted>)"
}
@Serializable internal data class StoredOutgoing(val request: Outgoing, val content: Content) {
    override fun toString() = "StoredOutgoing(<redacted>)"
}
@Serializable internal data class Receipt(val digest: String, val messageId: String)

class IdentityChanged : IllegalStateException("Код безопасности изменился. Отправка остановлена.")
class DeviceChanged : IllegalStateException("Устройство собеседника изменилось. Нужна повторная проверка.")
class EnvelopeMismatch : IllegalArgumentException("Encrypted message does not match its envelope")
class MessageConflict : IllegalArgumentException("Message identifier was reused with different content")
