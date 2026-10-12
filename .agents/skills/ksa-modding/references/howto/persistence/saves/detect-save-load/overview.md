---
type: Game Feature
title: Detect a save load or quickload and reset mod state safely
description: What a load destroys and keeps, the signals a mod can watch, and six ways mods react - latch on OnGameLoaded, pin the target, observe UncompressedSave.Load, postfix DeserializeSave, infer from the clock, or rebuild from the live world
category: persistence
group: saves
topic: detect-save-load
demonstrated_by:
  - mods/ksarmory/features/detect-save-load-ongameloaded
  - mods/ksagolf/features/save-load-detection
  - mods/ksastructures/features/save-load-detection
  - mods/ksa-advancedflightcomputer/features/save-scoped-sidecar-registries
  - mods/ohmystars/features/save-scoped-settings-sidecar
  - mods/ksa-measuretools/features/live-body-relative-anchors
  - mods/ksa-public-telemetry/features/mission-clock-and-liftoff-detection
  - mods/ksacars/features/frame-step-latch-and-car-registry
lifecycle: [startup:all-mods-loaded, event:load, frame:draw-ui, frame:after-on-frame]
game_types:
  - KSA.Program
  - KSA.Universe
  - KSA.UncompressedSave
  - KSA.GameSaves
  - KSA.CelestialSystem
  - KSA.Vehicle
  - KSA.Part
tags: [saves, load, quickload, harmony, state-reset, universe-clock]
---

StarMap has no load hook, and the flight scene is not recreated on a load: KSA builds one `CelestialSystem` at startup and a save only replaces what is inside it. A mod that holds `Vehicle`, body, `Part` or camera references, or integrates a clock, therefore cannot use "the scene changed" as its signal. It has to notice the load itself, or avoid needing to.

## Engine background

A load runs entirely inside `UncompressedSave.Load` ([current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L45-L83)), on the main thread, in whatever frame phase asked for it (a confirm popup drawn by `Popup.DrawAll` at the top of `Program.OnDrawUiFrame`, the console `load` action, or a mod calling `GameSaves.LoadSaveGame`):

| Step | Code | What a mod can rely on afterwards |
|---|---|---|
| Refuse | `GameSaves.RefusedInEditor()` | Nothing changed, `Load` returned normally |
| Read | `UniverseData = UniverseData.LoadFrom(this)` | On an exception, logged, `Load` returns normally; `GameSave.UniverseData` still holds the previous object |
| Replace the world | `Universe.DeserializeSave(UniverseData)` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430-L2478) | See below |
| Notify | `Program.OnGameLoaded()` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2599-L2606) | Only closes the Esc menu and the settings, save and vehicle-save windows and clears the menu pause |

`Universe.DeserializeSave` sets the simulation speed to 0, joins the workers (`JobSystems.WaitAll()`), resets emitters, plume trails and explosion volumes, sets `Program.EditorFlag = false`, calls `CurrentSystem.DestroyAllVehicles()` (every vehicle is disposed), destroys the physics bubbles, clears the clutter exclusions, unfollows the base and map camera of every game viewport, resets the last and next sim steps to a zero-length step at the save's `GameTime`, resets the running id counter, re-places the celestials at that time, rebuilds vehicles from the save ([current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L663-L720)), restores the kitten roster, calls `Program.RefreshVehiclesInFrame()`, applies the saved camera ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L832-L849)) and finally sets the speed to 1.0.

What stays the same object: `Universe.CurrentSystem` and the celestial bodies. What dies: every `Vehicle` (and its `Part`s), every viewport follow target, the sim-step boundary and `Part.InstanceId` counters. A disposed vehicle keeps its flight computer and burn plan intact, so a kept `Burn` or `FlightPlan` reference still looks alive (see approach 3).

`Program.OnGameLoaded` has one caller in this build, `UncompressedSave.Load`. A second path that replaces the world exists: the multiplayer client calls `Universe.DeserializeSave(message.UniverseData)` when the server accepts a join ([current/decomp/KSA.Networking/NetworkClient.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Networking/NetworkClient.cs#L108-L129)) and does not call `OnGameLoaded` or `UncompressedSave.Load`. A mod that must cover that path patches `DeserializeSave` itself (approach 4).

Phase placement is in the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) (row 15 of the startup sequence: "Loading a save").

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Postfix Program.OnGameLoaded, latch a flag, consume it in the next frame step and forget the world](./detect-save-load-1.md) | KSArmory, KSAGolf | `harmony-postfix` | `startup:all-mods-loaded`, `event:load`, `frame:draw-ui` | One patch, one flag, teardown on your own schedule; includes the "what to drop and why Discard, not Clear" table |
| [Same latch with the target pinned at compile time](./detect-save-load-2.md) | KSAStructures | `harmony-postfix` | `startup:all-mods-loaded`, `event:load` | A signature change becomes a build error; degrade-not-break when the target is missing |
| [Observe UncompressedSave.Load, compare the UniverseData reference and reset a list of save-lifetime caches](./detect-save-load-3.md) | KSA-AdvancedFlightComputer, OhMyStars | `harmony-prefix` | `event:load` | Ignores refused and failed loads; gives the loaded save id; one registry of caches to reset |
| [Postfix Universe.DeserializeSave when the CelestialSystem object survives](./detect-save-load-4.md) | KSA-MeasureTools | `harmony-postfix` | `event:load` | Clears synchronously inside the load; also covers the multiplayer join path |
| [Infer a reload from universe time running backwards](./detect-save-load-5.md) | KSA-Public-Telemetry | `starmap-lifecycle` | `frame:after-on-frame` | No patch; misses a load to a later time |
| [Do not detect at all: rebuild a derived registry from the live world each frame](./detect-save-load-6.md) | KSACars | `starmap-lifecycle` | `frame:draw-ui`, `frame:after-on-frame` | Nothing to miss; only valid when the world can reproduce the state |

## See also

- [Save a whole mod's scene state as versioned sidecar records, with reset and replay on load](../save-mod-state-with-the-scene/overview.md), [Store per-save mod data next to a KSA save and notice when the player saves](../save-mod-state-with-the-game-save/overview.md)
- [Create and load native KSA saves from mod code](../save-and-load-the-game-from-code/overview.md)
- [Mutate vehicles at a physics-safe point](../../../physics/solver/mutate-vehicles-at-a-physics-safe-point/overview.md), [Keep mod UI and per-frame work running when the HUD is hidden (F2)](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md)
- [Cache and key per-body data so it survives system reloads, repeated ids and extra star systems](../../../celestials/systems-and-stars/cache-and-key-body-data-per-loaded-system/overview.md), [Tracking which craft carry your part, classifying them and detecting staging and loss](../../../vehicles-and-parts/craft-tracking/track-and-classify-craft/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
