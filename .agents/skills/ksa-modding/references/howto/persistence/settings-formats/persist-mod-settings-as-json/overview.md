---
type: Game Feature
title: Persist mod settings as JSON with System.Text.Json
description: Serialise a settings object to a JSON file under the KSA documents folder, load it before the UI is built and write only when something changed - a two-second snapshot compare, or a debounced dirty flag with override-only colors
category: persistence
group: settings-formats
topic: persist-mod-settings-as-json
demonstrated_by:
  - mods/kitten-engineer-redux/features/json-settings-autosave
  - mods/ksa-public-telemetry/features/json-config-in-documents
  - mods/ksa-public-telemetry/features/live-tuning-and-palette-reflection
lifecycle: [startup:all-mods-loaded, frame:draw-ui, event:starmap-unload]
game_types: [KSA.Constants]
tags: [settings, json, system-text-json, autosave, documents-folder, config]
---

`System.Text.Json` ships with .NET, so a settings file in JSON costs no dependency: a class with public properties, one `JsonSerializer.Serialize`, one `Deserialize`. The design questions are where the file goes, when it is read, and when it is written without a save button.

## Engine background

| Fact | Where |
|---|---|
| `Constants.DocumentsFolderPath` is `Environment.SpecialFolder.Personal` + `My Games/Kitten Space Agency`; `LogsFolderPath` and the other special folders hang off it | [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281-L287) |
| StarMap redirects it per launch by patching the property getter when `-InstancePath` or `STARMAP_INSTANCE_PATH` is set; the patch is removed by the id-less `UnpatchAll()` before `StarMapUnload` runs | [StarMap.Core/Patches/DocumentsPathPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/DocumentsPathPatches.cs#L12-L45) |
| The ImGui context exists from the `Program` constructor on, before `StarMapAllModsLoaded` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L842) |
| Frame hooks (`StarMapBeforeGui`, `StarMapAfterGui`) run only while the HUD is visible | [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) |

Using `Constants.DocumentsFolderPath` keeps the file next to the right game data when an instance path is in use; using `Environment.SpecialFolder.MyDocuments` directly (as Kitten Engineer Redux does) always writes to the default folder. Where files go and how updates treat them is the subject of [Find where KSA keeps user data and choose where a mod stores its config and data files](../../user-data/choose-where-a-mod-stores-its-files/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Autosave tick that re-serialises a snapshot every two seconds and writes only on change](./persist-mod-settings-as-json-1.md) | Kitten-Engineer-Redux | `json-config` | `frame:draw-ui` | No dirty flag to thread through the UI; a small allocation every two seconds; no flush at unload |
| [Settings object plus override-only colors and tuning, debounced dirty-flag save](./persist-mod-settings-as-json-2.md) | KSA-Public-Telemetry | `json-config` | `startup:all-mods-loaded`, `frame:draw-ui`, `event:starmap-unload` | Hand-editable, tolerant of broken fields; the unload write misses an instance path |

## See also

- [Persist mod settings as TOML](../persist-mod-settings-toml/overview.md), [Persist mod settings in a hand-rolled ini file](../persist-mod-settings-ini/overview.md), [Write config files atomically and load hand-edited ones without losing them](../write-and-load-config-files-safely/overview.md)
- [Find where KSA keeps user data and choose where a mod stores its config and data files](../../user-data/choose-where-a-mod-stores-its-files/overview.md), [Persist ImGui window state and layouts](../../user-data/persist-imgui-window-state/overview.md)
