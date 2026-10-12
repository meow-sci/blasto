---
type: How-To
title: Time-slice a search across frames with a C# iterator and per-step and total work budgets
description: Write an expensive search as an IEnumerable that yields after each small unit of work, advance it a few milliseconds per frame from the vehicle step, and bound it with a per-call budget and a total work limit
category: flight-and-orbits
group: guidance
topic: run-an-expensive-solve-without-stalling-the-frame
approach: 4
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/deorbit-landing-planner]
techniques: [iterator-time-slicing, harmony-prefix]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers]
game_types: [KSA.Vehicle, KSA.FlightComputer, KSA.Celestial, KSA.Universe]
tags: [time-slicing, iterator, yield, work-budget, planner, lambert, no-threads]
---
Outcome: a search that needs hundreds of milliseconds of CPU (KSA-AdvancedFlightComputer's deorbit planner scans Lambert transfers, checks terrain along flown arcs and refines the best) runs a few milliseconds per frame on the main thread with no thread, no locks and no copy of game state. The trick is to write the search as a C# iterator that yields after each small unit of work and let the caller decide how much to advance per frame.[Deorbit landing planner](../../../../mods/ksa-advancedflightcomputer/features/deorbit-landing-planner.md)
Prerequisites: StarMap and Harmony. The planner this comes from is used in [Plan and fly a powered landing, approach 4](../plan-and-fly-a-powered-landing/plan-and-fly-a-powered-landing-4.md).

## Background: how the game does it

All main-thread mod code is called from the frame (`Program.OnFrame`) or from `Universe.PrepareVehicleWorkers`, so there is no scheduler to slice for you.[^ksa-universe] [^ksa-vehicle] An iterator keeps its locals alive between calls, which is exactly the cooperative-multitasking tool needed: each `MoveNext()` runs to the next `yield return`. Because everything stays on the main thread, state can be read freely, but a long gap between slices means the world has moved, so the planner re-validates its input (orbit, settings) each slice and restarts when it changed.

## Lifecycle and timing

- **Where:** `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`, main thread), once per vehicle per step. The planner's `Step()` call is the only place the iterator advances.
- **Budget:** 3 ms per call by default, 500 ms of total work (summed across calls) before the search gives up, plus eight fully checked candidates at most.
- **Gate:** planning waits for 1x warp, an idle engine and a settled orbit, because under warp a single frame covers a long simulated interval and would invalidate the input.
- See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Write the top-level search as an iterator that composes nested iterators.** Each nested search yields after every Lambert solve, propagation step or terrain sample. The nested searches return their value through a holder object, because an iterator cannot have an out parameter:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/DeorbitPlanner.cs L119-L120, L215-L243
// The search is an iterator that yields after each Lambert solve, UPFG step or terrain sample, so Step can stop it on the simulation thread's time budget.
// A nested search returns its value through a Result holder, because an iterator cannot have an out parameter.
// ...
    private IEnumerable<bool> Search()
    {
        DeorbitRequest request = _request;
        if (!request.Settings.IsValid || !double.IsFinite(request.SiteHeight)
            || !(request.Source.Period > 0) || !double.IsFinite(request.Source.Period)
            || request.Source.Eccentricity >= 1 || !(request.Mass > 0) || request.Model.Stages.Count == 0)
        {
            Status = "The approach needs a finite bound orbit, terrain, and an engine model.";
            yield break;
        }
        foreach (bool step in DirectSteps()) yield return step;
        if (Direct is { Refusal.Length: 0 })
        {
            Status = "Direct braking approach ready.";
            yield break;
        }
        yield return true;
        if (_brakingOnly)
        {
            Status = "The actual orbit has no safe braking approach. " + DirectRefusal;
            yield break;
        }
        if (request.Engines.Length == 0)
        {
            Status = DirectRefusal + " A deorbit burn needs active, supplied liquid engines.";
            yield break;
        }
        foreach (bool step in TransferSteps()) yield return step;
    }
