---
type: How-To
title: Fly a 6-DOF landing MPC with the solve on a dedicated background thread
description: Run a successive-convexification landing model-predictive controller whose solves take milliseconds to seconds on a per-vehicle worker thread, with one request and one result slot, an estimated acceleration bias, a wall-clock budget and a node ladder
category: flight-and-orbits
group: guidance
topic: plan-and-fly-a-powered-landing
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/scvx-6dof-landing-mpc]
techniques: [background-thread, custom-physics, harmony-prefix, native-interop]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker, harmony-postfix KSA.FlightComputer.ComputeControl]
lifecycle: [frame:dispatch-solvers, thread:background, physics:vehicle-solver]
game_types: [KSA.Vehicle, KSA.FlightComputer, KSA.Celestial, KSA.IParentBody, KSA.ManualControlInputs]
tags: [landing, scvx, mpc, 6dof, background-thread, atmosphere, gimbal, node-ladder]
---
Outcome: a landing controller for craft that G-FOLD ([approach 1](plan-and-fly-a-powered-landing-1.md)) cannot handle: high rotational inertia, gimbal-driven attitude, or descents through an atmosphere. A rigid-body model (position, velocity, attitude quaternion, body rates, mass; controls are gimbal deflections, thrust and roll torque) is solved by successive convexification (SCvx) and flown as a model-predictive controller that re-plans every cycle. A solve takes milliseconds to seconds, so it must not run on the game's threads. KSA-AdvancedFlightComputer (AFC) is the reference.[6-DOF SCvx landing MPC](../../../../mods/ksa-advancedflightcomputer/features/scvx-6dof-landing-mpc.md)
Prerequisites: StarMap and Harmony. The SCvx/SCS solver is a native component and is treated as a black box here; the transferable part is the threading and measurement discipline.

## Background: how the game does it

- **Live inertia.** `Vehicle.TotalMassPropsBody` is a `MassProperties` whose `Inertia` is a full symmetric tensor about the centre of mass; it tracks propellant drain, so it has to be re-read for every plan.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L559-L559)
- **Where the main thread may write.** `Universe.PrepareVehicleWorkers` runs `Vehicle.PrepareWorker` for each vehicle on the main thread before the vehicle step is dispatched;[^ksa-universe] [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2141-L2149) after that the vehicle's flight computer belongs to the physics worker until the next `ApplyVehicleSolvers`.
- **Actuation.** Attitude and gimbal commands reach the worker's `FlightComputer`; AFC writes them from postfixes on `FlightComputer.ComputeControl` / `UpdateAttitudeTarget` (`physics:vehicle-solver`), covered in [Drive the flight computer and attitude modes from a mod](../../autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md).

## Lifecycle and timing

| Work | Lifecycle | Thread |
|---|---|---|
| Measure state, dispatch, collect, command | `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`) | Main |
| SCvx and SCS solve | `thread:background` (`afc-6dof-solve`) | Mod-owned, one per guided vehicle |
| Gimbal and rate writes | `FlightComputer.ComputeControl` / `UpdateAttitudeTarget` postfixes (`physics:vehicle-solver`) | Vehicle worker |

The only synchronisation points are the lock-protected request/result slot and immutable input records. Game state is read on the main thread and copied before dispatch. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Run an expensive solve without stalling the frame](../run-an-expensive-solve-without-stalling-the-frame/overview.md).

## Steps

1. **Measure the vehicle for the model** rather than using defaults: thrust at live ambient pressure, inertia re-read per plan and projected onto the model's body axes (only the diagonal is kept, as KSA's own TVC parameters do). Refresh it before each re-solve, because a stale value is a systematic torque error the MPC cannot correct:

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofSetup.cs L44-L56
Symmetric3x3 t = vehicle.TotalMassPropsBody.Inertia;
KsaFrameBridge.BodyAxes(vehicle, out double3 mx, out double3 my, out double3 mz);

// a' I b, with Symmetric3x3's YX/ZX/ZY being Ixy/Ixz/Iyz.
double Bilinear(double3 a, double3 b) =>
    t.XX * a.X * b.X + t.YY * a.Y * b.Y + t.ZZ * a.Z * b.Z +
    t.YX * (a.X * b.Y + a.Y * b.X) +
    t.ZX * (a.X * b.Z + a.Z * b.X) +
    t.ZY * (a.Y * b.Z + a.Z * b.Y);

