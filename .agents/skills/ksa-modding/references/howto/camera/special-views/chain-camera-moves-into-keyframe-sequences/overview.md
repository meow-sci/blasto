---
type: Game Feature
title: Animate the flight camera with eased moves, keyframe sequences and saved recipes
description: Small animation classes that edit the live Orbit or Free camera each frame, queued into a sequence that plays, pauses, groups moves, blends back to the start and is stored as validated recipe JSON
category: camera
group: special-views
topic: chain-camera-moves-into-keyframe-sequences
demonstrated_by:
  - mods/unscience/features/camera-controller-override-keyframe-sequences
  - mods/unscience/features/camera-controller-override-camera-animations
lifecycle: [frame:viewports, frame:draw-ui, event:save, event:load]
game_types: [KSA.Controller, KSA.Camera, KSA.Transform3D, KSA.OrbitController, KSA.FlyController, KSA.IFollowable]
tags: [camera, animation, keyframes, easing, sequence, save]
---
Where [Play scripted cinematic camera tracks with blends and easing](../play-cinematic-camera-tracks/overview.md) composes whole shots, this topic is the lighter end: moves that act on top of the player's own camera. A move is an object with `Initialize`, `Update(elapsed)` and `Reset`; a sequence is a list of them; a group runs several at once; and a recipe is the saved, validated description of one. The whole thing hangs on one Harmony prefix
on the Orbit and Free controllers.

## Engine background

| Type | Role | Source |
|---|---|---|
| `OrbitController.OnFrame`, `FlyController.OnFrame` | Rewrite the pose every frame; the prefix point | [current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L487-L500), [current/decomp/KSA/FlyController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlyController.cs#L653-L660) |
| `Controller.Camera` | The camera the controller writes | [current/decomp/KSA/Controller.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Controller.cs#L12) |
| `Camera.LookAtRotation`, `Following` | Orientation and target | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L203-L209), [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L158) |
| `Camera.SerializeSave`, `DeserializeSave` | The engine's own camera save: pose, follow, mode | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L817-L849) |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1. `KeyframeSequencePlayer`, groups, return-to-start, saved recipes](chain-camera-moves-into-keyframe-sequences-1.md) | unscience | `harmony-prefix` | `frame:viewports`, `event:save`, `event:load` | Complete authoring and persistence; Orbit/Free only; delegates cannot be saved |
| [2. `IKeyframeAnimation` moves driven from the prefix](chain-camera-moves-into-keyframe-sequences-2.md) | unscience | `harmony-prefix` | `frame:viewports`, `frame:draw-ui` | Easy to add a move; each move owns its start state and drift handling |

## See also

- [Play scripted cinematic camera tracks with blends and easing](../play-cinematic-camera-tracks/overview.md), [Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md), [Save a whole mod's scene state as versioned sidecar records, with reset and replay on load](../../../persistence/saves/save-mod-state-with-the-scene/overview.md)
- [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
