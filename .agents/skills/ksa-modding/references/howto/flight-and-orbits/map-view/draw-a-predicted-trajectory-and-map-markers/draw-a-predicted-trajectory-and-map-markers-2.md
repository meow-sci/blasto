---
type: How-To
title: Preview several planned passes as dimmed orbit lines with custom markers and a quantized preview cache
description: Recolour one detached FlightPlan per planned pass with a brightness ramp, add its lines through FlightPlan.AddLineInstances, draw markers on the overlay draw list and cache the previews
category: flight-and-orbits
group: map-view
topic: draw-a-predicted-trajectory-and-map-markers
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/multi-pass-preview-rendering]
techniques: [harmony-postfix, imgui-drawlist, cache-quantized-keys]
hooks:
  - harmony-postfix KSA.TransferPlanner.OnPreRender
  - harmony-prefix KSA.TransferPlanner.DrawPlanWindow
  - harmony-postfix KSA.TransferPlanner.DrawPlanWindow
lifecycle: [frame:draw-ui, frame:pre-render]
game_types:
  - KSA.FlightPlan
  - KSA.PatchedConic
  - KSA.Orbit
  - KSA.BurnPlan
  - KSA.ImGuiHelper
  - KSA.UpdateTaskUtils
  - KSA.Camera
  - KSA.IViewport
  - KSA.TransferPlanner
tags: [trajectory-preview, multi-pass, orbit-lines, markers, overlay-draw-list, brightness-ramp, cache]
---
Outcome: while a burn is split into several passes, the map view shows every intermediate orbit from dim to bright, with the final orbit at full brightness, and small markers on each pass (apsides, nodes, sphere-of-influence transitions) that show details on hover. The lines are drawn by the game's own orbit renderer in the player's chosen orbit style; only the markers are custom ImGui drawing. KSA-AdvancedFlightComputer (AFC) does this for its multi-pass burn preview.[Multi-pass preview rendering](../../../../mods/ksa-advancedflightcomputer/features/multi-pass-preview-rendering.md)

Prerequisites: StarMap and Harmony, and one detached `FlightPlan` per pass. Approach 1 shows how to build and hook one plan; here the work is colouring many, drawing markers and not rebuilding the previews every frame.

## Background: how the game does it