ixx = Bilinear(mx, mx);
iyy = Bilinear(my, my);
izz = Bilinear(mz, mz);
```

2. **Own one worker thread per guided vehicle** with a single request slot and a single result slot. Below-normal priority, background thread, a `SemaphoreSlim` for wake-ups:

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofSolveWorker.cs L43-L52
public Ksa6DofSolveWorker()
{
    _thread = new Thread(Loop)
    {
        IsBackground = true,
        Name = "afc-6dof-solve",
        Priority = ThreadPriority.BelowNormal,
    };
    _thread.Start();
}
```

3. **Dispatch from the main thread without ever overwriting an uncollected result.** The state vector is cloned; a request while busy or while a result waits is refused and counted as skipped:

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

4. **Run the solve on the worker and publish one result.** Exceptions are caught and reported as a faulted result; the result keeps a reference to the job and the guidance object it came from so stale results can be discarded:

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofSolveWorker.cs L126-L137, L163-L180
_work.Wait();
Request request;
lock (_gate)
{
    if (!_running)
    {
        _request = null;
        _busy = false;
        return;
    }
    request = _request;
}
// ...
catch (Exception e)
{
    faulted = true;
    error = "solve threw: " + e.Message;
}

lock (_gate)
{
    _solveMs = sw.Elapsed.TotalMilliseconds;
    _completed++;
    // Dispose abandons both the worker and its guidance. No result from that owner can be collected after a later engagement.
    if (_running)
        _result = new Ksa6DofSolveResult(request.Job, request.Guidance,
            ok, error, faulted, produced);
    _request = null;
    _busy = false;
    if (!_running) return;
}
```

5. **Collect on a later step and check it matches** the job and source it asked for before swapping the plan in. Collect before anything that can rebuild the source guidance (a restart, a node-count change):

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/SixDof.cs L1046-L1069
Ksa6DofSolveResult coldResult = null;
if (_s.Worker != null && _s.Worker.TryCollect(out Ksa6DofSolveResult result)
    && result.Matches(_s.Converging ? Ksa6DofJob.StepCold : Ksa6DofJob.Update, _s.Guidance))
{
    if (result.Faulted)
    {
        string error = (result.Job == Ksa6DofJob.StepCold ? "cold" : "warm")
            + " solve failed: " + result.Error;
        Disengage6Dof(vehicle);
        _s.Error = error;
        SixDofLog.Event(_s, now, error);
        return;
    }
    if (result.Job == Ksa6DofJob.StepCold && _s.Converging)
    {
        coldResult = result;
    }
    else if (result.Job == Ksa6DofJob.Update && !_s.Converging)
    {
        _s.DidSolve = true;
        if (result.Ok) OnSolveSucceeded(now, x);
        else OnSolveRefused(now, result.Error);
    }
}
```

6. **Estimate what the model misses before re-solving.** An acceleration bias (measured acceleration minus modelled thrust and gravity) is low-pass filtered and clamped, then *published* in an immutable inputs record that the next solve folds in, so a running solve keeps the model it started with:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/SixDof.cs L701-L706, L716-L733
// Measured acceleration, site frame.
double ax = (x[3] - _s.PrevV[0]) / dt;
double ay = (x[4] - _s.PrevV[1]) / dt;
double az = (x[5] - _s.PrevV[2]) / dt;
_s.PrevV = [x[3], x[4], x[5]];
_s.PrevT = now;
// ...
double rx = ax - (thrustDelivered / m * zx + g0.X);
double ry = ay - (thrustDelivered / m * zy + g0.Y);
double rz = az - (thrustDelivered / m * zz + g0.Z);

// ~2 s time constant at a 0.02 s step: slow enough to ignore per-frame noise, fast enough to follow drag falling away through the descent.
double alpha = Math.Clamp(dt / 2.0, 0.0, 1.0);
_s.Bias = new double3(
    _s.Bias.X + alpha * (rx - _s.Bias.X),
    _s.Bias.Y + alpha * (ry - _s.Bias.Y),
    _s.Bias.Z + alpha * (rz - _s.Bias.Z));

