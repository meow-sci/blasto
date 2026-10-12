---
type: How-To
title: Compute the closest point of approach from straight-line relative motion and rank contacts by time to it
description: Difference two same-instant ecliptic samples, solve the time of closest approach of straight-line relative motion clamped to a horizon, classify a contact as a threat by its miss distance rather than its closing speed, and rank threats by how soon they arrive
category: flight-and-orbits
group: map-view
topic: find-closest-approach-along-the-flight-plan
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/cpa-threat-classification]
techniques: [frame-invariant-signatures, sim-boundary, flight-math]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Vehicle, KSA.Universe, KSA.Program]
tags: [closest-approach, cpa, relative-motion, threat, ranking, time-to-closest-approach, headless-tests]
---
Outcome: for every craft near you, a miss distance and a time to closest approach, computed in a few multiplications and good enough to decide what matters now. KSArmory's radar classifies a contact as a threat when, flying straight on at its current relative velocity, it will pass within a radius inside a horizon, not when it is merely closing, and ranks threats by time to closest approach. That catches a drone crossing a kilometre abeam (closing speed near zero) and ignores an airliner that will pass 9 km wide.[CPA threat classification](../../../../mods/ksarmory/features/cpa-threat-classification.md)

Use this when the horizon is seconds to minutes and the target is a craft in the same neighbourhood; use [approach 1](find-closest-approach-along-the-flight-plan-1.md) when the answer lies along an orbit.

Prerequisites: StarMap for a frame hook; two ecliptic samples taken at the same instant.

## Background: how the game does it

- **Samples.** `Vehicle.GetPositionEcl()` and `GetVelocityEcl()` return end-of-step values cached when `ApplyVehicleSolvers` installed the step, stamped at `Universe.GetElapsedTime()`.[^ksa-vehicle] [^ksa-universe] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L954-L963)
- **Why differencing first.** Ecliptic positions sit about 1.5e11 m from the origin and move at about 29.8 km/s with the planet. In `r = target - self` and `v = targetVel - selfVel` from the same instant, both cancel exactly, and everything after runs on small numbers.
- **The stock search** (`FlightPlan.TryFindNextClosestApproach`) answers along patched conics for tracked orbiters; it is not a fit for a crossing aircraft seconds away.[^ksa-flightplan] [current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L854-L868)

## Lifecycle and timing

KSArmory runs the assessment once per contact per radar scan inside its simulation step, which `StepOnce` runs from `[StarMapAfterGui]` (`frame:draw-ui`), from a `Program.OnFrameCelestials` prefix when the HUD is hidden (`frame:celestials`), or from `[StarMapAfterOnFrame]`. All three follow `PrepareFrame`, so the craft samples are settled end-of-step values while the next step runs on workers. The mod's own rounds are carried to the same instant (`PositionEcl + VelocityEcl * step`) before being compared, so every contact shares one epoch. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Solve the time of closest approach.** For relative motion `r(t) = r + v t`, the minimum of `|r(t)|` is at `t = -(r . v) / |v|^2`; clamp it to `[0, horizon]` so an opening contact gets `t = 0` and its CPA is its current range:

```csharp
// src/KSArmory/Sim/Vec.cs L85-L91
    public static double TimeOfClosestApproach(double3 r, double3 v, double horizon)
    {
        double vv = Len2(v);
        if (vv < 1e-12) return 0.0;
        double t = -Dot(r, v) / vv;
        return Math.Clamp(t, 0.0, horizon);
    }
```

2. **Feed it a same-instant difference.** The caller differences the contact against the sensor's own sample; nothing absolute crosses into the pure code:

```csharp
// src/KSArmory/Ksa/Radar.cs L238-L241
        var signature = new ThreatModel.ContactSignature(contact.MeanRadius, height, contact.IsMunition);

        if (!ThreatModel.TryAssess(targetPos - originEcl, targetVel - originVel,
                                   boresight, _sensor, signature, out var a)) return;
```

3. **Gate, then classify by miss distance.** Range and cone first, then a minimum speed (things drifting with you), a Doppler notch and a ground-clutter floor; then the CPA, which never rejects, only classifies. A munition must also be closing, because a round leaving a neighbouring mount is born inside the threat radius:

