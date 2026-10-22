package ru.kniterp.messenger.network

import java.security.MessageDigest
import java.time.OffsetDateTime
import java.util.Base64
import java.util.UUID
import ru.kniterp.messenger.core.Wire

/** Proof-of-possession key. Android keeps it non-exportable in AndroidKeyStore; it is NOT the Signal identity key. */
interface DeviceSigner {
    /** X.509 SubjectPublicKeyInfo DER of the P-256 key. */
    val authKey: ByteArray
    /** SHA256withECDSA over [payload], DER-encoded as java.security.Signature produces it. */
    fun sign(payload: ByteArray): ByteArray
}

/** Public parts of this phone's activation: never a private key. */
data class LocalDevice(val deviceId: String, val registrationId: Int, val identityKey: String) {
    init {
        Wire.id(deviceId)
        require(registrationId in 1..0x3fff)
        require(Wire.bytes(identityKey).let { it.size == 33 && it[0] == 5.toByte() })
    }
}

/** kniterp-device-proof-v1, byte-exact with docs/ANDROID_AUTH.md and the server. */
object DeviceProof {
    const val VERSION = "kniterp-device-proof-v1"

    fun activation(challenge: Challenge, phone: String, device: LocalDevice, authKey: ByteArray): ByteArray =
        payload("activate", challenge, phone, null, null, device, authKey)

    fun refresh(challenge: Challenge, accountId: String, refreshTokenId: String, device: LocalDevice, authKey: ByteArray): ByteArray =
        payload("refresh", challenge, null, accountId, refreshTokenId, device, authKey)

    private fun payload(op: String, challenge: Challenge, phone: String?, accountId: String?, tokenId: String?,
                        device: LocalDevice, authKey: ByteArray): ByteArray {
        val lines = listOf(
            VERSION,
            "op:$op",
            "challenge:" + UUID.fromString(challenge.challengeId).toString(),
            "nonce:" + base64Url(Wire.bytes(challenge.nonce)),
            "expires:" + OffsetDateTime.parse(challenge.expiresAt).toEpochSecond(),
            "phone:" + (phone ?: "-"),
            "account:" + (accountId?.let(Wire::id) ?: "-"),
            "token:" + (tokenId?.let(Wire::id) ?: "-"),
            "device:" + device.deviceId,
            "registration:" + device.registrationId,
            "identity:" + base64Url(Wire.bytes(device.identityKey)),
            "authkey:" + base64Url(MessageDigest.getInstance("SHA-256").digest(authKey)),
        )
        return (lines.joinToString("\n") + "\n").toByteArray(Charsets.US_ASCII)
    }

    private fun base64Url(bytes: ByteArray): String = Base64.getUrlEncoder().withoutPadding().encodeToString(bytes)
}

enum class Country(val callingCode: String, val nationalDigits: Int) { RU("7", 10), BY("375", 9), KZ("7", 10), UZ("998", 9) }

/** Builds E.164 from the chosen country and typed digits. The server's libphonenumber check is authoritative. */
object PhoneInput {
    fun e164(country: Country, typed: String): String {
        var digits = typed.filter(Char::isDigit)
        if (typed.trimStart().startsWith("+")) digits = digits.removePrefix(country.callingCode)
        else if (country.callingCode == "7" && digits.length == 11 && (digits[0] == '8' || digits[0] == '7')) digits = digits.drop(1)
        else if (country == Country.BY && digits.length == 11 && digits.startsWith("80")) digits = digits.drop(2)
        require(digits.length == country.nationalDigits && typed.all { it.isDigit() || it in " +-()" }) { "PHONE_INVALID" }
        return "+" + country.callingCode + digits
    }
}
