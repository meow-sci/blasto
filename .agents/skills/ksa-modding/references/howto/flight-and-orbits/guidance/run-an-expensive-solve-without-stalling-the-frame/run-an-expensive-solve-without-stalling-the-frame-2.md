---
type: How-To
title: Receding-horizon MPC on a worker with a wall-clock budget and one solver instance per thread
description: Re-solve a guidance problem every cycle on a per-vehicle worker with immutable inputs, a single request and result slot, and a cap derived from measured wall-clock cost per iteration
category: flight-and-orbits
group: guidance
topic: run-an-expensive-solve-without-stalling-the-frame
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/scvx-6dof-landing-mpc]
techniques: [background-thread, harmony-prefix]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers, thread:background]
game_types: [KSA.Vehicle, KSA.FlightComputer, KSA.Celestial]
tags: [mpc, receding-horizon, background-thread, wall-clock-budget, warm-start, single-slot]
---
Outcome: a guidance loop that re-plans every cycle (a model-predictive controller) without ever letting a slow solve delay the frame. Unlike a one-shot plan ([approach 1](run-an-expensive-solve-without-stalling-the-frame-1.md)), the worker is long-lived, the request slot is reused every cycle, and the limit on its work is a measured wall-clock budget, not an iteration count. KSA-AdvancedFlightComputer's 6-DOF landing controller is the reference.[6-DOF SCvx landing MPC](../../../../mods/ksa-advancedflightcomputer/features/scvx-6dof-landing-mpc.md)
Prerequisites: StarMap and Harmony. The full landing context is in [Plan and fly a powered landing](../plan-and-fly-a-powered-landing/plan-and-fly-a-powered-landing-2.md).

## Background: how the game does it

The game offers no job API for mods. The vehicle step (`Vehicle.PrepareWorker`, called per vehicle from `Universe.PrepareVehicleWorkers` before the vehicle solver is queued) is where main-thread state is settled, and the solver itself runs on the game's worker threads, so a mod that needs milliseconds-to-seconds of solver time brings its own thread.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2396)

## Lifecycle and timing

| Work | Lifecycle | Thread |
|---|---|---|
| Measure state, dispatch, collect, command | `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`) | Main |
| Solve | `thread:background` (`afc-6dof-solve`) | One per guided vehicle |

Between collections the vehicle keeps flying the current plan, interpolated at the current time. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **One worker, one mutable solver, one slot.** The worker owns the guidance object during a solve; callers may read its immutable plan and publish immutable inputs, but must collect the result before changing the solver:

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofSolveWorker.cs L23-L41
/// <summary>
/// Owns one mutable guidance during a solve. Callers can read its immutable plan and publish immutable inputs, but must collect the result before changing the solver.
/// A completed result holds the slot until collection so another request cannot erase it.
/// </summary>
public sealed class Ksa6DofSolveWorker : IDisposable
{
    private readonly Thread _thread;
    private readonly SemaphoreSlim _work = new(0, 1);
    private readonly object _gate = new();
    private bool _running = true;
    private bool _busy;
    private Request _request;
    private Ksa6DofSolveResult _result;
    private int _dispatched, _completed, _skipped;
    private double _solveMs;

