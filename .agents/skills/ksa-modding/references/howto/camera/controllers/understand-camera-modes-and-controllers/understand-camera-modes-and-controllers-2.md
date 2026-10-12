---
type: How-To
title: Read the scene camera with MainViewport.BaseCamera when the main camera may be the map camera
description: Program.GetMainCamera answers with the map camera in Map mode, so a view effect that needs the scene camera reads MainViewport.BaseCamera and takes the field of view from the camera that is actually showing
category: camera
group: controllers
topic: understand-camera-modes-and-controllers
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/burst-flash-and-view-shake]
techniques: [starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:celestials, frame:viewports]
game_types: [KSA.Program, KSA.GameViewport, KSA.IGameViewport, KSA.Camera, KSA.CameraMode]
tags: [camera, main-camera, map-camera, base-camera, view-effect]
---
Many view effects need to know where the player's scene camera is: a flash that blinds only what is in
frame, a shake, a sound that arrives from a direction. The obvious call, `Program.GetMainCamera()`,
is wrong while the player is in map mode. This article answers **Which camera is the main
one?** with KSArmory's approach: read `Program.MainViewport.BaseCamera` for the scene pose and
take the field of view from the camera that is being shown. It needs only a StarMap mod; there is no
reflection, because `BaseCamera` is a public property of `GameViewport`.

## Background: how the game does it

`Program` offers several camera accessors and they are not interchangeable:

| Accessor | Returns | Source |
|---|---|---|
| `Program.GetMainCamera()` | `MainViewport.GetCamera()`: the **map** camera while the main viewport is in `Map` mode | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L631-L634) |
| `Program.MainViewport.BaseCamera` | The scene camera `Orbit`, `Free`, `IVA` and `Fixed` drive; in Map mode it keeps its last world pose | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L30) |
| `Program.GetCamera()` | `FrameViewport.GetCamera()`; `FrameViewport` is the main viewport at the start of each frame and the viewport being rendered inside `Render` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L646-L649) |
| `Program.GetHoveredCamera()`, `GetRenderCamera()` | The hovered or rendered viewport's camera | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L636-L644) |
| `Program.GetCameraMode()` | `FrameViewport.Mode` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L651-L654) |

and `GameViewport.GetCamera()` is the one place the choice is made:

```csharp
// current/decomp/KSA/GameViewport.cs L297-L300
public override Camera GetCamera()
{
	return (base.Mode == CameraMode.Map) ? MapCamera : BaseCamera;
}
```

In map mode `GetMainCamera()` hands back `MapCamera`, which the map controller poses on its own scope and
view, so code that measures "is this burst in front of the camera" against it is not measuring the scene view.

## Lifecycle and timing

`BurstFlash.Update` runs from KSArmory's once-per-frame step (`StepOnce`), which the mod reaches from
`[StarMapAfterGui]` (`frame:draw-ui`) while the HUD is shown, from a prefix on `Program.OnFrameCelestials`
(`frame:celestials`) on hidden-HUD frames, and from `[StarMapAfterOnFrame]` as a fallback
([src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L324-L345)).
All three run after this frame's `OnFrameViewports` built the cameras ([game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence)).
It therefore reads a pose that is already final for this frame and is the same pose the render will use:
nothing here is written, so there is no phase problem. A mod that writes the camera instead needs the
patterns in [Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../write-the-camera-pose-in-phase-with-the-frame/overview.md).

## Steps

1. **Read the scene pose from the base camera.** `Camera.EgoToEcl(double3.Zero)` is the camera's
   ecliptic position and `GetForwardEcl()` its forward vector. KSArmory's helper:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L4716-L4734
