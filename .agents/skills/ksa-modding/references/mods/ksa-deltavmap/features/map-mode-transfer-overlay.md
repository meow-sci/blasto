---
type: Mod Feature
title: Transfer-window markers drawn into the game's 3D map mode
description: While the camera is in map mode it projects each sibling's future position at its next window, the live planet-star-planet phase angle and an ejection-angle gizmo from orbit (CCE) coordinates to screen with the viewport camera's EclToScreen and draws them on the ImGui background draw list
mod: ksa-deltavmap
category: flight-and-orbits
techniques: [imgui-drawlist, world-to-screen-projection]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, render:imgui]
game_types:
  - KSA.Camera
  - KSA.IViewport
  - KSA.CameraMode
  - KSA.ImGuiHelper
  - KSA.Orbit
  - KSA.IOrbiter
  - KSA.IPosition
  - KSA.Universe
  - KSA.TransferPlanner
tags: [flight-and-orbits, map-view, overlay, projection, imgui, draw-list, transfer-window]
---

An opt-in layer ("SHOW ON GAME MAP MODE (3D)" in the transfer-window panel) draws the same
transfer-window data into the game's own 3D map view: an amber marker on the destination's real
orbit where it will be when the window opens, the live planet-star-planet phase angle at the hub,
and an ejection-angle gizmo (prograde arm plus ejection arm) at the departure body. Mechanism:
positions are computed in the parent body's CCE frame from `Orbit`, lifted to ecliptic with
`GetPositionEclFromCce`, projected with the viewport camera's `Camera.EclToScreen`, offset by the
viewport position and drawn with ImGui draw-list primitives on
`ImGuiHelper.GetOverlayDrawList(viewport)`, exactly the path the stock Transfer Planner uses for
its "Show parent/target alignment" marker.[^dvmap-mapoverlay][^ksa-transferplanner]

## How it works

**Where it is called.** `MapWindow.DrawCanvas` ends with
`if (_showMapMarkers) _mapOverlay.Draw(viewport, _windows, _windowsHoverBodyId ?? _routeSiblingId);`.
The `viewport` is the `IViewport` the stock `ImGuiWindow.OnDrawUi` passes into `DrawContent`,
which is `Program.MainViewport` because `Mod.Draw` calls
`MapWindow.DrawActive(Program.MainViewport)`.[^dvmap-mapwindow][^dvmap-mod] So the 3D layer is
drawn only while the Delta-V Map window itself is open and drawing.

**Gate and inputs.** `Draw` returns immediately unless `viewport.Mode == CameraMode.Map`. It
takes the camera from `viewport.GetCamera()` (the comment notes the frame viewport at this hook
may differ from `Program`'s), the screen offset from `viewport.Position`, the draw list from
`ImGuiHelper.GetOverlayDrawList(viewport)` and the time from `Universe.GetElapsedTime()`.[^ksa-iviewport]

**Which draw list.** In the decompiled game `GetOverlayDrawList(viewport)` is
`viewport.IsMain() ? GetMainOverlayDrawList() : ImGui.GetWindowDrawList()`, and
`GetMainOverlayDrawList()` is `ImGui.GetBackgroundDrawList(ImGui.GetMainViewport())`
([current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L43-L51)).[^ksa-imguihelper] The mod
always passes `Program.MainViewport`, so the markers go on the background draw list of ImGui's
main viewport, the same list stock `Program.DrawFps` writes its frame counter to. They escape the
map window's (and its `##dvmap_canvas_col` child's) clip rect, render over the composited 3D scene
and under every ImGui window, including the Delta-V Map window itself. The non-main branch exists
because stock draws a secondary viewport's world UI (`Universe.OnDrawUi(this)`) from inside that
viewport's own ImGui window in `GameViewport.DrawImGui`, so the current window there is the one
that shows the viewport's rendered image ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L247-L251)).[^ksa-gameviewport]

**Projection.** Two helpers do all the math:

- `PositionCce(orbit, t) = orbit.GetPositionCce(orbit.GetTimeSincePeriapsisThisOrbit(t))`: the
  orbit takes time since its own periapsis, so absolute game time is converted first.[^ksa-orbit]
