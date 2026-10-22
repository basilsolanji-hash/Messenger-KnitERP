package ru.kniterp.messenger.core

import java.security.MessageDigest
import java.util.UUID
import org.signal.libsignal.protocol.*
import org.signal.libsignal.protocol.ecc.ECPublicKey
import org.signal.libsignal.protocol.groups.state.SenderKeyRecord
import org.signal.libsignal.protocol.state.*

/** Transaction-scoped adapter. Every load reconstructs a native object from serialized bytes.
 * Never cache native records: libsignal can mutate them before a transaction fails.
 */
internal class SignalStore(private val records: Records) : SignalProtocolStore {
    override fun getIdentityKeyPair() = IdentityKeyPair(requireNotNull(records.get("local", "identity")))
    override fun getLocalRegistrationId() = requireNotNull(records.get("local", "registration"))
        .toString(Charsets.US_ASCII).toInt()

    override fun isTrustedIdentity(address: SignalProtocolAddress, key: IdentityKey, direction: IdentityKeyStore.Direction): Boolean =
        records.get("identity", address.name)?.let { MessageDigest.isEqual(it, key.serialize()) } ?: true

    override fun saveIdentity(address: SignalProtocolAddress, key: IdentityKey): IdentityKeyStore.IdentityChange {
        // Fail closed in BOTH directions; accepting a changed identity requires a separate user flow.
        if (!isTrustedIdentity(address, key, IdentityKeyStore.Direction.SENDING)) throw IdentityChanged()
        records.put("identity", address.name, key.serialize())
        return IdentityKeyStore.IdentityChange.NEW_OR_UNCHANGED
    }
    override fun getIdentity(address: SignalProtocolAddress): IdentityKey? =
        records.get("identity", address.name)?.let(::IdentityKey)

    override fun loadPreKey(id: Int) = PreKeyRecord(records.get("prekey", "$id") ?: throw InvalidKeyIdException("Missing prekey"))
    override fun storePreKey(id: Int, record: PreKeyRecord) = records.put("prekey", "$id", record.serialize())
    override fun containsPreKey(id: Int) = records.get("prekey", "$id") != null
    override fun removePreKey(id: Int) = records.delete("prekey", "$id")

    override fun loadSignedPreKey(id: Int) = SignedPreKeyRecord(records.get("signed", "$id") ?: throw InvalidKeyIdException("Missing signed prekey"))
    override fun loadSignedPreKeys() = records.keys("signed").map { loadSignedPreKey(it.toInt()) }
    override fun storeSignedPreKey(id: Int, record: SignedPreKeyRecord) = records.put("signed", "$id", record.serialize())
    override fun containsSignedPreKey(id: Int) = records.get("signed", "$id") != null
    override fun removeSignedPreKey(id: Int) = records.delete("signed", "$id")

    override fun loadKyberPreKey(id: Int) = KyberPreKeyRecord(records.get("kyber", "$id") ?: throw InvalidKeyIdException("Missing PQ prekey"))
    override fun loadKyberPreKeys() = records.keys("kyber").map { loadKyberPreKey(it.toInt()) }
    override fun storeKyberPreKey(id: Int, record: KyberPreKeyRecord) = records.put("kyber", "$id", record.serialize())
    override fun containsKyberPreKey(id: Int) = records.get("kyber", "$id") != null
    override fun markKyberPreKeyUsed(id: Int, signedPreKeyId: Int, baseKey: ECPublicKey) {
        // All PQ prekeys in this milestone are one-time keys, never last-resort keys.
        if (!containsKyberPreKey(id)) throw InvalidKeyIdException("Consumed PQ prekey")
        records.delete("kyber", "$id")
    }

    private fun addressKey(address: SignalProtocolAddress) = "${address.name}:${address.deviceId}"
    override fun loadSession(address: SignalProtocolAddress) =
        records.get("session", addressKey(address))?.let(::SessionRecord) ?: SessionRecord()
    override fun loadExistingSessions(addresses: List<SignalProtocolAddress>): List<SessionRecord> = addresses.map {
        records.get("session", addressKey(it))?.let(::SessionRecord) ?: throw NoSessionException(it, "Missing session")
    }
    override fun getSubDeviceSessions(name: String): List<Int> = records.keys("session")
        .filter { it.startsWith("$name:") }.map { it.substringAfterLast(':').toInt() }.filter { it != 1 }
    override fun storeSession(address: SignalProtocolAddress, record: SessionRecord) = records.put("session", addressKey(address), record.serialize())
    override fun containsSession(address: SignalProtocolAddress) = records.get("session", addressKey(address)) != null
    override fun deleteSession(address: SignalProtocolAddress) = records.delete("session", addressKey(address))
    override fun deleteAllSessions(name: String) = records.keys("session").filter { it.startsWith("$name:") }.forEach { records.delete("session", it) }

    override fun storeSenderKey(sender: SignalProtocolAddress, distributionId: UUID, record: SenderKeyRecord) {
        throw UnsupportedOperationException("Group messaging is not enabled")
    }
    override fun loadSenderKey(sender: SignalProtocolAddress, distributionId: UUID): SenderKeyRecord? = null
}