    private sealed record Request(
        Ksa6DofJob Job, Ksa6DofGuidance Guidance, double[] X0, double Now,
        int MaxIterations, Func<Ksa6DofGuidance, double[], double, Ksa6DofGuidance> Rebuild);
```

2. **Dispatch refuses rather than queues.** If a solve is running or a result waits uncollected, the request is dropped and counted as skipped; the state vector is cloned at dispatch:

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofSolveWorker.cs L71-L103
private bool Dispatch(Ksa6DofJob job, Ksa6DofGuidance guidance, double[] x0, double now,
                      int maxIterations,
                      Func<Ksa6DofGuidance, double[], double, Ksa6DofGuidance> rebuild)
{
    lock (_gate)
    {
        if (_busy || !_running || _result != null)
        {
            _skipped++;
            return false;
        }

        _request = new Request(job, guidance, (double[])x0.Clone(), now, maxIterations, rebuild);
        _dispatched++;
        _busy = true;
        _work.Release();
        return true;
    }
}

public bool TryCollect(out Ksa6DofSolveResult result)
{
    lock (_gate)
    {
        result = null;
        if (!_running || _busy || _result == null)
            return false;

        result = _result;
        _result = null;
        return true;
    }
}
```

3. **Publish inputs as immutable records.** The linearisation reads gravity bias and inertia; the main thread replaces the whole record (`Inputs.WithBias(...)`, `WithInertia(...)`) and the next solve picks it up at entry, so a solve in progress keeps the model it started with. Never mutate in place.
4. **Dispatch on a cadence from the measured state**, and collect the matching result on a later step:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/SixDof.cs L1379-L1389
double cadence = Math.Clamp(_s.SixDofReplanSec, 0.02, 5.0);
// Refresh inertia from the live vehicle before re-solving.
// It changes as propellant drains, and a stale value is a SYSTEMATIC torque error that MPC structurally cannot correct - re-anchoring the state does not fix the model.
Ksa6DofSetup.Inertia(vehicle, out double ixx, out double iyy, out double izz);
_s.Guidance.Inputs = _s.Guidance.Inputs.WithInertia(ixx, iyy, izz);

if (_s.Worker != null)
{
    if (now - _s.LastReplan >= cadence)
        _s.Worker.TryDispatchUpdate(_s.Guidance, x, now, 5);
}
```

5. **Bound the work by wall-clock time, not iterations.** One ADMM iteration costs about 0.04 ms at 30 nodes and 0.77 ms at 80, so any fixed iteration cap is right for one problem size only. AFC measures the cost per iteration with an exponential average and derives each cycle's cap from the budget:

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofGuidance.cs L936-L945
private void ApplyTimeBudget(double budgetMs, double escalatedMs)
{
    double perIter = Math.Max(MsPerAdmmIteration, 1e-4);

    // ONE CAP, NOT TWO. The two-stage scheme ran a subproblem to the small cap and, if it truncated, ran the SAME subproblem again with the big one - so a truncating solve paid for both. It was built for one-off truncation, but measured in flight it fired on 77% of solves (213 escalations in 149 cycles), which means the small cap was not a budget, it was a tax.
    //  SCS stops at its tolerance, not at the cap, so a subproblem that converges is completely unaffected by how high the cap is - raising it costs nothing on the common path and saves the wasted first run on the uncommon one. What bounds the cycle now is CycleBudgetMs, which bounds the thing that actually matters.
    int cap = (int)Math.Clamp(Math.Max(budgetMs, escalatedMs) / perIter, 400, 100_000);
    _solver.MaxSubproblemIterations = cap;
    _solver.EscalatedSubproblemIterations = cap;   // equal, so no retry is issued
}
```

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofGuidance.cs L949-L959
private void LearnAdmmCost(double elapsedMs)
{
    int admm = 0;
    foreach (ScvxIteration it in _solver.Trace)
        admm += it.SolverIterations;
    if (admm > 50 && elapsedMs > 0.0)
    {
        double sample = elapsedMs / admm;
        MsPerAdmmIteration = 0.8 * MsPerAdmmIteration + 0.2 * sample;
    }
}
```

6. **Give recovery cycles more time.** The normal cycle budget is 25 ms; a cycle whose predecessor refused gets 120 ms, because with the normal one the deadline can fire after a single iteration every cycle and the loop never learns anything:

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofGuidance.cs L897-L899
BuildShiftedSeed(x0, simNow, sigma, out double[] xs, out double[] us, out _);
// A cycle whose predecessor refused gets the larger budget, so it can take the second and third iterations that let SCvx shrink its way to an accepted step. A healthy cycle never reaches the deadline at all.
double budget = _consecutiveFailures > 0 ? RecoveryCycleBudgetMs : CycleBudgetMs;
```

7. **Keep a refused solve harmless.** A truncated iterate poisons the warm start, so a refused re-solve keeps flying the old plan, and the cadence timer advances only on success so a failure retries on the next step.
8. **Stop without blocking.** `TryStopWhenIdle` refuses while busy and is retried on a later step; `Dispose` abandons the worker and its guidance together.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofSolveWorker.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofSolveWorker.cs) | thread, slots, dispatch, collect, stop |
| [AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofGuidance.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofGuidance.cs#L183-L208) | budget properties and their rationale |
| [AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofGuidance.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofGuidance.cs#L936-L972) | derived iteration cap and cost learning |
| [AdvancedFlightComputer/Features/Guidance/Modes/SixDof.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/SixDof.cs#L1046-L1069) | collect and match the result |

## Variations and gotchas

- **An iteration cap does not bound time.** Budget by measured cost per iteration.
- **One solver instance per thread, owned for the whole solve.** Rebuilding the guidance while the worker holds the old instance leaves a solve running on an object nobody reads.
- **Ordering matters for collect.** Collect before anything that can rebuild the source guidance (restart, node-count change).
- **Over-tight solver tolerance** costs most of the time and buys nothing.

## See also

- [Overview](overview.md); [Plan and fly a powered landing, approach 2](../plan-and-fly-a-powered-landing/plan-and-fly-a-powered-landing-2.md)
- [6-DOF SCvx landing MPC (mod feature)](../../../../mods/ksa-advancedflightcomputer/features/scvx-6dof-landing-mpc.md)
- [Build plain data, solve on a worker, publish an immutable plan](run-an-expensive-solve-without-stalling-the-frame-1.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
