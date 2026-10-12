---
type: Game Feature
title: Play scripted cinematic camera tracks with blends and easing
description: KSA has no camera track system, so mods compose keyframed shots over the player's camera in game-free code and apply them from the viewport pass, blending in and out
category: camera
group: special-views
topic: play-cinematic-camera-tracks
demonstrated_by:
  - mods/gatos/features/programmable-camera-director
  - mods/gatos/features/synthetic-camera-follow-anchor
  - mods/ksagolf/features/borrowed-chase-camera
  - mods/unscience/features/camera-controller-override-keyframe-sequences
lifecycle: [frame:viewports, frame:draw-ui]
game_types: [KSA.FixedController, KSA.GameViewport, KSA.Camera, KSA.IFollowable, KSA.KittenEva, KSA.Program]
tags: [camera, cinematic, track, blend, easing]
---
A cinematic camera is a function from time to a pose, applied once per frame in phase with the engine. The game provides only the second half (a camera is two properties baked by `Camera.OnFrame`), so mods write the first half in code that does not reference the game and unit-test it, and keep a thin seam that applies the result and hands the
view back. The four approaches cover a full track compositor, a chase blend with paced eases, and two ways to leave a shot.

## Engine background

| Type | Role | Source |
|---|---|---|
| `GameViewport.OnFrame` | Controller then `Camera.OnFrame` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L116-L124) |
| `FixedController` | The two pose writes relative to a followed object | [current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L22-L47) |
| `Camera.PositionEcl`, `LocalRotation`, `SetFieldOfView` | What a shot writes | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L110-L131), [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L417-L421) |
| `OrbitController`, `FlyController` | Rewrite the pose every frame, so a shot over them needs a prefix | [current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L636-L655), [current/decomp/KSA/FlyController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlyController.cs#L734) |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1. Three-layer compositor with splines and easing](play-cinematic-camera-tracks-1.md) | gatOS | `controller-subclass` | `frame:viewports` | Complete and testable; IVA and Map unsupported |
| [2. Smoothstep eye, equal-depth look, averaged step](play-cinematic-camera-tracks-2.md) | KSAGolf | `camera-arbitration` | `frame:viewports`, `frame:draw-ui` | Pure functions; not wired into the game at the pinned ref |
| [3. Blend back to the start pose when a sequence ends](play-cinematic-camera-tracks-3.md) | unscience | `harmony-prefix` | `frame:viewports`, `frame:draw-ui` | Seamless over the player's own camera; Orbit/Free only |
| [4. Release blend and named easings](play-cinematic-camera-tracks-4.md) | gatOS | `controller-subclass` | `frame:viewports` | Eased hand-back of position, rotation and FOV; extra state to capture |

## See also

- [Animate the flight camera with eased moves, keyframe sequences and saved recipes](../chain-camera-moves-into-keyframe-sequences/overview.md), [Make the camera follow a point or mod-simulated object, and change the follow target without a jump](../../main-view/follow-a-point-that-is-not-a-vehicle/overview.md), [Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md)
- [Schedule timed command sequences on render, wall or sim time](../../../external-io/control/schedule-timed-commands/overview.md), [Borrow the main camera, hand it back and arbitrate between camera users](../../main-view/borrow-the-main-camera-and-hand-it-back/overview.md), [Chase a projectile or other fast body with the main camera](../../main-view/chase-a-fast-body-with-the-main-camera/overview.md)
- [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
