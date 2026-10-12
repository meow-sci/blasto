---
type: How-To
title: "Cursor to world ray: the ego frame, EgoToEcl and picking points on a construction plane"
description: Lift an ego-space cursor ray into ecliptic space, intersect it with a camera-facing or ecliptic plane and store the hit relative to a body
category: ui
group: input
topic: cursor-to-world-ray
approach: 5
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-measuretools/features/cursor-unprojection-and-construction-plane]
techniques: [screen-to-world-ray, harmony-prefix, imgui-drawlist]
hooks: [harmony-prefix KSA.Program.OnMouseButton, starmap StarMapAfterGui]
lifecycle: [frame:input, frame:draw-ui, frame:viewports]
game_types:
  - KSA.Camera
  - KSA.Cursor
  - KSA.Ray
  - KSA.MathEx
  - KSA.Astronomical
  - KSA.VehicleEditingSpace
  - KSA.IViewport
  - KSA.GameViewport
  - KSA.Transform3D
  - KSA.Vehicle
tags: [cursor, raycast, ego, ecliptic, construction-plane, orthographic, anchors, measure]
---
When a click snaps to nothing, KSA-MeasureTools still places a point exactly under the cursor: on a plane facing
the camera through the previous point (or the reference body), or with Ctrl held on the ecliptic plane. A faint disc
and a spoke preview the plane before the click. The same unprojection feeds every other picker in the mod, so this is
the base technique for any click-to-world feature. This article explains the camera's ego frame, how a ray in it
becomes an ecliptic ray, and how to intersect that ray with a construction plane and keep the result attached to a body.
Prerequisites: StarMap and Harmony. Demonstrated by KSA-MeasureTools.

## Background: how the game does it

### The ego frame

"Ego" is the camera-relative frame KSA renders in. Per viewport camera there is one ego frame, and it has a simple
relation to the ecliptic (Ecl) frame:

- `Camera.EgoToEcl(posEgo)` is `posEgo + PositionEcl` and `EclToEgo` subtracts it: a **pure translation**, so ego axes
  are ecliptic axes [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L222-L233)[^ksa-camera]. A direction needs no conversion; only an origin must be lifted.
- `PositionEcl` is the camera's ecliptic position. When the camera follows something it is
  `Following.GetPositionEcl() + PositionCce`, so it moves with the followed body [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L110-L131)[^ksa-camera].
- Each viewport has its own camera, hence its own ego origin. An ego position sampled from one camera is not valid for
  another. `Camera.GetPositionEgo(astronomical)` gives an object's ego position as KSA draws it
  [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L236-L250)[^ksa-camera].
- `Camera.UpView` is `double3.UnitY`: the camera-up convention, not the ecliptic normal [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L73-L77)[^ksa-camera].

### Unprojection

