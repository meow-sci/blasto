---
type: Submod
title: Save Yourself
description: Opt-in timed auto saves that write ordinary native KSA saves through the game's own save path every N wall-clock seconds, with a sanitised name prefix and a global TOML preference file
mod: unscience
submod: save-yourself
tags: [saves, autosave, timer, toml, testing]
mod_id: save-yourself
---

Save Yourself writes a new native save every N real seconds (5-300, default 30) named
`<prefix>_YYYYMMDDTHHMMSS`, for example `autosave_20260928T203000`. It never serializes anything
itself: `GameSaveProvider.TryMakeSave` (shared in `ksa-abstractions.lib`) calls `SaveName.TryAccept`
and `UncompressedSave.Make` exactly like the console `save` command, so the save list, every native save
hook and the unscience scene sidecar behave as for a manual save. A due save waits while no world is
loaded or the vehicle editor is open, then fires as soon as saving is allowed. The timer is wall-clock
time, independent of warp, pause and frame hitches. Other mods can drive it through
`SaveYourselfSubmod.Instance?.Controller` or call `GameSaveProvider` directly.

## Hosting

- Manifest id `save-yourself`, entry assembly `MeowSci.SaveYourself` (development host only; only the
  unscience supermod ships).
- Standalone host: F11 toggles a "Save Yourself" window; `[StarMapBeforeGui]` calls `Update`,
  `[StarMapAfterGui]` draws the panel, and its `Patcher` applies only `HotkeyGuard`. It does not install
  the native save hooks, so standalone auto saves carry no sidecar, and nothing ticks while the HUD is
  hidden.
- In unscience the submod needs no patches: `Mod.cs` adds `SaveYourselfSubmod` to the submod list and
  drives `Update` from `UpdateSubmods`, which runs from `[StarMapBeforeGui]` or, with the HUD hidden,
  from the `HiddenUiFrameHook` replay.

## Features

| Feature | Category | Lifecycle | Summary |
|---|---|---|---|
| [Timed auto saves through the game's own save path](../features/save-yourself-timed-autosave.md) | persistence | `frame:draw-ui`, `event:save` | Save Yourself writes a new native KSA save every N wall-clock seconds by calling SaveName.TryAccept and UncompressedSave.Make the way the console save command does, so every native save hook and the unscience sidecar run exactly as for a manual save |

## Library classes

`SaveYourselfSubmod` (lifecycle, `Stopwatch` ticking, panel, `ApplySettings` write-through),
`AutoSaveController` (countdown, wait-for-game, save execution with injected `canSave` / `makeSave` /
clock delegates), `AutoSaveSettings` (DTO, prefix sanitising, interval clamping), `AutoSaveNaming`
(name builder), `AutoSaveSettingsStore` (Tomlyn-backed preference file); shared
`ksa-abstractions.lib/GameSaveProvider.cs` (`CanSaveNow`, `TryMakeSave`). The `save-yourself.tests`
console project links the four KSA-free classes and checks naming, sanitising, the countdown state
machine and settings-file robustness.

## Scene saves and persistence

No save participant, by design: the prefix, enabled flag and interval are a global Unscience preference
in `.unscience/save-yourself.toml` (an `[auto_save]` table), loaded at initialization and written on
every change, so loading a game never changes the auto-save configuration and the countdown continues
across loads. Countdown position, last-save name and counters are transient. The feature is a
**producer** of native saves: each one gets the full unscience sidecar like any manual save.

## Further reading

- [save-yourself/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/save-yourself/README.md)
- [save-yourself.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/save-yourself.lib/README.md)
- [save-yourself.tests/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/save-yourself.tests/README.md)
- [scope/saves.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/saves.md) (the "Save Yourself auto saves" section)
- [scope/game-integration-surface.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/game-integration-surface.md) (game-surface table and update risks)

## Entry points

- [save-yourself/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/save-yourself/mod.toml)
- [save-yourself/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/save-yourself/Mod.cs)
- [save-yourself/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/save-yourself/Patcher.cs)
- [ksa-abstractions.lib/GameSaveProvider.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/GameSaveProvider.cs)
- [unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L150-L167)

## See also

- [unscience overview](../overview.md)
- [Native save and load hooks](../features/lib-native-save-hooks.md)
- [Per-save scene sidecar](../features/unscience-scene-save-sidecar.md)
- [unscience host](./unscience.md)
