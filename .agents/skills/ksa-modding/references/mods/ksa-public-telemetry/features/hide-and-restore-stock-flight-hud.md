---
type: Mod Feature
title: Hide and restore the stock flight HUD
description: Disables every enabled, non-AlwaysEnabled GaugeCanvas while the overlay is up and re-enables exactly those afterwards, pausing while the game's LAYOUTS window is open so a saved HUD layout does not record everything as switched off
mod: ksa-public-telemetry
category: ui
techniques: [imgui-window-lookup, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, harmony-postfix KSA.Program.DrawFps, starmap StarMapUnload]
lifecycle: [frame:draw-ui, event:starmap-unload]
game_types:
  - KSA.GaugeCanvas
  - KSA.LayoutSaves
  - KSA.SerializedLayout
  - KSA.ImGuiWindow
  - KSA.Program
  - Brutal.ImGuiApi.ImGui
tags: [hud, gauges, gauge-canvas, layouts, flight-ui, imgui]
---

By default the overlay replaces the game's flight HUD (navball, gauges, panels) while it is visible,
and puts it back when the overlay is turned off, the mod is unloaded, or the mod disables itself after
errors. Only canvases the mod itself switched off are switched back on, so a canvas the player had
already hidden stays hidden. While the game's own layout manager is open the HUD comes back, because
saving a layout records each canvas's enabled flag.[^kpt-readme]

## How it works

