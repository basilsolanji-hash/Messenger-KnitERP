package ru.kniterp.messenger.core

import java.security.MessageDigest
import java.security.SecureRandom
import java.util.UUID
import org.signal.libsignal.protocol.*
import org.signal.libsignal.protocol.ecc.ECKeyPair
import org.signal.libsignal.protocol.ecc.ECPublicKey
import org.signal.libsignal.protocol.fingerprint.NumericFingerprintGenerator
import org.signal.libsignal.protocol.kem.KEMKeyPair
import org.signal.libsignal.protocol.kem.KEMKeyType
import org.signal.libsignal.protocol.kem.KEMPublicKey
import org.signal.libsignal.protocol.message.CiphertextMessage
import org.signal.libsignal.protocol.message.PreKeySignalMessage
import org.signal.libsignal.protocol.message.SignalMessage
import org.signal.libsignal.protocol.state.*

/** Single-device PQXDH/session integration. Each operation creates a transaction-scoped adapter.
 * Private keys never leave the vault. Native records are always reconstructed from saved bytes.
 */
class SignalClient(private val vault: Vault) {
    fun initialize(preKeyCount: Int = 20) = vault.transaction { r ->
        require(preKeyCount in 1..100)
        if (r.get("local", "identity") == null) {
            val identity = IdentityKeyPair.generate()
            r.put("local", "identity", identity.serialize())
            r.put("local", "registration", (SecureRandom().nextInt(16380) + 1).toString().toByteArray(Charsets.US_ASCII))
            val store = SignalStore(r)
            val signed = ECKeyPair.generate()
            store.storeSignedPreKey(1, SignedPreKeyRecord(1, System.currentTimeMillis(), signed,
                identity.privateKey.calculateSignature(signed.publicKey.serialize())))
            for (id in 1..preKeyCount) {
                store.storePreKey(id, PreKeyRecord(id, ECKeyPair.generate()))
                val pq = KEMKeyPair.generate(KEMKeyType.KYBER_1024)
                store.storeKyberPreKey(id, KyberPreKeyRecord(id, System.currentTimeMillis(), pq,
                    identity.privateKey.calculateSignature(pq.publicKey.serialize())))
            }
        }
    }
    fun registrationId(): Int = vault.transaction { SignalStore(it).localRegistrationId }
    fun identityPublic(): String = vault.transaction { Wire.base64(SignalStore(it).identityKeyPair.publicKey.serialize()) }
    fun bind(device: Device) = vault.transaction { r ->
        val existing = r.read<Device>("local", "device")
        require(existing == null || existing == device) { "A vault cannot be rebound to another account/device" }
        r.write("local", "device", device)
    }
    fun localDevice(): Device = vault.transaction { local(it) }
    fun deviceOrNull(): Device? = vault.transaction { it.read<Device>("local", "device") }
    private fun local(r: Records) = requireNotNull(r.read<Device>("local", "device")) { "Account is not bound" }
    private fun address(accountId: String) = SignalProtocolAddress(Wire.id(accountId), 1)

    fun publicBundle(): PublishBundle = vault.transaction { r ->
        val s = SignalStore(r)
        val signed = s.loadSignedPreKey(1)
        PublishBundle(signed.id, Wire.base64(signed.keyPair.publicKey.serialize()), Wire.base64(signed.signature),
            r.keys("prekey").map { id -> s.loadPreKey(id.toInt()).let { PublicPreKey(it.id, Wire.base64(it.keyPair.publicKey.serialize())) } },
            s.loadKyberPreKeys().map { PublicPqPreKey(it.id, Wire.base64(it.keyPair.publicKey.serialize()), Wire.base64(it.signature)) })
    }

    /** expectedAccountId comes from the selected contact, never from the untrusted response. */
    fun establish(expectedAccountId: String, bundle: ClaimedBundle) = vault.transaction { r ->
        Wire.id(expectedAccountId)
        require(bundle.accountId == expectedAccountId && bundle.signalDeviceId == 1)
        require(bundle.registrationId in 1..16383)
        require(listOf(bundle.preKey.id, bundle.signedPreKeyId, bundle.pqPreKey.id).all { it in 1..0xffffff })
        val me = local(r)
        require(me.accountId != expectedAccountId) { "Self-chat is not implemented" }
        val peer = bundle.device()
        checkPeer(r, peer)
        val store = SignalStore(r)
        val remote = address(expectedAccountId)
        val identity = IdentityKey(Wire.bytes(bundle.identityKey))
        if (!store.isTrustedIdentity(remote, identity, IdentityKeyStore.Direction.SENDING)) throw IdentityChanged()
        // libsignal checks ECC and PQ signatures; server validation cannot replace this.
        val keyBundle = PreKeyBundle(bundle.registrationId, 1, bundle.preKey.id, ECPublicKey(Wire.bytes(bundle.preKey.publicKey)),
            bundle.signedPreKeyId, ECPublicKey(Wire.bytes(bundle.signedPreKey)), Wire.bytes(bundle.signature), identity,
            bundle.pqPreKey.id, KEMPublicKey(Wire.bytes(bundle.pqPreKey.publicKey)), Wire.bytes(bundle.pqPreKey.signature))
        SessionBuilder(store, remote, address(me.accountId)).process(keyBundle)
        r.write("peer", peer.accountId, peer)
    }
    private fun checkPeer(r: Records, peer: Device) {
        val prior = r.read<Device>("peer", peer.accountId)
        if (prior != null && prior != peer) throw DeviceChanged()
    }

