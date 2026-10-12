---
type: How-To
title: Analytic closest approach t = -r.v / v.v clamped to the step
description: Treat the separation as linear over each sub-step, solve for the moment of closest approach, and fire proximity, contact, timed, height and ground fuses at the exact instant within the frame
category: physics
group: mod-simulation
topic: prevent-tunnelling-of-fast-bodies
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/analytic-subframe-fuses]
techniques: [analytic-closest-approach, sub-stepping]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Vehicle, KSA.Universe, KSA.Celestial]
tags: [physics, fuse, closest-approach, proximity, sub-stepping, projectile, ground-crossing]
---

Outcome: a round at 2 km/s that crosses 10 m per 5 ms sub-step still sees a 2.5 m fuse, and the burst position and its
time within the frame come out of the same solve. The pattern serves anything fast that must detect a near pass
against something it samples only once per frame: hit detection, docking triggers, sensor alarms. Prerequisites:
StarMap, and a body integrated in sub-steps ([approach 1 of the simulation topic](../simulate-bodies-outside-the-vehicle-system/simulate-bodies-outside-the-vehicle-system-1.md)).
KSArmory demonstrates it for missiles, shells and flak.

## Background: how the game does it

The engine offers nothing for this: a mod-simulated round has no Bepu body, and the vehicles it tests against are
sampled once per frame at the end of the step just applied (`Universe.GetElapsedTime()`), with
`Vehicle.GetPositionEcl()` and `GetVelocityEcl()` as the values `UpdatePerFrameData` wrote during
`Universe.ApplyVehicleSolvers`.[^ksa-universe] A target's size comes from `Vehicle.MeanRadius`
(`BoundingSphereRadiusBody`).[^ksa-vehicle] Everything else is the mod's.

## Lifecycle and timing

Fuses run inside `Interceptor.Step` and `Slug.Step`, inside the mod's once-per-frame step from `StarMapAfterGui`, the
`OnFrameCelestials` prefix (HUD hidden) or `StarMapAfterOnFrame`. At that point target samples are end-of-step values;
the next vehicle step is already running on the workers, so the fuse only reads and applies consequences later in the same call.
The [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) explains the order.
Every input must be a difference at the round's own epoch, which is why targets are back-dated first ([epoch
back-dating](../simulate-bodies-outside-the-vehicle-system/simulate-bodies-outside-the-vehicle-system-4.md)).

## Steps

1. Write the primitive. The separation is linear over the step, `r(t) = r + v t`; the minimiser is
   `t = -r.v / v.v`, clamped to `[0, horizon]`, and 0 for a stationary pair. Miss distance is `|r + v t|`:

```csharp
// src/KSArmory/Sim/Vec.cs L81-L91
    /// <summary>
    /// Time of closest approach for a point separating as r(t) = r + v*t, clamped to
    /// [0, horizon]. Returns 0 for a stationary relative pair.
    /// </summary>
    public static double TimeOfClosestApproach(double3 r, double3 v, double horizon)
    {
        double vv = Len2(v);
        if (vv < 1e-12) return 0.0;
        double t = -Dot(r, v) / vv;
        return Math.Clamp(t, 0.0, horizon);
    }
```

2. Build `r` and `v` as differences at the round's epoch. For a target, `r = body.PositionEcl +
   body.VelocityEcl * backdate - round.PositionEcl` where `backdate = elapsedInFrame - frameSeconds`, and
   `v = body.VelocityEcl - round.VelocityEcl`. The shared ecliptic carrier of about 29.8 km/s cancels in the
   subtraction.
3. Proximity fuse for a missile: evaluate after the guidance command and before the sub-step is integrated, with the
   same `r` and `v` the guidance uses. It does not ask the seeker's permission, so a round that lost its seeker can
   still fuse. Move the round to the closest approach, record the miss distance and the instant:

```csharp
// src/KSArmory/Sim/Interceptor.cs L545-L565
                if (Age >= munition.FuseArmSeconds)
                {
                    double trigger = munition.FuseRadius + t.Radius;
                    double tCa = Vec.TimeOfClosestApproach(r, v, h);
                    double miss = Vec.Len(r + v * tCa);
                    if (miss <= trigger)
                    {
                        // Detonate where the round actually is at closest approach.
                        PositionEcl += VelocityEcl * tCa;
                        MissDistance = miss;
                        // Reported on the same epoch as the geometry that produced it. Whatever
                        // applies the warhead advances the world forward by this much to place
                        // the burst and to sweep it, so it has to be measured from the target's
                        // back-dated instant too. Measured from the sampled instant instead, the
                        // blast lands V*frameSeconds out in the opposite direction: the
                        // back-dating above and this are one rule, not two.
                        DetonationElapsedInFrame = elapsedInFrame + tCa - frameSeconds;
                        State = RoundState.Detonated;
                        return;
                    }
                }
