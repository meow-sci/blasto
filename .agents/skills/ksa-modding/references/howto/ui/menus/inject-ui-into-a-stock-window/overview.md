---
type: Game Feature
title: Inject UI into the game's own windows and menus, including the orbit right-click menu
description: How KSA's stock settings, orbit context menu, transfer planner and burn editors are drawn, and four Harmony techniques for adding controls to them at an exact spot
category: ui
group: menus
topic: inject-ui-into-a-stock-window
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/mods-settings-page-sections
  - mods/ksa-advancedflightcomputer/features/orbit-context-menu-shortcuts
  - mods/ksa-advancedflightcomputer/features/plan-window-transpiler-injection
  - mods/ksa-advancedflightcomputer/features/rcs-burn-editor-and-gauge-ui
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types:
  - KSA.GameSettings
  - KSA.BurnContextMenu
  - KSA.TransferPlanner
  - KSA.Burn
  - KSA.BurnCanvasHost
  - KSA.GaugeCanvas
  - KSA.ConsoleStyle
  - KSA.ConsoleWidgets
  - KSA.Program
  - KSA.Camera
  - Brutal.ImGuiApi.ImGui
  - KSA.GaugeBase
  - KSA.ImGauge
  - KSA.ImGaugeWindow
tags:
  - stock-window
  - injection
  - harmony-transpiler
  - harmony-postfix
  - context-menu
  - settings
  - transfer-planner
  - burn-editor
  - il-anchor
---
Sometimes a mod should not open its own window at all: it should add a section to a window the player already uses, a shortcut to a menu the player already opens, or a block under a stock editor. KSA draws all of these with immediate-mode ImGui from ordinary C# methods, so a Harmony patch on the right method puts your widgets in the stock window, with the stock style, in the same frame. The only reference mod that does this at scale is KSA-AdvancedFlightComputer (AFC), which injects into four stock surfaces: the Settings > Mods page, the orbit right-click menu, the Transfer Planner window and the burn editors.

This topic compares the four techniques AFC uses. The constraint they share: ImGui has no "add child to that window" API, so your code must run **while the stock method is in the middle of drawing that window**, between its `Begin` and `End` (or `BeginPopup` and `EndPopup`). A postfix runs after the method returns, which is too late if the method closes the window itself.

## Engine background

| Surface | Stock method | Where it is called | Fact that drives the technique |
|---|---|---|---|
| Settings window, Mods tab | `GameSettings.OnDrawUi(Camera)` ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L1880)[^ksa-ksa-gamesettings]) | `Program.OnDrawUiFrame` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3131-L3141)[^ksa-ksa-program]) | Every tab draws into one body; the method's only `ConsoleStyle.PopWidgetStyle()` call ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L2251)) follows the last tab. `_openTab` is a private static of the private enum `SettingsTab` ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L150-L158), [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L1104)) |
| Orbit right-click menu | `BurnContextMenu.Draw()` ([current/decomp/KSA/BurnContextMenu.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnContextMenu.cs#L259-L348)[^ksa-ksa-burncontextmenu]) | `Program.OnDrawUiThreadSafe` when the editor is closed ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3228-L3233)) | Opens popup `burn_context_menu` and closes it with `ImGui.EndPopup()`, several times on early-return paths; submenus are written as `ImGui.BeginMenu("At Periapsis"u8)` |
| Transfer Planner window | `TransferPlanner.DrawPlanWindow(IGameViewport)` ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L305)[^ksa-ksa-transferplanner]) | `OnDrawUiThreadSafe`, right after the context menu | One large method: calls private `DrawCorrectionTransfer()` ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L624)), `ConsoleStyle.PopWidgetStyle()` ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L731)), `ConsoleWidgets.PrimaryButton("CREATE")` ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L748)) then `Burn.Create(...)` ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L778)) |
| Burn editors | `Burn.DrawBurnEditorWindowContent(Vehicle, FlightComputer, bool, bool)` ([current/decomp/KSA/Burn.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Burn.cs#L727)[^ksa-ksa-burn]); `BurnCanvasHost.Draw(GaugeCanvas, float2, float2)` ([current/decomp/KSA/BurnCanvasHost.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnCanvasHost.cs#L53)[^ksa-ksa-burncanvashost]) | Content: `TargetTrackWindow` ([current/decomp/KSA/TargetTrackWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TargetTrackWindow.cs#L755)); canvas: `GaugeCanvas` when `this == Program.BurnCanvas` ([current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L866-L874)[^ksa-ksa-gaugecanvas]) | Both methods end where you want to add: nothing follows your content |

The orbit menu is opened by a two-step process: `Program.OnMouseButton` records a right click and `BurnContextMenu.TryOpen` sets `_openBurnMenuNextFrame` on release ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2024-L2044)), after which `Draw` opens the popup on the next frame.

