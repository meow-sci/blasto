---
type: How-To
title: Shift a same-parent window and re-solve Lambert for a multi-pass Hohmann departure
description: Raise the apoapsis in periapsis kicks whose orbit periods sum to a whole number of parking periods so the last kick lands on the stock window, and move the whole schedule forward when the window is too close
category: flight-and-orbits
group: transfers
topic: compute-transfer-windows
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/multi-pass-hohmann-schedule]
techniques: [harmony-transpiler, custom-physics]
hooks: [harmony-transpiler KSA.TransferPlanner.DrawPlanWindow]
lifecycle: [frame:draw-ui, frame:apply-solvers]
game_types: [KSA.OrbitalTransfers, KSA.Orbit, KSA.TransferPlanner, KSA.Vehicle, KSA.UniverseTime]
tags: [hohmann, multi-pass, lambert, parking-orbit, window-shift, kick-schedule]
---

Outcome: a stock Hohmann transfer (LEO to Luna, LEO to Mars) whose departure burn is split across up to 10 passes: N-1 prior kicks at periapsis that raise the apoapsis step by step, then the final kick that leaves on the transfer at the time and in the direction stock's porkchop chose.
KSA-AdvancedFlightComputer does it by choosing the prior orbits' periods so their sum is a whole number of parking periods.[Multi-pass Hohmann departures with a period schedule and a shifted window](../../../../mods/ksa-advancedflightcomputer/features/multi-pass-hohmann-schedule.md) Prerequisites: StarMap and Harmony.

## Background: how the game does it

A tangential kick at periapsis keeps the periapsis point fixed in CCI; if the prior orbits have periods `K_i * T_park`, the vehicle is back at the same periapsis after each one. `OrbitalTransfers.SolveLambert` produces the stock departure and finishes through the private `FinalizeLambert`; for cross-parent transfers that step adjusts the departure time for the hyperbolic escape.[^ksa-orbitaltransfers]
[current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L321-L397) The window itself comes from `AlignmentTime` (see [the phase clock](compute-transfer-windows-1.md)); the planner exposes its min and max time of flight, which the mod uses for the scan. The plan window is the stock `TransferPlanner.DrawPlanWindow`, which the mod modifies with a transpiler (see [Add plan types to the stock Transfer Planner](../add-plan-type-to-the-transfer-planner/overview.md)).

## Lifecycle and timing

Preview planning runs in the plan-window draw (`frame:draw-ui`) and is cached; the Lambert scan only runs when the shift key changes. Execution-time recomputation runs in the post-solver tick (`frame:apply-solvers`); see [Execute planned burns hands-free with the stock Auto burn](../../autopilot/execute-planned-burns-with-the-stock-autopilot/overview.md). See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Round the sum of periods.** Compute `sum(K_i)` and round to an integer; start the first pass at `T_final - round(sum K) * T_park`; adjust the last prior period so the chain ends exactly at `T_final`:

```csharp
// AdvancedFlightComputer/Features/MultiPass/HohmannMultiPassPlanner.cs L149-L215
private static PassPreviewResult? BuildSchedule(
    Vehicle source, HohmannPlanInput input, int startPassIndex,
    PlanGeometry geometry, SequenceBurnState vehicleState, UniverseTime now, SplitMode mode,
    out double[] kSeq, out UniverseTime[] times, out double targetSumPeriods)
{
    kSeq = Array.Empty<double>();
    times = Array.Empty<UniverseTime>();
    targetSumPeriods = 0.0;
    var (mu, rp, tPark, soiLimit, remainingCount, _,
        vTargetXy, aPark, _, _, vpLive) = geometry;
    Orbit currentOrbit = source.Orbit;
    int priors = remainingCount - 1;
    var schedule = BuildRealKSchedule(
        mu, rp, aPark, soiLimit, vpLive, vTargetXy,
        remainingCount, mode, vehicleState);
    if (schedule.K == null)
    {
        PassPlanFailure kind = schedule.Failure ?? PassPlanFailure.Other;
        string detail = kind switch
        {
            PassPlanFailure.FuelShort => "vehicle has insufficient fuel for this N",
            PassPlanFailure.SoiCeiling =>
                "a prior orbit would reach past the parent SOI envelope at this N",
            PassPlanFailure.ParabolicVp => "a prior orbit would reach escape speed at this N",
            _ => "the requested split has no usable prior schedule",
        };
        return Fail(string.Format(CultureInfo.InvariantCulture,
            "vehicle '{0}': K-schedule build failed; {1}",
            source.Id, detail), kind);
    }

    kSeq = schedule.K!;
    UniverseTime timesZero;
    if (startPassIndex == 0)
    {
        double sumRaw = 0.0;
        for (int i = 0; i < priors; i++) sumRaw += kSeq[i];
        targetSumPeriods = Math.Round(sumRaw);
        timesZero = input.TFinal - targetSumPeriods * tPark;
    }
    else
    {
        if (currentOrbit.GetNextPeriapsisTime(now) is not UniverseTime nextPe)
            return Fail($"vehicle '{source.Id}': chained orbit has no next periapsis",
                PassPlanFailure.Other);
        timesZero = nextPe;
        targetSumPeriods = (input.TFinal - timesZero).Seconds() / tPark;
    }
    var failure = AdjustLastPeriod(source, geometry, kSeq, targetSumPeriods);
    if (failure != null) return failure;
    UniverseTime earliestAllowed = now + EarliestPassMarginSec;
    if (timesZero < earliestAllowed)
        return Fail(string.Format(CultureInfo.InvariantCulture,
            "vehicle '{0}': first pass would fire {1:F0}s before now+margin " +
            "(needs ~{2:F1} parking periods = {3:F0}s of warning time); " +
            "pick later porkchop entry",
            source.Id,
            (earliestAllowed - timesZero).Seconds(),
            targetSumPeriods, targetSumPeriods * tPark),
            PassPlanFailure.TimeBudget);
    times = new UniverseTime[remainingCount];
    times[0] = timesZero;
    for (int k = 1; k < remainingCount; k++)
        times[k] = times[k - 1] + kSeq[k - 1] * tPark;
    return null;
}

```