```

4. Contact fuse for an unguided shell. Consider the designated target and every other contact; keep the earliest
   contact time, not the first found, because enumeration order is arbitrary. Before arming the trigger radius is 0
   so a shell still strikes what it physically touches:

```csharp
// src/KSArmory/Sim/Slug.cs L658-L709
        // Touching does not wait for the fuze; only its reach does. A shell runs into anything in its
        // way before it has armed, as it runs into the ground, and until then its proximity fuse is dead.
        double trigger = Age >= munition.FuseArmSeconds ? munition.FuseRadius : 0.0;

        {
            bool struck = false;
            double soonest = double.MaxValue;
            double at = 0.0;
            object? hitBody = null;

            // Earliest, not first found: between two bodies a round hits the one it reaches, and
            // list order is whatever the caller's enumeration happened to be.
            void Consider(TargetState body)
            {
                if (!Vec.IsFinite(body.PositionEcl) || !Vec.IsFinite(body.VelocityEcl)) return;

                double3 r = body.PositionEcl + body.VelocityEcl * backdate - PositionEcl;
                double3 v = body.VelocityEcl - VelocityEcl;

                // The designated target already contributes its own; anything else would report
                // the nearest bystander as how close the round came to what it was shooting at.
                if (target is null) ClosestApproach = Math.Min(ClosestApproach, Vec.Len(r));

                if (ContactSweep.TryStrike(r, v, h, trigger, body.Radius,
                                           Hull, body.Handle,
                                           out double when, out double miss)
                    && when < soonest)
                {
                    struck = true;
                    soonest = when;
                    at = miss;
                    hitBody = body.Handle;
                }
            }

            if (target is { } designated) Consider(designated);
            for (int i = 0; i < Contacts.Count; i++) Consider(Contacts[i]);

            if (struck)
            {
                PositionEcl += VelocityEcl * soonest;
                MissDistance = at;
                StruckBody = hitBody;
                ClosestApproach = Math.Min(ClosestApproach, at);

                // Negative: the world sample is end-of-step, so the caller advances the world
                // backward by this much to place the burst.
                DetonationElapsedInFrame = elapsedInFrame + soonest - frameSeconds;
                State = RoundState.Detonated;
                return;
            }
        }
```

5. Timed (flak) fuse: before proximity, when `Age >= FuseSeconds` burst exactly then, `tBurst = h - (Age -
   FuseSeconds)` into the sub-step, with a live target still getting a miss distance measured at the burst:

```csharp
// src/KSArmory/Sim/Slug.cs L617-L625
        // Before proximity: a shell fused for a time bursts then, whether or not anything is near,
        // which is the whole point of flak. A live target still gets a miss distance measured at
        // the burst, so lethality is decided the same way as any other detonation.
        if (FuseSeconds > 0.0 && Age >= FuseSeconds)
        {
            double tBurst = Math.Max(0.0, h - (Age - FuseSeconds));

            PositionEcl += VelocityEcl * tBurst;
            DetonationElapsedInFrame = elapsedInFrame + tBurst - frameSeconds;
```

6. Ground crossing: linear in altitude over the sub-step. `f = was / (was - now)` gives the fraction of the step; only
   a downward crossing counts, so a round launched from a tube slightly below the coarse height field does not burst
   on its rail. The ground centre is itself back-dated per sub-step:

```csharp
// src/KSArmory/Sim/Interceptor.cs L578-L598
        if (_haveGround)
        {
            double was = Vec.Len(before - GroundCentre(elapsedInFrame - frameSeconds)) - _groundRadius;
            double now = Vec.Len(PositionEcl - GroundCentre(elapsedInFrame + h - frameSeconds)) - _groundRadius;

            // Only crossing down from above. A tube can sit a hair under the coarse height field,
            // and a round that started below it would otherwise burst on its own rail.
            if (was > 0.0 && now <= 0.0)
            {
                double f = Math.Clamp(was / (was - now), 0.0, 1.0);

                PositionEcl = before + stepEcl * f;
                // Not a fuse range: zero would read as a direct hit on the target. The blast sweep
                // judges what was near where it went off.
                MissDistance = double.PositiveInfinity;
                HitGround = true;
                DetonationElapsedInFrame = elapsedInFrame + h * f - frameSeconds;
                State = RoundState.Detonated;
                return;
            }
        }
```

7. Keep the burst instant. `DetonationElapsedInFrame = elapsedInFrame + t - frameSeconds` is negative; `Detonate`
   uses it to move every world sample back to the burst instant before judging lethality. Reversing the sign
   doubles the frame-sized error instead of cancelling it.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Sim/Vec.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Vec.cs#L81-L91) | `TimeOfClosestApproach` |
| [src/KSArmory/Sim/ContactSweep.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ContactSweep.cs) | `TryContact`, `TryStrike` (sphere then hull), `TryReachSphere` |
| [src/KSArmory/Sim/Interceptor.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Interceptor.cs#L524-L598) | Missile proximity fuse and ground crossing |
| [src/KSArmory/Sim/Slug.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Slug.cs#L617-L843) | Timed, contact, height and ground fuses for unguided rounds |
| [tests/KSArmory.Tests/HullStrikeTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/HullStrikeTests.cs) | Kinetic rounds must touch; the sphere rejects before the hull is asked |

## Variations and gotchas

- `MissDistance` is not a miss distance: it is the range at a threshold crossing whose look-ahead is one sub-step,
  so it is bounded by the trigger and converges toward the fuse radius as the sub-step shrinks. The author measured
  10-15 m while rounds missed by 450-680 m before target back-dating was added.
- A shell's proximity reach is contact-only against meshed craft: a `Missed` hull verdict vetoes the sphere, so a
  shell a few metres beside a craft does not burst. The flak effect comes from the timed fuse instead.
- Linear separation drops curvature inside one sub-step; the error is about half the lateral acceleration times the
  step squared, which is why the faithful-step clamp defaults to 0.32 s.
- Ground crossings use the chord between two height samples; a landing-frame refinement against the real terrain
  (`Slug.StopOnTheTerrain`) fixes that.
- For every shipped missile `FuseRadius < LethalRadius`, so every proximity trigger is lethal regardless of shot quality.

## See also

- [Overview and comparison](overview.md)
- [Sweep against a bounding sphere, then cast the hull triangles](prevent-tunnelling-of-fast-bodies-2.md): the narrow phase
- Mod feature: [KSArmory analytic sub-frame fuses](../../../../mods/ksarmory/features/analytic-subframe-fuses.md)
- [Simulate bodies outside the vehicle system](../simulate-bodies-outside-the-vehicle-system/overview.md)

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
