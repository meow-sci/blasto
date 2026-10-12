---
type: How-To
title: Compose cached per-system layouts around a home layout that never moves
description: Lay out each destination as a cached part, place the parts in a strip beside the unmoved home map, route connectors as a pure function of bounds, and re-anchor the pan only when the clicked node would leave the canvas
category: ui
group: draw-lists
topic: lay-out-a-tree-or-graph-diagram
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksa-deltavmap/features/multi-system-scene-composition]
techniques: [imgui-drawlist, imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, KSA.CelestialSystem]
tags: [layout, composition, cache, stable, anchor, multi-system]
---
When the loaded universe has several star systems, KSA-DeltaVMap shows the others beside the home map as small graphs of their own, joined to the home star by a dashed lavender line marked "//" (not to scale). Opening or closing a system must not make the home map jump. The mechanism: the visual tree carries one interstellar edge per other system, each destination is laid out as a separate cached part, and a composer places the parts in a strip and routes connectors around a home layout it never moves. Demonstrated by [Other star systems as separate graph parts joined by dashed interstellar connectors](../../../../mods/ksa-deltavmap/features/multi-system-scene-composition.md). Prerequisite: [approach 1](lay-out-a-tree-or-graph-diagram-1.md).

## Background: how the game does it

Each star system is rooted at an `IIndependentRoot` ([current/decomp/KSA/IIndependentRoot.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IIndependentRoot.cs#L7)[^ksa-iindependentroot]); `IIndependentRoot.RootOf(body)` walks up a body's parent chain to it. The mod reads their positions once per loaded system to build its system graph; inter-system distances come from fixed root positions. The composer itself is pure layout code and reads no game state. Label measurement again needs the ImGui context (`ImGui.CalcTextSize`, [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8092-L8098)[^brutal-imguiapi-imgui]).

## Lifecycle and timing

Composition runs in `frame:draw-ui` (from `[StarMapAfterGui]`) inside `RebuildAt` (the whole scene after a root build) and `UpdateScene` (selection or search changes), never per frame. Part layout measures text through a memoized delegate, so it must run inside the ImGui frame like the home layout. See [Game loop and mod lifecycle](../../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Cut the tree at the system boundary.** The visual tree is one tree (so routing and selection are uniform), but layout converts only the home system with `ToLayoutTree` and each destination with `ToPartTree(edge, graph, expanded)`, collapsed to a single stub or expanded to its whole subtree ([DeltaVMap/Layout/VisualTreeAdapter.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/VisualTreeAdapter.cs)).

2. **Cache parts by (root, expanded).** At most two systems are open (the one holding the selection and the one holding the searched body). A miss runs the layout; the cache is cleared on every root rebuild, so opening a system lays out only that part. `build` must return a fresh tree per call because a layout run writes positions into the nodes:

```csharp
// DeltaVMap/Render/MapWindow.cs L2212-L2245
    private void ComposeScene()
    {
        if (_visualTree == null || _egoLayout == null || _graph == null)
            return;
#if DEBUG
        using var perf = new PerfTracker.Scope("DvMap.Compose");
#endif
        _shownParts.Clear();
        IReadOnlyList<Edge> interstellar = _visualTree.InterstellarEdges;
        for (int i = 0; i < interstellar.Count; i++)
        {
            Edge edge = interstellar[i];
            string rootId = edge.To.Id;
            bool expanded = rootId == _expandedA || rootId == _expandedB;
            if (!_parts.TryGetValue((rootId, expanded), out LayoutPart? part))
            {
#if DEBUG
                using var partPerf = new PerfTracker.Scope("DvMap.Layout.Part");
#endif
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

3. **Spread destinations so they stay readable at the home zoom.** The home map is much larger, so the fit zoom is small; a part is retried with a structure spread factor derived from the nominal zoom. Derive these from the home layout's size, never from the part:

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

4. **Place parts without moving the home layout.** `Compose` puts the strip below the home map when it is at least 1.5 times as wide as tall, otherwise to its right; the side depends only on the home bounds, so opening a system never flips it. Gaps are sized in screen pixels at a nominal fit zoom (dots and titles stay a fixed screen size), nearest system next to the trunk, and an opened system keeps its collapsed root's place while pushing only the systems on its far side ([DeltaVMap/Layout/SceneComposer.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/SceneComposer.cs)).

5. **Route connectors from bounds only.** Each connector is a trunk leaving the home hub by the cheapest crossing-free exit, a shared bus in the channel and a short branch into the root; all of it is a pure function of the laid-out bounds, so a delta-v or cruise-speed change never moves it.

6. **Anchor the view across recomposition.** Record the anchor node's canvas position before recomposing, shift the pan by the change of the scene's min corner, and only if the anchor would now sit within a margin of the canvas edge put it back where it was:

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

7. **Fit with screen-fixed overhang.** `SceneFit.Fit` computes zoom and pan for a padded canvas and, with parts present, keeps titles and glyphs (screen-fixed) inside the canvas ([DeltaVMap/Layout/SceneFit.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/SceneFit.cs)).

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Layout/SceneComposer.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/SceneComposer.cs) | Part spreading, strip placement, connector routing, title spots |
| [DeltaVMap/Layout/SceneFit.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/SceneFit.cs) | Fit-to-view with screen-fixed overhang |
| [DeltaVMap/Layout/LayoutModel.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/LayoutModel.cs#L267-L340) | `LayoutPart`, `LayoutConnector`, `LayoutScene`, `StripSide` |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L2117-L2245) | `UpdateScene` anchoring and `ComposeScene` part cache |

## Variations and gotchas

- **Never move the home layout.** Composition must not alter it, or every open and close makes the map jump.
- **Invalidate on root rebuild.** Cache parts per (root, expanded) and clear them when the root changes; a full relayout on every click is what makes opening a system feel slow.
- **Pixels, not layout units.** Gap sizes are screen pixels at a nominal zoom, because dots and labels keep a fixed screen size while geometry scales.
- **Symbolic length.** The connector length is not to scale; real distances are in the title and tooltip.
- **Version.** Pure layout code on top of the system graph.

## See also

- [Overview of tree and graph layout](overview.md)
- [Approach 1: three layout modes](lay-out-a-tree-or-graph-diagram-1.md)
- [Approach 4: ego re-rooting](lay-out-a-tree-or-graph-diagram-4.md)
- [Build pannable, zoomable and 3D canvases inside an ImGui window](../../../../howto/ui/draw-lists/build-a-pan-zoom-canvas-in-imgui/overview.md)
- Mod feature: [Other star systems as separate graph parts joined by dashed interstellar connectors](../../../../mods/ksa-deltavmap/features/multi-system-scene-composition.md)

[^ksa-iindependentroot]: KSA.IIndependentRoot (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