- **A line is a property of the orbit.** `FlightPlan.AddLineInstances` reads each patch's colour from `patchedConic.Orbit.OrbitLineColor`, skips patches with `HidePatch` set, and replaces the colour with `FlightPlan.InactiveColor` when `isActive` is false. Setting `OrbitLineColor` on a detached plan's orbits recolours exactly that plan.[^ksa-flightplan] [current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L908-L927)
- **The method returns early** unless the orbiter passed in has `ShowOrbit` or `TargetOfControlledVehicle` set, so pass the vehicle being flown.[^ksa-flightplan] [current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L913)
- **Darkening floors saturation and lightness.** `ColorExtensions.Darken(byte4, float)` converts to HSL and clamps saturation and lightness to at least 0.1, so even a factor of 1.0 can change a colour that is already dark or grey.[^ksa-colorextensions] [current/decomp/KSA/ColorExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ColorExtensions.cs#L25-L31)
- **Base colour.** `BurnPlan.BurnPatchColor` is the stock colour of a post-burn orbit and converts implicitly to `byte4`.[^ksa-burnplan] [current/decomp/KSA/BurnPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnPlan.cs#L20)
- **Overlay list.** `ImGuiHelper.GetOverlayDrawList(viewport)` returns the main viewport's background draw list for the main viewport and the current window's draw list for any other viewport.[^ksa-imguihelper] [current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L43-L51)

## Lifecycle and timing

| Part | Where | Lifecycle |
|---|---|---|
| Build or refresh the previews | Plan-window draw (the `DrawPlanWindow` prefix) | `frame:draw-ui` |
| Orbit lines | `TransferPlanner.OnPreRender(IViewport)` postfix | `frame:pre-render` |
| Markers | After the plan window, in the same `DrawPlanWindow` prefix, plus a `DrawPlanWindow` postfix for the Hohmann case | `frame:draw-ui` |

Lines join the frame's orbit-line instances with stock's, in `Universe.OnPreRender` per viewport, after `ImGui.Render` and before `RenderGame` records the frame.[^ksa-program] [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2286-L2323) Markers are ImGui draw-list commands, so they must be issued between `ImGui.NewFrame` and `ImGui.Render`; that is why they are drawn from the window draw and not from pre-render. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Keep one detached plan per pass.** A pass is a record around a `FlightPlan` that a planner built for that pass (see the [forward-chain planner](../../../../mods/ksa-advancedflightcomputer/features/multi-pass-burn-splitting.md)):

```csharp
// AdvancedFlightComputer/Features/MultiPass/PassPreview.cs L6-L10
internal readonly record struct PassPreview(
    UniverseTime BurnTime,
    double3 DvVlf,
    double EstimatedBurnTimeSec,
    FlightPlan FlightPlan);
```

2. **Colour each plan from a brightness ramp.** The first shown pass is at 40 percent brightness and the last at 100; the last is not darkened at all because of the lightness floor above, and every patch of the plan gets the colour:

```csharp
// AdvancedFlightComputer/Features/MultiPass/MultiPassRenderer.cs L39-L50
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
```

3. **Make sure each plan has cached points, then add its lines.** A plan the orbit solver never processed has no points, so `AddLineInstances` would draw nothing; generate them first. Pass `isActive: true` (otherwise the inactive colour replaces yours), `drawVehiclePosition: false`, and `isPostBurnOrbit: true`. Danger ground tracks are limited to the final pass:

```csharp
// AdvancedFlightComputer/Features/MultiPass/MultiPassRenderer.cs L9-L37
    // Hidden passes retain their positions in the brightness sequence.
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
```

```csharp
// AdvancedFlightComputer/Features/MultiPass/MultiPassRenderer.cs L52-L61
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

   Passes hidden because stock already draws them (the committed first burn) keep their slot in the ramp, so the next pass does not jump to full brightness. `rampCount` includes the hidden final pass for the same reason.

4. **Call it from the `OnPreRender` postfix**, guarded so a stale preview for another vehicle never draws. The wrapper also skips the first pass when a multi-pass burn is already queued:

```csharp
// AdvancedFlightComputer/Features/MultiPass/MultiPassUI.cs L165-L174
    public static void Render(IViewport viewport, Vehicle source)
    {
        if (!HasMultiPassPreview) return;
        if (source == null || source.Id != MultiPassPreviewCache.PreviewSourceId) return;

        // Stock already draws the queued burn orbit.
        bool skipFirst = MultiPassRegistry.Has(source.Id);
        MultiPassRenderer.RenderPassOrbits(
            viewport, source, MultiPassPreviewCache.PreviewPasses, skipFirst);
    }
```

   The caller is the `Patch_OnPreRender` postfix from [approach 1](draw-a-predicted-trajectory-and-map-markers-1.md), which fault-isolates the whole call ([AdvancedFlightComputer/Features/PlanWindow/Patch_OnPreRender.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_OnPreRender.cs#L12-L31)).

5. **Project a marker position the way the stock planner does.** The marker code lifts a point in the orbit's parent frame to ecliptic with `GetPositionEclFromCce` (declared on `IPosition`, which `IParentBody` extends)[^ksa-iposition] [current/decomp/KSA/IPosition.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IPosition.cs#L9), then to the screen with the camera, and adds `viewport.Position`. `EgoToScreen` returns NaN for a point behind the camera, so test for it:

```csharp
// AdvancedFlightComputer/Features/MultiPass/MultiPassMarkers.cs L187-L196
    private static void DrawAt(
        IParentBody parent, double3 posCce, string label, MarkerDetail detail,
        byte4 color, MarkerMode mode, int passNumber,
        ImDrawListPtr drawList, Camera camera, float2 vpPos, float2 mousePos)
    {
        double3 posEcl = parent.GetPositionEclFromCce(posCce);
        float2 screen = vpPos + camera.EgoToScreen(camera.EclToEgo(posEcl));
        if (float.IsNaN(screen.X) || float.IsNaN(screen.Y)) return;
        DrawMarker(drawList, screen, mousePos, color, label, detail, mode, passNumber);
    }
```

6. **Draw on the viewport's overlay list, not an ImGui window.** The list is fetched once per call so markers do not appear over a portrait viewport:

```csharp
// AdvancedFlightComputer/Features/MultiPass/MultiPassMarkers.cs L46-L50
        Camera camera = viewport.GetCamera();
        float2 vpPos = viewport.Position;
        // Use the overlay viewport so markers do not appear over a portrait viewport.
        ImDrawListPtr drawList = ImGuiHelper.GetOverlayDrawList(viewport);
        float2 mousePos = ImGui.GetIO().MousePos;
```

   Each pass's first orbit supplies the colour; `Ap` and `Pe` come from `GetApoapsisPositionOrb` and `GetPeriapsisPositionOrb` transformed by `GetOrb2ParentCce`, and each patch adds sphere-of-influence transitions (from `PatchedConic.EndTransition`), AN/DN from `PatchedConic.TargetData` and closest approaches from `PatchedConic.ClosestApproaches`. A marker for an angle outside the patch is skipped with `PatchedConic.TrueAnomalyInPatch`.[^ksa-patchedconic] [current/decomp/KSA/PatchedConic.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PatchedConic.cs#L305)

7. **Make intermediate passes quiet.** The first pass gets full labelled markers, the final one gets a `Final` prefix, and the intermediate passes get a small filled triangle with the label shown only when the mouse is within 100 px:

```csharp
// AdvancedFlightComputer/Features/MultiPass/MultiPassMarkers.cs L198-L229
    private static void DrawMarker(
        ImDrawListPtr drawList, float2 screen, float2 mousePos, byte4 color,
        string label, MarkerDetail detail, MarkerMode mode, int passNumber)
    {
        bool hovered = Math.Abs(screen.X - mousePos.X) < HoverRadiusPx
                    && Math.Abs(screen.Y - mousePos.Y) < HoverRadiusPx;

        string? hoverExtra = hovered ? detail.Format() : null;

        if (mode == MarkerMode.Triangle)
        {
            const float s = 6f;
            drawList.AddTriangleFilled(
                new float2(screen.X - s, screen.Y - s),
                new float2(screen.X + s, screen.Y - s),
                new float2(screen.X, screen.Y + s),
                color);
            if (hovered)
            {
                // Include the pass number in hover labels so overlapping markers remain identifiable.
                string hoverLabel = string.Format(CultureInfo.InvariantCulture,
                    "{0} Pass {1}", label, passNumber);
                float2 textPos = new float2(screen.X, screen.Y + s + 4f);
                ImGuiHelper.DrawTextOnScreen(drawList, textPos, hoverLabel, color);
                if (hoverExtra != null)
                {
                    textPos.Y += ImGui.GetTextLineHeight();
                    ImGuiHelper.DrawTextOnScreen(drawList, textPos, hoverExtra, color);
                }
            }
            return;
        }
```

   Text goes through `ImGuiHelper.DrawTextOnScreen`, which draws a one-pixel black shadow before the coloured text so labels stay readable over bright planets.[^ksa-imguihelper] [current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L518-L525)

8. **Cache the preview behind quantized keys.** A preview runs the staging model, the pass splitter and a trajectory computation per pass, far too much per frame. The key rounds every drifting input (mass to 100 kg, semi-major axis to 100 m, total delta-v to 1 m/s, and a hash of the staging model's sequences) so ordinary propellant drain does not rebuild it, and keeps the inputs that change the burn without changing its magnitude (target, node, inclination):

```csharp
// AdvancedFlightComputer/Features/MultiPass/MultiPassPreviewCache.cs L257-L296
    // BurnTime is omitted because it advances each frame.
    // Keep node and inclination inputs in the key because they can change the maneuver without changing its delta v magnitude.
    private readonly record struct PreviewKey(
        string TypeKey,
        string VehicleId,
        int PassCount,
        SplitMode Mode,
        long DvBucket,
        long SmaBucket,
        long MassBucket,
        bool UseDescendingNode,
        long TargetIncMilliRad,
        OrbitManeuvers.InclinationReference Reference,
        string TargetId,
        int SequenceSignature)
    {
        public static PreviewKey From(
            Vehicle source, string typeKey, int passCount,
            SplitMode mode, double totalDv, SequenceBurnState state)
        {
            IOrbiter? target = ManeuverToolsWindow.GetSelectedTargetOrbiter();
            return new(
                typeKey,
                source.Id,
                passCount,
                mode,
                (long)totalDv,
                (long)(source.Orbit.SemiMajorAxis / SmaQuantumM),
                (long)(source.TotalMass / MassQuantumKg),
                ManeuverToolsWindow.UseDescendingNode,
                (long)(ManeuverToolsWindow.TargetInclinationRad * 1000.0),
                ManeuverToolsWindow.InclinationRef,
                target?.Id ?? string.Empty,
                ComputeSequenceSignature(state));
        }

        // Exclude orbit, mass, delta v, and sequence performance changes when comparing intent during thrust.
        public PreviewKey WithoutDrift() =>
            this with { DvBucket = 0, SmaBucket = 0, MassBucket = 0, SequenceSignature = 0 };
    }
```

   While the flight computer is flying the burn, a second comparison with `WithoutDrift` freezes the preview so the lines do not shift under the pilot. The cache is cleared when a vehicle is disposed (`MultiPassPreviewCache.OnVehicleDisposed`, called from `SharedVehicleHooks`) and reset on save load (`SaveScopedState`).

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/MultiPass/MultiPassRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassRenderer.cs) | Colour ramp, points, line instances |
| [AdvancedFlightComputer/Features/MultiPass/MultiPassMarkers.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassMarkers.cs) | Marker modes, projection, hover |
| [AdvancedFlightComputer/Features/MultiPass/MultiPassPreviewCache.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassPreviewCache.cs) | Quantized caches, thrust freeze |
| [AdvancedFlightComputer/Features/MultiPass/MultiPassUI.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/MultiPassUI.cs#L165-L191) | Render and marker entry points |
| [AdvancedFlightComputer/Features/MultiPass/PassPreview.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/MultiPass/PassPreview.cs) | One pass: time, delta-v, plan |
| [AdvancedFlightComputer/Features/PlanWindow/Patch_OnPreRender.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_OnPreRender.cs) | Pre-render dispatch |

## Variations and gotchas

- **`isActive` must be true** or `FlightPlan.InactiveColor` replaces your ramp (`FlightPlan.AddLineInstances`, `isActive` branch).
- **Recolouring is per orbit.** The orbits of a detached plan are your own objects, so recolouring them does not touch the vehicle's real plan; do not recolour `Vehicle.FlightPlan` this way.
- **Lines need `ShowOrbit`.** If the source vehicle has its orbit display switched off, `AddLineInstances` draws nothing at all.
- **Do not darken the last pass.** The 0.1 floor means a factor of 1.0 is not an identity.
- **Key everything the preview reads.** Quantize the drifting inputs but include every input that changes the answer, or the cache goes stale; hash identities (parts, engines, tanks) rather than copying their contents.
- **Markers need the draw phase.** Pre-render runs after `ImGui.Render`; an ImGui call made there is illegal, so markers must come from the UI pass.
- **Per-frame allocation.** The marker code formats strings only while hovered; keep that property when you add labels.

## See also

- [Overview](overview.md); [Real FlightPlan from OrbitalTransfers.BuildFlightPlan](draw-a-predicted-trajectory-and-map-markers-1.md); [ImGui polyline of a flown prediction](draw-a-predicted-trajectory-and-map-markers-3.md)
- [Multi-pass preview rendering (mod feature)](../../../../mods/ksa-advancedflightcomputer/features/multi-pass-preview-rendering.md)
- [Planner preview trajectories (mod feature)](../../../../mods/ksa-advancedflightcomputer/features/planner-preview-trajectories.md)
- [Compute transfer windows](../../transfers/compute-transfer-windows/overview.md)
- [Run an expensive solve without stalling the frame](../../guidance/run-an-expensive-solve-without-stalling-the-frame/overview.md)

[^ksa-flightplan]: KSA.FlightPlan (decompiled, build 2026.10.10.5554)
[^ksa-colorextensions]: KSA.ColorExtensions (decompiled, build 2026.10.10.5554)
[^ksa-burnplan]: KSA.BurnPlan (decompiled, build 2026.10.10.5554)
[^ksa-imguihelper]: KSA.ImGuiHelper (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-iposition]: KSA.IPosition (decompiled, build 2026.10.10.5554)
[^ksa-patchedconic]: KSA.PatchedConic (decompiled, build 2026.10.10.5554)
