---
type: Mod Feature
title: Snap to any visible orbit line with the stock nearest-orbit-point machinery
description: Runs the stock Orbit.GetNearestPosition and OrbitHoverCandidate.Wins over the stock burn picker's candidate set for every body, re-checks winners on screen to close a behind-camera NaN hole, and anchors to the orbit's parent
mod: ksa-measuretools
category: flight-and-orbits
techniques: [reuse-stock-picker, screen-space-picking]
hooks: [harmony-prefix KSA.Program.OnMouseButton, starmap StarMapAfterGui]
lifecycle: [frame:input, frame:draw-ui]
game_types:
  - KSA.Orbit
  - KSA.OrbitHoverCandidate
  - KSA.CelestialPosition
  - KSA.OrbitPointCce
  - KSA.FlightPlan
  - KSA.BurnPlan
  - KSA.Burn
  - KSA.PatchedConic
  - KSA.Astronomical
  - KSA.BurnContextMenu
  - KSA.CelestialSystem
  - KSA.IParentBody
tags: [map-view, orbit-lines, snapping, patched-conics, burn-plan, picking]
---

A click near any drawn orbit line, including a vehicle's planned trajectory after a burn and the
orbits of planets and moons, places a point on that line ("Luna orbit"). The point stays on the
orbit geometry as the parent body moves.[^mt-readme]

Rather than sampling orbit lines itself, the mod calls the game's own nearest-point solver on each
candidate orbit and the game's own tie-break rules, then fixes one acceptance bug.

## How it works

`MapPicker.PickOrbitPoint` loops over `Universe.CurrentSystem.All`:[^mt-mappicker]

| Candidate | Visibility gate | Solver call |
|---|---|---|
| `Vehicle` shown (`ShowOrbit`), targeted, or controlled: each `FlightPlan.Patches[i]` | `Astronomical.ShouldDrawUiOrLines(patch.PrimaryBody, viewport, patch.Orbit)` | `patch.Orbit.GetNearestPosition(viewport, cursor, patch, out pos, spliceVehicleFromNow: false)` |
| Each burn branch of that vehicle's `BurnPlan` (`TryGetBurn`, `burn.FlightPlan.Patches`) | same | same, with `BurnContextMenu.GetGrabRadiusPercent(burn.Vehicle)` as the radius |
| `Celestial` shown or targeted | `Astronomical.ShouldDrawLines(body, viewport, body.Orbit)` | `celestial.Orbit.GetNearestPosition(viewport, cursor, null, out pos, false)` |

That is the candidate set of the stock burn picker `CelestialSystem.SetNearestOrbitPoint` (which the
game runs per frame from `NearestOrbitPointJob` on the `NearestOrbitAndPerformanceWorker` job
runner). The differences are what the winner is used for and where the scan runs: stock reads
`NearestOrbitPoint` for burn work on the controlled vehicle (left-click burn creation at the end of
`Program.OnMouseButton`, the right-click burn menu armed by `BurnContextMenu.OnRightClick`), while
the mod places a point on any body's orbit and runs the scan itself on the main thread for the
viewport it was given.[^ksa-celestialsystem][^ksa-program]

Each result becomes `new OrbitHoverCandidate(position, owner, hoversMarker, isReached)` (with
`CelestialPosition.IsReachedWithin` for reached/unreached branches, as `BurnPlan.FindNearestBurnPoint`
does) and replaces the best one if `candidate.Wins(in best, camera, cursor)`. `Wins` prefers marker
hovers, then compares owners by cursor proximity (`OrbitPointCce.IsBetterThan`), then reached
branches, then earlier patch start times.[^ksa-orbithovercandidate][^ksa-burnplan] Burn branches are
validated one patch at a time because `Wins` ranks reached and earlier patches above proximity, so
one stock pick behind the camera would hide every visible branch.

The winner becomes `Anchor.OnOrbit(position.Parent, position.Point.PositionCce, owner.Id)`: an
`IParentBody` plus a CCE offset, resolved each frame through `parent.GetPositionEclFromCce(offset)`.
The point therefore stays on the orbit's shape while the parent moves along its own orbit.[^mt-anchor]

### What GetNearestPosition does with the orbit line data

`Orbit.GetNearestPoint` (wrapped by `GetNearestPosition`) has two branches. For hyperbolic,
parabolic and circular orbits it walks `_cachedPoints`, the `OrbitPointCce` samples that
`Orbit.DrawLines` also draws from (filled through `Orbit.UpdateCachedPoints`), cut at the patch's
end time when the patch ends in an impact or at its impact-search frontier while that search is
unresolved. For elliptic orbits it solves a closed-form nearest true anomaly and builds a single
`OrbitPointCce`. Either way the candidate is accepted when its NDC distance to the cursor, with the X
term scaled by `inViewPort.Size.X / inViewPort.Size.Y`, is at most `mouseDistanceScreenPercent`
(0.025 by default).[^ksa-orbit]

### The on-screen re-check

`TryAccept` runs `IsOnScreenNearCursor` before `Wins`: the candidate is projected with
`OrbitPointCce.GetPositionScreen(parent, camera)` and must be finite and within
`max(24 px, 0.025 * viewport height)` of the cursor. The source comment documents two stock quirks
this guards against, both confirmed in the decomp:[^mt-mappicker][^ksa-orbit]

