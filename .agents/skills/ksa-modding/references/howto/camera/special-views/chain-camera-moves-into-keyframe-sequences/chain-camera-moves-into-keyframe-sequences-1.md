---
type: How-To
title: Queue animations into a KeyframeSequencePlayer with groups and return-to-start, and save the recipes with the scene
description: Camera Controller Override plays a list of keyframes through one prefix on the Orbit and Free controllers, runs simultaneous moves as an AnimationGroup, blends back to the start pose and stores the authored sequence as validated recipe JSON restored stopped on load
category: camera
group: special-views
topic: chain-camera-moves-into-keyframe-sequences
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/camera-controller-override-keyframe-sequences]
techniques: [harmony-prefix, save-sidecar]
hooks: [harmony-prefix KSA.OrbitController.OnFrame, harmony-prefix KSA.FlyController.OnFrame]
lifecycle: [frame:viewports, frame:draw-ui, event:save, event:load]
game_types: [KSA.Controller, KSA.Camera, KSA.Transform3D, KSA.OrbitController, KSA.FlyController, KSA.IFollowable]
tags: [camera, animation, keyframes, sequence, group, save, recipes, unscience]
---
Camera Controller Override's "Camera Animations" panel lets a player build a list of moves (zoom, spiral, orbit, shake, pan, rotate), bundle several into a **group** that plays at once, press Play and watch the flight camera perform them, then ease back to where it started. The authored sequence
survives a save. The sequencer is small and the game coupling is one prefix ([approach 4](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md)); the interesting parts are the player, the group and the persistence. Prerequisites: StarMap and Harmony. The individual moves are
[approach 2](chain-camera-moves-into-keyframe-sequences-2.md).

## Background: how the game does it

The player's Orbit or Free camera is rewritten by its controller every frame ([current/decomp/KSA/OrbitController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitController.cs#L636-L655), [current/decomp/KSA/FlyController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlyController.cs#L734)), so a sequence has to take the frame over from inside the controller's `OnFrame`; the shared prefix hands each controller's public `Camera` to the player. The game offers nothing for
persistence of mod state; the scene save is provided by the unscience host through `ISaveParticipantSource` ([ksa-abstractions.lib/Persistence/ISaveParticipant.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/Persistence/ISaveParticipant.cs)), and the engine's own camera save stores only the pose and follow ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L817-L849)).

## Lifecycle and timing

| Code | Runs | Value |
|---|---|---|
| `KeyframeSequencePlayer.Update` | In the Orbit/Free `OnFrame` prefix, before `Camera.OnFrame` bakes the matrices | `frame:viewports` |
| Play, Pause, Stop, add, reorder | ImGui panel buttons | `frame:draw-ui` (effect next frame) |
| Capture, validate, reset, restore | Scene coordinator, record `camera-controller-override`, order 210 | `event:save`, `event:load` |

Time accumulates from the controller's `inDeltaTime` (player time), so a sequence keeps playing while the simulation is paused or warped ([game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence)).

## Steps

1. **Wrap an animation in a keyframe and keep a list.** Add, remove and move renumber ids and adjust the current index if playback is active ([camera-controller-override.lib/Animation/Keyframe.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/Animation/Keyframe.cs)).

2. **Update lazily, one keyframe at a time.** The first controller after `Play()` becomes the active one; the start pose is captured as an offset from the followed object; each keyframe is initialised from wherever the previous one left the camera, so moves compose; time is added before `Update`
   so an animation sees end-of-frame time:

```csharp
// camera-controller-override.lib/Animation/KeyframeSequencePlayer.cs L441-L455, L495-L509
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
    Console.WriteLine($"[KeyframeSequencePlayer] Captured start offset: {_sequenceStartOffset}, rotation: {_sequenceStartRotation}");
}

// ...

// Get current keyframe
var keyframe = Keyframes[CurrentKeyframeIndex];

// Initialize keyframe animation on first update
if (!_currentKeyframeInitialized)
{
    Console.WriteLine($"[KeyframeSequencePlayer] Initializing keyframe {CurrentKeyframeIndex + 1}: camera pos = {transform.PositionEcl}");
    keyframe.Animation.Initialize(controller, transform);
    _currentKeyframeInitialized = true;
    CurrentKeyframeElapsedTime = 0.0;
    Console.WriteLine($"[KeyframeSequencePlayer] Started keyframe {CurrentKeyframeIndex + 1}: {keyframe.Animation.Name}");
}

// Accumulate time BEFORE Update so animations know time at END of frame
```

