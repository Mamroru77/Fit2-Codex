// The Android app for the Codex Quota Bridge.
//
// Only :app exists in V1. The version catalog is intentionally absent: there is one module and one
// version of each plugin, so the indirection would cost more than it saves.

pluginManagement {
    repositories {
        // Google first for the Android Gradle Plugin and AndroidX; Maven Central for everything else.
        google {
            content {
                includeGroupByRegex("com\\.android.*")
                includeGroupByRegex("com\\.google.*")
                includeGroupByRegex("androidx.*")
            }
        }
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    // A module may not add its own repositories: every dependency comes from here, so a stray
    // repository cannot silently widen where the app's code comes from.
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)

    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "CodexQuota"
include(":app")
