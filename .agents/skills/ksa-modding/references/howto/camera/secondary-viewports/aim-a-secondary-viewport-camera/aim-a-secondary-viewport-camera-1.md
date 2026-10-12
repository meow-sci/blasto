---
type: How-To
title: Aim from the GUI hook with LookAt, then rebuild the matrices and per-viewport shader data and fill the nearby body yourself
description: KSArmory unfollows and sets Fixed, anchors the eye on the platform as that window's camera draws it, writes the pose with LookAt, then calls Camera.OnFrame, UpdateShaderData and SetCameraUbo and copies the nearby-celestial state the engine only computes for the frame camera
category: camera
group: secondary-viewports
topic: aim-a-secondary-viewport-camera
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/director-camera-window]
techniques: [direct-camera-write]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:viewports, frame:render, frame:celestials]
game_types: [KSA.Camera, KSA.IGameViewport, KSA.FixedController, KSA.Program, KSA.Celestial]
tags: [camera, secondary-viewport, lookat, matrices, nearby-celestial, terrain]
---
A leased secondary camera follows nothing unless told to, its view matrices were built in the viewport pass before your hook ran, and the engine fills its nearby planet, distances and altitude only for the frame camera. KSArmory aims a window camera from its GUI hook and does the three rebuilds itself,
so the window is drawn from the aim of this frame and the sky, terrain detail and statics look right. The lease is [Lease a secondary viewport for a mod camera and show it in a window](../borrow-a-secondary-viewport/overview.md); the alternative that avoids the rebuild is [approach 2](aim-a-secondary-viewport-camera-2.md). Prerequisites: StarMap.

## Background: how the game does it

- `GameViewport.OnFrame` runs the controller and `Camera.OnFrame` (clamp then matrices) in `frame:viewports`; the GUI hooks come after ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L116-L124), [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L487-L497)).
- `Program.OnFrameCelestials` computes `NearbyCelestial`, the distances, the terrain height and `CurrentAltitudeKm` for `GetCamera()` only, the frame camera (the main viewport's at that point), with an 80,000 km cut-off ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2701-L2732)). On a secondary camera they are stale or null.
- `Camera.GetEffectiveNearbyCelestial()` falls back to `Program.FindNearbyCelestial(camera)` for some consumers, but others read the field directly ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L164-L167), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5228)).
- `Render` rebuilds every viewport's shader data with `UpdateShaderData` after the hooks anyway ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2459-L2466)); `UpdateShaderData(double, IViewport)` and `SetCameraUbo(IViewport)` are public instance methods ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1346-L1360), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2854-L2860)).
- A camera that follows a craft draws its physics-bubble neighbours at their physics positions, while one following nothing draws every craft at its analytic position (`Camera.GetPositionEgo`, [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L236-L250)); they differ by metres.

## Lifecycle and timing

| Step | Where | Value |
|---|---|---|
| Resolve the eye and line of sight from the head | `StarMapAfterGui` step, `DriveWindow` | `frame:draw-ui` |
| `LookAt`, `SetFieldOfView`, `SetNearbyContext`, `camera.OnFrame`, `UpdateShaderData`, `SetCameraUbo` | same call | `frame:draw-ui` |
| Controller `OnFrame` (a no-op: nothing followed) and `Camera.OnFrame` (terrain clamp) | next viewport pass | `frame:viewports` |
| Secondary viewport render | `RenderGame` | `frame:render` |

Everything the window is drawn from is sampled in this frame: vehicles were advanced in `PrepareFrame`, the head was stepped earlier in the same step, and the pose is written straight to the camera ([game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence)).

## Steps

1. **Unfollow and set `Fixed`, then `LookAt`.** With nothing followed `FixedController.OnFrame` does nothing, so the written pose stands ([approach 4](../../controllers/drive-the-main-camera-in-fixed-mode/overview.md)).

