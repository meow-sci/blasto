---
type: Game Feature
title: Harmony finalizers, cleanup and scoped overrides
description: Why a postfix does not run when the original throws and a finalizer does, how KSA mods use finalizers to restore state and swallow failures, and how to scope a patch on a shared method to the mod's own calls
category: patching
group: harmony
topic: run-cleanup-or-recover-from-exceptions-with-a-finalizer
demonstrated_by:
  - mods/unscience/features/garrys-torch-scoped-collision-suppression
  - mods/unscience/features/lib-iva-force-render
  - mods/ksa-advancedflightcomputer/features/hyperbolic-transfer-targets
  - mods/compendium/features/flight-computer-root-attitude-tracking
  - mods/unscience/features/lib-native-save-hooks
  - mods/unscience/features/pyro-scoped-exhaust-look-override
  - mods/unscience/features/pebbles-private-clutter-materials
  - mods/ksastructures/features/terrain-height-roof-override
lifecycle:
  - physics:bepu
  - frame:render
  - frame:draw-ui
  - physics:vehicle-solver
  - event:save
  - frame:pre-render
  - frame:dispatch-solvers
game_types:
  - KSA.ConstraintSim
  - KSA.PartTreeRenderData
  - KSA.TransferPlanner
  - KSA.FlightComputer
  - KSA.UncompressedSave
  - KSA.Universe
  - KSA.VolumetricExhaustRenderer
  - KSA.Vehicle
  - KSA.Celestial
  - KSA.ClutterCubeCellGrid
tags: [harmony, finalizer, postfix, cleanup, exception, scoped-override, thread-static, state]
---

A Harmony postfix runs only if the original method returned. If the original throws, the postfix is skipped. A
finalizer always runs. That one difference gives KSA mods four jobs for finalizers: put back state a prefix changed,
even if the game call fails; finish a method that throws on data it was never meant to see; put back an edited input
struct using `__state`; and bracket load or save transactions so failures are reported. It also gives them a way to
*scope* a patch on a shared method to the mod's own calls, with a mark that a prefix sets and a finalizer (or a
`finally`) clears.

## Engine background

