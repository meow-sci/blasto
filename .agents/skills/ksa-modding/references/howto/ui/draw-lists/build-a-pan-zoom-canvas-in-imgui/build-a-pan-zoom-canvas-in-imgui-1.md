---
type: How-To
title: "Pan and zoom a 2D canvas: InvisibleButton, wheel key ownership, clip rect and priority label culling"
description: Build a clipped canvas that pans by dragging, zooms about the cursor, hit-tests nodes by screen distance, keeps glyphs and text a fixed screen size, and culls overlapping labels greedily by priority
category: ui
group: draw-lists
topic: build-a-pan-zoom-canvas-in-imgui
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksa-deltavmap/features/subway-map-canvas-rendering]
techniques: [imgui-drawlist, imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImDrawListPtr
  - Brutal.ImGuiApi.ImDrawListExtensions
  - KSA.Program
tags: [canvas, pan, zoom, wheel, hit-testing, label-culling, clip-rect]
---
KSA-DeltaVMap's map is a custom canvas inside an ordinary ImGui window: drag to pan, wheel to zoom about the cursor, hover a node or a line for a tooltip, click to select, Shift+click to re-root. The mechanism is small: a transform from layout space to screen space, a clip rect on the window draw list, and an `InvisibleButton` the size of the canvas that captures the mouse. This article walks that, then the label-culling pass that keeps text legible because glyphs and text stay a fixed pixel size while positions scale with zoom. Demonstrated by [Pannable, zoomable ImGui canvas that draws the subway map with draw-list primitives](../../../../mods/ksa-deltavmap/features/subway-map-canvas-rendering.md). Prerequisite: an ImGui window (see [Build a mod window on the game's ImGuiWindow base class, with stable ids and saved placement](../../../../howto/ui/windows/subclass-the-stock-imguiwindow/overview.md) or [Organise a mod window: tabs, button tabs, tables and pop-out sections](../../../../howto/ui/windows/build-a-tabbed-imgui-window/overview.md)).

## Background: how the game does it

All of this is plain Brutal ImGui. The calls that carry the design are `ImGui.InvisibleButton` ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L6255-L6262)[^brutal-imguiapi-imgui]), `SetNextItemAllowOverlap` ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7938-L7941)[^brutal-imguiapi-imgui]) and `SetItemKeyOwner` ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8186-L8189)[^brutal-imguiapi-imgui]), the draw list's `PushClipRect` ([current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L10-L19)[^brutal-imguiapi-imdrawlistextensions]) and `ImGuiIOPtr.MouseWheel` ([current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs#L172)[^brutal-imguiapi-imguiioptr]). The game's own orbit map does its projection with `Camera.EclToScreen` ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L340)[^ksa-camera]); DeltaVMap's canvas is 2D layout space, so it needs no camera.

## Lifecycle and timing

The canvas is drawn each frame the window is shown, through `ImGuiWindow.OnDrawUi` from `[StarMapAfterGui]`: `frame:draw-ui`. Input is read from `ImGui.GetIO()` in the same frame; `SetItemKeyOwner` claims the wheel for the canvas item so other ImGui consumers do not see it. The draw list becomes part of ImGui's draw data in `ImGui.Render()` and is recorded in the final ImGui pass. With the HUD hidden the window is not drawn. See [Game loop and mod lifecycle](../../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Define a transform with no inverse.** Positions scale with zoom, glyph radii do not. Hit testing compares screen positions, so no inverse is needed:

```csharp
// DeltaVMap/Render/CanvasRenderer.cs L17-L42
internal readonly struct CanvasTransform
{
    public readonly float2 Origin;
    public readonly double Zoom;
    public readonly double PanX;
    public readonly double PanY;
    public readonly double MinX;
    public readonly double MinY;

    public CanvasTransform(float2 origin, double zoom, double panX, double panY, double minX, double minY)
    {
        Origin = origin;
        Zoom = zoom;
        PanX = panX;
        PanY = panY;
        MinX = minX;
        MinY = minY;
    }

    public float2 ToScreen(double lx, double ly)
    {
        double sx = Origin.X + (lx - MinX) * Zoom + PanX;
        double sy = Origin.Y + (ly - MinY) * Zoom + PanY;
        return new float2((float)sx, (float)sy);
    }
}
```

2. **Take the region and draw the background.** Origin and size come from the cursor and the available content region; bail out if the canvas is tiny:

