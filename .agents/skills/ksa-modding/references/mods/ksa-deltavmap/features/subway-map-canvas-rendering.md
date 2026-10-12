---
type: Mod Feature
title: Pannable, zoomable ImGui canvas that draws the subway map with draw-list primitives
description: Draws the laid-out map every frame on the window draw list inside a clipped child region, with a full-canvas InvisibleButton that owns drag and wheel, screen-fixed KSP-style node glyphs shared with the legend, per-system colors, priority-ordered label culling and allocation-free cached text
mod: ksa-deltavmap
category: ui
techniques: [imgui-drawlist, imgui-window, screen-space-culling, cached-text]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, render:imgui]
game_types: [KSA.ConsoleWidgets, KSA.ConsoleStyle]
tags: [ui, imgui, draw-list, canvas, pan-zoom, hit-testing, legend, tooltips, allocation-free]
---

The map itself is a custom canvas inside an ordinary ImGui window: drag to pan, wheel to zoom
about the cursor, hover a node or a line for a tooltip, click to plan a route, Shift+click to
re-root. Lines, KSP-style node symbols, delta-v badges, labels, system titles and the legend are
all drawn with draw-list primitives (no textures, no icon font). Mechanism: `MapWindow.DrawCanvas`
fills a clipped child region on `ImGui.GetWindowDrawList()`, `CanvasRenderer` turns the
`LayoutScene` into geometry through a `CanvasTransform` in which positions scale with zoom but
glyphs, text and line widths stay a fixed screen size, and an `InvisibleButton` the size of the
canvas captures the mouse.[^dvmap-mapwindow][^dvmap-canvasrenderer]

## How it works

**Layout of the window.** `DrawContent` splits the content region into a canvas column and a
right-hand panel column (30% of the width, clamped to 320-470 px), each a `BeginChild` closed in
`finally`. Below 220 px of canvas width the panel is dropped.

**Canvas frame.** `DrawCanvas` (each frame):

1. `dl = ImGui.GetWindowDrawList()`, `origin = GetCursorScreenPos()`, `size =
   GetContentRegionAvail()`, background rect, optional `FitToView` (`SceneFit.Fit`).
2. `new CanvasTransform(origin, zoom, panX, panY, scene.MinX, scene.MinY)`: `screen = origin +
   (layout - min) * zoom + pan`. No inverse transform is needed because hit testing compares
   screen positions.
3. Refresh transfer-window countdowns once, build the window-marker set.
4. `dl.PushClipRect(origin, canvasMax, intersectWithCurrentClipRect: true)`, then
   `CanvasRenderer.Draw(...)`, then `PopClipRect()` in `finally`.
5. `ImGui.SetNextItemAllowOverlap()` and `ImGui.InvisibleButton("##dvmap_canvas", size)`; while
   hovered, `ImGui.SetItemKeyOwner(ImGuiKey.MouseWheelY)` so the wheel zooms the map and not the
   window or the game camera.
6. Overlay widgets (layout toggle, transfer-window panels) are submitted after the button, so with
   allow-overlap they take their own clicks; each reports whether the mouse is over it and the
   canvas input is suppressed underneath.
7. `HandleInput`: wheel zoom `Math.Pow(1.1, wheel)` about the cursor (consumed by setting
   `io.MouseWheel = 0`), left-drag pan while the button is active and dragging, middle-drag pan
   latched from a press on the canvas, `NearestNode` / `NearestEdge` hover, click handling.
   A click with no drag returns true from `InvisibleButton`, which is what makes click-to-select
   and drag-to-pan coexist.

**Renderer passes** (`CanvasRenderer.Draw`): (1) edge polylines and dashed interstellar
connectors, off-route geometry faded to 20% and the selected route redrawn heavy and white on top;
(2) node dots with glyphs, root halo, you-are-here, focus (cyan double ring) and hover rings,
atmosphere halo and ring ellipse markers, off-screen nodes skipped; (3) edge markers (aerobrake
triangles, plane-change numbers); (4) labels and badges through a screen-space culling pass;
(5) transfer-window badges.

**Glyph vocabulary.** `NodeGlyphs` has one static method per state (`Surface`, `LowOrbit`,
`Stationary`, `SoiEdge`, `Intercept`, `Hub`, `Star`, `Barycenter`, `SystemStub`, `MinorGroup`,
`Solid`) plus markers (`AtmosphereHalo`, `RingEllipse`, `AerobrakeTriangle`, `ScaleBreak`) and
`DashedSegment`, a 7-on / 5-off dash that first clips the segment to the canvas (Liang-Barsky) and
keeps its dash phase anchored at the start point so dashes do not crawl while panning. Each glyph
takes only a screen point, a radius and two colors.[^dvmap-nodeglyphs] `LegendRenderer` calls the
very same methods into fixed-size cells reserved with `ImGui.Dummy`, so the legend cannot drift
from the map.[^dvmap-legend]

