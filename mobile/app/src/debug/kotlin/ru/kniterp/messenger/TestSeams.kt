package ru.kniterp.messenger

import javax.net.ssl.X509TrustManager

/** Debug builds only: instrumentation sets the root of a CI-generated HTTPS front. Pins and hostname still apply. */
internal object TestSeams {
    @Volatile var trust: X509TrustManager? = null
}
