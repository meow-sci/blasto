---
type: How-To
title: Project orbit positions to the map view with GetPositionEclFromCce and EclToScreen and draw markers and fixed-length arrows
description: Gate a map-view overlay on the viewport's camera mode, convert a point in a parent body's CCE frame to ecliptic and to screen pixels, add the viewport position, and draw markers, a phase-angle arc and fixed-length direction arrows on the main viewport's overlay draw list
category: flight-and-orbits
group: map-view
topic: draw-a-predicted-trajectory-and-map-markers
approach: 4
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-deltavmap/features/map-mode-transfer-overlay]
techniques: [world-to-screen-projection, imgui-drawlist, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, render:imgui]
game_types:
  - KSA.Camera
  - KSA.CameraMode
  - KSA.IViewport
  - KSA.ImGuiHelper
  - KSA.Orbit
  - KSA.IParentBody
  - KSA.IOrbiter
  - KSA.Universe
  - KSA.TransferPlanner
tags: [map-view, markers, phase-angle, ejection-angle, overlay-draw-list, projection, cce, transfer-window]
---
Outcome: markers and direction arrows that sit on the right place in the 3D map view and keep their size at any zoom: a dot on a destination body's real orbit where it will be when a transfer window opens, the live angle between two bodies seen from their common parent, and an ejection gizmo at the departure body. KSA-DeltaVMap draws these as an opt-in layer next to its transfer-window panel, using the same path the stock Transfer Planner uses for its alignment marker.[Map-mode transfer overlay](../../../../mods/ksa-deltavmap/features/map-mode-transfer-overlay.md)

Use this when the thing to mark is a position on an orbit or a direction at a body, and you can compute it in the parent's CCE frame. For a point stored on a rotating body, see [anchor and pick points on bodies](../anchor-and-pick-points-on-bodies/overview.md); for a prediction made of many points, see [approach 3](draw-a-predicted-trajectory-and-map-markers-3.md).

Prerequisites: StarMap for `[StarMapAfterGui]`.

## Background: how the game does it

- **The stock marker.** In `TransferPlanner.DrawPlanWindow` the "show parent/target alignment" checkbox turns on a blue dot: it takes the body's position at the alignment time with `Orbit.GetPositionOrb(GetTimeSincePeriapsisThisOrbit(t)).Transform(GetOrb2ParentCce())`, lifts it with `Parent.GetPositionEclFromCce`, projects it with `camera.EclToScreen`, adds `inViewport.Position` and draws on `ImGuiHelper.GetOverlayDrawList(inViewport)`.[^ksa-transferplanner] [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L470-L485)
- **Frames.** `Astronomical.GetPositionEclFromCce(positionCce)` is the body's ecliptic position plus the CCE vector; `Camera.EclToScreen(posEcl, ignoreBehind = true)` returns `(NaN, NaN)` for a point behind the camera.[^ksa-astronomical] [^ksa-camera] [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L369) [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L340-L345)
- **Overlay list.** `ImGuiHelper.GetOverlayDrawList(viewport)` is `viewport.IsMain() ? GetMainOverlayDrawList() : ImGui.GetWindowDrawList()`, and the main one is `ImGui.GetBackgroundDrawList(ImGui.GetMainViewport())`, so main-viewport markers are drawn under every ImGui window and escape any window's clip rectangle.[^ksa-imguihelper] [current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L43-L51)
- **Camera mode.** `IViewport.Mode` is a `CameraMode`; `CameraMode.Map` is the map view. The map view is a mode of the viewport's camera, not a separate camera, so a world overlay has to check the mode to decide when to draw.[^ksa-cameramode] [current/decomp/KSA/CameraMode.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CameraMode.cs#L11-L12) [current/decomp/KSA/IViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IViewport.cs#L29)

## Lifecycle and timing

The overlay is called from DeltaVMap's `[StarMapAfterGui]` method (a postfix on `Program.OnDrawUiViewports`, `frame:draw-ui`) as a late step of drawing its own window, so the commands land on the background draw list the same frame and render in the ImGui pass (`render:imgui`). Because `OnDrawUiViewports` is called only inside `if (DrawUI)`, nothing is submitted while the HUD is hidden.[^ksa-program] [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2262-L2268) Camera state is final by then: `OnFrameViewports` runs before the draw calls. Body positions come from analytic orbits at `Universe.GetElapsedTime()`, a pure read, safe while the next vehicle step runs on workers. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [draw UI while the HUD is hidden](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md).

## Steps

