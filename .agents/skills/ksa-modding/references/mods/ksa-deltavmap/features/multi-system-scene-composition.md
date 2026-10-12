---
type: Mod Feature
title: Other star systems as separate graph parts joined by dashed interstellar connectors
description: Cuts the visual tree at its Interstellar edges, lays out each other star system as its own collapsed or expanded part (cached per root build), places the parts in a strip beside the never-moved home map with routed dashed connectors, and keeps the clicked node on screen when a system opens or closes
mod: ksa-deltavmap
category: ui
techniques: [graph-layout, scene-composition, imgui-drawlist]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: []
tags: [ui, layout, multi-star, interstellar, scene-composition, fit-to-view]
---

When the loaded universe has several star systems, the other systems appear beside the home map
as small graphs of their own, each joined to the home star by a dashed lavender line marked "//"
(not to scale). Clicking a system, or a body in it, selects a route there and opens that system
in place; clearing the route closes it again, and the clicked node stays where it was on screen.
Mechanism: the visual tree carries one `Interstellar` edge per other system from the ego system
hub; `MapWindow.ComposeScene` lays out each destination as a separate `LayoutPart` through
`SceneComposer.LayOutPart`, caches it per (root Id, expanded), and `SceneComposer.Compose` places
the parts in a strip and routes the connectors around the unmoved home layout.[^dvmap-mapwindow][^dvmap-scenecomposer]

## How it works

**Cutting the tree.** `VisualTree.Build` adds, for each other root from
`SystemGraph.DestinationsFrom` (nearest first, at most 8, the rest folded), an `Interstellar` edge
from the ego system hub to a destination subtree whose edges are dV-free `Approach` edges.[^dvmap-visualtree]
One tree keeps routing and selection uniform for every target, but the layout cuts it at those
edges: `VisualTreeAdapter.ToLayoutTree` converts only the ego system and
`VisualTreeAdapter.ToPartTree(edge, graph, expanded)` converts one destination, either collapsed to
a single star-system stub (with a summary line and a title carrying the distance between the two
roots) or expanded to its whole subtree.[^dvmap-visualtreeadapter]

**Which systems are open.** At most two: the one holding the selected node and the one holding
the searched body (`_expandedA`, `_expandedB`, resolved by `DestinationRootHubOf`). `ComposeScene`
looks each needed part up in `Dictionary<(string RootId, bool Expanded), LayoutPart>`; only a miss
runs the layout. The cache is cleared on every root rebuild, so opening or closing a system
re-lays out only that part.

**Laying out a part.** `LayOutPart(build, ego, measureText)` memoizes the text measurer, runs the
plain layout, and then, so a destination stays readable at the zoom that fits the much larger home
map, retries with a structure spread (`LayoutConfig.WithSpread`): in the tree modes a whole factor
`round(0.4 / nominalZoom)` clamped to 1-3 with band-step caps lowered until the part fits within
60% of the ego map across the strip and 50% along it; in Spring a fractional spread lowered in
0.25 steps. `build` must return a fresh tree per call because a layout run writes positions into
the nodes.

**Composing.** `Compose(ego, egoHub, parts, cfg)` never moves the ego layout, so the home map looks
identical with or without other systems. The strip goes below the ego map when it is at least 1.5
times as wide as tall, otherwise to its right; the side depends only on the ego bounds, so opening a
system never flips it. Gaps (165 px channel, 64 px between parts, 18 px clearance) are sized in
screen pixels at a nominal fit zoom (the zoom that fits the ego map into a 1536x936 area), with
screen-fixed dots and titles counted 35% over. Places come from the collapsed shape of every
system, nearest system next to the trunk, all roots of a row on one line; an opened system keeps
its collapsed root's place and pushes only the systems on its far side. Each connector is a trunk
leaving the ego hub by the cheapest crossing-free exit (octilinear in the tree modes, any of 32
angles in Spring), a shared bus in the channel and a short branch into the root; all of it is a
pure function of the laid-out bounds, so a delta-v or cruise-speed change never moves it.[^dvmap-layoutmodel]

