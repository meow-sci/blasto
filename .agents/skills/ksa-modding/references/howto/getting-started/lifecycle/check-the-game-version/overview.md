---
type: Game Feature
title: Warn when the running game build differs from the one a mod was tested on
description: How to read the running KSA version, compare it with a tested-version constant or a build stamp, and log both so bug reports carry their context
category: getting-started
group: lifecycle
topic: check-the-game-version
demonstrated_by:
  - mods/ksaero/features/game-version-check
  - mods/ksa-measuretools/features/resilient-patching-and-diagnostics
  - mods/ksagolf/features/mod-log-file-with-rotation
  - mods/ksacars/features/mod-log-file
  - mods/ksarmory/features/own-log-file-and-developer-switch
lifecycle: [startup:all-mods-loaded, startup:immediate-load]
game_types: [KSA.VersionInfo, KSA.Vehicle]
tags: [version-check, versioninfo, build-stamp, tested-version, diagnostics]
---

KSA changes between builds, and a StarMap mod that patches game internals can break without any compile error.
Mods therefore record the build they were tested on and compare it with the running build, as an advisory warning
and as context in every log. Two ways: a string constant compared at runtime, and a build stamp written from a lock
file and read back from assembly metadata.

## Engine background

`KSA.VersionInfo` ([current/decomp/KSA/VersionInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VersionInfo.cs#L1-L190)) holds the running version in
`VersionInfo.Current`, a `static readonly` instance built from the `KSA` assembly's `AssemblyInformationalVersionAttribute`
(`2026.10.10.5554+<commit>` in the analysed build). Its `VersionString` is `v{Major}.{Minor}.{Build}.{Revision}{Suffix}`
with a leading `v` and the `+commit` dropped. It implements `IComparable<VersionInfo>`, and `CompareTo` checks
`Revision` first, then `Build`, then `Suffix`. The build date and change list are in `current/version.json`.[current/version.json](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/version.json)

Both approaches run during startup ([Game loop and mod lifecycle](../game-loop-and-lifecycle/overview.md)): the comparison at `startup:all-mods-loaded`, the
stamped build logged at `startup:immediate-load`.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Runtime VersionInfo compare at AllModsLoaded](./check-the-game-version-1.md) | KSAero, KSA-MeasureTools | `starmap-lifecycle` | `startup:all-mods-loaded` | One constant, one comparison; the constant can be read by CI; warns on every game update |
| [Stamp the targeted build into assembly metadata](./check-the-game-version-2.md) | KSAGolf, KSACars, KSArmory | `msbuild` | `startup:immediate-load` | The build is written down once in a lock file shared with CI; relies on the KSA assembly's `Version` for the running build (equal to the build string in 5554, not guaranteed later) |

## Rules

- A mismatch is advisory. Structural checks of the patch target decide whether features run (see
  `isolate-patch-failures-and-fault-proof-patch-bodies`).
- Log both values first so every bug report states them ([Log from a mod: per-mod log file, game log category and log-once](../write-a-per-mod-log-file/overview.md)).
- Keep one source of truth that release tooling can also read ([Package and publish a mod release (zip, CI publishing, installer)](../../build/package-and-publish-a-mod-release/overview.md)).

## See also

- [Log from a mod: per-mod log file, game log category and log-once](../write-a-per-mod-log-file/overview.md), [Choose and use the right StarMap lifecycle hook](../choose-a-starmap-startup-hook/overview.md), [Package and publish a mod release (zip, CI publishing, installer)](../../build/package-and-publish-a-mod-release/overview.md)
- [Game version check](../../../../mods/ksaero/features/game-version-check.md), [Per-patch Harmony application, drift checks, once-only logging and debug perf scopes](../../../../mods/ksa-measuretools/features/resilient-patching-and-diagnostics.md),
  [Mod log file with rotation and a verbose toggle](../../../../mods/ksagolf/features/mod-log-file-with-rotation.md), [Per-mod log file, build stamp and fault limiter](../../../../mods/ksacars/features/mod-log-file.md),
  [Own log file, build stamps and a developer marker file](../../../../mods/ksarmory/features/own-log-file-and-developer-switch.md)
- [Isolating patch failures and keeping patch bodies from throwing](../../../patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md) - related topic

