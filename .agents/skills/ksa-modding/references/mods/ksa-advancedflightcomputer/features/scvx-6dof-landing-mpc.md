---
type: Mod Feature
title: 6-DOF successive-convexification landing as a receding-horizon MPC on a solver thread
description: A rigid-body landing planner re-solved every cycle from the measured state with SCS on a dedicated background thread, with immutable input snapshots, a single result slot, an estimated acceleration bias, a wall-clock budget and a node ladder
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [native-interop, background-thread, harmony-prefix, custom-physics]
hooks:
  - harmony-prefix KSA.Vehicle.PrepareWorker
  - harmony-postfix KSA.FlightComputer.ComputeControl
  - harmony-postfix KSA.FlightComputer.UpdateAttitudeTarget
lifecycle: [frame:dispatch-solvers, thread:background, physics:vehicle-solver]
game_types: [KSA.Vehicle, KSA.FlightComputer, KSA.Celestial]
tags: [landing, scvx, mpc, 6-dof, background-thread, scs, convex-optimization, booster]
---

For boosters with high rotational inertia and for descents through an atmosphere, AFC uses a 6-DOF
landing guidance: a rigid-body model (position, velocity, attitude quaternion, body rates, mass; controls
are gimbal deflections, thrust and roll torque) solved by successive convexification (SCvx) and flown as
a model-predictive controller that re-plans every cycle. It is the most demanding part of the mod in
threading terms, because a solve takes milliseconds to seconds and must not run on the game's threads.
The repo's `docs/guidance/scvx-bridge.md` and `scvx-bridge-devnotes.md` explain the algorithm and the
lessons learned in depth.

## How it works

### The algorithm

- **SCvx loop.** Linearize the nonlinear dynamics (`Dynamics6Dof`, Jacobians by forward-mode dual
  numbers) about the reference trajectory, build one cone program per iteration
  (`Scvx6DofSubproblemScs`: dynamics as soft constraints with virtual control, trust region, cones for
  thrust, tilt and gimbal limits), solve with SCS, accept or reject by a ratio test, adapt the trust
  region ([AdvancedFlightComputer/Features/Guidance/Scvx/SixDof/Scvx6DofSolver.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Scvx/SixDof/Scvx6DofSolver.cs)).
- **MPC, not a planner.** Every cycle: measure the state, re-solve seeded with the previous plan
  shifted forward, apply the first fraction, discard the rest. Node 0 is pinned to the measured state,
  so the feedback is the re-solve; warm starts make a re-solve a few milliseconds instead of hundreds.
- **Departures from textbook SCvx** (per the author): an estimated, filtered, clamped acceleration bias
  (`measured - (thrust/m + gravity)`) added to the planner's gravity, so persistent model errors such as
  drag are planned around; a proximal term instead of large regularizers; soft L1-penalized glideslope and
  descent-rate corridors that start at node 1; an iteration cap derived each cycle from measured cost per
  iteration against a wall-clock budget; a feasibility gate in metres rather than scaled units; a node
  ladder that steps the node count down at fixed altitudes (reference trajectory interpolated onto the new
  count); and a hand-off to the terminal hover for the last metres ([docs/guidance/scvx-bridge.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/docs/guidance/scvx-bridge.md)).

### Measured vehicle

