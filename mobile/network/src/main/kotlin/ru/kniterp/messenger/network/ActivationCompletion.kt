package ru.kniterp.messenger.network

import ru.kniterp.messenger.core.*

/** Complete activation after the session is durable. Retries use the SAME Signal keys and immutable device binding.
 * The readiness marker follows /me verification, binding and successful publication; it is never inferred from a token.
 */
class ActivationCompletion(private val vault: Vault, private val store: AuthStore,
                           private val local: LocalDevice, private val api: MessageApi) {
    private val signal = SignalClient(vault)

    fun ready(): Boolean {
        val session = store.load().session ?: return false
        val binding = Device(session.accountId, session.deviceId, session.generation)
        return signal.deviceOrNull() == binding && vault.transaction {
            it.get("auth", "ready")?.decodeToString() == marker(binding)
        }
    }

    @Synchronized fun complete() {
        val session = requireNotNull(store.load().session) { "No durable session" }
        val remote = api.me()
        val matches = remote.accountId == session.accountId && remote.deviceId == session.deviceId &&
            remote.generation == session.generation && remote.deviceId == local.deviceId &&
            remote.registrationId == local.registrationId && remote.identityKey == local.identityKey
        if (!matches) {
            store.save(AuthState(ended = "ACTIVATION_IDENTITY_MISMATCH"))
            throw SessionEnded("ACTIVATION_IDENTITY_MISMATCH")
        }
        val binding = remote.device()
        signal.bind(binding)
        if (ready()) return
        api.publish(signal.publicBundle())
        // The server keeps consumed prekeys consumed on an identical publication retry.
        vault.transaction { it.put("auth", "ready", marker(binding).toByteArray()) }
    }

    private fun marker(device: Device) = "${device.accountId}/${device.deviceId}/${device.generation}"
}
