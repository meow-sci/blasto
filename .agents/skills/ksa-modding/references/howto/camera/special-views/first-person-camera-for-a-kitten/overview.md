---
type: Game Feature
title: Put the camera in an EVA kitten's eyes, with mouse look and look-driven walking
description: How a mod reads the head bone, borrows the main view in Fixed mode at that point, keeps a mouse look in a world frame and makes the walking kitten face where the player looks
category: camera
group: special-views
topic: first-person-camera-for-a-kitten
demonstrated_by:
  - mods/ksastructures/features/first-person-kitten-view
  - mods/ksagolf/features/first-person-kitten-view
  - mods/ksagolf/features/level-horizon-camera-controller
  - mods/ksastructures/features/level-horizon-camera-controller
lifecycle: [frame:draw-ui, frame:viewports, frame:render, frame:input, physics:vehicle-solver]
game_types:
  - KSA.KittenEva
  - KSA.KittenRenderable
  - KSA.CharacterAvatar
  - KSA.IAnimProcessor
  - KSA.GameViewport
  - KSA.FixedController
  - KSA.Controller
  - Brutal.GlfwApi.GlfwCursorMode
  - KSA.KittenLocomotion
  - KSA.CharacterControlInputs
  - KSA.LocomotionCommand
  - KSA.LocomotionState
  - KSA.PhysicsBubble
tags: [camera, first-person, kitten, eva, mouse-look, locomotion]
---
KSA has no first-person camera for an EVA kitten: the IVA camera needs a vehicle seat, and the orbit and free cameras are third person. A mod therefore assembles one from four independent pieces: where the head is (a skeleton bone), a camera there (the `Fixed` mode borrowed from the
player), a look (mouse yaw and pitch) and a walk that agrees with the look. Each piece touches a different part of the frame, so the lifecycle table matters.

## Engine background

| Type | Role | Source |
|---|---|---|
| `AnimatedRenderable.AnimProcessors`, `IAnimProcessor` | A processor sees the skeleton after the world transforms are rebuilt, once per view | [current/decomp/KSA/AnimatedRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AnimatedRenderable.cs#L150-L180), [current/decomp/KSA/IAnimProcessor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IAnimProcessor.cs) |
| `KittenRenderable` | Private `_characterAvatar`; `ModelToBodyMatrix` | [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L13), [current/decomp/KSA/KittenRenderable.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenRenderable.cs#L107-L110) |
| `KittenEva.PrepareWorker` | Copies the main camera basis into the kitten's control inputs | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L319-L330) |
| `KittenLocomotion.StepGrounded` | The grounded move and facing | [current/decomp/KSA/KittenLocomotion.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenLocomotion.cs#L340-L369) |
| `FixedController`, `GameViewport` | The borrowed view | [Drive the main camera through Fixed mode without crashing it, with a stable up vector](../../controllers/drive-the-main-camera-in-fixed-mode/overview.md) |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1. `EyesPose` reads `Head_M`; right-drag look; `StepGrounded` patches](first-person-camera-for-a-kitten-1.md) | KSAStructures | `anim-processor` | `frame:draw-ui`, `frame:viewports`, `frame:render`, `physics:vehicle-solver` | The reference build; V toggle, eye 9 cm ahead and 6 cm up |
| [2. Golf pose, captured-cursor look, pitch-tilted up, drawn turn](first-person-camera-for-a-kitten-2.md) | KSAGolf | `anim-processor` | same | Shooter-style look; a turn drawn on the skeleton for a standing kitten; more state |

## See also

- [Hide a kitten's body from the main view only, keeping its shadow](../../../kittens/appearance/hide-a-kitten-body-from-one-viewport/overview.md), [Borrow the main camera, hand it back and arbitrate between camera users](../../main-view/borrow-the-main-camera-and-hand-it-back/overview.md), [Pose a kitten's bones and aim its limbs with an IAnimProcessor](../../../kittens/animation/pose-kitten-bones-with-an-anim-processor/overview.md), [Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md)
- [Replace a viewport's camera controller and take input through it](../../controllers/replace-a-viewport-camera-controller/overview.md), [Write worker-thread-safe patches inside the vehicle solver](../../../physics/solver/write-worker-thread-safe-solver-patches/overview.md), [Make a walking kitten move and face along the look direction, and drive it with the mouse](../../../kittens/eva/steer-a-walking-kitten/overview.md), [Share the keyboard and mouse between ImGui, mod tools and the game](../../../ui/input/coordinate-imgui-focus-with-game-input/overview.md)
- [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
