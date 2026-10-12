---
type: Mod Feature
title: Route finding and accumulation
description: Click a node to route from "you are here" along the unique tree path (lowest common ancestor), sum the per-leg burns with from-surface, landing, aerobraking, plane-change and return-trip toggles, and show a per-segment breakdown with a display-time piloting margin
mod: ksa-deltavmap
category: flight-and-orbits
techniques: [tree-lca-routing, patched-conic-math, imgui-window, imgui-drawlist, cached-ui-strings]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Program
  - KSA.Vehicle
  - KSA.Orbit
  - KSA.IParentBody
  - KSA.PhysicalAtmosphereReference
  - KSA.ConsoleWidgets
tags: [delta-v, routing, lca, aerobraking, plane-change, return-trip, imgui, route-panel]
---

Click any body or rung on the map and the route from where your vessel is now to that state lights
up, with a per-segment breakdown ("Eject from Earth", "Capture at Mars (ellipse)", "Circularize at
Mars", "Land on Mars"), a total, the transfer time and a bar comparing the total with your
vessel's staged delta-v. Toggles add the launch from the surface, the landing, aerobraking at the
destination or on the way back, the plane change and a return trip; a piloting-margin slider
inflates every figure without touching the route.

Because the map is a tree rooted at the body you are at, routing needs no graph search: the path
between two nodes is unique and runs through their lowest common ancestor. The accumulator then
walks that path once, derives each transfer's two burns with the shared `TransferBurns` rule and
applies the toggles.[^dvm-routefinder][^dvm-routeaccumulator]

## How it works

### From a click to a route

1. `MapWindow.HandleInput`: a click without drag on a node toggles `_selectedId` (a second click
   clears it). Shift+click re-roots instead; a "+N" minor group is not routable. Clicking a
   `Surface` node also ticks "Land at destination" so the route and the bar include the landing.
2. `RecomputeRoute` resolves the clicked node with `ResolveTarget`: a hub bus routes to that body's
   low orbit or parking orbit (the ego system's own root star or barycenter normally has no such
   node, so it plans no route); with "Land at destination" on, a low-orbit or intercept target extends to the body's surface.
3. `ResolveOrigin` picks the start: `you ?? lowOrbit ?? surface ?? root`, or with "From surface"
   `surface ?? lowOrbit ?? you ?? root`. A hub-only root never offers its parking orbit as origin.
   Without a vessel (for example in the editor) the map still routes from the root's low orbit.
4. `RouteFinder.FindPath(origin, target)` returns the steps; `BuildRouteContext` asks
   `InterstellarLegs.VesselParksAt` whether an interstellar departure can burn from the vessel's
   own orbit.[^dvm-interstellarlegs]
5. `RouteAccumulator.Accumulate` produces a `RouteSummary`; the node Ids go into `_routeNodeIds` for
   the highlight, and the first transfer-window sibling on the path biases the transfer-window
   clock.[^dvm-mapwindow]

### Lowest common ancestor

`FindPath` records the origin's ancestor chain in a `Dictionary<StateNode, int>`, walks up from the
target until it hits a node in that dictionary (the LCA), then emits the origin half as backward
steps (`ParentEdge` traversed To to From) and the target half as forward steps. `Nodes` always has
`Steps.Count + 1` entries. Every star system hangs in the same tree below its `Interstellar` edge,
so a cross-system target needs nothing special; a `null` result (no shared ancestor) is guarded but
cannot happen.[^dvm-routefinder]

### Accumulation

Pass 1 finds the aerobrake body: the last body on the path whose transfer arrival is not the hub
and whose atmosphere is usable (`SeaLevelDensity > 0.01 kg/m^3`), for example Mars on an Earth to
Phobos trip. Pass 2 walks the steps in order:[^dvm-routeaccumulator]

| Step | Segments added |
|---|---|
| `Transfer` | Depart burn ("Depart X orbit" at a hub, "Eject from X" otherwise) and arrive burn ("Arrive X orbit", "Capture at X", or "Capture at X (ellipse)" when split), both from `TransferBurns.ComputeLegs`; adds `TransferTimeSeconds`; with the toggle on, adds the plane change against the smaller `vInf` for sibling legs only |
| `Ascent` | Forward (low orbit to surface) uses `DescentDv`, backward uses the ascent `LadderDv`; the difference is recorded in `returnAdjustment` |
| `Raise`, `Capture`, `Land` | `LadderDv` |
| `HubLink` | nothing, except a cruise link (below) |

`AddSegment` zeroes a segment when aerobraking is on and the segment is the capture at the
aerobrake body (the transfer arrive burn or the `Capture` ladder edge), and keeps both the applied
`outbound` sum and the raw `baseSum`. The return trip is not twice the outbound:
`ReturnDv = baseSum + returnAdjustment - (returnAero ? originDepartBurn : 0)`, i.e. it pays the
destination ascent instead of its landing, and a separate "Aerobraking on return" zeroes the final
capture at the origin, which equals the first ejection burn by symmetry.

A vessel orbiting a star or barycenter directly (a `YouAreHere` on a hub-only body that is not an
open cruise) gets real costs for the otherwise dV-free hub links: `CruiseLegs` prices a Hohmann (or
comet `ConicTransfer`, or own-central-mass transfer) from the vessel's radius to the child, with an
Oberth capture at the child; `FromCruiseOrbit` replaces the ejection from a star the vessel is
already orbiting with an Oberth burn at the vessel's radius. A path that contains an
`Interstellar` or `Approach` edge is handed to `AccumulateInterstellar` instead, which folds that
part into one `InterstellarRoute` priced by the interstellar section.

### Toggles

| `RouteOptions` field | Effect |
|---|---|
| `FromSurface` | Origin is the root surface; adds the ascent |
| `LandAtDestination` | Target extends to the surface (descent cost) |
| `Aerobraking` | Zeroes the outbound capture at the last atmospheric body |
| `AerobrakingReturn` | Zeroes the return capture at the origin; only offered with a return trip and an atmospheric origin |
| `IncludePlaneChange` | Adds the plane change to the outbound and again to the return |
| `ShowReturnTrip` | Shows return and round-trip totals |

All default to off and are session-only.[^dvm-routeoptions] For an interstellar route the panel
shows aerobraking, plane change and return as disabled hints.

### Route panel

`RoutePanelRenderer.Draw` lays out, top to bottom: the legend, route toggles, a view section
(minor bodies, comets, transfer times, body markers, piloting margin), the breakdown, the
interstellar section when present, the totals, the vehicle bar, then fuel and the stock planner
button for interstellar routes. It returns a `PanelResult(RouteChanged, RebuildNeeded)`;
`MapWindow.DrawPanel` rebuilds the tree for a visibility change and only re-accumulates for a
toggle change.[^dvm-routepanel][^dvm-mapwindow] The rows use the game's own console widget style:
`ConsoleUi.CheckboxRow` wraps `ConsoleWidgets.BeginRow`, `Checkbox` and `EndRow`.[^dvm-consoleui][^ksa-consolewidgets]

The piloting margin (`ViewOptions.PilotingMarginPercent`, 0 to 50) becomes
`DvScale = 1 + percent / 100` and is multiplied in only when strings are built: every segment, the
plane change, the totals and the bar's needed figure. The total line appends
"(incl. +N% margin)" so a forgotten margin does not read as a wrong number.[^dvm-viewoptions] All
breakdown and total strings are rebuilt only when a `TextKey` record (summary reference, scale,
margin, plane-change and return toggles, interstellar view version) changes.

The vehicle bar (described with the staged readout) fills with `needed / available` and is green at
`available >= 1.1 * needed`, yellow at `>= 0.9 * needed`, red below.

## Lifecycle and timing

Everything runs on the main thread inside the `[StarMapAfterGui]` draw (`frame:draw-ui`), and only
while the map window is open.

- Route computation is event-driven: `RecomputeRoute` runs on a click, on a toggle change
  reported by the panel, after a rebuild that keeps the selection (`RebuildPreservingSelection`,
  `FollowTo`), and when the interstellar section reports that the vessel parked at or left the
  departure body. A toggle is drawn, then the route is recomputed after the panel returns, so its
  effect shows from the next frame.[^dvm-mapwindow]
- Per frame the panel only draws cached strings; the staged delta-v for the bar is read through a
  gated cache (see the staged readout feature).
- State read: the tree nodes and cached ladders (built on the main thread), and in
  `BuildRouteContext` the controlled vessel's `HasMeaningfulOrbit`, `Parent` and `Orbit.Periapsis`,
  the main-thread copy applied in `frame:apply-solvers` this frame.[^ksa-vehicle] Nothing is written
  to the game.
- `RecomputeRoute` wraps accumulation in `try`/`catch` and logs once; `DrawContent` wraps the whole
  draw, because an exception escaping it would skip the base class's `ImGui.End`.

## Key code

| Path | Role |
|---|---|
| [DeltaVMap/Route/RouteFinder.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Route/RouteFinder.cs#L40-L107) | `RouteStep`, `RoutePath`, LCA path |
| [DeltaVMap/Route/RouteAccumulator.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Route/RouteAccumulator.cs#L116-L237) | Two-pass accumulation, aerobrake, return trip; cruise legs (L354-L450), plane change (L462-L474) |
| [DeltaVMap/Route/RouteOptions.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Route/RouteOptions.cs#L8-L38) | The six toggles |
| [DeltaVMap/Model/TransferBurns.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/TransferBurns.cs#L82-L129) | Shared transfer-to-burns rule[^dvm-transferburns] |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L1395-L1531) | `RecomputeRoute`, `BuildRouteContext`, `ResolveOrigin`, `ResolveTarget`; click handling at L1360-L1389 |
| [DeltaVMap/Render/RoutePanelRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/RoutePanelRenderer.cs#L78-L278) | Panel layout, cached breakdown and totals |
| [DeltaVMap/Render/ViewOptions.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/ViewOptions.cs#L22-L29) | Piloting margin and `DvScale` |

## Snippets

```csharp
// DeltaVMap/Route/RouteFinder.cs L55-L70, L76-L97
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

        // ...
        StateNode lca = cursor;
        int lcaIndex = originIndex[lca];

        var steps = new List<RouteStep>(lcaIndex + targetChain.Count);
        var nodes = new List<StateNode>(lcaIndex + targetChain.Count + 1);

        // Up from the origin to the LCA: each node's ParentEdge connects its parent
        // (From) to it (To), so we traverse it backward (To -> From).
        for (int i = 0; i < lcaIndex; i++)
        {
            nodes.Add(originChain[i]);
            steps.Add(new RouteStep(originChain[i].ParentEdge!, forward: false));
        }
        nodes.Add(lca);

        // Down from the LCA to the target: walk the target chain in reverse (child of
        // the LCA first), traversing each node's ParentEdge forward (From -> To).
        for (int i = targetChain.Count - 1; i >= 0; i--)
        {
            steps.Add(new RouteStep(targetChain[i].ParentEdge!, forward: true));
            nodes.Add(targetChain[i]);
        }
```

```csharp
// DeltaVMap/Route/RouteAccumulator.cs L203-L214, L230-L234, L340-L343
                bool canAero = e.Kind == SegmentKind.Capture && e.To.Body.Id == aeroId;
                double dv = e.LadderDv;
                if (e.Kind == SegmentKind.Ascent)
                {
                    // Ascent runs LowOrbit -> Surface: a forward step lands (descent), a
                    // backward step launches (ascent). The return reverses each leg and so
                    // pays the opposite direction; record that delta for the round trip.
                    dv = step.Forward ? e.DescentDv : e.LadderDv;
                    returnAdjustment += step.Forward ? (e.LadderDv - e.DescentDv) : (e.DescentDv - e.LadderDv);
                }
                AddSegment(summary, ref outbound, ref baseSum, aero,
                    LadderLabel(e, step.Forward), dv, e.Kind, e.IsApproximate, canAero);
        // ...
        // The return reverses the path: the transfer legs cost the same, but the Ascent
        // legs swap (take off where the outbound landed, land where it took off), which is
        // returnAdjustment. The return aerobrake then zeroes the origin capture.
        if (options.ShowReturnTrip)
            summary.ReturnDv = baseSum + returnAdjustment - (returnAero ? originDepartBurn : 0.0);
        // ...
        bool zeroed = aeroActive && canAero;
        double finalDv = zeroed ? 0.0 : baseDv;
        outbound += finalDv;
        baseSum += baseDv;
```

```csharp
// DeltaVMap/Render/MapWindow.cs L1485-L1497, L1511-L1530
    private StateNode ResolveOrigin()
    {
        VisualTree tree = _visualTree!;
        StateNode? you = tree.YouAreHere;
        StateNode? surface = tree.FindBodyNode(tree.RootBodyId, StateKind.Surface);
        // A star or barycenter root has no low orbit to start from; the parking orbit an open
        // cruise root carries is a destination, never the origin.
        StateNode? lowOrbit = SystemGraph.IsHubOnlyBody(tree.Root.Body) ? null : tree.FindBodyNode(tree.RootBodyId, StateKind.LowOrbit);

        if (_options.FromSurface)
            return surface ?? lowOrbit ?? you ?? tree.Root;
        return you ?? lowOrbit ?? surface ?? tree.Root;
    }
    // ...
        VisualTree tree = _visualTree!;
        StateNode target = clicked;

        if (target.Kind == StateKind.Hub)
        {
            StateNode? lo = tree.FindBodyNode(target.Body.Id, StateKind.LowOrbit);
            if (lo == null)
                return null;
            target = lo;
        }

        if (_options.LandAtDestination
            && (target.Kind == StateKind.LowOrbit || target.Kind == StateKind.Intercept))
        {
            StateNode? surface = tree.FindBodyNode(target.Body.Id, StateKind.Surface);
            if (surface != null)
                return surface;
        }

        return target;
```

```csharp
// DeltaVMap/Render/RoutePanelRenderer.cs L249-L266
        // Plane change is additive: it is incurred per interplanetary leg, so a round trip
        // pays it on both the outbound and the return. Every figure is scaled by the piloting
        // margin so the panel and the on-map badges agree.
        double planeChange = (options.IncludePlaneChange ? summary.PlaneChangeDv : 0.0) * scale;
        double outboundTotal = summary.OutboundDv * scale + planeChange;
        double returnTotal = summary.ReturnDv * scale + planeChange;
        double roundTrip = outboundTotal + returnTotal;
        _needed = options.ShowReturnTrip ? roundTrip : outboundTotal;

        // Plane change sits above the total and is rolled into it (the user opted in).
        if (planeChange > 0.0)
            _totals.Add(("+ plane change: " + Format.Dv(planeChange), true));
        _totals.Add(("Total: " + Format.Dv(outboundTotal) + marginNote, false));
        if (options.ShowReturnTrip)
        {
            _totals.Add(("Return: " + Format.Dv(returnTotal), true));
            _totals.Add(("Round trip: " + Format.Dv(roundTrip), false));
        }
```

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `Program.ControlledVehicle` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs) | Route context and origin fallback when there is no vessel[^ksa-program] |
| `Vehicle.HasMeaningfulOrbit`, `Vehicle.Parent`, `Vehicle.Orbit` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs) | `VesselParksAt` for the interstellar departure[^ksa-vehicle] |
| `Orbit.GetRelativeInclination`, `Orbit.Parent`, `Orbit.Mu` | [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs) | Plane change between sibling orbits; own-mass cruise legs[^ksa-orbit] |
| `IParentBody.GetAtmosphereReference`, `IParentBody.Mu` | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs) | Aerobrake eligibility; cruise-leg hub mu[^ksa-iparentbody] |
| `ConsoleWidgets.BeginRow`, `Checkbox`, `EndRow` | [current/decomp/KSA/ConsoleWidgets.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleWidgets.cs) | Stock-styled toggle rows[^ksa-consolewidgets] |

## Pitfalls and version notes

- The route starts at the "you are here" node of the last rebuild, which is not refreshed every
  frame (see the ladder feature); after a burn, the origin can be stale until a rebuild.
- `RecomputeRoute` clears the summary first, so any exception leaves no route rather than a half
  route; the error is logged once per key.
- The plane change is only priced on sibling legs (both bodies with the same `Orbit.Parent`); a
  transfer to or from a hub's own ladder, and cruise legs, never carry one.
- Aerobraking assumes the whole capture can be shed in the atmosphere; there is no heating or
  entry-speed limit, and interstellar arrivals never offer it.
- `TextKey` holds the `RouteSummary` by reference, so the cached strings depend on the
  accumulator building a new summary for every change, which `RecomputeRoute` does.
- No settings persist: toggles and the margin reset with the session.

## See also

- [Closed-form patched-conic transfer model](./patched-conic-transfer-model.md)
- [Per-body state ladder and "you are here"](./body-state-ladder-and-you-are-here.md)
- [Interstellar routes and fuel](./interstellar-routes-and-fuel.md)
- [Vehicle staged delta-v readout](./vehicle-staged-dv-readout.md)
- [Map window and menu entry](./map-window-and-menu-entry.md)
- [Ego re-rooting and vessel follow](./ego-rerooting-and-vessel-follow.md)
- [Transfer windows and phase clock](./transfer-windows-and-phase-clock.md)
- [Panel rows built on the game's ConsoleWidgets kit](./stock-console-widget-panels.md)
- [Subway-map canvas rendering](./subway-map-canvas-rendering.md)
- [KSA-DeltaVMap overview](../overview.md)
- [Find a delta-v route through a re-rooted body tree and total it with a display-time margin](../../../howto/flight-and-orbits/transfers/plan-multi-leg-and-interstellar-delta-v-routes/plan-multi-leg-and-interstellar-delta-v-routes-1.md) - how-to article demonstrating this feature

[^dvm-routefinder]: DeltaVMap/Route/RouteFinder.cs
[^dvm-routeaccumulator]: DeltaVMap/Route/RouteAccumulator.cs
[^dvm-routeoptions]: DeltaVMap/Route/RouteOptions.cs
[^dvm-transferburns]: DeltaVMap/Model/TransferBurns.cs
[^dvm-mapwindow]: DeltaVMap/Render/MapWindow.cs
[^dvm-routepanel]: DeltaVMap/Render/RoutePanelRenderer.cs
[^dvm-viewoptions]: DeltaVMap/Render/ViewOptions.cs
[^dvm-consoleui]: DeltaVMap/Render/ConsoleUi.cs
[^dvm-interstellarlegs]: DeltaVMap/Model/InterstellarLegs.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-consolewidgets]: KSA.ConsoleWidgets (decompiled, build 2026.10.10.5554)
