---
type: Mod Feature
title: Split a maneuver into passes with a forward-chained flight plan
description: Allocate a maneuver's delta-v to N passes by equal burn time or equal delta-v from a private staging model, then chain FlightPlan.CalculateBurnPatch pass by pass so every later pass is placed on the orbit the earlier passes produce
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [custom-physics]
hooks: []
lifecycle: [frame:draw-ui, frame:apply-solvers]
game_types:
  - KSA.FlightPlan
  - KSA.PatchedConic
  - KSA.Orbit
  - KSA.SequencePerformanceList
  - KSA.SequencePerformance
  - KSA.PatchTransition
tags: [multi-pass, oberth, finite-burn-loss, flight-plan, staging-model, maneuver-planning]
---

A long burn far from periapsis wastes delta-v. Multi-pass splits a maneuver (Hohmann, set apse, match
or set inclination, circularize) into 2-10 shorter burns on successive orbits, each near the optimal
point, the technique real kick stages use for lunar injection. Planning has three parts: how much
delta-v each pass gets, where each pass happens on the orbit left by the previous one, and when the
split stops being possible.

## How it works

### Intents

Each maneuver kind is an `IManeuverIntent` (`ApseIntent`, `CircularizeIntent`, `MatchInclinationIntent`,
`SetInclinationIntent`, `HohmannTransferIntent`). An intent stores the goal (not the UI input),
computes the remaining maneuver from the vehicle's current state, says whether the goal is already
satisfied, recomputes the next pass at execution time, and serializes itself to TOML with a stable
`Kind` ([AdvancedFlightComputer/Features/MultiPass/IManeuverIntent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/IManeuverIntent.cs)).

### Allocation from a private staging model

