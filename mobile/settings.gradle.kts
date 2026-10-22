pluginManagement {
    repositories {
        mavenCentral()
        google { content { includeGroupByRegex("com\\.android.*"); includeGroupByRegex("androidx.*"); includeGroup("com.google.testing.platform") } }
        gradlePluginPortal()
    }
}
dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google { content { includeGroupByRegex("com\\.android.*"); includeGroupByRegex("androidx.*"); includeGroup("com.google.testing.platform") } }
        mavenCentral()
        exclusiveContent {
            forRepository { maven("https://build-artifacts.signal.org/libraries/maven/") }
            filter { includeGroup("org.signal") }
        }
    }
}
rootProject.name = "KnitERPMessenger"
include(":core", ":network", ":app")