```csharp
// src/KSArmory/Sim/ThreatModel.cs L94-L102
        double tCa = Vec.TimeOfClosestApproach(r, v, sensor.ThreatHorizonSeconds);
        double cpa = Vec.Len(r + v * tCa);
        double range = Math.Sqrt(rangeSquared);

        assessment = new Assessment(
            Range: range,
            ClosingSpeed: closing,
            ClosestApproach: cpa,
            TimeToClosestApproach: tCa,
```

```csharp
// src/KSArmory/Sim/ThreatModel.cs L121-L122
            IsThreat: (cpa <= sensor.ThreatRadius || range <= sensor.ThreatRadius)
                      && (closing > 0.0 || !signature.IsMunition));
```

4. **Rank by time to closest approach, with a stable tie-break.** Non-threats sort last but stay listed; range breaks ties because `List.Sort` is unstable and something downstream watches the first entry:

```csharp
// src/KSArmory/Sim/TrackState.cs L68
    public double Priority => IsThreat ? TimeToClosestApproach : double.MaxValue;
```

```csharp
// src/KSArmory/Sim/ThreatModel.cs L166-L177
    public static void SortByPriority<T>(List<T> tracks) where T : TrackState
        => tracks.Sort(static (a, b) =>
        {
            int byPriority = a.Priority.CompareTo(b.Priority);

            // Range breaks the tie, because there is always a tie: every non-threat has a priority
            // of MaxValue, so a scope holding four of them is comparing four equal keys. List.Sort
            // is not stable, so without a second key their order is whatever the sort happened to
            // do -- and an optical director watches Tracks[0], which then means the instrument
            // picks a different contact with nothing in the world having changed.
            return byPriority != 0 ? byPriority : a.Range.CompareTo(b.Range);
        });
```

5. **Pin it with a headless test.** Because the maths takes no KSA types, a unit test can state the case that motivated it: a target crossing 1 km abeam at 400 m/s is a threat with CPA 1000 m in 20 s:

```csharp
// tests/KSArmory.Tests/ThreatModelTests.cs L80-L94
    [Fact]
    public void ACrossingTargetThatWillPassCloseIsAThreat()
    {
        // Flying across the site, not at it: closing speed is nearly zero at this instant, but
        // it will pass 1 km away. Classifying on closing speed would miss it entirely, which is
        // exactly what the CPA model exists to prevent.
        SensorProfile s = Sensor();
        double3 r = new(1000, 8000, 0);
        double3 v = new(0, -400, 0);

        Assert.True(ThreatModel.TryAssess(r, v, Up, s, Unseen, out var a));
        Assert.True(a.IsThreat);
        Assert.Equal(1000, a.ClosestApproach, 1);
        Assert.Equal(20, a.TimeToClosestApproach, 1);
    }
```

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Sim/Vec.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Vec.cs#L85-L91) | `TimeOfClosestApproach` |
| [src/KSArmory/Sim/ThreatModel.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ThreatModel.cs#L62-L177) | `TryAssess` and `SortByPriority` |
| [src/KSArmory/Sim/TrackState.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TrackState.cs#L68) | `Priority` |
| [src/KSArmory/Ksa/Radar.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Radar.cs#L208-L289) | Caller: same-instant difference, then the track |
| [tests/KSArmory.Tests/ThreatModelTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/ThreatModelTests.cs) | Crossing, opening, munition, sort and salvo cases |

## Variations and gotchas

- **Straight-line only.** A manoeuvring or ballistic contact is judged on its current velocity; gravity curvature over a 40 s horizon is ignored by design. The track's acceleration estimate feeds the lead computation, not the threat test.
- **The clamp works both ways.** Anything already inside the radius is a threat even while receding, unless it is a munition.
- **Closing speed is the wrong key.** Ranking by closing speed misses crossers and over-weights fast receding contacts.
- **Different epochs leak motion.** A craft sample is end-of-step; anything you integrate yourself must be carried to the same instant before differencing.
- **For orbital distances** the straight line is wrong within minutes; use the patch search of [approach 1](find-closest-approach-along-the-flight-plan-1.md).

## See also

- [Overview](overview.md); [Search the flight plan](find-closest-approach-along-the-flight-plan-1.md)
- [CPA threat classification (mod feature)](../../../../mods/ksarmory/features/cpa-threat-classification.md)
- [Understand KSA coordinate frames and epochs](../../flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)
- [Steer by predicted miss and model homing sensors](../../guidance/steer-by-zero-effort-miss/overview.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-flightplan]: KSA.FlightPlan (decompiled, build 2026.10.10.5554)
