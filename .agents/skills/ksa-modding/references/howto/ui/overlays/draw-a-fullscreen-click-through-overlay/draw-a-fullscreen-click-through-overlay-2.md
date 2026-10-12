---
type: How-To
title: "Overlays without a window: ImGuiHelper.GetOverlayDrawList"
description: Draw 3D-projected overlay graphics straight into the main viewport's background draw list with the stock helper, keeping them under every window, with NaN culling and a once-per-type error guard
category: ui
group: overlays
topic: draw-a-fullscreen-click-through-overlay
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by:
  - mods/ksa-measuretools/features/overlay-drawlist-rendering
  - mods/ksa-deltavmap/features/map-mode-transfer-overlay
techniques: [imgui-drawlist, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, render:imgui]
game_types:
  - KSA.ImGuiHelper
  - KSA.IViewport
  - KSA.IGameViewport
  - KSA.ViewportRegistry
  - KSA.Camera
  - Brutal.ImGuiApi.ImDrawListPtr
tags: [overlay, draw-list, background-list, projection, nan-culling, measure, map]
---
KSA's own body labels, orbit overlays and frame statistics do not use a window: they draw into the main viewport's **background draw list**, which ImGui renders under every window. The game exposes that list as a helper,
`ImGuiHelper.GetOverlayDrawList(viewport)`. KSA-MeasureTools (ruler, protractor, snap highlights) and KSA-DeltaVMap (transfer-window markers on the map) draw this way. Compared with
[the full-screen window](draw-a-fullscreen-click-through-overlay-1.md) there is no window to size, flag or end, nothing to receive input and nothing to appear over your own panels. The catch: only the main viewport
has a persistent list. Demonstrated by KSA-MeasureTools and KSA-DeltaVMap.

## Background: how the game does it

`ImGuiHelper.GetMainOverlayDrawList()` returns `ImGui.GetBackgroundDrawList(ImGui.GetMainViewport())`; `GetOverlayDrawList(viewport)` returns that for the main viewport and `ImGui.GetWindowDrawList()` for any other:

```csharp
// current/decomp/KSA/ImGuiHelper.cs L43-L51
public static ImDrawListPtr GetMainOverlayDrawList()
{
    return ImGui.GetBackgroundDrawList(ImGui.GetMainViewport());
}

public static ImDrawListPtr GetOverlayDrawList(IViewport viewport)
{
    return viewport.IsMain() ? GetMainOverlayDrawList() : ImGui.GetWindowDrawList();
}
```

- The stock `DrawFps` draws its numbers into the same list, so game windows cover the FPS text.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3248-L3268)[^ksa-program]
- Non-main game viewports draw their world UI **inside their own ImGui window** (`Universe.OnDrawUi(this)` inside `GameViewport.DrawImGui`), which is why the helper falls back to the current window list; by the
  time a StarMap GUI hook runs those windows are closed, so the fallback is only meaningful inside such a window.[current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L247-L250)[^ksa-gameviewport]
- `Camera.EclToScreen` gives viewport-local pixels with `NaN` behind the camera; add `viewport.Position`.[current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L333-L356)[^ksa-camera]

## Lifecycle and timing

Both mods draw from `[StarMapAfterGui]` (`frame:draw-ui`), after `OnFrameViewports` computed this frame's camera and with the last applied physics results, so projected points match the 3D scene. The draw list is consumed by
the game's ImGui render pass (`render:imgui`) after `ImGui.Render`. Two stock values are not yet refreshed for this frame: `MainViewport.Position` is set at the start of `OnDrawUiThreadSafe` (after your hook), so
`viewport.Position` is last frame's (it only differs on the frame the OS window moves), and the stock hover state (`ViewportPartPicker`, `Astronomical.CursorHoverEligible`) is filled in `Universe.OnDrawUi`, also after.
With the HUD hidden the overlay disappears with the rest of the UI. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and
[keep UI running when the HUD is hidden](../draw-ui-while-the-hud-is-hidden/overview.md).

## Steps

1. **Resolve the main viewport without the throwing accessor.** `ViewportRegistry.MainViewport` throws until the registry has one, so scan `GameViews`:

```csharp
// MeasureTools/Features/Measure/MeasureViewport.cs L5-L33
// The tool runs in the main viewport only, and this is the single place that says
// so. ImGuiHelper.GetOverlayDrawList gives the persistent background list to no
// other viewport (the rest draw their world UI inside their own ImGui window, long
// closed by the time [StarMapAfterGui] runs), and they are built without
// ViewportOptionFlags.AllowSelection, so stock does no part hover there either.
internal static class MeasureViewport
{
    public static bool IsHost(IViewport? viewport)
    {
        return viewport != null && viewport.IsMain();
    }

    // Scanned rather than read off Program.MainViewport, which throws before the
    // registry has one.
    public static bool TryGetActive(out IGameViewport viewport)
    {
        ReadOnlySpan<IGameViewport> views = ViewportRegistry.GameViews;
        for (int i = 0; i < views.Length; i++)
        {
            if (views[i].IsMain())
            {
                viewport = views[i];
                return true;
            }
        }
        viewport = null!;
        return false;
    }
}
```