    /** Ratchet advance, immutable retry envelope and local history commit together. */
    fun prepare(peerAccountId: String, text: String, id: String = UUID.randomUUID().toString()): Outgoing = vault.transaction { r ->
        Wire.id(id); Wire.id(peerAccountId)
        require(text.isNotBlank() && text.toByteArray(Charsets.UTF_8).size <= MAX_TEXT_BYTES)
        val me = local(r)
        val peer = requireNotNull(r.read<Device>("peer", peerAccountId)) { "Establish a trusted session first" }
        val content = Content(id = id, sender = me, recipient = peer, text = text)
        r.read<StoredOutgoing>("outgoing", id)?.let {
            if (it.content != content) throw MessageConflict()
            return@transaction it.request
        }
        val encoded = Wire.json.encodeToString(content).toByteArray(Charsets.UTF_8)
        val cipher = try { SessionCipher(SignalStore(r), address(me.accountId), address(peerAccountId)).encrypt(encoded) }
        finally { encoded.fill(0) }
        val outgoing = Outgoing(id, peer.accountId, peer.deviceId, peer.generation, cipher.type, Wire.base64(cipher.serialize()))
        require(Wire.bytes(outgoing.ciphertext).size <= MAX_CIPHERTEXT_BYTES)
        r.write("outgoing", id, StoredOutgoing(outgoing, content))
        r.write("pending", id, outgoing)
        append(r, id, peerAccountId, true, text)
        outgoing
    }
    fun pending(): List<Outgoing> = vault.transaction { r -> r.keys("pending").map { requireNotNull(r.read<Outgoing>("pending", it)) } }
    fun accepted(id: String) = vault.transaction { r ->
        require(r.get("outgoing", Wire.id(id)) != null)
        r.delete("pending", id)
    }

    /** ACK is allowed only after this returns. Failure rolls ratchet, prekeys and plaintext back. */
    fun receive(item: Incoming): ChatMessage = vault.transaction { r ->
        require(item.sequence > 0 && item.messageType in setOf(CiphertextMessage.WHISPER_TYPE, CiphertextMessage.PREKEY_TYPE))
        Wire.id(item.clientMessageId)
        val me = local(r)
        val peer = item.sender()
        require(peer.accountId != me.accountId)
        checkPeer(r, peer)
        val bytes = Wire.bytes(item.ciphertext)
        require(bytes.isNotEmpty() && bytes.size <= MAX_CIPHERTEXT_BYTES)
        val receiptKey = "${peer.accountId}/${item.clientMessageId}"
        val digest = Wire.base64(MessageDigest.getInstance("SHA-256").digest(
            "${item.messageType}:${peer.accountId}:${peer.deviceId}:${peer.generation}:${item.ciphertext}".toByteArray(Charsets.UTF_8)))
        r.read<Receipt>("receipt", receiptKey)?.let {
            if (it.digest != digest) throw MessageConflict()
            return@transaction requireNotNull(r.read<ChatMessage>("messages", it.messageId))
        }
        val cipher = SessionCipher(SignalStore(r), address(me.accountId), address(peer.accountId))
        val plaintext = try {
            when (item.messageType) {
                CiphertextMessage.PREKEY_TYPE -> cipher.decrypt(PreKeySignalMessage(bytes))
                else -> cipher.decrypt(SignalMessage(bytes))
            }
        } catch (_: UntrustedIdentityException) { throw IdentityChanged() }
        val content = try { Wire.json.decodeFromString<Content>(plaintext.toString(Charsets.UTF_8)) }
        finally { plaintext.fill(0) }
        if (content.version != 1 || content.id != item.clientMessageId || content.sender != peer || content.recipient != me)
            throw EnvelopeMismatch()
        require(content.text.isNotBlank() && content.text.toByteArray(Charsets.UTF_8).size <= MAX_TEXT_BYTES)
        r.write("peer", peer.accountId, peer)
        val result = append(r, content.id, peer.accountId, false, content.text)
        r.write("receipt", receiptKey, Receipt(digest, messageKey(peer.accountId, content.id, false)))
        result
    }
    private fun messageKey(peer: String, id: String, outgoing: Boolean) = "$peer/$outgoing/$id"
    private fun append(r: Records, id: String, peer: String, outgoing: Boolean, text: String): ChatMessage {
        val ordinal = (r.read<Long>("local", "ordinal") ?: 0L) + 1
        r.write("local", "ordinal", ordinal)
        return ChatMessage(id, peer, outgoing, text, ordinal).also { r.write("messages", messageKey(peer, id, outgoing), it) }
    }
    fun history(): List<ChatMessage> = vault.transaction { r ->
        r.keys("messages").map { requireNotNull(r.read<ChatMessage>("messages", it)) }.sortedBy { it.ordinal }
    }
    fun safetyNumber(peerAccountId: String): String = vault.transaction { r -> fingerprint(r, peerAccountId).displayableFingerprint.displayText }
    fun safetyQr(peerAccountId: String): ByteArray = vault.transaction { r -> fingerprint(r, peerAccountId).scannableFingerprint.serialized }
    fun matchesSafetyQr(peerAccountId: String, scanned: ByteArray): Boolean = vault.transaction { r ->
        fingerprint(r, peerAccountId).scannableFingerprint.compareTo(scanned)
    }
    private fun fingerprint(r: Records, peerAccountId: String): org.signal.libsignal.protocol.fingerprint.Fingerprint {
        Wire.id(peerAccountId)
        val me = local(r)
        val store = SignalStore(r)
        val remote = requireNotNull(store.getIdentity(address(peerAccountId))) { "No pinned identity" }
        return NumericFingerprintGenerator(5200).createFor(2, me.accountId.toByteArray(Charsets.UTF_8),
            store.identityKeyPair.publicKey, peerAccountId.toByteArray(Charsets.UTF_8), remote)
    }
    companion object {
        const val MAX_TEXT_BYTES = 16 * 1024
        const val MAX_CIPHERTEXT_BYTES = 65536
    }
}
