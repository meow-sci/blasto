---
type: How-To
title: Pick the point on an orbit line under the cursor with Orbit.GetNearestPosition and re-validate it on screen
description: Reuse the game's nearest-point solver and OrbitHoverCandidate tie-break on flight-plan patches, burn branches and body orbits, then re-project each candidate to close the behind-camera NaN hole and the integer aspect bug in the stock acceptance test
category: flight-and-orbits
group: map-view
topic: anchor-and-pick-points-on-bodies
approach: 4
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-measuretools/features/orbit-line-snapping]
techniques: [reuse-stock-picker, screen-space-picking, harmony-prefix]
hooks: [harmony-prefix KSA.Program.OnMouseButton, starmap StarMapAfterGui]
lifecycle: [frame:input, frame:draw-ui]
game_types:
  - KSA.Orbit
  - KSA.OrbitHoverCandidate
  - KSA.OrbitPointCce
  - KSA.CelestialPosition
  - KSA.FlightPlan
  - KSA.PatchedConic
  - KSA.BurnPlan
  - KSA.BurnContextMenu
  - KSA.Astronomical
  - KSA.CelestialSystem
tags: [picking, orbit-line, nearest-point, cursor, burn-branch, flight-plan, ndc, map-view]
---
Outcome: a click near any drawn orbit line (a vehicle's coast trajectory, a planned burn's branch, a planet's or moon's orbit) lands on that line, and the point keeps its place on the orbit's shape as the parent body moves. Instead of sampling lines itself, KSA-MeasureTools calls the game's own nearest-point solver and tie-break on the same candidate set the stock burn picker uses, then fixes one acceptance bug by re-checking each candidate on screen.[Orbit-line snapping](../../../../mods/ksa-measuretools/features/orbit-line-snapping.md)

Prerequisites: StarMap; Harmony for the click prefix; an anchor that stores a CCE offset from the orbit's parent ([approach 2](anchor-and-pick-points-on-bodies-2.md)).

## Background: how the game does it

- **The solver.** `Orbit.GetNearestPosition(IViewport, float2 mousePosScreen, PatchedConic? patch, out CelestialPosition?, bool spliceVehicleFromNow = false, float mouseDistanceScreenPercent = 0.025f)` walks the cached line points for hyperbolic, parabolic and circular orbits, solves a closed-form nearest true anomaly for elliptic ones, and accepts the result if its NDC distance to the cursor is within the percentage.[^ksa-orbit] [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L2983-L3200)
- **The stock acceptance test** on the elliptic branch has two holes: a point behind the camera projects to NaN, and `NaN > threshold` is false, so it is accepted; and the aspect factor is an `int` division, 0 on a portrait viewport:

```csharp
// current/decomp/KSA/Orbit.cs L3174-L3186
		double2 positionScreenDouble2 = pointSelected.Value.GetPositionScreenDouble(Parent, camera);
		double3 double18 = camera.ScreenToNdc(positionScreenDouble2, 1.0);
		double3 double19 = camera.ScreenToNdc(double2.Unpack(in mousePosScreen), 1.0);
		double3 double20 = double18 - double19;
		int2 size2 = inViewPort.Size;
		int num19 = size2.X / size2.Y;
		double20.X *= num19;
		double num20 = Math.Sqrt(double20.X * double20.X + double20.Y * double20.Y);
		if (num20 > (double)mouseDistanceScreenPercent)
		{
			pointSelected = null;
			return false;
		}
```

