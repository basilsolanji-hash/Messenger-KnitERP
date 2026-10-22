package ru.kniterp.messenger.network

import java.util.UUID
import kotlinx.serialization.Serializable
import ru.kniterp.messenger.core.*

@Serializable data class EnrollmentRequest(val displayName: String, val deviceId: String, val registrationId: Int, val identityKey: String)

/** All bytes reside in the encrypted Room profile containing this phone's Signal state. */
class Enrollment(private val vault: Vault) {
    val signal = SignalClient(vault)
    fun initialize() = signal.initialize()
    private fun deviceId(): String = vault.transaction { r ->
        r.get("network", "enrollment-device")?.toString(Charsets.US_ASCII) ?: UUID.randomUUID().toString().also {
            r.put("network", "enrollment-device", it.toByteArray(Charsets.US_ASCII))
        }
    }
    fun request(name: String): EnrollmentRequest {
        require(name.isNotBlank() && name.codePointCount(0, name.length) <= 64 && name.none(Char::isISOControl))
        return EnrollmentRequest(name.trim(), deviceId(), signal.registrationId(), signal.identityPublic())
    }
    fun saved(): SavedConnection? = vault.transaction { r ->
        r.get("network", "session")?.let { Wire.json.decodeFromString<SavedConnection>(it.toString(Charsets.UTF_8)) }
    }
    fun validateTarget(config: TestConnection) {
        config.server.url()
        saved()?.let { prior ->
            require(prior.connection.server.url() == config.server.url() && prior.connection.server.pins.toSet() == config.server.pins.toSet()) {
                "This vault is pinned to a different server"
            }
        }
    }
    fun accept(config: TestConnection, me: RemoteMe): SavedConnection {
        validateTarget(config)
        require(me.deviceId == deviceId() && me.identityKey == signal.identityPublic() && me.registrationId == signal.registrationId()) {
            "Session does not belong to this phone's keys"
        }
        val value = SavedConnection(config, me)
        val remaining = value.expiresAtMillis() - System.currentTimeMillis()
        require(remaining in 1001..960_000) { "Expected a short test session; check phone time" }
        // An interrupted save may leave an immutable binding; reimporting the SAME device is safe.
        signal.bind(me.device())
        vault.transaction { it.put("network", "session", Wire.json.encodeToString(value).toByteArray(Charsets.UTF_8)) }
        return value
    }
}
