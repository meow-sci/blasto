---
type: Mod Feature
title: Confirm a planned flyby by propagating it through the game's patched conics
description: Propagate a candidate burn with FlightPlan.CalculateBurnPatch and ComputeCompleteTrajectory filtered to the target, read the periapsis of the patch around the target, report a typed reason when there is none, and draw that plan in place of stock's preview
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [harmony-postfix, harmony-prefix]
hooks:
  - harmony-postfix KSA.TransferPlanner.OnPreRender
  - harmony-prefix KSA.TransferPlanner.DrawSelectedTransfer
lifecycle: [frame:draw-ui, frame:pre-render]
game_types: [KSA.FlightPlan, KSA.PatchedConic, KSA.Orbit, KSA.TransferPlanner]
tags: [flyby, patched-conics, flight-plan, encounter, propagation, preview]
---

The impulsive flyby solve says what periapsis it aimed for; the game's propagator decides what the
player will actually see in the map view. AFC runs the candidate departure through the same
patched-conic propagation stock uses for its own previews, reads the periapsis of the resulting
encounter with the target, and shows it next to the request ("periapsis reached"), or a reason such
as "No prediction: the preview did not resolve a target encounter".

## How it works

1. **Start from the live plan.** `source.FlightPlan.TryFindPatch(burnTime)` gives the patch the burn
   happens on; no patch means `NoCoveringPatch` (for example a departure outside the current flight
   plan) ([AdvancedFlightComputer/Features/Flyby/HohmannFlybyUI.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Flyby/HohmannFlybyUI.cs#L319-L349)).
2. **Apply the burn as a patch.** `FlightPlan.CreateUninitialized(source.Hash)`, then
   `plan.Patches.Add(plan.CalculateBurnPatch(prePatch, timeSincePe, dvVlf, burnTime))` with
   `timeSincePe = prePatch.Orbit.GetTimeSincePeriapsisThisOrbit(burnTime)`. This is the stock way to
   turn an impulsive VLF delta-v into the next patch ([current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L399)).[^ksa-flightplan]
3. **Propagate.** `plan.ComputeCompleteTrajectory(out errors, patchLimit: 8, polynomialOrderN: 8, encounterFilter: targetOrbiter, resolveImpactsCompletely: true)`
   finds SOI transitions, restricted to the target, and resolves impacts; `ImpactClearanceMargin` is
   the vehicle's bounding sphere so a grazing pass counts as an impact ([current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L214)).
4. **Read the encounter.** `FlybyPrediction.FromPlan` returns the periapsis of the first patch whose
   `Orbit.Parent` is the target; otherwise `NoEncounter`, and a non-finite periapsis is
   `InvalidPeriapsis` ([AdvancedFlightComputer/Features/Flyby/FlybyPrediction.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Flyby/FlybyPrediction.cs)). Statuses map
   to player-facing reasons.
5. **Multi-pass.** With several passes, the prediction uses the final pass's preview plan built by the
   multi-pass planner, and the readout uses the selected departure after any schedule shift.
6. **Recompute only on change.** The preview is rebuilt only when the cached departure, the covering
   patch's start and end, the clearance, the target radius or the floor changed.
7. **Draw it.** In the `TransferPlanner.OnPreRender` postfix, if a single-burn flyby is armed with a
   propagated plan, stock's plan window shows a Hohmann for the same vehicle, "Preview Selected
   Transfer" is on and stock reports `_transferCalculated`, the plan's lines are added with
   `FlightPlan.AddLineInstances` (generating spaced points first), and stock's own center-aimed preview
   is suppressed (see [planner preview trajectories](planner-preview-trajectories.md))
   ([AdvancedFlightComputer/Features/Flyby/HohmannFlybyUI.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Flyby/HohmannFlybyUI.cs#L351-L411)).

## Lifecycle and timing

The propagation runs on the main thread during the plan-window draw (`frame:draw-ui`), synchronously,
which is acceptable because it is cached and limited to 8 patches with a target filter. The lines are
added in `frame:pre-render`. The `_transferCalculated` gate matters: stock clears it on a source or
destination change and once the created burn's time passes while keeping its selected entry, so
without the gate a cached plan would keep painting a stale flyby.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/Flyby/FlybyPrediction.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Flyby/FlybyPrediction.cs) | Status enum, reasons, periapsis extraction |
| [AdvancedFlightComputer/Features/Flyby/HohmannFlybyUI.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Flyby/HohmannFlybyUI.cs) | `BuildPreview`, change detection, render gates, `RenderPreview` |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Flyby/FlybyPreviewTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Flyby/FlybyPreviewTest.cs) | Headless test of the prediction |

## Snippets

```csharp
// AdvancedFlightComputer/Features/Flyby/HohmannFlybyUI.cs L319-L343
    // FlightPlan.CalculateBurnPatch and ComputeCompleteTrajectory provide the same propagation used by stock previews.
    internal static FlybyPrediction BuildPreview(
        Vehicle source, IOrbiter targetOrbiter, IParentBody target,
        FlybyTargeting.FlybyResult? result, out FlightPlan? preview)
    {
        preview = null;
        if (result is not FlybyTargeting.FlybyResult departure)
            return new(FlybyPredictionStatus.NoDeparture);

        PatchedConic? prePatch = source.FlightPlan.TryFindPatch(departure.BurnTime);
        if (prePatch == null)
            return new(FlybyPredictionStatus.NoCoveringPatch);

        try
        {
            UniverseTime timeSincePe = prePatch.Orbit.GetTimeSincePeriapsisThisOrbit(departure.BurnTime);
            FlightPlan plan = FlightPlan.CreateUninitialized(source.Hash);
            plan.ImpactClearanceMargin = source.BoundingSphereRadiusBody;
            plan.Patches.Add(plan.CalculateBurnPatch(
                prePatch, timeSincePe, departure.DvVlf, departure.BurnTime));
            plan.ComputeCompleteTrajectory(out bool errors, 8, 8, targetOrbiter, resolveImpactsCompletely: true);
            if (errors) return new(FlybyPredictionStatus.PropagationFailed);
            preview = plan;
            return FlybyPrediction.FromPlan(plan, target);
        }
```

## Game APIs touched

| Member | Use |
|---|---|
| `FlightPlan.CreateUninitialized`, `CalculateBurnPatch`, `ComputeCompleteTrajectory`, `ImpactClearanceMargin`, `AddLineInstances` ([current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs)) | Propagation and drawing |
| `FlightPlan.TryFindPatch` ([current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L304)) | Patch covering the burn |
| `Orbit.GetTimeSincePeriapsisThisOrbit`, `Periapsis`, `Parent` ([current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs)) | Burn phase and encounter periapsis |

## Pitfalls and version notes

- Pass the target as `encounterFilter`; otherwise an unrelated encounter can end the patch chain
  first.
- Use the same clearance margin stock uses for the vehicle, or "impact" and "flyby" disagree with the
  map view.
- The preview shows the impulsive result; a long finite burn flies a slightly different periapsis.

## See also

- [Flyby periapsis targeting](flyby-periapsis-targeting.md)
- [Planner preview trajectories](planner-preview-trajectories.md)
- [Multi-pass preview rendering](multi-pass-preview-rendering.md)
- [Predict a planned burn's encounter with CalculateBurnPatch and ComputeCompleteTrajectory filtered to the target](../../../howto/flight-and-orbits/transfers/aim-a-transfer-at-a-flyby-periapsis/aim-a-transfer-at-a-flyby-periapsis-2.md) - how-to article demonstrating this feature

[^ksa-flightplan]: KSA.FlightPlan (decompiled, build 2026.10.10.5554)