**Anchoring the view.** `UpdateScene(anchorNodeId, anchorBodyId)` records the anchor node's
canvas position before recomposing, shifts the pan by the change of the scene's min corner, and
only if the anchor would now sit within 40 px of the canvas edge moves the pan to put it back where
it was. A click on another system's node calls `UpdateScene(anchorNodeId: hit.Id)` right after
`RecomputeRoute`.

**Fit and drawing.** `SceneFit.Fit` computes zoom and pan for the 32 px padded canvas and, with
parts present, also keeps screen-fixed titles at the right edge and system glyphs and titles at the
bottom edge inside the canvas.[^dvmap-scenefit] `CanvasRenderer` draws the shared network once as
dashed lines, each connector branch, a scale-break mark, system rings, stub fans, and titles that
take the first of `SceneComposer.SystemTitleSpots` that collides with nothing drawn; it records each
drawn title's rectangle so `NearestNode` can treat a click on a title as a click on its root.[^dvmap-canvasrenderer]

**Checked offline and in-game.** `SceneChecks` encodes the composed-scene rules (clearances,
channel, strip geometry, overlays) at fit zoom in screen space; it is compiled into the offline
tests and the harness tests but removed from the shipped mod (`<Compile Remove>` in the csproj).[^dvmap-scenechecks]

## Lifecycle and timing

- Hook: `[StarMapAfterGui]`, main thread, `frame:draw-ui`, inside `RebuildAt` (whole scene after a
  root build) and `UpdateScene` (selection or search changes). Never per frame.
- Part layout measures text with `ImGui.CalcTextSize` through the memoized delegate, so it has to
  run inside the ImGui frame like the ego layout.
- Inter-system distances come from `SystemGraph.Distance` on fixed root positions, computed once
  per loaded system.

## Key code

| Path | Role |
|---|---|
| [DeltaVMap/Layout/SceneComposer.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/SceneComposer.cs) | Part spreading, strip placement, connector routing, title spots |
| [DeltaVMap/Layout/SceneFit.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/SceneFit.cs) | Fit-to-view with screen-fixed overhang |
| [DeltaVMap/Layout/LayoutModel.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/LayoutModel.cs#L267-L340) | `LayoutPart`, `LayoutConnector`, `LayoutScene`, `StripSide` |
| [DeltaVMap/Layout/VisualTreeAdapter.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/VisualTreeAdapter.cs) | `ToLayoutTree` (ego) and `ToPartTree` (one destination) |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L2117-L2245) | `UpdateScene` anchoring, `ComposeScene` part cache |
| [DeltaVMap/Layout/SceneChecks.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/SceneChecks.cs) | Scene rules shared by offline and harness tests |

## Snippets

```csharp
// DeltaVMap/Render/MapWindow.cs L2212-L2245
private void ComposeScene()
{
    if (_visualTree == null || _egoLayout == null || _graph == null)
        return;
    // ...
    _shownParts.Clear();
    IReadOnlyList<Edge> interstellar = _visualTree.InterstellarEdges;
    for (int i = 0; i < interstellar.Count; i++)
    {
        Edge edge = interstellar[i];
        string rootId = edge.To.Id;
        bool expanded = rootId == _expandedA || rootId == _expandedB;
        if (!_parts.TryGetValue((rootId, expanded), out LayoutPart? part))
        {
            // ...
            SystemGraph graph = _graph;
            LayoutResult laid = SceneComposer.LayOutPart(() => VisualTreeAdapter.ToPartTree(edge, graph, expanded), _egoLayout, MeasureText);
            part = new LayoutPart { RootId = rootId, Result = laid, Expanded = expanded };
            _parts[(rootId, expanded)] = part;
        }
        _shownParts.Add(part);
    }

    LayoutScene scene = SceneComposer.Compose(_egoLayout, _egoHub, _shownParts, _egoLayout.Config);
    _scene = scene;
    _sceneById.Clear();
    IReadOnlyList<LayoutNode> nodes = scene.Nodes;
    for (int i = 0; i < nodes.Count; i++)
        _sceneById[nodes[i].Id] = nodes[i];
}
```

