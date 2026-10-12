---
type: How-To
title: Allocate a long burn across passes from a private staging model
description: Split a total delta-v into per-pass budgets by equal burn time or equal delta-v using the rocket equation over a private SequencePerformanceList, and place each pass on the orbit the previous one produces
category: flight-and-orbits
group: autopilot
topic: execute-planned-burns-with-the-stock-autopilot
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/multi-pass-burn-splitting]
techniques: [custom-physics]
hooks: []
lifecycle: [frame:draw-ui, frame:apply-solvers]
game_types: [KSA.SequencePerformanceList, KSA.SequencePerformance, KSA.Vehicle, KSA.FlightPlan, KSA.PatchedConic]
tags: [multi-pass, rocket-equation, staging, allocation, sequence-performance]
---

Outcome: the delta-v and burn-time budget of each of N passes, so that a long kick is split into shorter burns near the best point of successive orbits.
KSA-AdvancedFlightComputer's `Splitter` does the allocation.[Split a maneuver into passes with a forward-chained flight plan](../../../../mods/ksa-advancedflightcomputer/features/multi-pass-burn-splitting.md) Prerequisite: StarMap.

## Background: how the game does it

A single long burn far from periapsis wastes delta-v; real kick stages split lunar injection into several perigee burns. The game provides the staging
numbers per sequence in a `SequencePerformanceList` (built from `vehicle.Parts` and refreshed with `RecomputeForFlight(ambientPressure)`). The stock list of the controlled vehicle is recomputed by `PhysicsBubble` on the vehicle worker every step while the Sequence or EngineControl gauge canvas is enabled (`PhysicsBubble.cs` L793-L796), so recomputing it on the main thread races that write and the mod builds its own for planning.[^ksa-sequenceperformancelist]
[current/decomp/KSA/SequencePerformanceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePerformanceList.cs#L103) Pass placement uses detached flight plans (see [Create, chain and read stock maneuver nodes from code](../create-and-read-maneuver-nodes/overview.md), approach 4).

## Lifecycle and timing

Allocation runs on the main thread for the preview inside the plan-window draw (`frame:draw-ui`, cached) and again at execution time when the next pass is
recomputed from the vehicle's actual orbit and mass (`frame:apply-solvers`). Because the staging model is private, it does not touch the game worker's shared result.
See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Snapshot the staging model.** `SequenceBurnState.Analyze(vehicle)` builds its own `SequencePerformanceList(vehicle.Parts)`, calls `RecomputeForFlight(0f)` and keeps, per sequence,
   start mass, fuel mass, mass flow and exhaust velocity from Isp ([AdvancedFlightComputer/Features/MultiPass/SequenceBurnState.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/SequenceBurnState.cs#L14-L60)).
2. **Allocate.** `EqualBurnTime` first computes the total burn time and gives each pass the same time (Oberth-optimal for raising apoapsis); `EqualDv` splits delta-v evenly. The
   final pass absorbs rounding, and an allocation is never scaled up past what the fuel delivers:

```csharp
// AdvancedFlightComputer/Features/MultiPass/Splitter.cs L22-L59
private static PassAllocation[] AllocateByTime(
    double totalDv, int passCount, SequenceBurnState state, SequenceDrain drain)
{
    double totalBurnTime = drain.ByDv(totalDv).timeUsed;
    double tPerPass = totalBurnTime / passCount;
    drain.Reset(state);
    var result = new PassAllocation[passCount];
    double dvSum = 0.0;
    for (int p = 0; p < passCount; p++)
    {
        // The final pass absorbs any remainder from floating point rounding.
        double tBudget = p == passCount - 1
            ? Math.Max(0.0, totalBurnTime - tPerPass * (passCount - 1))
            : tPerPass;
        var (dvDelivered, timeUsed) = drain.ByTime(tBudget);
        result[p] = new PassAllocation(dvDelivered, timeUsed);
        dvSum += dvDelivered;
    }
    // Do not scale an allocation up to the requested delta v when fuel is insufficient.
    if (dvSum > 1e-9)
    {
        double scale = Math.Min(1.0, totalDv / dvSum);
        for (int p = 0; p < passCount; p++)
            result[p] = result[p] with { DvCapacityMs = result[p].DvCapacityMs * scale };
    }
    return result;
}

private static PassAllocation[] AllocateByDv(double totalDv, int passCount, SequenceDrain drain)
{
    var result = new PassAllocation[passCount];
    double dvPerPass = totalDv / passCount;
    for (int p = 0; p < passCount; p++)
    {
        double dvBudget = p == passCount - 1
            ? Math.Max(0.0, totalDv - dvPerPass * (passCount - 1))
            : dvPerPass;
        var (dvDelivered, timeUsed) = drain.ByDv(dvBudget);
```

3. **Convert plane-change budgets to angles** with `theta = 2 asin(dv / (2 v_perp))` using only the velocity perpendicular to the node axis, and pass `theta / fullAngle` as the fraction to the quick-tool math
   ([AdvancedFlightComputer/Features/MultiPass/PlaneChangeBurnPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/PlaneChangeBurnPlanner.cs#L91-L105)).
4. **Place each pass on the orbit the previous one leaves**, via the forward chain (see [Create, chain and read stock maneuver nodes from code](../create-and-read-maneuver-nodes/overview.md)). The chain stops with a reason when an intermediate pass escapes the SOI,
   encounters another body, impacts, or leaves an unbound orbit.
5. **Estimate the benefit** with the finite-burn steering loss `1 - sin(phi)/phi`, `phi = pi * burnTime / period`, saturating at 1 for a burn longer than the orbit.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/MultiPass/Splitter.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/Splitter.cs) | delta-v allocation and sequence drain |
| [AdvancedFlightComputer/Features/MultiPass/SequenceBurnState.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/SequenceBurnState.cs) | private staging model |
| [AdvancedFlightComputer/Features/MultiPass/MultiPassLoss.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassLoss.cs) | finite-burn loss estimate |
| [tests/AdvancedFlightComputer.HarnessTests/Features/MultiPass/SequenceBurnStateTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/MultiPass/SequenceBurnStateTest.cs) | headless test of the staging model |

## Variations and gotchas

- **Maximum 10 passes** (`Splitter.MaxPasses`); fewer may be possible if the chain fails.
- **No usable engines** falls back to an equal-delta-v split (`EqualDvFallback`).
- **Never recompute the stock `SequencePerformanceList`** from the main thread while the worker may be filling it; build a private one.
- Later sequence masses already account for jettison, so the drain keeps them apart from the current sequence end mass.

## See also

- [Overview](overview.md); [state machine](execute-planned-burns-with-the-stock-autopilot-1.md)
- [Split a maneuver into passes with a forward-chained flight plan](../../../../mods/ksa-advancedflightcomputer/features/multi-pass-burn-splitting.md)
- [Create, chain and read stock maneuver nodes from code](../create-and-read-maneuver-nodes/overview.md)

[^ksa-sequenceperformancelist]: KSA.SequencePerformanceList (decompiled, build 2026.10.10.5554)
