plugins {
    // Kotlin compilation is built into AGP 9; applying `org.jetbrains.kotlin.android` here is an
    // error, and the Kotlin JVM target follows `compileOptions.targetCompatibility`.
    id("com.android.application")
    id("org.jetbrains.kotlin.plugin.compose")
    id("org.jetbrains.kotlin.plugin.serialization")
    id("com.google.devtools.ksp")
}

/** The shared v1 contract fixtures, at the repository root. */
val CONTRACTS_V1 = rootProject.projectDir.parentFile.parentFile.resolve("contracts/v1").absolutePath

/** Test-only sources compiled into both the JVM and the instrumentation test source sets. */
val SHARED_TEST = "src/sharedTest/java"

android {
    namespace = "com.codexquota.app"

    // API 37 is the first release with ACCESS_LOCAL_NETWORK, which this app needs to reach the
    // Bridge on the LAN. Older phones still run it: the permission is requested at runtime only when
    // the platform has it, behind LocalNetworkPermissionController.
    //
    // API 37 ships as a minor release, so the installed platform package is `android-37.0`, and AGP
    // resolves `compileSdk = 37` to minor 0 — that package.
    compileSdk = 37

    // Pinned rather than left to the plugin's default. AGP's default build-tools revision for this
    // release is older than the platform, and a build that silently picks a different revision on a
    // different machine is not reproducible. CI installs exactly this revision.
    buildToolsVersion = "37.0.0"

    defaultConfig {
        applicationId = "com.codexquota.app"
        minSdk = 29
        targetSdk = 37
        versionCode = 1
        versionName = "1.0.0"

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    buildTypes {
        release {
            // V1 is a personal tool distributed by hand, so there is no minification or signing
            // configuration here; a release build must still be reproducible from the same sources.
            isMinifyEnabled = false
        }
    }

    buildFeatures {
        compose = true
    }

    packaging {
        resources {
            // BouncyCastle ships three jars that each carry a copy of the same licence files, and
            // the instrumentation APK cannot contain the same path twice. They are documentation, not
            // code, and nothing in the app reads them; BouncyCastle is a test-only dependency, so
            // this affects only the test APK.
            excludes += setOf(
                "META-INF/LICENSE.md",
                "META-INF/LICENSE-notice.md",
                "META-INF/NOTICE.md",
                "META-INF/versions/9/OSGI-INF/MANIFEST.MF",
            )
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    testOptions {
        unitTests {
            // The unit tests are plain JVM tests over pure Kotlin: the alert engine, the connection
            // state machine and the protocol mapper are all deliberately free of framework types.
            // Keeping this false means a test that accidentally needs the Android framework fails
            // loudly instead of being papered over.
            isIncludeAndroidResources = false
        }
    }

    sourceSets {
        getByName("test") {
            // The v1 contract fixtures are the single source of truth shared with the Windows tests,
            // so the unit tests read the repository copy rather than a duplicated one. A copy would
            // be able to drift from the fixture the Bridge is proven against, which is exactly the
            // failure the shared fixtures exist to prevent.
            resources.directories.add(CONTRACTS_V1)
        }

        // The fake Bridge in the instrumentation tests serves these same documents, so the app's
        // parsing is exercised against the fixture the real Bridge is proven against.
        getByName("androidTest") { resources.directories.add(CONTRACTS_V1) }

        // The certificate authority and the leaves it signs are needed by both the JVM tests and the
        // instrumentation tests, and they must be the *same* code. A second copy in `androidTest`
        // would be a second place for the identity model to be wrong, and the identity model is the
        // thing every security assertion in this module rests on.
        getByName("test") { kotlin.directories.add(SHARED_TEST) }
        getByName("androidTest") { kotlin.directories.add(SHARED_TEST) }
    }
}

// These versions are not guesses. Each was resolved from the published metadata of the repository it
// comes from, and the Android CI job proves the whole set resolves and compiles together.
val coroutinesVersion = "1.11.0"

dependencies {
    implementation(platform("androidx.compose:compose-bom:2026.09.00"))
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-graphics")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.material3:material3")

    implementation("androidx.core:core-ktx:1.19.1")
    implementation("androidx.activity:activity-compose:1.13.0")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.11.0")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.11.0")
    implementation("androidx.navigation:navigation-compose:2.10.2")

    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:$coroutinesVersion")
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.11.0")

    // HTTPS and WSS. OkHttp owns both, so there is one TLS stack and one place the pin is applied.
    implementation("com.squareup.okhttp3:okhttp:5.5.0")

    // The local cache. Room's generated code is produced by KSP.
    implementation("androidx.room:room-runtime:2.8.5")
    implementation("androidx.room:room-ktx:2.8.5")
    ksp("androidx.room:room-compiler:2.8.5")

    // Ordinary phone-local settings, which are not secrets and do not belong in the cache.
    implementation("androidx.datastore:datastore-preferences:1.2.1")

    // The low-power periodic mode.
    implementation("androidx.work:work-runtime-ktx:2.12.0")

    debugImplementation("androidx.compose.ui:ui-tooling")

    testImplementation("junit:junit:4.13.2")

    // Named explicitly rather than via `kotlin("test")`: kotlin-test is published with a variant per
    // test framework, and this module's unit tests are JUnit4. Naming the artefact removes the
    // ambiguity instead of relying on variant resolution to guess it.
    testImplementation("org.jetbrains.kotlin:kotlin-test-junit:2.4.10")
    testImplementation("org.jetbrains.kotlinx:kotlinx-coroutines-test:$coroutinesVersion")

    // The identity tests need a real certificate authority and a real leaf signed by it. Generating
    // them at test time is what makes the "a replaced identity is refused" assertion meaningful, and
    // it keeps a private key out of the repository entirely.
    testImplementation("org.bouncycastle:bcpkix-jdk18on:1.86")

    // The fake Bridge lives in `sharedTest`, so the JVM tests need the same server toolkit the
    // instrumentation tests use. Sharing it is what lets the discovery-probe boundary tests run on
    // every build instead of only where an emulator exists.
    testImplementation("com.squareup.okhttp3:mockwebserver:5.5.0")

    androidTestImplementation("androidx.test:runner:1.7.0")
    androidTestImplementation("androidx.test:rules:1.7.0")
    androidTestImplementation("androidx.test:core-ktx:1.7.0")
    androidTestImplementation("androidx.test.ext:junit:1.3.0")
    androidTestImplementation("org.jetbrains.kotlin:kotlin-test-junit:2.4.10")
    androidTestImplementation("org.jetbrains.kotlinx:kotlinx-coroutines-test:$coroutinesVersion")

    // The Stage C runtime gate needs a real HTTPS server that presents a real chain, so the app's
    // pinning code runs against a genuine handshake rather than against a stub. MockWebServer can
    // serve TLS and upgrade to WebSocket, which is the whole of the v1 protocol surface.
    androidTestImplementation("com.squareup.okhttp3:mockwebserver:5.5.0")

    // The same certificate authority the JVM tests use, so the identity the fake Bridge presents is
    // generated by the same code the unit tests prove correct.
    androidTestImplementation("org.bouncycastle:bcpkix-jdk18on:1.86")

    // `work-testing` is deliberately not a dependency. The runtime gate drives the application's own
    // WorkManager rather than a test-initialised one, so that the worker factory the app ships with
    // is the thing under test — which is where a missing factory would otherwise hide.
}
