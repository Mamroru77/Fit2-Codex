// AGP 9's built-in Kotlin brings its own Kotlin Gradle plugin, and that baseline can be older than
// the Compose compiler plugin below. Declaring the Kotlin version here is the documented way to
// raise it, so the compiler plugin and the compiler itself cannot disagree about which Kotlin this
// is. The `buildscript` block is the one statement Gradle allows before `plugins`.
buildscript {
    dependencies {
        classpath("org.jetbrains.kotlin:kotlin-gradle-plugin:2.4.10")
    }
}

// Plugin versions are declared once here and applied by :app.
//
// These are not guesses: the Android CI job prints the versions the runner can actually resolve
// (AGP, Compose BOM, Kotlin), and these come from that list.
//
// AGP 9 compiles Kotlin itself, so `org.jetbrains.kotlin.android` is deliberately absent — applying
// it is an error from AGP 9.0 onwards. Only the Compose compiler plugin is still applied by name,
// and it is versioned with Kotlin because Kotlin 2.x ships it as a Kotlin plugin.

plugins {
    id("com.android.application") version "9.4.1" apply false
    id("org.jetbrains.kotlin.plugin.compose") version "2.4.10" apply false

    // Room's schema and query code is generated, so a symbol processor is needed. KSP is the
    // supported processor for Room on Kotlin 2.x; kapt is not compatible with AGP 9's built-in Kotlin.
    id("com.google.devtools.ksp") version "2.3.12" apply false

    // The v1 wire format is parsed with kotlinx.serialization, which is a compiler plugin rather than
    // a reflective library: a missing required field is then a compile-time-shaped failure at parse
    // time rather than a silently absent value.
    id("org.jetbrains.kotlin.plugin.serialization") version "2.4.10" apply false
}