`Camera.ScreenToEgoRay(float2)` returns a `Ray` in ego space. With a perspective projection the origin is
`double3.Zero` and the direction goes through the near-plane point; with an orthographic projection (the vehicle
editor's toggle, `VehicleEditor.OrthographicCamera`) the origin is the cursor's own near-plane point and every cursor
position shares one direction [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L704-L740)[^ksa-camera].

`Cursor.GetPosition(viewport)` is the desktop position minus `viewport.Position` [current/decomp/KSA/Cursor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Cursor.cs#L17-L25)[^ksa-cursor].
`IViewport.GetCamera()` returns the map camera in `CameraMode.Map` and the base camera otherwise
[current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L297-L300)[^ksa-gameviewport], so one code path serves the map, flight and editor views.

`MathEx.RayPlaneIntersection(origin, direction, planePoint, planeNormal, out t)` returns false for a parallel ray and,
unless `checkReverse` is set, for `t < 0` [current/decomp/KSA/MathEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MathEx.cs#L1347-L1362)[^ksa-mathex].

## Lifecycle and timing

- **Placement** runs in a Harmony prefix on `Program.OnMouseButton` (`frame:input`, during `Glfw.PollEvents`).
  `ScreenToEgoRay` reads the view-projection inverse `Camera.OnFrame` cached last frame, and `Cursor.GetPosition`
  reads the mouse position `PrepareImGui` copied last frame, so ray and cursor both describe the image the player
  clicked on. Bodies were already advanced by the just-applied physics step, and a following camera's `PositionEcl` is
  computed live from the followed body, so the origin moved with it. Because anchors are stored relative to a body,
  the one-step difference only shifts where on the plane the point lands, not what it follows.
- **Preview** runs in `[StarMapAfterGui]` (`frame:draw-ui`), after `OnFrameViewports` computed this frame's camera, so
  preview and drawn overlay use the same camera.
- Nothing here writes game state. See
  [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Intercept the click. A Harmony prefix on `Program.OnMouseButton` gets the viewport the press latched
   (`Program.InputViewport`), declines when the tool is not armed, a popup is open or ImGui wants the mouse, and
   reads the cursor in viewport space:

```csharp
// MeasureTools/Patches/Patch_MouseButton.cs L111-L132
if (!MeasureState.IsArmed)
{
    // Drop a half-tracked right click so its state cannot leak across a
    // disarm/re-arm cycle and cancel a point unexpectedly.
    _rightPressPending = false;
    return true;
}
// InputViewport is what the original tests: a press latches its viewport
// for the whole sequence, so a release still belongs to where the drag
// started. Not ours means another viewport (see MeasureViewport), or a
// click the original serves before any world click: an open popup, ImGui
// holding the mouse, or a user binding on this button. The tool consumes a
// release only after it handled the press, so a bound action always gets
// its release.
IGameViewport inputViewport = Program.InputViewport;
if (!MeasureViewport.IsHost(inputViewport)
    || Popup.AnyOpen
    || ImGui.GetIO().WantCaptureMouse
    || IsBoundInput(window, button, action, mods))
{
    return true;
}
```
```csharp
// MeasureTools/Patches/Patch_MouseButton.cs L180-L187
// Shift (target-set) and alt (focus, editor duplicate) stay with stock.
// Ctrl is ours: a free point on the ecliptic plane instead of the camera
// plane, even where snapping would win.
if ((mods & (GlfwModifier.Shift | GlfwModifier.Alt)) != 0)
    return true;
bool eclipticFree = (mods & GlfwModifier.Control) != 0;

float2 mouseViewport = Cursor.GetPosition(inputViewport);
```

   Then it calls `MapPicker.Pick(inputViewport, mouseViewport, eclipticFree)` and returns `false` to consume the click.
   The consume rules (pairing presses and releases, handing back the game's mouse latches) belong to
   [sharing input with the game](../coordinate-imgui-focus-with-game-input/overview.md).
2. Unproject and lift the origin. The origin is taken from the ray rather than assumed to be the camera, which is what
   keeps the orthographic editor correct:

```csharp
// MeasureTools/Features/Measure/MapPicker.cs L1017-L1038
private static Anchor? PickFreePoint(IViewport viewport, float2 mouseViewport, bool eclipticPlane)
{
    if (!TryGetFreePlane(viewport, eclipticPlane, out double3 planePoint, out double3 normal, out Astronomical? refBody))
        return null;
    Camera camera = viewport.GetCamera();

    // Ego axes are ECL axes (Camera.EgoToEcl is a pure translation), so the ego
    // ray direction is an ECL direction and lifting its origin is a translation.
    // Taking the origin from the ray rather than assuming the camera keeps this
    // correct under the editor's orthographic projection, where every cursor
    // position gets the same direction and its own near-plane origin.
    Ray ray = camera.ScreenToEgoRay(mouseViewport);
    double3 originEcl = camera.EgoToEcl(ray.Origin);
    if (!MathEx.RayPlaneIntersection(originEcl, ray.Direction, planePoint, normal, out double t) || !(t > 0.0))
        return null;
    double3 pointEcl = originEcl + ray.Direction * t;
    if (Program.Editor != null)
        return Anchor.EditorFree(pointEcl - Program.Editor.EditingSpace.PositionEcl);
    if (refBody == null)
        return null;
    return Anchor.Free(refBody, refBody.GetPositionCceFromEcl(pointEcl));
}
```

3. Pick points on a construction plane (below).
4. Show the plane before the click: a 64-segment circle of radius `0.4 * depth * tan(fov / 2)` and a spoke from the
   plane anchor to the cursor, each point projected with `viewport.Position + camera.EclToScreen(p)` and culled when it is
   NaN (behind the camera):

```csharp
// MeasureTools/Features/Measure/MeasureOverlay.cs L656-L699
private static void DrawConstructionPlane(ImDrawListPtr dl, Camera camera, IViewport viewport, float2 vpPos, float2 cursor, bool eclipticPlane)
{
    if (!MapPicker.TryGetFreePlane(viewport, eclipticPlane, out double3 planePoint, out double3 normal, out _))
        return;

    double depth = (planePoint - camera.PositionEcl).Length();
    if (!(depth > 0.0))
        return;
    // Spans roughly 40% of the half view height at the plane's distance.
    double radius = 0.4 * depth * Math.Tan(camera.GetFieldOfView() * 0.5);

    // Basis vectors in the plane. The seed is any ECL axis not near-parallel to the
    // normal; which one only rotates the disc within its own plane, so it is invisible.
    double3 n = double3.Normalize(normal);
    double3 seed = Math.Abs(double3.Dot(n, double3.UnitX)) < 0.9 ? double3.UnitX : double3.UnitZ;
    double3 u = double3.Cross(n, seed).Normalized();
    double3 w = double3.Cross(n, u).Normalized();

    float2 prev = default;
    bool hasPrev = false;
    for (int i = 0; i <= PlaneSegments; i++)
    {
        double a = Math.PI * 2.0 * i / PlaneSegments;
        double3 p = planePoint + u * (radius * Math.Cos(a)) + w * (radius * Math.Sin(a));
        float2 s = vpPos + camera.EclToScreen(p);
        if (Valid(s))
        {
            if (hasPrev)
                dl.AddLine(in prev, in s, PlaneColor, 1f);
            prev = s;
            hasPrev = true;
        }
        else
        {
            hasPrev = false;
        }
    }

    float2 center = vpPos + camera.EclToScreen(planePoint);
    if (Valid(center))
    {
        dl.AddCircleFilled(in center, 2.5f, PlaneColor);
        dl.AddLine(in center, in cursor, PlaneColor, 1f);
    }
```

## Pick points on a construction plane

`MapPicker.TryGetFreePlane` chooses the plane:

| Context | Plane point | Normal |
|---|---|---|
| Editor open | Previous pending point, else `Program.Editor.EditingSpace.PositionEcl` | `camera.GetForwardEcl()`, or `double3.UnitZ` with Ctrl |
| Flight or map | Previous pending point, else the reference body's `GetPositionEcl()` | same |

```csharp
// MeasureTools/Features/Measure/MapPicker.cs L983-L1015
// (so all points of one measurement share a depth basis), else through the
// reference body. The ecliptic plane is the ECL XY plane, normal double3.UnitZ;
// verified against orbit-point CCE offsets in-game (Earth's orbit has tiny Z,
// Hale-Bopp at 89 deg inclination has huge Z). Camera.UpView = (0,1,0) is the
// camera-up convention, NOT the ecliptic normal.
public static bool TryGetFreePlane(IViewport viewport, bool eclipticPlane, out double3 planePointEcl, out double3 normalEcl, out Astronomical? refBody)
{
    planePointEcl = default;
    normalEcl = default;
    refBody = null;
    // In the editor free points anchor to the editing space (the reference
    // combo is disabled there); the system reference bodies stay out of it.
    if (Program.Editor != null)
    {
        planePointEcl = MeasureState.Pending.Count > 0
            ? MeasureState.Pending[^1].ResolveEcl()
            : Program.Editor.EditingSpace.PositionEcl;
        normalEcl = eclipticPlane
            ? double3.UnitZ
            : viewport.GetCamera().GetForwardEcl();
        return true;
    }
    refBody = MeasureState.ResolveReferenceBody(viewport);
    if (refBody == null)
        return false;
    planePointEcl = MeasureState.Pending.Count > 0
        ? MeasureState.Pending[^1].ResolveEcl()
        : refBody.GetPositionEcl();
    normalEcl = eclipticPlane
        ? double3.UnitZ
        : viewport.GetCamera().GetForwardEcl();
    return true;
}
```

- **The ecliptic normal is ECL +Z**, checked in game against orbit-point offsets (Earth's orbit has tiny Z, Hale-Bopp at 89
  degrees inclination has a huge Z). `Camera.UpView` is not the ecliptic normal.
- **A plane through the previous point** gives all points of one measurement a common depth basis, so a two-click free ruler
  measures what the player sees.
- **Reject behind-camera hits.** `RayPlaneIntersection` returning `t <= 0` means the plane is behind the camera; the mod
  rejects it (`!(t > 0.0)`).
- **Store the hit relative to a body.** `Anchor.Free(refBody, refBody.GetPositionCceFromEcl(pointEcl))` keeps an offset in the
  reference body's CCE frame, so a free point next to a planet keeps riding with that planet. In the editor,
  `Anchor.EditorFree(pointEcl - EditingSpace.PositionEcl)` is used since the editing space never moves during a session.
- **Ctrl skips all snapping**, not only the plane choice, and Shift and Alt stay with stock.

## Complete example

| File | Role |
|---|---|
| [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs) | `TryGetFreePlane`, `PickFreePoint` |
| [MeasureTools/Patches/Patch_MouseButton.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_MouseButton.cs) | The click interception |
| [MeasureTools/Features/Measure/MeasureOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureOverlay.cs) | Construction-plane preview |
| [MeasureTools/Features/Measure/Anchor.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Anchor.cs) | `Free` and `EditorFree` anchors |

## Variations and gotchas

- **Do not assume the ray starts at the camera.** In orthographic mode the origin is the near-plane point; using
  `camera.PositionEcl` lands every editor click at the same place along the view axis.
- **`EclToScreen` is viewport-local**: add `viewport.Position` before drawing into a draw list that covers the window.
- **Guard `Vehicle.Orbit`** (it reads `FlightPlan.Patches[0]`) before using it in per-frame UI code.
- **The hover preview is throttled**; the click does a full scan because input callbacks run before the frame's UI draw, where
  stock hover values are still a frame stale.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of cursor-to-world-ray](overview.md)
- [Approach 1: ScreenToEgoRay with viewport offset and framebuffer scale](cursor-to-world-ray-1.md)
- [Approach 4: Cursor.GetEgoRay](cursor-to-world-ray-4.md)
- [Project world and sky positions to screen pixels and point at off-screen targets](../../../rendering/overlays/project-world-positions-to-screen/overview.md)
- [Understand KSA coordinate frames and epochs (ECL, CCE, CCI, CCF, ego, body)](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)
- Mod feature: [Cursor unprojection and construction plane](../../../../mods/ksa-measuretools/features/cursor-unprojection-and-construction-plane.md)

[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-cursor]: KSA.Cursor (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ksa-mathex]: KSA.MathEx (decompiled, build 2026.10.10.5554)