1. **What the flight HUD is.** Every stock HUD element is a `GaugeCanvas`; the static
   `GaugeCanvas.AllCanvases` lists them, `Enabled` reads the flag, `AlwaysEnabled` (an XML attribute)
   marks canvases that cannot be turned off, and `SetEnabled(bool)` writes `value || AlwaysEnabled`
   ([current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L100-L101), [current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L177-L187),
   [current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L664-L667)). A disabled canvas returns early from `OnDrawUi`
   (its ImGui interaction layer) and is skipped by `CanvasesRender`, which draws the gauges in the
   render pass ([current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L717-L722),
   [current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L1347-L1357)).[^ksa-gaugecanvas] Of the 14 stock canvases in
   `Content/Core/Gauges.xml` only `Burn` is `AlwaysEnabled="true"`, so the mod hides the navball,
   altitude, telemetry, resources, sequence, crew portraits and the other control canvases, and leaves
   the burn canvas alone ([current/Content/Core/Gauges.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Gauges.xml#L294)).[^ksa-gauges-xml] Menu bar,
   vehicle windows and other ImGui windows are not canvases and stay as they are.
2. **Suppress and remember.** `FlightUiController.SetHidden(true)` runs every frame while hiding. It
   re-disables anything it suppressed that something else turned back on, and whenever
   `AllCanvases.Count` changes it scans for canvases that are enabled, not `AlwaysEnabled` and not yet
   in its list, disables them and records them ([Rendering/FlightUiController.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/FlightUiController.cs#L14-L71)). It only
   acts when `Program.ControlledVehicle` is set, so menus and the editor are left alone.
3. **Restore.** `Restore()` re-enables each recorded canvas that still exists in `AllCanvases` and
   clears the list ([Rendering/FlightUiController.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/FlightUiController.cs#L73-L96)). It is called when hiding stops,
   from `[StarMapUnload]`, and when the mod disables itself after repeated errors
   ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L149-L171), [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L224-L235)).
4. **When to hide.** `OverlayRenderer.Draw` calls
   `SetHidden((overlayVisible || outroActive) && config.ReplaceFlightUi && !LayoutWindowOpen())`, so the
   HUD only returns after the outro animation finishes ([Rendering/OverlayRenderer.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayRenderer.cs#L104-L106)).
5. **The layout-window exception.** `LayoutWindowOpen` looks up the game's layout manager by its full
   ImGui window name, `"LAYOUTS###KSA.LayoutSaves+LayoutSavesWindow_"`, with
   `ImGui.Internal.FindWindowByName` and checks `WasActive`
   ([Rendering/OverlayRenderer.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayRenderer.cs#L158-L162)). That name is not written anywhere in the game: it is
   what `KSA.ImGuiWindow` produces, `SetWindowTitle("LAYOUTS")` plus `"###"` plus a debug name made of
   the window's full type name and an underscore ([current/decomp/KSA/LayoutSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LayoutSaves.cs#L15-L24),
   [current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L89-L111), [current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L140-L144)).[^ksa-imguiwindow]
   Layout saves serialise each canvas with `Enabled = _enabled` through
   `SerializedLayout.CaptureCurrent` ([current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L1208-L1220),
   [current/decomp/KSA/SerializedLayout.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedLayout.cs#L20-L32)), which is why the HUD must be visible while saving.[^ksa-serializedlayout]

## Lifecycle and timing

- **`frame:draw-ui`.** The toggle runs inside the overlay draw from `[StarMapAfterGui]` (postfix of
  `Program.OnDrawUiViewports`). The stock canvases draw later in the same frame, in
  `OnDrawUiThreadSafe` (`GaugeCanvas.CanvasesOnDrawUi`, [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3178-L3203)) and in
  the gauges render pass (`GaugeCanvas.CanvasesRender` in `RenderGame`, [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4937-L4940)), so a change
  takes effect in the frame it is made.[^ksa-program] `GaugeCanvas.UpdateCanvasesOnFrame` still calls
  `OnFrame` on every canvas earlier in the frame, hidden or not.
- **`WasActive` is last frame's state.** `LAYOUTS` is an `IStaticWindow` drawn by
  `ImGuiWindow.DrawAllStaticWindows` in `OnDrawUiFrame`, before the StarMap hook
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3131-L3140)), so by then the window has already been submitted this frame too.
- **While the HUD is hidden with F2** the draw comes from the `DrawFps` postfix and keeps the canvases
  suppressed (the LAYOUTS window is not drawn then, so the exception never applies). Canvases are not
  drawn at all with `DrawUI` false, in either place, so the toggle has no visible effect until F2 is
  pressed again.
- **`event:starmap-unload`.** `[StarMapUnload]` runs from StarMap's `ModLoader.Dispose` at shutdown
  ([StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L143-L151)), after the game's loop has returned and
  the `Program` has been disposed.[^starmap-modloader] The restore there only flips in-memory flags; the
  restores that matter are the toggle-off path and the self-disable path.

## Key code

| Path | Role |
|---|---|
| [Rendering/FlightUiController.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/FlightUiController.cs#L1-L97) | Suppress, maintain, restore |
| [Rendering/OverlayRenderer.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayRenderer.cs#L104-L106) | When to hide |
| [Rendering/OverlayRenderer.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayRenderer.cs#L158-L162) | LAYOUTS window detection |
| [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L149-L171) | Restore on unload |

## Snippets

```csharp
// Rendering/FlightUiController.cs L34-L71
    private void Maintain()
    {
        // Only touch the HUD in flight
        if (Program.ControlledVehicle is null)
        {
            return;
        }

        for (int i = 0; i < _suppressed.Count; i++)
        {
            if (_suppressed[i].Enabled)
            {
                _suppressed[i].SetEnabled(false);
            }
        }

        IReadOnlyList<GaugeCanvas> canvases = GaugeCanvas.AllCanvases;

        if (canvases.Count == _seenCanvasCount)
        {
            return;
        }

        _seenCanvasCount = canvases.Count;

        for (int i = 0; i < canvases.Count; i++)
        {
            GaugeCanvas canvas = canvases[i];

            if (!canvas.Enabled || canvas.AlwaysEnabled || _suppressed.Contains(canvas))
            {
                continue;
            }

            canvas.SetEnabled(false);
            _suppressed.Add(canvas);
        }
    }
```

```csharp
// Rendering/OverlayRenderer.cs L158-L162
    private static bool LayoutWindowOpen()
    {
        ImGuiWindowPtr window = ImGui.Internal.FindWindowByName("LAYOUTS###KSA.LayoutSaves+LayoutSavesWindow_"u8);
        return !window.IsNull() && window.WasActive;
    }
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `GaugeCanvas.AllCanvases`, `Enabled`, `AlwaysEnabled`, `SetEnabled` | [current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L100-L101), [current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L177-L187), [current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L664-L667) | Enumerate and toggle HUD canvases |
| `GaugeCanvas.ExportLayout`, `SerializedLayout.CaptureCurrent` | [current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L1208-L1220), [current/decomp/KSA/SerializedLayout.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedLayout.cs#L20-L32) | Why layouts capture the enabled flag |
| `LayoutSaves.LayoutSavesWindow` | [current/decomp/KSA/LayoutSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LayoutSaves.cs#L15-L24) | The window whose name is matched |
| `ImGuiWindow` title and debug name | [current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L89-L111), [current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L140-L144) | How `###` ids are formed |
| `ImGui.Internal.FindWindowByName`, `ImGuiWindowPtr.WasActive` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L45-L50), [current/decomp/Brutal.ImGuiApi.Internal/ImGuiWindowPtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi.Internal/ImGuiWindowPtr.cs#L104) | Window lookup |
| `Program.ControlledVehicle` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502) | Only touch the HUD in flight |

## Pitfalls and version notes

- **State lives in the canvases.** `SetEnabled` does not mark a canvas dirty, and the enabled flags are
  only captured when a layout is created or overwritten (`SerializedLayout.CaptureCurrent` callers in
  [current/decomp/KSA/LayoutSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LayoutSave.cs#L56), [current/decomp/KSA/LayoutSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LayoutSave.cs#L125)), so a crash
  while the HUD is suppressed leaves nothing persisted unless a layout was saved at that moment, which
  is exactly what the LAYOUTS exception prevents.[^ksa-layoutsave]
- **Name-coupled detection.** Renaming or re-namespacing `LayoutSavesWindow` silently breaks the
  exception; the HUD would stay hidden while saving.
- **Other mods toggling canvases** are overridden every frame while hidden, because the controller
  re-disables its list.
- **New canvases** created while hidden (count change) are suppressed too and restored later, which is
  the intended behaviour but means a canvas that a mod adds disabled-by-intent can be enabled on
  restore only if it was enabled when suppressed.

## See also

- [Broadcast overlay on the ImGui foreground draw list](foreground-drawlist-broadcast-overlay.md)
- [Keep drawing while the game UI is hidden](draw-while-ui-hidden-drawfps-postfix.md)
- [Error budget and self-disable](error-budget-self-disable.md)
- [Hide the stock flight HUD with GaugeCanvas.SetEnabled and restore only what you changed](../../../howto/ui/menus/work-with-stock-cockpit-gauges/work-with-stock-cockpit-gauges-4.md) - how-to article demonstrating this feature

[^kpt-readme]: README.md
[^ksa-gaugecanvas]: KSA.GaugeCanvas (decompiled, build 2026.10.10.5554)
[^ksa-imguiwindow]: KSA.ImGuiWindow (decompiled, build 2026.10.10.5554)
[^ksa-serializedlayout]: KSA.SerializedLayout (decompiled, build 2026.10.10.5554)
[^ksa-gauges-xml]: Content/Core/Gauges.xml (build 2026.10.10.5554)
[^ksa-layoutsave]: KSA.LayoutSave (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^starmap-modloader]: StarMap StarMap.Core/ModRepository/ModLoader.cs