`Ksa6DofSetup` builds the configuration from the live vehicle instead of the defaults (which describe a
Super Heavy-class booster on a 9.81 m/s^2 world): thrust from the engines at live ambient pressure,
inertia re-read per plan from `Vehicle.TotalMassPropsBody` (a full symmetric tensor that tracks
propellant drain) and projected onto the model's body axes, keeping only the diagonal as KSA's own
`UpdateTvcParams` does ([AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofSetup.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofSetup.cs#L1-L40)).
A G-FOLD solution can seed a cold start (`Ksa6DofGfoldSeed`).

### The solve thread

`Ksa6DofSolveWorker` owns one background thread (`afc-6dof-solve`, `IsBackground`, below-normal priority)
per guided vehicle and one mutable `Ksa6DofGuidance` during a solve:

1. The per-vehicle step on the main thread builds an immutable input record (state vector copied with
   `Clone()`, time, iteration cap) and calls `TryDispatchUpdate` / `TryDispatchStepCold` /
   `TryDispatchRebuild`. Dispatch is refused (and counted as skipped) while a solve is running or a result
   waits for collection, so a request can never overwrite an uncollected result.
2. The thread waits on a `SemaphoreSlim`, solves, and stores one `Ksa6DofSolveResult`.
3. A later step calls `TryCollect`, checks that the result matches the job and source it asked for, and
   swaps in the new plan; inputs that the linearization reads (gravity bias, inertia) are published as
   immutable `Ksa6DofInputs` records, never mutated in place while a solve may read them
   ([AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofSolveWorker.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofSolveWorker.cs#L20-L120),
   [AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofInputs.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofInputs.cs)).
4. Between collections the vehicle keeps flying the current plan, interpolated at the current time
   (`PlanStateInterpolation`).
5. Disengage can come from the ImGui draw, so stopping never blocks: `TryStopWhenIdle` refuses while busy
   and is retried on a later step.

### Actuation

The plan's gimbal torque goes to the gimbals through the least-squares allocator and the
[command sink](flight-computer-command-sink.md), its attitude and turning rate through the stock flight
computer with the rate feedforward, and its thrust through the engine-performance inversion (see
[guidance attitude and gimbal actuation](guidance-attitude-and-gimbal-actuation.md)).

## Lifecycle and timing

| Work | Lifecycle | Thread |
|---|---|---|
| Measure, dispatch, collect, command | `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`) | Main |
| SCvx and SCS | `thread:background` (`afc-6dof-solve`) | Mod-owned |
| Gimbal and rate writes | `FlightComputer.ComputeControl` / `UpdateAttitudeTarget` postfixes (`physics:vehicle-solver`) | Vehicle worker |

The synchronization points are the lock-protected dispatch/collect slot and the immutable records; the
game's state is read only on the main thread and copied before dispatch. Telemetry (`SixDofLog`) is
written per cycle with plan snapshots and an event log.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Scvx/SixDof/Scvx6DofSolver.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Scvx/SixDof/Scvx6DofSolver.cs) | SCvx loop |
| [AdvancedFlightComputer/Features/Guidance/Scvx/SixDof/Scvx6DofSubproblemScs.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Scvx/SixDof/Scvx6DofSubproblemScs.cs) | Cone program per iteration |
| [AdvancedFlightComputer/Features/Guidance/Scvx/SixDof/Dynamics6Dof.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Scvx/SixDof/Dynamics6Dof.cs) | Dynamics and Jacobians |
| [AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofGuidance.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofGuidance.cs) | MPC loop and command interface |
| [AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofSolveWorker.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofSolveWorker.cs) | Solve thread and result slot |
| [AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofSetup.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofSetup.cs) | Live vehicle measurement |
| [AdvancedFlightComputer/Features/Guidance/Modes/SixDof.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/SixDof.cs) | Mode: stepping, hand-off, UI |
| [docs/guidance/scvx-bridge-devnotes.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/docs/guidance/scvx-bridge-devnotes.md) | What had to be true besides the maths |
| [tests/AdvancedFlightComputer.Guidance.Tests/Scvx/ThreadCheck.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.Guidance.Tests/Scvx/ThreadCheck.cs) | Console check of the threaded path |

## Snippets

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

## Game APIs touched

| Member | Use |
|---|---|
| `Vehicle.TotalMassPropsBody` (inertia tensor, mass) | Live inertia per plan |
| `FlightComputer` gimbal and attitude outputs | Actuation (via the shared sink and postfixes) |
| Body rotation, site position, atmosphere | Frames and drag via `KsaFrameBridge` and the measured drag table |

## Pitfalls and version notes

- Never mutate solver inputs while a background solve may read them; publish immutable records.
- An iteration cap does not bound time; budget by measured wall-clock cost per iteration.
- A truncated iterate poisons the warm start; a refused re-solve keeps flying the old plan.
- Over-tight ADMM tolerance costs most of the time and buys nothing (see
  [native conic solver interop](native-conic-solver-interop.md)).
- The author reports in-game flights only for single-craft ascent from this tree; 6-DOF is covered by
  console checks against a JAX reference and by harness tests.

## See also

- [G-FOLD powered descent](gfold-powered-descent.md)
- [Drag table measurement](drag-table-measurement.md)
- [Terminal hover and touchdown](terminal-hover-touchdown.md)
- [Guidance vehicle step loop](guidance-vehicle-step-loop.md)
- [Fly a 6-DOF landing MPC with the solve on a dedicated background thread](../../../howto/flight-and-orbits/guidance/plan-and-fly-a-powered-landing/plan-and-fly-a-powered-landing-2.md) - how-to article demonstrating this feature
- [Receding-horizon MPC on a worker with a wall-clock budget and one solver instance per thread](../../../howto/flight-and-orbits/guidance/run-an-expensive-solve-without-stalling-the-frame/run-an-expensive-solve-without-stalling-the-frame-2.md) - how-to article demonstrating this feature

