---
type: How-To
title: Find a delta-v route through a re-rooted body tree and total it with a display-time margin
description: Route between two states through their lowest common ancestor in a tree rooted at the body you are at, accumulate the legs in two passes with landing, aerobraking and plane-change toggles, and apply a piloting margin only when strings are built
category: flight-and-orbits
group: transfers
topic: plan-multi-leg-and-interstellar-delta-v-routes
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-deltavmap/features/route-finding-and-accumulation]
techniques: [tree-lca-routing]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.Orbit
  - KSA.IParentBody
  - KSA.ConsoleWidgets
  - KSA.AtmosphericBody
  - KSA.PhysicalAtmosphereReference
tags: [route, lca, accumulation, aerobraking, piloting-margin, delta-v-map, imgui]
---

Outcome: click a body or a rung and the route from where the vessel is lights up with a per-segment breakdown, a total, the transfer time and a bar comparing the total with the vessel's staged delta-v. KSA-DeltaVMap implements it.[Route finding and accumulation](../../../../mods/ksa-deltavmap/features/route-finding-and-accumulation.md)
Prerequisite: StarMap for a draw hook.

## Background: how the game does it

The game supplies no route object; the mod builds one. Because the map is a tree rooted at the body you are at (every star system hangs below an `Interstellar` edge), the path between two nodes is unique and runs through their lowest common ancestor, so no graph search is needed. The per-edge costs come from the shared transfer rule in
[Estimate transfer delta-v in closed form with patched conics](../estimate-transfer-delta-v-with-patched-conics/overview.md) and the node states from [approach 2](plan-multi-leg-and-interstellar-delta-v-routes-2.md). The vessel's staged delta-v for the bar is read from the game's staging data (see [Reading staged delta-v, thrust and Isp from the game or from your own stage model](../../../vehicles-and-parts/staging-and-engines/read-staged-delta-v-from-the-game/overview.md)).

## Lifecycle and timing

Everything runs on the main thread inside the `[StarMapAfterGui]` draw (`frame:draw-ui`), only while the map window is open. Route computation is **event-driven**: it runs on a click, on a toggle change reported by the panel, after a rebuild that keeps the selection, and when the interstellar section reports a parked or departed vessel. A toggle is drawn, then the route is recomputed after the panel returns, so its effect shows from the next frame. Per frame the panel only draws cached strings. The vessel read is the main-thread copy applied
in `frame:apply-solvers`. Nothing is written to the game. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Resolve the click.** A click without drag toggles the selected node; shift+click re-roots. A hub bus routes to that body's low or parking orbit; with "Land at destination" a low-orbit or intercept target extends to the surface. The origin is `you ?? lowOrbit ?? surface ?? root` (or surface first with "From surface").
2. **Find the path through the LCA.** Index the origin's ancestor chain, walk up from the target until a shared node is hit, emit the origin half as backward steps and the target half as forward steps; `Nodes` always has `Steps.Count + 1` entries:

```csharp
// DeltaVMap/Route/RouteFinder.cs L42-L72
public static RoutePath? FindPath(StateNode origin, StateNode target)
{
    if (ReferenceEquals(origin, target))
        return new RoutePath
        {
            Origin = origin,
            Target = target,
            Steps = System.Array.Empty<RouteStep>(),
            Nodes = new[] { origin }
        };

    // Index the origin's ancestor chain (origin first, root last) so the target's
    // upward walk can stop at the first shared node, the LCA.
    var originChain = new List<StateNode>();
    var originIndex = new Dictionary<StateNode, int>();
    for (StateNode? n = origin; n != null; n = n.Parent)
    {
        originIndex[n] = originChain.Count;
        originChain.Add(n);
    }

    var targetChain = new List<StateNode>();
    StateNode? cursor = target;
    while (cursor != null && !originIndex.ContainsKey(cursor))
    {
        targetChain.Add(cursor);
        cursor = cursor.Parent;
    }

    // No shared ancestor means the two nodes are in different trees, which cannot
    // happen because every star system hangs in one tree; guard rather than crash.
```

3. **Accumulate in two passes.** Pass 1 finds the aerobrake body: the last body on the path whose transfer arrival is not the hub and whose atmosphere is usable (`SeaLevelDensity > 0.01 kg/m^3`). Pass 2 walks the steps: a `Transfer` adds depart and arrive burns from the shared `TransferBurns.ComputeLegs` plus the transfer time (and, with the toggle on, a plane change against the smaller `vInf` for sibling legs only); `Ascent` adds the descent or ascent cost;
   `Raise`, `Capture`, `Land` add their ladder cost; `HubLink` adds nothing (except a cruise link for a vessel orbiting a star).
4. **Zero the aerobraked capture** only for the capture at the aerobrake body, and keep both the applied sum and the raw base sum. The return trip is not twice the outbound: `ReturnDv = baseSum + returnAdjustment - (returnAero ? originDepartBurn : 0)`.
5. **Apply the piloting margin when building strings**, not in the data: `DvScale = 1 + percent / 100` multiplies every segment, the plane change, the totals and the bar's needed figure, and the total line appends "(incl. +N% margin)" so a forgotten margin does not read as a wrong number. Cache every breakdown and total string behind a `TextKey` record (summary reference, scale, margin, toggles) so the panel is cheap.
6. **Colour the bar** green at `available >= 1.1 * needed`, yellow at `>= 0.9 * needed`, red below.

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Route/RouteFinder.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Route/RouteFinder.cs#L40-L107) | `RouteStep`, `RoutePath`, LCA path |
| [DeltaVMap/Route/RouteAccumulator.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Route/RouteAccumulator.cs#L116-L237) | two-pass accumulation, aerobrake, return trip; cruise legs (L354-L450), plane change (L462-L474) |
| [DeltaVMap/Route/RouteOptions.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Route/RouteOptions.cs#L8-L38) | the six toggles |
| [DeltaVMap/Model/TransferBurns.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/TransferBurns.cs#L75-L137) | shared transfer-to-burns rule |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L1395-L1531) | `RecomputeRoute`, `BuildRouteContext`, `ResolveOrigin`, `ResolveTarget` |
| [DeltaVMap/Render/RoutePanelRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/RoutePanelRenderer.cs#L78-L278) | panel layout, cached breakdown and totals |

## Variations and gotchas

- **Plane change against `vInf`, not the hub speed.** Using the orbital speed was an earlier bug that overstated the cost about tenfold for Earth to Mars.
- **Defaults are off and session-only.** For an interstellar route aerobraking, plane change and return are shown as disabled hints.
- **A vessel orbiting a star directly** gets real costs for the otherwise dV-free hub links (`CruiseLegs`): a Hohmann or comet transfer from the vessel's radius with an Oberth capture at the child.
- Without a vessel (for example in the editor) the map still routes from the root's low orbit.

## See also

- [Overview](overview.md); [ladder and classification](plan-multi-leg-and-interstellar-delta-v-routes-2.md); [interstellar legs](plan-multi-leg-and-interstellar-delta-v-routes-3.md)
- [Route finding and accumulation](../../../../mods/ksa-deltavmap/features/route-finding-and-accumulation.md)
- [Estimate transfer delta-v in closed form with patched conics](../estimate-transfer-delta-v-with-patched-conics/overview.md)
