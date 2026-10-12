---
type: Mod Feature
title: Closest-point-of-approach threat classification
description: Contacts are classed as threats by where straight-line relative motion takes them within a horizon, not by closing speed, then ranked by time to closest approach, with a closing-only rule for munitions and a median-filtered acceleration kept beside each track
mod: ksarmory
category: vehicles-and-parts
techniques: [starmap-lifecycle, harmony-prefix, closed-form-geometry, relative-frame-math, median-filter]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types: [KSA.Vehicle, KSA.KinematicMeasurements, KSA.Universe]
tags: [threat, cpa, closest-approach, ranking, track, acceleration, filter, headless-tests]
---

A radar contact in KSArmory becomes a *threat* when, flying straight on at its current relative
velocity, it will pass within `ThreatRadius` of the system inside `ThreatHorizonSeconds`, not
when it is merely closing. That is what lets a site engage a drone crossing a kilometre abeam
(near-zero closing speed) while ignoring an airliner that will pass 9 km wide. Threats are ranked
by time to closest approach, and rounds in flight get one extra rule: they must be closing,
because a missile leaving a neighbouring mount is born inside the threat radius.[^ksarmory-threatmodel]

All of it is pure arithmetic over relative position and velocity in `Sim/` (no KSA types), so it
is unit-tested headlessly, and the ecliptic frame's 1.5e11 m offset and 29.8 km/s common motion
cancel before any of it runs.[^ksarmory-threattests]

## How it works

`ThreatModel.TryAssess(r, v, boresight, sensor, signature, out a)` takes target-minus-system
position `r` and velocity `v` (Ecl) and runs, in order:[^ksarmory-threatmodel]

| Step | Test | Rejects |
|---|---|---|
| 1 | `|r|² > reach²` or `< 1 m²` | Out of range for the contact's size, or zero range (no direction) |
| 2 | `dot(unit(r), boresight) < cos(cone)` | Outside the search cone |
| 3 | `|v| < MinTargetSpeed` | Drifting with the site (docked craft) |
| 4 | `|closing| < NotchSpeed` | Doppler notch (absolute: opening and closing alike) |
| 5 | `height < ClutterFloorMetres` | Down in ground return (mean sphere, no terrain fetch) |
| 6 | CPA | Classify, never rejects |

`reach` is `RadarSignature.DetectionRange`: the profile's range scaled by the **fourth root** of the
contact's cross-section (`π·MeanRadius²`) over `ReferenceCrossSectionM2`, or the raw range when no
reference is set.[^ksarmory-radarsignature]

The CPA itself:

- `tCa = Vec.TimeOfClosestApproach(r, v, horizon)` = `-dot(r, v) / |v|²`, clamped to
  `[0, ThreatHorizonSeconds]`; 0 for a stationary pair.[^ksarmory-vec]
- `cpa = |r + v·tCa|`; `closing = -dot(v, unit(r))` (positive is inbound).
- `IsThreat = (cpa <= ThreatRadius || range <= ThreatRadius) && (closing > 0 || !IsMunition)`.
  The second disjunct is redundant while `tCa` is clamped at 0 (then `cpa <= range`); it is kept
  deliberately, and the test `ClosestApproachNeverExceedsCurrentRange` pins the invariant.
- Acceleration is **not** used in the CPA: the threat test is straight-line. Acceleration is carried
  on the track for the lead computation only.

`Radar.Consider` then ANDs in `!contact.IsDebris` and `IffPolicy.MayEngage(allegiance)`, so a
friendly or wreckage track is listed but never a threat.[^ksarmory-radar]

**Ranking** (`TrackState.Priority`, `ThreatModel.SortByPriority`):

- `Priority = IsThreat ? TimeToClosestApproach : double.MaxValue`; non-threats sort last but stay
  on the list for the panel.[^ksarmory-trackstate]
- Ties (every non-threat) break on range, because `List.Sort` is unstable and an optical director
  watches `Tracks[0]`.
- `IndexOfFirstThreat` picks the lock after sorting; `IndexOfMostUrgent` (order-independent, soonest
  `tCa` among threats) aims the turret while no lock exists (WeaponSystem.cs L2188-L2193).[^ksarmory-weaponsystem]

**Engagement rules built on the same state** (consumed by fire control):

| Helper | Rule |
|---|---|
| `MayEngage(track, iff)` | Allegiance check, before any geometry |
| `InEngagementEnvelope(track, munition)` | `MinRange <= Range <= MaxRange` of the round, not the radar |
| `HasSalvoCapacity(track, n)` | `RoundsAssigned < RoundsPerTarget`; counts rounds still in the air, so a miss frees capacity |
| `InSensorVolume(r, boresight, sensor, sig)` | Range and cone only, for command uplink: can the launcher still see it |