**Colors.** `ColorPalette.Build(graph)` assigns each body the hue of its "system anchor" (the
planet directly under the star, or the star / barycenter itself), so a planet and its moons read
as one metro line. A small table pins stock hues (Sol, Earth, Mars, ...); every other anchor
hashes its Id with FNV-1a into a stable hue. Moons are lighter, minor bodies desaturated.
Computed once per system, looked up per node once per scene.[^dvmap-colorpalette]

**Label culling.** Layout placed labels without overlap at 100% zoom, but text stays a fixed screen
size while positions scale, so below 100% labels would smear. Each frame the renderer builds
candidate rectangles for labels and badges, sorts them by `LabelLod.Priority` (hovered 0, root and
system roots 1, you-are-here 2, on-route 3, "+N" group 4, major bodies 5, moons 6, rest 7, then
screen position as a stable tiebreak), and draws greedily, skipping anything that overlaps text
already drawn (badges and titles also avoid foreign dots), using bucketed screen grids (48 px
cells). Below 50% zoom a body collapses to one short label and minor-body names drop; below 30%
moon names drop (root-context moons exempt); below 45% delta-v badges are hidden.[^dvmap-labellod]

**Allocation discipline.** Node styles and system titles are resolved once per scene; badge
strings and their `CalcTextSize` widths once per scene, piloting margin and transfer-time setting;
`TooltipRenderer` rebuilds its lines only when the hovered item or a display setting changes; panel
strings use `CachedText` (`IsStale(owner, key) ? Set(owner, key, Build()) : Text`). A held hover
or an idle map allocates nothing per frame.[^dvmap-cachedtext][^dvmap-tooltips]

**Panel widgets.** The route panel uses rows on the game's `ConsoleWidgets` (`BeginRow`,
`Checkbox`, `EndRow`) and a logarithmic `ImGui.SliderFloat` inside a console row for values that
span decades.[^dvmap-consoleui] Display-only options (`ShowTransferTimes`, `ShowBodyMarkers`,
`PilotingMarginPercent` 0-50, applied as `DvScale = 1 + percent / 100` at draw time) need no
rebuild; visibility options do.[^dvmap-viewoptions]

## Lifecycle and timing

- Hook: `[StarMapAfterGui]` (postfix on `Program.OnDrawUiViewports`), main thread,
  `frame:draw-ui`, through `ImGuiWindow.OnDrawUi` -> `DrawContent` -> `DrawCanvas`, every frame the
  window is shown and the HUD is visible.[^ksa-imguiwindow]
- The draw list commands become part of ImGui's draw data, which `Program.RenderGame` renders with
  `ImGuiBackend.Vulkan.RenderDrawData` in the final pass (`render:imgui`).[^ksa-program]
- Input is read from `ImGui.GetIO()` in the same frame; `ImGui.SetItemKeyOwner` claims the wheel
  for the canvas item so other consumers of `MouseWheelY` in ImGui do not see it.[^imgui-binding]
- No game state is read during drawing except through the already-built scene, the cached
  palette, and the transfer-window refresh.

## Key code

| Path | Role |
|---|---|
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L1088-L1390) | `DrawCanvas`, layout toggle, `HandleInput` |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L1542-L1683) | `NearestNode`, `NearestEdge`, point-to-segment distance |
| [DeltaVMap/Render/CanvasRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/CanvasRenderer.cs) | `CanvasTransform`, five draw passes, label and badge culling |
| [DeltaVMap/Render/NodeGlyphs.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/NodeGlyphs.cs) | Glyph vocabulary, clipped dashed lines |
| [DeltaVMap/Render/LegendRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/LegendRenderer.cs) | Controls help and legend from the same glyphs |
| [DeltaVMap/Render/ColorPalette.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/ColorPalette.cs) | Per-planetary-system hues |
| [DeltaVMap/Layout/LabelLod.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Layout/LabelLod.cs) | Zoom LOD and draw priority |
| [DeltaVMap/Render/TooltipRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/TooltipRenderer.cs) | Cached node, edge and connector tooltips |
| [DeltaVMap/Render/CachedText.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/CachedText.cs) | Text-until-the-value-changes helper |
| [DeltaVMap/Render/ConsoleUi.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/ConsoleUi.cs) | Console-widget rows, log slider |

## Snippets