- `CceToScreen(orbit, cce) = vpPos + camera.EclToScreen(orbit.Parent.GetPositionEclFromCce(cce))`.
  `GetPositionEclFromCce` is `GetPositionEcl() + positionCce`.[^ksa-astronomical]
  `EclToScreen(pos, ignoreBehind = true)` converts ECL to camera-relative ("ego") coordinates and
  returns `(NaN, NaN)` when the point is behind the camera.[^ksa-camera] Every projected point
  goes through `Valid()` (rejects NaN and infinity) before drawing.

The stock alignment marker in `TransferPlanner.DrawPlanWindow` does the same:
`GetPositionOrb(GetTimeSincePeriapsisThisOrbit(t)).Transform(GetOrb2ParentCce())`, then
`Parent.GetPositionEclFromCce`, then `camera.EclToScreen`, then `inViewport.Position + ...`, drawn
on `overlayDrawList` from `ImGuiHelper.GetOverlayDrawList(inViewport)`.

**What it draws** (emphasis = hovered body, else the selected route's sibling, else soonest):

| Element | Construction |
|---|---|
| Destination markers | Each sibling's CCE position at `now + TimeToWindowSeconds` on its real orbit. Emphasized: filled 5 px dot, 9 px ring, shadowed label "<body> ~<time>". Others: faint 5 px ring |
| Phase angle | Hub = CCE origin `double3.Zero` projected; lines from hub to source and target at their current positions; a 40 px screen-space arc between the two projected directions; label on the angular bisector |
| Ejection gizmo | `vHat` from `src.GetVelocityCce()`, `nHat = cross(rHat, vHat).NormalizeOrZero()`, ejection direction = Rodrigues rotation of `vHat` about `nHat` by `EjectionSign * (ahead ? 1 : -1) * EjectionAngle`. Probe points at 5% of the orbit radius along each direction are projected to read the on-screen directions; arms are then drawn at a fixed 72 px |

A straight 3D ray projects to a straight screen line, so projecting one probe point is enough to
get the exact screen direction while keeping the gizmo a constant screen size at any zoom. The
drawn angles are the projected angles in a tilted view; the numeric labels carry the exact values.
Label strings are cached against integer keys (rounded degrees, countdown key), so the layer does
not allocate per frame.

## Lifecycle and timing

- Hook: `[StarMapAfterGui]` (Harmony postfix on `Program.OnDrawUiViewports`), main thread,
  `frame:draw-ui`, nested in `ImGuiWindow.OnDrawUi` -> `DrawContent` -> `DrawCanvas`. It draws
  only while the map window is shown and its `ImGui.Begin` succeeds, a scene is built, the canvas
  is at least 8 px each way, the panel toggle `_showMapMarkers` is on and the main viewport is in
  `CameraMode.Map`.
- Hidden HUD: the background list itself survives (`ImGui.Render` and the `render:imgui` pass run
  regardless of `Program.DrawUI`, and `DrawFps` keeps writing to the same list), but nothing is
  submitted to it. `Program.OnFrame` calls `OnDrawUiViewports` only inside `if (DrawUI)`, so
  StarMap's postfix and with it `Mod.Draw` never run while the HUD is hidden
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2265-L2273)).[^ksa-program] Stock's own map
  markers (`Universe.OnDrawUi` in `OnDrawUiThreadSafe`) disappear the same way. A mod that must keep
  map markers on screen with the HUD hidden has to submit to this list from a hook outside the
  `DrawUI` guard, such as a patch on `Program.OnDrawUiConsole`.
- Camera state is final for the frame: `OnFrameViewports` (viewport and camera `OnFrame`) runs
  before the `OnDrawUi*` calls in `Program.OnFrame`, so `GetCamera()` here projects with the
  same view the frame will render.
- The commands are recorded into ImGui's draw data, which `Program.RenderGame` renders with
  `ImGuiBackend.Vulkan.RenderDrawData` in the final pass after the final composite
  (`render:imgui`), so the markers sit on top of the 3D map.
- Celestial positions come from analytic orbits at `Universe.GetElapsedTime()`, a pure read that
  is safe while the next vehicle-solver batch runs on worker threads.

## Key code