**Acceleration estimate.** `TrackState.AccelerationEcl` comes from `AccelerationEstimate.Believe`.
The engine's `Vehicle.AccelerationBody` is an accelerometer reading (no gravity); `KsaWorld.AccelerationEcl`
rotates it by `Body2Cce` and adds gravity back. The estimate keeps a 0.5 s window (at least 5
samples) of finite-difference accelerations and takes the per-axis median; it believes the
accelerometer unless the two differ by more than 3 m/s², which catches impulses and tumbling
frames without the quarter-window lag of the median.[^ksarmory-accelerationestimate][^ksarmory-ksaworld]

**Settings.** Per-sensor `ThreatRadius`, `ThreatHorizonSeconds`, `LockSeconds`, `MinTargetSpeed`
default to 8000 m, 40 s, 1.5 s, 15 m/s, are set per profile in `Arsenal` or pack XML, and are live
sliders in the panel's tuning tree (100-10000 m, 5-120 s, 0-5 s, 0-200 m/s).[^ksarmory-uituning][^ksarmory-sensorprofile]

| Built-in profile | Threat radius | Horizon | Lock |
|---|---|---|---|
| AIM-9 seeker head | 6000 m | 30 s | 1.0 s |
| AIM-120 seeker head | 30000 m | 60 s | 1.4 s |
| VPS-2 search and track | 2500 m | 15 s | 0.6 s |
| Mk 68 fire control | 15700 m | 30 s | 1.2 s |

## Lifecycle and timing

`TryAssess` runs once per contact per scan from `Radar.Consider`, inside `WeaponSystem.Update`,
inside the fire loop of `KSArmoryMod.StepSimulation` (L776), which only runs on a frame where
`KsaWorld.ConsumeSimStep()` returned a new engine step. The `dt` that feeds the acceleration window
is that step, clamped by the round-faithful limit.[^ksarmory-mod]

`StepSimulation` is reached from `StepOnce` under `[StarMapAfterGui]` (postfix on
`Program.OnDrawUiViewports`, Program.cs L2268), or, with the UI hidden, from the Harmony prefix on
`Program.OnFrameCelestials` (L2320), or `[StarMapAfterOnFrame]` as the last fallback. Each of these
runs after `PrepareFrame` applied the vehicle step (`Universe.ApplyVehicleSolvers`, Universe.cs
L1922; `_lastSimStep = _nextSimStep`, L1948) and dispatched the next (`ExecuteNextVehicleSolvers`,
Program.cs L2220).[^ksa-program][^ksa-universe]

Consequences for the samples:

- Craft positions, velocities and `AccelerationBody` are the end-of-step values at
  `Universe.GetElapsedTime()` (Universe.cs L2399); the worker writing the next step does not touch
  them until the next `ApplyVehicleSolvers`.[^ksa-vehicle]
- Rounds are carried to the same instant by `CollectAirborne` (`PositionEcl + VelocityEcl * step`),
  so a round contact and a craft contact are compared at one epoch.
- The platform's own position comes from `SampleWorld` earlier in the same pass, so `r` and `v`
  difference two samples of one instant and the common motion cancels exactly.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Sim/ThreatModel.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ThreatModel.cs#L62-L125) | `TryAssess`: volume, notch, clutter, CPA, munition rule |
