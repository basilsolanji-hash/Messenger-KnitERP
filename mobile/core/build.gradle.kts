plugins {
    kotlin("jvm")
    kotlin("plugin.serialization")
}
kotlin { jvmToolchain(21) }
dependencies {
    implementation("org.signal:libsignal-client:0.105.4")
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.9.0")
    testImplementation(kotlin("test-junit"))
}
dependencyLocking { lockAllConfigurations() }
tasks.test { jvmArgs("-Xcheck:jni"); exclude("**/ServerSmokeTest*") }
tasks.register<Test>("serverSmoke") {
    description = "Requires a running loopback Development server and DEV_PROVISIONING_KEY."
    testClassesDirs = sourceSets.test.get().output.classesDirs
    classpath = sourceSets.test.get().runtimeClasspath
    include("**/ServerSmokeTest*")
    jvmArgs("-Xcheck:jni")
    outputs.upToDateWhen { false }
}
