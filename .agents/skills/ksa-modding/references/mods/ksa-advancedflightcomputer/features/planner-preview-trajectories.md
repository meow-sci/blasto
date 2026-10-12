---
type: Mod Feature
title: Preview a planned burn's trajectory with the game's own orbit lines
description: Build a FlightPlan for a candidate burn with OrbitalTransfers.BuildFlightPlan, draw it in a TransferPlanner.OnPreRender postfix with FlightPlan.AddLineInstances, and suppress stock's selected-transfer preview while a replacement is shown
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [harmony-postfix, harmony-prefix, reflection]
hooks:
  - harmony-postfix KSA.TransferPlanner.OnPreRender
  - harmony-postfix KSA.TransferPlanner.DrawPlanWindow
  - harmony-prefix KSA.TransferPlanner.DrawSelectedTransfer
  - harmony-prefix KSA.TransferPlanner.DrawSelectedTransferUi
lifecycle: [frame:draw-ui, frame:pre-render]
game_types:
  - KSA.TransferPlanner
  - KSA.FlightPlan
  - KSA.OrbitalTransfers
  - KSA.PatchedConic
  - KSA.Orbit
  - KSA.UpdateTaskUtils
  - KSA.CelestialSystem
tags: [map-view, orbit-lines, flight-plan, preview, transfer-planner, markers]
---

With PREVIEW ORBIT checked, the map view shows the trajectory the quick-tool burn would produce in
the same style as stock maneuver previews, with apsis and node markers; PREVIEW FLIGHT PLAN opens a
stock-styled flight plan window for it. For flyby targeting, the retargeted trajectory replaces
stock's center-aimed preview instead of being drawn next to it. Nothing here draws lines itself: AFC
builds real `FlightPlan` objects and lets the game render them.

## How it works

1. **Build the plan (draw time).** For the current `ManeuverResult`, `BuildTransferEntry` creates
   `FlightPlan.CreateUninitialized(source.Hash)`, sets `ImpactClearanceMargin` to the vehicle's bounding
   sphere, wraps the burn in an `OrbitalTransfers.TransferData` and calls the same
   `OrbitalTransfers.BuildFlightPlan(ref plan, info, start, dvVlf, ...)` stock uses for porkchop
   entries; if the impact search did not resolve, it runs `ComputeCompleteTrajectory(..., resolveImpactsCompletely: true)`.
   The result is wrapped in a stock `PorkChopEntry` so stock-shaped helpers can consume it
   ([AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.Preview.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.Preview.cs#L60-L92),
   [current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L298)). A chained maneuver (after a pending burn) uses the forward-chain
   planner instead (see [multi-pass burn splitting](multi-pass-burn-splitting.md)).
2. **Draw the lines (pre-render).** A postfix on `TransferPlanner.OnPreRender(IViewport)` calls
   `RenderOrbitPreview` for AFC plan types, or the multi-pass/flyby overlays for Hohmann
   ([AdvancedFlightComputer/Features/PlanWindow/Patch_OnPreRender.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_OnPreRender.cs)). If the first patch has no
   cached points (`Orbit.IsMissingPoints()`), it fills them with `UpdateTaskUtils.GenerateSpacedPoints(patch)`
   and `Orbit.UpdateCachedPoints`, then calls
   `FlightPlan.AddLineInstances(viewport, source, isActive: true, drawVehiclePosition: false, NaN, NaN, isPostBurnOrbit: true)`
   ([AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.Preview.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.Preview.cs#L103-L135)).
3. **Markers (draw UI).** Apsis/node markers use `FlightPlan.DrawUi(viewport, Astronomical.UiContext, tintDanger: true)`
   from the window draw, and a postfix on `DrawPlanWindow` draws AFC's own plan markers
   (`PlanWindowMarkers`).
4. **Suppress stock's preview.** While a single-burn flyby is armed, stock's selected-transfer preview
   is the impact trajectory the retarget replaces. Prefixes on the private
   `TransferPlanner.DrawSelectedTransfer(IViewport)` (lines, called from `OnPreRender`) and
   `DrawSelectedTransferUi(IGameViewport)` (markers, called from `DrawPlanWindow`) return `false` in that
   case. They bind through `TargetMethod()` to validated soft-anchor handles and are only applied as a
   pair, so lines and markers cannot disagree; both fail open (stock preview stays) on an exception
   ([AdvancedFlightComputer/Features/PlanWindow/Patch_TransferPlanner_DrawSelectedTransfer_Flyby.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_TransferPlanner_DrawSelectedTransfer_Flyby.cs),
   [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L130-L151)). Stock's `_displaySelectedTransfer` checkbox drives
   both stock's preview and AFC's Hohmann overlay, so the player manages one switch.

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Build `FlightPlan` | `TransferPlanner.DrawPlanWindow` prefix, main thread | `frame:draw-ui` |
| `AddLineInstances` | `TransferPlanner.OnPreRender`, called from `CelestialSystem.OnPreRender` via `Universe.OnPreRender` in `Program.OnPreRender` ([current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L541-L549))[^ksa-celestialsystem] | `frame:pre-render` |
| Stock line suppression | `DrawSelectedTransfer`, inside `TransferPlanner.OnPreRender` ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1679-L1690)) | `frame:pre-render` |
| Stock marker suppression | `DrawSelectedTransferUi`, inside `DrawPlanWindow` ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L645-L655)) | `frame:draw-ui` |

