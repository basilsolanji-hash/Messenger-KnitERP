package ru.kniterp.messenger.core

/** Test fixture only. Deliberately never included in the Android artifact. */
internal class TestVault(initial: Map<Pair<String, String>, ByteArray> = emptyMap()) : Vault {
    private var data = initial.mapValues { it.value.copyOf() }
    var failNextCommit = false
    @Synchronized fun snapshot() = data.mapValues { it.value.copyOf() }
    @Synchronized override fun <T> transaction(block: (Records) -> T): T {
        val staged = data.mapValues { it.value.copyOf() }.toMutableMap()
        var active = true
        val r = object : Records {
            override fun get(bucket: String, key: String): ByteArray? { check(active); return staged[bucket to key]?.copyOf() }
            override fun put(bucket: String, key: String, value: ByteArray) { check(active); staged[bucket to key] = value.copyOf() }
            override fun delete(bucket: String, key: String) { check(active); staged.remove(bucket to key) }
            override fun keys(bucket: String): List<String> { check(active); return staged.keys.filter { it.first == bucket }.map { it.second }.sorted() }
        }
        try {
            val result = block(r)
            if (failNextCommit) { failNextCommit = false; error("Injected failure before COMMIT") }
            data = staged
            return result
        } finally { active = false }
    }
}

internal data class Participant(val vault: TestVault, val client: SignalClient, val device: Device)
internal fun participant(account: String = java.util.UUID.randomUUID().toString()): Participant {
    val v = TestVault()
    val c = SignalClient(v)
    c.initialize(8)
    val device = Device(account, java.util.UUID.randomUUID().toString(), 1)
    c.bind(device)
    return Participant(v, c, device)
}
internal fun Participant.bundle(): ClaimedBundle {
    val b = client.publicBundle()
    return ClaimedBundle(device.accountId, device.deviceId, device.generation, 1, client.registrationId(),
        client.identityPublic(), b.signedPreKeyId, b.signedPreKey, b.signature, b.preKeys.first(), b.pqPreKeys.first())
}
internal fun Outgoing.incoming(from: Device, sequence: Long = 1) = Incoming(sequence, clientMessageId,
    from.accountId, from.deviceId, from.generation, messageType, ciphertext, "2026-10-22T00:00:00Z", "2026-11-21T00:00:00Z")

/** Change actual encrypted payload bytes, not optional outer pre-key metadata. */
internal fun Incoming.withTamperedBody(): Incoming {
    val bytes = Wire.bytes(ciphertext)
    val body = if (messageType == org.signal.libsignal.protocol.message.CiphertextMessage.PREKEY_TYPE)
        org.signal.libsignal.protocol.message.PreKeySignalMessage(bytes).whisperMessage.body
    else org.signal.libsignal.protocol.message.SignalMessage(bytes).body
    require(body.isNotEmpty())
    val start = bytes.indices.single { offset -> offset + body.size <= bytes.size &&
        body.indices.all { bytes[offset + it] == body[it] } }
    val position = start + body.size / 2
    bytes[position] = (bytes[position].toInt() xor 1).toByte()
    return copy(ciphertext = Wire.base64(bytes))
}