```csharp
// DeltaVMap/Layout/SceneComposer.cs L75-L92
public static double NominalZoom(LayoutResult ego)
{
    return Math.Min(1.0, Math.Min(NominalW / Math.Max(1.0, ego.Width), NominalH / Math.Max(1.0, ego.Height)));
}

// The whole factor a destination graph is spread by in the tree modes, so its nodes stay
// apart at the zoom that fits the ego map. It is 1 at a nominal zoom of 0.4 or more and
// grows to 3 for a huge map.
public static int SpreadFactor(LayoutResult ego)
{
    int k = (int)Math.Round(SpreadTargetZoom / NominalZoom(ego), MidpointRounding.AwayFromZero);
    return Math.Clamp(k, 1, MaxSpread);
}

public static StripSide SideOf(LayoutResult ego)
{
    return ego.Width >= BelowAspect * ego.Height ? StripSide.Below : StripSide.Right;
}
```

```csharp
// DeltaVMap/Render/MapWindow.cs L2148-L2178
if (before != null && _scene != null)
{
    _panX += (_scene.MinX - before.MinX) * _zoom;
    _panY += (_scene.MinY - before.MinY) * _zoom;
    LayoutNode? node = null;
    double sx = 0.0;
    double sy = 0.0;
    if (haveNode && anchorNodeId != null && _sceneById.TryGetValue(anchorNodeId, out LayoutNode? n))
    {
        node = n;
        sx = nodeX;
        sy = nodeY;
    }
    else if (haveRoot && fallback != null && _sceneById.TryGetValue(fallback, out LayoutNode? f))
    {
        node = f;
        sx = rootX;
        sy = rootY;
    }
    if (node != null)
    {
        double x = (node.SnappedX - _scene.MinX) * _zoom + _panX;
        double y = (node.SnappedY - _scene.MinY) * _zoom + _panY;
        bool onCanvas = _lastSize.X <= 0f
            || (x >= AnchorMarginPx && x <= _lastSize.X - AnchorMarginPx && y >= AnchorMarginPx && y <= _lastSize.Y - AnchorMarginPx);
        if (!onCanvas)
        {
            _panX = sx - (node.SnappedX - _scene.MinX) * _zoom;
            _panY = sy - (node.SnappedY - _scene.MinY) * _zoom;
        }
    }
```

## Game APIs touched

None directly. The composer and fit are pure layout code; game data arrives through the visual
tree (see [Live celestial system graph](./live-celestial-system-graph.md) for how roots and
distances are read) and text widths through the `ImGui.CalcTextSize` delegate passed in by
`MapWindow`.

## Pitfalls and version notes

- Composing the scene must not move the home layout, or every open and close would make the map
  jump. Keep the ego coordinates fixed and only offset parts.
- Cache parts by (root, expanded) and invalidate on root rebuild; a full relayout on every click
  is what makes opening a system feel slow.
- Size gaps in screen pixels at a nominal zoom, not in layout units, because dots and labels keep a
  fixed screen size while geometry scales.
- The connector length is symbolic ("//" scale break); real distances are in the title and the
  connector tooltip.

## See also

- [Three layout modes](./three-layout-modes.md)
- [Subway-map canvas rendering](./subway-map-canvas-rendering.md)
- [Interstellar routes and fuel](./interstellar-routes-and-fuel.md)
- [Search, focus, isolate and minor-body aggregation](./search-focus-isolate-and-aggregation.md)
- [Offline layout and math tests](./offline-layout-and-math-tests.md)
- [Compose cached per-system layouts around a home layout that never moves](../../../howto/ui/draw-lists/lay-out-a-tree-or-graph-diagram/lay-out-a-tree-or-graph-diagram-3.md) - how-to article demonstrating this feature

[^dvmap-scenecomposer]: DeltaVMap/Layout/SceneComposer.cs
[^dvmap-scenefit]: DeltaVMap/Layout/SceneFit.cs
[^dvmap-layoutmodel]: DeltaVMap/Layout/LayoutModel.cs
[^dvmap-visualtreeadapter]: DeltaVMap/Layout/VisualTreeAdapter.cs
[^dvmap-visualtree]: DeltaVMap/Model/VisualTree.cs
[^dvmap-mapwindow]: DeltaVMap/Render/MapWindow.cs
[^dvmap-canvasrenderer]: DeltaVMap/Render/CanvasRenderer.cs
[^dvmap-scenechecks]: DeltaVMap/Layout/SceneChecks.cs
