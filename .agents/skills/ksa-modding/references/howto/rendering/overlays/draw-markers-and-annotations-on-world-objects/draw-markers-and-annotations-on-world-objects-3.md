---
type: How-To
title: Map-mode markers and gizmos for future positions
description: Draw destination markers on a real orbit, a phase angle and an ejection-angle gizmo in the map view by lifting orbit positions to the ecliptic and projecting them on the viewport overlay list
category: rendering
group: overlays
topic: draw-markers-and-annotations-on-world-objects
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/ksa-deltavmap/features/map-mode-transfer-overlay
  - mods/ksa-advancedflightcomputer/features/multi-pass-preview-rendering
techniques: [imgui-drawlist]
hooks:
  - starmap StarMapAfterGui
  - harmony-postfix KSA.TransferPlanner.OnPreRender
  - harmony-postfix KSA.TransferPlanner.DrawPlanWindow
lifecycle: [frame:draw-ui, frame:pre-render]
game_types: [KSA.Camera, KSA.IViewport, KSA.ImGuiHelper, KSA.Orbit, KSA.TransferPlanner]
tags: [map-mode, markers, gizmo, orbit, imgui-drawlist, transfer]
---

Outcome: markers for positions that are not where anything is yet: the destination body at the moment a transfer window opens, a phase angle at the hub star, an ejection-angle gizmo at the departure body, or apsis and node markers of a previewed plan, all drawn in the 3D map view. Positions are computed in the parent body's CCE frame from `Orbit` and
lifted to ecliptic, then projected with the viewport camera and drawn on `ImGuiHelper.GetOverlayDrawList(viewport)`, the path the stock Transfer Planner uses for its alignment marker. Demonstrated by KSA-DeltaVMap ([Transfer-window markers drawn into the game's 3D map mode](../../../../mods/ksa-deltavmap/features/map-mode-transfer-overlay.md)) and the AFC multi-pass preview
([Draw several predicted orbits with a brightness ramp and custom map markers](../../../../mods/ksa-advancedflightcomputer/features/multi-pass-preview-rendering.md)). Prerequisites: StarMap.

## Background: how the game does it

`Orbit.GetPositionCce(timeSincePeriapsis)` and `GetTimeSincePeriapsisThisOrbit(UniverseTime)` give a parent-frame position at an absolute game time; `Parent.GetPositionEclFromCce(cce)` [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L369) lifts it. The stock alignment marker in `TransferPlanner.DrawPlanWindow` does exactly this and draws on
`ImGuiHelper.GetOverlayDrawList(inViewport)` [current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L43-L51); for the main viewport that is the background list of ImGui's main viewport, so markers escape the map window's clip rect, render over the composited 3D scene and under every ImGui window. Orbit lines themselves are drawn by the game's own line renderer
([draw world-space lines](../draw-world-space-lines-and-gizmos/overview.md) approach 3).

## Lifecycle and timing

- Draws inside the map window's content (`ImGuiWindow.OnDrawUi` -> `DrawContent` -> `DrawCanvas`) from `[StarMapAfterGui]` (`frame:draw-ui`), only while the window is open, a scene is built, the panel toggle is on and the main viewport is in `CameraMode.Map`.
- Camera state is final for the frame, so `GetCamera()` projects with the same view the frame will render.
- Celestial positions come from analytic orbits at `Universe.GetElapsedTime()`, a pure read that is safe while the next vehicle-solver batch runs on workers.
- With the HUD hidden `OnDrawUiViewports` is not called; a layer that must survive needs a hook outside the `DrawUI` guard such as a patch on `Program.OnDrawUiConsole`.
- The AFC preview adds orbit lines in a postfix on `TransferPlanner.OnPreRender` (`frame:pre-render`) but its markers from the plan window draw (`frame:draw-ui`), because pre-render runs after `ImGui.Render`.

See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Gate:** return unless `viewport.Mode == CameraMode.Map`. Take the camera from `viewport.GetCamera()` (the frame viewport at this hook may differ from `Program`'s), the offset from `viewport.Position`, the list from `ImGuiHelper.GetOverlayDrawList(viewport)`, the time from `Universe.GetElapsedTime()`.

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

2. **Project orbit-relative points:** convert absolute time to time since periapsis, ask the orbit for its CCE position, lift with the parent's `GetPositionEclFromCce`, then `EclToScreen`; reject invalid results.

   ```csharp
   // DeltaVMap/Render/TransferWindowMapOverlay.cs L256-L270
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

3. **Constant-size gizmo by probing:** a straight 3D ray projects to a straight screen line, so project the origin plus one probe point per direction, normalise the screen vectors and draw fixed-length arms. The ejection direction is the prograde direction rotated about the orbit normal by the ejection angle (Rodrigues):

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

4. **Cache label strings** against integer keys (rounded degrees, a countdown key) so the layer does not allocate per frame.

5. **Preview lines the game's way:** recolour a detached `FlightPlan`'s orbits and call `FlightPlan.AddLineInstances` from a `TransferPlanner.OnPreRender` postfix; draw apsis, AN/DN and SOI markers with `ImGuiHelper.GetOverlayDrawList(viewport)` from the plan window draw, with hover details from the mouse position.

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Render/TransferWindowMapOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/TransferWindowMapOverlay.cs) | Markers, phase angle, ejection gizmo |
| [AdvancedFlightComputer/Features/MultiPass/MultiPassRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassRenderer.cs#L10-L61) | Preview orbit lines |

## Variations and gotchas

- **Use the viewport's own camera and `viewport.Position`;** adding the offset is what makes the result correct for a non-fullscreen or secondary viewport.
- **Check projected points for NaN:** `EclToScreen` deliberately returns NaN behind the camera and a draw-list call with NaN produces garbage.
- **The layer lives inside the map window's draw:** closing the window removes it; draw from the hook directly if it must be independent.
- **On a non-main viewport** `GetOverlayDrawList` returns the current window's list (here a clipped child), which is why the mod only passes the main viewport.
- **Sign conventions** such as the ejection sign were set by in-game observation; verify against a known transfer.

## See also

- [Overview](overview.md)
- [Approach 1: constant-pixel-size brackets](draw-markers-and-annotations-on-world-objects-1.md)
- [Transfer-window markers drawn into the game's 3D map mode](../../../../mods/ksa-deltavmap/features/map-mode-transfer-overlay.md), [Draw several predicted orbits with a brightness ramp and custom map markers](../../../../mods/ksa-advancedflightcomputer/features/multi-pass-preview-rendering.md)
- [Draw world-space lines and gizmos with the game's own renderers](../draw-world-space-lines-and-gizmos/overview.md), [Project world and sky positions to screen pixels and point at off-screen targets](../project-world-positions-to-screen/overview.md)
