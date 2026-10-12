---
type: Game Feature
title: Add to, drive and hide the stock cockpit gauges and navball
description: How KSA's XML-defined gauge canvases are loaded, drawn and fed, and five ways mods add a button, rewrite a button's state, force a panel visible, hide the flight HUD or paint on the navball
category: ui
group: menus
topic: work-with-stock-cockpit-gauges
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/autostage-gauge-button
  - mods/ksa-advancedflightcomputer/features/rcs-burn-editor-and-gauge-ui
  - mods/ksacars/features/engine-hud-for-engineless-craft
  - mods/ksa-public-telemetry/features/hide-and-restore-stock-flight-hud
  - mods/ohmystars/features/navball-star-marker
lifecycle:
  - startup:immediate-load
  - startup:asset-load
  - frame:draw-ui
  - frame:input
  - frame:render
  - frame:gauges
  - startup:all-mods-loaded
  - frame:dispatch-solvers
  - event:starmap-unload
game_types:
  - KSA.GaugeCanvas
  - KSA.GaugeButtonFlightComputer
  - KSA.EnumTypeOption
  - KSA.GaugeVisibilityFlag
  - KSA.ManualControlInputs
  - KSA.FixedAlert
  - KSA.Vehicle
  - KSA.ModLibrary
  - KSA.LayoutSaves
  - KSA.ImGuiWindow
  - KSA.Program
  - KSA.NavBallRenderer
  - KSA.CanvasRect
  - KSA.ImGuiHelper
  - KSA.SerializedLayout
  - KSA.GaugeBase
tags: [gauges, gaugecanvas, gaugebutton, navball, hud, xml-patch, flight-computer, enum-binding]
---
The flight HUD in KSA is not ImGui widgets. It is a set of **gauge canvases** defined in XML (`Content/Core/Gauges.xml`): the navball, engine and burn controls, telemetry readouts, resources, crew portraits and more. A canvas is an ImGui window that reserves a rectangle and adds a draw callback; the GPU then renders the gauge components (digit rollers, buttons, the navball sphere) into it. That has three consequences for mods: new buttons are **data** (an XML patch) bound to **code** (an enum type name), the state a button shows comes from `Vehicle` methods that Harmony can answer for, and anything you draw yourself over a gauge must go on a draw list that sits above the canvas windows.

Five reference-mod techniques follow, from adding a stock-looking button to hiding the whole HUD.

## Engine background

| Piece | Where | Role |
|---|---|---|
| `Gauges.xml` canvases | [current/Content/Core/Gauges.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Gauges.xml#L765-L913)[^ksa-current-content-core-gauges-xml] | `<GaugeCanvas Id="EngineControl">` has a `Buttons` component with RCS, IGNITE and SHUTDN `<Button>`s, each bound by `<FlightComputer Action="..." Value="..." Toggle="...">`. `Navball` canvas with a `Ball` component is at [current/Content/Core/Gauges.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Gauges.xml#L2236-L2242) |
| `GaugeCanvas` | [current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L98-L104)[^ksa-ksa-gaugecanvas] | Static `AllCanvases`; `Enabled` / `SetEnabled(bool)` ([current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L664-L667)); `AlwaysEnabled`; `VisibleInContext` flags tested by `IsContextVisible()` ([current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L238-L276)) |
| Canvas draw | `GaugeCanvas.OnDrawUi` | Returns early unless enabled and context-visible; otherwise begins an ImGui window and adds a draw callback ([current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L717-L723), [current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L857)) |
| Button binding | `GaugeButtonFlightComputer` | Private static `List<EnumTypeOption> EnumTypes` filled by the static constructor; `OnDataLoad` resolves the XML `Action` attribute by `Type.Name` with first-match-wins ([current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L15-L99)[^ksa-ksa-gaugebuttonflightcomputer]); `IsDisabled()`, `PackData()` and `OnReleased` ([current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L135-L173)) |
| `EnumTypeOption` | [current/decomp/KSA/EnumTypeOption.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EnumTypeOption.cs)[^ksa-ksa-enumtypeoption] | `readonly record struct EnumTypeOption(Type Type)`; `GetName()` is `Type.Name` |
| State queries | `Vehicle.IsSet<T>(T, bool)`, `IsFlightComputerDisabled<T>(T)`, `ToggleEnum(Enum?)` | `public virtual` generics and a method that decide what a button shows and does ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6431), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6860), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6266))[^ksa-ksa-vehicle] |
| Click path | `InputEvents.FlightComputerInputBuffer` | `OnReleased` queues; `ApplyAll()` runs with the other input buffers ([current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L592-L605), [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1129))[^ksa-ksa-inputevents] |
| Load order | `Program` | `ModLibrary.PrepareAll()` (registers each enabled mod and calls `PrepareSystems`), then `PreloadAssetBundles()` and later `LoadAll()` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899-L942),[^ksa-ksa-program] [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L523)[^ksa-ksa-modlibrary]) |
| Navball maths | `NavBallRenderer` | `Body2Screen` and `ToScreenMarker` ([current/decomp/KSA/NavBallRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NavBallRenderer.cs#L22), [current/decomp/KSA/NavBallRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NavBallRenderer.cs#L78-L86)[^ksa-ksa-navballrenderer] |

**Frame order** (`Program.OnFrame`, build 2026.10.10.5554): `GaugeCanvas.UpdateCanvasesOnFrame()` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2250-L2254)), then `OnDrawUiFrame`/`OnDrawUiViewports` (the StarMap `BeforeGui`/`AfterGui` hooks), then `OnDrawUiThreadSafe` where `GaugeCanvas.CanvasesOnDrawUi()` runs ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3198-L3206)), `ImGui.Render`, `UpdateGauges(vehicle, dt)` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2296-L2302)) which sets the digit rollers (for example `ThrottleRoller.SetTarget((int)(vehicle.GetManualThrottle() * 100f))`, [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2792)), and finally `Render` where `GaugeCanvas.CanvasesRender` draws the gauge pass ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4937-L4940)). Button state bits (`PackData`) are read in that render pass, not in the UI pass. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