| Path | Role |
|---|---|
| [DeltaVMap/Render/TransferWindowMapOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/TransferWindowMapOverlay.cs) | Mode gate, projection helpers, markers, phase angle, ejection gizmo |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L1171-L1173) | Call site at the end of `DrawCanvas` |
| [DeltaVMap/Render/TransferWindowRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/TransferWindowRenderer.cs#L101-L106) | The opt-in checkbox |

## Snippets

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

```csharp
// DeltaVMap/Render/TransferWindowMapOverlay.cs L256-L278
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

// Rotate a vector about a unit axis by an angle (Rodrigues' rotation), used to swing the
// prograde direction into the ejection direction within the orbital plane.
private static double3 Rotate(double3 v, double3 axis, double angle)
{
    double c = Math.Cos(angle);
    double s = Math.Sin(angle);
    return v * c + double3.Cross(axis, v) * s + axis * (double3.Dot(axis, v) * (1.0 - c));
}
```

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
// ...
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

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `IViewport.Mode`, `IViewport.GetCamera()`, `IViewport.Position` | [current/decomp/KSA/IViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IViewport.cs) | Map-mode gate, camera, screen offset |
| `ImGuiHelper.GetOverlayDrawList(IViewport)` | [current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs) | Background draw list of the main viewport |
| `Camera.EclToScreen(double3, bool ignoreBehind = true)` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs) | World (ECL) to viewport pixels; NaN behind the camera |
| `Orbit.GetPositionCce(UniverseTime)`, `GetTimeSincePeriapsisThisOrbit`, `Parent` | [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs) | Body position on its orbit at any time |
| `IPosition.GetPositionEclFromCce` (`Astronomical`: `GetPositionEcl() + cce`) | [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs) | Parent frame to ecliptic |
| `IOrbiter.GetVelocityCce()` | (KSA.IOrbiter) | Prograde direction for the gizmo |
| `TransferPlanner.DrawPlanWindow` alignment marker | [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs) | Not called; the stock projection pattern this mirrors |

## Pitfalls and version notes

- Use the viewport's own camera and `viewport.Position`; adding the viewport offset is what makes
  the result correct for a non-fullscreen or secondary viewport.
- Always check projected points for NaN. `EclToScreen` deliberately returns NaN for points behind
  the camera, and a draw-list call with NaN coordinates produces garbage geometry.
- The layer lives inside the map window's draw. Closing the Delta-V Map window removes it; a mod
  that wants a 3D overlay independent of its window would have to draw from the hook directly
  instead of from window content, and one that must survive a hidden HUD needs a hook outside the
  `DrawUI` guard (see Lifecycle and timing).
- On a non-main viewport `GetOverlayDrawList` returns the current window's draw list, which from
  `DrawCanvas` would be the `##dvmap_canvas_col` child of the map window (clipped to it). The mod
  only ever passes the main viewport, and only draws into the main viewport's map view.
- `EjectionSign = -1.0` was set by in-game observation (an Earth to Mars ejection must point out
  past Earth's orbit); the comment invites flipping it if a later build disagrees.

## See also

- [Transfer windows and phase clock](./transfer-windows-and-phase-clock.md)
- [Map window and menu entry](./map-window-and-menu-entry.md)
- [Stock Transfer Planner bridge](./stock-transfer-planner-bridge.md)
- [Subway-map canvas rendering](./subway-map-canvas-rendering.md)
- [Project orbit positions to the map view with GetPositionEclFromCce and EclToScreen and draw markers and fixed-length arrows](../../../howto/flight-and-orbits/map-view/draw-a-predicted-trajectory-and-map-markers/draw-a-predicted-trajectory-and-map-markers-4.md) - how-to article demonstrating this feature
- [Map-mode markers and gizmos for future positions](../../../howto/rendering/overlays/draw-markers-and-annotations-on-world-objects/draw-markers-and-annotations-on-world-objects-3.md) - how-to article demonstrating this feature
- [Body-fixed or orbit-relative anchors lifted to ecliptic, then Camera.EclToScreen](../../../howto/rendering/overlays/project-world-positions-to-screen/project-world-positions-to-screen-1.md) - how-to article demonstrating this feature
- [Overlays without a window: ImGuiHelper.GetOverlayDrawList](../../../howto/ui/overlays/draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-2.md) - how-to article demonstrating this feature

[^dvmap-mapoverlay]: DeltaVMap/Render/TransferWindowMapOverlay.cs
[^dvmap-mapwindow]: DeltaVMap/Render/MapWindow.cs
[^dvmap-mod]: DeltaVMap/Mod.cs
[^ksa-imguihelper]: KSA.ImGuiHelper (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-iviewport]: KSA.IViewport (decompiled, build 2026.10.10.5554)
[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^ksa-astronomical]: KSA.Astronomical (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
