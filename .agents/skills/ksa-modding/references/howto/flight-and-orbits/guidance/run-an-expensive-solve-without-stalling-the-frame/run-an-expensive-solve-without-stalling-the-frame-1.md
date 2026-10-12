---
type: How-To
title: Build plain data on the main thread, solve on a worker, publish an immutable plan
description: Move a multi-second guidance solve off the frame by capturing everything it needs as plain data in the vehicle step, running it on a job-owned background thread with a lock-protected result slot, and polling for an immutable plan on later steps
category: flight-and-orbits
group: guidance
topic: run-an-expensive-solve-without-stalling-the-frame
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/convex-ascent-scvx]
techniques: [background-thread, harmony-prefix]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers, thread:background, frame:draw-ui]
game_types: [KSA.Vehicle, KSA.FlightComputer, KSA.Celestial, KSA.Universe]
tags: [background-thread, immutable, job, cancellation, plan, guidance, main-thread]
---
Outcome: a planner whose solve takes seconds (a convex ascent in KSA-AdvancedFlightComputer takes several) runs without ever blocking the frame or the physics workers. The recipe has four rules: read the game only on the main thread, hand the worker plain data, let the worker publish one immutable result, and poll for it on a later step. Problems that fit in a few milliseconds belong in [approach 3](run-an-expensive-solve-without-stalling-the-frame-3.md) or [approach 4](run-an-expensive-solve-without-stalling-the-frame-4.md) instead.[Minimum-propellant convex ascent](../../../../mods/ksa-advancedflightcomputer/features/convex-ascent-scvx.md)
Prerequisites: StarMap and Harmony. The ascent solver itself is described in [Fly a closed-loop ascent to a target orbit](../fly-closed-loop-ascent-guidance/fly-closed-loop-ascent-guidance-2.md).

## Background: how the game does it

- **The frame order.** In `Program.PrepareFrame` the finished vehicle step is applied first, then `Universe.ExecuteNextVehicleSolvers` calls `PrepareVehicleWorkers`, which calls `Vehicle.PrepareWorker(SimStep)` on the main thread for each vehicle, and finally queues the vehicle solver job on the worker threads.[^ksa-program] [^ksa-universe] [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2128-L2149)
- **Consequence.** The `PrepareWorker` prefix is the one main-thread point where the part tree and module states can be read without racing a vehicle worker, and it runs once per vehicle per step.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2396) The game's UI draw runs while workers are active, so reads from `frame:draw-ui` see the last applied state but must not assume the worker is idle.
- **Nothing in the game is thread-safe for a mod to read from a thread it owns.** The part tree, flight computer and orbit objects are mutated on the main thread and on the vehicle worker.

## Lifecycle and timing

| Work | Lifecycle | Thread |
|---|---|---|
| Request flag set | `frame:draw-ui` (panel button) | Main |
| Pick up request, capture data, start job, poll the job | `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`) | Main |
| Build problem, solve, publish | `thread:background` | Mod-owned, below-normal priority |
| Draw plan, progress | `frame:draw-ui` | Main |

See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Set a request flag in the UI and consume it in the vehicle step.** The panel never touches the part tree; the step does, because that is the one place it is safe to read.
2. **Capture everything the result will record into locals before you create the job.** The publish delegate runs on the worker and must not touch the game, so ids, names, positions and the target orbit are copied first:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs L375-L382
// Everything the plan records about what it was for, captured now: the publisher runs on the worker and must not touch the game.
string vehicleId = vehicle.Id;
string bodyName = parent.Id;
double solvedAt = launchAt;
double3 startCcf = rNow.Transform(parent.GetCci2Ccf());
double peKm = _s.PeKm, apKm = _s.ApKm, incDeg = _s.IncDeg, lanDeg = _s.LanDeg;
double qMaxKpa = qMax / 1000.0;
double insAltKm = (rIns - bodyRadius) / 1000.0;
```

3. **Create the job with `build` and `publish` delegates that close over data only.** The job owns its thread, a cancellation token and a lock, and rebuilds the problem for retries without going back to the game:

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/Ascent/KsaAscentPlan.cs L447-L466
/// <param name="build">The problem for a number of stages and a max-q, Pa. Must touch nothing of the game.</param>
/// <param name="available">Stages the vehicle has.</param>
/// <param name="initial">Stages to try first.</param>
/// <param name="qMax">The max-q asked for, Pa.</param>
/// <param name="publish">Wraps the chosen solution, its stage count, the max-q it was planned to, the attempt notes and the wall time in a plan; run on the worker, so it too must touch nothing of the game.</param>
public AscentPlanJob(Func<int, double, AscentProblem> build, int available, int initial, double qMax,
                     Func<AscentSolution, int, double, string[], double, AscentPlan> publish)
{
    _build = build;
    _qMax = qMax;
    _available = available;
    _initial = Math.Clamp(initial, 1, available);
    _publish = publish;
    _thread = new Thread(Run)
    {
        IsBackground = true,
        Name = "afc-ascent-scvx",
        Priority = ThreadPriority.BelowNormal,
    };
    _thread.Start();
```

