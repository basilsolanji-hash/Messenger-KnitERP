# Third-party components

Application source: AGPL-3.0-only, see `LICENSE`. The knitERP graphic and trademark are not licensed as a free trademark.

| Component | Pinned version | License / upstream |
| --- | --- | --- |
| libsignal-client, libsignal-android | 0.105.4 | AGPL-3.0-only; Copyright Signal Messenger, LLC; https://github.com/signalapp/libsignal/tree/v0.105.4 |
| Kotlin plugin / stdlib | 2.2.20 / 2.2.21 | Apache-2.0; https://github.com/JetBrains/kotlin |
| kotlinx.serialization | 1.9.0 | Apache-2.0; https://github.com/Kotlin/kotlinx.serialization |
| kotlinx.coroutines | 1.10.2 | Apache-2.0; https://github.com/Kotlin/kotlinx.coroutines |
| AndroidX / Compose / Room | See Gradle lock files | Apache-2.0; https://android.googlesource.com/platform/frameworks/support/ |
| SQLCipher for Android | 4.19.1 | BSD-style license and included notices; https://github.com/sqlcipher/sqlcipher-android |
| OkHttp / MockWebServer (tests) | 5.3.0 | Apache-2.0; https://github.com/square/okhttp |
| Okio | 3.16.2 | Apache-2.0; https://github.com/square/okio |
| Gradle Wrapper | 8.14.3 | Apache-2.0; https://github.com/gradle/gradle/tree/v8.14.3 |

libsignal's Android AAR includes its own `assets/acknowledgments/` notices. Those assets must be preserved in distributed APKs. The corresponding upstream source and license are linked above; upstream binaries are obtained from https://build-artifacts.signal.org/libraries/maven/ and are not committed into this repository.

This file records the direct Android dependencies introduced by this milestone. A complete distribution license inventory, in-app notice viewer, and corresponding-source delivery must be completed before a public release. The current debug APK is an internal development build.
