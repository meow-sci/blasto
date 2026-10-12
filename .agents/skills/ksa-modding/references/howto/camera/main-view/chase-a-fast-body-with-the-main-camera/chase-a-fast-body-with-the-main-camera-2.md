---
type: How-To
title: Hold a line to the target near the flight path with a carried lift and lean off the singular axis
description: KSAGolf's pure ChaseView solves a stable eye behind a fast ball, held within 80 degrees of the flight path with a carried lift and a lean off the axis the stock controller cannot cross; only the geometry is tested and part of it is wired in game at the pinned ref
category: camera
group: main-view
topic: chase-a-fast-body-with-the-main-camera
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/borrowed-chase-camera]
techniques: [camera-arbitration]
hooks: [subclass KSA.FixedController.OnFrame (intended; ChaseView has no in-game caller at the pinned ref)]
lifecycle: [frame:viewports, frame:draw-ui]
game_types: [KSA.IFollowable, KSA.Camera, KSA.FixedController, KSA.OrbitView]
tags: [camera, chase, geometry, headless-tests, singularity]
---
KSAGolf wrote the maths for watching a fast ball in flight as engine-free code (`Sim/ChaseView.cs`) and unit-tested it; the mod's notes say it is what "watching the ball" will build on. This article documents that geometry because it is the cleanest
statement of the three hazards of a chase: the line to the target reverses when the body passes it, the lift (roll reference) flips near the vertical, and the stock controller cannot look along a particular axis.

**Status at the pinned ref.** `ChaseView`, `ViewClaim`, `SmoothedStep` and `ChaseOrbit`'s drag logic have no caller in the game code; only their tests use them. KSArmory carries fuller versions in a working chase
camera ([approach 1](overview.md), [article](chase-a-fast-body-with-the-main-camera-1.md)). The wired parts are `StableUp` through the level-horizon controller and the main-view hand-back.

## Background: how the game does it