public static bool TryMainCameraPose(out double3 positionEcl, out double3 forwardEcl)
{
    positionEcl = forwardEcl = Vec.Zero;

    try
    {
        if (Program.MainViewport?.BaseCamera is not { } camera) return false;

        positionEcl = camera.EgoToEcl(Vec.Zero);
        forwardEcl = camera.GetForwardEcl();

        return Vec.IsFinite(positionEcl) && Vec.Len2(forwardEcl) > 1e-12;
    }
    catch (Exception e)
    {
        Log.Warn($"could not read the main camera pose: {e.Message}");
        return false;
    }
}
```

   The helper returns false rather than throwing, because it runs inside the game's frame loop,
   which has no catch ([current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L40-L71)).

2. **Read the field of view from the camera that is showing**, with `GetCamera()` (map camera in Map
   mode) because the picture the player sees uses that field. `GetFieldOfView()` returns radians:

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L4018-L4034
public static double MainViewFovDeg()
{
    try
    {
        if (Program.MainViewport?.GetCamera() is not { } camera) return SightZoom.DefaultFovDeg;

        double degrees = double.RadiansToDegrees(camera.GetFieldOfView());

        return double.IsFinite(degrees) && degrees > 0.0 && degrees < 180.0
            ? degrees
            : SightZoom.DefaultFovDeg;
    }
    catch
    {
        return SightZoom.DefaultFovDeg;
    }
}
```

3. **Use both together.** The flash takes half the field as its "in frame" cone:

```csharp
// src/KSArmory/Ksa/BurstFlash.cs L91-L100
if (!KsaWorld.TryMainCameraPose(out double3 eyeEcl, out double3 forwardEcl))
{
    Whiteout = 0f;
    return;
}

// Half the field, so a burst in frame blinds and one outside it only glares. The
// view's own rather than a constant: at the sight's magnification the frame is three
// degrees wide, and a burst twenty degrees off it is genuinely not being looked at.
double halfField = KsaWorld.MainViewFovDeg() * 0.5;
```

4. **When you write, write to the same object you read.** KSArmory's `TryPlaceMainCamera` sets
   `Program.MainViewport.BaseCamera.PositionEcl` for the same reason ([src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4746-L4762)).

## Complete example

- [src/KSArmory/Ksa/BurstFlash.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BurstFlash.cs): the flash that consumes the pose
- [src/KSArmory/Ksa/BlastShake.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BlastShake.cs): the shake struck as a blast front passes the camera
- [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4716-L4734): the pose helper

## Variations and gotchas

- **A viewport that is not the main one.** `BaseCamera` and `MapCamera` exist on every `IGameViewport`;
  use the viewport you hold, not `Program.MainViewport`, for a secondary camera.
- **Hovered is not main.** `Program.HoveredViewport` becomes a secondary viewport while the pointer
  is over it; `GetHoveredCamera()` follows it ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2608-L2625)).
- **Camera identity checks.** The engine ships `ViewportRegistry.IsMainCamera(Camera)`, true for the
  main viewport's `BaseCamera` and `MapCamera` ([current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L101-L105)); see
  [approach 3](understand-camera-modes-and-controllers-3.md).
- **Map mode pose.** In Map mode no controller writes `BaseCamera` (the map controller drives `MapCamera`), so
  it keeps its last rotation and, while a target is followed, its last offset from that target; an effect
  that reads `BaseCamera` therefore keeps working while the map is open.

## See also

- [Topic overview](overview.md), [approach 1](understand-camera-modes-and-controllers-1.md), [approach 3](understand-camera-modes-and-controllers-3.md), [approach 4](understand-camera-modes-and-controllers-4.md)
- [A burst's whiteout and a blast front's view shake, painted into the scene image instead of moving the camera](../../../../mods/ksarmory/features/burst-flash-and-view-shake.md)
- [Override the camera field of view and zoom past the stock clamp](../../main-view/override-the-camera-field-of-view/overview.md), [Pack parameters and camera data for a compute effect](../../../shaders/compute-effects/pack-parameters-for-a-compute-effect/overview.md), [Draw full-screen click-through overlays and choose the right ImGui draw list](../../../ui/overlays/draw-a-fullscreen-click-through-overlay/overview.md)
