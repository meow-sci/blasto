---
type: Mod Feature
title: Multi-pass Hohmann departures with a period schedule and a shifted window
description: Raise apoapsis in periapsis kicks whose orbital periods sum to a whole number of parking periods so the final kick lands on the transfer window, shifting a same-parent window forward and re-solving Lambert when there is not enough time
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [harmony-transpiler, custom-physics]
hooks: [harmony-transpiler KSA.TransferPlanner.DrawPlanWindow]
lifecycle: [frame:draw-ui, frame:apply-solvers]
game_types: [KSA.OrbitalTransfers, KSA.Orbit, KSA.FlightPlan, KSA.TransferPlanner, KSA.Burn]
tags: [multi-pass, hohmann, lambert, departure-window, oberth, schedule]
---

For a stock Hohmann transfer (LEO to Luna, LEO to Mars) the departure burn can be split across up to
10 passes: N-1 "prior" kicks at periapsis that raise the apoapsis step by step, then the final kick
that leaves on the transfer. The final kick must still happen at the time and in the direction stock's
porkchop chose. AFC gets that by choosing the prior orbits' periods so their sum is a whole number of
parking periods, and, for transfers inside one SOI, by moving the whole schedule forward a few parking
periods when the window is too close.

## How it works

### Why periods line up

A tangential kick at periapsis keeps the periapsis point fixed in CCI. If the prior orbits have periods
`K_i * T_park`, the vehicle is back at the same periapsis after each one, and after
`sum(K_i)` parking periods in total. AFC rounds `sum(K_i)` to an integer and starts the first pass at
`T_final - round(sum K) * T_park`; the last prior period is then adjusted so the chain ends exactly at
`T_final` ([AdvancedFlightComputer/Features/MultiPass/HohmannMultiPassPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannMultiPassPlanner.cs#L149-L215)).

### Building the K schedule

`BuildRealKSchedule` splits the periapsis speed gain `v_target - v_p` with the shared
[splitter](multi-pass-burn-splitting.md), then for each prior adds its delta-v to the periapsis speed and
computes `a = 1 / (2/r_p - v_p^2/mu)` and `K = (a / a_park)^1.5`. It refuses with a typed reason when
a prior apoapsis would pass 95 % of the parent's SOI (the periapsis speed is capped once at the SOI
envelope), when a prior would reach escape speed, or when fuel runs out
([AdvancedFlightComputer/Features/MultiPass/HohmannMultiPassPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannMultiPassPlanner.cs#L615-L661)). `LargestFeasibleN`
lets the UI clamp the pass count.

### Shifting a same-parent window

If the first pass would have to fire before now plus a 30 s margin, `PrepareShiftedInput` computes
`K_shift = ceil((earliest - T_final) / T_park)`, moves the start by that many parking periods (which
keeps the phase geometry for a target orbiting the same parent only approximately), and scans 64
transit times between the planner's min and max time of flight with stock's
`OrbitalTransfers.SolveLambert`, keeping the cheapest "clean" candidate (no impact or other problem
before arrival) or, failing that, the cheapest one with an advisory. The shift and any advisory are
shown in the plan window ([AdvancedFlightComputer/Features/MultiPass/HohmannMultiPassPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannMultiPassPlanner.cs#L351-L516)).
Cross-parent transfers (LEO to Mars) are not shifted, because `OrbitalTransfers.FinalizeLambert`
adjusts their departure time for the hyperbolic escape, which breaks the integer-period constraint.
The result is cached by a key of the transfer geometry.

### UI and Create

`HohmannMultiPassUI.DrawInline` is injected into the stock plan window by the
[plan window transpiler](plan-window-transpiler-injection.md) and draws the PASSES slider, split mode
and readouts. On CREATE, the swapped call site (`HohmannCreateInterceptor.CreateMaybeMultiPass`) starts
a multi-pass execution and returns the first pass's `Burn` for stock to queue; a flyby request is baked
into the final pass. A second click while an execution runs is absorbed by the click gate
([AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs)).

### Recomputing during execution

Later passes are recomputed from the live orbit with `startPassIndex > 0`: the first remaining time is
the vehicle's next periapsis and the remaining periods are fitted to `T_final`, because finite burns
shift periapsis times; the parking period stays the original one.

## Lifecycle and timing

Preview planning runs in the plan-window draw (`frame:draw-ui`) and is cached; the Lambert scan only
runs when the shift key changes. Execution-time recomputation runs in the post-solver tick
(`frame:apply-solvers`), see [multi-pass execution](multi-pass-execution.md).

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/MultiPass/HohmannMultiPassPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannMultiPassPlanner.cs) | K schedule, period fitting, window shift and Lambert scan |
| [AdvancedFlightComputer/Features/MultiPass/HohmannTransferIntent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannTransferIntent.cs) | Intent persisted for execution |
| [AdvancedFlightComputer/Features/MultiPass/HohmannMultiPassUI.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannMultiPassUI.cs) | Inline section in the stock window |
| [AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/HohmannCreateInterceptor.cs) | Create interception and click gate |
| [tests/AdvancedFlightComputer.HarnessTests/Features/MultiPass/MultiPassFallbackTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/MultiPass/MultiPassFallbackTest.cs) | Headless test of fallbacks |

## Snippets

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

## Game APIs touched

| Member | Use |
|---|---|
| `OrbitalTransfers.SolveLambert(TransferInfo, ref TransferData)` ([current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L321)) | Re-solve at the shifted start |
| `OrbitalTransfers.FinalizeLambert` (private, [current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L353)) | Cross-parent departure adjustment (why no shift) |
| `OrbitalTransfers.BuildFlightPlan` ([current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L298)) | Apoapsis target of the shifted departure |
| `Orbit.GetNextPeriapsisTime`, `Periapsis`, `Parent.SphereOfInfluence` ([current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs)) | Schedule anchor and SOI envelope |

## Pitfalls and version notes

- Rounding the period sum can make one pass count fail while a larger one succeeds; search for the
  largest feasible N instead of assuming monotonic behavior.
- A prior at K close to 1 leaves the periapsis direction undefined on a circular orbit; AFC floors K at
  1.01.
- Shifting changes the transfer: the shifted departure's delta-v and arrival differ from stock's entry
  and are shown separately.

## See also

- [Multi-pass burn splitting](multi-pass-burn-splitting.md)
- [Multi-pass execution](multi-pass-execution.md)
- [Flyby periapsis targeting](flyby-periapsis-targeting.md)
- [Plan window transpiler injection](plan-window-transpiler-injection.md)
- [Shift a same-parent window and re-solve Lambert for a multi-pass Hohmann departure](../../../howto/flight-and-orbits/transfers/compute-transfer-windows/compute-transfer-windows-2.md) - how-to article demonstrating this feature

