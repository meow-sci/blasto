---
type: Submod
title: Camera Controller Override
description: Cinematic camera animations and keyframe sequences that pre-empt the stock Orbit and Fly camera controllers while a sequence plays, with eased target-relative moves and saved recipes
mod: unscience
submod: camera-controller-override
tags: [camera, animation, keyframes, easing, harmony]
mod_id: camera-controller-override
---

Camera Controller Override (shown as "Camera Animations" in the toolbox) plays eased camera moves (zoom in
and out, zoom to offset, spirals, orbit, loopy orbit, shake, pan, rotate) and chains them into keyframe
sequences with parallel groups and an optional blend back to the starting pose. A shared Harmony prefix on
the stock `OrbitController.OnFrame` and `FlyController.OnFrame` hands the live camera to the sequence
player while it runs, so the stock controllers resume untouched afterwards.

## Hosting

- Manifest id `camera-controller-override`, entry assembly `MeowSci.CameraControllerOverride` (development
  host only).
- Standalone host: F11 toggles the window; its Patcher applies `CameraControllerOverridePatches` with the
  host's `KeyframeSequencePlayer`, and `HotkeyGuard`.
- In unscience: `Mod.cs` hands `cameraOverride.SequencePlayer` to `Patcher.CameraSequencePlayer` before
  `CameraControllerOverridePatches.Apply` runs on the consolidated instance.

## Features

| Feature | Category | Lifecycle | Summary |
|---|---|---|---|
| [Cinematic camera moves by pre-empting the Orbit and Fly controllers](../features/camera-controller-override-camera-animations.md) | camera | `frame:viewports`, `frame:draw-ui` | A shared Harmony prefix on OrbitController.OnFrame and FlyController.OnFrame hands the controller's live Camera to an animation player while a sequence plays, so ten eased, target-relative moves (zooms, spirals, orbits, loopy orbit, shake, pan, rotate) replace the stock camera math for that frame |
| [Keyframe camera sequences with parallel groups, return-to-start and saved recipes](../features/camera-controller-override-keyframe-sequences.md) | camera | `frame:viewports`, `frame:draw-ui`, `event:save`, `event:load` | KeyframeSequencePlayer chains camera animations into a played, paused or stopped sequence, composes simultaneous moves as one keyframe, blends back to the captured start pose, and saves the authored sequence as validated recipe JSON with the scene |

## Library classes

`CameraControllerOverrideSubmod` (configuration and UI, + `.Saves`), `CameraControllerOverridePatches`,
`Animation/KeyframeSequencePlayer`, `Animation/Keyframe`, `Animation/AnimationGroup`,
`Animation/IKeyframeAnimation`, `Animation/AnimationHelpers`, `Animation/SavedAnimation`, ten animation classes
under `Animation/Animations/`, `UI/KeyframeSequencePanel`; easing from `ksa-abstractions.lib/EasingHelper`.

## Scene saves and persistence

Record `camera-controller-override` (order 210, after the main camera exists): the authored keyframes, a
pending parallel group and the return-to-start settings, as `SavedAnimation` recipes. Sequences restore
stopped; playback state is not a checkpoint. Covered by `camera-saves.tests`.

## Further reading

- [camera-controller-override/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override/README.md)
- [camera-controller-override.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/README.md)
- [scope/camera.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/camera.md)
- [camera-saves.tests/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-saves.tests/README.md)

## Entry points

- [camera-controller-override/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override/mod.toml)
- [camera-controller-override/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override/Mod.cs)
- [camera-controller-override/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override/Patcher.cs)
- [camera-controller-override.lib/CameraControllerOverridePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/CameraControllerOverridePatches.cs)

## See also

- [unscience overview](../overview.md)
- [Glass](./glass.md) (field of view)
- [Hot Pursuit](./hot-pursuit.md) (secondary cameras)
