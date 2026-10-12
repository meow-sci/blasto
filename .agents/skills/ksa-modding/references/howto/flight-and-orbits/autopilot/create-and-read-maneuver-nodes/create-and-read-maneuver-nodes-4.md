---
type: How-To
title: Chain burns with CalculateBurnPatch so every later pass sits on the orbit the earlier ones produce
description: Build a detached FlightPlan per pass with CalculateBurnPatch and ComputeCompleteTrajectory, feed each pass the previous burn patch, and stop the chain when an intermediate orbit escapes, encounters or impacts
category: flight-and-orbits
group: autopilot
topic: create-and-read-maneuver-nodes
approach: 4
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/multi-pass-burn-splitting]
techniques: [custom-physics]
hooks: []
lifecycle: [frame:draw-ui, frame:apply-solvers]
game_types: [KSA.FlightPlan, KSA.PatchedConic, KSA.PatchTransition, KSA.Orbit, KSA.Vehicle, KSA.UniverseTime]
tags: [maneuver-node, flight-plan, calculate-burn-patch, multi-pass, patched-conic, chain]
---

Outcome: a sequence of burns where burn k is computed on the orbit that burns 0..k-1 produce, with a failure reason when the chain breaks. KSA-AdvancedFlightComputer uses it to
split a maneuver across 2 to 10 passes.[Split a maneuver into passes with a forward-chained flight plan](../../../../mods/ksa-advancedflightcomputer/features/multi-pass-burn-splitting.md) Prerequisite: StarMap.

## Background: how the game does it

`FlightPlan.CalculateBurnPatch(patch, burnTimeFromPe, deltaVVlf, startTime)` returns the `PatchedConic` that follows a burn applied to `prePatch`;
`FlightPlan.ComputeCompleteTrajectory(out errorsEncountered, patchLimit, polynomialOrderN, encounterFilter, resolveImpactsCompletely)` fills the later patches;
`FlightPlan.CreateUninitialized(KeyHash idHash)` makes a plan with no vehicle trajectory behind it. `PatchedConic.EndTransition` is a `PatchTransition`
(`Escape`, `Encounter`, `Impact`, ...).[^ksa-flightplan] [current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L162-L170) [current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L214-L222) [current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L346-L356) The stock flight plan
does the same thing for every real burn, with a worker finishing the incremental terrain search that a detached plan must resolve completely.

## Lifecycle and timing

Planning runs on the main thread: for the preview inside the plan-window draw (`frame:draw-ui`, cached), and for execution when the next pass is recomputed from the vehicle's
actual orbit after the previous pass (`frame:apply-solvers`, in the tick described in [Execute planned burns hands-free with the stock Auto burn](../execute-planned-burns-with-the-stock-autopilot/overview.md)). The mod uses a private
`SequencePerformanceList` for staging because the stock one of the controlled vehicle is recomputed by `PhysicsBubble` on the vehicle worker every step while the Sequence or EngineControl gauge canvas is enabled (both start enabled), so recomputing it on the main thread races that write.
See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Define a step factory**: given the orbit before the burn, a delta-v capacity and the earliest time, return the burn time and VLF delta-v. For apse burns it is
   "next time at the same true anomaly, along the same direction" ([AdvancedFlightComputer/Features/MultiPass/ApseBurnPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/ApseBurnPlanner.cs)).
2. **Build one detached flight plan per pass.** Pass 0 starts on the vehicle's live patch at the burn time; later passes start on the previous burn patch:

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

3. **Run the chain.** For each pass take the step, find the pre-patch (`source.FlightPlan.TryFindPatch` for pass 0, the previous burn patch afterwards), build the plan, then
   continue from `burnPatch.Orbit`:

