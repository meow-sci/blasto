---
type: Game Feature
title: Save a whole mod's scene state as versioned sidecar records, with reset and replay on load
description: How to persist many features with native objects to rebuild - versioned JSON records per feature, validated before the world is touched, reset in reverse order and replayed in order - and the patches that make it work
category: persistence
group: saves
topic: save-mod-state-with-the-scene
demonstrated_by:
  - mods/unscience/features/lib-scene-save-coordinator
  - mods/unscience/features/camera-controller-override-keyframe-sequences
  - mods/unscience/features/kitten-animations-locomotion-tuning
  - mods/unscience/features/unscience-scene-save-sidecar
  - mods/unscience/features/lib-native-save-hooks
lifecycle: [startup:all-mods-loaded, event:save, event:load, frame:prepare, frame:draw-ui, frame:input]
game_types:
  - KSA.GameSave
  - KSA.UncompressedSave
  - KSA.Universe
  - KSA.UniverseData
  - KSA.GameSaves
  - KSA.JobSystems
  - KSA.Program
tags: [saves, scene, sidecar, json, harmony, versioning, transactional-load]
---

A mod with a handful of settings can use a single sidecar file ([Store per-save mod data next to a KSA save and notice when the player saves](../save-mod-state-with-the-game-save/overview.md)). A mod with dozens of features that own native objects (cameras, lights, decals, welds, scaled craft) needs more: each feature saves its own record, a record the running build cannot restore must not be lost, and the world has to be torn down and rebuilt in a defined order. unscience solves this in three layers, each a separate article here.

## Engine background

| Type and member | What it does for this problem |
|---|---|
| `GameSave.Populate()` (virtual) | `UniverseData = UniverseData.Create()`; the single point at which the universe is snapshotted for a save ([current/decomp/KSA/GameSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSave.cs#L37-L40)) |
| `UncompressedSave.Write()` | Returns `bool`; deletes and recreates the save folder, then writes `universe.xml` ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L108-L139)) |
| `UncompressedSave.Load()` | Reads `UniverseData`, then `Universe.DeserializeSave`, then `Program.OnGameLoaded()`; since build 5554 an unreadable file is logged and `Load` returns without touching the world ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L45-L83)) |
| `Universe.DeserializeSave(UniverseData)` | `JobSystems.WaitAll()` and then destroys vehicles and bubbles, rebuilds the system, restores the kitten roster and camera ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430-L2478)) |
| `Universe.LoadSystem(string)` | Builds a new `CelestialSystem`; startup calls it from `LoadDefaultSystem` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L232-L260)) |
| `Program.PrepareFrame` | Waits on the four job systems, applies solver results (`Apply*Solvers`), then `GetJobSimStep` and `ExecuteNext*Solvers` dispatch the next step ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2225)). Between apply and dispatch is the only main-thread window in which a world can be replaced without a worker owning it |
| Multiplayer client join | Calls `Universe.DeserializeSave` directly, without `UncompressedSave.Load` ([current/decomp/KSA.Networking/NetworkClient.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Networking/NetworkClient.cs#L108-L129)) |

A load can be triggered from three frame phases: the console (`frame:input`, inside `Glfw.PollEvents` and the key handler), a confirm popup in the save window (`frame:draw-ui`, `Popup.DrawAll` at the top of `OnDrawUiFrame`), or a mod calling `GameSaves.LoadSaveGame` (see [Create and load native KSA saves from mod code](../save-and-load-the-game-from-code/overview.md)). Replacing the world synchronously from a late UI phase would release textures and native objects that the same frame's ImGui draw lists still reference, which is why approach 3 defers every load to the `PrepareFrame` handoff. Phase terms are defined in the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

Read them in order: 1 is the contract and the state machine, 2 the host that connects it to a save, 3 the patches underneath.

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [ISaveParticipant records through a game-independent coordinator](./save-mod-state-with-the-scene-1.md) | unscience | `save-sidecar` | `event:save`, `event:load`, `frame:prepare` | Records are versioned, validated before the world is touched, retained when unrestorable; the whole core compiles without KSA so it is testable |
| [Host wiring: pair each capture with its write by object identity](./save-mod-state-with-the-scene-2.md) | unscience | `save-sidecar` | `event:save`, `event:load` | A write without its capture is reported, not filled with fresh state; a missing sidecar means reset everything and restore nothing |
| [Eight static callbacks over five patched methods, loads replayed at the frame handoff](./save-mod-state-with-the-scene-3.md) | unscience | `harmony-prefix` | `startup:all-mods-loaded`, `frame:prepare`, `event:save`, `event:load` | Deferring every load changes when `LoadSaveGame` takes effect for all mods; exact-signature binding fails loudly on a game update |

## See also

- [Identify vessels and parts stably across saves](../identify-vessels-and-parts-across-saves/overview.md), [Detect a save load or quickload and reset mod state safely](../detect-save-load/overview.md), [Store per-save mod data next to a KSA save and notice when the player saves](../save-mod-state-with-the-game-save/overview.md)
- [Keep grass, rocks and trees off a custom site, and replace or preserve a planet's ground clutter](../../../celestials/terrain/exclude-or-replace-ground-clutter/overview.md), [Animate the flight camera with eased moves, keyframe sequences and saved recipes](../../../camera/special-views/chain-camera-moves-into-keyframe-sequences/overview.md), [Restore kitten colours, clips and tuning after a save load](../../../kittens/characters/restore-kitten-customizations-after-a-load/overview.md)
- [Mutate vehicles at a physics-safe point](../../../physics/solver/mutate-vehicles-at-a-physics-safe-point/overview.md), [Create and load native KSA saves from mod code](../save-and-load-the-game-from-code/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