const double Limit = 5.0;
// PUBLISHED, not applied.
// The guidance folds it into the model at the entry to its next solve and not before, so a solve already running keeps the model it started with.
// See Ksa6DofInputs.
_s.Guidance.Inputs = _s.Guidance.Inputs.WithBias(new double3(
    Math.Clamp(_s.Bias.X, -Limit, Limit),
    Math.Clamp(_s.Bias.Y, -Limit, Limit),
```

7. **Re-plan on a cadence from the measured state**, with node 0 pinned to it so the re-solve is the feedback, and cap work by wall-clock time, not iterations. An iteration cap does not bound time (one iteration costs about 0.04 ms at 30 nodes and about 0.77 ms at 80); AFC derives the iteration cap each cycle from the measured cost per iteration against a budget (25 ms per cycle, 120 ms when the predecessor cycle refused):

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

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofGuidance.cs L897-L904
BuildShiftedSeed(x0, simNow, sigma, out double[] xs, out double[] us, out _);
// A cycle whose predecessor refused gets the larger budget, so it can take the second and third iterations that let SCvx shrink its way to an accepted step. A healthy cycle never reaches the deadline at all.
double budget = _consecutiveFailures > 0 ? RecoveryCycleBudgetMs : CycleBudgetMs;

FellBack = false;
LastTrustRegionStart = WarmTrustRegion;
_solver.Reseed(x0, xs, us, sigma, trustRegion: WarmTrustRegion);
if (Finish(x0, simNow, maxIterations, budgetMs: budget))
```

8. **Step the node count down at altitude gates** (one-way): node spacing, not count, is what to hold roughly constant, and every change of N rebuilds the solver and discards the ADMM warm start, so a ladder of a few transitions is affordable where continuous tracking is not. The reference trajectory is interpolated onto the new count.
9. **Hand off to the terminal hover** below a configured altitude; ask the hover first so a refusal keeps the plan (`TerminalHoverAvailable`), then `Disengage6Dof(vehicle, cutEngine: false)` and `StartTerminalHover`; see [approach 3](plan-and-fly-a-powered-landing-3.md).
10. **Stop without blocking.** Disengage can come from the ImGui draw (`frame:draw-ui`); `TryStopWhenIdle` refuses while a solve runs and is retried on a later step. `Dispose` abandons the worker and its guidance together, because an active solve can still change that instance.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofSolveWorker.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofSolveWorker.cs) | thread, slots, dispatch, collect, stop |
| [AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofGuidance.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofGuidance.cs) | MPC loop, budgets, warm starts, fallbacks |
| [AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofSetup.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/SixDof/Ksa6DofSetup.cs) | live vehicle measurement |
| [AdvancedFlightComputer/Features/Guidance/Modes/SixDof.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/SixDof.cs) | per-step mode: collect, bias, ladder, cadence, hand-off |
| [AdvancedFlightComputer/Features/Guidance/Scvx/SixDof/Scvx6DofSolver.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Scvx/SixDof/Scvx6DofSolver.cs) | SCvx loop (solver side) |

## Variations and gotchas

- **Never mutate solver inputs while a background solve may read them**; publish immutable records (`Ksa6DofInputs`).
- **A truncated iterate poisons the warm start**; a refused re-solve keeps flying the old plan (interpolated at the current time) and the plan clock must not advance past the plan's end unnoticed: AFC reports "plan expired" and holds terminal control.
- **A refused cycle must not repeat forever.** AFC grants the cycle after a refusal a larger wall-clock budget, because with the normal one the first SCvx iteration alone can exhaust it every time.
- **The G-FOLD fallback.** If 6-DOF has published no plan within a window, AFC falls back to a G-FOLD solution to seed or fly the descent.
- **One mutable solver per thread.** Rebuilding the guidance while the worker holds the old instance leaves a solve running on an object nobody reads; hence the one-way, idle-gated ladder.
- The author reports in-game flights only for single-craft ascent; 6-DOF is covered by console checks against a JAX reference and by harness tests.

## See also

- [Overview](overview.md); [G-FOLD synchronous solve](plan-and-fly-a-powered-landing-1.md); [Sink-profile hover](plan-and-fly-a-powered-landing-3.md)
- [6-DOF SCvx landing MPC (mod feature)](../../../../mods/ksa-advancedflightcomputer/features/scvx-6dof-landing-mpc.md)
- [Run an expensive solve without stalling the frame](../run-an-expensive-solve-without-stalling-the-frame/overview.md)
- [Predict impact points with drag and build a ballistic computer](../predict-impact-point-and-build-a-ballistic-computer/overview.md) for the drag table the atmospheric model uses

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