| [src/KSArmory/Sim/ThreatModel.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ThreatModel.cs#L134-L238) | Detection range, sensor volume, sort, lock pick, envelope, salvo capacity |
| [src/KSArmory/Sim/Vec.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Vec.cs#L85-L91) | `TimeOfClosestApproach` |
| [src/KSArmory/Sim/TrackState.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TrackState.cs) | Track kinematics and `Priority` |
| [src/KSArmory/Sim/AccelerationEstimate.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/AccelerationEstimate.cs) | Median-window check on the accelerometer |
| [src/KSArmory/Sim/RadarSignature.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/RadarSignature.cs) | Fourth-root detection range |
| [src/KSArmory/Ksa/Radar.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Radar.cs#L208-L289) | Caller: builds the `Track` from the assessment |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L2128-L2141) | `AccelerationEcl`: accelerometer plus gravity |
| [src/KSArmory/Ksa/Ui/UiTuning.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiTuning.cs#L95-L118) | Live sliders on the sensor profile |
| [src/KSArmory/Sim/Arsenal.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Arsenal.cs#L560-L720) | Built-in sensor profiles |
| [src/KSArmory/Ksa/Diagnostics.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Diagnostics.cs#L285-L306) | Log replay of the verdict per craft |
| [tests/KSArmory.Tests/ThreatModelTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/ThreatModelTests.cs) | Crossing, opening, munition, sort and salvo cases |
| [tests/KSArmory.Tests/AccelerationEstimateTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/AccelerationEstimateTests.cs) | Kick rejection and lag cases |

## Snippets

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

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `Vehicle.GetPositionEcl`, `Vehicle.GetVelocityEcl` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L954-L963) | `r` and `v` inputs (through `IContact`) |
| `Vehicle.AccelerationBody`, `Vehicle.Body2Cce` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L476-L573) | Accelerometer the estimate checks |
| `KinematicMeasurements.AccelerationBody` | [current/decomp/KSA/KinematicMeasurements.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KinematicMeasurements.cs#L9) | Backing field of `Vehicle.AccelerationBody`[^ksa-kinematicmeasurements] |
| `Universe.GetElapsedTime`, `Universe.ApplyVehicleSolvers` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1948) | Epoch of the sampled kinematics (L2399) |

## Pitfalls and version notes

- **Straight-line CPA.** A manoeuvring or ballistic contact is judged on its current velocity only;
  gravity curvature over a 40 s horizon is ignored by design. The acceleration on the track feeds
  the lead, not the threat test.
- **The horizon clamps both ways.** An opening contact gets `tCa = 0`, so its CPA is its current
  range: anything already inside `ThreatRadius` is a threat even while receding (unless it is a
  munition).
- **Munition rule is a seam on `IContact.IsMunition`.** Without it, per CLAUDE.md, a Phalanx beside a
  Pantsir emptied its belt on the Pantsir's outgoing rounds; the tests
  `ARoundLeavingANeighbouringMountIsNotAThreat` and `ARoundThatHasGonePastStopsBeingAThreat` pin it.
- **Diagnostics disagree with the live model.** `Diagnostics.cs` L297-L306 re-derives the verdict
  with raw `Sensor.Range` and no notch, clutter, size scaling or munition rule, so its log line can
  say `TRACK: threat` for a contact the radar rejected.[^ksarmory-diagnostics]
- **Live tuning is shared.** The sliders write the `SensorProfile` instance, which every system
  fitted with that profile shares.
- **README wording.** README.md L240-L242 says the launcher "commits Rounds per target rounds before
  re-evaluating"; the code (`HasSalvoCapacity`) re-evaluates every step and counts only rounds still
  flying, so a miss frees capacity at once.[^ksarmory-readme]

## See also

- [Radar search cone, masking and lock dwell](./radar-search-and-lock.md)
- [IFF teams and allegiance](./iff-teams-and-allegiance.md)
- [Fire control and target allocation](./fire-control-and-target-allocation.md)
- [Unguided ballistic lead](./unguided-ballistic-lead.md)
- [Sim boundary and headless tests](./sim-boundary-headless-tests.md)
- [Ecliptic frames and epochs](./ecliptic-frames-and-epochs.md)
- [Consume the applied simulation step](./consume-applied-sim-step.md)
- [Use Ecl as the working frame, Ego for anything drawn, and cancel the 29.8 km/s carrier](../../../howto/flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/understand-ksa-coordinate-frames-and-epochs-2.md) - how-to article demonstrating this feature
- [Compute the closest point of approach from straight-line relative motion and rank contacts by time to it](../../../howto/flight-and-orbits/map-view/find-closest-approach-along-the-flight-plan/find-closest-approach-along-the-flight-plan-2.md) - how-to article demonstrating this feature
- [Classify threats by closed-form closest approach with median-filtered acceleration](../../../howto/vehicles-and-parts/craft-tracking/target-and-gate-automated-weapons/target-and-gate-automated-weapons-1.md) - how-to article demonstrating this feature

[^ksarmory-threatmodel]: src/KSArmory/Sim/ThreatModel.cs
[^ksarmory-threattests]: tests/KSArmory.Tests/ThreatModelTests.cs
[^ksarmory-radarsignature]: src/KSArmory/Sim/RadarSignature.cs
[^ksarmory-vec]: src/KSArmory/Sim/Vec.cs
[^ksarmory-radar]: src/KSArmory/Ksa/Radar.cs
[^ksarmory-trackstate]: src/KSArmory/Sim/TrackState.cs
[^ksarmory-weaponsystem]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksarmory-accelerationestimate]: src/KSArmory/Sim/AccelerationEstimate.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-uituning]: src/KSArmory/Ksa/Ui/UiTuning.cs
[^ksarmory-sensorprofile]: src/KSArmory/Sim/SensorProfile.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-diagnostics]: src/KSArmory/Ksa/Diagnostics.cs
[^ksarmory-readme]: README.md
[^ksa-program]: KSA.Program (decompiled)
[^ksa-universe]: KSA.Universe (decompiled)
[^ksa-vehicle]: KSA.Vehicle (decompiled)
[^ksa-kinematicmeasurements]: KSA.KinematicMeasurements (decompiled)
