package ru.kniterp.messenger.network

import java.time.OffsetDateTime
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
        DeviceProofPayload.activation(UUID.fromString(Wire.id(challenge.challengeId)), Wire.bytes(challenge.nonce),
            OffsetDateTime.parse(challenge.expiresAt).toInstant(), phone, UUID.fromString(device.deviceId),
            device.registrationId, Wire.bytes(device.identityKey), authKey)
    fun refresh(challenge: Challenge, accountId: String, refreshTokenId: String, device: LocalDevice, authKey: ByteArray): ByteArray =
        DeviceProofPayload.refresh(UUID.fromString(Wire.id(challenge.challengeId)), Wire.bytes(challenge.nonce),
            OffsetDateTime.parse(challenge.expiresAt).toInstant(), UUID.fromString(Wire.id(accountId)),
            UUID.fromString(Wire.id(refreshTokenId)), UUID.fromString(device.deviceId), device.registrationId,
            Wire.bytes(device.identityKey), authKey)
}

enum class Country(val callingCode: String, val nationalDigits: Int) { RU("7", 10), BY("375", 9), KZ("7", 10), UZ("998", 9) }

/** Builds E.164 from the chosen country and typed digits. The server's libphonenumber check is authoritative. */
object PhoneInput {
    fun e164(country: Country, typed: String): String {
        val input = typed.trim()
        require(input.isNotEmpty() && input.all { it in '0'..'9' || it in " +-()" } &&
            input.count { it == '+' } <= 1 && ('+' !in input || input.startsWith('+'))) { "PHONE_INVALID" }
        var digits = input.filter { it in '0'..'9' }
        if (input.startsWith('+')) {
            require(digits.startsWith(country.callingCode)) { "PHONE_INVALID" }
            digits = digits.removePrefix(country.callingCode)
        } else if (country.callingCode == "7" && digits.length == 11 && (digits[0] == '8' || digits[0] == '7')) digits = digits.drop(1)
        else if (country == Country.BY && digits.length == 11 && digits.startsWith("80")) digits = digits.drop(2)
        require(digits.length == country.nationalDigits) { "PHONE_INVALID" }
        return "+" + country.callingCode + digits
    }
}
