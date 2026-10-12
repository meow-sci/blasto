---
type: Mod Feature
title: Keyframe camera sequences with parallel groups, return-to-start and saved recipes
description: KeyframeSequencePlayer chains camera animations into a played, paused or stopped sequence, composes simultaneous moves as one keyframe, blends back to the captured start pose, and saves the authored sequence as validated recipe JSON with the scene
mod: unscience
submod: camera-controller-override
category: camera
techniques: [harmony-prefix, save-sidecar, json-config]
hooks:
  - harmony-prefix KSA.OrbitController.OnFrame (via CameraControllerOverridePatches)
  - harmony-prefix KSA.FlyController.OnFrame (via CameraControllerOverridePatches)
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent, sequence panel)
  - unscience ISaveParticipantSource (scene save record camera-controller-override)
lifecycle: [frame:viewports, frame:draw-ui, event:save, event:load]
game_types: [KSA.Controller, KSA.Transform3D, KSA.Camera, KSA.OrbitController, KSA.FlyController]
tags: [camera, animation, keyframes, sequencing, scene-saves, json]
---

The animation library in Camera Controller Override is driven by a small sequencer. Players build a
list of keyframes in the ImGui panel (each one is a configured animation with its own duration and
easing), optionally bundle several animations into a group that plays them at the same time, and press
Play. When the last keyframe ends the camera eases back to where it started. The authored sequence is
saved with the Unscience scene and restored stopped, ready to replay.[^cco-lib-readme]

Mechanism in one line: `KeyframeSequencePlayer.Update(controller, transform, dt)` is called from the
controller prefix described in [camera animations](./camera-controller-override-camera-animations.md);
it lazily initializes the current keyframe from the live camera pose, advances time, moves to the next
keyframe when `Update` reports completion, and finally runs a target-relative return blend.[^cco-player]

## How it works

Player state (`Stopped`, `Playing`, `Paused`) and keyframe list:

- `Keyframe` is a thin wrapper with an `Id` and an `IKeyframeAnimation`; `AddKeyframe`,
  `RemoveKeyframe`, `MoveKeyframe` and `Clear` renumber ids and adjust `CurrentKeyframeIndex` if
  playback is active.[^cco-keyframe]
- `Play()` resets every piece of state, including `Reset()` on every animation, so replaying always
  starts clean. `Pause()`/`Resume()` only flip the state; `Stop()` resets like `Play()` but leaves the
  state `Stopped`. `TotalDuration` is the sum of keyframe durations.

Per-frame `Update` (only while `Playing`):

1. Controller lock: the first `Controller` to call `Update` after `Play()` becomes `_activeController`;
   calls from any other controller return `true` (skip its stock update) without advancing time. The
   prefix is on both `OrbitController` and `FlyController` in every viewport, so this prevents the
   sequence from advancing twice per frame.
2. On the first frame, capture the start pose as an offset from the followed target
   (`PositionEcl - target`) plus `LocalRotation`.
3. Initialize the current keyframe on its first update, from wherever the previous keyframe left the
   camera. Each animation therefore composes naturally with the one before it.
4. Add `deltaTime` before calling the animation, so it sees end-of-frame time, then advance the index
   when it returns `true`.
5. After the last keyframe, if `ReturnToStartEnabled` (default on, 3 s, `EaseInOut`, duration clamped
   to 1-10 s): capture the current offset and rotation, then lerp the offset and slerp the rotation
   back to the captured start, re-adding the current target position each frame so the blend tracks a
   moving vessel. Otherwise `Stop()`.

`AnimationGroup` is itself an `IKeyframeAnimation`, so a group is one keyframe:[^cco-group]

- Each position-changing child runs on a virtual pose (its own offset and rotation, re-based on the
  current target every frame). The group sums each child's offset delta from the common base offset.
- The composed position is applied, rotation is reset to the base and re-aimed at the target with
  look-at.
- Rotation-only children (`RotateAnimation`, `ShakeAnimation`) are recomputed from elapsed time and
  applied on top of the look-at rotation using the current view axes.