| Type | Member | Why a finalizer is involved |
|---|---|---|
| `ConstraintSim` | `DetectCollisions`, `Simulate` | Bracket for every Bepu collision pass; both rethrow after poisoning the sim ([current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L870-L902)[^ksa-constraintsim]) |
| `PartTreeRenderData` | `Compose` | Applies the interior/IVA gate ([current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L1258-L1303)[^ksa-parttreerenderdata]) |
| `TransferPlanner` | private static `SetTransferInfo` | Throws on a NaN `Period` after storing most of its state ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1573-L1603)[^ksa-transferplanner]) |
| `FlightComputer` | `ComputeControl(ref readonly FlightComputerNavigation, ...)` | Runs on solver threads per tick ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L400-L404)[^ksa-flightcomputer]) |
| `UncompressedSave`, `Universe` | `Load`, `Write`, `DeserializeSave`, `LoadSystem` | Swallow read failures or return `false` ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L45-L135)[^ksa-uncompressedsave], [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430-L2440)[^ksa-universe]) |
| `VolumetricExhaustRenderer` | `AddInstance(ExhaustInstance, int)` | Shared final enqueue of every plume ([current/decomp/KSA/VolumetricExhaustRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustRenderer.cs#L787-L792)[^ksa-volumetricexhaustrenderer]) |
| `Vehicle`, `Celestial` | `PrepareWorker`, `TeleportToLocation`, `GetTerrainHeightFromDirCce` | Who-is-asking marker and lent value ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2398)[^ksa-vehicle], [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L777-L781)[^ksa-celestial]) |
| `ClutterEcotypeRenderData`, `GroundClutterPlacementData`, `ClutterCubeCellGrid` | constructors, `RebuildFrameResources` | Scoped construction context ([current/decomp/KSA/ClutterEcotypeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ClutterEcotypeRenderData.cs#L932-L934)[^ksa-clutterecotyperenderdata]) |

Phases involved, from [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md): the vehicle solver and Bepu run on worker threads between `ExecuteNextVehicleSolvers`
and the next frame's `ApplyVehicleSolvers` (`physics:vehicle-solver`, `physics:bepu`, see [Run code inside the physics step on worker threads](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md));
`frame:dispatch-solvers`, `frame:draw-ui` and `frame:render` are main-thread frame phases; saves and loads are
`event:save` hooks that must be replayed at the `PrepareFrame` handoff ([Mutate simulation state safely relative to the physics step](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md)).

## Finalizer semantics (checked against Lib.Harmony 2.4.2)

StarMap references Lib.Harmony 2.4.2 ([StarMap.Core/StarMap.Core.csproj](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMap.Core.csproj#L11)[^starmap-starmap-core]). A throwaway console test
against that version, using one target method per row, showed:

| Situation | Result |
|---|---|
| Original returns normally | prefix, original, postfix and finalizer all run; `__state` set by the prefix reaches both the postfix and the finalizer |
| Original throws, void finalizer | finalizer runs and sees the exception; the caller still receives it; the postfix does not run |
| Original throws, finalizer returns `__exception` | same as above (explicitly rethrows) |
| Original throws, finalizer returns `null` | exception swallowed; a non-void method returns `default`; the postfix does not run |
| A prefix throws | the original is not called; the finalizer still runs and sees the prefix's exception |
| A prefix returns `false` | the original is skipped, the postfix and finalizer run; the postfix sees `__runOriginal == false` |
| Several finalizers or prefixes | all run in priority order, `Priority.First` to `Priority.Last` |

```text
// observed output of a local test against Lib.Harmony 2.4.2 (not part of any mod repository)
C: prefix throws, finalizer swallows
  prefix throws
  finalizer sees ApplicationException
=> 0
```

## Scoped overrides

A patch on a shared method changes behaviour for every caller. The mods here limit the effect to their own
calls by making the patch conditional on a context that only their own code sets:

| Context | Set by | Cleared by | Approach |
|---|---|---|---|
| The game call itself (state in `__state`) | prefix | finalizer | 1, 3, 4 |
| A `[ThreadStatic]` flag | the mod's own call site | the mod's `try`/`finally` | 5 |
| A `[ThreadStatic]` construction context | a `using` around the mod's own build loop, re-entered by a prefix | a finalizer that returns `__exception` | 6 |
| An "asker" marker | a `Priority.First` prefix | a `Priority.Last` finalizer | 7 |

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [Restore what a prefix changed around one game call](run-cleanup-or-recover-from-exceptions-with-a-finalizer-1.md) | unscience | Prefix removes state, finalizer restores it via `__state` | `physics:vehicle-solver`, `physics:bepu`, `frame:render` | Robust to exceptions; hot path must stay allocation-free |
| 2 | [Swallow an exception and finish the method's remaining work](run-cleanup-or-recover-from-exceptions-with-a-finalizer-2.md) | KSA-AdvancedFlightComputer | Finalizer swallows an `ArgumentException` and replays the method's tail | `frame:draw-ui` | Copies stock lines that can drift |
| 3 | [Prefix with __state and a finalizer that restores an edited input](run-cleanup-or-recover-from-exceptions-with-a-finalizer-3.md) | Compendium | Prefix injects into a by-ref struct, `__state` says to put it back | `physics:vehicle-solver` | Worker-thread constraints on everything the prefix reads |
| 4 | [Prefix, postfix and finalizer as a transaction around loads](run-cleanup-or-recover-from-exceptions-with-a-finalizer-4.md) | unscience | Prefix, postfix and finalizer as a load/save transaction | `event:save` | Reports failures the game swallows; defers loads |
| 5 | [ThreadStatic override set in try/finally around the mod's own submission](run-cleanup-or-recover-from-exceptions-with-a-finalizer-5.md) | unscience | `[ThreadStatic]` override set in `try`/`finally` at the mod's call site | `frame:pre-render` | Needs a synchronous call on one thread |
| 6 | [Thread-static construction context scoping a transpiler and a prefix](run-cleanup-or-recover-from-exceptions-with-a-finalizer-6.md) | unscience | `[ThreadStatic]` construction context scoping a transpiler and prefixes | `startup:asset-load`, `frame:render` | Exact-count transpiler; several moving parts |
| 7 | [Mark the asker in a Priority.First prefix, clear it in a Priority.Last finalizer](run-cleanup-or-recover-from-exceptions-with-a-finalizer-7.md) | KSAStructures | Marker in a `Priority.First` prefix, cleared in a `Priority.Last` finalizer | `frame:dispatch-solvers` | Answer depends on who asks; not a pure function |

## See also

- [Changing behaviour of a game method marked AggressiveInlining](../../internals/patch-around-aggressive-inlining/overview.md) for why approach 1 brackets `ConstraintSim` instead of the narrow phase
- [Patching game methods that run on worker and pool threads](../patch-methods-that-run-on-background-threads/overview.md) for the worker-side rules that approaches 1 and 3 follow
- [Coexisting with other mods that patch the same method](../coexist-with-other-mods-patching-the-same-method/overview.md) for priorities
- Related tasks: suppress-collisions-for-one-vessel, synthesize-a-navigation-target-for-the-flight-computer, save-mod-state-with-the-game-save, write-worker-thread-safe-solver-patches, override-terrain-height-for-roofs-and-platforms
- [Patching private and overloaded game methods by name](../../internals/patch-a-private-game-method-by-name/overview.md)
- [Disable collisions for one vessel without touching others](../../../physics/colliders/suppress-collisions-for-one-vessel/overview.md) - related topic
- [Synthesize a NavigationTarget for an object that has no orbit](../../../flight-and-orbits/autopilot/synthesize-a-navigation-target-for-the-flight-computer/overview.md) - related topic
- [Store per-save mod data next to a KSA save and notice when the player saves](../../../persistence/saves/save-mod-state-with-the-game-save/overview.md) - related topic
- [Write worker-thread-safe patches inside the vehicle solver](../../../physics/solver/write-worker-thread-safe-solver-patches/overview.md) - related topic
- [Override the terrain height cameras and wheels see](../../../physics/aero-and-ground/override-terrain-height-for-roofs-and-platforms/overview.md) - related topic

[^ksa-constraintsim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^ksa-parttreerenderdata]: KSA.PartTreeRenderData (decompiled, build 2026.10.10.5554)
[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-uncompressedsave]: KSA.UncompressedSave (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-volumetricexhaustrenderer]: KSA.VolumetricExhaustRenderer (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-clutterecotyperenderdata]: KSA.ClutterEcotypeRenderData (decompiled, build 2026.10.10.5554)
[^starmap-starmap-core]: StarMap/StarMap.Core/StarMap.Core.csproj
