---
type: Game Feature
title: Override the camera field of view and zoom past the stock clamp
description: Camera.SetFieldOfView takes degrees without clamping, GetFieldOfView answers radians, the zoom keys clamp to 15 to 120 degrees and the projection throws outside 0 to 180, and three ways mods hold a field
category: camera
group: main-view
topic: override-the-camera-field-of-view
demonstrated_by:
  - mods/unscience/features/glass-fov-override
  - mods/ksarmory/features/borrow-main-view-camera
  - mods/ksarmory/features/director-camera-window
  - mods/ksagolf/features/level-horizon-camera-controller
lifecycle: [frame:draw-ui, frame:input, frame:viewports]
game_types: [KSA.Camera, KSA.Program, KSA.GameSettings, KSA.ViewportRegistry]
tags: [camera, field-of-view, zoom, projection, lens]
---
Every camera stores its field of view as radians in a private field. Code sets it in degrees with `SetFieldOfView`, the player changes it with the zoom keys through `ChangeFieldOfView`, and `UpdateProjection` rebuilds the matrix from it. The three entry points disagree about units and
clamps, and one value (zero or half a turn) ends the process, so the safe way to override it depends on whether the mod is nudging, borrowing or enforcing.

## Engine background

| Member | Unit and behaviour | Source |
|---|---|---|
| `Camera.SetFieldOfView(float)` | Degrees; no clamp; rebuilds the projection | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L417-L421) |
| `Camera.GetFieldOfView()` | Radians | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L790-L793) |
| `Camera.ChangeFieldOfView(float)` | Zoom keys; clamps to `MINIMUM_FOV` 15 and `MAXIMUM_FOV` 120 | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L37-L39), [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L455-L462) |
| `Camera.UpdateProjection()` | Rebuilds from `_fovRadians`; the builder throws for zero or at least pi | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L471-L485), [current/decomp/RenderCore/ReverseDepthBufferUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ReverseDepthBufferUtils.cs#L8-L13) |
| `Camera.OnKey` | Handles the zoom keys before the active controller, in every mode | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L856-L874) |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1. Prefixes on `UpdateProjection` and `ChangeFieldOfView`](override-the-camera-field-of-view-1.md) | unscience (Glass) | `harmony-prefix` | `frame:input`, `frame:draw-ui`, `frame:viewports` | Holds a lens against every other path; patches private members by name |
| [2. `SetFieldOfView` in degrees, restated each frame](override-the-camera-field-of-view-2.md) | KSArmory | `direct-camera-write` | `frame:draw-ui`, `frame:viewports` | No patches; must clamp and rewrite every frame |
| [3. The field as one half of a borrowed view](override-the-camera-field-of-view-3.md) | KSAGolf | `state-record-and-restore` | `frame:draw-ui`, `frame:viewports` | Always returns the player's field; part of the borrowing protocol |

## See also

- [Understand KSA camera modes, controllers, the viewport frame order and which camera is which](../../controllers/understand-camera-modes-and-controllers/overview.md), [Borrow the main camera, hand it back and arbitrate between camera users](../borrow-the-main-camera-and-hand-it-back/overview.md), [Aim a secondary viewport camera, rebuild its matrices and give it its nearby body](../../secondary-viewports/aim-a-secondary-viewport-camera/overview.md)
- [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
