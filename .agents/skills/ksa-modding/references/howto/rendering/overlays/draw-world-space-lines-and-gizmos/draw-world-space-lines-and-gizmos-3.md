---
type: How-To
title: FlightPlan.AddLineInstances per plan with dimmed colours, postfix hook and quantized cache keys
description: Preview extra predicted orbits through the game's own line instances so they look and clip like stock orbits, adding them from a TransferPlanner.OnPreRender postfix
category: rendering
group: overlays
topic: draw-world-space-lines-and-gizmos
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/multi-pass-preview-rendering]
techniques: [harmony-postfix]
hooks: [harmony-postfix KSA.TransferPlanner.OnPreRender, harmony-postfix KSA.TransferPlanner.DrawPlanWindow]
lifecycle: [frame:draw-ui, frame:pre-render, render:orbit-lines]
game_types: [KSA.FlightPlan, KSA.OrbitalTransfers, KSA.Program, KSA.TransferPlanner]
tags: [orbit-lines, flightplan, preview, harmony-postfix, cache, map-view]
---

Outcome: extra predicted orbits (every intermediate orbit of a multi-pass burn, from dim to bright, with the final orbit at full brightness) drawn in the 3D and map views by the game's own orbit renderer, so they use the player's chosen orbit style and clip like stock orbits. Only the apsis, node and SOI markers are custom
ImGui drawing (see [draw markers and annotations](../draw-markers-and-annotations-on-world-objects/overview.md), approach 3). Demonstrated by KSA-AdvancedFlightComputer ([Draw several predicted orbits with a brightness ramp and custom map markers](../../../../mods/ksa-advancedflightcomputer/features/multi-pass-preview-rendering.md)). Prerequisites: StarMap, Harmony.

## Background: how the game does it

`FlightPlan.AddLineInstances(IViewport viewport, IOrbiter orbiter, bool isActive, bool drawVehiclePosition, TrueAnomaly startTa, TrueAnomaly nextBurnTa, bool drawDangerGroundTrack = true, bool isPostBurnOrbit = false)` [current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L908) appends line instances for each patch of the plan to the orbit-line renderer. It needs cached points on every patch orbit.
The orbit lines are recorded later by `OrbitLinePass.Run` (step 18 of `RenderGame`, only with `DrawUI`, `render:orbit-lines`). `TransferPlanner.OnPreRender(IViewport)` [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1679) is where the stock planner adds its lines for the frame. See [the render sequence](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Lifecycle and timing

| Part | Where | Lifecycle |
|---|---|---|
| Build previews | plan-window draw (or inline inside the stock `DrawPlanWindow`) | `frame:draw-ui` |
| Lines | `TransferPlanner.OnPreRender` postfix | `frame:pre-render` |
| Markers | `DrawPlanWindow` window draw and its postfix | `frame:draw-ui` |

Lines are added in pre-render with the rest of the frame's orbit lines. Markers are ImGui draw-list commands, so they must be issued between `ImGui.NewFrame` and `ImGui.Render`; that is why they are drawn from the window draw rather than from pre-render (which runs after `ImGui.Render` in `Program.OnFrame`).

## Steps

1. **Build one detached `FlightPlan` per pass** (the mod's forward-chain planner), so recolouring their orbits does not touch the vehicle's real plan.

2. **Postfix `TransferPlanner.OnPreRender(IViewport)`**, with a `try`/`catch` that warns once per exception type, and render the previews for the viewport:

   ```csharp
   // AdvancedFlightComputer/Features/PlanWindow/Patch_OnPreRender.cs L11-L31

   [HarmonyPatch(typeof(TransferPlanner), nameof(TransferPlanner.OnPreRender), new[] { typeof(IViewport) })]
   internal static class Patch_OnPreRender
   {
       static void Postfix(IViewport inViewport)
       {
           try
           {
               Patch_DrawPlanWindow.TickWindowState();
               string? typeKey = StockPlanner.TransferTypeKey;
               if (typeKey != null && ManeuverToolsFeature.IsHandledType(typeKey))
                   Patch_DrawPlanWindow.RenderOrbitPreview(inViewport);
               else
                   RenderHohmannOverlay(inViewport);
           }
           catch (Exception ex)
           {
               LogHelper.WarnOnce("maneuvertools-onprerender:" + ex.GetType().Name,
                   $"[AFC] ManeuverTools OnPreRender: {ex}");
           }
       }
   ```

3. **Dim and draw.** Set `patch.Orbit.OrbitLineColor` for every patch of a pass to the burn colour darkened from 40 percent to 100 percent (do not darken the last pass: the HSL floor changes some colours even at factor 1), generate missing cached points, then call `AddLineInstances`. Hidden passes keep their slot in the brightness ramp so the next pass does not jump to full brightness.

   ```csharp
   // AdvancedFlightComputer/Features/MultiPass/MultiPassRenderer.cs L10-L61
   public static void RenderPassOrbits(
       IViewport viewport, Vehicle source, PassPreview[] passes,
       bool skipFirst = false, bool skipLast = false)
   {
       int start = skipFirst ? 1 : 0;
       int end = passes.Length - (skipLast ? 1 : 0);
       if (end - start <= 0) return;

       // Include the hidden final pass in the ramp so the preceding pass does not jump to full brightness.
       int rampCount = passes.Length - start;

       for (int i = start; i < end; i++)
       {
           FlightPlan fp = passes[i].FlightPlan;
           if (fp.Patches.Count == 0)
               continue;

           ApplyPassColor(fp, i - start, rampCount);
           EnsurePatchPointsCached(fp);

           // Use the selected stock orbit style. Restrict danger ground tracks to the final pass, which stock draws when it is hidden here.
           fp.AddLineInstances(viewport, source, isActive: true,
               drawVehiclePosition: false,
               TrueAnomaly.NaN, TrueAnomaly.NaN,
               drawDangerGroundTrack: i == passes.Length - 1,
               isPostBurnOrbit: true);
       }
   }

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

4. **Cache behind quantized keys.** A preview runs the staging model, the splitter and up to ten trajectory computations, too expensive per frame. Cache the burn state behind a hash of everything the computation reads, and cache previews behind quantized orbit and input keys (100 kg, km, 0.1 m/s), dropping entries on vehicle dispose and save load.

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/MultiPass/MultiPassRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassRenderer.cs) | Colour ramp and line instances |
| [AdvancedFlightComputer/Features/PlanWindow/Patch_OnPreRender.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_OnPreRender.cs) | Pre-render postfix |
| [AdvancedFlightComputer/Features/MultiPass/MultiPassMarkers.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassMarkers.cs) | ImGui markers |

## Variations and gotchas

- **`OrbitLineColor` is per orbit:** a preview plan's orbits are the mod's own objects, so recolouring them does not touch the vehicle's real plan.
- **Quantize cache keys** so ordinary propellant drain and integration noise do not rebuild previews every frame, but include everything the computation reads, or the cache goes stale.
- **Markers use `GetOverlayDrawList(viewport)`,** so they do not appear over a portrait viewport.

## See also

- [Overview](overview.md)
- [Approach 1: spheres and lines from the StarMap GUI postfix](draw-world-space-lines-and-gizmos-1.md)
- [Draw several predicted orbits with a brightness ramp and custom map markers](../../../../mods/ksa-advancedflightcomputer/features/multi-pass-preview-rendering.md)
- [Draw markers, brackets and measurement annotations on world objects](../draw-markers-and-annotations-on-world-objects/overview.md)
