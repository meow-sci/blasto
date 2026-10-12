---
type: How-To
title: Classify threats by closed-form closest approach with median-filtered acceleration
description: Rank contacts by time to closest approach from paired relative samples, with notch, cone and clutter rejects ahead of the CPA and a median window guarding the accelerometer
category: vehicles-and-parts
group: craft-tracking
topic: target-and-gate-automated-weapons
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/cpa-threat-classification]
techniques: [closed-form-geometry]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Vehicle, KSA.Universe, KSA.CelestialSystem, KSA.Program, KSA.Part, KSA.Celestial]
tags: [targeting, cpa, threat, closed-form, median]
---
Outcome: a contact becomes a *threat* when, flying straight on at its current relative velocity, it will pass within `ThreatRadius` of the system inside `ThreatHorizonSeconds`, not when it is merely closing. A site engages a drone crossing a kilometre abeam
(near-zero closing speed) and ignores an airliner that will pass 9 km wide. Threats are ranked by time to closest approach.

Prerequisite: StarMap. All of it is pure arithmetic in a layer with no KSA types, so it is unit-tested headlessly. Demonstrated by [ksarmory/cpa-threat-classification](../../../../mods/ksarmory/features/cpa-threat-classification.md).

## Background: how the game does it

The game gives you state vectors, not a threat model. For a craft you read `GetPositionEcl()` and `GetVelocityEcl()` ([overview](overview.md)); for acceleration, `Vehicle.AccelerationBody` is an accelerometer reading (no gravity), so KSArmory rotates it by `Body2Cce` and adds gravity back
(`KsaWorld.AccelerationEcl`). Position and velocity are in the ecliptic frame, so a 1.5e11 m offset and 29.8 km/s common motion are present in both operands and cancel when you subtract the platform's own sample taken at the same instant.

## Lifecycle and timing

`TryAssess` runs once per contact per scan inside `WeaponSystem.Update`, inside the fire loop of the mod's `StepSimulation`, which only runs on a frame where a new engine step was consumed. The `dt` that feeds the acceleration window is that step. `StepSimulation` is reached through `StepOnce` under `[StarMapAfterGui]` (`frame:draw-ui`; postfix on `Program.OnDrawUiViewports`, only while the HUD is shown), or with the UI hidden from a Harmony prefix on `Program.OnFrameCelestials` (`frame:celestials`),
or from `[StarMapAfterOnFrame]` as the last fallback (`frame:after-on-frame`). All run after `PrepareFrame` applied the vehicle step and dispatched the next, so positions and velocities are end-of-step values at one instant. Reading vehicle state here is safe (the workers write their own copies);
whatever is decided only reaches a craft in a later step. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Take target-minus-system position `r` and velocity `v`** (both ecliptic) from one instant, then reject cheaply and in order: out of range for the contact's size, outside the search cone, drifting with the site (`|v| < MinTargetSpeed`), inside the Doppler notch, down in ground return. Detection range scales with the fourth root
   of the contact's cross-section over a reference. None of these reject on CPA.
2. **Closest approach, clamped to a horizon.** `tCa = -dot(r, v) / |v|^2` clamped to `[0, horizon]`, zero for a stationary pair:

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

3. **Build the assessment.** CPA distance is `|r + v*tCa|`; closing speed is `-dot(v, unit(r))`:

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

4. **Decide.** A threat passes within the radius (or is already inside it) and is closing, unless it is not a munition; a round leaving a neighbouring mount is born inside the threat radius, so for rounds "must be closing" is the extra rule:

```csharp
// src/KSArmory/Sim/ThreatModel.cs L121-L122
IsThreat: (cpa <= sensor.ThreatRadius || range <= sensor.ThreatRadius)
          && (closing > 0.0 || !signature.IsMunition));
```

5. **Rank.** Priority is time to closest approach for a threat and `double.MaxValue` otherwise; ties break on range because `List.Sort` is unstable and an optical director watches `Tracks[0]`.
6. **Believe the accelerometer unless a median disagrees.** Keep a 0.5 s window (at least 5 samples) of finite-difference accelerations, take the per-axis median, and use the accelerometer unless the two differ by more than 3 m/s^2, which catches impulses and tumbling frames without the quarter-window lag of the median. Acceleration feeds the lead computation, not the threat test.
7. **Pin the behaviour with a test.** The crossing case is the reason the model exists:

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

- [src/KSArmory/Sim/ThreatModel.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ThreatModel.cs#L60-L130)[^ksarmory-threatmodel] - `TryAssess`, `SortByPriority`, `IndexOfFirstThreat`, `InEngagementEnvelope`, `HasSalvoCapacity`.
- [src/KSArmory/Sim/Vec.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Vec.cs#L80-L95)[^ksarmory-vec] and [tests/KSArmory.Tests/ThreatModelTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/ThreatModelTests.cs#L70-L100)[^ksarmory-threatmodeltests].

## Variations and gotchas

- **Straight-line CPA.** A manoeuvring or ballistic contact is judged on current velocity only; gravity curvature over a 40 s horizon is ignored by design.
- **The horizon clamps both ways.** An opening contact gets `tCa = 0`, so anything already inside the radius is a threat even while receding (unless it is a munition).
- **Live tuning is shared.** Sliders write the sensor profile instance that every system fitted with it shares.
- **Diagnostics can disagree** with the live model if they re-derive the verdict with raw range and no notch, clutter or munition rule.
- **Unit-test in a game-free layer.** The geometry has no engine types, so the whole ladder is tested without KSA.

## See also

- [Overview](overview.md); [Article 2](target-and-gate-automated-weapons-2.md)
- [ksarmory/cpa-threat-classification](../../../../mods/ksarmory/features/cpa-threat-classification.md)
- [Track which craft carry your part](../track-and-classify-craft/overview.md)

[^ksarmory-threatmodel]: KSArmory: src/KSArmory/Sim/ThreatModel.cs
[^ksarmory-vec]: KSArmory: src/KSArmory/Sim/Vec.cs
[^ksarmory-threatmodeltests]: KSArmory: tests/KSArmory.Tests/ThreatModelTests.cs
