---
type: Mod Feature
title: Unproject the cursor through the view camera onto a construction plane
description: Free points come from Camera.ScreenToEgoRay intersected with a camera-facing or ecliptic plane through the previous point or a reference body, relying on ego axes being ECL axes and on the ray origin for the editor's orthographic projection
mod: ksa-measuretools
category: camera
techniques: [screen-to-world-ray, imgui-drawlist]
hooks: [harmony-prefix KSA.Program.OnMouseButton, starmap StarMapAfterGui]
lifecycle: [frame:input, frame:draw-ui]
game_types: [KSA.Camera, KSA.GameViewport, KSA.MathEx, KSA.Astronomical, KSA.VehicleEditingSpace, KSA.Cursor]
tags: [camera, unprojection, ray, map-view, construction-plane, ecliptic, coordinates]
---

When a click snaps to nothing, MeasureTools still places a point exactly under the cursor: on a plane
facing the camera through the previous point (or the reference body), or with Ctrl held on the
ecliptic plane. A faint disc and a spoke show the plane before the click, labelled "camera plane @
Earth" or "ecliptic plane @ editor".[^mt-readme] The same unprojection feeds every other picker in
the mod (part raycasts, the limb snap, surface pins), so it is the base technique for any
click-to-world feature.

## How it works

### Screen to ray

1. The viewport-local cursor is `Cursor.GetPosition(viewport)` (desktop position minus
   `viewport.Position`).[^ksa-cursor]
2. `viewport.GetCamera()` returns the map camera in `CameraMode.Map` and the base camera otherwise
   (`GameViewport.GetCamera`), so one code path serves the map view, the flight orbit view and the
   editor.[^ksa-gameviewport]