```csharp
// DeltaVMap/Render/MapWindow.cs L1104-L1120
ImDrawListPtr dl = ImGui.GetWindowDrawList();
float2 origin = ImGui.GetCursorScreenPos();
float2 size = ImGui.GetContentRegionAvail();
_lastSize = size;
if (size.X < 8f || size.Y < 8f)
    return;

float2 canvasMax = origin + size;
dl.AddRectFilled(in origin, in canvasMax, CanvasRenderer.CanvasBackground);

if (_needsFit)
{
    FitToView(size);
    _needsFit = false;
}

var transform = new CanvasTransform(origin, _zoom, _panX, _panY, scene.MinX, scene.MinY);
```

3. **Clip, draw, and always pop.** The `try`/`finally` keeps the clip stack balanced if drawing throws:

```csharp
// DeltaVMap/Render/MapWindow.cs L1128-L1138
dl.PushClipRect(in origin, in canvasMax, intersectWithCurrentClipRect: true);
try
{
    _canvas.Draw(dl, scene, _lookup!, _palette!, in transform, origin, canvasMax, _hoverId, _focusNodeId, _routeNodeIds,
        _options.IncludePlaneChange, _view.DvScale, _view.ShowTransferTimes, _view.ShowBodyMarkers, in windowMarkers, connectorBadge);
}
finally
{
    // Always pop, even on an unexpected throw, so the clip stack stays balanced.
    dl.PopClipRect();
}
```

4. **Capture input with a full-canvas `InvisibleButton`.** It must exist so a left-drag pans the map instead of moving the window (only the title bar then moves it). `SetNextItemAllowOverlap` lets overlay widgets submitted just after it (a layout switcher, a panel) take their own clicks. While hovered, claim the wheel:

```csharp
// DeltaVMap/Render/MapWindow.cs L1140-L1150, L1156-L1163
// An invisible button over the whole canvas captures the left button, so a
// left-drag pans the map instead of moving the window (only the title bar
// moves it). Its return value is a click without a drag, used for selection.
// AllowOverlap lets the layout switcher (submitted just after, on top) take its
// own clicks instead of the canvas swallowing them.
ImGui.SetNextItemAllowOverlap();
bool clicked = ImGui.InvisibleButton("##dvmap_canvas"u8, in size);
bool hovered = ImGui.IsItemHovered();
bool active = ImGui.IsItemActive();
if (hovered)
    ImGui.SetItemKeyOwner(ImGuiKey.MouseWheelY);
// ...
bool overOverlay = DrawLayoutOverlay(origin);

// The Transfer-windows overlay floats bottom-left and likewise claims the input under it,
// so neither a click on its rows nor a drag over it leaks through to a node or a pan.
bool overWindows = DrawTransferWindowOverlay(origin, size);

bool overAny = overOverlay || overWindows;
HandleInput(hovered && !overAny, active, clicked && !overAny, origin, in transform);
```

5. **Zoom about the cursor.** Keep the layout point under the cursor fixed on screen by adjusting the pan whenever the zoom changes; the wheel step is `1.1^wheel`:

```csharp
// DeltaVMap/Render/MapWindow.cs L2306-L2316
// Zoom keeping the layout point currently under (relX, relY) - canvas-relative
// pixels, i.e. excluding the origin - fixed on screen.
private void ZoomAbout(double factor, double relX, double relY)
{
    double newZoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
    if (newZoom == _zoom)
        return;
    _panX = relX - (relX - _panX) / _zoom * newZoom;
    _panY = relY - (relY - _panY) / _zoom * newZoom;
    _zoom = newZoom;
}
```

6. **Pan, wheel, hover, click.** The wheel is consumed by zeroing `io.MouseWheel`, left-drag pans while the button is active and dragging, the middle button does not activate the item so it is latched from the press until release, and a click without a drag returns true from `InvisibleButton`, which is what lets click-to-select and drag-to-pan coexist:

