---
type: Game Feature
title: Pin the KSA build with a hashed assemblies lock and notice new builds
description: Commit SHA-256 digests of the referenced game DLLs plus the build number so local builds, a private CI mirror and the running game agree on one KSA build, stamp that build into the mod and poll the version endpoint from a scheduled job
category: testing-and-tooling
group: game-updates
topic: pin-the-game-build-with-a-hashed-lock
demonstrated_by:
  - mods/ksarmory/features/ksa-assemblies-lock
  - mods/ksagolf/features/api-surface-and-assembly-lock
  - mods/ksacars/features/api-surface-pinning-and-diff
  - mods/ksastructures/features/headless-tests-and-ci
lifecycle: [startup:immediate-load]
game_types: [KSA.Vehicle, KSA.VersionInfo]
tags: [game-update, lock-file, sha256, version-check, ci, assemblies]
---

A KSA mod is built against assemblies that are not redistributable and that change between pre-release builds. Three copies of the game exist for any mod at once: the developer's, the private mirror CI compiles against, and the player's. A lock file makes the first two agree and lets the mod notice the third.
The analysed mods commit a text file of the build number and per-DLL SHA-256 digests (safe in a public repository: only names and hashes), compare it in scripts and in CI, stamp the build into the compiled assembly, and poll the game's own version endpoint on a schedule.

## Engine background

| Piece | Where | Use |
|---|---|---|
| `VersionInfo.MASTER_URL`, `VERSION_URI` | [current/decomp/KSA/VersionInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VersionInfo.cs#L25-L27) | The master-server endpoint the game itself asks for the published version |
| `VersionMetaInfo` | [current/decomp/KSA/VersionMetaInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VersionMetaInfo.cs) | The response shape: `Version` and `Url` |
| Update comparison | [current/decomp/KSA/VersionInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VersionInfo.cs#L58-L80) | The game compares the master's version with its own and shows an update popup |
| Running build | `typeof(KSA.Vehicle).Assembly.GetName().Version` | The loaded game's own assembly version, readable at mod load |
| Mod startup | [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) | `StarMapImmediateLoad` is a safe place to log and compare the two builds |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [ksa-assemblies.lock, check-assemblies.sh and AssemblyMetadata from the lock](pin-the-game-build-with-a-hashed-lock.md) | ksarmory ([ksa-assemblies.lock](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/ksa-assemblies.lock#L1-L5)) | `msbuild` | `startup:immediate-load` | Catches drift between developer, CI and player; the version endpoint is an undocumented public URL that may change, so the watch must not fail the build |

## See also

- [Upgrade a mod to a new KSA build](../upgrade-a-mod-to-a-new-ksa-build/overview.md), [Track the game API your mod uses](../track-the-game-api-your-mod-uses/overview.md), [Keep a searchable decompiled copy of the game next to your mod](../decompile-the-game-for-reference/overview.md)
- [Reference the game assemblies from a mod project](../../../getting-started/build/reference-game-assemblies-from-a-mod-project/overview.md), [Build in CI without the game installed](../../../getting-started/build/build-in-ci-without-the-game-installed/overview.md), [Check the game version](../../../getting-started/lifecycle/check-the-game-version/overview.md), [Validate patch targets and detect game-update breakage](../../../patching/safety/validate-patch-targets-and-detect-game-update-breakage/overview.md)
- Mod features: [KSArmory KSA assemblies lock](../../../../mods/ksarmory/features/ksa-assemblies-lock.md), [KSAGolf API surface and assembly lock](../../../../mods/ksagolf/features/api-surface-and-assembly-lock.md), [KSACars API surface pinning and diff](../../../../mods/ksacars/features/api-surface-pinning-and-diff.md)