4. **Expose progress and the result through lock-protected properties.** The result is visible only when the job is done, and a cancelled job finishes with no plan:

```csharp
// AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/Ascent/KsaAscentPlan.cs L476-L493
public string Progress { get { lock (_gate) return _progress; } }

/// <summary>Why the attempt running now is a retry: the one before it did not converge, and what was loosened. Empty on the first attempt.</summary>
public string Retry { get { lock (_gate) return _retry; } }

public bool IsDone { get { lock (_gate) return _done; } }

/// <summary>The plan once done, else null. A cancelled job finishes with no plan.</summary>
public AscentPlan Result { get { lock (_gate) return _done ? _result : null; } }

public void Cancel() => _cts.Cancel();

public void Dispose() => _cts.Cancel();

private void SetProgress(string text)
{
    lock (_gate) _progress = text;
}
```

5. **Poll on later steps; a fault costs the plan, never the step.** The step checks `job.IsDone`, takes `job.Result` (an immutable `AscentPlan`), stores it, and clears the job:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs L61-L68
private static void StepAscentPlannerCore(Vehicle vehicle, Orbit orbit, IParentBody parent)
{
    AscentPlanJob job = _s.AscentPlanJob;
    if (job != null && job.IsDone)
    {
        _s.AscentPlanJob = null;
        AscentPlan plan = job.Result;
        string failure;
```

6. **Wrap the whole step in a catch** so a planner fault becomes a status and a throttled log line instead of an exception in the game's loop (`LogHelper.WarnOnce`).
7. **Cancel and dispose** when the user cancels, the vehicle changes or the mod unloads: both `Cancel()` and `Dispose()` trip the token, and the solver checks it between iterations.

A minimal reusable shape (adapted from the job above; game-independent):

```csharp
// adapted from KSA-AdvancedFlightComputer/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/Ascent/KsaAscentPlan.cs (AscentPlanJob)
public sealed class SolveJob<TPlan> : IDisposable where TPlan : class
{
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private TPlan? _result;
    private bool _done;

    public SolveJob(Func<CancellationToken, TPlan?> solve)
    {
        new Thread(() =>
        {
            TPlan? plan = null;
            try { plan = solve(_cts.Token); }
            catch (Exception) { /* a fault costs the plan, never the step */ }
            lock (_gate) { _result = plan; _done = true; }
        })
        { IsBackground = true, Name = "my-solve", Priority = ThreadPriority.BelowNormal }.Start();
    }

    public bool IsDone { get { lock (_gate) return _done; } }
    public TPlan? Result { get { lock (_gate) return _done ? _result : null; } }
    public void Dispose() => _cts.Cancel();
}
```

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs#L61-L125) | request pick-up and polling in the vehicle step |
| [AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs#L215-L452) | problem build on the main thread, captured locals, job start |
| [AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/Ascent/KsaAscentPlan.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Adapters/Scvx/Ascent/KsaAscentPlan.cs#L420-L600) | the job: thread, retries, progress, result |

## Variations and gotchas

- **Never read the part tree from the solve thread.** Build plain data first.
- **Only hand over immutable results by reference.** The one shared object is the finished plan; mutable progress strings go through the lock.
- **One job at a time per vehicle**: a second request while a job exists is ignored (`if (_s.AscentPlanJob != null) return;`).
- **The result can be stale** by the time it arrives; AFC refuses a plan if the vehicle moved too far, its mass changed too much, or it is flown too long after the instant it was solved for.
- **Distinguish retries from failures** and say which: the job records one note per attempt and a reason when it loosens a constraint.
- A single request/result slot per worker (as in [approach 2](run-an-expensive-solve-without-stalling-the-frame-2.md)) is the right shape for a repeating solve; a one-shot job like this one is right for a plan.

## See also

- [Overview](overview.md); [Receding-horizon MPC](run-an-expensive-solve-without-stalling-the-frame-2.md); [Synchronous solve with a time limit](run-an-expensive-solve-without-stalling-the-frame-3.md); [Iterator time-slicing](run-an-expensive-solve-without-stalling-the-frame-4.md); [Wall-clock rate limits](run-an-expensive-solve-without-stalling-the-frame-5.md)
- [Minimum-propellant convex ascent (mod feature)](../../../../mods/ksa-advancedflightcomputer/features/convex-ascent-scvx.md)
- [Fly a closed-loop ascent to a target orbit](../fly-closed-loop-ascent-guidance/overview.md)
- [Measure what a mod costs per frame, throttle expensive work and warm up hot code](../../../testing-and-tooling/in-game-testing/measure-and-budget-mod-performance/overview.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