2. **Anchor the eye as this window's camera draws it**, not at the analytic position:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L2935-L2950
public static bool TryDrawnCentreEcl(Vehicle craft, out double3 ecl, int viewIndex = -1)
{
    ecl = default;

    try
    {
        if (CameraFor(viewIndex) is not { } camera || !IsAlive(craft)) return false;

        ecl = camera.EgoToEcl(craft.CenterOfMassAsmb.Transform(craft.GetMatrixAsmb2Ego(camera)));
        return Vec.IsFinite(ecl);
    }
    catch
    {
        return false;
    }
}
```

3. **Set the field (clamped) and then the nearby-body context for this camera**, same cut-off as the engine, so terrain level of detail and the sky read the right body:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L5761-L5780
private static void SetNearbyContext(Camera camera)
{
    Celestial? body = Program.FindNearbyCelestial(camera);

    if (body is null || !body.IsBillboarded())
    {
        camera.NearbyCelestial = null;
        return;
    }

    double distanceKm = camera.DistanceTo(body.GetPositionEcl()) * 0.001;
    double surfaceKm = distanceKm - body.MeanRadius * 0.001;

    // The engine's own cut-off for a nearby body, so a window and the main view agree on it.
    camera.NearbyCelestial = surfaceKm > 80000.0 ? null : body;
    camera.DistanceToNearbyCelestialKm = distanceKm;
    camera.DistanceToNearbyCelestialSurfaceMeanKm = surfaceKm;
    camera.NearbyCelestialTerrainHeight = body.GetTerrainHeight(camera) * 0.001;
    camera.CurrentAltitudeKm = Program.GetCurrentAltitudeKm(camera);
}
```

4. **Rebuild the matrices now, then the per-viewport shader data.** `LookAt` sets rotation alone; the view matrices that the render, culling and projections read are built in `Camera.OnFrame`, which ran before the hook. Without it the window draws last frame's aim:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L5719-L5746
// Position and orientation together: setting one without the other leaves a frame
// drawn from the old place looking the new way.
camera.LookAt(eyeEcl, eyeEcl + Vec.Unit(forwardEcl) * 1000.0, upEcl);

// SetFieldOfView does not clamp, and the projection throws outside (0, 180).
float wanted = (float)Math.Clamp(double.IsFinite(fovDeg) ? fovDeg : SightZoom.DefaultFovDeg,
                                 SightZoom.MinFovDeg, SightZoom.MaxFovDeg);
if (Math.Abs(double.RadiansToDegrees(camera.GetFieldOfView()) - wanted) >= 1e-3)
{
    camera.SetFieldOfView(wanted);
}

SetNearbyContext(camera);

// LookAt sets the rotation alone; the view matrix the render, the culling and every
// projection read is rebuilt in Camera.OnFrame, which ran in the viewport pass before
// this. Without it the window draws with last frame's aim: one frame of the line of
// sight's swing, a fixed angle that judders with the frame time and grows with the zoom.
camera.OnFrame(dt);

// Setting the fields is not enough on its own: the sky and atmosphere are shaded from
// data the engine uploads per viewport, which by this point in the frame already holds
// where the camera *was*. These recompute and re-upload it for the new position.
if (Program.Instance is { } program)
{
    program.UpdateShaderData(dt, viewport);
    program.SetCameraUbo(viewport);
}
```

   The whole method is [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5679-L5755).

## Complete example

- [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L5679-L5780), [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L1109-L1170)
- [src/KSAStructures/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KsaWorld.cs#L919-L995): the same method in KSAStructures ([Fly the nearest craft from the office computer](../../../../mods/ksastructures/features/computer-flies-nearest-craft.md))

## Variations and gotchas

- **Observation, not measurement.** In 5554 `Render` rebuilds shader data for every viewport after the hook, and `SetCameraUbo` replaces the slot's data with a fresh struct, so the extra calls may be redundant; the feature page records this as read from the decomp, not measured.
- **`SetFieldOfView` does not clamp** and the projection throws outside (0, 180); clamp to 1-120 ([Override the camera field of view and zoom past the stock clamp](../../main-view/override-the-camera-field-of-view/overview.md)).
- **Name only when it differs.** `SetName` changes the ImGui window name the stock window is keyed on; rewriting it each frame resets where the player put the window.
- **Position as this window's camera draws it.** The metres between the analytic and physics positions are shake at high zoom.

## See also

- [Topic overview](overview.md); [approach 2](aim-a-secondary-viewport-camera-2.md)
- [Director camera windows on leased secondary viewports](../../../../mods/ksarmory/features/director-camera-window.md)
- [Lease a secondary viewport for a mod camera and show it in a window](../borrow-a-secondary-viewport/overview.md), [Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md), [Mount a camera on a vessel part and look through it](../mount-a-camera-on-a-part/overview.md), [Turn the mouse cursor into a world ray and pick points on a construction plane](../../../ui/input/cursor-to-world-ray/overview.md)
