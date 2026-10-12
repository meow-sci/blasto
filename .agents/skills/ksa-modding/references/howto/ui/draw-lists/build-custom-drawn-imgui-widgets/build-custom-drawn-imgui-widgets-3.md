---
type: How-To
title: Share one glyph function between a canvas and its legend
description: Draw state symbols as static draw-list methods that take only a screen point, a radius and two colours, and call the same methods from the map and from a legend of fixed-size Dummy cells
category: ui
group: draw-lists
topic: build-custom-drawn-imgui-widgets
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-deltavmap/features/subway-map-canvas-rendering]
techniques: [imgui-drawlist, imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImDrawListPtr, Brutal.ImGuiApi.ImDrawListExtensions]
tags: [glyphs, symbols, legend, canvas, draw-list, screen-fixed]
---
KSA-DeltaVMap draws a KSP-style symbol vocabulary on its map (a ring with a dot for low orbit, a reticle for an intercept, a star with rays, a rounded square for a collapsed star system) and the same symbols in a legend panel. Both are produced by one static class, `NodeGlyphs`, whose methods take only a screen point, a radius and two colours, so the legend cannot drift from the map. The symbols are a fixed screen size regardless of zoom. Demonstrated by [Pannable, zoomable ImGui canvas that draws the subway map with draw-list primitives](../../../../mods/ksa-deltavmap/features/subway-map-canvas-rendering.md). Prerequisite: an ImGui window with a canvas (see [Build pannable, zoomable and 3D canvases inside an ImGui window](../../../../howto/ui/draw-lists/build-a-pan-zoom-canvas-in-imgui/overview.md) for pan and zoom).

## Background: how the game does it

No game types are involved. The glyphs use `ImDrawListExtensions.AddCircle`, `AddCircleFilled`, `AddLine`, `AddRect`, `AddRectFilled` ([current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L51-L181)[^brutal-imguiapi-imdrawlistextensions]) and, for the legend cells, `ImGui.Dummy` and `SameLine`. Colours are `byte4` values, which convert to the packed colour the draw list expects (the mod passes them straight in). The game's own map markers are also plain draw-list shapes (`ImGuiHelper.DrawMarkerDot`, [current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L537-L540)[^ksa-imguihelper]), so this style matches the engine's.

## Lifecycle and timing

Both the canvas and the legend are drawn each frame the window is shown, from the window's `OnDrawUi` through `[StarMapAfterGui]` (`frame:draw-ui`); nothing is cached across frames because a glyph is a handful of primitives. The glyph methods read no state, so they can be called from any code that has an `ImDrawListPtr` during the UI pass. See [Game loop and mod lifecycle](../../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Define one method per state, with a uniform signature** `(ImDrawListPtr dl, float2 p, float r, byte4 fill, byte4 stroke)`. Nothing knows about layout trees or game types; fill is the body's system colour and stroke a lightened accent:

```csharp
// DeltaVMap/Render/NodeGlyphs.cs L14-L66
internal static class NodeGlyphs
{
    // The plane-change number's color, warm so it never reads as one of the grey dV badges
    // sitting near it. Shared here so the map marker and the legend sample stay in lockstep.
    internal static readonly byte4 PlaneChangeColor = new byte4(232, 178, 96, 255);

    // A landed body: a solid filled disc with a thin outline.
    public static void Surface(ImDrawListPtr dl, float2 p, float r, byte4 fill, byte4 stroke)
    {
        dl.AddCircleFilled(in p, r, fill);
        dl.AddCircle(in p, r, stroke, 24, 1.5f);
    }

    // Low orbit: a thin ring with a small filled center dot (KSP "Low Orbit").
    public static void LowOrbit(ImDrawListPtr dl, float2 p, float r, byte4 fill, byte4 stroke)
    {
        dl.AddCircle(in p, r, stroke, 24, 2f);
        dl.AddCircleFilled(in p, CenterDot(r), fill);
    }

    // Synchronous orbit: the low-orbit glyph plus a short accent tick at the top, so the
    // labelled stationary node reads as a notch above low orbit rather than identical to it
    // (KSP draws it as a labelled higher orbit, "Keostationary Orbit").
    public static void Stationary(ImDrawListPtr dl, float2 p, float r, byte4 fill, byte4 stroke)
    {
        dl.AddCircle(in p, r, stroke, 24, 2f);
        dl.AddCircleFilled(in p, CenterDot(r), fill);
        var inner = new float2(p.X, p.Y - r);
        var outer = new float2(p.X, p.Y - r - 4f);
        dl.AddLine(in inner, in outer, stroke, 2f);
        dl.AddCircleFilled(in inner, 2f, stroke);
    }

    // Elliptical orbit to the SOI edge: an outer SOI ring with a small inner ellipse, the
    // loose capture / park ellipse it represents (KSP "Elliptical Orbit to SOI Edge").
    public static void SoiEdge(ImDrawListPtr dl, float2 p, float r, byte4 fill, byte4 stroke)
    {
        dl.AddCircle(in p, r, stroke, 24, 2f);
        Ellipse(dl, p, r * 0.52f, r * 0.34f, fill, 1.6f);
    }

    // Intercept: a flyby target. A thin ring, a center dot, and four short crosshair ticks
    // just outside the ring, so it reads as a reticle and not a plain low-orbit node (KSP
    // "Intercept").
    public static void Intercept(ImDrawListPtr dl, float2 p, float r, byte4 fill, byte4 stroke)
    {
        dl.AddCircle(in p, r, stroke, 24, 1.6f);
        dl.AddCircleFilled(in p, CenterDot(r), fill);
        Tick(dl, p, 0f, -1f, r + 1f, r + 4f, stroke);
        Tick(dl, p, 0f, 1f, r + 1f, r + 4f, stroke);
        Tick(dl, p, -1f, 0f, r + 1f, r + 4f, stroke);
        Tick(dl, p, 1f, 0f, r + 1f, r + 4f, stroke);
    }
```

