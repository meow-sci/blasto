# Game Feature

* [Warn when the running game build differs from the one a mod was tested on](overview.md) - How to read the running KSA version, compare it with a tested-version constant or a build stamp, and log both so bug reports carry their context

# How-To

* [Compare VersionInfo.Current with a tested-version constant at AllModsLoaded](check-the-game-version-1.md) - Warn, without blocking, when the running KSA version differs from the one the mod was tested on, using a single constant that CI can also read
* [Stamp the targeted KSA build into assembly metadata and compare it at run time](check-the-game-version-2.md) - Write the KSA build a mod was compiled against into AssemblyMetadata from a lock file at build time, read it back at startup and log it beside the running version
