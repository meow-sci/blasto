---
type: How-To
title: Offset against the followed object sampled in the same call, with sampling and publishing on GameViewport.OnFrame
description: gatOS' CameraPoseController reads following.GetPositionEcl() at that instant and writes PositionEcl and LocalRotation, while a prefix on the main GameViewport.OnFrame prepares the pose and a postfix publishes the clamped result
category: camera
group: controllers
topic: write-the-camera-pose-in-phase-with-the-frame
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony, reflection]
demonstrated_by: [mods/gatos/features/programmable-camera-director, mods/gatos/features/synthetic-camera-follow-anchor]
techniques: [controller-subclass, harmony-prefix, harmony-postfix]
hooks:
  - subclass KSA.FixedController.OnFrame
  - harmony-prefix KSA.GameViewport.OnFrame
  - harmony-postfix KSA.GameViewport.OnFrame
lifecycle: [frame:viewports]
game_types: [KSA.FixedController, KSA.GameViewport, KSA.Camera, KSA.IViewport, KSA.IFollowable, KSA.Transform3D]
tags: [camera, phase, fixed-camera, controller, followable, gatos]
---
gatOS gives a guest program the main camera. Its first design wrote the pose from a Harmony prefix and left the
camera one frame out of register at orbital speed; it was replaced by a `FixedController` subclass whose
`OnFrame` makes the engine's two writes against a position sampled in that same call. Two further pieces keep
the frame coherent: a prefix on `GameViewport.OnFrame` prepares the pose, and a postfix publishes the final
clamped transform. The offsets are measured from a synthetic follow anchor
([Make the camera follow a point or mod-simulated object, and change the follow target without a jump](../../main-view/follow-a-point-that-is-not-a-vehicle/overview.md)), which also keeps precision at orbital speed.

## Background: how the game does it

The same facts as [approach 1](write-the-camera-pose-in-phase-with-the-frame-1.md): the controller's `OnFrame`
runs immediately before `Camera.OnFrame` ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L116-L124)), and the stock fixed controller writes
`PositionEcl` and `LocalRotation` ([current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L36-L46)). While a body is followed, `Camera.PositionEcl` is
the followed position plus the camera's own offset ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L110-L131)), so an offset written against
a followed position sampled elsewhere in the frame carries a frame of that body's motion.

## Lifecycle and timing

`frame:viewports`, in three parts for the main viewport: (1) the prefix runs the sampler, schedule tick and
command drain and lets the director prepare the pose; (2) KSA runs the installed controller's `OnFrame`, which writes;
(3) the postfix reads the final, ground-clamped transform. It runs every rendered frame, including with the HUD hidden,
because `OnFrameViewports` is outside the `DrawUI` block ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2260-L2272)).

## Steps

1. **Write the controller against a pose-source interface.** The source gets the followed position now and the frame delta:

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraPoseController.cs L17-L27
internal interface ICameraPoseSource
{
    /// <param name="camera">The camera being driven (the main viewport's base camera).</param>
    /// <param name="followedEcl">Where the followed anchor is <i>now</i>, from the engine's pass.</param>
    /// <param name="dt">The engine's frame delta for this viewport pass, seconds.</param>
    /// <param name="offsetFromFollowed">The camera's placement as an offset from the anchor.</param>
    /// <param name="rotationEcl">The camera's final ECL orientation (aim, roll and smoothing applied).</param>
    /// <returns>False to leave the camera riding the anchor on its previous offset and rotation.</returns>
    bool TryPose(KsaCamera camera, double3 followedEcl, double dt,
        out double3 offsetFromFollowed, out doubleQuat rotationEcl);
}
```

2. **Make the two writes in the same call that sampled the anchor**, and keep the stock fields coherent
   so a fallback into `base.OnFrame` never sees a zero `CameraRotation`:

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraPoseController.cs L88-L122
{
    if (Pose is not { } source || Faulted)
    {
        base.OnFrame(inViewport, inDeltaTime);
        return;
    }

    if (Camera.Following is not { } following)
        return;

    try
    {
        var followedEcl = following.GetPositionEcl();
        if (!source.TryPose(Camera, followedEcl, inDeltaTime, out var offset, out var rotation))
            return; // hold: the camera keeps riding the followable on its previous offset

        Camera.PositionEcl = followedEcl + offset;
        Camera.LocalRotation = rotation;

        // Keep the stock input fields coherent with what was applied, so any fallback into
        // base.OnFrame (fault, source cleared) continues from this pose instead of a stale or —
        // fatally — zero CameraRotation.
        CameraOffset = offset;
        var forward = (-double3.UnitZ).Transform(rotation);
        if (forward.IsFinite() && !forward.IsNearlyZero())
            CameraRotation = forward;
    }
    catch (Exception ex)
    {
        // Engine loop: never rethrow. One error line, then hold the last pose until the director
        // notices the fault in the next prefix and hands the camera back.
        Faulted = true;
        ModLog.Log.Error($"gatOS camera: in-phase pose solve failed — {ex.Message}");
    }
}
```