2. **Order your AfterGui work**: prune stale state, draw the tool window, then the overlay, all behind one `try`:

```csharp
// MeasureTools/Mod.cs L57-L78
    // Prune first, so neither the window nor the overlay resolves an anchor whose
    // body is gone.
    [StarMapAfterGui]
    public void Draw(double dt)
    {
        try
        {
#if DEBUG
            using var perfScope = new PerfTracker.Scope("Mod.Draw");
#endif
            MeasureState.Prune();
            if (!MeasureViewport.TryGetActive(out IGameViewport viewport))
                return;
            MeasureWindow.DrawActive(viewport);
            MeasureOverlay.Draw(viewport);
        }
        catch (Exception ex)
        {
            // Per-frame path: first throw of each type logs a stack, then quiet.
            LogHelper.ErrorOnce("aftergui-" + ex.GetType().Name, $"[MeasureTools] Per-frame draw failed: {ex}");
        }
    }
```

3. **Get the list and the camera from the same viewport**, draw each item, and guard the whole draw so a projection bug can never unwind into the game's UI pass (the first throw of each exception type logs a stack,
   then stays quiet):

```csharp
// MeasureTools/Features/Measure/MeasureOverlay.cs L60-L88
    public static void Draw(IGameViewport viewport)
    {
        try
        {
#if DEBUG
            using var perfScope = new PerfTracker.Scope("MeasureOverlay.Draw");
#endif
            if (!MeasureWindow.IsOpen)
                return;
            if (!MeasureState.IsSupportedViewMode(viewport.Mode))
                return;
            if (Universe.CurrentSystem == null)
                return;

            Camera camera = viewport.GetCamera();
            float2 vpPos = viewport.Position;
            ImDrawListPtr dl = ImGuiHelper.GetOverlayDrawList(viewport);

            for (int i = 0; i < MeasureState.Measurements.Count; i++)
                DrawMeasurement(dl, camera, vpPos, MeasureState.Measurements[i], i == MeasureState.HighlightIndex);

            DrawPlacementPreview(dl, camera, viewport, vpPos);
        }
        catch (Exception ex)
        {
            // Per-frame path: first throw of each type logs a stack, then quiet.
            LogHelper.ErrorOnce("overlay-" + ex.GetType().Name, $"[MeasureTools] Overlay draw failed: {ex}");
        }
    }
```

4. **Cull with the NaN rule** and break polylines at invalid samples instead of drawing a wild segment:

```csharp
// MeasureTools/Features/Measure/MeasureOverlay.cs L828-L831
private static bool Valid(float2 s)
{
    return !float.IsNaN(s.X) && !float.IsNaN(s.Y) && !float.IsInfinity(s.X) && !float.IsInfinity(s.Y);
}
```

5. **Keep labels readable and allocation-free.** A dark rounded plate behind each label, and numbers formatted into a `stackalloc` buffer passed straight to ImGui:

```csharp
// MeasureTools/Features/Measure/MeasureOverlay.cs L774-L791
private static void Label(ImDrawListPtr dl, float2 pos, ImString text)
{
    // Background plate so labels stay readable over orbit lines, planet discs
    // and other labels.
    float2 size = ImGui.CalcTextSize(text);
    var pMin = new float2(pos.X - 4f, pos.Y - 2f);
    var pMax = new float2(pos.X + size.X + 4f, pos.Y + size.Y + 2f);
    dl.AddRectFilled(in pMin, in pMax, LabelPlate, 3f);
    dl.AddText(in pos, LabelColor, text);
}

// Distance labels without prefix text: ToNearest writes into the stack
// buffer and the span goes straight into the ImGui arena.
private static void LabelDistance(ImDrawListPtr dl, float2 pos, double meters)
{
    Span<char> buffer = stackalloc char[64];
    Label(dl, pos, DistanceReference.ToNearest(meters, buffer));
}
```

6. **Throttle expensive picks by wall-clock time**, not frame count, and invalidate on state changes (a mode change, a placed point, a modifier key):

