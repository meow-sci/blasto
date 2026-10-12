---
type: How-To
title: Hide the stock flight HUD with GaugeCanvas.SetEnabled and restore only what you changed
description: Disable every enabled, not AlwaysEnabled GaugeCanvas while a replacement HUD is shown, remember which ones you switched off, restore them on exit, unload or self-disable, and pause while the game's LAYOUTS window is open because saved layouts capture the enabled flag
category: ui
group: menus
topic: work-with-stock-cockpit-gauges
approach: 4
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-public-telemetry/features/hide-and-restore-stock-flight-hud]
techniques: [imgui-window]
hooks: [starmap StarMapAfterGui, starmap StarMapUnload]
lifecycle: [frame:draw-ui, event:starmap-unload]
game_types: [KSA.GaugeCanvas, KSA.LayoutSaves, KSA.SerializedLayout, KSA.ImGuiWindow, KSA.Program]
tags: [hud, gaugecanvas, setenabled, layouts, restore, imgui-window-lookup]
---
KSA-Public-Telemetry draws its own broadcast-style flight overlay. By default it replaces the game's flight HUD (navball, gauges, panels) while the overlay is visible and puts the HUD back when the overlay is turned off, the mod unloads or it disables itself after errors. It does this with the public `GaugeCanvas` API and no patches: it switches canvases off, remembers exactly which ones it switched off, and switches only those back on. A canvas the player had already hidden stays hidden. Prerequisites: StarMap, and a per-frame hook that is still running when the HUD is on (`[StarMapAfterGui]`). Demonstrated by [Hide and restore the stock flight HUD](../../../../mods/ksa-public-telemetry/features/hide-and-restore-stock-flight-hud.md).

## Background: how the game does it

Every stock HUD element is a `GaugeCanvas`. `GaugeCanvas.AllCanvases` lists them, `Enabled` reads the flag, `AlwaysEnabled` (an XML attribute) marks canvases that cannot be switched off, and `SetEnabled(bool)` writes `value || AlwaysEnabled`:

```csharp
// current/decomp/KSA/GaugeCanvas.cs L664-L667
	public void SetEnabled(bool value)
	{
		_enabled = value || AlwaysEnabled;
	}
```