3. **Run simultaneous moves as one keyframe.** `AnimationGroup` is itself an `IKeyframeAnimation`: each position-changing child runs on a virtual pose, the group sums each child's offset delta from the common base, applies the position, resets rotation to the base and re-aims with look-at; rotation-only children (rotate, shake)
   are recomputed from elapsed time and layered on top; the group lasts as long as its longest child and finished children keep contributing their final offset ([camera-controller-override.lib/Animation/AnimationGroup.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/Animation/AnimationGroup.cs#L25-L120)). "Start Building Group" redirects the panel's add buttons into a pending list ([camera-controller-override.lib/CameraControllerOverrideSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/CameraControllerOverrideSubmod.cs)).

4. **Return to the start** when the last keyframe ends (default on, 3 s, ease-in-out): see [approach 3](../play-cinematic-camera-tracks/overview.md) ([article](../play-cinematic-camera-tracks/play-cinematic-camera-tracks-3.md)).

## Save sequences with the scene

5. **Store recipes, not objects.** `SavedAnimation` is an explicit version-one recipe (kind string, duration, easing and powers, parameter array, children) that excludes delegates and captured transforms. `Capture` maps each concrete class with a type-pattern switch and refuses custom look-at delegates, nesting beyond 8, and unknown types:

```csharp
// camera-controller-override.lib/Animation/SavedAnimation.cs L19-L43
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
        ZoomInToOffsetAnimation a => ("zoom-offset", new[] { a.SpeedMetersPerSecond, a.OffsetX, a.OffsetY, a.OffsetZ }),
        SpiralZoomInAnimation a => ("spiral-in", new[] { a.SpeedMetersPerSecond, a.SpiralDegrees }),
        SpiralZoomOutAnimation a => ("spiral-out", new[] { a.SpeedMetersPerSecond, a.SpiralDegrees }),
        OrbitAnimation a => ("orbit", new[] { a.Degrees }),
        LoopyOrbitAnimation a => ("loopy", new[] { a.Degrees, a.LoopIntervalDegrees, a.AmplitudeMeters }),
        ShakeAnimation a => ("shake", new[] { (double)a.ShakeCount, a.AmplitudeDegrees, a.ShakeSpeed }),
        PanAnimation a => ("pan", new[] { a.OffsetX, a.OffsetY, a.OffsetZ }),
        RotateAnimation a => ("rotate", new[] { a.YawDegrees, a.PitchDegrees }),
        AnimationGroup => ("group", Array.Empty<double>()),
        _ => throw new InvalidOperationException($"Unsupported camera animation {animation.GetType().Name}.")
    };
    if (animation is AnimationGroup group)
        data.Children = Enumerable.Range(0, group.Count).Select(i => Capture(group.GetAnimation(i), depth + 1)).ToArray();
    return data;
}
```

6. **Validate before constructing.** `Create` checks finite numbers, a non-negative duration, a defined easing, positive powers, at most 128 children and an exact parameter count per kind:

```csharp
// camera-controller-override.lib/Animation/SavedAnimation.cs L45-L72
public IKeyframeAnimation Create(int depth = 0)
{
    if (depth > 8 || Parameters == null || Children == null || Children.Length > 128 || Parameters.Any(p => !double.IsFinite(p))
        || !double.IsFinite(Duration) || Duration < 0 || !Enum.IsDefined(Easing)
        || !double.IsFinite(PowerStart) || !double.IsFinite(PowerEnd) || PowerStart <= 0 || PowerEnd <= 0)
        throw new InvalidOperationException("Invalid camera animation recipe.");
    var p = Parameters;
    if (Kind == "group")
    {
        var group = new AnimationGroup();
        foreach (var child in Children) group.Add(child.Create(depth + 1));
        return group;
    }
    return (Kind, p.Length) switch
    {
        ("zoom-in", 1) => new ZoomInAnimation(p[0], Duration, Easing, PowerStart, PowerEnd),
        ("zoom-out", 1) => new ZoomOutAnimation(p[0], Duration, Easing, PowerStart, PowerEnd),
        ("zoom-offset", 4) => new ZoomInToOffsetAnimation(p[0], Duration, Easing, p[1], p[2], p[3], PowerStart, PowerEnd),
        ("spiral-in", 2) => new SpiralZoomInAnimation(p[0], Duration, Easing, p[1], PowerStart, PowerEnd),
        ("spiral-out", 2) => new SpiralZoomOutAnimation(p[0], Duration, Easing, p[1], PowerStart, PowerEnd),
        ("orbit", 1) => new OrbitAnimation(p[0], Duration, Easing, PowerStart, PowerEnd),
        ("loopy", 3) when p[1] != 0 => new LoopyOrbitAnimation(p[0], p[1], p[2], Duration, Easing, PowerStart, PowerEnd),
        ("shake", 3) when p[0] >= 1 && p[0] <= 100000 && p[0] == Math.Truncate(p[0]) => new ShakeAnimation(Duration, (int)p[0], p[1], p[2], Easing, PowerStart, PowerEnd),
        ("pan", 3) => new PanAnimation(p[0], p[1], p[2], Duration, Easing, PowerStart, PowerEnd),
        ("rotate", 2) => new RotateAnimation(p[0], p[1], Duration, Easing, PowerStart, PowerEnd),
        _ => throw new InvalidOperationException($"Invalid or unsupported camera recipe '{Kind}'.")
    };
}
```

7. **Register a save participant** that captures the keyframe list, the pending group and the return-to-start settings, validates every recipe before anything is touched, resets the player, and restores the keyframes stopped:

```csharp
// camera-controller-override.lib/CameraControllerOverrideSubmod.Saves.cs L23-L52
public IEnumerable<ISaveParticipant> SaveParticipants => new[]
{
    new SaveParticipant<SavedCameraSequence>("camera-controller-override", () => new()
    {
        Keyframes = _sequencePlayer.Keyframes.Select(k => SavedAnimation.Capture(k.Animation)).ToArray(),
        PendingGroup = _pendingGroupAnimations.Select(a => SavedAnimation.Capture(a)).ToArray(),
        ReturnToStart = _sequencePlayer.ReturnToStartEnabled, ReturnDuration = _sequencePlayer.ReturnToStartDuration,
        ReturnEasing = _sequencePlayer.ReturnToStartEasing, PowerStart = _sequencePlayer.ReturnToStartEasingPowerStart,
        PowerEnd = _sequencePlayer.ReturnToStartEasingPowerEnd
    }, () => { _sequencePlayer.Clear(); _pendingGroupAnimations.Clear(); _groupMode = false; },
    (state, _) =>
    {
        foreach (var recipe in state.Keyframes) _sequencePlayer.AddKeyframe(recipe.Create());
        foreach (var recipe in state.PendingGroup) _pendingGroupAnimations.Add(recipe.Create());
        _groupMode = state.PendingGroup.Length > 0;
        _sequencePlayer.ReturnToStartEnabled = state.ReturnToStart;
        _sequencePlayer.ReturnToStartDuration = state.ReturnDuration;
        _sequencePlayer.ReturnToStartEasing = state.ReturnEasing;
        _sequencePlayer.ReturnToStartEasingPowerStart = state.PowerStart;
        _sequencePlayer.ReturnToStartEasingPowerEnd = state.PowerEnd;
    }, order: 210, validate: state =>
    {
        if (state.Keyframes == null || state.PendingGroup == null || state.Keyframes.Length > 1024 || state.PendingGroup.Length > 128
            || !double.IsFinite(state.ReturnDuration) || state.ReturnDuration < 1 || state.ReturnDuration > 10
            || !Enum.IsDefined(state.ReturnEasing) || !double.IsFinite(state.PowerStart) || state.PowerStart <= 0
            || !double.IsFinite(state.PowerEnd) || state.PowerEnd <= 0)
            throw new InvalidOperationException("Invalid camera sequence settings.");
        foreach (var recipe in state.Keyframes.Concat(state.PendingGroup)) _ = recipe.Create();
    })
};
```

   Playback position, captured transforms and delegates are not saved, so a loaded sequence starts `Stopped`.

8. **Test the mapper without the game.** `camera-saves.tests` compiles the production `SavedAnimation.cs` against fixture classes matching the ten animation constructors and round-trips every kind, nested groups and an unknown kind (12 checks) ([camera-saves.tests/Program.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-saves.tests/Program.cs)).

## Complete example

- [camera-controller-override.lib/Animation/KeyframeSequencePlayer.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/Animation/KeyframeSequencePlayer.cs), [camera-controller-override.lib/Animation/AnimationGroup.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/Animation/AnimationGroup.cs), [camera-controller-override.lib/Animation/SavedAnimation.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/Animation/SavedAnimation.cs)
- [camera-controller-override.lib/CameraControllerOverrideSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/CameraControllerOverrideSubmod.Saves.cs), [camera-controller-override.lib/UI/KeyframeSequencePanel.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/camera-controller-override.lib/UI/KeyframeSequencePanel.cs)

## Variations and gotchas

- **Constructor order is part of the format.** Saved recipes depend on constructor parameter order; the test fixtures must change together with any constructor change.
- **Custom animation classes or look-at delegates cannot be saved**; capture throws and the coordinator reports a warning.
- **The controller lock** freezes other Orbit/Free viewports during playback.
- **Programmatic use.** `CameraControllerOverrideSubmod.Instance.SequencePlayer` exposes the same player to other code.
- **No engine member is reached by reflection** here; all game coupling is the controller prefix.

## See also

- [Topic overview](overview.md); [approach 2](chain-camera-moves-into-keyframe-sequences-2.md)
- [Keyframe camera sequences with parallel groups, return-to-start and saved recipes](../../../../mods/unscience/features/camera-controller-override-keyframe-sequences.md)
- [Play scripted cinematic camera tracks with blends and easing](../play-cinematic-camera-tracks/overview.md), [Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md), [Save a whole mod's scene state as versioned sidecar records, with reset and replay on load](../../../persistence/saves/save-mod-state-with-the-scene/overview.md)
