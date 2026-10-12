---
type: Mod Feature
title: Game-free subway-map layout engine with three modes
description: A game-type-free pipeline lays out the delta-v tree as cumulative-down bands, gravity wells on a low-orbit spine, or a Barnes-Hut spring web, measuring labels with ImGui.CalcTextSize
mod: ksa-deltavmap
category: ui
techniques: [graph-layout, tidy-tree, force-directed-layout, imgui-text-metrics]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: []
tags: [ui, layout, tidy-tree, force-directed, barnes-hut, graph-drawing, subway-map]
---

The map can be drawn three ways, switched from a 60 px icon button in the canvas corner or the
**Options > Layout** menu: **Cumulative-down** (default; height is cumulative delta-v from where
you are, like reading a subway map downward), **Gravity-well** (every body's low orbit on one
horizontal spine, surfaces hanging below, high orbits and captures above, the shape of the
canonical KSP delta-v map) and **Spring** (an organic force-directed web with the root pinned in
the middle). Mechanism: a game-type-free layout engine under `DeltaVMap/Layout/` runs a fixed
pipeline whose Y and X passes branch on `LayoutConfig.Mode`; only `VisualTreeAdapter` touches
game-derived data, which is what lets the same engine run in the offline test executable.[^dvmap-layoutengine][^dvmap-layoutconfig][^dvmap-visualtreeadapter]

## How it works

**Input.** `VisualTreeAdapter.ToLayoutTree(visual, graph)` converts the re-rooted `VisualTree`
into `LayoutNode` / `LayoutEdge` objects (`LayoutKind`, `EdgeClass` Ladder / Transfer / HubLink,
rank, label, a representative `Dv` per edge). The representative dV for band placement is the
ladder cost or the sum of a transfer's two v_inf legs; exact Oberth burns are only needed for the
badge and the route.[^dvmap-layoutmodel]

**Pipeline** (`LayoutEngine.Run`):

1. `MeasureNodes`: label, short-label and summary widths from the `measureText` delegate
   (`ImGui.CalcTextSize(label).X` in game, `text.Length * CharWidthPx` offline); node width =
   text + badge padding; dot radius by role and rank.
2. Y pass: `BandLayout.AssignBands` (cumulative) or `WellLayout.Assign` (gravity well). Spring
   skips both and the X pass.
3. X pass: `TidyTree.AssignX`.
4. `Spread`: optional stretch about the root (used when laying out other star systems).
5. `GridSnap.Snap`: square grid (35 px) with deterministic nearest-free-cell nudging.
6. `EdgeRouter.Route`: octilinear polylines (straight lines in Spring).
7. `LabelPlacer.Place`: greedy label slots (right, left, above, below) checked against a
   spatial hash of placed labels and dots.