([current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L177-L187),[^ksa-ksa-gaugecanvas] flag at [current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L100-L101)). A disabled canvas returns early from `OnDrawUi` ([current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L717-L723)) and is skipped by `CanvasesRender` ([current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L1347-L1357)). Of the 14 canvases in `Content/Core/Gauges.xml` only `Burn` is `AlwaysEnabled="true"` ([current/Content/Core/Gauges.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Gauges.xml#L294)),[^ksa-current-content-core-gauges-xml] so the navball, altitude, telemetry, resources, sequence, crew portraits and the control canvases can all be hidden while the burn editor stays. Menu bar, vehicle windows and other ImGui windows are not canvases.

**Layout saves capture the enabled flag.** The game's LAYOUTS window serialises each canvas through `ExportLayout` (`Enabled = _enabled`) and `SerializedLayout.CaptureCurrent` ([current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L1208-L1220), [current/decomp/KSA/SerializedLayout.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedLayout.cs#L20-L32)),[^ksa-ksa-serializedlayout] so a layout saved while you have the HUD hidden would store the hidden state. The mod therefore restores the HUD while that window is open.

## Lifecycle and timing

- **`frame:draw-ui`.** The toggle runs inside the overlay draw from `[StarMapAfterGui]`. The stock canvases draw later in the same frame (`GaugeCanvas.CanvasesOnDrawUi()` in `OnDrawUiThreadSafe`, and `CanvasesRender` in the render pass: [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3198-L3206),[^ksa-ksa-program] [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4937-L4940)), so a change takes effect in the frame it is made. `GaugeCanvas.UpdateCanvasesOnFrame` still calls `OnFrame` on every canvas earlier in the frame, hidden or not.
- **`WasActive` is last frame's state, but LAYOUTS is submitted earlier.** The LAYOUTS window is an `IStaticWindow` drawn by `ImGuiWindow.DrawAllStaticWindows` in `OnDrawUiFrame` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3131-L3141)), before the StarMap `AfterGui` hook, so by then it has already been submitted this frame.
- **HUD hidden with F2.** Canvases are not drawn at all when `DrawUI` is false, so the toggle has no visible effect until F2 is pressed again.
- **`event:starmap-unload`.** `[StarMapUnload]` runs at shutdown, after the game's loop; the restore there only flips in-memory flags. The restores that matter are the toggle-off path and the self-disable path.
- See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Suppress and remember.** Each frame while hiding: only act when a vehicle is controlled (so menus and the editor are left alone); re-disable anything you suppressed that something else turned back on; and whenever `AllCanvases.Count` changes, disable newly enabled, non-`AlwaysEnabled`, not-yet-known canvases and record them:

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

2. **Restore only your list.** Re-enable each recorded canvas that still exists in `AllCanvases`, then clear the list:

```csharp
// Rendering/FlightUiController.cs L73-L96
    public void Restore()
    {
        _hidden = false;
        _seenCanvasCount = -1;

        if (_suppressed.Count == 0)
        {
            return;
        }

        IReadOnlyList<GaugeCanvas> canvases = GaugeCanvas.AllCanvases;

        for (int i = 0; i < _suppressed.Count; i++)
        {
            GaugeCanvas canvas = _suppressed[i];

            if (canvases.Contains(canvas))
            {
                canvas.SetEnabled(true);
            }
        }

        _suppressed.Clear();
    }
```

3. **Decide when to hide.** The mod hides while its overlay or its outro animation is visible, the user setting allows it, and the layout window is not open, so the HUD only returns after the outro finishes:

```csharp
// Rendering/OverlayRenderer.cs L104-L106
        // The game HUD waits for the outro to finish, so the two never overlap.
        _flightUi.SetHidden(
            (overlayVisible || _outroActive) && _config.ReplaceFlightUi && !LayoutWindowOpen());
```

4. **Detect the game's LAYOUTS window without a reference to it.** `LayoutSaves.LayoutSavesWindow` is private, so find it by ImGui window name. The name is not written in the game: it is what `ImGuiWindow` builds, `SetWindowTitle("LAYOUTS")` gives `"LAYOUTS###" + debugName` and the debug name is the window's full type name plus `_`:

```csharp
// Rendering/OverlayRenderer.cs L158-L162
    private static bool LayoutWindowOpen()
    {
        ImGuiWindowPtr window = ImGui.Internal.FindWindowByName("LAYOUTS###KSA.LayoutSaves+LayoutSavesWindow_"u8);
        return !window.IsNull() && window.WasActive;
    }
```

```csharp
// current/decomp/KSA/ImGuiWindow.cs L140-L144
	public void SetWindowTitle(string name)
	{
		_displayTitle = name;
		_windowTitle = name + "###" + _debugName;
	}
```

   ([current/decomp/KSA/LayoutSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LayoutSaves.cs#L15-L24)[^ksa-ksa-layoutsaves] sets the title; [current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L85-L111)[^ksa-ksa-imguiwindow] builds the debug name; `ImGui.Internal.FindWindowByName` is the public lookup, [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L45-L50).)[^ksa-brutal-imguiapi-imgui]

5. **Restore on every exit path.** Unload, repeated errors (self-disable) and plain toggle-off all call `Restore`; guard each in `try`/`catch` so a failure to restore is logged, not thrown:

```csharp
// TelemetryOverlayMod.cs L149-L171
    [StarMapUnload]
    public void OnUnload()
    {
        if (_config is not null)
        {
            _windows.CaptureInto(_config);
            ConfigStore.SaveNow();
        }

        _windows.HideAll();

        try
        {
            _renderer?.RestoreFlightUi();
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"could not restore the flight HUD: {ex.Message}");
        }

        TelemetrySampler.Reset();
        Console.WriteLine(LogPrefix + "unloaded");
    }
```

6. **Check in game.** With the overlay on, the stock navball and gauges disappear and the burn editor still works; open the game's LAYOUTS window and the HUD comes back; turn the overlay off or press the setting off and everything you hid returns, but anything the player had hidden beforehand stays hidden.

## Complete example

| File | Role |
|---|---|
| [Rendering/FlightUiController.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/FlightUiController.cs#L1-L97) | Suppress, maintain, restore |
| [Rendering/OverlayRenderer.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayRenderer.cs#L104-L106) | When to hide |
| [Rendering/OverlayRenderer.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayRenderer.cs#L158-L162) | LAYOUTS window detection |
| [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L149-L171) | Restore on unload |

## Variations and gotchas

- **State lives in the canvases.** `SetEnabled` does not mark a canvas dirty, and the enabled flags are only captured when a layout is created or overwritten (the two `SerializedLayout.CaptureCurrent` callers in `LayoutSave`), so a crash while the HUD is suppressed persists nothing unless a layout was saved at that moment, which is exactly what the LAYOUTS exception prevents.
- **Name-coupled detection.** Renaming or re-namespacing `LayoutSavesWindow` silently breaks the exception; the HUD would stay hidden while saving.
- **Other mods toggling canvases** are overridden every frame while hidden because the controller re-disables its list.
- **New canvases** created while hidden (the count changes) are suppressed too and restored later. A canvas that something else added disabled-by-intent is only re-enabled on restore if it was enabled when you suppressed it.
- **Public API, no reflection.** Despite the plan's technique label, `AllCanvases`, `Enabled`, `AlwaysEnabled`, `SetEnabled` and `ImGui.Internal.FindWindowByName` are all public, so nothing needs reflection.
- **Hidden UI.** For a mod whose overlay must keep drawing under F2, see [Draw UI while the HUD is hidden](../../overlays/draw-ui-while-the-hud-is-hidden/overview.md).

## See also

- [Overview](overview.md)
- [Approach 3: show the engine panel for an engineless craft](work-with-stock-cockpit-gauges-3.md)
- [Draw UI while the HUD is hidden](../../overlays/draw-ui-while-the-hud-is-hidden/overview.md)
- [Draw a fullscreen click-through overlay](../../overlays/draw-a-fullscreen-click-through-overlay/overview.md)
- [Clean up on game exit](../../../getting-started/lifecycle/clean-up-on-game-exit/overview.md)
- Mod feature: [Hide and restore the stock flight HUD](../../../../mods/ksa-public-telemetry/features/hide-and-restore-stock-flight-hud.md)

[^ksa-ksa-gaugecanvas]: KSA.GaugeCanvas (decompiled, build 2026.10.10.5554)
[^ksa-current-content-core-gauges-xml]: Game content: current/Content/Core/Gauges.xml
[^ksa-ksa-serializedlayout]: KSA.SerializedLayout (decompiled, build 2026.10.10.5554)
[^ksa-ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-ksa-layoutsaves]: KSA.LayoutSaves (decompiled, build 2026.10.10.5554)
[^ksa-ksa-imguiwindow]: KSA.ImGuiWindow (decompiled, build 2026.10.10.5554)
[^ksa-brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
