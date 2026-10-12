---
type: How-To
title: Plan a deorbit burn to a landing site with a time-sliced Lambert search, then let a stock node fly it
description: Search for a deorbit transfer to a rotating site as a C# iterator advanced a few milliseconds per frame under work budgets, then load it as a stock maneuver node and let the stock autopilot compute the ignition timing
category: flight-and-orbits
group: guidance
topic: plan-and-fly-a-powered-landing
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/deorbit-landing-planner]
techniques: [harmony-prefix, custom-physics, iterator-time-slicing]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers, physics:vehicle-solver]
game_types:
  - KSA.Vehicle
  - KSA.Celestial
  - KSA.IParentBody
  - KSA.FlightComputer
  - KSA.Burn
  - KSA.BurnTarget
  - KSA.FlightPlan
  - KSA.PatchedConic
  - KSA.Orbit
  - KSA.Universe
tags: [landing, deorbit, lambert, time-slicing, iterator, maneuver-node, stock-autopilot, rotating-body]
---
Outcome: a craft in orbit is taken to a landing site on a rotating body: the planner finds a deorbit burn (a Lambert transfer to a gate uprange of the site, or a direct braking approach), the burn is loaded as an ordinary stock maneuver node, and the stock Auto autopilot flies it. The search never blocks the frame, and the ignition timing is computed by stock rather than guessed. The braking and powered descent that follow are [approach 1](plan-and-fly-a-powered-landing-1.md) or [approach 2](plan-and-fly-a-powered-landing-2.md), ending in the [hover](plan-and-fly-a-powered-landing-3.md). KSA-AdvancedFlightComputer (AFC) is the reference.[Deorbit landing planner](../../../../mods/ksa-advancedflightcomputer/features/deorbit-landing-planner.md)
Prerequisites: StarMap and Harmony. For node creation and the stock autopilot see [Create, chain and read stock maneuver nodes from code](../../autopilot/create-and-read-maneuver-nodes/overview.md) and [Execute planned burns with the stock autopilot](../../autopilot/execute-planned-burns-with-the-stock-autopilot/overview.md).

## Background: how the game does it