- `SequenceBurnState.Analyze(vehicle)` builds its own `SequencePerformanceList(vehicle.Parts)` and
  calls `RecomputeForFlight(0f)`, so the game worker's shared result is not touched; per sequence it
  keeps start mass, fuel mass, mass flow and exhaust velocity (from Isp)
  ([AdvancedFlightComputer/Features/MultiPass/SequenceBurnState.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/SequenceBurnState.cs#L14-L60)).
- `Splitter.Allocate(totalDv, passes, mode, state)` runs a `SequenceDrain` that burns through the
  sequences with the rocket equation. **EqualBurnTime** first computes the total burn time and gives
  each pass the same time (Oberth-optimal for raising apoapsis); **EqualDv** splits delta-v evenly. The
  final pass absorbs rounding, and an allocation is never scaled up past what the fuel delivers
  ([AdvancedFlightComputer/Features/MultiPass/Splitter.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/Splitter.cs#L9-L63)).
- Plane changes convert each pass's delta-v budget into a rotation angle with
  `theta = 2 asin(dv / (2 v_perp))`, using only the velocity component perpendicular to the node axis,
  and pass `theta / fullAngle` as the fraction to the quick-tool math
  ([AdvancedFlightComputer/Features/MultiPass/PlaneChangeBurnPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/PlaneChangeBurnPlanner.cs#L91-L105)).

### Forward chain

`MultiPassForwardChainPlanner.PlanForwardChain` takes a step factory "orbit before the burn ->
(burn time, delta-v VLF)". Pass 0 starts on the vehicle's live patch at the burn time; every later pass
starts on the burn patch of the previous one. For each pass it builds a detached `FlightPlan`:
`CalculateBurnPatch(prePatch, timeSincePe, dvVlf, burnTime)`, `ComputeCompleteTrajectory(..., resolveImpactsCompletely: true)`
(detached plans have no worker to finish the incremental terrain search) and
`CalculateTargetNodes(target)` for markers ([AdvancedFlightComputer/Features/MultiPass/MultiPassForwardChainPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassForwardChainPlanner.cs#L31-L148)).
The apse planner's factory is simply "next time at the same true anomaly, along the same direction"
([AdvancedFlightComputer/Features/MultiPass/ApseBurnPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/ApseBurnPlanner.cs)).

### When splitting fails

The chain stops with a reason when an intermediate pass's plan escapes the SOI, encounters another
body or impacts before the next pass (`PatchTransition.Escape`/`Encounter`/`Impact`), or when a pass
leaves an unbound orbit. Very energetic departures from small SOIs therefore clamp to fewer passes.

### Benefit estimate

`MultiPassLoss.FiniteBurnLossFraction(burnRatio) = 1 - sin(phi)/phi` with `phi = pi * burnTime / period`
(Robbins/sinc steering loss for a constant-inertial-attitude burn centered on an apse), saturating at 1
for a burn longer than the orbit; it is used as a first-order proxy for plane changes too
([AdvancedFlightComputer/Features/MultiPass/MultiPassLoss.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassLoss.cs)).

## Lifecycle and timing

Planning runs on the main thread: for the preview inside the plan-window draw (`frame:draw-ui`, cached,
see [multi-pass preview rendering](multi-pass-preview-rendering.md)), and for execution in the
[post-solver vehicle tick](post-solver-vehicle-tick.md) when the next pass is recomputed from the
vehicle's actual orbit (`frame:apply-solvers`). The private `SequencePerformanceList` matters for
threading: the stock one of the controlled vehicle is recomputed by `PhysicsBubble` on the vehicle worker every
step while the Sequence or EngineControl gauge canvas is enabled (both start enabled), so recomputing it on the
main thread races that write.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/MultiPass/IManeuverIntent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/IManeuverIntent.cs) | Intent contract |
| [AdvancedFlightComputer/Features/MultiPass/SequenceBurnState.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/SequenceBurnState.cs) | Private staging model |
| [AdvancedFlightComputer/Features/MultiPass/Splitter.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/Splitter.cs) | Delta-v allocation and sequence drain |
| [AdvancedFlightComputer/Features/MultiPass/MultiPassForwardChainPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassForwardChainPlanner.cs) | Pass-by-pass flight plans |
| [AdvancedFlightComputer/Features/MultiPass/ApseBurnPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/ApseBurnPlanner.cs) | Apse step factory |
| [AdvancedFlightComputer/Features/MultiPass/PlaneChangeBurnPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/PlaneChangeBurnPlanner.cs) | Plane-change step factory |
| [AdvancedFlightComputer/Features/MultiPass/MultiPassLoss.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassLoss.cs) | Finite-burn loss estimate |
| [tests/AdvancedFlightComputer.HarnessTests/Features/MultiPass/SequenceBurnStateTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/MultiPass/SequenceBurnStateTest.cs) | Headless test of the staging model |

## Snippets

```csharp
// AdvancedFlightComputer/Features/MultiPass/MultiPassForwardChainPlanner.cs L131-L148
    internal static (FlightPlan fp, PatchedConic burnPatch) BuildPassFlightPlan(
        Vehicle source, PatchedConic prePatch, UniverseTime burnTime, double3 dvVlf,
        IOrbiter? encounterFilter = null)
    {
        UniverseTime timeSincePe = prePatch.Orbit.GetTimeSincePeriapsisThisOrbit(burnTime);
        FlightPlan fp = FlightPlan.CreateUninitialized(source.Hash);
        // Use the same terrain clearance as plans installed on the vehicle.
        fp.ImpactClearanceMargin = source.BoundingSphereRadiusBody;
        PatchedConic burnPatch = fp.CalculateBurnPatch(prePatch, timeSincePe, dvVlf, burnTime);
        fp.Patches.Add(burnPatch);
        // Detached plans have no worker to finish the incremental terrain search.
        fp.ComputeCompleteTrajectory(out _, FlightPlanPatchLimit, FlightPlanPolynomialOrder,
            encounterFilter, resolveImpactsCompletely: true);
        // Markers use closest approaches from every pass, including intermediate passes.
        if (source.Target != null)
            fp.CalculateTargetNodes(source.Target);
        return (fp, burnPatch);
    }
```

## Game APIs touched

| Member | Use |
|---|---|
| `FlightPlan.CalculateBurnPatch` ([current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L399)), `ComputeCompleteTrajectory`, `CalculateTargetNodes` ([current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L292)) | Detached per-pass plans |
| `PatchedConic.EndTransition`, `PatchTransition` ([current/decomp/KSA/PatchTransition.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PatchTransition.cs)) | Detect escape, encounter, impact between passes |
| `SequencePerformanceList(PartTree)`, `RecomputeForFlight(float)`, `PerformanceSequences` ([current/decomp/KSA/SequencePerformanceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePerformanceList.cs#L69-L103)) | Private staging performance |
| `Orbit.TimeOfTrueAnomaly`, `GetTimeSincePeriapsisThisOrbit`, `IsBound` ([current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs)) | Pass timing and validity |

## Pitfalls and version notes

- Sequence performance for a non-active sequence takes thrust from ignition and mass flow from the
  paced first phase, so their ratio is not an exhaust velocity when a solid motor burns; AFC uses Isp.
- A chained preview is only valid until the vehicle flies; execution recomputes each pass from the
  live orbit rather than replaying the preview.
- Multi-pass cannot start on a pending burn's trajectory (a chained planning basis).

## See also

- [Multi-pass execution](multi-pass-execution.md)
- [Multi-pass Hohmann schedule](multi-pass-hohmann-schedule.md)
- [Multi-pass preview rendering](multi-pass-preview-rendering.md)
- [Apse and inclination quick tools](apse-and-inclination-quick-tools.md)
- [Chain burns with CalculateBurnPatch so every later pass sits on the orbit the earlier ones produce](../../../howto/flight-and-orbits/autopilot/create-and-read-maneuver-nodes/create-and-read-maneuver-nodes-4.md) - how-to article demonstrating this feature
- [Allocate a long burn across passes from a private staging model](../../../howto/flight-and-orbits/autopilot/execute-planned-burns-with-the-stock-autopilot/execute-planned-burns-with-the-stock-autopilot-2.md) - how-to article demonstrating this feature

