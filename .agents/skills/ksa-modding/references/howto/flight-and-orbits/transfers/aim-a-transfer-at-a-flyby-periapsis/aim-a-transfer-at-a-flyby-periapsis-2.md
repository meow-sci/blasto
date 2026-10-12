---
type: How-To
title: Predict a planned burn's encounter with CalculateBurnPatch and ComputeCompleteTrajectory filtered to the target
description: Run a candidate departure burn through the same patched-conic propagation stock uses for its previews, read the periapsis of the resulting encounter with the target, and draw the plan in place of stock's preview
category: flight-and-orbits
group: transfers
topic: aim-a-transfer-at-a-flyby-periapsis
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/flyby-encounter-prediction]
techniques: [harmony-postfix, patched-conic]
hooks:
  - harmony-postfix KSA.TransferPlanner.OnPreRender
  - harmony-prefix KSA.TransferPlanner.DrawSelectedTransfer
lifecycle: [frame:draw-ui, frame:pre-render]
game_types: [KSA.FlightPlan, KSA.PatchedConic, KSA.Encounter, KSA.TransferPlanner, KSA.Vehicle, KSA.IOrbiter]
tags: [flyby, encounter, periapsis, flight-plan, propagation, preview, patched-conic]
---

Outcome: the impulsive flyby solve says what periapsis it aimed for; the game's propagator decides what the player will actually see on the map. AFC runs the candidate departure through the same propagation stock uses for its previews, reads the periapsis of the resulting encounter, and shows it as "periapsis reached" or a reason such as "No prediction: the preview did not resolve a target encounter".
[Confirm a planned flyby by propagating it through the game's patched conics](../../../../mods/ksa-advancedflightcomputer/features/flyby-encounter-prediction.md) Prerequisites: StarMap and Harmony.

## Background: how the game does it

`FlightPlan.CreateUninitialized(IdHash)` makes a plan with no vehicle trajectory behind it; `CalculateBurnPatch(prePatch, burnTimeFromPe, dvVlf, startTime)` rotates the VLF delta-v into CCI at the burn time and creates the next patch from the new state (`Orbit.CreateFromStateCci`); `ComputeCompleteTrajectory` then finds SOI transitions, with an optional `encounterFilter` restricting them to one body and
`resolveImpactsCompletely` finishing the impact search that a worker-less plan would otherwise leave incremental.[^ksa-flightplan] [current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L162-L170) [current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L214-L222) [current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L399-L410) `FlightPlan.ImpactClearanceMargin` widens the impact test; the mod sets it to the vehicle's bounding sphere so a grazing pass counts as an impact.

## Lifecycle and timing

The propagation runs on the main thread during the plan-window draw (`frame:draw-ui`), synchronously, acceptable because it is cached and limited to 8 patches with a target filter. The lines are added in `frame:pre-render`. The `_transferCalculated` gate matters: stock clears it on a source or destination change and once the created burn's time passes while keeping its selected entry, so without the gate a cached plan would keep painting a stale flyby. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Find the patch** the burn happens on: `source.FlightPlan.TryFindPatch(burnTime)`; none means `NoCoveringPatch` (for example a departure outside the current flight plan).
2. **Apply the burn as a patch and propagate:**

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

3. **Read the encounter.** Return the periapsis of the first patch whose `Orbit.Parent` is the target; otherwise `NoEncounter`; a non-finite periapsis is `InvalidPeriapsis`. Map statuses to player-facing reasons (`FlybyPrediction.FromPlan`, [AdvancedFlightComputer/Features/Flyby/FlybyPrediction.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Flyby/FlybyPrediction.cs)).
4. **With several passes,** predict from the final pass's preview plan built by the multi-pass planner (see [Create, chain and read stock maneuver nodes from code](../../autopilot/create-and-read-maneuver-nodes/overview.md)), using the selected departure after any schedule shift.
5. **Recompute only on change:** the cached departure, the covering patch's start and end, the clearance, the target radius or the floor.
6. **Draw it.** In a postfix on `TransferPlanner.OnPreRender`, when a single-burn flyby is armed with a propagated plan, stock's plan window shows a Hohmann for the same vehicle, "Preview Selected Transfer" is on and stock reports `_transferCalculated`, add the plan's lines with `FlightPlan.AddLineInstances` (generating spaced points first), and suppress stock's center-aimed preview
   with a prefix on `DrawSelectedTransfer`.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Flyby/FlybyPrediction.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Flyby/FlybyPrediction.cs) | status enum, reasons, periapsis extraction |
| [AdvancedFlightComputer/Features/Flyby/HohmannFlybyUI.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Flyby/HohmannFlybyUI.cs#L319-L411) | `BuildPreview`, change detection, render gates, `RenderPreview` |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Flyby/FlybyPreviewTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Flyby/FlybyPreviewTest.cs) | headless test of the prediction |

## Variations and gotchas

- **Patch limit 8, polynomial order 8** keep the synchronous call cheap; raise them only if an encounter is missed.
- **Typed failure reasons** are cheaper to explain than a bare failure: no departure, no covering patch, propagation failed, no encounter, invalid periapsis.
- **Lines are added in pre-render,** after the draw, so the plan must be cached across the two phases.
- A grazing pass reads as an impact because of the clearance margin; that is intended for safety, not an error.

## See also

- [Overview](overview.md); [impact-parameter offset](aim-a-transfer-at-a-flyby-periapsis-1.md)
- [Confirm a planned flyby by propagating it through the game's patched conics](../../../../mods/ksa-advancedflightcomputer/features/flyby-encounter-prediction.md), [Preview a planned burn's trajectory with the game's own orbit lines](../../../../mods/ksa-advancedflightcomputer/features/planner-preview-trajectories.md)
- Related topics: [Draw a predicted trajectory and markers in the map view, and control orbit lines](../../map-view/draw-a-predicted-trajectory-and-map-markers/overview.md)

[^ksa-flightplan]: KSA.FlightPlan (decompiled, build 2026.10.10.5554)