- Group duration is the longest child; finished children keep contributing their final offset.

Building groups in the UI: "Start Building Group" redirects the panel's add buttons into a pending
list; "Finish Group" adds the group as one keyframe.[^cco-submod]

Scene persistence (record `camera-controller-override`, restore order 210):[^cco-submod-saves][^save-participant]

- `SavedAnimation` is an explicit recipe: `Kind` string (`zoom-in`, `zoom-out`, `zoom-offset`,
  `spiral-in`, `spiral-out`, `orbit`, `loopy`, `shake`, `pan`, `rotate`, `group`), duration, easing,
  the two easing powers, a `double[] Parameters` and `Children` for groups.[^cco-saved-animation]
- `Capture` maps each concrete class with a C# type-pattern `switch`; it refuses custom
  `LookAtTargetProvider` delegates, nesting deeper than 8, and unknown animation types (a capture
  warning instead of invalid JSON).
- `Create` validates before constructing: finite numbers, non-negative duration, defined easing,
  positive powers, at most 128 children, an exact parameter count per kind, a non-zero loop interval,
  and an integer shake count from 1 to 100000.
- The participant saves the keyframe list, the pending (unfinished) group and the return-to-start
  settings; `validate` builds every recipe before anything is touched; reset clears the player;
  restore re-adds the keyframes. Playback position, captured transforms and delegates are not saved, so
  a loaded sequence starts `Stopped`.

`camera-saves.tests` checks the recipe mapper without loading KSA: the project compiles the
production `SavedAnimation.cs` and the shared JSON helpers against small fixture classes that match the
ten animation constructors, then round-trips every kind, nested groups and an unknown kind (12
checks).[^camera-saves-tests][^camera-saves-csproj][^camera-saves-fixtures]

Programmatic use: `CameraControllerOverrideSubmod.Instance.SequencePlayer` exposes the same player, so
other code can `AddKeyframe(...)` and `Play()`.

## Lifecycle and timing

| Code | When it runs | Notes |
|---|---|---|
| `KeyframeSequencePlayer.Update` | `frame:viewports`, inside the Orbit/Fly controller prefix, before `Camera.OnFrame` bakes matrices | Main thread; reads the followed object's applied position, writes only the camera |
| Panel buttons (`Play`, `Pause`, `Resume`, `Stop`, add, reorder) | `frame:draw-ui` (`StarMapAfterGui` via `RenderContent`) | After this frame's viewports; changes take effect next frame[^cco-panel] |
| Save capture / reset / restore | `event:save` / `event:load` through the Unscience scene coordinator | Restore runs after native reconstruction, in order 210; the native camera pose is already loaded |

Because time is accumulated from the controller's `inDeltaTime` (player time), a sequence keeps playing
while the simulation is paused or warped.

## Key code

