---
type: How-To
title: Smoothstep the eye, lerp the look at equal depth and pace the ease with an averaged step
description: KSAGolf's ChaseView.TryBlend smoothsteps the eye from the player's pose onto the chase and lerps the look at one depth, and SmoothedStep evens the simulated step for cosmetic eases that survive uneven frame times and pauses; both are headless-tested only at the pinned ref
category: camera
group: special-views
topic: play-cinematic-camera-tracks
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/borrowed-chase-camera]
techniques: [camera-arbitration]
hooks: [subclass KSA.FixedController.OnFrame]
lifecycle: [frame:viewports, frame:draw-ui]
game_types: [KSA.FixedController, KSA.GameViewport, KSA.Camera, KSA.Program]
tags: [camera, cinematic, blend, easing, smoothstep, frame-pacing, headless-tests]
---
A scripted camera that begins from wherever the player had the view must blend onto the shot without a cut, and the blend must not stutter when frame times are uneven or stop when the game pauses. KSAGolf solves both with two pure functions:
`ChaseView.TryBlend` for the geometry and `SmoothedStep` for the clock. This article covers **blending in and out and easing the pacing**.

**Status at the pinned ref.** `ChaseView.TryBlend` and `SmoothedStep` have no caller in KSAGolf's game code; they are headless-tested and are the groundwork for "watching the ball". KSArmory carries a fuller working version in its chase camera
([Borrowing the main view and handing it back](../../../../mods/ksagolf/features/borrowed-chase-camera.md)).

## Background: how the game does it

KSA's step is a report, not a clock: `dtPlayer x achievedFraction x simSpeed`, and `dtPlayer` carries the display's frame pacing. KSAGolf measured an alternation of 8.33 ms and 25 ms on a 120 Hz screen at a nominal 60 fps. Anything that integrates the world must use the step as it comes;
a cosmetic ease advanced by it lurches along its path, and a camera translation displaces an object by roughly 1/range, so it lands on whatever is nearest and moving slowest. The camera itself is written by the controller in `frame:viewports` ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L116-L124)).

## Lifecycle and timing

The blend is evaluated when the pose is asked, inside the controller's `OnFrame` (`frame:viewports`), from state the GUI step settled (`frame:draw-ui`); progress is advanced once per frame in the step. The pacing clock is `SmoothedStep.Next(stepSeconds)`: a paused or unusable step
returns 0 and leaves the average alone, and the first step seeds it ([Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md)).

## Blend in and out and ease the pacing

1. **Smoothstep the eye and lerp the look at equal depth.** The look starts on the body and ends on the target; both ends of the turn are carried to one depth first, because a body 100 m off lerped against a target 5 km away is taken over by the far point almost at once (seven-eighths of a 60 degree turn in the first fifth):

```csharp
// src/KSAGolf/Sim/ChaseView.cs L310-L347
public static bool TryBlend(double3 fromEcl, double3 toEcl,
                            double3 bodyEcl, double3 toLookAtEcl,
                            double3 engineAxisEcl, double t,
                            out double3 eyeEcl, out double3 forwardEcl)
{
    eyeEcl = toEcl;
    forwardEcl = Vec.Unit(toLookAtEcl - toEcl);

    if (!Vec.IsFinite(fromEcl) || !Vec.IsFinite(toEcl)) return false;
    if (!Vec.IsFinite(bodyEcl) || !Vec.IsFinite(toLookAtEcl) || !double.IsFinite(t))
    {
        return false;
    }

    double e = Smoothstep(Math.Clamp(t, 0.0, 1.0));

    eyeEcl = fromEcl + ((toEcl - fromEcl) * e);

    double depth = Vec.Len(toLookAtEcl - eyeEcl);
    if (depth < MinAimRange) return false;

    // An eye on top of the body has no direction to it, so the far end is all there is.
    double3 toBody = bodyEcl - eyeEcl;
    double3 onBody = Vec.Len(toBody) > MinAimRange
                      ? eyeEcl + (Vec.Unit(toBody) * depth)
                      : toLookAtEcl;

    double3 lookAt = onBody + ((toLookAtEcl - onBody) * e);
    double3 forward = lookAt - eyeEcl;

    if (Vec.Len2(forward) < 1e-6) return false;

    // The same tilt the settled pose gets, for the same reason: a view along the axis KSA's
    // fixed camera crosses against divides by zero, and a transition can sweep through it.
    forwardEcl = LeanOffAxis(Vec.Unit(forward), engineAxisEcl);

    return Vec.IsFinite(eyeEcl) && Vec.Len2(forwardEcl) > 0.5;
}
```

2. **Flat at both ends**, so the camera leaves and arrives without a kick:

```csharp
// src/KSAGolf/Sim/ChaseView.cs L349-L350
// Flat at both ends, so the camera leaves and arrives without a kick at either.
private static double Smoothstep(double t) => t * t * (3.0 - (2.0 * t));
```

3. **Sample every point this frame.** The points are anchored to different moving things and the ecliptic is inertial; a point captured at the start and held still falls half a kilometre behind per frame at orbital speed.

4. **Pace with an averaged step.** Exponential average, weight 0.15 (a 3:1 alternation comes out about plus or minus 4% of its mean), a paused step returns zero, the first step seeds:

```csharp
// src/KSAGolf/Sim/SmoothedStep.cs L34-L45
public double Next(double stepSeconds)
{
    // A paused or unusable step advances nothing and leaves the average alone, so what resumes
    // is what was running before rather than a decay towards zero.
    if (!double.IsFinite(stepSeconds) || stepSeconds <= 0.0) return 0.0;

    // Seeded rather than eased into from zero: a transition that started at a third of its
    // rate would crawl for its first few frames, which is the artefact this exists to remove.
    _seconds = _seconds > 0.0 ? _seconds + (stepSeconds - _seconds) * Weight : stepSeconds;

    return _seconds;
}
```

5. **Choose the clock per consumer**: transitions that must arrive on a body that moves at simulated speed use the simulated step; eases that answer a hand on the mouse use viewing time that still stops while paused ([approach 6](../../controllers/replace-a-viewport-camera-controller/overview.md)).

## Complete example

- [src/KSAGolf/Sim/ChaseView.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/ChaseView.cs#L285-L350), [src/KSAGolf/Sim/SmoothedStep.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/SmoothedStep.cs), tests [tests/KSAGolf.Tests/SmoothedStepTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/SmoothedStepTests.cs)

## Variations and gotchas

- **An exactly opposed pair lerps to nothing halfway**, and is refused rather than normalised into NaN.
- **A transition can sweep through the singular axis** of the stock fixed camera, so the blend applies `LeanOffAxis` to the forward too.
- **Not a seam for the real game yet.** Only the hand-back, `StableUp` and the `ChaseOrbit` constants are wired in KSAGolf.

## See also

- [Topic overview](overview.md); [approach 1](play-cinematic-camera-tracks-1.md), [3](play-cinematic-camera-tracks-3.md), [4](play-cinematic-camera-tracks-4.md)
- [Borrowing the main view and handing it back](../../../../mods/ksagolf/features/borrowed-chase-camera.md)
- [Chase a projectile or other fast body with the main camera](../../main-view/chase-a-fast-body-with-the-main-camera/overview.md), [Borrow the main camera, hand it back and arbitrate between camera users](../../main-view/borrow-the-main-camera-and-hand-it-back/overview.md)
