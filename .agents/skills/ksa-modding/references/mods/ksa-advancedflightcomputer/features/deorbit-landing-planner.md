---
type: Mod Feature
title: Plan a deorbit to a landing site and let stock Auto fly the node
description: A time-sliced Lambert search to an approach point uprange of a rotating site, run as a C# iterator under a per-step and total work budget, then a stock maneuver node whose ignition timing stock computes before AFC arms Auto, followed by braking re-planned from the measured orbit
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [harmony-prefix, custom-physics]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker, reflection KSA.Vehicle._manualControlInputs]
lifecycle: [frame:dispatch-solvers, physics:vehicle-solver]
game_types:
  - KSA.Burn
  - KSA.BurnTarget
  - KSA.FlightComputer
  - KSA.FlightPlan
  - KSA.PatchedConic
  - KSA.Orbit
  - KSA.Universe
  - KSA.ManualControlInputs
tags: [deorbit, landing, lambert, time-slicing, maneuver-node, auto-burn, time-warp]
---

**Deorbit and land** takes a craft in orbit to a chosen surface site: a deorbit burn, a coast, then a
braking burn and a powered descent (G-FOLD or 6-DOF) ending in a terminal hover. The deorbit part shows
two techniques worth copying: running an expensive search a few milliseconds per frame without a
thread, and executing a burn with the **stock** Auto autopilot while using stock's own worker to
compute the burn timing before committing.

## How it works

### The landing phase machine