3. `Camera.ScreenToEgoRay(float2)` returns a `Ray` in ego space. With a perspective projection the
   origin is the camera (`double3.Zero`) and the direction goes through the near-plane point; with an
   orthographic projection (the vehicle editor's projection toggle, `VehicleEditor.OrthographicCamera`)
   the origin is the cursor's own near-plane point and every cursor position shares one
   direction.[^ksa-camera][^ksa-vehicleeditor]
4. `Camera.EgoToEcl(posEgo)` is `posEgo + PositionEcl`, a pure translation, so ego axes are ECL axes:
   the ray direction can be used as an ECL direction unchanged, and only the origin is lifted. The
   mod always lifts `ray.Origin` instead of assuming the camera position, which is what keeps the
   orthographic case right.[^mt-mappicker]

### Ray to plane

`MapPicker.TryGetFreePlane` chooses the plane:

| Context | Plane point | Normal |
|---|---|---|
| Editor open | Previous pending point, else `Program.Editor.EditingSpace.PositionEcl` | `camera.GetForwardEcl()`, or `double3.UnitZ` with Ctrl |
| Flight or map | Previous pending point, else the reference body's `GetPositionEcl()` | same |

The reference body (`MeasureState.ResolveReferenceBody`) is the user override from the window's
Reference combo, else the camera's `Following`; a followed vehicle defers to its SOI parent in the
map view (when its flight plan has patches, because `Vehicle.Orbit` reads `FlightPlan.Patches[0]`)
and stays the vehicle in the flight view, else the system's `HomeBody`.[^mt-state] The comment in
the picker records that the ecliptic normal is ECL `+Z`, checked in game against orbit-point offsets,
and that `Camera.UpView` (`double3.UnitY`) is only the camera-up convention.

`MathEx.RayPlaneIntersection(originEcl, direction, planePoint, normal, out t)` gives `t`; points
behind the camera (`t <= 0`) are rejected.[^ksa-mathex] The hit becomes:

- `Anchor.Free(refBody, refBody.GetPositionCceFromEcl(pointEcl))`: an offset in the reference
  body's CCE frame, so a free point next to a planet keeps riding with that planet instead of being
  left behind in absolute ECL space.[^mt-anchor]
- `Anchor.EditorFree(pointEcl - EditingSpace.PositionEcl)` in the editor, since the editing space
  never moves during a session.

Putting the plane through the previous pending point gives all points of one measurement a common
depth basis, so a two-click free ruler measures what the player sees.

### World to screen (the other direction)

Every overlay point is drawn with `viewport.Position + camera.EclToScreen(posEcl)`.
`EclToScreen` is `EclToEgo` then `EgoToScreen`, and `EgoToScreen` returns `(NaN, NaN)` for points
behind the camera (`ignoreBehind` defaults to `true`). The overlay treats NaN and infinity as "do not
draw", which is how all of its lines, dots and labels cull.[^ksa-camera][^mt-overlay]

### The construction plane preview

`MeasureOverlay.DrawConstructionPlane` builds an in-plane basis from the normal and a seed axis,
draws a 64-segment circle of radius `0.4 * depth * tan(fov / 2)` (about 40% of the half view height
at the plane's distance, via `camera.GetFieldOfView()`), breaks the polyline wherever a sample
projects to NaN, and draws a spoke from the plane anchor to the cursor.[^mt-overlay]

## Lifecycle and timing

- Placement runs in the `Program.OnMouseButton` prefix (`frame:input`). `ScreenToEgoRay` reads the
  view-projection inverse that `Camera.OnFrame` cached last frame (it is rebuilt later this frame in
  `OnFrameViewports`), and `Cursor.GetPosition` reads the mouse position `Program.PrepareImGui`
  copied last frame, so ray and cursor both describe the image the player clicked on. The bodies
  were already advanced by the just-applied physics step, and a following camera's `PositionEcl` is
  computed live from the followed body, so the ray origin moved with it. Because anchors are stored
  relative to a body, the one-step difference only shifts where on the plane the point lands, not
  what it follows.[^ksa-camera][^ksa-cursor]
- The preview runs in `[StarMapAfterGui]` (`frame:draw-ui`), after `OnFrameViewports` computed this
  frame's camera, so preview and drawn overlay use the same camera.
- Nothing here writes game state.

## Key code

| Path | Role |
|---|---|
| [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L988-L1038) | `TryGetFreePlane`, `PickFreePoint` |
| [MeasureTools/Features/Measure/MeasureState.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureState.cs#L305-L330) | `ResolveReferenceBody` |
| [MeasureTools/Features/Measure/Anchor.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Anchor.cs#L329-L348) | `EditorFree` and `Free` anchors |
| [MeasureTools/Features/Measure/MeasureOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureOverlay.cs#L656-L700) | Construction-plane disc and spoke |

## Snippets

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

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `Camera.ScreenToEgoRay`, `EgoToEcl`, `EclToScreen`, `EgoToScreen`, `GetForwardEcl`, `GetFieldOfView`, `PositionEcl`, `Following` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L704-L740) | Unprojection, projection with behind-camera NaN, plane normal and size |
| `GameViewport.GetCamera` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L297-L300) | Map camera in Map mode, base camera otherwise |
| `MathEx.RayPlaneIntersection` | [current/decomp/KSA/MathEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MathEx.cs#L1347-L1362) | Ray-plane hit, rejects `t < 0` |
| `Astronomical.GetPositionCceFromEcl`, `GetPositionEclFromCce`, `GetPositionEcl` | [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L369-L378) | Body-relative storage of free points |
| `VehicleEditingSpace.PositionEcl` | [current/decomp/KSA/VehicleEditingSpace.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditingSpace.cs) | Editor free-point origin |
| `Cursor.GetPosition` | [current/decomp/KSA/Cursor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Cursor.cs#L17-L30) | Viewport-local cursor |

## Pitfalls and version notes

- Do not assume the ray starts at the camera. In orthographic mode `ScreenToEgoRay` starts at the
  near-plane point; code that uses `camera.PositionEcl` as the origin lands every editor click at the
  same place along the view axis.
- `EclToScreen` returns screen coordinates relative to the viewport; add `viewport.Position` before
  drawing into a draw list that covers the whole window.
- `Camera.UpView` is `double3.UnitY` and is not the ecliptic normal.
- `Vehicle.Orbit` dereferences `FlightPlan.Patches[0]`; guard on `Patches.Count` before using it in
  per-frame UI code.
- Ctrl skips all snapping, not only the plane choice (see `MapPicker.Pick`).

## See also

- [Overview](../overview.md)
- [Click capture](./click-capture-mouse-prefix.md)
- [Live anchors in body, orbit, surface and part frames](./live-body-relative-anchors.md)
- [Overlay rendering](./overlay-drawlist-rendering.md)
- [Cursor to world ray: the ego frame, EgoToEcl and picking points on a construction plane](../../../howto/ui/input/cursor-to-world-ray/cursor-to-world-ray-5.md) - how-to article demonstrating this feature

[^mt-mappicker]: MeasureTools/Features/Measure/MapPicker.cs
[^mt-anchor]: MeasureTools/Features/Measure/Anchor.cs
[^mt-state]: MeasureTools/Features/Measure/MeasureState.cs
[^mt-overlay]: MeasureTools/Features/Measure/MeasureOverlay.cs
[^mt-readme]: KSA-MeasureTools README.md
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ksa-mathex]: KSA.MathEx (decompiled, build 2026.10.10.5554)
[^ksa-cursor]: KSA.Cursor (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditor]: KSA.VehicleEditor (decompiled, build 2026.10.10.5554)
