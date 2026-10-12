---
type: How-To
title: Blend back to the start pose when a sequence ends
description: Camera Controller Override interpolates the offset and slerps the rotation from the final keyframe back to the captured start pose, re-adding the followed object's position each frame so the blend tracks a moving vessel
category: camera
group: special-views
topic: play-cinematic-camera-tracks
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/camera-controller-override-keyframe-sequences]
techniques: [harmony-prefix]
hooks: [harmony-prefix KSA.OrbitController.OnFrame, harmony-prefix KSA.FlyController.OnFrame]
lifecycle: [frame:viewports, frame:draw-ui]
game_types: [KSA.OrbitController, KSA.FlyController, KSA.Controller, KSA.Camera]
tags: [camera, cinematic, blend, return-to-start, easing, unscience]
---
Scripted moves are played on top of the player's own camera, so when the last move ends the stock controller takes over again and, if nothing blends, the camera jumps to the stock pose. Camera Controller Override avoids the jump by capturing the start pose and blending back to it. This article covers **blending in and out and easing the pacing**. The sequence engine is [Animate the flight camera with eased moves, keyframe sequences and saved recipes](../chain-camera-moves-into-keyframe-sequences/overview.md).

## Background: how the game does it

`OrbitController.OnFrame` rebuilds `LocalRotation` and `PositionCce` from the followed object's `OrbitView` every frame, and `FlyController.OnFrame` rewrites `LocalRotation` from its cached offset ([current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L636-L655), [current/decomp/KSA/FlyController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlyController.cs#L734)). A mod that stops writing therefore hands back the
stock pose on the next frame. The stock pose is the pose the camera had at the start of the sequence, because the sequence edits the `Camera` and never the controller's own state (the orbit view, or the fly controller's cached look), so blending to the captured start offset makes the hand-back seamless. A `FlyController` does not rewrite position, so what the blend leaves behind is also where a free camera stays.

## Lifecycle and timing

`frame:viewports`, inside the shared prefix on both controllers' `OnFrame`, immediately before `Camera.OnFrame` ([approach 4](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md)). Time is the controller's `inDeltaTime` (player time), so a blend keeps going while the simulation is paused or warped. The blend duration and ease are set from the ImGui panel in `frame:draw-ui`.

## Blend in and out and ease the pacing

1. **Capture the start pose on the first playing frame, as an offset from the followed object** so it survives the vessel moving:

```csharp
// camera-controller-override.lib/Animation/KeyframeSequencePlayer.cs L446-L454
// Capture start offset and rotation on first frame of playback
if (!_hasStartedSequence)
{
    double3 currentTargetPos = AnimationHelpers.GetTargetPosition(controller);
    _sequenceStartOffset = transform.PositionEcl - currentTargetPos;
    _sequenceStartRotation = transform.LocalRotation;
    _hasStartedSequence = true;
    Console.WriteLine($"[KeyframeSequencePlayer] Captured start offset: {_sequenceStartOffset}, rotation: {_sequenceStartRotation}");
}
```

2. **When the last keyframe ends, capture where the camera is and start the return** (on by default, 3 s, ease-in-out, duration clamped to 1-10 s):

```csharp
// camera-controller-override.lib/Animation/KeyframeSequencePlayer.cs L530-L545
    if (_returnToStartEnabled)
    {
        // Capture current offset and rotation, then start return-to-start
        double3 currentTargetPos = AnimationHelpers.GetTargetPosition(controller);
        _returnFromOffset = transform.PositionEcl - currentTargetPos;
        _returnFromRotation = transform.LocalRotation;
        _isReturningToStart = true;
        _returnElapsedTime = 0.0;
        Console.WriteLine($"[KeyframeSequencePlayer] Starting return to start from offset: {_returnFromOffset}, rotation: {_returnFromRotation}");
    }
    else
    {
        // No return-to-start, just stop
        Stop();
    }
}
```

3. **Each frame, re-add the current target position, lerp the offset and slerp the rotation** with the chosen easing; stop at the end:

```csharp
// camera-controller-override.lib/Animation/KeyframeSequencePlayer.cs L462-L487
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

    if (_returnElapsedTime >= _returnToStartDuration)
    {
        _isReturningToStart = false;
        State = PlaybackState.Stopped;
        Console.WriteLine("[KeyframeSequencePlayer] Return to start complete");
    }

    return true;
}
```

4. **Respect the lock.** The first controller to call `Update` after `Play()` drives the animation and every other Orbit/Free controller is skipped, so the blend advances once per frame, not once per controller ([camera-controller-override.lib/Animation/KeyframeSequencePlayer.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/Animation/KeyframeSequencePlayer.cs#L429-L549)).

## Complete example

- [camera-controller-override.lib/Animation/KeyframeSequencePlayer.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/Animation/KeyframeSequencePlayer.cs), [camera-controller-override.lib/Animation/AnimationHelpers.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/Animation/AnimationHelpers.cs), [ksa-abstractions.lib/EasingHelper.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/EasingHelper.cs)

## Variations and gotchas

- **With return-to-start off** the last frame is followed by the stock controller's own pose, a visible jump.
- **Switching the main camera mode mid-sequence** leaves the lock on the old controller, which no longer runs, so the sequence stalls until Stop.
- **The easing** is `Linear`, `EaseIn` (`t^p`), `EaseOut` (`1-(1-t)^p`) or `EaseInOut` with separate powers for the two halves; the game-side coupling is only the prefix.

## See also

- [Topic overview](overview.md); [approach 1](play-cinematic-camera-tracks-1.md), [2](play-cinematic-camera-tracks-2.md), [4](play-cinematic-camera-tracks-4.md)
- [Keyframe camera sequences with parallel groups, return-to-start and saved recipes](../../../../mods/unscience/features/camera-controller-override-keyframe-sequences.md)
- [Animate the flight camera with eased moves, keyframe sequences and saved recipes](../chain-camera-moves-into-keyframe-sequences/overview.md), [Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md)
