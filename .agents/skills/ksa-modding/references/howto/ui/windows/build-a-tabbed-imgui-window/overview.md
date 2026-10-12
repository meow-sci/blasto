---
type: Game Feature
title: "Organise a mod window: tabs, button tabs, tables and pop-out sections"
description: KSA gives mods plain Dear ImGui for tabs, so five mods show five structures - a real tab bar, a per-craft window with panes, hand-drawn tab buttons, button tabs over a table, and sections that pop out into windows
category: ui
group: windows
topic: build-a-tabbed-imgui-window
demonstrated_by:
  - mods/purrtty/features/terminal-window-chrome-tabs-and-resize-snap
  - mods/ksarmory/features/operator-panel-imgui
  - mods/ohmystars/features/modmenu-entry-and-console-style-windows
  - mods/mpfx/features/effect-settings-window
  - mods/kitten-engineer-redux/features/draw-list-widget-kit
lifecycle: [frame:draw-ui]
game_types:
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImGuiTabBarFlags
  - Brutal.ImGuiApi.ImGuiTabItemFlags
  - KSA.ConsoleStyle
  - KSA.ImGuiWindow
  - KSA.ConsoleWidgets
  - Brutal.ImGuiApi.ImGuiTableFlags
  - KSA.Vehicle
  - KSA.GameViewport
tags: [tabs, tab-bar, pop-out, sections, tables, imgui-window]
---
KSA has no tab or pane abstraction for mods: the stock windows are single-page, and the game's `ImGuiWindow` base class draws one `DrawContent`. A mod that outgrows one page therefore builds its structure out of Dear ImGui primitives, and the five mods covered here show five different ways: use the real tab bar, split the UI into several top-level windows, hand-draw tab buttons, drive a table from buttons, or let sections move between a docked panel and floating windows.

## Engine background

| Piece | File | Role |
|---|---|---|
| `ImGui.BeginTabBar` / `EndTabBar` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7736-L7746) | Real ImGui tab bar |
| `ImGuiTabBarFlags` | [current/decomp/Brutal.ImGuiApi/ImGuiTabBarFlags.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiTabBarFlags.cs#L6-L20) | `Reorderable`, `AutoSelectNewTabs`, `FittingPolicyScroll` and others |
| `ImGuiTabItemFlags` | [current/decomp/Brutal.ImGuiApi/ImGuiTabItemFlags.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiTabItemFlags.cs#L7-L18) | `SetSelected` to force a tab, `UnsavedDocument`, `Trailing` |
| `ConsoleStyle.PushWidgetStyle` | [current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L575-L650) | Colours tabs (`Tab`, `TabHovered`, `TabSelected`) along with every other widget when pushed |
| `ImGuiWindow` | [current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L181-L248) | Stock window shell: one `DrawContent`, id = name + `###` + type name |

**Frame order.** All of these draw in `frame:draw-ui`, from a StarMap GUI hook or a Harmony postfix on a stock UI method; window state is just ImGui window state. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [Closeable ImGui tab bar with programmatic selection](build-a-tabbed-imgui-window-1.md) | purrTTY | `BeginTabBar` plus per-tab close buttons, `SetSelected` and a stale-selection guard | `frame:draw-ui` | Real tab behaviour for free; you own the selection bookkeeping |
| 2 | [Per-craft window with pop-out panes and a reopen button](build-a-tabbed-imgui-window-2.md) | KSArmory | Several independent windows, a tab bar inside the per-craft one, a pane table, a reopen button | `frame:draw-ui` | Flexible, multi-craft; lots of window state to manage |
| 3 | [Title-bar-coloured custom tab buttons](build-a-tabbed-imgui-window-3.md) | Oh My Stars | `ConsoleWidgets` buttons plus a painter that fills the active tab with `TitleBgActive` | `frame:draw-ui` | Matches the console look; no keyboard navigation |
| 4 | [Button tabs driving a table of collapsing headers](build-a-tabbed-imgui-window-4.md) | MPFX | Disabled-when-active buttons under a reserved-height child; a 3-column table per page | `frame:draw-ui` | Simple and compact; lots of repeated ids to wrap with `PushID` |
| 5 | [Collapsible sections that pop out into their own windows](build-a-tabbed-imgui-window-5.md) | Kitten Engineer Redux | One delegate per section, docked or floating, state keyed by section id | `frame:draw-ui` | Best for many panes; draw-list layout and Harmony hook needed |

## Choosing

- A handful of same-shaped pages, possibly closeable: approach 1. Several pages that should look like the console: approach 3.
- A dense settings page with many parameters: approach 4, then see [the settings-window topic](../build-a-settings-window/overview.md).
- A panel that must work for several objects, or whose panes should live in their own windows: approach 2 for per-object windows, approach 5 for per-section pop-out.

## See also

- [Build a mod settings window](../build-a-settings-window/overview.md)
- [Match the stock console UI style](../match-the-stock-console-ui-style/overview.md)
- [Build a mod window on the game's ImGuiWindow base class](../subclass-the-stock-imguiwindow/overview.md)
- [Build custom-drawn ImGui widgets, icons, trees and alert tiles on the draw list](../../draw-lists/build-custom-drawn-imgui-widgets/overview.md)