1. **Gate on the map view and fetch this viewport's camera.** Use the camera of the viewport you were handed (the frame viewport at the hook can differ from `Program`'s), its `Position` for the screen offset and the overlay list; fault-isolate the whole thing so a camera or projection change cannot unwind into rendering:

```csharp
// DeltaVMap/Render/TransferWindowMapOverlay.cs L59-L97
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

2. **Compute positions in the parent's CCE frame, then lift and project.** An orbit takes time since its own periapsis, so convert absolute game time first; then lift with `GetPositionEclFromCce` and project exactly as the stock marker does:

```csharp
// DeltaVMap/Render/TransferWindowMapOverlay.cs L256-L269
    // The orbiting body's position in its parent's CCE frame at an absolute game time. The orbit
    // takes time since its own periapsis, so the absolute time is converted first.
    private static double3 PositionCce(Orbit orbit, UniverseTime gameTime)
    {
        return orbit.GetPositionCce(orbit.GetTimeSincePeriapsisThisOrbit(gameTime));
    }

    // Lift a parent-frame CCE point to ECL and project it to viewport screen coordinates, exactly
    // as the stock alignment marker does.
    private static float2 CceToScreen(Orbit orbit, double3 cce, Camera camera, float2 vpPos)
    {
        double3 ecl = orbit.Parent.GetPositionEclFromCce(cce);
        return vpPos + camera.EclToScreen(ecl);
    }
```

3. **Validate every projected point before drawing.** A point behind the camera or at infinity gives a NaN or infinite pixel; drop the marker rather than draw it:

```csharp
// DeltaVMap/Render/TransferWindowMapOverlay.cs L340-L343
    private static bool Valid(float2 s)
    {
        return !float.IsNaN(s.X) && !float.IsNaN(s.Y) && !float.IsInfinity(s.X) && !float.IsInfinity(s.Y);
    }
```

4. **Mark a body's future position on its real orbit.** The destination's CCE position at `now + TimeToWindowSeconds` gives a dot; an emphasized body also gets a ring and a shadowed label whose string is rebuilt only when its integer key changes, so there is no per-frame allocation:

```csharp
// DeltaVMap/Render/TransferWindowMapOverlay.cs L101-L133
    private void DrawDestinationMarker(
        ImDrawListPtr dl, Camera camera, float2 vpPos, TransferWindowInfo w, UniverseTime now, bool emphasized)
    {
        if (!double.IsFinite(w.TimeToWindowSeconds) || w.Target is not IOrbiter orbiter)
            return;

        Orbit orbit = orbiter.Orbit;
        float2 s = CceToScreen(orbit, PositionCce(orbit, now + w.TimeToWindowSeconds), camera, vpPos);
        if (!Valid(s))
            return;

        if (emphasized)
        {
            dl.AddCircleFilled(in s, 5f, MarkerColor);
            dl.AddCircle(in s, 9f, MarkerColor, 20, 2f);
            long key = Format.WindowTimeKey(w.TimeToWindowSeconds);
            if (key != _markerKey || !ReferenceEquals(w.TargetId, _markerTarget))
            {
                _markerLabel = w.TargetId + " ~" + Format.WindowTime(w.TimeToWindowSeconds);
                _markerKey = key;
                _markerTarget = w.TargetId;
            }
            string label = _markerLabel;
            var lp = new float2(s.X + 12f, s.Y - 8f);
            var sh = new float2(lp.X + 1f, lp.Y + 1f);
            dl.AddText(in sh, LabelShadow, label);
            dl.AddText(in lp, LabelColor, label);
        }
        else
        {
            dl.AddCircle(in s, 5f, MarkerFaint, 16, 1.6f);
        }
    }
