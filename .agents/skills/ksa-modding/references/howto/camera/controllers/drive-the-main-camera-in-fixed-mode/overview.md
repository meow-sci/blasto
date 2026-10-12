---
type: Game Feature
title: Drive the main camera through Fixed mode without crashing it, with a stable up vector
description: What Fixed mode requires (a followed object, a non-zero CameraRotation, a way out for the player) and how mods keep the horizon level with their own up vector
category: camera
group: controllers
topic: drive-the-main-camera-in-fixed-mode
demonstrated_by:
  - mods/ksagolf/features/level-horizon-camera-controller
  - mods/ksastructures/features/level-horizon-camera-controller
  - mods/ksagolf/features/borrowed-chase-camera
  - mods/ksarmory/features/borrow-main-view-camera
  - mods/ksarmory/features/chase-camera-on-a-round
  - mods/ksarmory/features/director-camera-window
  - mods/gatos/features/programmable-camera-director
lifecycle: [frame:viewports, frame:controller, frame:draw-ui]
game_types:
  - KSA.FixedController
  - KSA.GameViewport
  - KSA.ViewportBase
  - KSA.Camera
  - KSA.CameraMode
  - KSA.IFollowable
  - KSA.OrbitView
tags: [camera, fixed-camera, crash, roll, up-vector, camera-mode]
---
`Fixed` is the camera mode KSA ships for a camera a program controls: the pose is a pure function of the followed object's
position, `CameraOffset` and `CameraRotation`, and the controller reads no input. That makes it the mode every
mod-driven camera in this corpus uses, and it has four sharp edges: a zero or axis-parallel `CameraRotation` throws, the
roll comes from the followed object's type (wrong for mod objects and a hard flip near the poles), nothing leaves the mode
but an explicit `SetCameraMode`, and entry raises an alert and clears held input.

## Engine background

| Type | What matters | Source |
|---|---|---|
| `FixedController` | `CameraOffset`, `CameraRotation`, `DockingConnector`; `OnFrame`; `GetFrame2Ecl`; `OnSwitchOn` alert | [current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L7-L48), [current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L50-L100), [current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L140-L143) |
| `GameViewport.SetCameraMode`, `NextCameraMode`, `ResetToDefaults` | Immediate mode field; the mode key cannot leave Fixed; defaults zero the rotation | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L302-L330), [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L66-L85) |
| `Camera.LookAt`, `LookAtRotation`, `Unfollow` | Position and orientation; normalises and throws on a degenerate pair | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L203-L220), [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L620-L631) |
| `Program` View menu | The only player-facing way out of `Fixed` (the `SetCamera` console command, opening the vehicle editor and a save load also set the mode) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3690-L3702) |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1. Never enter Fixed with a zero or along-axis forward](drive-the-main-camera-in-fixed-mode-1.md) | KSAGolf | `reflection` | `frame:draw-ui`, `frame:viewports` | Cheap guards; relies on the step re-asserting the mode |
| [2. Set rotation before the mode, restore the mode explicitly](drive-the-main-camera-in-fixed-mode-2.md) | KSArmory | `state-record-and-restore` | `frame:draw-ui`, `frame:celestials` | The borrower is the only thing that can leave Fixed |
| [3. Park through the Mode setter, silence the alert](drive-the-main-camera-in-fixed-mode-3.md) | gatOS | `controller-subclass` | `frame:viewports` | No alert and no held-input clear; skips Map/IVA bookkeeping |
| [4. Unfollowed Fixed with `LookAt` and the local up](drive-the-main-camera-in-fixed-mode-4.md) | KSArmory | `direct-camera-write` | `frame:draw-ui`, `frame:viewports` | No controller trap, but must rebuild matrices and unfollow every frame |
| [5. Carried, rate-limited `StableUp`](drive-the-main-camera-in-fixed-mode-5.md) | KSAGolf, KSAStructures | `controller-subclass` | `frame:viewports` | No flip near vertical; a controller to install |
| [6. Controller adds the up the stock lacks](drive-the-main-camera-in-fixed-mode-6.md) | KSArmory | `controller-subclass` | `frame:viewports`, `frame:draw-ui` | Correct for mod-simulated bodies; reflection on a backing-field name |

## See also

- [Replace a viewport's camera controller and take input through it](../replace-a-viewport-camera-controller/overview.md), [Borrow the main camera, hand it back and arbitrate between camera users](../../main-view/borrow-the-main-camera-and-hand-it-back/overview.md), [Understand KSA camera modes, controllers, the viewport frame order and which camera is which](../understand-camera-modes-and-controllers/overview.md)
- [Chase a projectile or other fast body with the main camera](../../main-view/chase-a-fast-body-with-the-main-camera/overview.md), [Aim a secondary viewport camera, rebuild its matrices and give it its nearby body](../../secondary-viewports/aim-a-secondary-viewport-camera/overview.md)
- [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
