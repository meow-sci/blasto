---
type: How-To
title: Compose each frame's pose from keyframed tracks, manual overrides and a captured baseline
description: gatOS evaluates Track over Override over Baseline per channel in a game-free compositor, with spline and easing playback, and applies the composed pose from a FixedController subclass inside the viewport pass
category: camera
group: special-views
topic: play-cinematic-camera-tracks
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony, reflection]
demonstrated_by: [mods/gatos/features/programmable-camera-director, mods/gatos/features/synthetic-camera-follow-anchor]
techniques: [controller-subclass]
hooks: [harmony-prefix KSA.GameViewport.OnFrame, subclass KSA.FixedController.OnFrame]
lifecycle: [frame:viewports, frame:draw-ui]
game_types: [KSA.FixedController, KSA.GameViewport, KSA.Camera, KSA.IFollowable, KSA.KittenEva, KSA.Program]
tags: [camera, cinematic, track, spline, compositor, easing, gatos]
---
gatOS lets a guest program play camera shots: keyframed tracks with spline positions, slerp or squad orientations, easing and roll and field-of-view channels, placed in one of several vessel, part or body reference frames. The design splits cleanly: a **game-free** compositor
and track evaluator (`gatOS.SimFs/Camera/`) decide what the camera should look like, and a thin KSA seam applies the result in phase with the frame. This article is the compositor and playback; the seam is the pattern in
[approach 2](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md) ([article](../../controllers/write-the-camera-pose-in-phase-with-the-frame/write-the-camera-pose-in-phase-with-the-frame-2.md)). Prerequisites: StarMap, Harmony, reflection.

## Background: how the game does it

KSA has no camera track system. A camera is `Camera.PositionEcl` and `Camera.LocalRotation`, written by the active controller each frame and baked into matrices by `Camera.OnFrame` ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L116-L124), [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L487-L497)). The `Fixed` controller writes exactly those
two properties from `CameraOffset` and `CameraRotation` relative to the followed object ([current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L22-L47)), which is what a track needs. IVA and Map ownership are unsupported by gatOS because their controllers overwrite their own contexts each frame.

## Lifecycle and timing

All of it is `frame:viewports`: the prefix on the main `GameViewport.OnFrame` samples the schedule clock and drains commands, the director prepares the composed pose, and the installed controller's `OnFrame` writes it; the postfix publishes the clamped result. It runs every rendered frame, including with the HUD
hidden. Everything is main-thread; transports enqueue commands and read a volatile status snapshot. While gatOS owns the camera, EVA "forward" follows the shot, because `KittenEva.PrepareWorker` takes the camera basis from `Program.GetMainCamera()` ([current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L319-L330)).

## Steps

1. **Layer the channels.** Each channel (position, aim, roll, FOV, ...) is `Track ?? Override ?? Baseline`: the baseline is captured from the live camera at take (so taking the camera is visually a no-op), an override persists until reset, and a track claims only the channels the shot declares.
   Writing a channel a shot is driving is accepted but superseded next frame. The compositor allocates nothing:

```csharp
// gatOS.SimFs/Camera/CameraState.cs L483-L512
public CameraPose Compose(in CameraPose? trackSample, CameraChannelMask trackChannels)
{
    var track = trackSample ?? default;
    var claims = trackSample.HasValue ? trackChannels : CameraChannelMask.None;

    // Position moves as one unit (Cartesian spelling + geodetic spelling + the "which is live"
    // bit), because they are two ways of authoring the same channel.
    var position = Pick(CameraChannel.Position, claims);
    var frame = Pick(CameraChannel.Frame, claims);
    var anchor = Pick(CameraChannel.Anchor, claims);

    return new CameraPose
    {
        Position = position switch
        {
            Layer.Track => track.Position,
            Layer.Override => _overrides.Position,
            _ => _baseline.Position,
        },
        PositionIsGeo = position switch
        {
            Layer.Track => track.PositionIsGeo,
            Layer.Override => _overrides.PositionIsGeo,
            _ => _baseline.PositionIsGeo,
        },
        Latitude = position switch
        {
            Layer.Track => track.Latitude,
            Layer.Override => _overrides.Latitude,
            _ => _baseline.Latitude,
```

2. **Evaluate keyframes with splines and easing.** Position is Catmull-Rom or cubic Bezier through the keys, orientation is slerp (two keys, or `linear`) or squad (three or more), and the ease comes from the departing key:

