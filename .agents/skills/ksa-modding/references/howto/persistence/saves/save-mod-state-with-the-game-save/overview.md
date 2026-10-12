---
type: Game Feature
title: Store per-save mod data next to a KSA save and notice when the player saves
description: Why per-save mod data lives in a sidecar, where the sidecar can go, how to detect that the game wrote a save, and the three layouts mods use - JSON in the save folder, a per-save store written on Make, and TOML in the mod folder keyed by save and vehicle
category: persistence
group: saves
topic: save-mod-state-with-the-game-save
demonstrated_by:
  - mods/ksarmory/features/per-craft-settings-json-sidecar
  - mods/ohmystars/features/save-scoped-settings-sidecar
  - mods/ksa-advancedflightcomputer/features/save-scoped-sidecar-registries
lifecycle: [startup:all-mods-loaded, event:save, event:load, frame:draw-ui]
game_types: [KSA.GameSaves, KSA.GameSave, KSA.UncompressedSave, KSA.SaveDirectory, KSA.UniverseData, KSA.Vehicle]
tags: [saves, sidecar, per-save, universe-xml, save-folder, harmony]
---

KSA's save is a directory with a fixed XML payload. A mod that wants to keep data per save (settings per craft, an execution in progress, a window layout) has nowhere to put it in `universe.xml`, so it keeps a sidecar. The design questions are where the sidecar lives, how it is keyed, and when it is written.

## Engine background

| Fact | Where |
|---|---|
| A save is a folder `saves/<Id>/` under `Constants.DocumentsFolderPath` (`GameSaves.SaveFolderPath`) | [current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L124), [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281) |
| `UniverseData` is a fixed class with XML element attributes (`GameTime`, `CameraData`, `System` list, `KittenRoster`); there is no extension slot | [current/decomp/KSA/UniverseData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UniverseData.cs#L8-L21) |
| `UncompressedSave.Write` first calls `SaveDirectory.TryReplace`, which deletes the save folder recursively (clearing attributes) and creates it again, retrying three times at 50 ms; only then it writes the metadata and `universe.xml` | [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L108-L118), [current/decomp/KSA/SaveDirectory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SaveDirectory.cs#L21-L100) |
| `Write` returns `false` after a failed folder replace or a failed write; on success it registers the save with `GameSaves` | [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L108-L139) |
| `UncompressedSave.Make(name)` is `new UncompressedSave(name)`, `Populate()`, `Write()` and returns `null` when `Write` fails; every save path (the console command, the save window, overwrite) goes through it | [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L141-L146) |
| `GameSaves.Selected` is assigned only by `GameSaves.OnSelected`, which the save browser calls when a row is clicked; it is not "the save currently loaded" | [current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L327-L330), [current/decomp/KSA/UncompressedSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UncompressedSave.cs#L228-L230) |
| `GameSave.Id` is the folder name and the key in the save list; `UncompressedSave.Load` and `Write` are public hook points (see below) | [current/decomp/KSA/GameSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSave.cs#L1-L40) |

Two consequences shape every sidecar:

1. **A file written into the save folder is deleted by the next save of that name.** KSA 5554 wipes and recreates the folder on every write. A sidecar inside the save folder therefore has to be written after `Write`, every time.
2. **There is no save hook in StarMap.** A mod either patches `UncompressedSave.Make` or `Write` (the postfix runs after the folder exists again) or watches the save's `universe.xml` timestamp.

The timing of saves, loads and the frame phases involved is in the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md); the save side is `event:save`.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [JSON file inside the save folder, written when universe.xml changes](./save-mod-state-with-the-game-save-1.md) | KSArmory | `save-sidecar` | `frame:draw-ui`, `event:save` | No patch; save copy, rename and delete carry the data; one-step delay and a file-clock dependency; includes polling `universe.xml` as the save detector |
| [Generic per-save store keyed by save id, written on UncompressedSave.Make](./save-mod-state-with-the-game-save-2.md) | OhMyStars | `save-sidecar` | `startup:all-mods-loaded`, `event:save`, `event:load` | Write exactly when the game does; the pattern is reusable though the settings type is empty in this version; includes Make versus Write postfix |
| [TOML in the mod folder keyed by (save id, vehicle id), rekeyed on save-as](./save-mod-state-with-the-game-save-3.md) | KSA-AdvancedFlightComputer | `save-sidecar` | `startup:all-mods-loaded`, `event:save`, `event:load` | Stock saves untouched, uninstalling loses nothing; needs rekeying on save-as and rename/dispose hooks |

## See also

- [Save a whole mod's scene state as versioned sidecar records, with reset and replay on load](../save-mod-state-with-the-scene/overview.md), [Detect a save load or quickload and reset mod state safely](../detect-save-load/overview.md), [Identify vessels and parts stably across saves](../identify-vessels-and-parts-across-saves/overview.md)
- [Write config files atomically and load hand-edited ones without losing them](../../settings-formats/write-and-load-config-files-safely/overview.md), [Create and load native KSA saves from mod code](../save-and-load-the-game-from-code/overview.md)
- [Open the stock vehicle editor on a live EVA kitten, show it upright and keep its attachment nodes](../../../kittens/eva/open-the-vehicle-editor-on-a-live-eva-kitten/overview.md), [Restore kitten colours, clips and tuning after a save load](../../../kittens/characters/restore-kitten-customizations-after-a-load/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
