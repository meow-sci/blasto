---
type: Game Feature
title: Persist mod settings in a hand-rolled ini file
description: A flat key=value settings file with no library - where to read it, what ImGui already remembers, and four write policies - on every change, load first, an explicit Save button, or a dirty flag flushed on close and unload
category: persistence
group: settings-formats
topic: persist-mod-settings-ini
demonstrated_by:
  - mods/ohmystars/features/ini-settings-persistence
  - mods/compendium/features/settings-ini-persistence
  - mods/ksa-aircrafthud/features/ini-settings-and-settings-window
  - mods/ksa-measuretools/features/color-palettes-persistence
lifecycle: [startup:immediate-load, startup:all-mods-loaded, frame:draw-ui, event:starmap-unload]
game_types: [KSA.Mod, Brutal.ImGuiApi.ImGui]
tags: [settings, ini, config, imgui, persistence, invariant-culture]
---

Four mods keep their settings in a plain `key=value` text file with a few dozen lines of parsing and no package reference. The format is the same everywhere (`Key=Value` lines, `[Section]` headers that are decorative or real), what differs is when the file is read and when it is written.

## Engine background

KSA gives a mod nothing to persist its own settings: there is no settings API, and the game's own `settings.toml` is for the game (see [Persist mod settings as TOML](../persist-mod-settings-toml/overview.md)). A mod therefore picks a folder ([Find where KSA keeps user data and choose where a mod stores its config and data files](../../user-data/choose-where-a-mod-stores-its-files/overview.md)) and a format. What the engine does provide:

| Fact | Where |
|---|---|
| `Mod.DirectoryPath` is the folder of the mod manifest; `StarMapImmediateLoad(Mod)` receives the `Mod` | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L91), [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L108-L109) |
| The ImGui context is created in the `Program` constructor, before `ModLibrary.PrepareAll` and `LoadAll`, so it exists at `startup:immediate-load` and `startup:all-mods-loaded` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L842) |
| ImGui remembers position and size of a window in its own ini file unless the window sets `NoSavedSettings`; it does not remember whether a window is open (the mod owns that bool) | `ImGuiWindowFlags.NoSavedSettings` |
| The stock window helpers force `NoSavedSettings`: `ConsoleStyle.BeginWindowCore` includes it in its flags, and so does the base `ImGuiWindow` | [current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L703), [current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L188) |
| `StarMapBeforeGui` and `StarMapAfterGui` do not run while the HUD is hidden, so a write driven from them waits until it returns | [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) |

Window geometry is covered in [Persist ImGui window state and layouts](../../user-data/persist-imgui-window-state/overview.md); this topic is about the mod's own values.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Flat settings.ini rewritten on every widget change](./persist-mod-settings-ini-1.md) | OhMyStars | `toml-config` | `startup:all-mods-loaded`, `frame:draw-ui` | Always current, no save button; many small writes, whole file each time |
| [Load first at StarMapImmediateLoad, rewrite on change](./persist-mod-settings-ini-2.md) | Compendium | `toml-config` | `startup:immediate-load`, `frame:draw-ui` | The first frame already knows the saved options; clamped values |
| [Eleven-key hud.ini saved by an explicit button](./persist-mod-settings-ini-3.md) | KSA_AircraftHUD | `imgui-window` | `startup:immediate-load`, `frame:draw-ui` | Player controls when to write; no protection against a malformed value |
| [Sectioned palette file written when a dirty flag is set, on close and on unload](./persist-mod-settings-ini-4.md) | KSA-MeasureTools | `starmap-lifecycle` | `startup:all-mods-loaded`, `frame:draw-ui`, `event:starmap-unload` | Few writes and migration of an older format; state is lost if the game is killed |

## See also

- [Persist mod settings as JSON with System.Text.Json](../persist-mod-settings-as-json/overview.md), [Persist mod settings as TOML](../persist-mod-settings-toml/overview.md), [Write config files atomically and load hand-edited ones without losing them](../write-and-load-config-files-safely/overview.md)
- [Persist ImGui window state and layouts](../../user-data/persist-imgui-window-state/overview.md), [Build a mod settings window (and developer panel)](../../../ui/windows/build-a-settings-window/overview.md)