```csharp
// MeasureTools/Features/Measure/MeasureOverlay.cs L403-L436
float2 mouseViewport = Cursor.GetPosition(viewport);
// A cursor outside the viewport cannot place a point, and a cursor that
// left the OS window reports far outside it; either way picking over
// that position is wasted work.
if (mouseViewport.X < 0f || mouseViewport.Y < 0f
    || mouseViewport.X > viewport.Size.X || mouseViewport.Y > viewport.Size.Y)
{
    Reset();
    return;
}
// Ctrl previews (and places) a free point on the ecliptic plane even where
// snapping would win; a modifier change re-picks immediately so the preview
// flips with the key.
bool eclipticFree = ImGui.GetIO().KeyCtrl;
long now = Stopwatch.GetTimestamp();
if (Stopwatch.GetElapsedTime(_previewLastPickTimestamp, now).TotalMilliseconds >= PreviewPickIntervalMs
    || _previewStateVersion != MeasureState.StateVersion
    || _previewEclipticFree != eclipticFree)
{
    if (MeasureState.Mode == MeasureMode.Circle)
    {
        MapPicker.PickCircle(viewport, mouseViewport, out Anchor? circleCenter, out Anchor? circleRim, reuseStockHover: true);
        _previewCache = circleRim;
        _previewCacheSecondary = circleCenter;
    }
    else
    {
        _previewCache = MapPicker.Pick(viewport, mouseViewport, eclipticFree, reuseStockHover: true);
        _previewCacheSecondary = null;
    }
    _previewLastPickTimestamp = now;
    _previewStateVersion = MeasureState.StateVersion;
    _previewEclipticFree = eclipticFree;
}
```

7. **Other viewports and modes.** KSA-DeltaVMap's overlay takes the viewport it is given, returns unless `viewport.Mode == CameraMode.Map`, and uses that viewport's camera rather than `Program`'s:

```csharp
// DeltaVMap/Render/TransferWindowMapOverlay.cs L59-L96
public void Draw(IViewport viewport, IReadOnlyList<TransferWindowInfo> windows, string? emphasisHint)
{
    if (windows.Count == 0)
        return;
    try
    {
        if (viewport.Mode != CameraMode.Map)
            return;

        // This viewport's camera, not Program's: the frame viewport at this hook may differ.
        Camera camera = viewport.GetCamera();
        float2 vpPos = viewport.Position;
        ImDrawListPtr dl = ImGuiHelper.GetOverlayDrawList(viewport);

        UniverseTime now = Universe.GetElapsedTime();
        string? emphasis = ResolveEmphasis(windows, emphasisHint);
        TransferWindowInfo? focus = Find(windows, emphasis);

        // The live planet-star-planet angle for the emphasized window, drawn first so the
        // markers and the gizmo sit on top of its long hub lines.
        if (focus != null)
            DrawPhaseAngle(dl, camera, vpPos, focus, now);

        // Where each sibling will be when its window opens. The emphasized one is drawn
        // prominently and labeled; the rest are faint rings so a dense root does not smear.
        for (int i = 0; i < windows.Count; i++)
            DrawDestinationMarker(dl, camera, vpPos, windows[i], now, windows[i].TargetId == emphasis);

        // The ejection-angle gizmo, only for the emphasized window, at the departure body.
        if (focus != null)
            DrawEjectionGizmo(dl, camera, vpPos, focus, now);
    }
    catch (Exception ex)
    {
        // A camera or projection change must never unwind into the render path.
        LogHelper.ErrorOnce("twindow-map-overlay", $"[DvMap] Transfer window map overlay failed: {ex}");
    }
}
```

## Complete example

| File | Role |
|---|---|
| [MeasureTools/Features/Measure/MeasureOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureOverlay.cs) | Throttle state, entry point, per-mode drawing, labels, validity |
| [MeasureTools/Features/Measure/MeasureViewport.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureViewport.cs) | Main-viewport-only rule |
| [MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs#L57-L78) | AfterGui order |
| [DeltaVMap/Render/TransferWindowMapOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/TransferWindowMapOverlay.cs) | Map-mode overlay with markers and an ejection gizmo |
| [AdvancedFlightComputer/Features/MultiPass/MultiPassMarkers.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassMarkers.cs#L35-L50) | The same list in AFC, chosen so markers do not appear over a portrait viewport |

## Variations and gotchas

- **Under everything.** The background list sits beneath every ImGui window, including stock panels, which is usually what you want. Stock overlays that submit to the same list later in the frame (`IOrbiter.OnDrawUi`'s
  body outline boxes) end up above yours, because the list orders by submission.
- **Main viewport only.** `GetOverlayDrawList(nonMainViewport)` is the current window's list; calling it outside that window draws into whatever window is current. The tool restricts itself to the main viewport.
- **`EclToScreen` is viewport-local**; add `viewport.Position` before drawing into a list that covers the whole window.
- **No input.** Nothing here can be clicked; use a separate window for widgets (see approach 1).
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of draw-a-fullscreen-click-through-overlay](overview.md)
- [Approach 1: NoInputs/NoBackground fullscreen window](draw-a-fullscreen-click-through-overlay-1.md)
- [Approach 3: ImGui.GetForegroundDrawList](draw-a-fullscreen-click-through-overlay-3.md)
- [Approach 5: per-surface draw list](draw-a-fullscreen-click-through-overlay-5.md)
- [Project world and sky positions to screen pixels and point at off-screen targets](../../../rendering/overlays/project-world-positions-to-screen/overview.md)
- Mod features: [Overlay rendering](../../../../mods/ksa-measuretools/features/overlay-drawlist-rendering.md),
  [Map-mode transfer overlay](../../../../mods/ksa-deltavmap/features/map-mode-transfer-overlay.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
