---
type: Game Feature
title: "Make mod UI look like the game: console widgets, window skin and global theme"
description: How KSA's console style is built from public ConsoleStyle and ConsoleWidgets helpers, and four ways mods use them or the global ImGuiStyle to match or re-theme the UI
category: ui
group: windows
topic: match-the-stock-console-ui-style
demonstrated_by:
  - mods/ksa-deltavmap/features/stock-console-widget-panels
  - mods/ksa-measuretools/features/measure-menu-and-tool-window
  - mods/ohmystars/features/modmenu-entry-and-console-style-windows
  - mods/ksa-advancedflightcomputer/features/guidance-panel-and-overlays
  - mods/ksa-advancedflightcomputer/features/stock-styled-console-ui
  - mods/unscience/features/skittles-imgui-theme-manager
lifecycle: [frame:draw-ui, startup:all-mods-loaded, event:save, event:load, frame:prepare]
game_types:
  - KSA.ConsoleStyle
  - KSA.ConsoleWidgets
  - KSA.ImGuiWindow
  - KSA.TransferPlanner
  - KSA.ImGuiDisabledScope
  - Brutal.ImGuiApi.ImGuiStyle
  - Brutal.ImGuiApi.ImGuiCol
  - KSA.FontManager
  - KSA.KSAColor
  - Brutal.ImGuiApi.ImGui
tags: [console-style, console-widgets, theme, imgui-style, window-skin, stock-look]
---
KSA's windows share a recognisable look: bezel, title strip with a signature tag, upper-case labels in a label font, a third-width label column, hover wash on rows, segmented buttons, toggle-style checkboxes. None of it is a global ImGui theme (the game never sets the global style); it is a pair of **public** static classes the stock windows call,
`KSA.ConsoleStyle` (palette, fonts, metrics, window shell, widget-style push) and `KSA.ConsoleWidgets` (rows and controls). A mod can use them at three levels of commitment: the widgets only, the widget style plus a plain window, or the full window shell. A fourth mod goes the other way and re-themes the global style.

## Engine background

| Piece | File | Role |
|---|---|---|
| `ConsoleStyle.PushWidgetStyle` / `PopWidgetStyle` | [current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L575-L650) | Pushes 48 colours, 10 style vars and the label font; pops them again |
| `ConsoleStyle.BeginWindow` / `EndWindow` | [current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L678-L760) | Window shell: bezel, title strip, close button; forces `NoSavedSettings`; pins to the main viewport unless `pinToMainViewport: false` |
| `ConsoleStyle.BeginBody` / `BeginFooter` | [current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L843-L943) | Body and footer regions of the shell |
| `ConsoleWidgets.BeginRow` / `EndRow` / `Checkbox` / `Button` / `PrimaryButton` / `Segmented` / `RegionHeader` / `Readout` / `Tooltip` / `BeginComboControl` | [current/decomp/KSA/ConsoleWidgets.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleWidgets.cs#L142-L450) | Rows and controls; rows keep state in statics |
| `ImGuiWindow.UseConsoleChrome` | [current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L181-L186) | Opt-in: `OnDrawUi` goes through `ConsoleStyle.BeginWindow` and pushes the widget style around `DrawContent` |
| `TransferPlanner` private row helpers | [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L944-L948) | The model mods copy: `DrawSliderFloatRow`, `DrawComboRow` |

**Frame order.** All of it is immediate-mode ImGui drawn in `frame:draw-ui`. The widget style is a push/pop pair that must balance on every exit path. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [ConsoleWidgets rows inside an ImGuiWindow subclass](match-the-stock-console-ui-style-1.md) | KSA-DeltaVMap, KSA-MeasureTools | Rows, headers, readouts and buttons from the kit; no widget-style push | `frame:draw-ui` | Cheapest; global spacing and raw controls still look default |
| 2 | [Plain ImGui.Begin plus PushWidgetStyle](match-the-stock-console-ui-style-2.md) | OhMyStars | Console look with `imgui.ini` placement; hand-painted tab buttons | `frame:draw-ui` | Keeps window freedom; you balance push/pop |
| 3 | [ConsoleStyle window skin unpinned, mirrored row helpers](match-the-stock-console-ui-style-3.md) | KSA-AdvancedFlightComputer | `BeginWindow(..., pinToMainViewport: false)`; private planner rows re-created | `frame:draw-ui` | Full stock chrome and multi-monitor; no saved placement |
| 4 | [Re-theme every window through the global ImGuiStyle](match-the-stock-console-ui-style-4.md) | unscience (Skittles) | Capture, write and restore `ImGui.GetStyle()`; TOML presets | `startup:all-mods-loaded`, `frame:draw-ui` | Re-themes everything; per-widget pushes still win |

## Choosing

- Add a few stock-looking rows to a panel: approach 1. A whole window that feels stock but moves and saves like a normal ImGui window: approach 2.
- Exactly the stock window shell, possibly on another monitor, or UI injected into a stock window: approach 3.
- A user-selectable skin for the entire game: approach 4.

## See also

- [Build a mod window on the game's ImGuiWindow base class](../subclass-the-stock-imguiwindow/overview.md)
- [Build a mod settings window](../build-a-settings-window/overview.md)
- [Build a tabbed ImGui window](../build-a-tabbed-imgui-window/overview.md)
- [Inject UI into the game's own windows and menus, including the orbit right-click menu](../../menus/inject-ui-into-a-stock-window/overview.md)
- [Build custom-drawn ImGui widgets, icons, trees and alert tiles on the draw list](../../draw-lists/build-custom-drawn-imgui-widgets/overview.md)
- [Persist mod settings as TOML](../../../persistence/settings-formats/persist-mod-settings-toml/overview.md)

