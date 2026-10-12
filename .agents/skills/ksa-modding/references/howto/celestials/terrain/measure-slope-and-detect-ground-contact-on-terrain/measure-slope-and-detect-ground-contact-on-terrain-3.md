---
type: How-To
title: Per-frame centre and radius, crossing test, interpolation and near-surface resample
description: Stop a mod-simulated round at the terrain by testing a was and now height each sub-step against a per-frame sphere, interpolating the contact point and re-reading the ground near impact
category: celestials
group: terrain
topic: measure-slope-and-detect-ground-contact-on-terrain
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/terrain-ground-impact-test]
techniques: [custom-physics, terrain-query]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Celestial, KSA.CelestialSystem, KSA.Universe, KSA.IParentBody]
tags: [terrain, ground-contact, sub-step, interpolation, epoch, fuse]
---
Outcome: a body the engine does not simulate (a bomb, a shell, a reentry vehicle) stops at the surface, with the contact point accurate to millimetres even at 5 km/s and with the body having moved and rotated since the ground was sampled. Demonstrated by KSArmory ([Terrain ground impact test](../../../../mods/ksarmory/features/terrain-ground-impact-test.md)). The seam and the sea clamp are in [query terrain height, approach 2](../query-terrain-height-and-line-of-sight/query-terrain-height-and-line-of-sight-2.md); this article is the contact logic that sits on top.

## Background: how the game does it

KSA stops vehicles with its own terrain patches and Bepu; nothing stops a mod's integrated body. The mod therefore asks the ground test for a **centre and a surface radius** once per frame and tests height by subtraction at every integration sub-step. The body was sampled at the end of the applied physics step ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L591-L606)), while the round is part-way through the frame, so two corrections are needed (centre drift, query drift) and, for fast arrivals, a re-read near the surface.

## Lifecycle and timing

