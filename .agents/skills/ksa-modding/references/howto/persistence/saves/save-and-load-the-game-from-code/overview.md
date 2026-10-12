---
type: Game Feature
title: Create and load native KSA saves from mod code
description: The game methods that write and read a save, and three ways mods call them - a timed autosave, a command-file load that waits for a craft, and a scenario runner that loads after startup
category: persistence
group: saves
topic: save-and-load-the-game-from-code
demonstrated_by:
  - mods/unscience/features/save-yourself-timed-autosave
  - mods/ksagolf/features/file-command-bridge
  - mods/ksastructures/features/dev-file-bridge-and-mcp
  - mods/ksacars/features/file-drop-command-bridge
  - mods/ksarmory/features/unattended-scenario-runner
lifecycle: [frame:draw-ui, event:save, frame:after-on-frame, event:load]
game_types: [KSA.GameSaves, KSA.UncompressedSave, KSA.GameSave, KSA.SaveName, KSA.Program, KSA.Universe]
tags: [saves, load, autosave, save-name, harness, game-saves]
---

KSA has two public entry points for persistence and both are ordinary static methods that a mod can call: `GameSaves.MakeUncompressedSave(name)` (the console `save` command) writes a save, and `GameSaves.LoadSaveGame(name)` (the console `load` command) reads one. A mod that calls them, instead of serialising the world itself, gets everything a manual save gets: `universe.xml`, the save list refresh, and every other mod's save and load patches (see [Save a whole mod's scene state as versioned sidecar records, with reset and replay on load](../save-mod-state-with-the-scene/overview.md) and [Store per-save mod data next to a KSA save and notice when the player saves](../save-mod-state-with-the-game-save/overview.md)).

## Engine background

| Type and member | What it does |
|---|---|
| `GameSaves.MakeUncompressedSave(string)` [current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L273-L280) | `[TerminalAction("save")]`. Returns early if `RefusedInEditor()`, sanitises the name with `SaveName.TryAccept`, then `UncompressedSave.Make(name)` |
| `GameSaves.RefusedInEditor()` [current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L198-L207) | Logs "Cannot save or load the game while the vehicle editor is open" and raises a `TimedAlert` when `Program.IsEditorOpen` |
| `SaveName.TryAccept` / `Sanitize` / `MAX_LENGTH` [current/decomp/KSA/SaveName.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SaveName.cs#L8-L69) | Keeps letters, digits, `space`, `-` and `_` (Unicode letters allowed), collapses leading and doubled spaces, trims trailing spaces, truncates at 64, appends `_` to Windows device names such as `CON`. An empty result logs an error and raises an alert |
| `UncompressedSave.Make(string)` [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L141-L146) | `new UncompressedSave(name)`, `Populate()`, `Write()`; returns the save or `null` when the write failed |
| `UncompressedSave.Write()` [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L108-L139) | `SaveDirectory.TryReplace` (delete and recreate the save folder), metadata, `universe.xml`, then `GameSaves.Register` |
| `GameSaves.LoadSaveGame(string)` [current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L282-L304) | Looks the name up in the registered save list; logs `save '<name>' not found` and returns if absent, otherwise `GameSave.Load()` |
| `UncompressedSave.Load()` [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L45-L83) | Returns silently if `RefusedInEditor()`; reads `UniverseData` (logs "could not be read" and returns on an exception); then `Universe.DeserializeSave` and `Program.OnGameLoaded()` |
| `Universe.DeserializeSave` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2430-L2478) | Zeroes the sim speed, `JobSystems.WaitAll()`, `CurrentSystem.DestroyAllVehicles()`, rebuilds vehicles from the save, applies the saved camera, restores the sim speed to 1 |

The important consequences for a caller:

- In the stock game both calls are **synchronous on the calling thread**: `LoadSaveGame` has finished the whole of `DeserializeSave` before it returns, and it raises no event the caller can wait on. A mod can change that: unscience's native save hooks defer every `UncompressedSave.Load` to the `PrepareFrame` handoff and return before the world has changed ([Hook native save and load with five patches, eight callbacks and a deferred world change](../save-mod-state-with-the-scene/save-mod-state-with-the-scene-3.md)), so code that must read the new world should wait for a live craft rather than rely on the return.
- Neither call reports failure. `MakeUncompressedSave` returns `void`; `LoadSaveGame` returns `void` and also returns normally when the editor is open, the name is unknown or the file is unreadable. `UncompressedSave.Make` is the one entry point that tells you whether the write succeeded (`null` when it did not).
- A load does **not** recreate the scene. `DeserializeSave` keeps the same `CelestialSystem` object and replaces its vehicles: `DestroyAllVehicles` disposes each one ([current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L122-L132)) and the save's vehicles are created anew ([current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L663-L720)). A reference to a `Vehicle` taken before the load is a disposed object afterwards. `DeserializeSave` first calls `Unfollow()` on each game viewport's cameras, which sets `Program.ControlledVehicle` to `null`, and `Camera.DeserializeSave` then assigns the saved follow target as a `Vehicle` (staying `null` when the save followed a body or nothing; [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L832-L849)), so after a stock synchronous load "is a craft flown" means `Program.ControlledVehicle is { IsDisposed: false }`, and the `IsDisposed` guard matters only when a mod defers the load.

Lifecycle placement is in the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md): a load is `startup:game-loaded` / `event:load` and a write is `event:save`.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [GameSaveProvider.TryMakeSave driven by a Stopwatch-fed controller with injected delegates](./save-and-load-the-game-from-code-1.md) | unscience (Save Yourself) | `starmap-lifecycle` | `frame:draw-ui`, `event:save` | Saves every N real seconds through the game's own save path; testable without KSA; the write stalls one frame |
| [A file-bridge load command that waits for a controlled craft](./save-and-load-the-game-from-code-2.md) | KSAGolf, KSAStructures, KSACars (KSArmory has the same command) | `starmap-lifecycle` | `frame:after-on-frame`, `event:load` | External tools can load a save and know when the world is ready; polling for a craft is the only completion signal |
| [A scenario runner that loads a save a few seconds after start](./save-and-load-the-game-from-code-3.md) | KSArmory | `starmap-lifecycle` | `frame:draw-ui`, `event:load` | A developer-only unattended run; a fixed three-second beat instead of a readiness check |

## See also

- [Save a whole mod's scene state as versioned sidecar records, with reset and replay on load](../save-mod-state-with-the-scene/overview.md), [Store per-save mod data next to a KSA save and notice when the player saves](../save-mod-state-with-the-game-save/overview.md), [Detect a save load or quickload and reset mod state safely](../detect-save-load/overview.md)
- [Keep mod UI and per-frame work running when the HUD is hidden (F2)](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
