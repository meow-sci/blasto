# Game Feature

* [Pin the KSA build with a hashed assemblies lock and notice new builds](overview.md) - Commit SHA-256 digests of the referenced game DLLs plus the build number so local builds, a private CI mirror and the running game agree on one KSA build, stamp that build into the mod and poll the version endpoint from a scheduled job

# How-To

* [Pin the KSA build with a hashed assemblies lock, stamp it into the mod and watch for new builds](pin-the-game-build-with-a-hashed-lock.md) - Commit SHA-256 digests of the referenced game DLLs plus the build number so local builds, a private CI mirror and the running game agree on one KSA build, and poll the version endpoint on a schedule to learn of a new build early
