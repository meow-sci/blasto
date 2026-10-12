---
type: Game Feature
title: Aim a secondary viewport camera, rebuild its matrices and give it its nearby body
description: A secondary camera follows nothing and has its matrices built before a GUI hook runs, and the engine fills its nearby planet only for the frame camera; two ways to aim it correctly
category: camera
group: secondary-viewports
topic: aim-a-secondary-viewport-camera
demonstrated_by:
  - mods/ksarmory/features/director-camera-window
  - mods/unscience/features/hot-pursuit-part-mounted-cameras
lifecycle: [frame:draw-ui, frame:viewports, frame:render, frame:celestials]
game_types:
  - KSA.Camera
  - KSA.IGameViewport
  - KSA.FixedController
  - KSA.Program
  - KSA.Celestial
  - KSA.StaticCelestialDistanceRendering
  - KSA.StaticObjectRenderer
tags: [camera, secondary-viewport, aim, matrices, nearby-celestial]
---
Leasing a secondary viewport ([Lease a secondary viewport for a mod camera and show it in a window](../borrow-a-secondary-viewport/overview.md)) gives a mod a camera. Aiming it correctly takes care of two engine facts: the camera's view matrices are built in `Camera.OnFrame` during the viewport pass, before any GUI hook, and the nearby body, distances and altitude that terrain, sky and static objects read are computed only for
the frame camera in `Program.OnFrameCelestials`.

## Engine background

| Type | Role | Source |
|---|---|---|
| `Camera.LookAt`, `OnFrame`, `ClampCamera` | Pose, matrices, 0.5 m terrain clamp | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L211-L220), [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L487-L497), [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L633-L639) |
| `Camera.NearbyCelestial` and the distance fields; `GetEffectiveNearbyCelestial` | Per-camera state | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L21-L28), [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L164-L167) |
| `Program.OnFrameCelestials` | Fills those fields for the frame camera only | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2701-L2732) |
| `Program.UpdateShaderData`, `SetCameraUbo`, `FindNearbyCelestial` | Per-viewport shader data and the nearby lookup | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1346-L1360), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2854-L2860), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5228) |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1. `LookAt` from the hook, then `OnFrame`, `UpdateShaderData`, `SetCameraUbo`; `SetNearbyContext`](aim-a-secondary-viewport-camera-1.md) | KSArmory | `direct-camera-write` | `frame:draw-ui`, `frame:render` | Works from a plain hook; three manual rebuilds that may be redundant |
| [2. Pose written in a `FixedController.OnFrame` prefix; nearby sync inside it](aim-a-secondary-viewport-camera-2.md) | unscience | `harmony-prefix` | `frame:viewports` | In phase, no rebuilds; a Harmony patch scoped to owned viewports |

## See also

- [Lease a secondary viewport for a mod camera and show it in a window](../borrow-a-secondary-viewport/overview.md), [Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md), [Mount a camera on a vessel part and look through it](../mount-a-camera-on-a-part/overview.md), [Turn the mouse cursor into a world ray and pick points on a construction plane](../../../ui/input/cursor-to-world-ray/overview.md)
- [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