Stock style: the console look (`ConsoleStyle.PushWidgetStyle`, `ConsoleWidgets` rows) is already pushed inside these windows when your inserted call runs; see [Match the stock console UI style](../../windows/match-the-stock-console-ui-style/overview.md).

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [One-call transpiler before PopWidgetStyle on the Mods settings page](inject-ui-into-a-stock-window-1.md) | AFC | `harmony-transpiler` | `frame:draw-ui` | Smallest transpiler (one unique anchor); draws only while a private enum says the Mods tab is open |
| 2 | [Transpiler anchored on a UTF-8 label literal in BurnContextMenu.Draw](inject-ui-into-a-stock-window-2.md) | AFC | `harmony-transpiler` | `frame:draw-ui` | Inserts a submenu inside the popup and an item inside stock submenus; reads label bytes out of IL |
| 3 | [Call-site swap inside the Transfer Planner plan window](inject-ui-into-a-stock-window-3.md) | AFC | `harmony-transpiler` | `frame:draw-ui` | Takes over a decision (what CREATE does) without replacing the window; same-signature substitute |
| 4 | [Postfixes on the burn editor and burn canvas host](inject-ui-into-a-stock-window-4.md) | AFC | `harmony-postfix` | `frame:draw-ui` | No IL; works only because the target ends where the new block should go |

## Choosing

1. If the stock method **ends** where you want to add: postfix (approach 4). Cheapest, most robust.
2. If you must land **inside** an open window or popup that the method closes itself: a transpiler inserting one `call` before a unique anchor (approaches 1 and 2).
3. If you must change what a stock button **does**: swap the call at its site and gate the click (approach 3).
4. Validate every anchor at load and degrade to "no injection, mod still loads" if it is missing, as AFC does; a stock refactor is the failure mode that matters. See [Swap one call inside a game method with a transpiler](../../../patching/transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/overview.md).

## See also

- [Swap one call inside a game method with a transpiler](../../../patching/transpilers/swap-one-call-inside-a-game-method-with-a-transpiler/overview.md)
- [Insert calls into a game method at an IL anchor](../../../patching/transpilers/insert-calls-into-a-game-method-at-an-il-anchor/overview.md)
- [Match the stock console UI style](../../windows/match-the-stock-console-ui-style/overview.md)
- [Add to, drive and hide the stock cockpit gauges and navball](../work-with-stock-cockpit-gauges/overview.md)
- [Create and read maneuver nodes](../../../flight-and-orbits/autopilot/create-and-read-maneuver-nodes/overview.md)
- [Add a plan type to the Transfer Planner](../../../flight-and-orbits/transfers/add-plan-type-to-the-transfer-planner/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)

[^ksa-ksa-gamesettings]: KSA.GameSettings (decompiled, build 2026.10.10.5554)
[^ksa-ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-ksa-burncontextmenu]: KSA.BurnContextMenu (decompiled, build 2026.10.10.5554)
[^ksa-ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-ksa-burn]: KSA.Burn (decompiled, build 2026.10.10.5554)
[^ksa-ksa-burncanvashost]: KSA.BurnCanvasHost (decompiled, build 2026.10.10.5554)
[^ksa-ksa-gaugecanvas]: KSA.GaugeCanvas (decompiled, build 2026.10.10.5554)