```csharp
// AdvancedFlightComputer/Features/MultiPass/MultiPassForwardChainPlanner.cs L40-L66, L86-L92
Orbit currentOrbit = source.Orbit;
UniverseTime earliestTime = now;
PatchedConic? prePatch = null;
FlightPlan? lastFp = null;
PatchedConic? lastBurnPatch = null;

for (int i = 0; i < allocations.Length; i++)
{
    PassStep? step = stepFactory(currentOrbit, allocations[i].DvCapacityMs, earliestTime);
    if (step == null)
        return new PassPreviewResult(results.ToArray(), Failed: true,
            $"step factory returned no step for pass {i}");
    string? failure = CheckPreviousPass(lastFp, step.Value.BurnTime, i);
    if (failure != null)
        return new PassPreviewResult(results.ToArray(), Failed: true, failure);
    if (i == 0)
    {
        prePatch = source.FlightPlan.TryFindPatch(step.Value.BurnTime);
        if (prePatch == null || prePatch.PrimaryBody == null)
            return new PassPreviewResult(results.ToArray(), Failed: true,
                $"no parking patch at t={step.Value.BurnTime.Seconds():F0}s");
    }
    else
    {
        prePatch = lastBurnPatch;
    }

// ...
    currentOrbit = burnPatch.Orbit;
    earliestTime = step.Value.BurnTime + NextOccurrenceEpsilonSec;
    lastFp = fp;
    lastBurnPatch = burnPatch;
}

return new PassPreviewResult(results.ToArray(), Failed: false, FailureReason: null);
```

4. **Stop with a reason.** Before accepting the next pass, check that no earlier patch ends before it with `Escape`, `Encounter` or `Impact`; stop when a pass leaves an unbound orbit.
   Very energetic departures from small SOIs therefore clamp to fewer passes.
5. **Allocate delta-v per pass** with the rocket equation over a private staging model (equal burn time or equal delta-v; the final pass absorbs rounding); for plane
   changes convert a pass budget to an angle with `theta = 2 asin(dv / (2 v_perp))` using the velocity perpendicular to the node axis (see
   [AdvancedFlightComputer/Features/MultiPass/Splitter.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/Splitter.cs#L9-L63) and [AdvancedFlightComputer/Features/MultiPass/PlaneChangeBurnPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/PlaneChangeBurnPlanner.cs#L91-L105)).

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/MultiPass/MultiPassForwardChainPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassForwardChainPlanner.cs) | pass-by-pass flight plans |
| [AdvancedFlightComputer/Features/MultiPass/Splitter.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/Splitter.cs) | delta-v allocation and sequence drain |
| [AdvancedFlightComputer/Features/MultiPass/SequenceBurnState.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/SequenceBurnState.cs) | private staging model |
| [AdvancedFlightComputer/Features/MultiPass/MultiPassLoss.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassLoss.cs) | finite-burn loss estimate |

## Variations and gotchas

- **Normal pass commits use the live plan**, not the predicted one: the vehicle has already flown the previous pass and the prediction can be stale.
- **Detached plans have no worker**, so pass `resolveImpactsCompletely: true` or impact detection stays incomplete.
- **Set `ImpactClearanceMargin`** on the detached plan to the vehicle's bounding radius so it matches plans installed on the vehicle.
- Markers use closest approaches from every pass, including intermediate ones (`CalculateTargetNodes(target)`).
- The loss estimate `1 - sin(phi)/phi` with `phi = pi * burnTime / period` is a first-order proxy for a constant-attitude burn centred on an apse.

## See also

- [Overview](overview.md); [impulse formulas](create-and-read-maneuver-nodes-3.md); [Burn.Create queued](create-and-read-maneuver-nodes-1.md)
- [Split a maneuver into passes with a forward-chained flight plan](../../../../mods/ksa-advancedflightcomputer/features/multi-pass-burn-splitting.md)
- [Execute planned burns hands-free with the stock Auto burn](../execute-planned-burns-with-the-stock-autopilot/overview.md)

[^ksa-flightplan]: KSA.FlightPlan (decompiled, build 2026.10.10.5554)
