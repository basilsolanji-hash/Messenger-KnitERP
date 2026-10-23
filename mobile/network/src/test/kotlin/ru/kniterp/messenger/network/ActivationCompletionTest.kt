package ru.kniterp.messenger.network

import java.io.IOException
import java.util.UUID
import kotlin.test.*
import ru.kniterp.messenger.core.*

class ActivationCompletionTest {
    private val vault = MemoryVault()
    private val signal = SignalClient(vault).apply { initialize(2) }
    private val deviceId = UUID.randomUUID().toString()
    private val accountId = UUID.randomUUID().toString()
    private val local = LocalDevice(deviceId, signal.registrationId(), signal.identityPublic())
    private val store = VaultAuthStore(vault)
    private val saved = StoredSession(accountId, deviceId, 1, "unused-token", "2030-01-01T00:00:00Z")
    private var remote = RemoteMe(accountId, deviceId, 1, "Test", local.registrationId, local.identityKey, "2030-01-01T00:00:00Z")
    private val bundles = mutableListOf<PublishBundle>()
    private var meFails = false
    private var publishFails = false
    private val api = object : MessageApi {
        override fun me(): RemoteMe { if (meFails) throw IOException("offline"); return remote }
        override fun publish(bundle: PublishBundle) { bundles += bundle; if (publishFails) throw IOException("lost response") }
        override fun claim(request: ClaimRequest): ClaimedBundle = error("unused")
        override fun send(message: Outgoing): Accepted = error("unused")
        override fun inbox(cursor: String?): InboxPage = error("unused")
        override fun ack(request: AckRequest) = error("unused")
    }
    private fun completion() = ActivationCompletion(vault, store, local, api)
    init { store.save(AuthState(session = saved)) }

    @Test fun completionVerifiesBindsAndCommitsReadiness() {
        val completion = completion()
        assertFalse(completion.ready())
        completion.complete()
        assertTrue(completion.ready())
        assertEquals(Device(accountId, deviceId, 1), signal.localDevice())
        assertEquals(listOf(signal.publicBundle()), bundles)
        completion().complete() // A process restart checks /me but never republishes a completed activation.
        assertEquals(1, bundles.size)
    }

    @Test fun lostPublishResponseRetriesIdenticalKeysAfterRestart() {
        val identity = signal.identityPublic()
        publishFails = true
        assertFailsWith<IOException> { completion().complete() }
        assertFalse(completion().ready())
        assertEquals(Device(accountId, deviceId, 1), signal.localDevice())
        publishFails = false
        completion().complete()
        assertTrue(completion().ready())
        assertEquals(bundles.first(), bundles.last())
        assertEquals(identity, signal.identityPublic())
        assertEquals(saved, store.load().session)
    }

    @Test fun noBindingOrPublicationBeforeVerifiedMe() {
        meFails = true
        assertFailsWith<IOException> { completion().complete() }
        assertFalse(completion().ready())
        assertNull(signal.deviceOrNull())
        assertTrue(bundles.isEmpty())
        assertEquals(saved, store.load().session)
    }

    @Test fun wrongAccountDeviceGenerationOrSignalKeysEndSessionAndPreserveHistory() {
        val correct = remote
        val cases = listOf(correct.copy(accountId = UUID.randomUUID().toString()),
            correct.copy(deviceId = UUID.randomUUID().toString()), correct.copy(generation = 2),
            correct.copy(registrationId = if (local.registrationId == 1) 2 else 1), correct.copy(identityKey = "incorrect"))
        for (wrong in cases) {
            store.save(AuthState(session = saved)); remote = wrong
            assertEquals("ACTIVATION_IDENTITY_MISMATCH", assertFailsWith<SessionEnded> { completion().complete() }.code)
            assertFalse(completion().ready())
            assertNull(store.load().session)
            assertNull(signal.deviceOrNull())
            assertEquals(local.identityKey, signal.identityPublic())
            assertTrue(bundles.isEmpty())
        }
    }

    @Test fun readyMarkerCannotOutliveEndedSession() {
        completion().complete()
        store.save(AuthState(ended = "LOGOUT"))
        assertFalse(completion().ready())
        assertEquals(local.identityKey, signal.identityPublic())
        assertEquals(Device(accountId, deviceId, 1), signal.localDevice())
    }

    @Test fun readyMarkerDoesNotAuthorizeAnotherGeneration() {
        completion().complete()
        store.save(AuthState(session = saved.copy(generation = 2)))
        assertFalse(completion().ready())
    }
}
