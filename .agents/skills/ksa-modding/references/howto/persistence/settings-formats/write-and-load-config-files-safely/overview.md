---
type: Game Feature
title: Write config files atomically and load hand-edited ones without losing them
description: Write rules (atomic replace, save on change, debounce, flush at unload) and load rules (clamp, drop the broken field, migrate, never overwrite an unparseable file) for mod config files
category: persistence
group: settings-formats
topic: write-and-load-config-files-safely
demonstrated_by:
  - mods/ksa-public-telemetry/features/json-config-in-documents
  - mods/kitten-engineer-redux/features/json-settings-autosave
  - mods/gatos/features/toml-config-seeded-template
  - mods/ksa-measuretools/features/color-palettes-persistence
  - mods/purrtty/features/toml-themes-and-settings
lifecycle: [frame:draw-ui, event:starmap-unload, thread:background, startup:all-mods-loaded]
game_types: []
tags: [config, atomic-write, debounce, dirty-flag, sanitise, migration, resilience]
---

A settings file is user data with two enemies: the process dying halfway through a write, and the player editing a value the mod cannot parse. A mod that handles both loses nothing the player cares about. The rules below are the common ground of four mods that each implement them differently; the format choice is a separate topic ([Persist mod settings in a hand-rolled ini file](../persist-mod-settings-ini/overview.md), [Persist mod settings as JSON with System.Text.Json](../persist-mod-settings-as-json/overview.md), [Persist mod settings as TOML](../persist-mod-settings-toml/overview.md)).

## Engine background

KSA provides no config service, so these rules come from the host environment:

| Fact | Consequence |
|---|---|
| `Program.OnFrame` runs UI, render and your hooks on one thread; there is no exception handler around it (see [Keep mod exceptions out of KSA's frame loop](../../../getting-started/lifecycle/contain-exceptions-in-starmap-hooks/overview.md)) | Blocking file I/O in a draw hook stalls the frame, and an exception in a load hook ends the game |
| `StarMapUnload` runs after the game loop has returned and after StarMap's id-less `UnpatchAll()`; the instance-path prefix on `Constants.DocumentsFolderPath` is gone by then | The last flush can still write, but a path recomputed from the constant at that moment points at the default folder ([StarMap.Core/Patches/DocumentsPathPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/DocumentsPathPatches.cs#L12-L45), [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)) |
| `StarMapAfterGui` and `StarMapBeforeGui` do not run while the HUD is hidden | A flush driven from them waits until the HUD returns |
| A process kill or crash skips `StarMapUnload` | Anything not yet written is lost; a write-on-quit-only design loses a session |

Hook placement and phase names are defined in the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md); the one frame-loop fact that matters most here is that `Program.OnFrame` calls the UI phase and the hooks on the same thread ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2290)).

## Approaches

The articles split each mod's practice into a write side and a load side.

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Dirty flag, debounce and atomic replace on write; JsonObject retry on load](./write-and-load-config-files-safely-1.md) | KSA-Public-Telemetry | `json-config` | `frame:draw-ui`, `event:starmap-unload`, `startup:all-mods-loaded` | Few writes, no truncation, a bad field costs only that field; an unparseable file is overwritten at unload |
| [Compare the serialised snapshot with the last write](./write-and-load-config-files-safely-2.md) | Kitten-Engineer-Redux | `json-config` | `frame:draw-ui` | No dirty flag in the UI; polls and allocates every interval; not atomic |
| [Save from Task.Run with an atomic helper; clamp and log on load, leave an unparseable file alone](./write-and-load-config-files-safely-3.md) | gatOS | `background-thread` | `startup:all-mods-loaded`, `thread:background` | The render thread never does I/O; clamping is bootable-range logic; a later save can still replace a broken file |
| [Flush on close and unload; skip junk keys and migrate the previous format on load](./write-and-load-config-files-safely-4.md) | KSA-MeasureTools, purrTTY | `starmap-lifecycle` | `frame:draw-ui`, `event:starmap-unload`, `startup:all-mods-loaded` | Rare writes and explicit migration; not atomic; a kill loses edits |

## See also

- [Persist mod settings as JSON with System.Text.Json](../persist-mod-settings-as-json/overview.md), [Persist mod settings as TOML](../persist-mod-settings-toml/overview.md), [Persist mod settings in a hand-rolled ini file](../persist-mod-settings-ini/overview.md)
- [Persist user-created palettes, presets and content files](../../user-data/persist-user-created-presets-and-content/overview.md), [Find where KSA keeps user data and choose where a mod stores its config and data files](../../user-data/choose-where-a-mod-stores-its-files/overview.md)
- [Clean up on game exit and relaunch the game from a mod](../../../getting-started/lifecycle/clean-up-on-game-exit/overview.md)
