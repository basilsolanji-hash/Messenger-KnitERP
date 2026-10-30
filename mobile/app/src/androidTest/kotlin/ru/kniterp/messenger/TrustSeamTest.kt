package ru.kniterp.messenger

import android.content.pm.ApplicationInfo
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import java.security.KeyStore
import javax.net.ssl.TrustManagerFactory
import javax.net.ssl.X509TrustManager
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

/** The replaced trust root is honoured only by a debuggable build; release sources expose no setter (CI checks them). */
@RunWith(AndroidJUnit4::class)
class TrustSeamTest {
    @Test fun replacedRootIsIgnoredUnlessDebuggable() {
        val candidate = TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm())
            .apply { init(null as KeyStore?) }.trustManagers.single() as X509TrustManager
        assertNull(testTrust(debuggable = false, candidate = candidate))
        assertSame(candidate, testTrust(debuggable = true, candidate = candidate))
        assertNull(testTrust(debuggable = true, candidate = null))
        val flags = InstrumentationRegistry.getInstrumentation().targetContext.applicationInfo.flags
        assertTrue("Instrumentation runs the debug build", (flags and ApplicationInfo.FLAG_DEBUGGABLE) != 0)
    }
}