The round steps once per frame from `[StarMapAfterGui]` (`frame:draw-ui`). With the HUD hidden that hook does not fire; the same latched step then runs from a Harmony prefix on `Program.OnFrameCelestials` (`frame:celestials`, still before the render), and from `[StarMapAfterOnFrame]` (`frame:after-on-frame`, one frame late) only if that patch could not be applied ([src/KSArmory/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/PreRenderHook.cs#L56-L80)). The ground sample is read at the start of that step, from the end-of-step state; the sub-steps run inside the same call. Corrections are expressed as seconds into the frame, negative because the sample is at the frame's end. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Sample once per frame** from the round's position at its own epoch and hold `_groundCentre` and `_groundRadius`:

```csharp
// src/KSArmory/Sim/Slug.cs L486-L497
double3 atOwnEpoch = GroundQueryAt(PositionEcl, -dt);

_groundSampledAtEcl = atOwnEpoch;
_groundSampledOverSeconds = dt;

_haveGround = munition.HitsTerrain && Ground is not null
              && Ground.TryGround(atOwnEpoch, out _groundCentre, out _groundRadius)
              && double.IsFinite(_groundRadius) && _groundRadius > 0.0;

// The frame's own sample is the ground under where its first sub-step begins.
_radiusBefore = _groundRadius;
_radiusBeforeFresh = _haveGround;
```

2. **Back-date the centre** to each sub-step instant, and the query by the ground's velocity (see [query terrain height approach 2](../query-terrain-height-and-line-of-sight/query-terrain-height-and-line-of-sight-2.md) for the callbacks). Without it the held centre drifts against the round by `bodyVelocity x (frame - elapsed)`, which at orbital speeds is kilometres of ground.

3. **Near the surface, re-read under both ends of the step.** Inside a 200 m band of the held sphere (`GroundResampleBandMetres`) a sphere of the frame's radius is the height of ground the round has already left on a slope:

```csharp
// src/KSArmory/Sim/Slug.cs L732-L758
double radiusWas = _groundRadius;
double radiusNow = _groundRadius;
bool reread = false;

// Near the surface, the ground under both ends of this step rather than the sphere the
// frame began with, which on a slope is the height of ground the round has left.
if (ResampleGroundNearImpact
    && Vec.Len(PositionEcl - centreNow) - _groundRadius < GroundResampleBandMetres
    && TryRadiusUnder(PositionEcl, elapsedInFrame + h - frameSeconds, out double under))
{
    if (!_radiusBeforeFresh)
    {
        _radiusBeforeFresh = TryRadiusUnder(before, elapsedInFrame - frameSeconds,
                                            out _radiusBefore);
    }

    radiusWas = _radiusBeforeFresh ? _radiusBefore : _groundRadius;
    radiusNow = under;
    reread = true;

    _radiusBefore = under;
    _radiusBeforeFresh = true;
}
else
{
    _radiusBeforeFresh = false;
}
```

4. **Test `was` and `now`, fire a height fuse, then contact.** The fuse fires on the downward crossing (`was > fuseAt && now <= fuseAt`); contact on `now <= 0`, with the point interpolated linearly along the sub-step. An interceptor fuses only on a crossing from above (`was > 0`):

```csharp
// src/KSArmory/Sim/Slug.cs L760-L785
double was = Vec.Len(before - centreWas) - radiusWas;
double now = Vec.Len(PositionEcl - centreNow) - radiusNow;

// The height fuse, crossed on the way down. Linear across the step like the ground
// crossing below; a store already under the height when it arms bursts on the ground.
double fuseAt = munition.BurstHeightMetres;
if (fuseAt > 0.0 && Age >= munition.FuseArmSeconds && was > fuseAt && now <= fuseAt)
{
    double g = Math.Clamp((was - fuseAt) / (was - now), 0.0, 1.0);

    PositionEcl = before + stepEcl * g;
    MissDistance = double.PositiveInfinity;
    BurstAtHeight = true;
    DetonationElapsedInFrame = elapsedInFrame + h * g - frameSeconds;
    State = RoundState.Detonated;
    return;
}

if (now <= 0.0)
{
    // Back to where it crossed, so the burst is on the surface rather than up to a
    // sub-step underneath it. Linear across the step: the round's own drop over 1.5 m
    // is not where the curvature lives.
    double f = Math.Clamp(was > 0.0 ? was / (was - now) : 0.0, 0.0, 1.0);

    // That solves against the CHORD joining two height samples a whole sub-step apart --
    // ...
```

5. **Refine against the chord.** A linear solution lands where the path meets the chord between two height samples a sub-step apart, not where the terrain does. With `StopOnTheTerrain` the round queries the ground at the crossing and takes one false-position step from the same bracket, then reads again at the refined point so the reported radius is the radius under the stopping point:

```csharp
// src/KSArmory/Sim/Slug.cs L795-L824
if (StopOnTheTerrain && reread && was > 0.0)
{
    double atCross = elapsedInFrame + h * f - frameSeconds;
    double3 crossEcl = before + stepEcl * f;

    if (TryRadiusUnder(crossEcl, atCross, out double there))
    {
        double alt = Vec.Len(crossEcl - GroundCentre(atCross)) - there;

        crossRadius = there;

        if (Math.Abs(was - alt) > 1e-12)
        {
            f = Math.Clamp(f * was / (was - alt), 0.0, 1.0);

            // Read again where the refinement actually put it. The step is
            // millimetres, so this changes nothing about where the round stops --
            // it is what makes GroundRadiusUsed the radius under GroundSampledAtEcl
            // rather than the radius under the point that solved for it, which was
            // 5.9 mm out on a metre of relief every forty. The pair is what the
            // trace's surface line compares, and 46c reads that line.
            if (TryRadiusUnder(before + stepEcl * f,
                               elapsedInFrame + h * f - frameSeconds,
                               out double settled))
            {
                crossRadius = settled;
            }
        }
    }
}
```

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Sim/Slug.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Slug.cs#L486-L497) | Per-frame sample, `was`/`now`, fuse, contact, refinement |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L3323-L3343) | Centre and query drift callbacks |
| [src/KSArmory/Ksa/GroundTest.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/GroundTest.cs) | Engine-backed `IGroundTest` |

## Variations and gotchas

- **Only fuse on a crossing from above for interceptors**, or a tube under the field bursts on its own rail.
- **The sphere is exact only for slow bodies.** For anything covering tens of metres of ground a frame, re-read near the surface.
- **Sea is a flat clamp** in this mod; the engine's vehicle physics uses waves.
- **Failure answers false.** An exception or non-finite radius means no ground; the round keeps flying rather than stopping at the origin.

## See also

- [Overview](overview.md); [approach 1](measure-slope-and-detect-ground-contact-on-terrain-1.md)
- [Terrain ground impact test](../../../../mods/ksarmory/features/terrain-ground-impact-test.md)
- [Prevent tunnelling of fast bodies](../../../physics/mod-simulation/prevent-tunnelling-of-fast-bodies/overview.md), [Simulate bodies outside the vehicle system](../../../physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/overview.md)
