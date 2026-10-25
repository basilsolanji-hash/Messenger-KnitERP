package ru.kniterp.messenger.live

import android.app.Application
import androidx.test.platform.app.InstrumentationRegistry
import java.io.Closeable
import java.net.ConnectException
import java.net.SocketTimeoutException
import java.net.URL
import java.security.KeyStore
import java.security.SecureRandom
import java.security.cert.CertificateFactory
import java.util.Base64
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.TrustManagerFactory
import javax.net.ssl.X509TrustManager
import kotlinx.coroutines.Job
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.*
import org.junit.Assume.assumeTrue
import ru.kniterp.messenger.AuthController
import ru.kniterp.messenger.AuthUiState
import ru.kniterp.messenger.core.*
import ru.kniterp.messenger.network.Country
import ru.kniterp.messenger.network.LoginStep
import ru.kniterp.messenger.network.ServerEndpoint

/**
 * Live API given by instrumentation arguments: a CI-generated CA, an HTTPS origin with SPKI pins and the operator key of
 * a disposable Fake-SMS server. The key reaches only this test process at run time, never an APK or the app's code.
 */
object Live {
    private val args = InstrumentationRegistry.getArguments()
    private val origin: String? = args.getString("liveOrigin")
    private val random = SecureRandom()

    fun assumeConfigured() {
        if (origin == null) {
            assertNotEquals("liveRequired is set but no live API was passed", "true", args.getString("liveRequired"))
            assumeTrue("Live API arguments are absent", false)
        }
    }

    val endpoint: ServerEndpoint by lazy { ServerEndpoint(requireNotNull(origin), requireNotNull(args.getString("livePins")).split(',')) }
    val json: String get() = Wire.json.encodeToString(endpoint)
    val trust: X509TrustManager by lazy {
        val ca = CertificateFactory.getInstance("X.509").generateCertificate(Base64.getDecoder().decode(args.getString("liveCa")).inputStream())
        val store = KeyStore.getInstance(KeyStore.getDefaultType()).apply { load(null); setCertificateEntry("ci-root", ca) }
        TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm()).apply { init(store) }
            .trustManagers.single() as X509TrustManager
    }
    val application: Application get() = InstrumentationRegistry.getInstrumentation().targetContext.applicationContext as Application

    /** National part of a fresh synthetic RU mobile number. */
    fun number(): String = "912" + (0 until 7).joinToString("") { random.nextInt(10).toString() }

    /** What the operator does outside the APK: read the Fake SMS code by the public challenge id. */
    fun code(challengeId: String): String {
        Wire.id(challengeId)
        val context = SSLContext.getInstance("TLS").apply { init(null, arrayOf(trust), null) }
        val connection = URL(endpoint.canonicalOrigin() + "api/v1/dev/sms/code").openConnection() as HttpsURLConnection
        try {
            connection.sslSocketFactory = context.socketFactory // Default hostname verifier stays.
            connection.requestMethod = "POST"; connection.doOutput = true
            connection.setRequestProperty("Content-Type", "application/json")
            connection.setRequestProperty("X-Dev-Provisioning-Token", requireNotNull(args.getString("liveOperatorKey")))
            connection.outputStream.use { it.write("""{"challengeId":"$challengeId"}""".toByteArray()) }
            assertEquals(200, connection.responseCode)
            val body = connection.inputStream.use { it.readBytes().decodeToString() }
            return Wire.json.parseToJsonElement(body).jsonObject["code"]!!.jsonPrimitive.content.also { assertEquals(6, it.length) }
        } finally { connection.disconnect() }
    }

    fun wrong(code: String) = ((code.toInt() + 1) % 1_000_000).toString().padStart(6, '0')
}

/** Faults around the REAL pinned API. [loseAfter]: the server processed the call and the answer was lost. One-shot. */
class FaultyApi(private val real: MessageApi) : MessageApi {
    @Volatile var offline = false
    val loseAfter: MutableSet<String> = ConcurrentHashMap.newKeySet()
    val failBefore: MutableSet<String> = ConcurrentHashMap.newKeySet()
    private fun <T> call(op: String, block: () -> T): T {
        if (offline || failBefore.remove(op)) throw ConnectException("Injected: no network before $op")
        val result = block()
        if (loseAfter.remove(op)) throw SocketTimeoutException("Injected: $op response lost")
        return result
    }
    override fun me() = call("me") { real.me() }
    override fun publish(bundle: PublishBundle) = call("publish") { real.publish(bundle) }
    override fun claim(request: ClaimRequest) = call("claim") { real.claim(request) }
    override fun send(message: Outgoing) = call("send") { real.send(message) }
    override fun inbox(cursor: String?) = call("inbox") { real.inbox(cursor) }
    override fun ack(request: AckRequest) = call("ack") { real.ack(request) }
}

/** One synthetic phone: the production controller with its own registry, so several can share one emulator. */
class Phone(val registry: String = "live-${UUID.randomUUID()}") : Closeable {
    @Volatile lateinit var faults: FaultyApi
        private set
    val controller = AuthController(Live.application, registry, Live.trust) { FaultyApi(it).also { api -> faults = api } }
    val state: AuthUiState get() = controller.state.value

    /** Runs one controller operation to completion; a dropped (busy) call fails the test. */
    fun act(operation: AuthController.() -> Job?): AuthUiState {
        val job = requireNotNull(controller.operation()) { "Operation dropped: controller busy" }
        runBlocking { withTimeout(180_000) { job.join() } }
        return state
    }
    fun start() = act { start() }
    fun configure() = act { configureServer(Live.json) }
    fun request(number: String) = act { login { it.submitPhone(Country.RU, number) } }
    fun submitCode(code: String) = act { login { it.submitCode(code) } }
    fun submitPin(pin: String) = act { login { it.submitPin(pin) } }
    fun code(): String = Live.code(state.challengeId)

    /** Fresh activation through the real SMS flow; returns the account id. */
    fun register(number: String = Live.number()): String {
        start(); configure()
        assertEquals(LoginStep.CODE, request(number).login.step)
        val done = submitCode(code())
        assertEquals(LoginStep.ACTIVATED, done.login.step)
        assertTrue("Activation must reach readiness", done.ready)
        assertEquals(true, done.login.newAccount)
        return done.accountId
    }
    fun open(peer: String) = assertNull(act { selectPeer(peer) }.error)
    fun send(text: String) = act { send(text) }
    fun sync() = act { synchronize() }
    fun incoming(peer: String) = state.messages.filter { !it.outgoing && it.peerAccountId == peer }.map { it.text }
    fun outgoing(text: String) = state.messages.single { it.outgoing && it.text == text }
    override fun close() = controller.close()
}