`LandingPhase` runs per vehicle: `DeorbitPlanning -> DeorbitNodePending -> DeorbitBurn -> TransferCoast`
for a transfer, or `Coast` for a direct approach, then `Prep -> Burn` (braking) `-> GfoldDescent` or 6-DOF
`-> TerminalHover/TerminalCoast/TerminalBrake -> Done` ([AdvancedFlightComputer/Features/Guidance/Modes/Landing.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Landing.cs#L16-L24)).
Upcoming passes over the site are found in closed form (`SitePasses`).

### Time-sliced planning

`DeorbitPlanner` first checks direct braking from the current orbit. If refused, it searches a Lambert
transfer to a point uprange of the rotating site (11 km above it by default), scanning departures and
flight times over the next source orbit (16 x 24 samples), fully checking at most 8 candidates (braking
with UPFG's landing mode, stock propagation, terrain along both flown arcs with a 1 km margin) and
refining the best with golden-section search. Arrival must be between 10 degrees below and 0.5 above
the local horizon unless the player sets an arrival angle. The search is written as an
`IEnumerable<bool>` iterator that yields after every Lambert solve, UPFG step or terrain sample;
`Step(budgetMs = 3)` advances it until the per-call budget or the 500 ms total work limit is spent
([AdvancedFlightComputer/Features/Guidance/Modes/DeorbitPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/DeorbitPlanner.cs#L119-L232)). Planning waits for 1x
warp, an idle engine and a settled orbit.

### Stock flies the node

1. **Load.** `Burn.Create` at the planned time with the delta-v converted to VLF, then
   `FlightComputer.AddBurn` directly (the step runs in the `PrepareWorker` prefix, with no vehicle worker
   running), and verify the node is the loaded target. AFC refuses when another burn is already planned
   ([AdvancedFlightComputer/Features/Guidance/Modes/StockDeorbit.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/StockDeorbit.cs#L111-L148)).
2. **Ask stock for the timing.** With the engine off and the throttle set to the vehicle's minimum,
   wait until a vehicle worker has filled the new `BurnTarget` (its `IgnitionTime` is `EndOfTime` until
   then). Stock's `FlightComputer.UpdateBurnTarget` ignites half the burn before the node at the
   previewed throttle, so the minimum-throttle preview bounds the ignition lead and duration.
3. **Arm only if it fits.** If ignition leaves time for stock's alignment (`BurnTarget.AlignmentStartTime`,
   from `FlightComputer.ConservativeFlipTime`) and the burn ends before braking preparation, restore the
   throttle and give Auto back through `StockBurnMode.GiveBackAuto`; otherwise stop with a reason and
   leave the node in Manual ([AdvancedFlightComputer/Features/Guidance/Modes/StockDeorbit.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/StockDeorbit.cs#L150-L181)).
4. **Warp.** Optional warp to just before stock's preparation start, stopping only the warp AFC started.
5. **Watch.** Completion needs stock's delta-v reversal on the kept `BurnTarget` (the node itself may have
   been removed by stock or [automatic removal](auto-remove-finished-burns.md)); a changed node or target,
   or a player takeover, stops the landing and preserves the player's target.
6. **Braking.** After the burn, braking is planned again from the measured orbit and mass.

## Lifecycle and timing

All of it runs in the per-vehicle `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`, main thread,
see [guidance vehicle step loop](guidance-vehicle-step-loop.md)): the input queue is drained and the
vehicle solver has not been queued, so burn-mode and flight-computer writes are what the next worker
step sees. Burn-plan edits there are not fully isolated: `Program.PrepareFrame` queues the
nearest-orbit job just before `ExecuteNextVehicleSolvers`, and that job's `SetNearestOrbitPoint` walks
the controlled vehicle's `BurnPlan` (`FindNearestBurnPoint` iterates the burn list) on its own worker,
while `FlightComputer.AddBurn` adds to and sorts that list
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2196-L2221), [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L473-L484),
[current/decomp/KSA/BurnPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnPlan.cs#L389-L394)).[^ksa-burnplan] Stock makes its own burn edits in
`InputEvents.ApplyInputEvents` and the `RaisePendingAlerts` loop, before that job is queued. Stock's burn timing is computed on the vehicle worker (`physics:vehicle-solver`) in
`UpdateBurnTarget` and read back one or more frames later. The time-sliced planner keeps the main thread
cost to about 3 ms per frame.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Modes/DeorbitPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/DeorbitPlanner.cs) | Iterator-based Lambert search with budgets |
| [AdvancedFlightComputer/Features/Guidance/Modes/StockDeorbit.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/StockDeorbit.cs) | Node load, stock timing check, Auto, warp, release |
| [AdvancedFlightComputer/Features/Guidance/Modes/Deorbit.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Deorbit.cs) | Deorbit tab and coast logic |
| [AdvancedFlightComputer/Features/Guidance/Modes/Landing.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Landing.cs) | Landing phase machine and hand-overs |
| [AdvancedFlightComputer/Features/Guidance/Modes/SitePasses.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/SitePasses.cs) | Closed-form site passes |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceStockDeorbitTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceStockDeorbitTest.cs) | Headless test of the stock-node path |

## Snippets

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/DeorbitPlanner.cs L180-L211
    internal void Step(double budgetMilliseconds = 3)
    {
        if (Complete) return;
        long start = Stopwatch.GetTimestamp();
        try
        {
            do
            {
                if (WorkMilliseconds + Stopwatch.GetElapsedTime(start).TotalMilliseconds >= WorkLimitMilliseconds)
                {
                    Status = Plan != null ? "Stock deorbit node ready. Using the checked plan."
                        : "The planning work limit ended before a safe approach was found. Change the site or braking altitude.";
                    // An accepted direct approach that was still being checked becomes a refusal. An earlier refusal keeps its reason.
                    if (Direct is { Refusal.Length: 0 }) Direct = Direct with { Refusal = Status };
                    Complete = true;
                    _search.Dispose();
                    return;
                }
                if (!_search.MoveNext()) { Complete = true; return; }
            } while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < budgetMilliseconds);
        }
        // A failed iterator cannot continue, so the refusal names the failure instead of the last progress status.
        catch (Exception error)
        {
            Status = "Deorbit planning failed: " + error.Message;
            if (Direct is { Refusal.Length: 0 }) Direct = Direct with { Refusal = Status };
            Plan = null;
            Complete = true;
            _search.Dispose();
        }
        finally { WorkMilliseconds += Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
    }
```

## Game APIs touched

| Member | Use |
|---|---|
| `Burn.Create` ([current/decomp/KSA/Burn.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Burn.cs#L185)), `FlightComputer.AddBurn` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L2209)) | Create and load the node |
| `BurnTarget.IgnitionTime`, `BurnDuration`, `AlignmentStartTime` ([current/decomp/KSA/BurnTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnTarget.cs#L12)) | Stock timing read back from the worker |
| `FlightComputer.ConservativeFlipTime`, `PlannedBurnThrottle`, `ActiveEnginePerformanceMax` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L121)) | Alignment lead and minimum-throttle check |
| `Vehicle.GetMinThrottle`, `_manualControlInputs` | Preview throttle |
| `Universe` auto-warp methods ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs)) | Warp to preparation |

## Pitfalls and version notes

- `FlightComputer.AddBurn` loads the node in Manual; select Auto only after verifying the loaded target.
- AFC adds and removes the deorbit node directly in the `PrepareWorker` prefix
  ([AdvancedFlightComputer/Features/Guidance/Modes/StockDeorbit.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/StockDeorbit.cs#L127-L134)), where the nearest-orbit job may
  be iterating the same burn list for cursor hover. Queueing the node through
  `InputEvents.BurnUpdateBuffer` (applied in the next frame's drain), or adding it from a
  `FlightComputer.RaisePendingAlerts` postfix (called for every vehicle each frame, after the drain and
  before that job is queued), avoids the race.
- A new `BurnTarget` has no duration and an `EndOfTime` ignition until a worker updates it; poll, do not
  assume.
- The stock solver picks its transfer direction from global Z; AFC rotates the requested orbit normal to
  local Z before solving.
- The author reports in-game flights only for single-craft ascent; the landing chain is covered by
  harness tests.

## See also

- [G-FOLD powered descent](gfold-powered-descent.md)
- [6-DOF SCvx landing MPC](scvx-6dof-landing-mpc.md)
- [Terminal hover and touchdown](terminal-hover-touchdown.md)
- [Planner burn node creation](planner-burn-node-creation.md) (the queued variant used from the UI)
- [Let stock compute the ignition timing before you arm Auto](../../../howto/flight-and-orbits/autopilot/execute-planned-burns-with-the-stock-autopilot/execute-planned-burns-with-the-stock-autopilot-3.md) - how-to article demonstrating this feature
- [Plan a deorbit burn to a landing site with a time-sliced Lambert search, then let a stock node fly it](../../../howto/flight-and-orbits/guidance/plan-and-fly-a-powered-landing/plan-and-fly-a-powered-landing-4.md) - how-to article demonstrating this feature
- [Time-slice a search across frames with a C# iterator and per-step and total work budgets](../../../howto/flight-and-orbits/guidance/run-an-expensive-solve-without-stalling-the-frame/run-an-expensive-solve-without-stalling-the-frame-4.md) - how-to article demonstrating this feature
- [Offset the Lambert aim point by an impact parameter and hand the result to the planner's Create path](../../../howto/flight-and-orbits/transfers/aim-a-transfer-at-a-flyby-periapsis/aim-a-transfer-at-a-flyby-periapsis-1.md) - how-to article demonstrating this feature

[^ksa-burnplan]: KSA.BurnPlan (decompiled, build 2026.10.10.5554)
