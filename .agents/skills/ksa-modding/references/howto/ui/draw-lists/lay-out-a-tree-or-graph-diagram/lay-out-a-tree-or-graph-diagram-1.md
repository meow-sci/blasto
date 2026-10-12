---
type: How-To
title: Lay out a tree in three modes with a game-free pipeline, Barnes-Hut and an iteration budget
description: Run measure, band or well assignment, tidy-tree X, grid snap, octilinear routing and greedy label placement, or a deterministic force layout whose iteration count shrinks as the node count grows
category: ui
group: draw-lists
topic: lay-out-a-tree-or-graph-diagram
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksa-deltavmap/features/three-layout-modes]
techniques: [imgui-drawlist]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui]
tags: [layout, tidy-tree, force-directed, barnes-hut, grid-snap, budget]
---
KSA-DeltaVMap draws one body hierarchy three ways: Cumulative-down (height is cumulative delta-v from where you are, like reading a subway map downward), Gravity-well (every body's low orbit on one spine, surfaces below, high orbits above) and Spring (an organic force-directed web with the root pinned). All three run through a fixed pipeline in a game-type-free engine under `DeltaVMap/Layout/`; only `VisualTreeAdapter` touches game-derived data, which is what lets the same engine run in the mod's offline test executable. Demonstrated by [Game-free subway-map layout engine with three modes](../../../../mods/ksa-deltavmap/features/three-layout-modes.md). Prerequisite: a node type with width, depth and edges that you fill from your data.

## Background: how the game does it

The game offers no layout engine. The one engine fact the pipeline depends on is label measurement: `ImGui.CalcTextSize(ImString)` ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8092-L8098)[^brutal-imguiapi-imgui]) needs the current ImGui context and font, so the layout must run inside the ImGui frame. The engine takes the measurer as a `Func<string, double>` parameter, and tests substitute `text.Length * CharWidthPx`.

## Lifecycle and timing

