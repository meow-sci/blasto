---
type: Game Feature
title: Persist ImGui window state and layouts
description: What Dear ImGui's ini remembers for a window and what it never does, then three ways to keep window state - a plain ImGui.Begin plus a saved open flag, a filtered ini section of your own, and named layouts of every live window applied on request
category: persistence
group: user-data
topic: persist-imgui-window-state
demonstrated_by:
  - mods/ohmystars/features/ini-settings-persistence
  - mods/compendium/features/settings-ini-persistence
  - mods/unscience/features/unscience-supermod-host
  - mods/purrtty/features/saved-terminal-layouts
lifecycle: [startup:immediate-load, startup:all-mods-loaded, frame:draw-ui, event:starmap-unload]
game_types: [KSA.ImGuiWindow, KSA.ConsoleStyle, Brutal.ImGuiApi.ImGui, KSA.Vehicle, KSA.LayoutSaves]
tags: [imgui, windows, imgui-ini, layouts, open-state, nosavedsettings, window-geometry]
---
A mod window should come back where the player left it. Three different things can be meant by that: its position and size, whether it is open at all, and (for mods with many windows) a whole arrangement the player can save and recall. Dear ImGui remembers only the first, and only for windows that allow it. The three approaches below split the work differently: let ImGui's own ini do geometry and save the open flag yourself, take over the ini text for your own window, or build named layouts as files.

## Engine background

| Piece | What it means for a mod | Source |
|---|---|---|
| `ConsoleStyle.BeginWindowCore` forces `NoSavedSettings` | A window opened through the console-style helper never gets saved geometry | [current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L703) |
| `ImGuiWindow.OnDrawUi` starts from `NoCollapse \| NoSavedSettings` | The same holds for subclasses of the stock window; see [Subclass the stock ImGuiWindow](../../../ui/windows/subclass-the-stock-imguiwindow/overview.md) | [current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L188) |
| `ImGui.SaveIniSettingsToMemory()` / `LoadIniSettingsFromMemory(ImString)` | Read and apply the ini text without touching a file | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8402-L8415) |
| `ImGui.CreateContext()` in the `Program` constructor, before `ModLibrary.LoadAll` | The context exists when `[StarMapAllModsLoaded]` runs, so ini text can be loaded there | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L842), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L942) |
| `FontManager.RegenerateFonts` clears the ImGui font atlas | A game font-settings change rebuilds the atlas; pixel geometry survives, text-derived sizes do not | [current/decomp/KSA/FontManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FontManager.cs#L55-L60) |
| `LayoutSaves` / `LayoutSave` | The game's own HUD layouts (gauge canvases, a folder each under `<docs>/HUDLayouts`), not window layouts | [current/decomp/KSA/LayoutSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LayoutSaves.cs#L119) |

Nothing persists whether a mod window is *open*; every approach stores that flag itself. Timing is the usual one: read at `startup:immediate-load` or `startup:all-mods-loaded`, save from the UI pass (`frame:draw-ui`), and make a final save at `[StarMapUnload]` only as a best effort, because StarMap has already removed its Harmony patches (so the instance-path redirect of the documents folder is gone and an unload-time path built from `Constants.DocumentsFolderPath` points at the default Documents folder) and the game has already disposed its renderer. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Find where KSA keeps user data and choose where a mod stores its config and data files](../choose-where-a-mod-stores-its-files/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Plain ImGui.Begin so imgui.ini restores geometry, and compare the open flag every frame](./persist-imgui-window-state-1.md) | OhMyStars, Compendium | `imgui-window` | `startup:immediate-load`, `startup:all-mods-loaded`, `frame:draw-ui` | Least code; loses the stock chrome and relies on the host's ini and on a stable title |
| [Filtered ImGui ini section saved on a timer and at unload](./persist-imgui-window-state-2.md) | unscience | `imgui-window`, `toml-config` | `startup:all-mods-loaded`, `frame:draw-ui`, `event:starmap-unload` | Geometry travels with the mod's data; early ini loads are unverified against the host's file and the unload save is best effort |
| [One TOML file per layout, applied only on request](./persist-imgui-window-state-3.md) | purrTTY | `toml-config`, `imgui-window` | `frame:draw-ui` | Named, shareable workspaces including in-world panels; the most code, and anchors are best-effort across restarts |

Choosing between them:

- **One or two windows**: approach 1.
- **One big toolbox window with header state**: approach 2.
- **Players create many windows and want to recall sets of them**: approach 3.

## See also

- [Persist mod settings in a hand-rolled ini file](../../settings-formats/persist-mod-settings-ini/overview.md), [Persist mod settings as TOML](../../settings-formats/persist-mod-settings-toml/overview.md)
- [Identify vessels and parts stably across saves](../../saves/identify-vessels-and-parts-across-saves/overview.md)
- [Subclass the stock ImGuiWindow](../../../ui/windows/subclass-the-stock-imguiwindow/overview.md), [Persist user-created palettes, presets and content files](../persist-user-created-presets-and-content/overview.md)
