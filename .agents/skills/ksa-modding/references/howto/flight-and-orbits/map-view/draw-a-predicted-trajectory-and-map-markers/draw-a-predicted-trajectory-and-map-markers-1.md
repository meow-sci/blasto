---
type: How-To
title: Preview a burn's trajectory with a real FlightPlan from OrbitalTransfers.BuildFlightPlan, drawn from a TransferPlanner.OnPreRender postfix
description: Build a detached FlightPlan for a planned burn with the stock plan builder, fill its line points, add it to the frame's orbit-line instances from a postfix on TransferPlanner.OnPreRender, and suppress the stock selected-transfer preview with a paired set of prefixes that fail open
category: flight-and-orbits
group: map-view
topic: draw-a-predicted-trajectory-and-map-markers
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/planner-preview-trajectories]
techniques: [harmony-postfix, harmony-prefix, reflection]
hooks:
  - harmony-prefix KSA.TransferPlanner.DrawPlanWindow
  - harmony-postfix KSA.TransferPlanner.OnPreRender
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
  - KSA.IViewport
tags: [trajectory-preview, flight-plan, orbit-lines, transfer-planner, pre-render, map-view, suppress-stock]
---
Outcome: the map view shows the trajectory a planned burn would produce, drawn by the game's own orbit renderer in the player's chosen orbit style, with stock apsis and node markers, and, where your preview replaces stock's, without a second competing line. KSA-AdvancedFlightComputer (AFC) does this for its quick-tool burns and its flyby retargeting: it never draws a line itself, it builds a real `FlightPlan` and hands it to the game.[Planner preview trajectories](../../../../mods/ksa-advancedflightcomputer/features/planner-preview-trajectories.md)

Prerequisites: StarMap and Harmony; a burn time and a delta-v in the vehicle's local frame (VLF), for example from [Create and read maneuver nodes](../../autopilot/create-and-read-maneuver-nodes/overview.md).

## Background: how the game does it

