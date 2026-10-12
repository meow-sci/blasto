---
type: How-To
title: Release with a blend and shape tracks with named easing functions
description: gatOS hands the camera back over camera_release_blend_s with an in-out ease that lerps position, slerps rotation and lerps the field of view to the captured baseline, and shapes tracks with linear, in, out, in-out and Bezier easings
category: camera
group: special-views
topic: play-cinematic-camera-tracks
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony, reflection]
demonstrated_by: [mods/gatos/features/programmable-camera-director]
techniques: [controller-subclass]
hooks: [harmony-prefix KSA.GameViewport.OnFrame, subclass KSA.FixedController.OnFrame]
lifecycle: [frame:viewports]
game_types: [KSA.FixedController, KSA.GameViewport, KSA.Camera]
tags: [camera, cinematic, release-blend, easing, gatos]
---
The director's hand-back is not a cut. On release it blends position, rotation and field of view from where the shot ended to the captured baseline over `camera_release_blend_s`, and only then restores the mode, follow and the rest. Tracks themselves are shaped with a small set of named easing
functions. This article covers **blending in and out and easing the pacing** for gatOS; the take and restore are in [approach 4 of the borrowing topic](../../main-view/borrow-the-main-camera-and-hand-it-back/overview.md).

## Background: how the game does it

Nothing in the engine blends a mod camera: the controller's `OnFrame` writes the pose and `Camera.OnFrame` bakes it ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L116-L124)). `Camera.SetFieldOfView` takes degrees and does not clamp ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L417-L421)), so a blend that interpolates the field must stay inside the safe range.

## Lifecycle and timing

`frame:viewports`: the blend step runs from the controller's pose callback, so it is in phase with the matrices. When the eased progress reaches 1 the director flags the blend complete, and the real restore (mode, follow, controller fields) runs from the prefix on the next frame, where those changes are safe.

## Blend in and out and ease the pacing

1. **Step the release inside the pose callback.** Accumulate `dt`, ease the progress in-out, lerp position, slerp rotation, lerp the field of view, and flag completion:

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs L941-L968
private bool StepRelease(KsaCamera camera, double3 followedEcl, double dt,
    out double3 offsetFromFollowed, out doubleQuat rotationEcl)
{
    _blendElapsed += double.IsFinite(dt) && dt > 0 ? dt : 0;
    var progress = _blendDuration > 0 ? Math.Clamp(_blendElapsed / _blendDuration, 0, 1) : 1;
    var eased = Easing.Apply(progress, EaseSpec.Named(EaseKind.InOut));

    var targetPosition = RestorePositionEcl();
    var position = Vec3.Lerp(ToVec(_blendStartPosition), ToVec(targetPosition), eased);
    var rotation = Splines.Slerp(ToQuat(_blendStartRotation), ToQuat(_restoreLocalRotation), eased);
    var fov = _blendStartFovDeg + (_restoreFovDeg - _blendStartFovDeg) * eased;

    _lastPositionEcl = ToKsa(position);
    _lastRotation = ToKsaQuat(rotation);
    _lastResolvedPositionEcl = _lastPositionEcl;
    if (double.IsFinite(fov) && fov != _appliedFovDeg)
    {
        camera.SetFieldOfView((float)fov);
        _appliedFovDeg = fov;
    }

    offsetFromFollowed = _lastPositionEcl - followedEcl;
    rotationEcl = _lastRotation;

    if (progress >= 1)
        _blendComplete = true;
    return true;
}
```

2. **Do the restore afterwards, from the prefix**, once the blend has completed ([gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs#L562-L568)).

3. **Name the easings.** The track format spells them `linear`, `in`, `out`, `in-out` plus an explicit cubic Bezier; `in` is `t^p`, `out` is `1-(1-t)^p`, and `in-out` joins two halves (the unscience formula, only C0 at 0.5 when the powers differ, which is what "leave fast, arrive slowly" means):

```csharp
// gatOS.SimFs/Camera/Easing.cs L222-L253
public static double Apply(double t, in EaseSpec spec)
{
    // Endpoint snap + clamp, before anything that could round. See the remarks.
    if (double.IsNaN(t)) return 0.0;
    if (t <= 0.0) return 0.0;
    if (t >= 1.0) return 1.0;

    switch (spec.Kind)
    {
        case EaseKind.In:
        {
            if (!double.IsFinite(spec.PowerStart)) return t;
            return Math.Pow(t, Clamp(spec.PowerStart));
        }

        case EaseKind.Out:
        {
            if (!double.IsFinite(spec.PowerEnd)) return t;
            return 1.0 - Math.Pow(1.0 - t, Clamp(spec.PowerEnd));
        }

        case EaseKind.InOut:
        {
            if (!double.IsFinite(spec.PowerStart) || !double.IsFinite(spec.PowerEnd)) return t;
            // The unscience formula, unchanged. NOTE: when PowerStart != PowerEnd this is only
            // C⁰ at t = 0.5 — the two halves meet at 0.5 but their slopes do not. That is
            // intentional (it is what "leave fast, arrive slowly" means); if a shot needs C¹
            // there, author a Bézier instead.
            return t < 0.5
                ? Math.Pow(2.0 * t, Clamp(spec.PowerStart)) / 2.0
                : 1.0 - Math.Pow(2.0 * (1.0 - t), Clamp(spec.PowerEnd)) / 2.0;
        }
```

4. **Resolve a segment's ease from its start key**, then its end key, then the track default, then linear, so the evaluator never looks sideways ([gatOS.SimFs/Camera/TrackParser.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Camera/TrackParser.cs#L470-L480)).

## Complete example

- [gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs#L941-L968), [gatOS.SimFs/Camera/Easing.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Camera/Easing.cs)

## Variations and gotchas

- **A release can be re-taken mid-blend**: `Take` during `Releasing` returns to `Owned` and resets smoothing.
- **A zero blend duration** completes on the first step.
- **Stand-down is not a blend**: when the player or another mod reclaimed the camera, the director leaves it where they put it ([Borrow the main camera, hand it back and arbitrate between camera users](../../main-view/borrow-the-main-camera-and-hand-it-back/overview.md)).

## See also

- [Topic overview](overview.md); [approach 1](play-cinematic-camera-tracks-1.md), [2](play-cinematic-camera-tracks-2.md), [3](play-cinematic-camera-tracks-3.md)
- [Programmable camera director](../../../../mods/gatos/features/programmable-camera-director.md)
- [Animate the flight camera with eased moves, keyframe sequences and saved recipes](../chain-camera-moves-into-keyframe-sequences/overview.md), [Schedule timed command sequences on render, wall or sim time](../../../external-io/control/schedule-timed-commands/overview.md)