2. **Build the K schedule.** Split the periapsis speed gain `v_target - v_p` with the shared splitter, then for each prior add its delta-v to the periapsis speed and compute `a = 1 / (2/r_p - v_p^2/mu)` and `K = (a / a_park)^1.5`. Refuse with a typed reason when a prior apoapsis would pass 95% of the parent's SOI, when a prior would reach escape speed, or when fuel runs out:

```csharp
// AdvancedFlightComputer/Features/MultiPass/HohmannMultiPassPlanner.cs L627-L658
PassAllocation[] alloc = Splitter.Allocate(totalDv, remainingCount, mode, state);

var K = new double[priors];
double aMaxBySoi = (rp + soiLimit) / 2.0;
double vpMaxPrior = (aMaxBySoi > rp)
    ? Math.Sqrt(Math.Max(0.0, mu * (2.0 / rp - 1.0 / aMaxBySoi))) * 0.999
    : 0.0;

double vpCum = vpLive;
bool reachedCap = false;
for (int k = 0; k < priors; k++)
{
    double targetVp = vpCum + alloc[k].DvCapacityMs;
    if (targetVp > vpMaxPrior)
    {
        // The chain cannot gain more speed after it reaches the SOI cap.
        if (reachedCap)
            return Refuse(PassPlanFailure.SoiCeiling);
        if (vpMaxPrior <= vpCum)
            return Refuse(PassPlanFailure.SoiCeiling);
        targetVp = vpMaxPrior;
        reachedCap = true;
    }
    if (!(targetVp > vpCum))
        return Refuse(PassPlanFailure.FuelShort);
    vpCum = targetVp;
    double term = 2.0 / rp - vpCum * vpCum / mu;
    if (!(term > 0.0))
        return Refuse(PassPlanFailure.ParabolicVp);
    double aPost = 1.0 / term;
    K[k] = Math.Pow(aPost / aPark, 1.5);
}
```

3. **Shift a window that is too close.** If the first pass would fire before now plus a 30 s margin, `K_shift = ceil((earliest - T_final) / T_park)` parking periods moves the start (which keeps the phase geometry only approximately for a target orbiting the same parent), then scan 64 transit times between the planner's min and max time of flight with `OrbitalTransfers.SolveLambert`, keeping the cheapest clean candidate (no impact or other problem before arrival).
4. **Do not shift cross-parent transfers** (LEO to Mars): `FinalizeLambert` adjusts their departure time for the hyperbolic escape, which breaks the integer-period constraint.
5. **Intercept CREATE.** The swapped call site starts a multi-pass execution and returns the first pass's `Burn` for stock to queue; a flyby request is baked into the final pass. A second click while an execution runs is absorbed by a click gate.
6. **Recompute during execution** from the live orbit (`startPassIndex > 0`): the first remaining time is the vehicle's next periapsis and the remaining periods are fitted to `T_final`, because finite burns shift periapsis times; the parking period stays the original one.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/MultiPass/HohmannMultiPassPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannMultiPassPlanner.cs) | K schedule, period fitting, window shift and Lambert scan |
| [AdvancedFlightComputer/Features/MultiPass/HohmannTransferIntent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannTransferIntent.cs) | intent persisted for execution |
| [AdvancedFlightComputer/Features/MultiPass/HohmannMultiPassUI.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannMultiPassUI.cs) | inline section in the stock window |
| [AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs) | Create interception and click gate |

## Variations and gotchas

- **The shift keeps phase geometry only approximately** for a target orbiting the same parent, which is why it re-solves Lambert over a scan.
- **SOI ceiling.** Prior apoapses are capped at 95% of the parent's SOI; the periapsis speed is capped once at the envelope, and a second hit refuses.
- **Typed refusal reasons** (`FuelShort`, `SoiCeiling`, `ParabolicVp`, `TimeBudget`) let the UI clamp the pass count (`LargestFeasibleN`) and say why.
- The result is cached by a key of the transfer geometry.

## See also

- [Overview](overview.md); [phase clock](compute-transfer-windows-1.md)
- [Multi-pass Hohmann departures with a period schedule and a shifted window](../../../../mods/ksa-advancedflightcomputer/features/multi-pass-hohmann-schedule.md)
- [Execute planned burns hands-free with the stock Auto burn](../../autopilot/execute-planned-burns-with-the-stock-autopilot/overview.md)

[^ksa-orbitaltransfers]: KSA.OrbitalTransfers (decompiled, build 2026.10.10.5554)