2. **Compose bigger symbols from smaller ones.** A star is a filled core with eight rays, a barycenter a ring with a cross and two dots, and a collapsed star system a rounded square with a star or barycenter glyph inside it:

```csharp
// DeltaVMap/Render/NodeGlyphs.cs L76-L127
// A star, drawn in a universe with several star systems: a filled disc with four long and
// four short rays, so a star reads apart from a planet and from a barycenter.
public static void Star(ImDrawListPtr dl, float2 p, float r, byte4 fill, byte4 stroke)
{
    float core = r * 0.62f;
    for (int i = 0; i < 8; i++)
    {
        float angle = i * (MathF.PI / 4f);
        float outer = i % 2 == 0 ? r : r * 0.82f;
        float dx = MathF.Cos(angle);
        float dy = MathF.Sin(angle);
        var a = new float2(p.X + dx * core * 0.9f, p.Y + dy * core * 0.9f);
        var b = new float2(p.X + dx * outer, p.Y + dy * outer);
        dl.AddLine(in a, in b, stroke, i % 2 == 0 ? 2f : 1.4f);
    }
    dl.AddCircleFilled(in p, core, fill);
    dl.AddCircle(in p, core, stroke, 20, 1.5f);
}

// A barycenter, the empty center of mass of a multiple star system: a thin ring with a
// center cross and two small dots on opposite sides, its stars.
public static void Barycenter(ImDrawListPtr dl, float2 p, float r, byte4 fill, byte4 stroke)
{
    dl.AddCircle(in p, r, stroke, 24, 1.4f);
    float arm = r * 0.35f;
    var h0 = new float2(p.X - arm, p.Y);
    var h1 = new float2(p.X + arm, p.Y);
    var v0 = new float2(p.X, p.Y - arm);
    var v1 = new float2(p.X, p.Y + arm);
    dl.AddLine(in h0, in h1, stroke, 1.5f);
    dl.AddLine(in v0, in v1, stroke, 1.5f);
    float dot = Math.Max(2f, r * 0.2f);
    var left = new float2(p.X - r * 0.68f, p.Y + r * 0.2f);
    var right = new float2(p.X + r * 0.68f, p.Y - r * 0.2f);
    dl.AddCircleFilled(in left, dot, fill);
    dl.AddCircleFilled(in right, dot, fill);
}

// A whole star system drawn as one node: a rounded square of half-size r, the square the
// stock map view marks another system's root with (IIndependentRoot.DrawSystemLabel), with
// the star or barycenter glyph inside.
public static void SystemStub(ImDrawListPtr dl, float2 p, float r, byte4 fill, byte4 stroke, bool barycenter)
{
    var min = new float2(p.X - r, p.Y - r);
    var max = new float2(p.X + r, p.Y + r);
    dl.AddRectFilled(in min, in max, StubBackground, 4f);
    dl.AddRect(in min, in max, stroke, 4f);
    if (barycenter)
        Barycenter(dl, p, r * 0.6f, fill, stroke);
    else
        Star(dl, p, r * 0.6f, fill, stroke);
}
```

3. **Draw the symbol that matches the node's kind on the canvas**, with state rings (root, you-are-here, focus, hover) layered around it:

```csharp
// DeltaVMap/Render/CanvasRenderer.cs L546-L574
private static void DrawSymbol(ImDrawListPtr dl, LayoutNode node, float2 p, float r, byte4 fill, byte4 stroke)
{
    switch (node.Kind)
    {
        case LayoutKind.Surface:
            NodeGlyphs.Surface(dl, p, r, fill, stroke);
            break;
        case LayoutKind.LowOrbit:
            NodeGlyphs.LowOrbit(dl, p, r, fill, stroke);
            break;
        case LayoutKind.Stationary:
            NodeGlyphs.Stationary(dl, p, r, fill, stroke);
            break;
        case LayoutKind.SoiEdge:
            NodeGlyphs.SoiEdge(dl, p, r, fill, stroke);
            break;
        case LayoutKind.Intercept:
            NodeGlyphs.Intercept(dl, p, r, fill, stroke);
            break;
        case LayoutKind.Hub:
            if (node.IsSystemStub)
                NodeGlyphs.SystemStub(dl, p, r, fill, stroke, node.HubRole == HubRole.Barycenter);
            else if (node.HubRole == HubRole.Star)
                NodeGlyphs.Star(dl, p, r, fill, stroke);
            else if (node.HubRole == HubRole.Barycenter)
                NodeGlyphs.Barycenter(dl, p, r, fill, stroke);
            else
                NodeGlyphs.Hub(dl, p, r, fill, stroke);
            break;
```