```

2. **Make the finest-grain step yield.** A flown-arc check is an object whose `Advance()` does one sample and returns whether more remain; the iterator loops on it:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/DeorbitPlanner.cs L246-L262
private IEnumerable<bool> DirectSteps()
{
    var found = new Result<DirectBrakingPlan?>();
    foreach (bool step in FindDirectSteps(_request, found, _arrivalHint)) yield return step;
    Direct = found.Value;
    DirectRefusal = Direct?.Refusal ?? $"No approaching braking point was found within {ApproachOrbits} orbits.";
    if (Direct is not { Refusal.Length: 0 }) yield break;
    // After the node, the measured orbit decides braking. Execution error must not demand another node.
    if (!_brakingOnly && _request.Settings.ArrivalDescentDeg.HasValue
        && !MatchesArrivalAngle(_request.Settings, Direct.Position, Direct.Velocity))
    {
        RefuseDirect("The current orbit misses the selected arrival angle. A deorbit transfer is required.");
        yield break;
    }
    var arc = new DeorbitArcCheck(_request, _request.Source, _request.Epoch, Direct.IgnitionTime);
    while (arc.Advance()) yield return true;
    if (arc.Refusal.Length > 0) RefuseDirect(arc.Refusal);
```

3. **Advance it with two budgets.** `Step` runs the iterator until the per-call budget is spent, and a total work limit turns an overlong search into a refusal that names the reason. A thrown exception becomes a status, and the elapsed time is added to the work total in `finally`:

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

4. **Call `Step()` once per vehicle step**, then publish status for the UI and finish when `Complete` is set:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/Deorbit.cs L198-L202
DeorbitPlanner planner = _s.DeorbitPlanner;
planner.Step();
_s.LandingStatus = planner.Status;
_s.DirectBrakingRefusal = planner.DirectRefusal;
if (planner.Complete) FinishDeorbitSearch(vehicle, parent, planner);
```

5. **Gate and restart.** Re-check the preconditions every slice (idle engine, 1x warp, unchanged source orbit and settings) and restart the search from scratch when they change (`RestartDeorbitPlanning`). Do not try to resume against a changed world.
6. **Dispose the iterator** (`_search.Dispose()`) when the search ends early, so its `finally` blocks run.

A minimal reusable shape (adapted from the planner above; game-independent):

```csharp
// adapted from KSA-AdvancedFlightComputer/AdvancedFlightComputer/Features/Guidance/Modes/DeorbitPlanner.cs
public sealed class SlicedSearch : IDisposable
{
    private readonly IEnumerator<bool> _search;
    public bool Complete { get; private set; }
    public double WorkMilliseconds { get; private set; }
    public double WorkLimitMilliseconds { get; init; } = 500;

    public SlicedSearch(IEnumerable<bool> search) => _search = search.GetEnumerator();

    public void Step(double budgetMilliseconds = 3)
    {
        if (Complete) return;
        long start = Stopwatch.GetTimestamp();
        try
        {
            do
            {
                if (WorkMilliseconds + Stopwatch.GetElapsedTime(start).TotalMilliseconds >= WorkLimitMilliseconds)
                { Complete = true; return; }
                if (!_search.MoveNext()) { Complete = true; return; }
            } while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < budgetMilliseconds);
        }
        finally { WorkMilliseconds += Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
    }

    public void Dispose() => _search.Dispose();
}
```

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Modes/DeorbitPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/DeorbitPlanner.cs#L119-L262) | iterator search, `Step` and budgets |
| [AdvancedFlightComputer/Features/Guidance/Modes/Deorbit.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Deorbit.cs#L163-L202) | the per-step gate, restart and `Step()` call |
| [AdvancedFlightComputer/Features/Guidance/Modes/StockDeorbit.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/StockDeorbit.cs) | what happens to the finished plan |

## Variations and gotchas

- **Yield granularity is the knob.** A unit of work that takes longer than the budget overshoots it; split it (the planner yields per Lambert solve, UPFG step or terrain sample).
- **No threads, no races, but no parallelism.** The cost is paid in main-thread milliseconds on every frame the search runs.
- **A failed iterator cannot continue**, so the exception becomes a refusal naming the failure, not the last progress status.
- **Pair the budget with a total limit**; otherwise a pathological input holds the planner for ever.
- For a solve whose unit of work cannot be split, use a worker ([approach 1](run-an-expensive-solve-without-stalling-the-frame-1.md)) or a time limit ([approach 3](run-an-expensive-solve-without-stalling-the-frame-3.md)).

## See also

- [Overview](overview.md); [Plan and fly a powered landing, approach 4](../plan-and-fly-a-powered-landing/plan-and-fly-a-powered-landing-4.md)
- [Deorbit landing planner (mod feature)](../../../../mods/ksa-advancedflightcomputer/features/deorbit-landing-planner.md)
- [Wall-clock rate limit and throttled caches](run-an-expensive-solve-without-stalling-the-frame-5.md)

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