| Path | Role |
|---|---|
| [camera-controller-override.lib/Animation/KeyframeSequencePlayer.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/Animation/KeyframeSequencePlayer.cs#L429-L549) | Per-frame sequencing, controller lock, return-to-start |
| [camera-controller-override.lib/Animation/Keyframe.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/Animation/Keyframe.cs) | Keyframe wrapper |
| [camera-controller-override.lib/Animation/AnimationGroup.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/Animation/AnimationGroup.cs#L74-L203) | Parallel composition |
| [camera-controller-override.lib/Animation/SavedAnimation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/Animation/SavedAnimation.cs) | Recipe capture and validated re-creation |
| [camera-controller-override.lib/CameraControllerOverrideSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/CameraControllerOverrideSubmod.Saves.cs#L23-L52) | Scene save participant |
| [camera-controller-override.lib/UI/KeyframeSequencePanel.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/UI/KeyframeSequencePanel.cs#L115-L150) | Playback buttons |
| [camera-saves.tests/Program.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-saves.tests/Program.cs) | Recipe round-trip checks |

## Snippets

Controller lock, start capture and the return blend:

```csharp
// camera-controller-override.lib/Animation/KeyframeSequencePlayer.cs L441-L487
        if (_activeController == null)
            _activeController = controller;
        else if (controller != _activeController)
            return true; // Still skip normal camera control, but don't run animation logic
        
        // Capture start offset and rotation on first frame of playback
        if (!_hasStartedSequence)
        {
            double3 currentTargetPos = AnimationHelpers.GetTargetPosition(controller);
            _sequenceStartOffset = transform.PositionEcl - currentTargetPos;
            _sequenceStartRotation = transform.LocalRotation;
            _hasStartedSequence = true;
// ...
        }
// ...
        if (_isReturningToStart)
        {
            // Accumulate return time BEFORE using it
            _returnElapsedTime += deltaTime;
            
            double3 currentTargetPos = AnimationHelpers.GetTargetPosition(controller);
            double t = _returnElapsedTime / _returnToStartDuration;
            double easedT = AnimationHelpers.ApplyEasing(t, _returnToStartEasing, _returnToStartEasingPowerStart, _returnToStartEasingPowerEnd);
            
            // Lerp position offset
            double3 currentOffset = double3.Lerp(_returnFromOffset, _sequenceStartOffset, easedT);
            transform.PositionEcl = currentTargetPos + currentOffset;
            
            // Slerp rotation (quaternion interpolation)
            doubleQuat currentRotation = doubleQuat.Slerp(_returnFromRotation, _sequenceStartRotation, easedT);
            transform.LocalRotation = currentRotation;
// ...
            return true;
        }
```

Group composition: virtual per-child poses, summed offsets, look-at, then rotation layers:

```csharp
// camera-controller-override.lib/Animation/AnimationGroup.cs L82-L117
        foreach (var entry in _entries)
        {
            if (IsRotationOnly(entry.Animation))
                continue;
// ...
            // Set virtual transform relative to current target
            transform.PositionEcl = currentTargetPos + entry.VirtualOffset;
            transform.LocalRotation = entry.VirtualRotation;

            bool complete = entry.Animation.Update(controller, transform, deltaTime, elapsedTime);

            // Extract offset from current target
            entry.VirtualOffset = transform.PositionEcl - currentTargetPos;
            entry.VirtualRotation = transform.LocalRotation;
// ...
            totalOffsetDelta += entry.VirtualOffset - _baseOffset;
        }

        // Phase 2: Apply composed position (target-relative)
        transform.PositionEcl = currentTargetPos + _baseOffset + totalOffsetDelta;

        // Phase 3: Compute LookAt rotation from composed position
        transform.LocalRotation = _baseRotation;
        AnimationHelpers.LookAtTarget(transform, currentTargetPos);

        // Phase 4: Apply rotation animation contributions on top of LookAt
        ApplyRotationContributions(transform, elapsedTime);
```

Recipe capture by type pattern, refusing what cannot round-trip:

```csharp
// camera-controller-override.lib/Animation/SavedAnimation.cs L19-L42
    public static SavedAnimation Capture(IKeyframeAnimation animation, int depth = 0)
    {
        if (depth > 8) throw new InvalidOperationException("Camera groups exceed supported nesting depth.");
        if (animation.LookAtTargetProvider != null) throw new InvalidOperationException("Custom camera target delegates cannot be saved.");
        var data = new SavedAnimation { Duration = animation.DurationSeconds, Easing = animation.Easing,
            PowerStart = animation.EasingPowerStart, PowerEnd = animation.EasingPowerEnd };
        (data.Kind, data.Parameters) = animation switch
        {
            ZoomInAnimation a => ("zoom-in", new[] { a.SpeedMetersPerSecond }),
            ZoomOutAnimation a => ("zoom-out", new[] { a.SpeedMetersPerSecond }),
// ...
            AnimationGroup => ("group", Array.Empty<double>()),
            _ => throw new InvalidOperationException($"Unsupported camera animation {animation.GetType().Name}.")
        };
        if (animation is AnimationGroup group)
            data.Children = Enumerable.Range(0, group.Count).Select(i => Capture(group.GetAnimation(i), depth + 1)).ToArray();
        return data;
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Transform3D.PositionEcl`, `Transform3D.LocalRotation` | [current/decomp/KSA/Transform3D.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Transform3D.cs#L13-L15) | Start capture, return blend, group virtual poses |
| `Camera.Following` (`IFollowable.GetPositionEcl()`) | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L158) | Target-relative offsets |
| `Camera.LookAtRotation` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L203) | Group re-aim after composition |
| `OrbitController.OnFrame` (stock pose rebuild) | [current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L629-L651) | Why return-to-start matters: the orbit camera rewrites the pose as soon as playback ends |

## Pitfalls and version notes

- With return-to-start off, the last frame of the sequence is followed by the stock controller's own
  pose (orbit) or cached orientation (fly), which is a visible jump. The default return blend targets
  the captured start offset, which is where the stock orbit camera was, so the hand-back is
  seamless.[^ksa-orbitcontroller]
- The controller lock means other viewports in Orbit or Free mode are frozen during playback, and the
  sequence follows whichever viewport's controller ran first (in practice the main viewport, which is
  registered first). Switching the main camera mode mid-sequence leaves the lock on the old
  controller, which no longer runs, so the sequence stalls until Stop.
- Shake inside a group is recomputed from elapsed time and stops contributing when its own duration
  ends; standalone Shake integrates yaw deltas and ends at its last yaw offset.
- Saved recipes depend on constructor parameter order (`SavedAnimation.Create`); the test fixtures
  must change together with any constructor change, as the test README states.
- Custom animation classes or look-at delegates added through the API cannot be saved; capture throws
  and the coordinator reports a warning.
- No engine member is reached by reflection here; all game coupling goes through the controller prefix
  documented in [camera animations](./camera-controller-override-camera-animations.md).

## See also

- [Camera animations and the controller prefix](./camera-controller-override-camera-animations.md)
- [Scene save coordinator](./lib-scene-save-coordinator.md)
- [Scene save sidecar](./unscience-scene-save-sidecar.md)
- [Managed test harnesses](./unscience-managed-test-harnesses.md)
- [Sub-mod: Camera Controller Override](../submods/camera-controller-override.md)
- [Mod overview](../overview.md)
- [Queue animations into a KeyframeSequencePlayer with groups and return-to-start, and save the recipes with the scene](../../../howto/camera/special-views/chain-camera-moves-into-keyframe-sequences/chain-camera-moves-into-keyframe-sequences-1.md) - how-to article demonstrating this feature
- [Blend back to the start pose when a sequence ends](../../../howto/camera/special-views/play-cinematic-camera-tracks/play-cinematic-camera-tracks-3.md) - how-to article demonstrating this feature
- [Persist feature state as ISaveParticipant records through a coordinator that retains what it cannot restore](../../../howto/persistence/saves/save-mod-state-with-the-scene/save-mod-state-with-the-scene-1.md) - how-to article demonstrating this feature

[^cco-lib-readme]: camera-controller-override.lib/README.md
[^cco-player]: camera-controller-override.lib/Animation/KeyframeSequencePlayer.cs
[^cco-keyframe]: camera-controller-override.lib/Animation/Keyframe.cs
[^cco-group]: camera-controller-override.lib/Animation/AnimationGroup.cs
[^cco-submod]: camera-controller-override.lib/CameraControllerOverrideSubmod.cs
[^cco-submod-saves]: camera-controller-override.lib/CameraControllerOverrideSubmod.Saves.cs
[^save-participant]: ksa-abstractions.lib/Persistence/ISaveParticipant.cs
[^cco-saved-animation]: camera-controller-override.lib/Animation/SavedAnimation.cs
[^camera-saves-tests]: camera-saves.tests/Program.cs
[^camera-saves-csproj]: camera-saves.tests/camera-saves.tests.csproj
[^camera-saves-fixtures]: camera-saves.tests/AnimationFixtures.cs
[^cco-panel]: camera-controller-override.lib/UI/KeyframeSequencePanel.cs
[^ksa-orbitcontroller]: KSA.OrbitController (decompiled, build 2026.10.10.5554)
