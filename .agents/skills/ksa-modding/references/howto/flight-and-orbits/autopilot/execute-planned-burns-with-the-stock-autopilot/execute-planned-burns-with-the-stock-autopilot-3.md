---
type: How-To
title: Let stock compute the ignition timing before you arm Auto
description: Load a computed node, hold the engine off at minimum throttle until a vehicle worker has filled the BurnTarget, check that stock's alignment and ignition window fit, then give Auto back
category: flight-and-orbits
group: autopilot
topic: execute-planned-burns-with-the-stock-autopilot
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/deorbit-landing-planner]
techniques: [harmony-prefix, reflection]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker, reflection KSA.Vehicle._manualControlInputs]
lifecycle: [frame:dispatch-solvers, physics:vehicle-solver]
game_types: [KSA.FlightComputer, KSA.BurnTarget, KSA.BurnPlan, KSA.Burn, KSA.Vehicle, KSA.Program]
tags: [auto-burn, burn-target, ignition-timing, deorbit, prepare-worker, minimum-throttle]
---

Outcome: a node that stock Auto flies, armed only after stock has computed when it would ignite and the mod verified that the alignment and burn fit in the
available time. KSA-AdvancedFlightComputer's stock deorbit does this before handing over to landing guidance.[Plan a deorbit to a landing site and let stock Auto fly the node](../../../../mods/ksa-advancedflightcomputer/features/deorbit-landing-planner.md)
Prerequisites: StarMap and Harmony; the step runs in a `Vehicle.PrepareWorker` prefix.

## Background: how the game does it

A new `BurnTarget` has no duration and an `IgnitionTime` of `UniverseTime.EndOfTime` until a vehicle worker updates it: stock computes the burn timing on the worker in
`FlightComputer.UpdateBurnTarget` and the result is read back one or more frames later. Stock ignites half the burn before the node at the previewed throttle, and the
preview throttle comes from the manual throttle input, so holding the throttle at the vehicle's minimum bounds the ignition lead and duration.[^ksa-burntarget]
[current/decomp/KSA/BurnTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnTarget.cs#L6-L24) `FlightComputer.ConservativeFlipTime`, `PlannedBurnThrottle` and `ActiveEnginePerformanceMax` are the worker results the mod reads; `Vehicle.GetMinThrottle()` is the lower bound.[^ksa-flightcomputer]
[current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L95-L121) [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1264)

## Lifecycle and timing

All of it runs in the per-vehicle `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`, main thread): the input queue is drained and the vehicle solver has not been queued, so burn-mode and
flight-computer writes are what the next worker step sees. Burn-plan edits there are not fully isolated: `Program.PrepareFrame` queues the nearest-orbit job just before `ExecuteNextVehicleSolvers`,
and its `SetNearestOrbitPoint` walks the controlled vehicle's `BurnPlan` on its own worker while `FlightComputer.AddBurn` adds to and sorts the list. Stock makes its own burn edits in `ApplyInputEvents`
and the `RaisePendingAlerts` loop, before that job is queued. See [Mutate simulation state safely](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. **Refuse if another burn is planned.** The node must be the first executable burn, or `AddBurn` will not load it.
2. **Create and add the node, then verify it is loaded** (`AddBurn` loads in Manual):

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/StockDeorbit.cs L110-L148
// FlightComputer.AddBurn loads the node in Manual, so Auto is selected only after the loaded target is verified.
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

3. **Hold the preview throttle.** Remember the player's throttle, set the minimum throttle (at least 0.001) and engine off through the manual inputs so stock previews the burn at the minimum.
4. **Wait for the worker.** Each step, check the controls did not change and `IgnitionTime != EndOfTime`.
5. **Arm only if it fits.** Require a bounded window: a positive `BurnDuration`, the preview throttle at the engine minimum, the preparation start in the future and the burn finishing before the next phase. Then restore the
   throttle and give Auto back:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/StockDeorbit.cs L150-L181
private static void ArmStockDeorbitAuto(Vehicle vehicle, DeorbitPlan plan, bool nodeLoaded)
{
    FlightComputer fc = vehicle.FlightComputer;
    ref ManualControlInputs inputs = ref ManualInputs(vehicle);
    if (!nodeLoaded || fc.BurnMode != FlightComputerBurnMode.Manual || inputs.EngineOn
        || inputs.EngineThrottle != _s.DeorbitPreviewThrottle)
    { StopStockDeorbit(vehicle, "Controls changed during the stock timing check. The node remains Manual.", keepNode: true); return; }
    if (plan.DepartureTime <= SimNow() + PrepLeadTime)
    { StopStockDeorbit(vehicle, "Stock timing was not ready before the preparation window. The node remains Manual.", keepNode: true); return; }
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

   Otherwise stop with a reason and leave the node in Manual for the player.
6. **Warp optionally** to just before stock's preparation start, stopping only the warp the mod started.
7. **Watch for completion** on the kept `BurnTarget` (the node itself may already be removed by stock or by automatic removal); a changed node or target, or a player takeover, stops the sequence and preserves the player's target.
   After the burn, replan braking from the measured orbit and mass.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Modes/StockDeorbit.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/StockDeorbit.cs) | node load, stock timing check, Auto, warp, release |
| [AdvancedFlightComputer/Features/Guidance/Modes/DeorbitPlanner.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/DeorbitPlanner.cs) | iterator-based Lambert search with budgets |
| [AdvancedFlightComputer/Features/Guidance/Modes/Landing.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Landing.cs) | landing phase machine and hand-overs |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceStockDeorbitTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceStockDeorbitTest.cs) | headless test of the stock-node path |

## Variations and gotchas

- **Time-sliced planning.** The Lambert search is written as an `IEnumerable<bool>` iterator that yields after each solve; `Step(budgetMs = 3)` advances it until the per-call budget or the 500 ms work limit is spent, so the main thread
  pays about 3 ms per frame without a thread.
- **`FlightComputer.SolveBurnThrottle` cannot go below the active engine minimum**, which is why the minimum-throttle preview is the bound.
- **Control hand-over.** Clear the existing plan first; the mod stops with "Clear the existing maneuver plan before executing the landing" if a burn or Auto is already set.
- Race note: adding a burn from the prefix can race the nearest-orbit job; keep the add rare and verify afterwards.

## See also

- [Overview](overview.md); [state machine](execute-planned-burns-with-the-stock-autopilot-1.md)
- [Plan a deorbit to a landing site and let stock Auto fly the node](../../../../mods/ksa-advancedflightcomputer/features/deorbit-landing-planner.md)
- [Execute burns with RCS translation and throttle inputs from code](../execute-burns-with-rcs-and-throttle-inputs/overview.md)

[^ksa-burntarget]: KSA.BurnTarget (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