**Cumulative-down.** `BandLayout` assigns `child.Band = parent.Band + step`, where `step =
round(dv / 250 m/s)` clamped to [1, 6] and HubLink edges add 0, so the whole hub spine and the
root share one row.[^dvmap-bandlayout] `TidyTree` is the linear-time Walker algorithm
(Buchheim-Junger-Leipert) extended to variable node widths (van der Ploeg / d3-flextree): sibling
separation is half of each width plus `SiblingGapPx`, so wide labels push neighbours apart and
sibling subtrees cannot overlap. The hub bus is handled explicitly: `CollectBus` follows HubLink
edges from the root, each bus node's spoke subtree is laid out on its own, and the blocks are
packed left to right with `BusGapPx` (plus `RootMarginPx` after the root's cluster).[^dvmap-tidytree]

**Gravity-well.** `WellLayout` groups nodes into columns (one per body's well). Each well's anchor
(low orbit, else hub, else intercept, else surface) sits on band 0; rungs reached by a ladder edge
go below (toward the surface) or above (high orbits, captures) with `step` clamped to
`WellMaxBandStep = 3` on a 70 px band, and each side is de-collided into strictly increasing
magnitudes. `TidyTree.AssignXColumns` then places columns left to right, a body's moons beside it
before the bus branch.[^dvmap-welllayout]

**Spring.** `SpringLayout` is Fruchterman-Reingold: initial positions on a golden-angle spiral
(`radius = k * sqrt(i + 1)`, no randomness, so it is deterministic and never jitters), root pinned
at the origin, repulsion through a pooled Barnes-Hut quadtree (theta 0.9), attraction
`dist^2 / ideal` with rest lengths of 0.55 k (ladder), 0.7 k (hub link) and 1.35 k (transfer),
a linearly cooling temperature cap on each move, and an iteration count scaled to
`min(460, max(60, 120000 / n))` so a dense system cannot freeze the draw thread.[^dvmap-springlayout]

**Grid snap and routing.** The grid cell is larger than the two largest dot radii combined
(`Validate()` enforces it), so unique cells mean no overlapping dots; collisions are resolved in
(row, col, id) order by searching rings of growing Chebyshev radius, preferring the same row.[^dvmap-gridsnap]
Each edge becomes horizontal run, a 45-degree diagonal of at most 96 px, then a vertical drop into
the child, so every node sits on a clean vertical lane; coincident points collapse, making hub
links straight horizontals.[^dvmap-edgerouter]

**Switching.** `SetLayoutMode` is a no-op for the active mode, otherwise it sets `_layoutMode` and
calls `RebuildAt(_currentRootId)`, the same cached relayout path the full-ladder toggle uses
(graph and transfer cache are reused, only the visual tree and layout are redone). The toggle
icon is an `InvisibleButton` whose glyph is drawn with draw-list primitives (a branching tree, a
spine with wells, a hub with spokes), so it needs no icon font.[^dvmap-mapwindow]

## Lifecycle and timing

- Hook: `[StarMapAfterGui]`, main thread, `frame:draw-ui`, inside `MapWindow.RebuildAt` and
  `ComposeScene`. Layout runs only on a rebuild (re-root, system change, detail, visibility or
  layout toggle, search reveal), never per frame; the per-frame cost is drawing.
- `MeasureText` calls `ImGui.CalcTextSize`, which needs the current ImGui context and font, so the
  layout must run inside the ImGui frame. The mod's comment in `RebuildAt` names this as the reason
  the build is not moved to a background thread; the never-hang guard (8,000 nodes) and the spring
  iteration scaling bound its cost instead.[^imgui-binding]
- The engine reads no game state at all; everything game-derived arrives through the adapter.

## Key code

| Path | Role |
|---|---|
| [DeltaVMap/Layout/LayoutEngine.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/LayoutEngine.cs) | Pipeline and measurement |
| [DeltaVMap/Layout/LayoutConfig.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/LayoutConfig.cs) | `LayoutMode`, tunables, invariants |
| [DeltaVMap/Layout/BandLayout.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/BandLayout.cs) | Cumulative-dV bands |
| [DeltaVMap/Layout/TidyTree.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/TidyTree.cs) | Variable-width Walker tidy tree, bus packing, well columns |
| [DeltaVMap/Layout/WellLayout.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/WellLayout.cs) | Signed well offsets on the spine |
| [DeltaVMap/Layout/SpringLayout.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/SpringLayout.cs) | Deterministic Fruchterman-Reingold with Barnes-Hut |
| [DeltaVMap/Layout/GridSnap.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/GridSnap.cs) | Unique grid cells |
| [DeltaVMap/Layout/EdgeRouter.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/EdgeRouter.cs) | Octilinear polylines |
| [DeltaVMap/Layout/LabelPlacer.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/LabelPlacer.cs) | Greedy label slots with a spatial hash |
| [DeltaVMap/Layout/VisualTreeAdapter.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/VisualTreeAdapter.cs) | The only bridge from the game-typed tree |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L1176-L1297) | Layout toggle button, icon glyphs, mode cycling |

## Snippets

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
// ...
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

```csharp
// DeltaVMap/Layout/SpringLayout.cs L86-L134
for (int step = 0; step < iterations; step++)
{
    Array.Clear(dispX);
    Array.Clear(dispY);
    // Repulsion via a Barnes-Hut quadtree: a distant cluster of nodes is summed as one
    // body, so each node gathers force from O(log n) cells instead of all n. This is
    // what scales the sim to thousands of nodes; the all-pairs version froze the game.
    tree2d.Build(px, py, n);
    for (int i = 0; i < n; i++)
    {
        tree2d.Repulsion(i, px[i], py[i], k, out double rfx, out double rfy);
        dispX[i] += rfx;
        dispY[i] += rfy;
    }
    // Attraction along edges: distance^2 / ideal, pulling endpoints together.
    foreach ((int a, int b, double ideal) in edges)
    {
        double ex = px[b] - px[a];
        double ey = py[b] - py[a];
        double dist = Math.Sqrt(ex * ex + ey * ey);
        if (dist < 1e-4)
            continue;
        double force = dist * dist / ideal;
        double ux = ex / dist;
        double uy = ey / dist;
        dispX[a] += ux * force;
        dispY[a] += uy * force;
        dispX[b] -= ux * force;
        dispY[b] -= uy * force;
    }
    // ...
    temperature = Math.Max(1.0, temperature - cooling);
}
```

```csharp
// DeltaVMap/Render/MapWindow.cs L2267-L2272
// Real label width from the active ImGui font. Valid because every RebuildAt call
// happens inside the draw loop (DrawContent, the menu bar, or a canvas click).
private static double MeasureText(string label)
{
    return ImGui.CalcTextSize(label).X;
}
```

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `ImGui.CalcTextSize(ImString, bool, float)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs) | Real label widths with the active font |

No KSA game types are used by the engine itself; that is the point of the design.

## Pitfalls and version notes

- Text metrics depend on the current font and interface scale; measure inside the frame and
  relayout when the font could have changed, or labels will collide.
- Feeding a zero-dV hub chain straight into a tidy tree stacks it at one X. Detect the bus and
  pack its blocks horizontally, as `TidyTree.AssignXCumulative` does.
- A force-directed layout run on the draw thread must be bounded: Barnes-Hut plus an iteration
  budget scaled by node count, and a hard node ceiling outside the engine.
- Determinism matters for a map that is rebuilt on toggles: fixed initial spiral, stable sort
  keys (row, col, ordinal Id) and no randomness, so the same tree always produces the same picture.

## See also

- [Subway-map canvas rendering](./subway-map-canvas-rendering.md)
- [Multi-system scene composition](./multi-system-scene-composition.md)
- [Offline layout and math tests](./offline-layout-and-math-tests.md)
- [Ego re-rooting and vessel follow](./ego-rerooting-and-vessel-follow.md)
- [Lay out a tree in three modes with a game-free pipeline, Barnes-Hut and an iteration budget](../../../howto/ui/draw-lists/lay-out-a-tree-or-graph-diagram/lay-out-a-tree-or-graph-diagram-1.md) - how-to article demonstrating this feature

[^dvmap-layoutengine]: DeltaVMap/Layout/LayoutEngine.cs
[^dvmap-layoutconfig]: DeltaVMap/Layout/LayoutConfig.cs
[^dvmap-bandlayout]: DeltaVMap/Layout/BandLayout.cs
[^dvmap-tidytree]: DeltaVMap/Layout/TidyTree.cs
[^dvmap-welllayout]: DeltaVMap/Layout/WellLayout.cs
[^dvmap-springlayout]: DeltaVMap/Layout/SpringLayout.cs
[^dvmap-gridsnap]: DeltaVMap/Layout/GridSnap.cs
[^dvmap-edgerouter]: DeltaVMap/Layout/EdgeRouter.cs
[^dvmap-layoutmodel]: DeltaVMap/Layout/LayoutModel.cs
[^dvmap-visualtreeadapter]: DeltaVMap/Layout/VisualTreeAdapter.cs
[^dvmap-mapwindow]: DeltaVMap/Render/MapWindow.cs
[^imgui-binding]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
