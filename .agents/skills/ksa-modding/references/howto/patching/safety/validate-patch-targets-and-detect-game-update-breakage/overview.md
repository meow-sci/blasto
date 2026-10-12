---
type: Game Feature
title: Validating patch targets and detecting game-update breakage
description: How KSA mods resolve private game members in one validated place, let each feature refuse to load when a member moved, and make signature drift visible at build time by pinning public patch targets
category: patching
group: safety
topic: validate-patch-targets-and-detect-game-update-breakage
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/validated-reflection-handles
  - mods/unscience/features/parts-now-reflection-self-test
  - mods/ksagolf/features/degrading-harmony-patches
lifecycle: [startup:immediate-load, startup:all-mods-loaded, frame:draw-ui]
game_types:
  - KSA.TransferPlanner
  - KSA.Vehicle
  - KSA.FlightComputer
  - KSA.GameSettings
  - KSA.ModLibrary
  - KSA.SerializedCollection
  - KSA.VehicleEditor
  - KSA.Program
  - KSA.Universe
tags: [reflection, validation, game-update, drift, pin, patching]
---

KSA changes every few days and a mod that reaches into private members will meet a build where one of them moved. Two
questions follow: how does the mod find out before the player does, and what does it do about it? KSA mods answer the first in
two layers. At run time they resolve every private field and method in one place at startup, tag each with the features that
need it, and let each feature refuse to load or the mod report a degraded state when one is gone. At build time they pin public
patch targets with never-called members so a signature change fails the compile. Build-level drift detection beyond that (hashed
assembly lock, external member surface, coupling catalog) is covered in the testing-and-tooling category.

## Engine background

| Type | Member | Why it drifts |
|---|---|---|
| `TransferPlanner` | private static fields and `SetTransferInfo` | Private planner state ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1573-L1603)[^ksa-transferplanner]) |
| `FlightComputer` | private `UpdateAttitudeTarget` | Called by `ComputeControl` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1264-L1277)[^ksa-flightcomputer]) |
| `ModLibrary` | `internal static readonly` registries | No public API for removal ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L68-L86)[^ksa-modlibrary]) |
| `SerializedCollection<T>` | private `_collection` | Backing dictionary ([current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L14)[^ksa-serializedcollection]) |
| `Program`, `Universe` | `OnGameLoaded`, `ExecuteNextVehicleSolvers` | Public targets that can be pinned ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2599)[^ksa-program], [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2126)[^ksa-universe]) |

Phases from [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md): handle resolution can start in `[StarMapImmediateLoad]` (`startup:immediate-load`); validation and patching
belong in `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`); status is shown in `frame:draw-ui`.

## What compile-time pinning relies on

A call to a member emits a member reference into the calling assembly; `nameof` does not. A throwaway test with a stub game
assembly confirmed it with `ilspycmd -il` (see [Pin patched members with a never-called method that really calls them](validate-patch-targets-and-detect-game-update-breakage-3.md)). The member reference is what lets a build step record a mod's external
surface and what makes a changed signature a compile error. A private member has no public reference to emit.

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [Feature-tagged handle registry with a Validate per feature](validate-patch-targets-and-detect-game-update-breakage-1.md) | KSA-AdvancedFlightComputer | Feature-tagged handle registry with `ValidateX` per feature | `startup:immediate-load`, `startup:all-mods-loaded` | Strong; needs discipline to tag every handle |
| 2 | [Non-throwing static constructor with fatal and degraded lists](validate-patch-targets-and-detect-game-update-breakage-2.md) | unscience (Parts Now) | Non-throwing static constructor, fatal and degraded lists, banner | `startup:all-mods-loaded`, `frame:draw-ui` | Simple; coarse health gate |
| 3 | [Pin patched members with a never-called method that really calls them](validate-patch-targets-and-detect-game-update-breakage-3.md) | KSAGolf | Never-called method that really calls the public target | build time | Catches signature change at compile time; public members only |

## See also

- [Refusing a patch and rolling back when the game changes](../refuse-a-patch-and-roll-back-when-the-game-changes/overview.md) for acting on a failed validation
- [Isolating patch failures and keeping patch bodies from throwing](../isolate-patch-failures-and-fault-proof-patch-bodies/overview.md) for the per-class degrade used with approach 3
- [Reading, writing and calling private game members](../../internals/call-private-game-members-through-reflection-and-delegates/overview.md) for the reflection forms
- Related topics: check-the-game-version, test-harmony-patches-without-the-game, load-and-unload-part-mods-at-runtime, pin-the-game-build-with-a-hashed-lock, upgrade-a-mod-to-a-new-ksa-build
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Warn when the running game build differs from the one a mod was tested on](../../../getting-started/lifecycle/check-the-game-version/overview.md) - related topic
- [Test Harmony patches without running the game](../../../testing-and-tooling/offline-tests/test-harmony-patches-without-the-game/overview.md) - related topic
- [Loading, validating and unloading part mods in a running game](../../../vehicles-and-parts/parts/load-and-unload-part-mods-at-runtime/overview.md) - related topic
- [Pin the KSA build with a hashed assemblies lock and notice new builds](../../../testing-and-tooling/game-updates/pin-the-game-build-with-a-hashed-lock/overview.md) - related topic
- [Upgrade a mod to a new KSA build and degrade gracefully when the API drifts](../../../testing-and-tooling/game-updates/upgrade-a-mod-to-a-new-ksa-build/overview.md) - related topic

[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-serializedcollection]: KSA.SerializedCollection (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