- On the elliptic branch a point behind the camera projects to NaN, and `num20 > threshold` is
  `false` for NaN, so the stock test accepts it (a click near Earth could grab a point on a distant
  orbit plane behind the camera). `IsBetterThan` then projects with `ignoreBehind: false` and can
  score the mirrored point as close.[^ksa-orbitpointcce]
- The aspect factor is an `int` division of width by height, which is 0 on a portrait viewport and
  drops the X term entirely.

## Lifecycle and timing

- Placement: the `Program.OnMouseButton` prefix (`frame:input`). Orbits and flight plans were just
  updated by `Universe.ApplyOrbitSolvers` / `ApplyVehicleSolvers`; the camera is last frame's.
- Preview: `[StarMapAfterGui]` (`frame:draw-ui`), re-picked about every 45 ms.
- The scan is closed-form per orbit and runs on the main thread. The stock picker runs the same kind
  of scan on a job runner, which the mod does not touch. The author notes this as the first place to
  optimize if a dense save shows up in the debug perf numbers.

## Key code

| Path | Role |
|---|---|
| [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L853-L980) | `PickOrbitPoint`, `AcceptBurnPoints`, `TryAccept`, `IsOnScreenNearCursor` |
| [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L27-L35) | Acceptance radius rationale |
| [MeasureTools/Features/Measure/Anchor.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Anchor.cs#L210-L219) | `Anchor.OnOrbit` |

## Snippets

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

```csharp
// current/decomp/KSA/Orbit.cs L3174-L3186 (stock acceptance on the elliptic branch)
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

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `Orbit.GetNearestPosition`, `GetNearestPoint`, `_cachedPoints`, `DrawLines` | [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L2983-L3200) | Nearest point on a drawn orbit or patch |
| `OrbitHoverCandidate.Wins` | [current/decomp/KSA/OrbitHoverCandidate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitHoverCandidate.cs) | Stock tie-break between candidates |
| `OrbitPointCce.PositionCce`, `GetPositionScreen`, `IsBetterThan` | [current/decomp/KSA/OrbitPointCce.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/OrbitPointCce.cs) | Stored offset; screen re-check |
| `CelestialPosition.Parent`, `Point`, `IsReachedWithin` | [current/decomp/KSA/CelestialPosition.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialPosition.cs) | Result of the solver |
| `CelestialSystem.SetNearestOrbitPoint` | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L473-L539) | Stock reference for the candidate set |
| `BurnPlan.TryGetBurn`, `BurnCount`, `FindBranchEndTime`, `IsHovered`, `FindNearestBurnPoint` | [current/decomp/KSA/BurnPlan.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnPlan.cs#L559-L600) | Burn branches, reached flags |
| `BurnContextMenu.GetGrabRadiusPercent` | [current/decomp/KSA/BurnContextMenu.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BurnContextMenu.cs) | Per-vehicle grab radius for burn branches |
| `Astronomical.ShouldDrawUiOrLines`, `ShouldDrawLines` | [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L493-L660) | Only orbits that are actually drawn are candidates |

## Pitfalls and version notes

- The stock acceptance test has the NaN and integer-aspect issues above in build 2026.10.10.5554;
  re-validate candidates in screen space before trusting them.
- Small differences from stock remain: for vehicle flight-plan patches the mod passes
  `spliceVehicleFromNow: false` and the default 0.025 radius, where stock passes `count == 0` for
  the splice flag and the vehicle's grab radius; for celestials the mod uses the default radius where
  stock uses `GetGrabRadiusPercent(celestial)`. Stock also skips stars and bodies the cursor is
  already hovering (`CursorHoverHit`).
- `Vehicle.BurnPlan` is `FlightComputer.BurnPlan`; burns of a vehicle can belong to another
  vehicle's plan (`burn.Vehicle`), which is why the visibility gate checks `burn.Vehicle`.
- Orbit anchors keep the orbit's shape at the time of the click; if the orbit itself changes (a burn
  executes), the anchor does not follow the new orbit.

## See also

- [Overview](../overview.md)
- [Body and limb snapping](./body-and-limb-snapping.md)
- [Live anchors in body, orbit, surface and part frames](./live-body-relative-anchors.md)
- [Pick the point on an orbit line under the cursor with Orbit.GetNearestPosition and re-validate it on screen](../../../howto/flight-and-orbits/map-view/anchor-and-pick-points-on-bodies/anchor-and-pick-points-on-bodies-4.md) - how-to article demonstrating this feature

[^mt-mappicker]: MeasureTools/Features/Measure/MapPicker.cs
[^mt-anchor]: MeasureTools/Features/Measure/Anchor.cs
[^mt-readme]: KSA-MeasureTools README.md
[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^ksa-orbithovercandidate]: KSA.OrbitHoverCandidate (decompiled, build 2026.10.10.5554)
[^ksa-orbitpointcce]: KSA.OrbitPointCce (decompiled, build 2026.10.10.5554)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
[^ksa-burnplan]: KSA.BurnPlan (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
