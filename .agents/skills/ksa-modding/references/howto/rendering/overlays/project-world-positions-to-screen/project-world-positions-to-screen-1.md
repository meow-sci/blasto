---
type: How-To
title: Body-fixed or orbit-relative anchors lifted to ecliptic, then Camera.EclToScreen
description: Project real world points through the viewport's own camera, adding the viewport origin, with orbit-relative and body-fixed anchors lifted to the ecliptic first
category: rendering
group: overlays
topic: project-world-positions-to-screen
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/ksarmory/features/world-anchored-screen-markers
  - mods/ksa-deltavmap/features/map-mode-transfer-overlay
  - mods/ksagolf/features/cursor-picking-and-terrain-ray
  - mods/ksa-advancedflightcomputer/features/guidance-panel-and-overlays
techniques: [imgui-drawlist, reference-frames]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Camera, KSA.IViewport, KSA.Program]
tags: [projection, ecl, cce, ccf, imgui-drawlist, viewport]
---

Outcome: a position in the game's frames becomes an ImGui screen coordinate that lands on the right pixel of the right viewport, drawn with a draw list rather than Vulkan. This is the base technique;
the other articles of the topic add the cases it leaves open (sky directions, behind-the-camera points, edge cues, occlusion). Prerequisites: a StarMap mod with a `[StarMapAfterGui]` method. Demonstrated by
KSArmory ([World-anchored screen markers on full-screen ImGui overlay windows](../../../../mods/ksarmory/features/world-anchored-screen-markers.md)), KSA-DeltaVMap ([Transfer-window markers drawn into the game's 3D map mode](../../../../mods/ksa-deltavmap/features/map-mode-transfer-overlay.md)), KSA-AdvancedFlightComputer ([A detachable console-window panel and world-space trajectory overlays](../../../../mods/ksa-advancedflightcomputer/features/guidance-panel-and-overlays.md))
and KSAGolf's cursor aiming ([Cursor picking and the terrain ray](../../../../mods/ksagolf/features/cursor-picking-and-terrain-ray.md)).

## Background: how the game does it

`Camera` converts between frames and to pixels [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L223-L236): `EclToEgo(posEcl)` subtracts `PositionEcl`, `EgoToEcl` adds it, `GetPositionEgo(IPosition)` gives the camera-relative position of an object (and, for a craft in the
followed craft's physics bubble, composes bubble-relative positions so nearby craft are precise). Projection is
`EclToScreen(double3 posEcl, bool ignoreBehind = true)` and `EgoToScreen(double3 posEgo, bool ignoreBehind = true)`, which return `(NaN, NaN)` for points behind the camera when `ignoreBehind` is true:

```csharp
// current/decomp/KSA/Camera.cs L339-L355
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public float2 EclToScreen(double3 posEcl, bool ignoreBehind = true)
{
	double3 posEgo = EclToEgo(posEcl);
	return EgoToScreen(posEgo, ignoreBehind);
}

[MethodImpl(MethodImplOptions.AggressiveInlining)]
public float2 EgoToScreen(double3 posEgo, bool ignoreBehind = true)
{
	if (ignoreBehind && double3.Dot(posEgo, GetForwardEcl()) < 0.0)
	{
		return new float2(float.NaN, float.NaN);
	}
	float3 positionNdc = EgoToNdc(posEgo);
	return NdcToScreen(positionNdc);
}
```

The result is in **framebuffer pixels relative to that camera's viewport**; ImGui wants absolute screen pixels, so add `IViewport.Position`. The game does this itself in the stock overlays (the Transfer Planner's alignment marker, the galactic sky grid).
`ImGuiHelper.GetOverlayDrawList(viewport)` returns the main viewport's background draw list for the main viewport and the current window's list otherwise [current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L43-L51).

## Lifecycle and timing

| Fact | Detail |
|---|---|
| Hook | `[StarMapAfterGui]`, a postfix on `Program.OnDrawUiViewports` (`frame:draw-ui`) |
| Camera matrices | Final for the frame: `OnFrameViewports` (`frame:viewports`) runs each viewport camera's `OnFrame` before the `OnDrawUi*` calls |
| Positions | Settled: `PrepareFrame` applied the last physics step and dispatched the next; body and vehicle positions are the main-thread copies until the next frame's apply |
| HUD hidden | `OnDrawUiViewports` is skipped when `Program.DrawUI` is false, so projected UI vanishes with F2 and in screenshots |

See the draw-ui rows of [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Pick the camera of the viewport you draw on: `viewport.GetCamera()` (the viewport at this hook may differ from `Program`'s), or `Program.GetMainCamera()` for the main view. Find the main viewport by `IsMain()` over
   `ViewportRegistry.GameViews`, not by index zero; `ViewportRegistry.MainViewport` throws before the registry has one.

2. Express the point in ecliptic. Real bodies and craft: `IPosition.GetPositionEcl()` or the object's own helper. Orbit-relative points: compute in the parent body's CCE frame and lift with `parent.GetPositionEclFromCce(cce)`
   [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L369); KSA-DeltaVMap does this for a destination's future position, using the same recipe as the stock alignment marker:

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

3. Body-fixed ground points should be projected from the **body-fixed** frame so they do not drift with the planet's rotation. KSA-AdvancedFlightComputer caches the camera, body position and rotations per frame and projects
   CCI or body-fixed points; it also rejects points behind the camera with the clip-space `W`:

   ```csharp
   // AdvancedFlightComputer/Features/Guidance/Ui/Overlays/OverlayCore.cs L61-L69
   // Project a CCI point to screen pixels; false if it is behind the camera. The chain is CCI -> body-fixed -> ECL (+ body ECL position) -> screen.
   private static bool TryProjectCci(double3 cci, out float2 screen)
   {
       double3 ecl = _ovBodyEcl + cci.Transform(_ovCci2Ccf).Transform(_ovCcf2Ecl);
       float4 clip = _ovCam.EgoToClip(_ovCam.EclToEgo(ecl));
       if (clip.W <= 0.001f) { screen = default; return false; } // behind the camera
       screen = _ovCam.EclToScreen(ecl, false);
       return true;
   }
   ```

4. Add the viewport origin and test for NaN or infinity before drawing; a draw-list call with NaN coordinates produces garbage geometry.

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

5. Project the **drawn** position of a craft, not the simulated one. `Camera.GetPositionEgo(vehicle)` is built from the bubble kinematics for craft in the followed craft's bubble; KSArmory projects the drawn position, because using the raw simulated
   position makes a bracket jump whenever warp changes. Ego positions sampled against one camera are wrong for another viewport's camera; re-base through ecliptic (`camera.EclToEgo(main.EgoToEcl(posEgo))`).

6. For a cursor-to-world ray (the inverse), subtract the viewport `Position`, reject the far edges, scale to the camera's `FramebufferSize` and call `Camera.ScreenToEgoRay`; see [Turn the mouse cursor into a world ray and pick points on a construction plane](../../../ui/input/cursor-to-world-ray/overview.md) and [Cursor picking and the terrain ray](../../../../mods/ksagolf/features/cursor-picking-and-terrain-ray.md).

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Render/TransferWindowMapOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/TransferWindowMapOverlay.cs) | Orbit-relative points lifted to ecliptic, projected, drawn on the overlay list |
| [AdvancedFlightComputer/Features/Guidance/Ui/Overlays/OverlayCore.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Ui/Overlays/OverlayCore.cs) | Per-frame projection setup for CCI and body-fixed points |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4559-L4595) | Main-viewport projection with edge fallback |

## Variations and gotchas

- **Behind the camera.** `EclToScreen(..., ignoreBehind: false)` mirrors a point behind the camera onto the screen; keep the default (NaN) and break polylines at NaN, or use the bearing/edge techniques of the other articles.
- **Always add `viewport.Position`.** Without it the overlay is right only on a single full-screen viewport.
- **Ego is per camera.** Re-base through ecliptic for other viewports.
- **Simulated versus drawn position** can differ by metres; overlay the drawn one.
- **Stock values refreshed late.** `MainViewport.Position` is set at the start of `OnDrawUiThreadSafe`, after `AfterGui`, so `viewport.Position` is last frame's (it differs only on the frame the OS window moves).
- Map mode uses the same camera as flight, so the same projection works at every zoom, but the overlay should only draw while the viewport is in `CameraMode.Map` when it is a map marker.

## See also

- [Overview](overview.md)
- [Approach 2: sky directions on the star sphere](project-world-positions-to-screen-2.md)
- [Approach 3: NaN culling with polyline breaking](project-world-positions-to-screen-3.md)
- [Approach 4: clip-space edge clamp](project-world-positions-to-screen-4.md)
- [Approach 5: edge cue from the camera basis](project-world-positions-to-screen-5.md)
- [Approach 6: CPU occlusion by bodies](project-world-positions-to-screen-6.md)
- [Draw full-screen click-through overlays and choose the right ImGui draw list](../../../ui/overlays/draw-a-fullscreen-click-through-overlay/overview.md), [Draw lines and labels on the sky](../draw-lines-on-the-sky/overview.md), [Choose a render frame and anchor effects to bodies, parts and terrain](../choose-a-render-frame-and-anchor-effects/overview.md)