Stock adds its own selected-transfer lines from the same `OnPreRender` call, so AFC's lines join the
frame's orbit-line instances at the same point, after the UI pass and before `Program.RenderGame`
records the frame; the plan itself is built earlier, in the draw, from main-thread orbit state. The postfix also ticks window state, because the
keybind and View menu can close the window without its body drawing.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.Preview.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.Preview.cs) | Plan building, marker drawing, line drawing, flight-plan window |
| [AdvancedFlightComputer/Features/PlanWindow/Patch_OnPreRender.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_OnPreRender.cs) | Pre-render postfix dispatch |
| [AdvancedFlightComputer/Features/PlanWindow/Patch_TransferPlanner_DrawSelectedTransfer_Flyby.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_TransferPlanner_DrawSelectedTransfer_Flyby.cs) | Stock line suppression |
| [AdvancedFlightComputer/Features/PlanWindow/Patch_TransferPlanner_DrawSelectedTransferUi_Flyby.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_TransferPlanner_DrawSelectedTransferUi_Flyby.cs) | Stock marker suppression |
| [AdvancedFlightComputer/Features/PlanWindow/PlanWindowMarkers.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/PlanWindowMarkers.cs) | Marker postfix |

## Snippets

```csharp
// AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.Preview.cs L120-L135
        FlightPlan flightPlan = _lastEntry.FlightPlan;
        if (flightPlan.Patches[0].Orbit.IsMissingPoints())
        {
            foreach (PatchedConic patch in flightPlan.Patches)
            {
                patch.HidePatch = false;
                MemoryOwner<OrbitPointCce> points = UpdateTaskUtils.GenerateSpacedPoints(patch);
                patch.Orbit.UpdateCachedPoints(points);
            }
        }

        flightPlan.AddLineInstances(inViewport, _lastSource, isActive: true,
            drawVehiclePosition: false, TrueAnomaly.NaN, TrueAnomaly.NaN,
            isPostBurnOrbit: true);
    }
}
```

## Game APIs touched

| Member | Use |
|---|---|
| `TransferPlanner.OnPreRender(IViewport)` ([current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1679)) | Postfixed render hook |
| `TransferPlanner.DrawSelectedTransfer` / `DrawSelectedTransferUi` (private, [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1951-L2004)) | Stock preview suppressed during flyby |
| `FlightPlan.CreateUninitialized`, `ComputeCompleteTrajectory`, `DrawUi` ([current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L834)), `AddLineInstances` ([current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L908)) | Plan object and its rendering |
| `OrbitalTransfers.BuildFlightPlan` ([current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L298)) | Stock plan builder |
| `UpdateTaskUtils.GenerateSpacedPoints(PatchedConic)` ([current/decomp/KSA/UpdateTaskUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UpdateTaskUtils.cs#L18)), `Orbit.IsMissingPoints`, `UpdateCachedPoints` ([current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1405)) | Fill line points for a plan the orbit solver never processed |

## Pitfalls and version notes

- A `FlightPlan` you build yourself has no cached line points (the orbit solver jobs fill them for
  real plans); generate them before `AddLineInstances` or nothing draws.
- Suppression prefixes must fail open; a broken check must never remove stock's own preview.
- Apply line and marker suppression together or not at all.

## See also

- [Transfer Planner custom plan types](transfer-planner-custom-plan-types.md)
- [Multi-pass preview rendering](multi-pass-preview-rendering.md)
- [Flyby encounter prediction](flyby-encounter-prediction.md)
- [Planner burn node creation](planner-burn-node-creation.md)
- [Preview a burn's trajectory with a real FlightPlan from OrbitalTransfers.BuildFlightPlan, drawn from a TransferPlanner.OnPreRender postfix](../../../howto/flight-and-orbits/map-view/draw-a-predicted-trajectory-and-map-markers/draw-a-predicted-trajectory-and-map-markers-1.md) - how-to article demonstrating this feature

[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
