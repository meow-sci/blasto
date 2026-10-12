---
type: Game Feature
title: Let the IVA camera leave the seat and fly around the cabin
description: How IVA mode is pinned to a seat, what depends on the mode staying IVA, and the controller-swap approach that frees the camera without losing interiors, audio or ray tracing
category: camera
group: special-views
topic: unlock-the-iva-camera
demonstrated_by: [mods/unscience/features/kitchen-sink-iva-camera-unlock]
lifecycle: [frame:viewports, frame:input, frame:controller, frame:render, event:save, event:load]
game_types: [KSA.GameViewport, KSA.IVAController, KSA.FlyController, KSA.IVASeat, KSA.Camera, KSA.Vehicle]
tags: [camera, iva, free-camera, interior]
---
In `IVA` mode the camera sits at an `IVASeat` module on the followed vehicle and the player can only look around within the seat's hemisphere. Many things hang on the mode being `IVA`: the interior audio blend, the IVA ray-traced lighting and the seated
kitten's head. A mod that wants a free camera inside the cabin therefore cannot simply switch to `Free`; it has to keep the mode and replace what moves the camera.

## Engine background

| Type | Role | Source |
|---|---|---|
| `IVAController` | Pins the camera to `Seat`; bails to `NextCameraMode` on a bad state; `OnSwitchOn` picks a seat | [current/decomp/KSA/IVAController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVAController.cs#L27-L41), [current/decomp/KSA/IVAController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVAController.cs#L155-L198) |
| `GameViewport.IvaController` | `{ get; protected set; }`; returned by `GetActiveController()` for `IVA` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L40), [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L284-L295) |
| `IVASeat` | Seat module; private `IsCameraInThisSeat(viewport)` hides the seated kitten's head | [current/decomp/KSA/IVASeat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVASeat.cs#L80-L110) |
| Dependents of `Mode == IVA` | Audio blend, ray tracing | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L140), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4775) |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1. `UnlockedIvaController` delegating to a `FlyController`, plus an `IsCameraInThisSeat` postfix](unlock-the-iva-camera.md) | unscience (Kitchen Sink) | `controller-subclass` | `startup:all-mods-loaded`, `frame:viewports`, `event:save` | Keeps everything IVA; main viewport only; no collision |

## See also

- [Replace a viewport's camera controller and take input through it](../../controllers/replace-a-viewport-camera-controller/overview.md), [Hide part meshes or subparts without removing them](../../../rendering/meshes/hide-parts-or-subparts-from-rendering/overview.md), [Hide a kitten's body from the main view only, keeping its shadow](../../../kittens/appearance/hide-a-kitten-body-from-one-viewport/overview.md)
- [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
