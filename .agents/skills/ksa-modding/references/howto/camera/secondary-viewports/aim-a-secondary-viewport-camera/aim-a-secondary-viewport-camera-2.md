---
type: How-To
title: Write the pose in a FixedController.OnFrame prefix before Camera.OnFrame and synchronize the nearby body inside it
description: Hot Pursuit composes the pose in the main camera's ego space, writes it before the stock Camera.OnFrame builds the matrices, repeats ClampCamera and fills the camera's nearby-celestial fields in the same prefix so no matrix rebuild is needed
category: camera
group: secondary-viewports
topic: aim-a-secondary-viewport-camera
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/hot-pursuit-part-mounted-cameras]
techniques: [harmony-prefix]
hooks: [harmony-prefix KSA.FixedController.OnFrame]
lifecycle: [frame:viewports, frame:render, frame:celestials]
game_types:
  - KSA.Camera
  - KSA.IGameViewport
  - KSA.FixedController
  - KSA.Program
  - KSA.Celestial
  - KSA.StaticCelestialDistanceRendering
  - KSA.StaticObjectRenderer
tags: [camera, secondary-viewport, nearby-celestial, in-phase, hot-pursuit]
---
The other way to aim a secondary camera is to write the pose where the matrices are still to be built, so nothing needs rebuilding. Hot Pursuit does that from a prefix on `FixedController.OnFrame` ([approach 3](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md)) and adds the one thing the prefix does not
give for free: the camera's nearby body. The pose maths is [Mount a camera on a vessel part and look through it](../mount-a-camera-on-a-part/overview.md). Prerequisites: StarMap and Harmony.

## Background: how the game does it

As in [approach 1](aim-a-secondary-viewport-camera-1.md): `OnFrameCelestials` fills the nearby fields only for the frame camera ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2701-L2732)). Which consumers need them matters:

- `StaticObjectRenderer.WriteCommandsColor` returns early when `NearbyCelestial` is null, so launch pads and other statics are missing from a secondary feed without it.
- The atmosphere renderer's `DistantPlanet` flag compares against it.
- Since 5541 `Camera.GetEffectiveNearbyCelestial()` is `NearbyCelestial ?? Program.FindNearbyCelestial(this)`, and 5554 uses it in the distant-sphere and atmosphere paths; that already covers the dark disc the distant-sphere pass drew across the feed ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L164-L167)).

## Lifecycle and timing

The prefix is `frame:viewports`, immediately before `Camera.OnFrame` builds the matrices and frustum ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L116-L124)), so no rebuild is needed; the feed has no one-frame lag. The nearby-body sync runs in the same prefix. The secondary scene is rendered later in `frame:render`.

## Give the camera its nearby body

1. **Compose in the main camera's ego space and convert once.** Working in ego (camera-relative) space and converting to ecliptic once avoids subtracting huge coordinates. The pose is then written:

```csharp
// hot-pursuit.lib/HotPursuitPose.cs L66-L76

var camera = ownedViewport.BaseCamera;
camera.PositionEcl = positionEcl;
// GameViewport calls Camera.OnFrame after this prefix, which terrain-clamps the camera.
// Clamp once now as well so the celestial metrics below describe the final render pose;
// Camera.OnFrame's second clamp is then idempotent.
camera.ClampCamera();
HotPursuitCelestialState.Synchronize(camera);
camera.WorldRotation = Camera.LookAtRotation(forwardEcl, upEcl);
camera.SetFieldOfView(entry.FieldOfView);
return true;
```

2. **Repeat `ClampCamera` first**, because `Camera.OnFrame` will clamp again and the nearby metrics must describe the final render pose; the second clamp is then idempotent.

3. **Synchronize the nearby-celestial fields for this camera**: `FindNearbyCelestial(camera)`, the engine's 80,000 km surface-distance cut-off, the distances, terrain height, and altitude above the terrain:

```csharp
// hot-pursuit.lib/HotPursuitCelestialState.cs L12-L38
internal static void Synchronize(Camera camera)
{
    Celestial? nearby = Program.FindNearbyCelestial(camera);
    if (nearby == null)
    {
        camera.NearbyCelestial = null;
        return;
    }

    double distanceKm = camera.DistanceTo(nearby.GetPositionEcl()) * MetersToKilometers;
    double surfaceDistanceKm = distanceKm - nearby.MeanRadius * MetersToKilometers;
    if (surfaceDistanceKm > MaxSurfaceDistanceKm)
    {
        camera.NearbyCelestial = null;
        return;
    }

    camera.NearbyCelestial = nearby;
    camera.DistanceToNearbyCelestialKm = distanceKm;
    camera.DistanceToNearbyCelestialSurfaceMeanKm = surfaceDistanceKm;
    camera.NearbyCelestialTerrainHeight = nearby.GetTerrainHeight(camera) * MetersToKilometers;

    double3 positionCce = nearby.GetPositionCce(camera);
    double terrainRadius = nearby.MeanRadius +
        nearby.GetTerrainHeightFromDirCce(positionCce.Normalized());
    camera.CurrentAltitudeKm = (positionCce.Length() - terrainRadius) * MetersToKilometers;
}
```

4. **Without it**, the nearby body was drawn by the distant-sphere pass as a large dark-grey disk across the feed (before 5541's fallback), and statics were missing.

## Complete example

- [hot-pursuit.lib/HotPursuitPose.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitPose.cs), [hot-pursuit.lib/HotPursuitCelestialState.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitCelestialState.cs), [hot-pursuit.lib/HotPursuitPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitPatches.cs)

## Variations and gotchas

- **The main camera is the reference frame for ego math.** Using the secondary camera's own (previous) pose would add a one-frame floating-origin error.
- **The camera is terrain-clamped to 0.5 m** by `Camera.OnFrame` as well, so mounts on a landed vessel's underside are lifted.
- **The sync still matters** for code that reads `NearbyCelestial` directly even with the engine's fallback.
- **No `UpdateShaderData` call here**: the write is in phase, so `Render` builds the data from the final pose.

## See also

- [Topic overview](overview.md); [approach 1](aim-a-secondary-viewport-camera-1.md)
- [Live part-mounted cameras in leased stock secondary viewports](../../../../mods/unscience/features/hot-pursuit-part-mounted-cameras.md)
- [Lease a secondary viewport for a mod camera and show it in a window](../borrow-a-secondary-viewport/overview.md), [Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md), [Mount a camera on a vessel part and look through it](../mount-a-camera-on-a-part/overview.md), [Turn the mouse cursor into a world ray and pick points on a construction plane](../../../ui/input/cursor-to-world-ray/overview.md)