```csharp
// gatOS.SimFs/Camera/TrackEvaluator.cs L264-L288
private static Quat EvalQuat(TrackChannel<Quat> channel, double t)
{
    if (!Segment(channel, t, out var i, out var u))
        return channel[i].Value;

    var k0 = channel[i];
    var k1 = channel[i + 1];
    if (channel.Curve == CurveKind.Step)
        return k0.Value;

    var e = Easing.Apply(u, k0.Ease);

    // Slerp is only C⁰ across keys — the angular velocity jumps at every waypoint, which reads as a
    // flick. Squad needs three keys to have a tangent to match, so a two-key channel (and an
    // explicit "curve": "linear") slerps.
    if (channel.Curve == CurveKind.Linear || channel.Count < 3)
        return Splines.Slerp(k0.Value, k1.Value, e);

    var lastIndex = channel.Count - 1;
    var previous = channel[Math.Max(i - 1, 0)].Value;
    var next = channel[Math.Min(i + 2, lastIndex)].Value;
    var controlA = Splines.SquadIntermediate(previous, k0.Value, k1.Value);
    var controlB = Splines.SquadIntermediate(k0.Value, k1.Value, next);
    return Splines.Squad(k0.Value, controlA, controlB, k1.Value, e);
}
```

3. **Keep the ease set small and named.** `linear`, `in`, `out`, `in-out` and an explicit cubic Bezier; the power curves are the unscience formulas unchanged:

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

4. **Play on a schedulable clock.** A playing track registers in the schedule registry as `kind = camera-track` so pause, scrub, rate and loop controls apply; the director samples it with `TryEvaluateNow` on the player's own clock rather than deriving "now" from `dt` ([gatOS.SimFs/Camera/Playback.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Camera/Playback.cs#L62-L66), [gatOS.SimFs/Camera/Playback.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Camera/Playback.cs#L155-L190)).

5. **Apply in phase.** The director resolves the composed pose in the engine's own epoch (the placement frame: vessel, part, body or held point), smooths if asked, and returns an offset from the followed anchor to the controller:

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

6. **Drive the camera through a synthetic anchor** so every offset is measured from a position sampled in that call ([Make the camera follow a point or mod-simulated object, and change the follow target without a jump](../../main-view/follow-a-point-that-is-not-a-vehicle/overview.md)).

## Complete example

- Game-free: [gatOS.SimFs/Camera/CameraState.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Camera/CameraState.cs), [gatOS.SimFs/Camera/TrackEvaluator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Camera/TrackEvaluator.cs), [gatOS.SimFs/Camera/Splines.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Camera/Splines.cs), [gatOS.SimFs/Camera/Easing.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Camera/Easing.cs), [gatOS.SimFs/Camera/Playback.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Camera/Playback.cs), [gatOS.SimFs/Camera/TrackParser.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Camera/TrackParser.cs)
- KSA seam: [gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraDirector.cs), [gatOS.GameMod/Game/Ksa/Camera/CameraPoseController.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraPoseController.cs), [gatOS.GameMod/Game/Ksa/Camera/CameraFollowable.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Camera/CameraFollowable.cs)

## Variations and gotchas

- **Slerp is only C0 across keys**: the angular velocity jumps at each waypoint, which reads as a flick, hence squad for three or more keys.
- **Aim is a constraint, not smoothed.** Smoothing aim makes a moving subject drift out of frame; the director smooths the offset and solves look-at exactly from the applied point.
- **The `time` channel** drives simulation speed from the composed pose, so a shot can ease into slow motion; the captured speed is restored at release.
- **Orthographic height** cannot be restored at release (no public getter).
- **Pending validation.** The repo notes a driver bug found in the first live pass and a fix whose live recheck is pending.

## See also

- [Topic overview](overview.md); [approach 2](play-cinematic-camera-tracks-2.md), [3](play-cinematic-camera-tracks-3.md), [4](play-cinematic-camera-tracks-4.md)
- [Programmable camera director](../../../../mods/gatos/features/programmable-camera-director.md), [Synthetic camera follow anchor](../../../../mods/gatos/features/synthetic-camera-follow-anchor.md)
- [Animate the flight camera with eased moves, keyframe sequences and saved recipes](../chain-camera-moves-into-keyframe-sequences/overview.md), [Make the camera follow a point or mod-simulated object, and change the follow target without a jump](../../main-view/follow-a-point-that-is-not-a-vehicle/overview.md), [Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md), [Schedule timed command sequences on render, wall or sim time](../../../external-io/control/schedule-timed-commands/overview.md), [Borrow the main camera, hand it back and arbitrate between camera users](../../main-view/borrow-the-main-camera-and-hand-it-back/overview.md), [Chase a projectile or other fast body with the main camera](../../main-view/chase-a-fast-body-with-the-main-camera/overview.md)
