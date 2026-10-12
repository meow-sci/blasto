---
type: Mod Feature
title: Draw several predicted orbits with a brightness ramp and custom map markers
description: Render one FlightPlan per planned pass through FlightPlan.AddLineInstances with per-pass dimmed orbit colors, draw apsis, node and SOI markers on the viewport's overlay draw list, and cache the expensive previews behind quantized vehicle-state keys
mod: ksa-advancedflightcomputer
category: rendering
techniques: [imgui-drawlist, harmony-postfix]
hooks: [harmony-postfix KSA.TransferPlanner.OnPreRender, harmony-postfix KSA.TransferPlanner.DrawPlanWindow]
lifecycle: [frame:draw-ui, frame:pre-render]
game_types:
  - KSA.FlightPlan
  - KSA.PatchedConic
  - KSA.Orbit
  - KSA.BurnPlan
  - KSA.ImGuiHelper
  - KSA.UpdateTaskUtils
  - KSA.SequencePerformanceList
tags: [map-view, orbit-lines, markers, imgui-drawlist, caching, multi-pass]
---

While a multi-pass plan is previewed, the map view shows every intermediate orbit, from dim to bright,
with the final orbit at full brightness, plus markers: apsides of the first pass, small triangles with
hover details on intermediate passes, the "Final" pass labels, AN/DN and the SOI transition. The orbit
lines are drawn by the game's own orbit renderer; only the markers are custom ImGui drawing.

## How it works

### Lines

- Each `PassPreview` carries a detached `FlightPlan` built by the
  [forward-chain planner](multi-pass-burn-splitting.md).