**Game lifecycle values used by the approaches:** `startup:immediate-load` (register an enum before the XML is bound), `startup:asset-load` (XML patches applied by KittenExtensions), `frame:input` (button clicks applied), `frame:draw-ui` (visibility and enable tests, ImGui draw), `frame:gauges` (`UpdateGauges` after `ImGui.Render`) and `frame:render` (`PackData` during the gauge pass of `Program.RenderGame`).

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [Register the enum at immediate load, append the Button by XML patch](work-with-stock-cockpit-gauges-1.md) | KSA-AdvancedFlightComputer | `xml-patch` + `harmony-prefix` | `startup:immediate-load`, `startup:asset-load`, `frame:input` | A real, stock-looking button; needs KittenExtensions and a load-order-sensitive reflection write |
| 2 | [Rewrite disabled bits and tooltip in postfixes](work-with-stock-cockpit-gauges-2.md) | KSA-AdvancedFlightComputer | `harmony-postfix` | `frame:draw-ui`, `frame:render` | Re-enable a stock button for a case stock disables; patch the concrete class, not the generics |
| 3 | [IsContextVisible and AreAllEnginesInactive postfixes plus a throttle write by reflection](work-with-stock-cockpit-gauges-3.md) | KSACars | `harmony-postfix` + `reflection` | `frame:draw-ui`, `frame:gauges`, `frame:dispatch-solvers` | Show stock panels for a craft with no engines; the throttle value is display only |
| 4 | [Toggle GaugeCanvas.SetEnabled and remember what was changed](work-with-stock-cockpit-gauges-4.md) | KSA-Public-Telemetry | `reflection` (state) | `frame:draw-ui`, `event:starmap-unload` | Hide and restore the whole flight HUD; must pause while the layout window saves |
| 5 | [Draw a navball marker on the foreground draw list](work-with-stock-cockpit-gauges-5.md) | OhMyStars | `imgui-drawlist` | `frame:draw-ui` | Pixel-matched marker without touching the shader; copied constants drift on game updates |

## Choosing

- A new **control** that behaves like the stock ones: approach 1.
- Make a **stock control work in a case stock rules out**, or show a stock panel for a craft the stock conditions miss: approaches 2 and 3 (postfix on public or concrete methods).
- **Remove** the stock HUD for your own: approach 4.
- Draw **on top of** a gauge: approach 5. Everything the canvas draws is behind an ImGui draw callback, so only the foreground draw list (or a later window) goes above it.

## See also

- [Inject UI into the game's own windows and menus](../inject-ui-into-a-stock-window/overview.md)
- [Match the stock console UI style](../../windows/match-the-stock-console-ui-style/overview.md)
- [Coordinate ImGui focus with game input](../../input/coordinate-imgui-focus-with-game-input/overview.md)
- [Draw a fullscreen click-through overlay](../../overlays/draw-a-fullscreen-click-through-overlay/overview.md)
- [XPath XML patch recipes](../../../assets/xml-patching/xpath-xml-patch-recipes/overview.md)
- [Project world positions to the screen](../../../rendering/overlays/project-world-positions-to-screen/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)

[^ksa-current-content-core-gauges-xml]: Game content: current/Content/Core/Gauges.xml
[^ksa-ksa-gaugecanvas]: KSA.GaugeCanvas (decompiled, build 2026.10.10.5554)
[^ksa-ksa-gaugebuttonflightcomputer]: KSA.GaugeButtonFlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-ksa-enumtypeoption]: KSA.EnumTypeOption (decompiled, build 2026.10.10.5554)
[^ksa-ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-ksa-inputevents]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
[^ksa-ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-ksa-navballrenderer]: KSA.NavBallRenderer (decompiled, build 2026.10.10.5554)