- **The tie-break.** `OrbitHoverCandidate(Position, Owner, HoversMarker, IsReached).Wins(in best, camera, cursor)` prefers marker hovers, then cursor proximity between owners, then reached branches, then earlier patches.[^ksa-orbithovercandidate] [current/decomp/KSA/OrbitHoverCandidate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitHoverCandidate.cs#L5-L7)
- **The stock candidate set** is `CelestialSystem.SetNearestOrbitPoint`: vehicle flight-plan patches and burn branches gated by `Astronomical.ShouldDrawUiOrLines`, celestial orbits gated by `ShouldDrawLines`. It runs per frame in `NearestOrbitPointJob` on a worker and feeds burn creation for the controlled vehicle only.[^ksa-celestialsystem] [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L473-L539)
- **Results.** `CelestialPosition` has `Parent`, `Point` (an `OrbitPointCce` with `PositionCce` and `GetPositionScreen(parent, camera)`) and `IsReachedWithin(start, end)`.[^ksa-celestialposition] [current/decomp/KSA/CelestialPosition.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialPosition.cs#L9-L15) [current/decomp/KSA/OrbitPointCce.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitPointCce.cs#L27)

## Lifecycle and timing

- **Placement: `frame:input`.** The click prefix on `Program.OnMouseButton` runs inside `Glfw.PollEvents` in `PrepareFrame`, after `ApplyOrbitSolvers` / `ApplyVehicleSolvers` installed fresh orbits and flight plans; the camera is last frame's.
- **Preview: `frame:draw-ui`.** `[StarMapAfterGui]`, re-picked about every 45 ms.
- **Cost.** The scan is closed-form per orbit on the main thread; the stock job does the same kind of scan on a worker. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Walk the stock candidate set and call the stock solver.** Vehicles that are shown, targeted or controlled contribute each flight-plan patch (newest first) and their burn branches; celestials that are shown or targeted contribute their orbit if its line is drawn. The winner becomes an orbit anchor, a CCE offset from the orbit's parent:

```csharp
// MeasureTools/Features/Measure/MapPicker.cs L874-L920
        foreach (Astronomical astronomical in system.All.AsSpan())
        {
            if (astronomical is not IOrbiter)
                continue;
            if (astronomical is Vehicle vehicle)
            {
                if (!vehicle.ShowOrbit && !vehicle.TargetOfControlledVehicle && Program.ControlledVehicle != vehicle)
                    continue;
                FlightPlan flightPlan = vehicle.FlightPlan;
                BurnPlan burnPlan = vehicle.BurnPlan;
                UniverseTime branchEnd = burnPlan.FindBranchEndTime(-1);
                List<PatchedConic> patches = flightPlan.Patches;
                for (int i = patches.Count - 1; i >= 0; i--)
                {
                    PatchedConic patch = patches[i];
                    // Vehicle patches use the wider UI-or-lines gate, celestials below the
                    // narrower lines-only one. Stock draws the same distinction in
                    // SetNearestOrbitPoint; matching it keeps the candidate sets identical.
                    if (!Astronomical.ShouldDrawUiOrLines(patch.PrimaryBody, viewport, patch.Orbit))
                        continue;
                    if (patch.Orbit.GetNearestPosition(viewport, mouseViewport, patch, out CelestialPosition? pos, spliceVehicleFromNow: false)
                        && pos is { } p)
                    {
                        TryAccept(new OrbitHoverCandidate(p, vehicle, flightPlan.HoveredMarker, p.IsReachedWithin(now, branchEnd)),
                            camera, viewport, mouseViewport, ref best);
                    }
                }
                AcceptBurnPoints(vehicle, burnPlan, camera, viewport, mouseViewport, ref best);
            }
            else if (astronomical is Celestial celestial)
            {
                if (!celestial.ShowOrbit && !celestial.TargetOfControlledVehicle)
                    continue;
                if (!Astronomical.ShouldDrawLines(astronomical, viewport, celestial.Orbit))
                    continue;
                if (celestial.Orbit.GetNearestPosition(viewport, mouseViewport, null, out CelestialPosition? pos, spliceVehicleFromNow: false)
                    && pos is { } p)
                {
                    TryAccept(new OrbitHoverCandidate(p, celestial, HoversMarker: false, IsReached: true),
                        camera, viewport, mouseViewport, ref best);
                }
            }
        }

        if (best is not { } winner)
            return null;
        return Anchor.OnOrbit(winner.Position.Parent, winner.Position.Point.PositionCce, winner.Owner.Id);
```

2. **Validate burn branches one patch at a time.** `Wins` ranks reached and earlier patches of one owner above proximity, so one stock pick behind the camera would hide every visible branch; use the vehicle's grab radius as stock does:

```csharp
// MeasureTools/Features/Measure/MapPicker.cs L927-L952
    private static void AcceptBurnPoints(Vehicle vehicle, BurnPlan burnPlan, Camera camera, IViewport viewport,
        float2 mouseViewport, ref OrbitHoverCandidate? best)
    {
        bool hovered = burnPlan.IsHovered();
        for (int b = 0; b < burnPlan.BurnCount; b++)
        {
            if (!burnPlan.TryGetBurn(b, out Burn? burn) || burn == null
                || (!burn.Vehicle.ShowOrbit && !burn.Vehicle.TargetOfControlledVehicle))
                continue;
            UniverseTime branchEnd = burn.ParentDepartureBurn ? UniverseTime.EndOfTime : burnPlan.FindBranchEndTime(b);
            float grabRadius = BurnContextMenu.GetGrabRadiusPercent(burn.Vehicle);
            List<PatchedConic> patches = burn.FlightPlan.Patches;
            for (int i = patches.Count - 1; i >= 0; i--)
            {
                PatchedConic patch = patches[i];
                if (!Astronomical.ShouldDrawUiOrLines(patch.PrimaryBody, viewport, patch.Orbit))
                    continue;
                if (patch.Orbit.GetNearestPosition(viewport, mouseViewport, patch, out CelestialPosition? pos, spliceVehicleFromNow: false, grabRadius)
                    && pos is { } p)
                {
                    TryAccept(new OrbitHoverCandidate(p, vehicle, hovered, p.IsReachedWithin(burn.Time, branchEnd)),
                        camera, viewport, mouseViewport, ref best);
                }
            }
        }
    }
```

3. **Re-check every candidate on screen before the tie-break.** Project the candidate yourself; reject non-finite results and anything farther than `max(24 px, 0.025 * viewport height)` from the cursor:

```csharp
// MeasureTools/Features/Measure/MapPicker.cs L954-L980
    private static void TryAccept(in OrbitHoverCandidate candidate, Camera camera, IViewport viewport,
        float2 mouseViewport, ref OrbitHoverCandidate? best)
    {
        if (IsOnScreenNearCursor(candidate.Position, camera, viewport, mouseViewport)
            && candidate.Wins(in best, camera, mouseViewport))
        {
            best = candidate;
        }
    }
    // ...
    private static bool IsOnScreenNearCursor(CelestialPosition candidate, Camera camera, IViewport viewport, float2 mouseViewport)
    {
        float2 s = candidate.Point.GetPositionScreen(candidate.Parent, camera);
        if (float.IsNaN(s.X) || float.IsNaN(s.Y) || float.IsInfinity(s.X) || float.IsInfinity(s.Y))
            return false;
        float maxPx = MathF.Max(24f, viewport.Size.Y * OrbitSnapMaxScreenFraction);
        return float2.Distance(s, mouseViewport) <= maxPx;
    }
```

4. **Resolve the anchor every frame** with `parent.GetPositionEclFromCce(offset)`, so the point rides along as the parent moves on its own orbit.

## Complete example

| File | Role |
|---|---|
| [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L853-L980) | `PickOrbitPoint`, `AcceptBurnPoints`, `TryAccept`, `IsOnScreenNearCursor` |
| [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L27-L35) | Why 0.025 of the viewport height |
| [MeasureTools/Features/Measure/Anchor.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Anchor.cs#L210-L219) | `Anchor.OnOrbit` |

## Variations and gotchas

- **Re-validate in screen space** before trusting any stock nearest-point result in build 2026.10.10.5554.
- **Small differences from stock remain.** MeasureTools passes `spliceVehicleFromNow: false` and the default radius for flight-plan patches and celestials, where stock uses `count == 0` and per-object grab radii, and stock also skips stars and already-hovered bodies.
- **`Vehicle.BurnPlan` is `FlightComputer.BurnPlan`**, and a burn can belong to another vehicle's plan; gate on `burn.Vehicle`.
- **The anchor keeps the old orbit.** A burn that changes the orbit leaves the point on the old shape.
- **Hidden orbits are not candidates**, which also means anything that changes `ShouldDrawLines` (see [Draw a predicted trajectory, approach 6](../draw-a-predicted-trajectory-and-map-markers/draw-a-predicted-trajectory-and-map-markers-6.md)) changes what can be picked.

## See also

- [Overview](overview.md); [Limb snap](anchor-and-pick-points-on-bodies-3.md)
- [Orbit-line snapping (mod feature)](../../../../mods/ksa-measuretools/features/orbit-line-snapping.md)
- [Create and read maneuver nodes](../../autopilot/create-and-read-maneuver-nodes/overview.md)

[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^ksa-orbithovercandidate]: KSA.OrbitHoverCandidate (decompiled, build 2026.10.10.5554)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
[^ksa-celestialposition]: KSA.CelestialPosition (decompiled, build 2026.10.10.5554)
