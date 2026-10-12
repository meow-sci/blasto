---
type: Game Feature
title: Understand KSA camera modes, controllers, the viewport frame order and which camera is which
description: Reference for every camera mod covering the per-viewport Orbit, Free, Map, IVA and Fixed controllers, the viewport pass order, mode-change timing, Fixed requirements and how to pick the right camera object
category: camera
group: controllers
topic: understand-camera-modes-and-controllers
demonstrated_by:
  - mods/ksagolf/features/level-horizon-camera-controller
  - mods/ksarmory/features/chase-camera-on-a-round
  - mods/ksarmory/features/burst-flash-and-view-shake
  - mods/unscience/features/glass-fov-override
  - mods/gatos/features/programmable-camera-director
lifecycle: [frame:viewports, frame:controller, frame:input, frame:draw-ui, frame:render]
game_types:
  - KSA.Program
  - KSA.GameViewport
  - KSA.ViewportBase
  - KSA.Controller
  - KSA.Camera
  - KSA.CameraMode
  - KSA.FixedController
  - KSA.OrbitController
  - KSA.FlyController
  - KSA.IVAController
  - KSA.MapController
  - KSA.ViewportRegistry
  - KSA.IGameViewport
tags: [camera, viewport, controller, camera-mode, frame-order, reference]
---
Every KSA camera mod relies on the same few engine facts, and getting one wrong either puts the pose one
frame late or ends the process. This topic records them. The topic's first article is the reference;
the other three are small recipes for the question every camera mod meets next: **which camera is the
main one?**

## What the system is

Each `GameViewport` owns a `BaseCamera` and a `MapCamera` and five controllers, one per `CameraMode`
(`Orbit`, `Free`, `Map`, `IVA`, `Fixed`). The engine has eight viewports: the main one, a part thumbnail,
four secondary camera windows and two crew-portrait cameras. `Program.OnFrameViewports` runs every one of
them each frame: the active controller writes the camera, then `Camera.OnFrame` clamps it to the terrain and
builds the matrices. Nothing else moves a camera. `Program.OnFrameController` only syncs the cursor mode
with the active controller. StarMap's `StarMapBeforeGui` and `StarMapAfterGui` hooks run after the viewport pass, so a pose
written from a hook is drawn a frame late.

## Engine background

| Type | Role | Source |
|---|---|---|
| `Program.OnFrameViewports`, `OnFrameController` | The viewport pass and the cursor-mode sync | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2608-L2689), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2520-L2552) |
| `Program.GetMainCamera`, `GetCamera`, `GetCameraMode`, `MainViewport`, `FrameViewport` | Camera accessors; `GetMainCamera` is the map camera in Map mode | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L484-L492), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L631-L654) |
| `GameViewport` | Owns the cameras and controllers; `OnFrame`, `GetActiveController`, `GetCamera`, `SetCameraMode`, `NextCameraMode` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L116-L124), [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L284-L330) |
| `ViewportBase`, `IGameViewport`, `ViewportRegistry` | `Mode`, the registry of viewports and the main-camera test `IsMainCamera` | [current/decomp/KSA/ViewportBase.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportBase.cs#L36), [current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L41-L60), [current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L101-L105) |
| `Controller` and the five controllers | `OnFrame`, input virtuals, `GetCursorMode` | [current/decomp/KSA/Controller.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Controller.cs#L22-L94) |
| `FixedController` | The mod camera mode: `CameraOffset`, `CameraRotation`, reads no input | [current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L7-L48) |
| `Camera` | `OnFrame`, `ClampCamera`, `SetFollow`, `SetFieldOfView`, `ChangeFieldOfView` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L487-L497), [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L602-L639) |

Facts worth memorising (all verified in build 2026.10.10.5554):

- `SetCameraMode` changes the field at once; the pose changes at the next viewport pass.
- The player's mode key cycles `Orbit`, `Free`, `IVA` only; a viewport in `Fixed` cannot be left from the keyboard (`Map` has its own toggle-map key, handled in `MapController.OnKey`, that returns to `Orbit`).
- `FixedController.OnFrame` normalises `CameraRotation`; zero throws `DivideByZeroException` and the frame loop has no catch.
- Camera patches fire for every viewport's camera: gate them on the instance.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1. Frame order, per-mode behaviour, crash inventory](understand-camera-modes-and-controllers-1.md) | KSAGolf, KSArmory | `starmap-lifecycle` | `frame:viewports`, `frame:draw-ui` | The reference; nothing to install, but you must apply it |
| [2. `MainViewport.BaseCamera` for the scene camera](understand-camera-modes-and-controllers-2.md) | KSArmory | plain property access | `frame:draw-ui`, `frame:celestials` | Right in map mode too; the field of view must still come from `GetCamera()` |
| [3. Gate `Camera` patches with `ViewportRegistry.IsMainCamera`](understand-camera-modes-and-controllers-3.md) | unscience (Glass) | `harmony-prefix` | `frame:input`, `frame:viewports` | One boolean per call for other cameras; string lookups of private members |
| [4. Prefix on the main `GameViewport.OnFrame`](understand-camera-modes-and-controllers-4.md) | gatOS | `harmony-prefix` | `frame:viewports` | Same-frame work before the controller; a patch on a per-viewport hot path |

## See also

- [Replace a viewport's camera controller and take input through it](../replace-a-viewport-camera-controller/overview.md), [Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../write-the-camera-pose-in-phase-with-the-frame/overview.md), [Drive the main camera through Fixed mode without crashing it, with a stable up vector](../drive-the-main-camera-in-fixed-mode/overview.md)
- [Understand KSA coordinate frames and epochs (ECL, CCE, CCI, CCF, ego, body)](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md), [Lease a secondary viewport for a mod camera and show it in a window](../../secondary-viewports/borrow-a-secondary-viewport/overview.md), [Override the camera field of view and zoom past the stock clamp](../../main-view/override-the-camera-field-of-view/overview.md)
- [Pack parameters and camera data for a compute effect](../../../shaders/compute-effects/pack-parameters-for-a-compute-effect/overview.md), [Draw full-screen click-through overlays and choose the right ImGui draw list](../../../ui/overlays/draw-a-fullscreen-click-through-overlay/overview.md)
- [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