- **Stock builds and draws its own previews the same way.** `OrbitalTransfers.BuildFlightPlan(ref FlightPlan, TransferInfo, UniverseTime startTime, double3 dvVlf, out OrbitPointCce, out double)` propagates a plan for a burn.[^ksa-orbitaltransfers] [current/decomp/KSA/OrbitalTransfers.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitalTransfers.cs#L298) The planner's private `DrawSelectedTransfer(IViewport)` fills missing line points and adds line instances for its selected entry:

```csharp
// current/decomp/KSA/TransferPlanner.cs L2004-L2016
	private static void DrawSelectedTransfer(IViewport inViewport)
	{
		if (Source == null || _selectedEntry == null || _selectedEntry.FlightPlan.Patches.Count <= 0)
		{
			return;
		}
		if (_selectedEntry.FlightPlan.Patches[0].Orbit.IsMissingPoints())
		{
			foreach (PatchedConic patch in _selectedEntry.FlightPlan.Patches)
			{
				patch.HidePatch = false;
				MemoryOwner<OrbitPointCce> points = UpdateTaskUtils.GenerateSpacedPoints(patch);
				patch.Orbit.UpdateCachedPoints(points);
```

- **Where lines are added.** `CelestialSystem.OnPreRender(IViewport)` (from `Universe.OnPreRender` in `Program.OnPreRender`) pre-renders every body, then calls `TransferPlanner.OnPreRender(IViewport)`, which calls `DrawSelectedTransfer` when the plan window shows a calculated transfer and `_displaySelectedTransfer` is on.[^ksa-celestialsystem] [^ksa-transferplanner] [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L541-L549) [current/decomp/KSA/TransferPlanner.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TransferPlanner.cs#L1679-L1689)
- **Lines.** `FlightPlan.AddLineInstances(IViewport, IOrbiter, bool isActive, bool drawVehiclePosition, TrueAnomaly startTa, TrueAnomaly nextBurnTa, bool drawDangerGroundTrack = true, bool isPostBurnOrbit = false)`; markers come from `FlightPlan.DrawUi(viewport, Astronomical.UiContext, ...)` in the UI pass.[^ksa-flightplan] [current/decomp/KSA/FlightPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightPlan.cs#L834-L908)
- **Points.** A plan the orbit solver never processed has no cached points: `Orbit.IsMissingPoints()` is true until `UpdateCachedPoints(UpdateTaskUtils.GenerateSpacedPoints(patch))`.[^ksa-updatetaskutils] [current/decomp/KSA/UpdateTaskUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UpdateTaskUtils.cs#L18)

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Build the `FlightPlan` | A prefix on `TransferPlanner.DrawPlanWindow`, main thread | `frame:draw-ui` |
| Add line instances | Postfix on `TransferPlanner.OnPreRender(IViewport)` | `frame:pre-render` |
| Suppress stock lines | Prefix on `DrawSelectedTransfer`, inside `OnPreRender` | `frame:pre-render` |
| Suppress stock markers | Prefix on `DrawSelectedTransferUi`, inside `DrawPlanWindow` | `frame:draw-ui` |

Lines join the frame's orbit-line instances with stock's, after the UI pass and before `RenderGame` records the frame; markers are ImGui commands and must be issued in the UI pass. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Build a detached plan with the stock builder.** Create an uninitialized plan keyed to the vehicle, give it the vehicle's bounding sphere as impact clearance, run `BuildFlightPlan` from the burn, and finish an unresolved impact search:

```csharp
// AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.Preview.cs L60-L92
    private static OrbitalTransfers.PorkChopEntry BuildTransferEntry(
        Vehicle source, OrbitManeuvers.ManeuverResult maneuver, PlanningBasis basis)
    {
        var transferData = new OrbitalTransfers.TransferData
        {
            Start = maneuver.BurnTime,
            Point = basis.Orbit.GetPointAt(maneuver.BurnTime),
            DeltaVelocityCci = maneuver.DvCci,
            TransferDvVlf = maneuver.DvVlf
        };

        if (basis.IsChained && basis.Patch != null)
        {
            var (chainedPlan, _) = MultiPassForwardChainPlanner.BuildPassFlightPlan(
                source, basis.Patch, maneuver.BurnTime, maneuver.DvVlf);
            return new OrbitalTransfers.PorkChopEntry(transferData, chainedPlan);
        }

        FlightPlan flightPlan = FlightPlan.CreateUninitialized(source.Hash);
        flightPlan.ImpactClearanceMargin = source.BoundingSphereRadiusBody;
        var info = new OrbitalTransfers.TransferInfo(source, source, source, usePorkChopData: false)
        {
            Target = null
        };
        OrbitalTransfers.BuildFlightPlan(
            ref flightPlan, info, transferData.Start, transferData.TransferDvVlf,
            out _, out _);
        if (flightPlan.ImpactSearchUnresolved)
            flightPlan.ComputeCompleteTrajectory(out _, 5, 8, null,
                resolveImpactsCompletely: true);

        return new OrbitalTransfers.PorkChopEntry(transferData, flightPlan);
    }
```

2. **Postfix `TransferPlanner.OnPreRender` and dispatch.** One postfix covers every preview the mod draws; fault-isolate it so a bad frame cannot unwind into rendering:

```csharp
// AdvancedFlightComputer/Features/PlanWindow/Patch_OnPreRender.cs L12-L31
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

3. **Fill points, then add line instances** as a post-burn orbit, without the vehicle marker:

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

4. **Draw the plan's markers in the UI pass** with the stock marker code, from the window draw:

```csharp
// AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.Preview.cs L94-L101
    private static void DrawOrbitMarkers(IViewport inViewport)
    {
        var uiContext = new Astronomical.UiContext(
            inViewport, _lastSource!, Color.Green,
            TrueAnomaly.Zero, new TrueAnomaly(Math.PI * 2.0),
            ManeuverToolsWindow.GetSelectedTargetOrbiter());
        _lastEntry!.FlightPlan.DrawUi(inViewport, uiContext, tintDanger: true);
    }
```

5. **Suppress the stock preview when yours replaces it, and fail open.** `DrawSelectedTransfer` is private, so bind it through `TargetMethod()` to a reflected handle; return `false` only while your preview replaces it, and `true` on any exception:

```csharp
// AdvancedFlightComputer/Features/PlanWindow/Patch_TransferPlanner_DrawSelectedTransfer_Flyby.cs L23-L49
[HarmonyPatch]
internal static class Patch_TransferPlanner_DrawSelectedTransfer_Flyby
{
    /// <summary>Whether stock still has the line and marker suppression anchor.</summary>
    public static bool IsAnchorPresent => GameReflection.TransferPlanner_DrawSelectedTransfer != null;

    static MethodBase TargetMethod() =>
        GameReflection.TransferPlanner_DrawSelectedTransfer ?? throw new InvalidOperationException(
            "[AFC] TransferPlanner.DrawSelectedTransfer(IViewport) not found; "
            + "patching this class requires an IsAnchorPresent check first.");

    static bool Prefix()
    {
        try
        {
            return !HohmannFlybyUI.SuppressesStockTransferPreview();
        }
        catch (Exception ex)
        {
            // Fails open, because a broken check must never remove stock's own preview.
            // Deduped because it runs per frame.
            LogHelper.WarnOnce("flyby-suppress-lines:" + ex.GetType().Name,
                $"[AFC] Flyby DrawSelectedTransfer prefix: {ex}; leaving stock preview on.");
            return true;
        }
    }
}
```

6. **Apply line and marker suppression as a pair or not at all**, so stock lines and stock markers can never disagree:

```csharp
// AdvancedFlightComputer/Mod.cs L130-L143
    private static void PatchPlanWindow(Harmony harmony)
    {
        harmony.CreateClassProcessor(typeof(Patch_DrawPlanWindow)).Patch();
        harmony.CreateClassProcessor(typeof(Patch_OnPreRender)).Patch();

        // Suppress both parts of the stock preview together so lines and markers cannot disagree.
        bool lines = Patch_TransferPlanner_DrawSelectedTransfer_Flyby.IsAnchorPresent;
        bool markers = Patch_TransferPlanner_DrawSelectedTransferUi_Flyby.IsAnchorPresent;
        if (lines && markers)
        {
            harmony.CreateClassProcessor(typeof(Patch_TransferPlanner_DrawSelectedTransfer_Flyby)).Patch();
            harmony.CreateClassProcessor(typeof(Patch_TransferPlanner_DrawSelectedTransferUi_Flyby)).Patch();
            return;
        }
```

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.Preview.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_DrawPlanWindow.Preview.cs) | Build, markers, lines, flight-plan window |
| [AdvancedFlightComputer/Features/PlanWindow/Patch_OnPreRender.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_OnPreRender.cs) | Pre-render postfix dispatch |
| [AdvancedFlightComputer/Features/PlanWindow/Patch_TransferPlanner_DrawSelectedTransfer_Flyby.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_TransferPlanner_DrawSelectedTransfer_Flyby.cs) | Stock line suppression |
| [AdvancedFlightComputer/Features/PlanWindow/Patch_TransferPlanner_DrawSelectedTransferUi_Flyby.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/PlanWindow/Patch_TransferPlanner_DrawSelectedTransferUi_Flyby.cs) | Stock marker suppression |
| [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L130-L151) | Paired patch application |

## Variations and gotchas

- **No points, no line.** A plan you build yourself has no cached points; generate them before `AddLineInstances` or nothing draws.
- **Lines in pre-render, markers in the UI pass.** `OnPreRender` runs after `ImGui.Render`, so ImGui calls from it are illegal; orbit-line instances added from the UI pass would be cleared or missed.
- **Fail open.** A suppression prefix that throws must leave stock's preview on.
- **Prefixes, not transpilers.** The stock previews are suppressed with two `bool Prefix()` patches on the private methods, applied only when both anchors resolve.
- **One switch.** Stock's `_displaySelectedTransfer` checkbox drives both stock's preview and AFC's Hohmann overlay, so the player manages one toggle.

## See also

- [Overview](overview.md); [Per-pass dimmed orbit lines with markers](draw-a-predicted-trajectory-and-map-markers-2.md)
- [Add a plan type to the transfer planner](../../transfers/add-plan-type-to-the-transfer-planner/overview.md)
- [Aim a transfer at a flyby periapsis](../../transfers/aim-a-transfer-at-a-flyby-periapsis/overview.md)
- [Planner preview trajectories (mod feature)](../../../../mods/ksa-advancedflightcomputer/features/planner-preview-trajectories.md)

[^ksa-orbitaltransfers]: KSA.OrbitalTransfers (decompiled, build 2026.10.10.5554)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
[^ksa-transferplanner]: KSA.TransferPlanner (decompiled, build 2026.10.10.5554)
[^ksa-flightplan]: KSA.FlightPlan (decompiled, build 2026.10.10.5554)
[^ksa-updatetaskutils]: KSA.UpdateTaskUtils (decompiled, build 2026.10.10.5554)
