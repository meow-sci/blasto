---
type: How-To
title: Solve a cheaper problem synchronously inside the vehicle step with a solver time limit
description: Run a convex solve of ten milliseconds or so on the main thread inside the per-vehicle prefix, bound each solve with a wall-clock time limit, and rate the whole search by cadence and measurement
category: flight-and-orbits
group: guidance
topic: run-an-expensive-solve-without-stalling-the-frame
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/gfold-powered-descent]
techniques: [custom-physics, harmony-prefix]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers]
game_types: [KSA.Vehicle, KSA.FlightComputer, KSA.Celestial]
tags: [synchronous, time-limit, cadence, g-fold, per-vehicle, frame-budget]
---
Outcome: a solve cheap enough for the frame (about 10-13 ms for a G-FOLD cadence solve at 50 nodes) runs synchronously inside the per-vehicle step with no thread, no locks and no result slot. The price is that every millisecond is frame time, so the solve gets a hard wall-clock limit, a re-solve cadence, and a measurement that tells you when it has outgrown the frame. KSA-AdvancedFlightComputer's G-FOLD descent is the reference.[G-FOLD powered descent](../../../../mods/ksa-advancedflightcomputer/features/gfold-powered-descent.md)
Prerequisites: StarMap and Harmony. The descent context is in [Plan and fly a powered landing](../plan-and-fly-a-powered-landing/plan-and-fly-a-powered-landing-1.md).

## Background: how the game does it

`Universe.PrepareVehicleWorkers` loops the vehicles and calls `Vehicle.PrepareWorker` on the main thread, one after another, before the vehicle solver job is queued; so anything slow in a prefix on `PrepareWorker` delays every later vehicle and the whole frame.[^ksa-universe] [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2141-L2149) The advantage is exactly that serialisation: a static or shared setting written right before a call cannot be seen by another vehicle's solve.[^ksa-vehicle]

## Lifecycle and timing

- **Where:** the `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`, main thread), for each landing vehicle in turn.
- **Cost:** the author measured about 10-13 ms for a cadence solve (previous time of flight reused) and about 256 ms for a bracketed time-of-flight search at the flown size (N = 50, Clarabel); SCS at any useful tolerance did not fit in a 16.7 ms frame.
- The [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) shows where this sits in the frame.

## Steps

1. **Put a time limit on every solve.** The solver exposes a process-wide wall-clock ceiling per solve (null or 0 means none). It limits each solve, not a search, which is tens of solves:

```csharp
// AdvancedFlightComputer/Features/Guidance/Gfold/GfoldPlanner.cs L62-L66
/// <summary>
/// Wall-clock ceiling per solve, in seconds. Null or 0 means no limit. A search
/// performs many solves, so this limits each solve rather than the full search.
/// </summary>
public static double? SolveTimeLimitS;
```

2. **Set it per vehicle right before the call.** The default is 40 ms, configurable in the UI. It is a static, safe only because the call is synchronous and returns before any other vehicle is served:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/GfoldDescent.cs L203-L206
// GfoldPlanner.SolveTimeLimitS is process-wide, which is safe ONLY because the solve below is synchronous and on this thread: each vehicle sets it immediately before its own call and the call has returned before any other vehicle is serviced.
// Move the solve to a worker and this becomes a race - see GfoldSolveMs for why that move is worth making anyway.
GfoldPlanner.SolveTimeLimitS = _s.GfoldSolveTimeLimitS > 0 ? _s.GfoldSolveTimeLimitS : null;
var solveClock = System.Diagnostics.Stopwatch.StartNew();
```

3. **Pass the limit to the backend** (here Clarabel's `timeLimitS`) so a hit is reported as a status, not an exception:

```csharp
// AdvancedFlightComputer/Features/Guidance/Gfold/GfoldPlanner.cs L492-L492
ClarabelSolver.Solve(problem, verbose, timeLimitS: SolveTimeLimitS ?? 0.0);
```

4. **Re-solve on a cadence, not every frame**, and mark the attempt on entry, not only on success, so a failing solve retries on the normal cadence rather than every frame while the last plan keeps flying:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/GfoldDescent.cs L163-L165, L200-L201
if (!GfoldInTerminalWindow(now) &&
    (_s.GfoldPlan == null || now - _s.GfoldLastSolveTime >= _s.GfoldIntervalS))
    SolveGfoldPlan(vehicle, parent, frame, siteCci, r, now);
// ...
// Mark the attempt now (not only on success) so a failing solve retries on the normal cadence rather than every frame while we keep flying the last plan.
_s.GfoldLastSolveTime = now;
```

5. **Treat a time-limited iterate as a failure.** Accept only optimal statuses and a plan that fits the propellant; otherwise keep the previous plan.
6. **Measure and display the cost.** The solve clock (`Stopwatch`) is recorded as `GfoldSolveMs`: the number the frame budget cares about. When a solve outgrows the budget, that is the signal to move it to a worker ([approach 1](run-an-expensive-solve-without-stalling-the-frame-1.md) or [2](run-an-expensive-solve-without-stalling-the-frame-2.md)); a per-solve limit cannot bound a search.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Modes/GfoldDescent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/GfoldDescent.cs#L178-L299) | cadence solve and bracketed search with the time limit |
| [AdvancedFlightComputer/Features/Guidance/Gfold/GfoldPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Gfold/GfoldPlanner.cs#L62-L66) | the static limit |
| [AdvancedFlightComputer/Features/Guidance/Modes/VehicleAutopilotState.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/VehicleAutopilotState.cs#L598-L604) | per-vehicle default and measured cost |

## Variations and gotchas

- **Moving the solve to a worker turns the static limit into a race**, because two threads would set it.
- **Tighter tolerance on a first-order solver** can make a time-of-flight search worse (more solves hit the iteration cap and are rejected).
- **Warm starts**: an interior-point solver cannot be warm started (the previous optimum sits on the boundary), but the search can: carry the previous time of flight forward and bracket around it.
- **The limit cuts the solve, not your frame.** Everything else in the prefix still counts.

## See also

- [Overview](overview.md); [Build plain data, solve on a worker](run-an-expensive-solve-without-stalling-the-frame-1.md); [Receding-horizon MPC](run-an-expensive-solve-without-stalling-the-frame-2.md)
- [G-FOLD powered descent (mod feature)](../../../../mods/ksa-advancedflightcomputer/features/gfold-powered-descent.md)
- [Measure what a mod costs per frame, throttle expensive work and warm up hot code](../../../testing-and-tooling/in-game-testing/measure-and-budget-mod-performance/overview.md)

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