```

5. **Draw fixed-length direction arrows by projecting one probe point.** A straight 3D ray projects to a straight screen line, so projecting a single probe point at a small fraction of the orbit radius along each direction gives the exact on-screen direction. Draw the arms at a constant 72 px so the gizmo keeps its size at every zoom; the viewport position is already included in each projected point:

```csharp
// DeltaVMap/Render/TransferWindowMapOverlay.cs L147-L178
        Orbit orbit = src.Orbit;
        double3 pCce = PositionCce(orbit, now);
        double3 vCce = src.GetVelocityCce();
        double rLen = pCce.Length();
        double vLen = vCce.Length();
        if (!(rLen > 0.0) || !(vLen > 0.0))
            return;

        double3 vHat = vCce * (1.0 / vLen);
        double3 rHat = pCce * (1.0 / rLen);
        // NormalizeOrZero (not Normalized) so a degenerate radial state cannot inject NaN; real
        // orbits never hit it (open orbits are filtered upstream), matching the stock guard.
        double3 nHat = double3.Cross(rHat, vHat).NormalizeOrZero();
        double angle = EjectionSign * (w.EjectionAhead ? 1.0 : -1.0) * w.EjectionAngle;
        double3 ejDir = Rotate(vHat, nHat, angle);

        double probe = rLen * DirectionProbeFraction;
        float2 s0 = CceToScreen(orbit, pCce, camera, vpPos);
        float2 sPro = CceToScreen(orbit, pCce + vHat * probe, camera, vpPos);
        float2 sEj = CceToScreen(orbit, pCce + ejDir * probe, camera, vpPos);
        if (!Valid(s0) || !Valid(sPro) || !Valid(sEj))
            return;

        float2 uPro = Unit(sPro - s0);
        float2 uEj = Unit(sEj - s0);
        if (IsZero(uPro) || IsZero(uEj))
            return;

        var proEnd = new float2(s0.X + uPro.X * RayPx, s0.Y + uPro.Y * RayPx);
        var ejEnd = new float2(s0.X + uEj.X * RayPx, s0.Y + uEj.Y * RayPx);
        dl.AddLine(in s0, in proEnd, ProgradeColor, 1.8f);
        dl.AddLine(in s0, in ejEnd, MarkerColor, 2.2f);
```

   The angle drawn between the arms is the projected angle in a tilted view, so the numeric label carries the exact value.

6. **Call it from your window's draw.** DeltaVMap draws the layer at the end of its map-canvas draw, on the overlay list so it escapes the window's clip rect:

```csharp
// DeltaVMap/Render/MapWindow.cs L1170-L1173

        // Draws on the viewport overlay list, so it escapes this window's clip rect.
        if (_showMapMarkers)
            _mapOverlay.Draw(viewport, _windows, _windowsHoverBodyId ?? _routeSiblingId);
```

   The viewport comes from the stock window's draw, which is `Program.MainViewport` because the mod's `[StarMapAfterGui]` method calls `MapWindow.DrawActive(Program.MainViewport)`.[DeltaVMap/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Mod.cs#L50-L53) The layer therefore draws only while the mod's own window is open.

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Render/TransferWindowMapOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/TransferWindowMapOverlay.cs) | Mode gate, projection helpers, markers, phase angle, ejection gizmo |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L1170-L1173) | Call site at the end of `DrawCanvas` |
| [DeltaVMap/Render/TransferWindowRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/TransferWindowRenderer.cs#L103-L106) | The opt-in checkbox |
| [DeltaVMap/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Mod.cs#L50-L53) | `[StarMapAfterGui]` entry |

## Variations and gotchas

- **Hidden HUD hides the layer.** `StarMapAfterGui` does not fire when the HUD is hidden, and neither does stock's own map marker drawing. A mod that must keep map markers up with the HUD hidden has to submit from a hook outside the `DrawUI` guard, for example a patch on `Program.OnDrawUiConsole`.
- **Only while your window draws.** Because the call sits inside the mod's window draw, closing the window removes the layer; hook `StarMapAfterGui` directly if the markers must stay.
- **Mode gate.** There is no separate map camera; without the `CameraMode.Map` test the markers would also draw over the flight view.
- **Pass the viewport you were handed.** `GetOverlayDrawList` picks the background list only for the main viewport; for any other viewport it uses the current ImGui window's list, so markers for a secondary viewport must be drawn from inside that viewport's window.
- **Not clamped to the screen.** A marker behind the camera or past the edge is simply dropped (NaN check); to point at an off-screen target see [point at an off-screen target](../../../rendering/overlays/draw-markers-and-annotations-on-world-objects/overview.md).
- **Allocation.** Cache label strings against integer keys, as the mod does, because these run every frame.

## See also

- [Overview](overview.md); [ImGui polyline of a flown prediction](draw-a-predicted-trajectory-and-map-markers-3.md); [Click-through overlay projecting CCI points](draw-a-predicted-trajectory-and-map-markers-5.md)
- [Compute transfer windows](../../transfers/compute-transfer-windows/overview.md)
- [Project world positions to the screen](../../../rendering/overlays/project-world-positions-to-screen/overview.md)
- [Draw markers and annotations on world objects](../../../rendering/overlays/draw-markers-and-annotations-on-world-objects/overview.md)
- [Map-mode transfer overlay (mod feature)](../../../../mods/ksa-deltavmap/features/map-mode-transfer-overlay.md)

[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-astronomical]: KSA.Astronomical (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-imguihelper]: KSA.ImGuiHelper (decompiled, build 2026.10.10.5554)
[^ksa-cameramode]: KSA.CameraMode (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