4. **Draw the legend from the same methods.** A `Row` helper takes a label and a lambda. It draws the glyph centred in a fixed cell, reserves the cell with `Dummy`, then puts the label beside it with `SameLine`:

```csharp
// DeltaVMap/Render/LegendRenderer.cs L79-L89
// Draw a glyph centered in a fixed cell, then its label vertically centered beside it.
private static void Row(string label, Action<ImDrawListPtr, float2> drawGlyph)
{
    ImDrawListPtr dl = ImGui.GetWindowDrawList();
    float2 cur = ImGui.GetCursorScreenPos();
    var center = new float2(cur.X + CellW * 0.5f, cur.Y + CellH * 0.5f);
    drawGlyph(dl, center);
    ImGui.Dummy(new float2(CellW, CellH));
    ImGui.SameLine(0f, 6f);
    LabelCentered(cur.Y, label);
}
```

```csharp
// DeltaVMap/Render/LegendRenderer.cs L39-L52
ImGui.SeparatorText("Legend"u8);

Row("Low orbit", static (dl, c) => NodeGlyphs.LowOrbit(dl, c, R, Fill, Stroke));
Row("Stationary orbit", static (dl, c) => NodeGlyphs.Stationary(dl, c, R, Fill, Stroke));
Row("Elliptical to SOI edge", static (dl, c) => NodeGlyphs.SoiEdge(dl, c, R, Fill, Stroke));
Row("Intercept (flyby)", static (dl, c) => NodeGlyphs.Intercept(dl, c, R, Fill, Stroke));
Row("Surface (landed)", static (dl, c) => NodeGlyphs.Surface(dl, c, R, Fill, Stroke));
Row("Minor-body group (+N)", static (dl, c) => NodeGlyphs.MinorGroup(dl, c, R, Fill, Stroke));
Row("Jet engine possible", static (dl, c) =>
{
    NodeGlyphs.LowOrbit(dl, c, 5f, Fill, Stroke);
    NodeGlyphs.AtmosphereHalo(dl, c, 5f, Marker);
});
Row("Aerobraking possible", static (dl, c) => NodeGlyphs.AerobrakeTriangle(dl, c, 1f, 0f, 8f, Marker));
```

5. **Keep glyph size independent of zoom.** The renderer asks for the glyph radius in pixels, not layout units (a collapsed star system is simply a larger square), and the canvas transform scales only positions, so the legend can draw the same symbols at a fixed pixel radius (`R = 7f`):

```csharp
// DeltaVMap/Layout/SceneComposer.cs L278-L282
// The screen radius a node's glyph is drawn at. A collapsed system is a larger square.
public static double GlyphRadiusPx(LayoutNode node)
{
    return node.IsSystemStub ? StubHalfPx : node.DotRadius;
}
```

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Render/NodeGlyphs.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/NodeGlyphs.cs) | The glyph vocabulary and the clipped dashed line |
| [DeltaVMap/Render/CanvasRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/CanvasRenderer.cs#L495-L575) | Node pass using the glyphs |
| [DeltaVMap/Render/LegendRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/LegendRenderer.cs) | Legend built from the same methods |

## Variations and gotchas

- **Draw order matters for halos.** The root halo is drawn before the dot so it sits underneath; rings drawn after the glyph sit on top.
- **Legend cell vs. glyph size.** The reserved cell (`CellW`, `CellH`) must exceed the glyph plus its decorations; a wider sample (the plane-change number) grows its own cell.
- **No textures means no atlas to rebuild.** Shapes survive a font atlas rebuild (see [Load a custom TTF font into KSA's ImGui atlas and keep it across font rebuilds](../../../../howto/ui/draw-lists/load-a-custom-font-into-imgui/overview.md)), unlike symbol glyphs from a custom font.
- **Hit testing is separate.** Glyphs only paint; the map compares screen positions with the mouse (see [pannable canvases](../build-a-pan-zoom-canvas-in-imgui/overview.md)).
- **Version.** Brutal ImGui calls only.

## See also

- [Overview of custom-drawn widgets](overview.md)
- [Approach 2: icon buttons](build-custom-drawn-imgui-widgets-2.md)
- [Build pannable, zoomable and 3D canvases inside an ImGui window](../../../../howto/ui/draw-lists/build-a-pan-zoom-canvas-in-imgui/overview.md)
- Mod feature: [Pannable, zoomable ImGui canvas that draws the subway map with draw-list primitives](../../../../mods/ksa-deltavmap/features/subway-map-canvas-rendering.md)

[^brutal-imguiapi-imdrawlistextensions]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
[^ksa-imguihelper]: KSA.ImGuiHelper (decompiled, build 2026.10.10.5554)