`FixedController.OnFrame` crosses `CameraRotation` with an axis from the followed object's type and normalises ([current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L36-L43)); a forward parallel to that axis divides by zero ([current/decomp/Brutal.Numerics/double3.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Numerics/double3.cs#L746-L754)). For
a mod object the axis is ecliptic +Z. Velocity is a trap too: the ecliptic frame's roughly 29.8 km/s is shared, so a chase must use the body's velocity relative to its surroundings (`VelocityLocal`), not its ecliptic velocity (the `along` direction passed to `ChaseView` is meant to be that local velocity; KSArmory's rounds expose it as `VelocityLocal`).

## Lifecycle and timing

The functions are pure; the pose is meant to be asked from the controller's `OnFrame` (`frame:viewports`) with every point sampled that frame, and advanced in the GUI hook step (`frame:draw-ui`). The ecliptic is inertial, so a stored point falls about half a
kilometre behind per frame at orbital speeds ([Write the camera pose inside the viewport pass so it has no frame of lag, or pre-empt a stock controller](../../controllers/write-the-camera-pose-in-phase-with-the-frame/overview.md)).

## Steps

1. **Stand on the line to the target, not on the flight path.** A bomb's target sits tens of degrees below the path it falls along; a camera looking along the path never has it in frame. Hold that line within 80 degrees of the path so a miss slides to the edge instead of whipping round:

```csharp
// src/KSAGolf/Sim/ChaseView.cs L64-L69, L73-L84, L88-L94, L109-L122
if (aimEcl is { } aim && Vec.IsFinite(aim))
{
    double3 toAim = aim - bodyEcl;
    double range = Vec.Len(toAim);

    // Held to the target all the way in, so the view does not swing off it at the one
    // ...
    // The look-at stays at least a look-ahead out so the framing does not pitch up as the
    // range collapses, and MinAimRange is a divide-by-zero guard rather than a distance.
    if (range > MinAimRange)
    {
        axis = HeldNearFlightPath(Vec.Unit(toAim), along);
        lookRange = Math.Max(range, ahead);
    }
}

// Carried when there is one to carry. Derived from the hint, the lift is whatever part of
// the hint lies across the axis, which near the vertical is a sliver pointing the way the
// axis leans -- and past a threshold a fixed perpendicular instead. Either reverses the
// ...
double3 carried = liftReference - axis * Vec.Dot(liftReference, axis);
double3 lift;

if (Vec.IsFinite(carried) && Vec.Len2(carried) > 1e-6)
{
    lift = Vec.Unit(carried);
}
// ...
eyeEcl = bodyEcl - axis * Math.Max(0.0, distanceBehind) + lift * heightAbove;

// Along the axis rather than at the aim itself, so a target held out at the cone edge
// leaves the body centred: the ride is of the body, and what it is flying at is what
// stands behind it.
double3 lookAt = bodyEcl + axis * lookRange;
forwardEcl = lookAt - eyeEcl;

if (Vec.Len2(forwardEcl) < 1e-12) return false;

forwardEcl = LeanOffAxis(Vec.Unit(forwardEcl), engineAxisEcl);
upEcl = lift;

return Vec.IsFinite(eyeEcl) && Vec.IsFinite(forwardEcl);
```

2. **Carry the lift.** Deriving the lift from the up hint each frame reverses it when the axis passes the vertical, so the eye swaps sides. The caller passes last frame's lift back in; the pose rejects the part along the axis.

3. **Lean off the axis the stock controller cannot cross.** The forward is nudged to stay about 2.6 degrees off it:

```csharp
// src/KSAGolf/Sim/ChaseView.cs L217-L231
public static double3 LeanOffAxis(double3 forward, double3 axisHint)
{
    double3 axis = Vec.Unit(axisHint);
    if (Vec.Len2(axis) < 0.5) return forward;

    double alongAxis = Vec.Dot(forward, axis);
    if (Math.Abs(alongAxis) <= MaxAlongAxis) return forward;

    double3 sideways = forward - axis * alongAxis;
    sideways = Vec.Len2(sideways) < 1e-12 ? AnyPerpendicular(axis) : Vec.Unit(sideways);

    double lean = alongAxis < 0.0 ? -MaxAlongAxis : MaxAlongAxis;

    return Vec.Unit(axis * lean + sideways * Math.Sqrt(1.0 - (MaxAlongAxis * MaxAlongAxis)));
}
```

4. **Close in on a root curve** as range runs out (`StandOff`), so the camera appears to hold station and then rush in:

```csharp
// src/KSAGolf/Sim/ChaseView.cs L269-L280
public static double StandOff(double range, double far, double near,
                              double farDistance, double nearDistance)
{
    if (!double.IsFinite(range) || !(far > near)) return farDistance;

    double t = Math.Clamp((range - near) / (far - near), 0.0, 1.0);

    // A root curve: the slope grows as the range runs out, so it holds station then rushes in.
    t = Math.Pow(t, Sharpness);

    return nearDistance + ((farDistance - nearDistance) * t);
}
```

5. **Blend onto it from the player's pose** with `TryBlend`: smoothstep the eye, lerp the look at equal depth, and require every point to be sampled this frame (see [the blend article](../../special-views/play-cinematic-camera-tracks/play-cinematic-camera-tracks-2.md)).

## Complete example

- [src/KSAGolf/Sim/ChaseView.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/ChaseView.cs), exercised by [tests/KSAGolf.Tests/ChaseOrbitTests.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tests/KSAGolf.Tests/ChaseOrbitTests.cs) (the only test file that references `ChaseView`)
- [src/KSAGolf/Sim/StableUp.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/StableUp.cs): the roll reference that is wired

## Variations and gotchas

- **Trimmed copies.** KSAGolf's `ChaseView` lacks stop-short, observer hand-off and burst framing that KSArmory's has; a fix in one does not reach the other.
- **Sample, do not store.** `TryBlend` insists on points sampled this frame.
- **Nothing here follows.** The caller still needs an `IFollowable` for the body ([Make the camera follow a point or mod-simulated object, and change the follow target without a jump](../follow-a-point-that-is-not-a-vehicle/overview.md)).

## See also

- [Topic overview](overview.md); [approach 1](chase-a-fast-body-with-the-main-camera-1.md)
- [Borrowing the main view and handing it back](../../../../mods/ksagolf/features/borrowed-chase-camera.md)
- [Make the camera follow a point or mod-simulated object, and change the follow target without a jump](../follow-a-point-that-is-not-a-vehicle/overview.md), [Drive the main camera through Fixed mode without crashing it, with a stable up vector](../../controllers/drive-the-main-camera-in-fixed-mode/overview.md), [Play scripted cinematic camera tracks with blends and easing](../../special-views/play-cinematic-camera-tracks/overview.md), [Replace a viewport's camera controller and take input through it](../../controllers/replace-a-viewport-camera-controller/overview.md)
