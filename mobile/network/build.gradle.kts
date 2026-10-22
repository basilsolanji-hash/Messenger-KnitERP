plugins {
    kotlin("jvm")
    kotlin("plugin.serialization")
}
kotlin { jvmToolchain(21) }
dependencies {
    implementation(project(":core"))
    implementation("com.squareup.okhttp3:okhttp:5.3.0")
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.9.0")
    testImplementation(kotlin("test-junit"))
    testImplementation("com.squareup.okhttp3:mockwebserver3:5.3.0")
    testImplementation("com.squareup.okhttp3:okhttp-tls:5.3.0")
}
dependencyLocking { lockAllConfigurations() }
tasks.test { exclude("**/ServerSmokeTest*"); jvmArgs("-Xcheck:jni") }
tasks.register<Test>("serverSmoke") {
    testClassesDirs = sourceSets.test.get().output.classesDirs
    classpath = sourceSets.test.get().runtimeClasspath
    include("**/ServerSmokeTest*")
    jvmArgs("-Xcheck:jni")
    outputs.upToDateWhen { false }
}