Layout runs inside `frame:draw-ui` (from `[StarMapAfterGui]` via `MapWindow.RebuildAt` and `ComposeScene`) and only on a rebuild: re-root, system change, detail or visibility toggle, layout toggle or search reveal. It never runs per frame; the per-frame cost is drawing. The build is not moved to a background thread because of the `CalcTextSize` requirement; instead the node ceiling (approach 2) and the spring iteration budget bound its cost. See [Game loop and mod lifecycle](../../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Fix the pipeline order and keep each pass a pure function of the tree.** Spring skips both the Y and X passes because the force layout sets both axes:

```csharp
// DeltaVMap/Layout/LayoutEngine.cs L33-L55
public static LayoutResult Run(LayoutTree tree, LayoutConfig cfg, Func<string, double>? measureText = null)
{
    cfg.Validate();
    MeasureNodes(tree, cfg, measureText);
    if (cfg.Mode == LayoutMode.Spring)
    {
        // Force-directed sets both axes at once; the tidy tree and band passes do not run.
        SpringLayout.Assign(tree, cfg);
    }
    else
    {
        if (cfg.Mode == LayoutMode.GravityWell)
            WellLayout.Assign(tree, cfg);
        else
            BandLayout.AssignBands(tree, cfg);
        TidyTree.AssignX(tree, cfg);
    }
    Spread(tree, cfg);
    GridSnap.Snap(tree, cfg);
    EdgeRouter.Route(tree, cfg);
    LabelPlacer.Result labels = LabelPlacer.Place(tree, cfg);
    return BuildResult(tree, cfg, labels);
}
```

2. **Measure first.** `MeasureNodes` fills label, short-label and summary widths from the injected measurer, then node width (text plus badge padding) and dot radius by role and rank. Everything later needs widths, which is why measurement is the only step that needs ImGui.

3. **Cumulative-down: assign bands from delta-v.** A normal edge drops its child at least one band below its parent; hub links add zero bands so the hub spine and the root share a row:

```csharp
// DeltaVMap/Layout/BandLayout.cs L26-L52
private static void Assign(LayoutNode node, LayoutConfig cfg)
{
    foreach (LayoutEdge edge in node.Out)
    {
        LayoutNode child = edge.To;
        int step = edge.IsHubLink ? 0 : BandStep(edge.Dv, cfg);
        child.Band = node.Band + step;
        child.Y = child.Band * cfg.BandHeightPx;
        Assign(child, cfg);
    }
}

// Map a dV magnitude to a whole number of band steps. A tiny rung still advances
// one band (so the parent and child never overlap); a large transfer advances
// proportionally more, clamped so the map does not grow unreadably tall.
private static int BandStep(double dv, LayoutConfig cfg)
{
    if (double.IsNaN(dv) || dv <= 0.0)
        return cfg.MinBandStep;

    int step = (int)Math.Round(dv / cfg.BandQuantumDv, MidpointRounding.AwayFromZero);
    if (step < cfg.MinBandStep)
        step = cfg.MinBandStep;
    if (step > cfg.MaxBandStep)
        step = cfg.MaxBandStep;
    return step;
}
```

4. **Assign X with a tidy tree.** `TidyTree` is the linear-time Walker algorithm (Buchheim-Junger-Leipert) extended to variable node widths: sibling separation is half of each width plus `SiblingGapPx`, so wide labels push neighbours apart. The hub bus is handled explicitly: each bus node's spoke subtree is laid out on its own and the blocks are packed left to right ([DeltaVMap/Layout/TidyTree.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/TidyTree.cs)).

5. **Gravity-well: group into columns.** `WellLayout` puts each body's anchor (low orbit, else hub, else intercept, else surface) on band 0; rungs go below or above with the step clamped, and `TidyTree.AssignXColumns` places the columns ([DeltaVMap/Layout/WellLayout.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/WellLayout.cs)).

6. **Spring: deterministic and bounded.** Initial positions follow a golden-angle spiral (no randomness, so the result never jitters between builds), the root is pinned at the origin, repulsion uses a pooled Barnes-Hut quadtree (theta 0.9) and each move is capped by a linearly cooling temperature:

```csharp
// DeltaVMap/Layout/SpringLayout.cs L26-L37
internal static class SpringLayout
{
    // Golden angle, for a low-clumping deterministic initial spiral.
    private const double GoldenAngle = 2.399963229728653;

    // Iteration scaling: keep the configured count for small graphs, and shrink toward a floor
    // as the node count grows, so a large settle never stretches into a quadratic-cost
    // freeze. The budget is the target product of iterations and node count, held between the
    // two clamps below; past the floor the per-build work grows only linearly again, and the
    // absolute node ceiling that caps it lives render-side (MapWindow.MaxLayoutNodes).
    private const int IterationWorkBudget = 120_000;
    private const int MinIterations = 60;
```

```csharp
// DeltaVMap/Layout/SpringLayout.cs L53-L65
for (int i = 0; i < n; i++)
{
    index[nodes[i]] = i;
    double radius = k * Math.Sqrt(i + 1);
    double angle = i * GoldenAngle;
    px[i] = radius * Math.Cos(angle);
    py[i] = radius * Math.Sin(angle);
    nodes[i].Band = 0;
}

int rootIndex = index[tree.Root];
px[rootIndex] = 0.0;
py[rootIndex] = 0.0;
```

7. **Scale the iteration count with the node count.** Small graphs get the configured count; large graphs shrink toward a floor so the settle never becomes a quadratic freeze:

```csharp
// DeltaVMap/Layout/SpringLayout.cs L156-L166
// Iterations to run for a graph of n nodes: the configured count for small graphs, scaled
// down toward MinIterations as n grows so a large system cannot stretch the settle into a
// multi-second build. Past about n = IterationWorkBudget / MinIterations the floor holds, so
// per-build work grows linearly again (MinIterations * O(n log n), not quadratic);
// the hard node bound is the render-side MaxLayoutNodes ceiling, not this. Never returns
// more than the configured count.
private static int ScaleIterations(int configured, int n)
{
    int byBudget = IterationWorkBudget / Math.Max(1, n);
    return Math.Min(configured, Math.Max(MinIterations, byBudget));
}
```

8. **Snap to a grid, route, and place labels.** `GridSnap` rounds each node to a square cell and nudges collisions in a stable (row, col, id) order, because the cell is wider than the two largest dots (so unique cells mean no overlapping dots). `EdgeRouter` makes octilinear polylines; `LabelPlacer` tries right, left, above, below checked against a spatial hash ([DeltaVMap/Layout/GridSnap.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/GridSnap.cs#L14-L40), [DeltaVMap/Layout/EdgeRouter.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/EdgeRouter.cs), [DeltaVMap/Layout/LabelPlacer.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/LabelPlacer.cs)).

9. **Switch modes by relayout, not by redraw.** `SetLayoutMode` is a no-op for the active mode, otherwise it rebuilds at the current root using the cached graph; only the visual tree and layout are redone ([DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L1176-L1297)).

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Layout/LayoutEngine.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/LayoutEngine.cs) | Pipeline and measurement |
| [DeltaVMap/Layout/BandLayout.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/BandLayout.cs) | Cumulative-dV bands |
| [DeltaVMap/Layout/TidyTree.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/TidyTree.cs) | Variable-width Walker tidy tree, bus packing, well columns |
| [DeltaVMap/Layout/SpringLayout.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/SpringLayout.cs) | Deterministic Fruchterman-Reingold with Barnes-Hut |
| [DeltaVMap/Layout/VisualTreeAdapter.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/VisualTreeAdapter.cs) | The only bridge from the game-typed tree |

## Variations and gotchas

- **Determinism matters.** A random initial placement would make the map jitter every rebuild; the golden-angle spiral costs nothing.
- **Layout writes into the nodes.** A layout run mutates positions, so a caller that retries (approach 3) must build a fresh tree per call.
- **No game types in the engine.** Keep the adapter as the single bridge, so the engine can be unit-tested headless.
- **Budget, not threads.** Offloading to a worker is not an option here because measurement needs the ImGui context.
- **Version.** Only `ImGui.CalcTextSize` touches the game.

## See also

- [Overview of tree and graph layout](overview.md)
- [Approach 2: aggregation, search and node ceiling](lay-out-a-tree-or-graph-diagram-2.md)
- [Build pannable, zoomable and 3D canvases inside an ImGui window](../../../../howto/ui/draw-lists/build-a-pan-zoom-canvas-in-imgui/overview.md)
- Mod feature: [Game-free subway-map layout engine with three modes](../../../../mods/ksa-deltavmap/features/three-layout-modes.md)

[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