3. **Resolve the anchor on every call** in the `IFollowable` the camera follows, so every offset is measured
   against the same sample ([gatOS.GameMod/Game/Ksa/Camera/CameraFollowable.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraFollowable.cs#L94-L107)).

4. **Prepare the pose in the viewport prefix, publish in the postfix**, both filtered by identity to
   `Program.MainViewport` ([approach 4 of the reference topic](../understand-camera-modes-and-controllers/overview.md)).

5. **Compute the pose from the engine's own sample** in the director's `TryPose`, the callback the controller invokes:

```csharp
// gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs L648-L652, L660-L665, L683-L694, L702-L708
public bool TryPose(KsaCamera camera, double3 followedEcl, double dt,
    out double3 offsetFromFollowed, out doubleQuat rotationEcl)
{
    offsetFromFollowed = default;
    rotationEcl = _lastRotation;
    // ...
    var pose = _statusPose;
    var anchor = _statusAnchor;

    // ---- placement, resolved fresh in the engine's epoch ---------------------------------------
    if (CameraFrames.TryResolvePlacement(pose, anchor, out var placement, out var error))
    {
    // ...
    _lastPositionEcl = ToKsa(smoothed);

    // ---- orientation ---------------------------------------------------------------------------
    var targetRotation = ResolveRotation(pose, anchor, _lastPositionEcl);
    if (pose.AimTarget.HasTarget)
    {
        // Aim is a constraint, not a loose rotation suggestion: smoothing it makes a moving
        // subject drift out of frame. Smooth the camera's offset, then solve look-at exactly from
        // that applied point every frame — the same sample the engine renders from.
        _rotationSmoother.Reset();
        _lastRotation = targetRotation;
    }
    // ...
    ApplyProjection(camera, pose, _frameClaims);
    ApplyTimeScale(pose, _frameClaims);

    offsetFromFollowed = _lastPositionEcl - followedEcl;
    rotationEcl = _lastRotation;
    return true;
}
```

## Complete example

- [gatOS.GameMod/Game/Ksa/Camera/CameraPoseController.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraPoseController.cs)
- [gatOS.GameMod/Game/Ksa/Camera/CameraFollowable.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraFollowable.cs)
- [gatOS.GameMod/Game/Ksa/Camera/CameraViewportPatch.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraViewportPatch.cs)

## Variations and gotchas

- **Faults hand the camera back.** If the pose source throws, the controller sets `Faulted`, holds its last pose, and the
  director releases at the next prefix; it never rethrows into the frame loop.
- **Hold on refusal.** `TryPose` returning false keeps the camera riding the followable on its previous offset.
- **Design history.** gatOS' notes say the earlier "write in a prefix" design lagged one frame; in-game
  validation of the rewrite is listed as pending in the repo ([Synthetic camera follow anchor](../../../../mods/gatos/features/synthetic-camera-follow-anchor.md)).
- **Offsets from a followed object keep precision at orbital speed**, because the difference of two huge ecliptic positions is avoided.

## See also

- [Topic overview](overview.md); [approach 1](write-the-camera-pose-in-phase-with-the-frame-1.md), [3](write-the-camera-pose-in-phase-with-the-frame-3.md)
- [Programmable camera director](../../../../mods/gatos/features/programmable-camera-director.md), [Synthetic camera follow anchor](../../../../mods/gatos/features/synthetic-camera-follow-anchor.md)
- [Replace a viewport's camera controller and take input through it](../replace-a-viewport-camera-controller/overview.md), [Make the camera follow a point or mod-simulated object, and change the follow target without a jump](../../main-view/follow-a-point-that-is-not-a-vehicle/overview.md), [Animate the flight camera with eased moves, keyframe sequences and saved recipes](../../special-views/chain-camera-moves-into-keyframe-sequences/overview.md)