```csharp
// DeltaVMap/Render/MapWindow.cs L1299-L1329
private void HandleInput(bool hovered, bool active, bool clicked, float2 origin, in CanvasTransform transform)
{
    ImGuiIOPtr io = ImGui.GetIO();
    float2 mouse = ImGui.GetMousePos();

    // Mouse-wheel zoom, centered on the cursor so the point under it stays put.
    if (hovered)
    {
        float wheel = io.MouseWheel;
        if (wheel != 0f)
        {
            io.MouseWheel = 0f;
            ZoomAbout(Math.Pow(1.1, wheel), mouse.X - origin.X, mouse.Y - origin.Y);
        }
    }

    // Pan with a left-drag (the invisible button is active and dragging) or a
    // middle-drag. The middle button does not activate the button, so latch it
    // from the press on the canvas until release.
    bool leftPan = active && ImGui.IsMouseDragging(ImGuiMouseButton.Left);
    if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
        _middlePanning = true;
    if (!ImGui.IsMouseDown(ImGuiMouseButton.Middle))
        _middlePanning = false;
    bool middlePan = _middlePanning && ImGui.IsMouseDragging(ImGuiMouseButton.Middle);
    if (leftPan || middlePan)
    {
        float2 delta = io.MouseDelta;
        _panX += delta.X;
        _panY += delta.Y;
    }
```

7. **Hit-test by screen distance.** The node under the cursor is the nearest whose glyph radius plus a margin (or a fixed hover radius, whichever is larger) contains the mouse; a large dot is clickable to its rim:

```csharp
// DeltaVMap/Render/MapWindow.cs L1542-L1572
// The node under the cursor: the nearest one whose dot, plus a small margin, or the fixed
// hover radius, whichever is larger, holds the cursor. A large dot (the root, a star-system
// stub) is therefore clickable all the way to its rim.
private LayoutNode? NearestNode(float2 mouse, in CanvasTransform transform)
{
    LayoutNode? best = null;
    double bestSq = double.MaxValue;
    IReadOnlyList<LayoutNode> nodes = _scene!.Nodes;
    for (int i = 0; i < nodes.Count; i++)
    {
        LayoutNode node = nodes[i];
        float2 p = transform.ToScreen(node.SnappedX, node.SnappedY);
        double dx = p.X - mouse.X;
        double dy = p.Y - mouse.Y;
        double sq = dx * dx + dy * dy;
        double reach = Math.Max(HoverRadiusPx, SceneComposer.GlyphRadiusPx(node) + 3.0);
        if (sq < bestSq && sq <= reach * reach)
        {
            bestSq = sq;
            best = node;
        }
    }
    // A system title stands for its root, so hovering or clicking it acts on the system.
    if (best == null)
    {
        int title = _canvas.TitleAt(mouse);
        if (title >= 0 && title < nodes.Count)
            best = nodes[title];
    }
    return best;
}
```

## Sub-step: cull overlapping labels by priority

Layout placed labels without overlap at 100 % zoom, but text stays a fixed screen size while positions scale, so zoomed out the labels would smear into each other. The renderer therefore re-decides every frame which labels draw. The recipe, run on screen rectangles measured with `ImGui.CalcTextSize`:

1. **Build a candidate per label** with its screen rectangle (the plate, not just the text, so neighbouring plates never touch) and a priority number. Lower wins: hovered 0, root and system roots 1, you-are-here 2, on-route 3, the "+N" group 4, major bodies 5, moons 6, the rest 7:

```csharp
// DeltaVMap/Layout/LabelLod.cs L80-L103
// Lower number = drawn first = wins the screen room. Names come before plain dV badges
// so the map reads as a labelled diagram first: hovered, root, you-are-here, on-route
// name; the selected route's dV; then every body name by rank; then dV badges by rank.
// Only the on-route dV outranks the names, since a chosen route wants both at once.
public static int Priority(LayoutNode node, string? hoverId, bool routing, IReadOnlySet<string>? routeNodes)
{
    if (node.Id == hoverId)
        return 0;
    if (node.IsRoot || node.IsSystemRoot)
        return 1;
    if (node.IsYouAreHere)
        return 2;
    if (routing && routeNodes!.Contains(node.Id))
        return 3;
    // A minor-body group's "+N" count is the headline of a dense overview, so it ranks
    // just under the route, ahead of every individual body name.
    if (node.Kind == LayoutKind.MinorGroup)
        return 4;
    if (IsMajor(node))
        return 5;
    if (node.Rank == 2)
        return 6;
    return 7;
}
```

2. **Sort by priority**, with a stable screen-position tiebreak so the survivor set does not flicker between frames at equal priority:

```csharp
// DeltaVMap/Render/CanvasRenderer.cs L219-L226
private static readonly Comparison<DrawItem> ByPriority = static (a, b) =>
{
    int byPriority = a.Priority.CompareTo(b.Priority);
    if (byPriority != 0)
        return byPriority;
    int byY = a.Rect.Y.CompareTo(b.Rect.Y);
    return byY != 0 ? byY : a.Rect.X.CompareTo(b.Rect.X);
};
```

3. **Draw greedily**, skipping any candidate whose rectangle overlaps one already drawn. Labels may sit on dots (they identify the dots) but badges and titles avoid them:

```csharp
// DeltaVMap/Render/CanvasRenderer.cs L838-L865
for (int i = 0; i < _items.Count; i++)
{
    DrawItem item = _items[i];
    if (item.Kind == ItemKind.Title)
    {
        PlaceTitle(dl, scene, in t, ref item, nodes[item.NodeIndex], spots);
        continue;
    }
    if (item.Kind == ItemKind.ConnectorBadge)
    {
        PlaceConnectorBadge(dl, in item, connectorBadge!.Value);
        continue;
    }
    if (_occupied.AnyOverlap(in item.Rect))
        continue;
    if (item.Kind != ItemKind.Label && _dots.AnyOverlap(in item.Rect))
        continue;
    _occupied.Insert(in item.Rect);
    switch (item.Kind)
    {
        case ItemKind.Label:
            DrawLabelItem(dl, in item, nodes[item.NodeIndex]);
            break;
        case ItemKind.Badge:
            DrawBadgeItem(dl, in item, _badges[item.NodeIndex]!);
            break;
    }
}
```

4. **Make the overlap test linear** with a flat screen-space grid of 48 px buckets whose entries live in shared arrays that are reused between frames, so a frame allocates nothing ([DeltaVMap/Render/CanvasRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/CanvasRenderer.cs#L1417-L1508)).

5. **Add zoom LOD on top.** Below a zoom floor each body collapses to one short label, moon names drop, and delta-v badges hide below `BadgeMinZoom` (0.45); special labels (hover, root, you-are-here, on-route) always draw in full ([DeltaVMap/Render/CanvasRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/CanvasRenderer.cs#L750-L752)).

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L1088-L1390) | `DrawCanvas`, layout toggle, `HandleInput` |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L1542-L1683) | `NearestNode`, `NearestEdge`, point-to-segment distance |
| [DeltaVMap/Render/CanvasRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/CanvasRenderer.cs) | `CanvasTransform`, five draw passes, label and badge culling |
| [DeltaVMap/Layout/LabelLod.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/LabelLod.cs) | Zoom LOD and draw priority |

## Variations and gotchas

- **Allow-overlap order.** Overlay widgets must be submitted after the canvas button; each reports whether the mouse is over it, and canvas input is suppressed underneath (`hovered && !overAny`).
- **Wheel, twice.** Claiming the key owner and zeroing `io.MouseWheel` are both used; if only one is applied, the game camera or the window may still respond.
- **Same-frame hover.** Hover state is computed from the mouse position in the same frame, so the tooltip never lags the drawn highlight.
- **Zoom limits.** Clamp zoom (`MinZoom`, `MaxZoom`) inside `ZoomAbout`; otherwise coordinates overflow float precision.
- **Draw cost.** Off-screen nodes are skipped in the node pass; label culling is bounded by candidate count, not by area.
- **Version.** Brutal ImGui only; the cull logic is pure managed code.

## See also

- [Overview of canvases](overview.md)
- [Approach 2: 3D orbit canvas](build-a-pan-zoom-canvas-in-imgui-2.md)
- [Draw per-frame text efficiently: monospace grids and allocation-free labels](../../../../howto/ui/draw-lists/draw-a-monospace-text-grid/overview.md)
- [Lay out a tree or graph diagram and keep it usable at scale](../../../../howto/ui/draw-lists/lay-out-a-tree-or-graph-diagram/overview.md)
- [Share the keyboard and mouse between ImGui, mod tools and the game](../../../../howto/ui/input/coordinate-imgui-focus-with-game-input/overview.md)
- Mod feature: [Pannable, zoomable ImGui canvas that draws the subway map with draw-list primitives](../../../../mods/ksa-deltavmap/features/subway-map-canvas-rendering.md)

[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imdrawlistextensions]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imguiioptr]: Brutal.ImGuiApi.ImGuiIOPtr (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