- **Orbit and flight plan.** `FlightPlan.TryFindPatch(UniverseTime)` returns the `PatchedConic` covering a time; its `Orbit` supplies `GetStateVectorsAt` and `GetPointAt`.[^ksa-flightplan] [current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L304-L304)
- **A burn node.** `Burn.Create(OrbitPointCce point, double time, double3 deltaVVlf, PatchedConic patch, Vehicle vehicle)` builds the node and `FlightComputer.AddBurn(burn)` loads it; the flight computer then holds a `BurnTarget` (`IgnitionTime`, `AlignmentStartTime`, `BurnDuration`) and a `BurnMode` of `Manual` or `Auto`.[^ksa-burn] [^ksa-flightcomputer] [current/decomp/KSA/Burn.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Burn.cs#L185-L185) [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L2209-L2209)
- **Stock computes the timing on the vehicle worker.** A new `BurnTarget` has an `EndOfTime` ignition and zero duration until a worker updates it; stock ignites half the burn before the node at the previewed throttle.[^ksa-burntarget]
- **A rotating site.** `IParentBody.GetCcf2Cci(UniverseTime)` rotates a body-fixed direction at any time; `Celestial.GetTerrainHeightFromDirCcf` supplies terrain height for clearance checks.[^ksa-iparentbody] [^ksa-celestial]

## Lifecycle and timing

- **Where:** the whole chain runs inside the per-vehicle `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`, main thread), after the input queue has been drained and before the vehicle solver is queued. Burn-mode and flight-computer writes there are what the next worker step sees. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).
- **Search cost:** about 3 ms of main-thread time per frame (the `Step` budget), with a 500 ms total work limit.
- **Stock timing:** computed on the vehicle worker (`physics:vehicle-solver`) in `FlightComputer.UpdateBurnTarget` and read back one or more frames later, so the planner polls.
- **A known race:** adding the node directly from the prefix can overlap the nearest-orbit job, which walks the burn list for cursor hover on its own worker (`Program.PrepareFrame` queues it just before `ExecuteNextVehicleSolvers`). Queue the node through `InputEvents.BurnUpdateBuffer` (applied in the next frame's drain) or add it from a `FlightComputer.RaisePendingAlerts` postfix to avoid it; see [Create, chain and read stock maneuver nodes from code](../../autopilot/create-and-read-maneuver-nodes/overview.md).

## Steps

1. **Wait for a quiet craft.** Planning waits for 1x warp, an idle engine, no minimum pulse remaining and a settled orbit, and restarts when the source state or the settings change:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/Deorbit.cs L175-L196, L198-L202
// Only the capture waits for idle engines and 1x warp. A direct coast checks its braking model again at ignition.
if (_s.LandingPhase == LandingPhase.DeorbitPlanning)
{
    if (vehicle.FlightComputer.BurnMode == FlightComputerBurnMode.Auto
        || ManualInputs(vehicle).EngineOn || KsaEnginePerf.RemainingMinimumPulse(vehicle) > 0)
    {
        RestartDeorbitPlanning("Planning waits for the active burn and engine pulse to finish.");
        return;
    }
    if (Universe.SimulationSpeed > 1)
    {
        ClearDeorbitPlanState();
        _s.LandingStatus = "Planning waits for time warp at 1x or less.";
        return;
    }
}
if (_s.DeorbitRequest != null && DeorbitSourceChanged(vehicle, orbit, parent))
{
    RestartDeorbitPlanning("The source state or settings changed. Planning again.");
    return;
}
if (_s.LandingPhase != LandingPhase.DeorbitPlanning) return;
// ...
DeorbitPlanner planner = _s.DeorbitPlanner;
planner.Step();
_s.LandingStatus = planner.Status;
_s.DirectBrakingRefusal = planner.DirectRefusal;
if (planner.Complete) FinishDeorbitSearch(vehicle, parent, planner);
```

2. **Write the search as an iterator** that yields after each Lambert solve, propagation step or terrain sample. First a direct braking approach is tried; if the craft's own orbit has no safe approach, the transfer search runs:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/DeorbitPlanner.cs L215-L243
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

3. **Target the site where it will be, not where it is.** The site is a body-fixed direction carried into CCI at the arrival time, and the gate is rotated uprange about the orbit normal:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/DeorbitPlanner.cs L82-L98
internal double3 SiteAt(double time) => SiteCcf.Transform(Parent.GetCcf2Cci(new UniverseTime(time)));

internal double3 GateAt(double time, double3 normal) =>
    GuidanceWindow.RotateAbout(SiteAt(time), normal, -Settings.GateUprange / Parent.MeanRadius) * GateRadius;

internal double Terrain(double3 directionCcf) => Parent is Celestial celestial
    ? celestial.GetTerrainHeightFromDirCcf(directionCcf) : double.NaN;

internal double Clearance(double3 position, double time)
{
    double radius = position.Length();
    if (!(radius > 0) || !DeorbitPlanner.Finite(position)) return double.NaN;
    double terrain = Terrain((position / radius).Transform(Parent.GetCci2Ccf(new UniverseTime(time))));
    double ocean = Parent.GetOceanReference() == null ? double.NegativeInfinity : 0;
    return radius - Parent.MeanRadius - Math.Max(terrain, ocean) - VehicleRadius;
}

```

4. **Advance the iterator under budgets.** `Step(3)` runs it until the per-call budget is spent; a total work limit turns a long search into a refusal that names the reason, and a thrown exception becomes a status instead of a crash:

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

5. **Load the plan as a stock node.** Verify the plan still matches the flight plan, convert the delta-v to the burn frame, create the node, add it, and confirm the flight computer loaded it as its target. Hold the engine off and the throttle at the minimum so stock's preview bounds the ignition lead and duration:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/StockDeorbit.cs L111-L148
private static void LoadStockDeorbitNode(Vehicle vehicle, IParentBody parent, DeorbitPlan plan)
{
    FlightComputer fc = vehicle.FlightComputer;
    if (plan.DepartureTime <= SimNow() + PrepLeadTime)
    { StopStockDeorbit(vehicle, "The deorbit node window expired. Execute again at 1x.", keepNode: false); return; }
    if (fc.BurnPlan.FindFirstExecutableBurn() != null || fc.BurnMode == FlightComputerBurnMode.Auto)
    { StopStockDeorbit(vehicle, "Clear the existing maneuver plan before executing the landing.", keepNode: true); return; }
    if (!TryTakeCraft(vehicle, acquire: false)) return;
    _s.DeorbitNodeClaimed = true;
    PatchedConic patch = vehicle.FlightPlan.TryFindPatch(new UniverseTime(plan.DepartureTime));
    if (patch == null || !ReferenceEquals(patch.Orbit.Parent, parent))
    { StopStockDeorbit(vehicle, "The deorbit time has no flight-plan patch on this body.", keepNode: false); return; }
    StateVectors state = patch.Orbit.GetStateVectorsAt(new UniverseTime(plan.DepartureTime));
    if (!DeorbitPlanner.SameState(state, plan.DeparturePosition, plan.DepartureVelocity - plan.DeltaV))
    { StopStockDeorbit(vehicle, "The flight plan changed after the deorbit solve. Execute again.", keepNode: false); return; }

    double3 dvVlf = plan.DeltaV.Transform(state.GetVlf2ParentCci().OrIdentity().Inverse());
    _s.DeorbitNodeDeltaV = dvVlf;
    _s.DeorbitNodeDeltaVCci = plan.DeltaV;
    _s.DeorbitNode = Burn.Create(patch.Orbit.GetPointAt(new UniverseTime(plan.DepartureTime)),
        plan.DepartureTime, dvVlf, patch, vehicle);
    _s.DeorbitNodeTime = _s.DeorbitNode.ImpulseTime.Seconds();
    _s.DeorbitNode.IsGizmoActive = false;
    fc.AddBurn(_s.DeorbitNode);
    _s.DeorbitTarget = fc.Burn;
    if (!ReferenceEquals(fc.BurnPlan.FindFirstExecutableBurn(), _s.DeorbitNode) || !StockDeorbitTargetUnchanged)
    { StopStockDeorbit(vehicle, "Stock could not load the deorbit node.", keepNode: false); return; }

    // Stock previews the burn at the throttle set here, so the minimum throttle bounds its ignition lead and duration.
    ref ManualControlInputs preview = ref ManualInputs(vehicle);
    _s.DeorbitSavedThrottle = preview.EngineThrottle;
    _s.DeorbitPreviewThrottle = Math.Max(vehicle.GetMinThrottle(), 0.001f);
    _s.DeorbitThrottleHeld = true;
    preview.EngineOn = false;
    preview.EngineThrottle = _s.DeorbitPreviewThrottle;
    _s.LandingPhase = LandingPhase.DeorbitNodePending;
    _s.LandingStatus = "Checking stock ignition timing with the engine off.";
}
```

6. **Ask stock for the timing and arm Auto only if it fits.** Poll until the `BurnTarget` has a finite ignition; require preparation time (stock's alignment, from `FlightComputer.ConservativeFlipTime`) before ignition and the burn to end before braking preparation; only then restore the throttle and give Auto back:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/StockDeorbit.cs L159-L181
    // A new BurnTarget has no duration and an EndOfTime ignition until a vehicle worker updates it.
    if (_s.DeorbitTarget.IgnitionTime == UniverseTime.EndOfTime) return;

    double duration = _s.DeorbitTarget.BurnDuration;
    double ignition = _s.DeorbitTarget.IgnitionTime.Seconds();
    double preparationStart = StockPreparationStart(_s.DeorbitTarget);
    double latestFinish = plan.DepartureTime + duration / 2 + _s.DeorbitRequest.MinimumPulse;
    // FlightComputer.SolveBurnThrottle cannot select a throttle below its active engine minimum.
    // The stock estimate at that minimum bounds the ignition lead and burn finish before Auto is armed.
    bool minimumPreview = fc.ActiveEnginePerformanceMax.MinThrottle > 0
        && fc.PlannedBurnThrottle <= fc.ActiveEnginePerformanceMax.MinThrottle + 1e-6;
    if (!minimumPreview || !(duration > 0) || !double.IsFinite(latestFinish + ignition + preparationStart)
        || preparationStart < SimNow() || latestFinish >= plan.ArrivalTime - PrepLeadTime)
    { StopStockDeorbit(vehicle, "Stock has no bounded burn window with enough preparation time. The node remains Manual.", keepNode: true); return; }

    RestoreDeorbitPreviewThrottle(vehicle);
    if (!StockBurnMode.GiveBackAuto(vehicle, _s.DeorbitTarget))
    { StopStockDeorbit(vehicle, "Stock could not arm the deorbit node. The node remains Manual.", keepNode: true); return; }
    _s.DeorbitAutoArmed = true;
    _s.LandingPhase = LandingPhase.DeorbitBurn;
    _s.LandingStatus = "Stock Auto will align and execute the deorbit node.";
    GuidanceLog.Info(vehicle, $"stock deorbit node armed: time {plan.DepartureTime:F3} s, delta-v {plan.DeltaV.Length():F3} m/s, minimum-throttle duration {duration:F3} s.");
}
```

7. **Warp, watch and hand over.** Optionally warp to just before stock's preparation start (stopping only the warp AFC started). Completion is stock's delta-v reversal on the kept `BurnTarget`; a changed node or target, or a player takeover, stops the landing and preserves the player's target. After the burn, braking is planned again from the measured orbit and mass.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Modes/DeorbitPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/DeorbitPlanner.cs) | iterator Lambert search with budgets |
| [AdvancedFlightComputer/Features/Guidance/Modes/StockDeorbit.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/StockDeorbit.cs) | node load, stock timing check, Auto, warp, release |
| [AdvancedFlightComputer/Features/Guidance/Modes/Deorbit.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Deorbit.cs) | planning gate and coast logic |
| [AdvancedFlightComputer/Features/Guidance/Modes/Landing.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Landing.cs#L15-L24) | landing phase machine and hand-overs |
| [AdvancedFlightComputer/Features/Guidance/Modes/SitePasses.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/SitePasses.cs) | closed-form site passes |

## Variations and gotchas

- **`FlightComputer.AddBurn` loads the node in Manual**; select Auto only after verifying the loaded target.
- **Poll, do not assume.** A new `BurnTarget` has no duration and an `EndOfTime` ignition until a worker updates it.
- **The stock solver picks its transfer direction from global Z**; AFC rotates the requested orbit normal to local Z before solving.
- **Time-slicing is not threading.** Everything stays on the main thread, so no locks are needed, at the cost of main-thread milliseconds; the heavier alternative is [approach 2](plan-and-fly-a-powered-landing-2.md).
- **Arrival constraint.** Arrival must be between 10 degrees below and 0.5 above the local horizon unless the player sets an angle; terrain along both flown arcs is checked with a 1 km margin.
- The author reports in-game flights only for single-craft ascent; the landing chain is covered by harness tests.

## See also

- [Overview](overview.md); [G-FOLD](plan-and-fly-a-powered-landing-1.md); [6-DOF MPC](plan-and-fly-a-powered-landing-2.md); [Hover](plan-and-fly-a-powered-landing-3.md)
- [Deorbit landing planner (mod feature)](../../../../mods/ksa-advancedflightcomputer/features/deorbit-landing-planner.md)
- [Aim a transfer at a flyby periapsis](../../transfers/aim-a-transfer-at-a-flyby-periapsis/overview.md) and [Build a site frame and compute a launch window](../../transfers/build-a-site-frame-and-compute-a-launch-window/overview.md)
- [Run an expensive solve without stalling the frame](../run-an-expensive-solve-without-stalling-the-frame/overview.md)

[^ksa-flightplan]: KSA.FlightPlan (decompiled, build 2026.10.10.5554)
[^ksa-burn]: KSA.Burn (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-burntarget]: KSA.BurnTarget (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
