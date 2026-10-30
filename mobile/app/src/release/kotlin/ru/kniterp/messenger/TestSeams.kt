package ru.kniterp.messenger

import javax.net.ssl.X509TrustManager

/** Release builds: platform roots only. There is no setter, so nothing can replace them. */
internal object TestSeams {
    val trust: X509TrustManager? get() = null
}