```csharp
// DeltaVMap/Render/MapWindow.cs L1128-L1163
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

```csharp
// DeltaVMap/Render/NodeGlyphs.cs L27-L66
// Low orbit: a thin ring with a small filled center dot (KSP "Low Orbit").
public static void LowOrbit(ImDrawListPtr dl, float2 p, float r, byte4 fill, byte4 stroke)
{
    dl.AddCircle(in p, r, stroke, 24, 2f);
    dl.AddCircleFilled(in p, CenterDot(r), fill);
}
// ...
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

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `ImGui.InvisibleButton`, `SetNextItemAllowOverlap`, `SetItemKeyOwner`, `IsItemHovered`, `IsItemActive`, `IsMouseDragging`, `GetIO`, `GetWindowDrawList`, `CalcTextSize`, `BeginChild`, `Dummy` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs) | Canvas item, input ownership, text metrics |
| `ImDrawListPtr` extensions `PushClipRect`, `PopClipRect`, `AddLine`, `AddCircle`, `AddCircleFilled`, `AddRectFilled`, `AddTriangleFilled`, `AddText` | [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs) | All map geometry |
| `KSA.ConsoleWidgets.BeginRow` / `Checkbox` / `EndRow`, `KSA.ConsoleStyle` colors and `PushValueFont` | (KSA console UI kit) | Panel rows matching the stock console look |
| `KSA.ImGuiWindow.OnDrawUi` | [current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs) | Window Begin/End around `DrawContent` |

## Pitfalls and version notes

- Pair every `PushClipRect`, `BeginChild` and `PushStyleColor` with its pop in `finally`; a throw
  that skips a pop corrupts ImGui's stacks for every later frame.
- Without `SetNextItemAllowOverlap` on the big invisible button, widgets drawn on top of it never
  receive clicks; without `SetItemKeyOwner(MouseWheelY)` the wheel also scrolls whatever else
  listens to it.
- Keep glyph and text sizes in screen pixels and scale only positions; scaling text with zoom
  makes a dense map unreadable at the auto-fit zoom.
- Labels placed in layout space are only overlap-free at 100% zoom; a per-frame screen-space cull
  is needed at other zooms.
- The `ConsoleUi` doc comment says the stock `ImGuiWindow` pushes the console widget style around
  `DrawContent`. In build 2026.10.10.5554 `ConsoleStyle.PushWidgetStyle` is only pushed on the
  `UseConsoleChrome` path of `ImGuiWindow`, which `MapWindow` does not enable, so the rows run
  without that pushed style (visual impact not verified in game); see
  [panel rows built on the game's ConsoleWidgets kit](./stock-console-widget-panels.md).

## See also

- [Three layout modes](./three-layout-modes.md)
- [Multi-system scene composition](./multi-system-scene-composition.md)
- [Map window and menu entry](./map-window-and-menu-entry.md)
- [Search, focus, isolate and minor-body aggregation](./search-focus-isolate-and-aggregation.md)
- [Route finding and accumulation](./route-finding-and-accumulation.md)
- [Transfer windows and phase clock](./transfer-windows-and-phase-clock.md)
- [Panel rows built on the game's ConsoleWidgets kit](./stock-console-widget-panels.md)
- [Pan and zoom a 2D canvas: InvisibleButton, wheel key ownership, clip rect and priority label culling](../../../howto/ui/draw-lists/build-a-pan-zoom-canvas-in-imgui/build-a-pan-zoom-canvas-in-imgui-1.md) - how-to article demonstrating this feature
- [Share one glyph function between a canvas and its legend](../../../howto/ui/draw-lists/build-custom-drawn-imgui-widgets/build-custom-drawn-imgui-widgets-3.md) - how-to article demonstrating this feature
- [Cache a display string until the shown value changes](../../../howto/ui/draw-lists/draw-a-monospace-text-grid/draw-a-monospace-text-grid-2.md) - how-to article demonstrating this feature

[^dvmap-canvasrenderer]: DeltaVMap/Render/CanvasRenderer.cs
[^dvmap-nodeglyphs]: DeltaVMap/Render/NodeGlyphs.cs
[^dvmap-legend]: DeltaVMap/Render/LegendRenderer.cs
[^dvmap-colorpalette]: DeltaVMap/Render/ColorPalette.cs
[^dvmap-cachedtext]: DeltaVMap/Render/CachedText.cs
[^dvmap-tooltips]: DeltaVMap/Render/TooltipRenderer.cs
[^dvmap-labellod]: DeltaVMap/Layout/LabelLod.cs
[^dvmap-consoleui]: DeltaVMap/Render/ConsoleUi.cs
[^dvmap-viewoptions]: DeltaVMap/Render/ViewOptions.cs
[^dvmap-mapwindow]: DeltaVMap/Render/MapWindow.cs
[^imgui-binding]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^ksa-imguiwindow]: KSA.ImGuiWindow (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
