---
type: Game Feature
title: Build custom-drawn ImGui widgets, icons, trees and alert tiles on the draw list
description: Laying out rows, tiles, gauges, toggles, icons, glyphs, trees and rule-driven alert tiles yourself with ImDrawList and InvisibleButton hit-testing instead of stock widgets or textures
category: ui
group: draw-lists
topic: build-custom-drawn-imgui-widgets
demonstrated_by:
  - mods/kitten-engineer-redux/features/draw-list-widget-kit
  - mods/ksarmory/features/operator-panel-imgui
  - mods/ksa-deltavmap/features/subway-map-canvas-rendering
  - mods/kittenextensions/features/imgui-tree-view
  - mods/kitten-engineer-redux/features/hud-widgets-and-threshold-alerts
lifecycle: [frame:draw-ui, startup:asset-load, editor:draw-ui]
game_types:
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImDrawListPtr
  - Brutal.ImGuiApi.ImDrawListExtensions
  - KSA.ViewportBase
  - Brutal.ImGuiApi.ImGuiStoragePtr
  - Brutal.Numerics.floatRect
  - KSA.Program
  - KSA.Vehicle
tags: [widgets, icons, tiles, gauge, toggle, tree-view, alerts, draw-list, invisible-button]
---
KSA's stock UI vocabulary for mods is Dear ImGui through the Brutal binding plus the game's console-style helpers. Mods that want a look the stock widgets cannot give (a dashboard of tiles and gauges, icons on buttons, a node-state glyph vocabulary, an indented tree with selection fills) build the widget themselves from three pieces: draw-list primitives for the picture, an `InvisibleButton` for the hit test, and a `Dummy` so ImGui knows how much layout the picture used. Five patterns are documented here, from a full widget kit to a rule evaluator that feeds several widgets. Everything runs in `frame:draw-ui` (the tree view in KittenExtensions runs in its own pre-game frame loop during `startup:asset-load`); see [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Engine background

| Piece | File | What it gives you |
|---|---|---|
| `ImGui.InvisibleButton`, `Dummy`, `SetCursorScreenPos`, `GetCursorScreenPos`, `GetItemRectMin/Max` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L6255)[^brutal-imguiapi-imgui] (`InvisibleButton`), L6090 (`Dummy`), L8013 (`GetItemRectMin`) | Hit-test an area, advance layout, and read an item's rectangle back |
| `ImDrawListPtr` primitives, `ChannelsSplit(int)`, `ChannelsSetCurrent`, `ChannelsMerge` | [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L436-L449)[^brutal-imguiapi-imdrawlistextensions] | Draw things in a different order than they were submitted |
| `ImGuiStoragePtr.GetBool/SetBool` via `ImGui.GetStateStorage()` | [current/decomp/Brutal.ImGuiApi/ImGuiStorageExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiStorageExtensions.cs#L23-L31)[^brutal-imguiapi-imguistorageextensions] | Per-ID state kept by ImGui itself, e.g. whether a tree node is open |
| `ImGuiHelper` | [current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L440-L474)[^ksa-imguihelper] | The game's own coloured `DrawButton`, `DrawTextOnScreen` (L518) and `DrawMarkerDot` (L537) |
| `floatRect` | [current/decomp/Brutal.Numerics/floatRect.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Numerics/floatRect.cs#L7-L11)[^brutal-numerics-floatrect] | Min/Max rectangle used for cell geometry |
| `Program.ControlledVehicle` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502)[^ksa-program] | The vehicle a flight HUD should describe |

**The three-step contract of a drawn widget.** (1) Decide the rectangle in screen coordinates. (2) `SetCursorScreenPos(rect.Min)` then `InvisibleButton(id, size)`: it returns the click and gives `IsItemHovered`/`IsItemActive` for the same rectangle. (3) Draw background and label on the draw list (hover colour from step 2 can be applied because the draw list is only consumed at render). Skip step 2 and the picture is inert; skip `Dummy` (or the equivalent `EndContent`) and the window does not grow, so scrolling and auto-size break.

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [Draw-list widget kit with a runtime-editable theme](build-custom-drawn-imgui-widgets-1.md) | Kitten-Engineer-Redux | `PanelKit` rows, tiles, gauges, toggles; `(dl, origin, width, y)` returns next `y` | `frame:draw-ui` | Total control of look; manual layout instead of ImGui auto-layout |
| 2 | [Icon buttons from primitives, no textures](build-custom-drawn-imgui-widgets-2.md) | KSArmory | Stock `Button` plus shapes drawn on `GetItemRectMin/Max` | `frame:draw-ui` | Works with any font; every icon is code |
| 3 | [One glyph function shared by map and legend](build-custom-drawn-imgui-widgets-3.md) | KSA-DeltaVMap | `static` glyph methods taking only point, radius and colours | `frame:draw-ui` | Map and legend cannot drift; glyphs fixed in screen pixels |
| 4 | [ImGuiTreeView with static abstract adapters and ChannelsSplit(2)](build-custom-drawn-imgui-widgets-4.md) | KittenExtensions | Generic tree, fills on channel 0 under labels, open state in ImGui storage | `startup:asset-load`, `frame:draw-ui` | ~440 lines to carry; strict begin/end protocol |
| 5 | [Rule list evaluated once per frame against a shared context](build-custom-drawn-imgui-widgets-5.md) | Kitten-Engineer-Redux | Alert rules drive banner, tile outline, warning row and graph line | `frame:draw-ui` | Consistent within a frame; visual only |

## Choosing

- A whole panel in one look: approach 1, then approach 5 for alerts.
- Only a few icons on otherwise stock controls: approach 2 (keep the stock button, draw on top).
- A symbol vocabulary used on a canvas and in a legend: approach 3.
- A hierarchy with selection that can be driven from elsewhere: approach 4.
- If the stock look is acceptable, prefer the game's own style (see [Make mod UI look like the game: console widgets, window skin and global theme](../../../../howto/ui/windows/match-the-stock-console-ui-style/overview.md)) because a drawn widget has to re-implement the hover and active feedback, spacing and sizing that stock widgets provide for free.

## See also

- [Organise a mod window: tabs, button tabs, tables and pop-out sections](../../../../howto/ui/windows/build-a-tabbed-imgui-window/overview.md)
- [Make mod UI look like the game: console widgets, window skin and global theme](../../../../howto/ui/windows/match-the-stock-console-ui-style/overview.md)
- [Show live vehicle and editor data in an ImGui window or HUD](../../../../howto/ui/windows/show-live-vehicle-data-in-a-window/overview.md)
- [Draw charts, scopes and relief maps with the ImGui draw list](../../../../howto/ui/draw-lists/draw-a-chart-with-imdrawlist/overview.md)
- [Organise a mod window: tabs, button tabs, tables and pop-out sections](../../windows/build-a-tabbed-imgui-window/overview.md) - related topic
- [Make mod UI look like the game: console widgets, window skin and global theme](../../windows/match-the-stock-console-ui-style/overview.md) - related topic
- [Show live vehicle and editor data in an ImGui window or HUD](../../windows/show-live-vehicle-data-in-a-window/overview.md) - related topic
- [Draw charts, scopes and relief maps with the ImGui draw list](../draw-a-chart-with-imdrawlist/overview.md) - related topic

[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imdrawlistextensions]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imguistorageextensions]: Brutal.ImGuiApi.ImGuiStorageExtensions (decompiled, build 2026.10.10.5554)
[^ksa-imguihelper]: KSA.ImGuiHelper (decompiled, build 2026.10.10.5554)
[^brutal-numerics-floatrect]: Brutal.Numerics.floatRect (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