- `MultiPassRenderer.RenderPassOrbits` sets `patch.Orbit.OrbitLineColor` for every patch of a pass to
  `BurnPlan.BurnPatchColor` darkened from 40 % (first shown pass) to 100 % (last), skipping darkening
  for the last pass because the HSL floor changes some colors even at factor 1
  ([AdvancedFlightComputer/Features/MultiPass/MultiPassRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassRenderer.cs#L38-L50)).
- It fills missing cached points with `UpdateTaskUtils.GenerateSpacedPoints(patch)` and calls
  `FlightPlan.AddLineInstances(viewport, source, isActive: true, drawVehiclePosition: false, NaN, NaN, drawDangerGroundTrack: last, isPostBurnOrbit: true)`,
  so the lines use the player's selected stock orbit style; danger ground tracks are drawn only for the
  final pass ([AdvancedFlightComputer/Features/MultiPass/MultiPassRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassRenderer.cs#L10-L36)).
- Hidden passes (for example the first, when stock already draws the committed burn) keep their slot
  in the brightness ramp so the next pass does not jump to full brightness.

### Markers

`MultiPassMarkers.Draw` gets `ImGuiHelper.GetOverlayDrawList(viewport)` (so markers do not appear over
a portrait viewport), projects apsis, AN/DN and SOI transition points from each pass's orbit, and draws
full markers, triangles or the "Pre-SOI-escape AP" label depending on the pass's role, with hover
details from the mouse position ([AdvancedFlightComputer/Features/MultiPass/MultiPassMarkers.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassMarkers.cs)).

### Caching

A preview runs the staging model, the splitter and up to ten `ComputeCompleteTrajectory` calls, too
expensive per frame. `MultiPassPreviewCache` caches the `SequenceBurnState` behind a hash of everything
`SequencePerformanceList.Recompute` reads (sequences, parts, mole/tank contents quantized to 100 kg,
engine and core data, consumption order), and caches previews behind quantized orbit and input keys;
it drops entries on vehicle dispose and save load ([AdvancedFlightComputer/Features/MultiPass/MultiPassPreviewCache.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassPreviewCache.cs)).

## Lifecycle and timing

| Part | Where | Lifecycle |
|---|---|---|
| Build previews | Plan-window draw (AFC types) or `HohmannMultiPassUI.DrawInline` inside stock `DrawPlanWindow` | `frame:draw-ui` |
| Lines | `TransferPlanner.OnPreRender` postfix (`Patch_OnPreRender`) | `frame:pre-render` |
| Markers | `DrawPlanWindow` window draw and its postfix | `frame:draw-ui` |

Lines are added in pre-render with the rest of the frame's orbit lines. Markers are ImGui draw-list
commands, so they must be issued between `ImGui.NewFrame` and `ImGui.Render`, which is why they are
drawn from the window draw rather than from pre-render (pre-render runs after `ImGui.Render` in
`Program.OnFrame`).

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/MultiPass/MultiPassRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassRenderer.cs) | Per-pass color ramp and line instances |
| [AdvancedFlightComputer/Features/MultiPass/MultiPassMarkers.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassMarkers.cs) | Overlay markers |
| [AdvancedFlightComputer/Features/MultiPass/MultiPassPreviewCache.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassPreviewCache.cs) | Quantized caches for staging model and previews |
| [AdvancedFlightComputer/Features/MultiPass/PassPreview.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/PassPreview.cs) | One pass: time, delta-v, flight plan |
| [AdvancedFlightComputer/Features/PlanWindow/Patch_OnPreRender.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_OnPreRender.cs) | Pre-render dispatch |

## Snippets

```csharp
// AdvancedFlightComputer/Features/MultiPass/MultiPassRenderer.cs L39-L61
    // Scale brightness from 40 to 100 percent. Do not darken the final pass because the HSL floor makes Darken change some colors even at a factor of one.
    private static void ApplyPassColor(FlightPlan fp, int shownIndex, int shownCount)
    {
        byte4 color = BurnPlan.BurnPatchColor;
        if (shownCount > 1 && shownIndex < shownCount - 1)
        {
            float brightness = 0.4f + 0.6f * shownIndex / (shownCount - 1);
            color = color.Darken(brightness);
        }
        foreach (PatchedConic patch in fp.Patches)
            patch.Orbit.OrbitLineColor = color;
    }

    // AddLineInstances needs cached points. Generate them if the preview has not done so yet.
    private static void EnsurePatchPointsCached(FlightPlan fp)
    {
        foreach (PatchedConic patch in fp.Patches)
        {
            patch.HidePatch = false;
            if (patch.Orbit.IsMissingPoints())
                patch.Orbit.UpdateCachedPoints(UpdateTaskUtils.GenerateSpacedPoints(patch));
        }
    }
```

## Game APIs touched

| Member | Use |
|---|---|
| `FlightPlan.AddLineInstances(..., drawDangerGroundTrack, isPostBurnOrbit)` ([current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L908)) | Stock orbit-line rendering |
| `Orbit.OrbitLineColor`, `IsMissingPoints`, `UpdateCachedPoints` ([current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs)) | Per-pass color and points |
| `BurnPlan.BurnPatchColor` ([current/decomp/KSA/BurnPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnPlan.cs#L20)) | Stock post-burn color as the base |
| `UpdateTaskUtils.GenerateSpacedPoints(PatchedConic)` ([current/decomp/KSA/UpdateTaskUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UpdateTaskUtils.cs#L18)) | Points for detached plans |
| `ImGuiHelper.GetOverlayDrawList(IViewport)` ([current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs)) | Marker draw list |

## Pitfalls and version notes

- `OrbitLineColor` is per orbit; a preview plan's orbits are AFC's own objects, so recoloring them does
  not touch the vehicle's real plan.
- Quantize cache keys (100 kg, km, 0.1 m/s) so ordinary propellant drain and integration noise do not
  rebuild previews every frame, but include everything the computation reads, or the cache goes stale.

## See also

- [Multi-pass burn splitting](multi-pass-burn-splitting.md)
- [Planner preview trajectories](planner-preview-trajectories.md)
- [Flyby encounter prediction](flyby-encounter-prediction.md)
- [Preview several planned passes as dimmed orbit lines with custom markers and a quantized preview cache](../../../howto/flight-and-orbits/map-view/draw-a-predicted-trajectory-and-map-markers/draw-a-predicted-trajectory-and-map-markers-2.md) - how-to article demonstrating this feature
- [Map-mode markers and gizmos for future positions](../../../howto/rendering/overlays/draw-markers-and-annotations-on-world-objects/draw-markers-and-annotations-on-world-objects-3.md) - how-to article demonstrating this feature
- [FlightPlan.AddLineInstances per plan with dimmed colours, postfix hook and quantized cache keys](../../../howto/rendering/overlays/draw-world-space-lines-and-gizmos/draw-world-space-lines-and-gizmos-3.md) - how-to article demonstrating this feature

