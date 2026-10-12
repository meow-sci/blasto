---
type: Game Feature
title: Build a mod settings window (and developer panel)
description: KSA has no settings-window API for mods, so six mods show six ImGui layouts - tabbed tables with colour pickers, label-gutter forms, ColorButton popups with INI, small auto-sized windows with tooltips, an XKCD colour catalogue and a developer panel
category: ui
group: windows
topic: build-a-settings-window
demonstrated_by:
  - mods/mpfx/features/effect-settings-window
  - mods/purrtty/features/imgui-filter-combo-and-form-widgets
  - mods/ksa-aircrafthud/features/ini-settings-and-settings-window
  - mods/ksastructures/features/settings-menu
  - mods/ksacars/features/car-panel-and-modmenu
  - mods/ksagolf/features/developer-panel-and-menu-bar
  - mods/unscience/features/lib-xkcd-color-catalog
lifecycle: [startup:immediate-load, frame:draw-ui]
game_types:
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImGuiTableFlags
  - Brutal.ImGuiApi.ImGuiWindowFlags
  - KSA.Program
  - KSA.KSAColor
  - KSA.Universe
  - KSA.SimSpeed
  - Brutal.ImGuiApi.ImGuiColorEditFlags
  - Brutal.Numerics.float4
  - Brutal.ImGuiApi.ImGuiTableColumnFlags
  - Brutal.Numerics.Color
  - Brutal.ImGuiApi.ImInputString
tags: [settings, form, table, color-picker, tooltip, modmenu, developer-panel, imgui-window]
---
KSA has no settings API for mods: a mod's settings are an ImGui window it draws itself. The game's own settings pages are `GameSettings` windows drawn by the engine, but nothing registers a mod page into them, so every mod invents a window. The six approaches here cover what goes into one: the layout of many parameters (tables, label gutters, sliders with typed input), colour editing, small panels with tooltips, and a developer panel that always has a way back.

## Engine background

| Piece | File | Role |
|---|---|---|
| `Program.OnFrame` UI section | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2260-L2290) | Where both StarMap GUI hooks run, gated by `DrawUI` |
| `Program.DrawMenuBar` and `DrawProgramMenusHook` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3480-L3486) | KSA's own "Menu Bar" window; the empty hook mods postfix to add a menu |
| `ImGuiWindowFlags` | [current/decomp/Brutal.ImGuiApi/ImGuiWindowFlags.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiWindowFlags.cs#L14-L29) | `NoCollapse`, `NoFocusOnAppearing`, `NoNav` and the rest |
| `KSAColor.Xkcd` | [current/decomp/KSA/KSAColor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KSAColor.cs#L23-L29) | Named colour presets (properties) for the catalogue approach |
| `Universe.SetSimulationSpeed(SimSpeed)` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L700-L703) | Direct speed setter used by the developer panel |

**Frame order.** Settings windows draw in `frame:draw-ui` from `[StarMapBeforeGui]` or `[StarMapAfterGui]`; persistence happens at `startup:immediate-load` or on a button. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [Slider-plus-input rows, ColorPicker4 and Reset buttons](build-a-settings-window-1.md) | MPFX | Table-row helper, pickers bound to `float4` | `frame:draw-ui` | Dense and powerful; heavy id discipline |
| 2 | [Label-gutter form tables](build-a-settings-window-2.md) | purrTTY | Two-column form table with measured gutter, red destructive button | `frame:draw-ui` | Consistent dialogs; helper library to maintain |
| 3 | [ColorButton popups and INI persistence](build-a-settings-window-3.md) | KSA Aircraft HUD | Swatches opening `ColorPicker4` popups, hand-parsed INI | `startup:immediate-load`, `frame:draw-ui` | Tiny; fragile parsing, writes into install folder |
| 4 | [Menu entry, small window, wrapped tooltips](build-a-settings-window-4.md) | KSAStructures, KSACars | `AlwaysAutoResize`, `###id`, `NoNav`, release-apply sliders | `frame:draw-ui` | Quick; session-only settings |
| 5 | [Reflected XKCD colour catalogue](build-a-settings-window-5.md) | unscience | Reflection over `KSAColor.Xkcd`, filter combo with swatches | `frame:draw-ui` | Friendly colour names; breaks silently if the class changes |
| 6 | [Developer panel with a floating reopen button](build-a-settings-window-6.md) | KSAGolf | Reopen button, ModMenu entry, slow-motion speeds | `frame:draw-ui` | Never strands the player; global speed side effect |

## Choosing

- Lots of numeric parameters: approach 1 (dense table) or approach 2 (form gutter).
- A few options and some colours that must persist: approach 3, plus the persistence topics.
- A short list of toggles for a feature mod: approach 4. Add approach 6's reopen button if the panel is closable.
- Colour by name: approach 5.

## See also

- [Build a tabbed ImGui window](../build-a-tabbed-imgui-window/overview.md)
- [Match the stock console UI style](../match-the-stock-console-ui-style/overview.md)
- [Build searchable lists and browsers](../build-searchable-lists-and-browsers/overview.md)
- [Persist mod settings as TOML](../../../persistence/settings-formats/persist-mod-settings-toml/overview.md)
- [List a mod in ModMenu with the ModMenuEntry attribute](../../menus/list-a-mod-in-modmenu/overview.md)
- [Add a menu to the game's top menu bar](../../menus/add-items-to-the-game-menu-bar/overview.md)

