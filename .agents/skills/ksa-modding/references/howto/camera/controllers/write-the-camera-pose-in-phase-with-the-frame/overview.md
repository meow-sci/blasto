---
type: Game Feature
title: Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller
description: Why a camera pose set from a StarMap GUI hook is drawn a frame late and the four ways mods write it where the engine's own controller does
category: camera
group: controllers
topic: write-the-camera-pose-in-phase-with-the-frame
demonstrated_by:
  - mods/ksagolf/features/level-horizon-camera-controller
  - mods/ksastructures/features/level-horizon-camera-controller
  - mods/ksarmory/features/chase-camera-on-a-round
  - mods/gatos/features/programmable-camera-director
  - mods/gatos/features/synthetic-camera-follow-anchor
  - mods/unscience/features/hot-pursuit-part-mounted-cameras
  - mods/unscience/features/hot-pursuit-mesh-ray-placement
  - mods/unscience/features/camera-controller-override-camera-animations
lifecycle: [frame:viewports, frame:draw-ui]
game_types:
  - KSA.FixedController
  - KSA.GameViewport
  - KSA.Camera
  - KSA.IViewport
  - KSA.OrbitController
  - KSA.FlyController
  - KSA.Controller
  - KSA.Transform3D
tags: [camera, phase, frame-order, fixed-camera, controller, jitter]
---
In one frame `Program.OnFrame` runs `OnFrameViewports` (controllers write cameras, `Camera.OnFrame` builds
matrices) and **then** `OnDrawUiFrame` / `OnDrawUiViewports`, where StarMap's `StarMapBeforeGui` and
`StarMapAfterGui` fire. A pose written from a hook is consumed by the next frame's viewport pass, while the
followed body has moved on; against a target at orbital speed that is jitter, scaling with simulation speed.
The cure is to write the pose where the engine writes it: inside the viewport pass, immediately before
`Camera.OnFrame`.

## Engine background

| Type | Role | Source |
|---|---|---|
| `GameViewport.OnFrame` | `GetActiveController().OnFrame` then `GetCamera().OnFrame` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L116-L124) |
| `FixedController.OnFrame` | The two stock writes: `PositionEcl = following + CameraOffset`, `LocalRotation` | [current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L22-L47) |
| `Camera.OnFrame`, `ClampCamera` | Clamp then matrices | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L487-L497), [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L633-L639) |
| `OrbitController.OnFrame`, `FlyController.OnFrame` | Rewrite the pose from their own state every frame | [current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L487-L500), [current/decomp/KSA/FlyController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlyController.cs#L653-L660) |
| `Program.OnFrameViewports` | Runs every viewport's `OnFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2608-L2689) |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1. `FixedController` subclass asks a pose source](write-the-camera-pose-in-phase-with-the-frame-1.md) | KSAGolf, KSAStructures, KSArmory | `controller-subclass` | `frame:viewports`, `frame:draw-ui` | Exact phase; needs a controller install ([reflection](../replace-a-viewport-camera-controller/overview.md)) |
| [2. Offset against the followed object sampled in the same call](write-the-camera-pose-in-phase-with-the-frame-2.md) | gatOS | `controller-subclass` | `frame:viewports` | Also in phase and keeps precision at orbital speed; viewport prefix and postfix for sampling and publishing |
| [3. Prefix `FixedController.OnFrame` for owned viewports](write-the-camera-pose-in-phase-with-the-frame-3.md) | unscience (Hot Pursuit) | `harmony-prefix` | `frame:viewports` | No controller swap and other Fixed cameras run stock; Harmony on a hot path |
| [4. Shared name-resolved prefix on Orbit and Free](write-the-camera-pose-in-phase-with-the-frame-4.md) | unscience (Camera Controller Override) | `harmony-prefix` | `frame:viewports`, `frame:draw-ui` | Keeps the player's camera; every Orbit/Free viewport is affected and the pose jumps back when it ends |

## See also

- [Replace a viewport's camera controller and take input through it](../replace-a-viewport-camera-controller/overview.md), [Drive the main camera through Fixed mode without crashing it, with a stable up vector](../drive-the-main-camera-in-fixed-mode/overview.md), [Mount a camera on a vessel part and look through it](../../secondary-viewports/mount-a-camera-on-a-part/overview.md), [Understand KSA camera modes, controllers, the viewport frame order and which camera is which](../understand-camera-modes-and-controllers/overview.md)
- [Keep mod UI and per-frame work running when the HUD is hidden (F2)](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md), [Animate the flight camera with eased moves, keyframe sequences and saved recipes](../../special-views/chain-camera-moves-into-keyframe-sequences/overview.md)
- [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
