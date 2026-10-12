---
type: Mod Feature
title: World-space measurement overlay on the ImGui background draw list
description: Draws measurements, previews, snap markers and plated labels into the main viewport's background draw list from StarMapAfterGui, culls behind-camera points, and throttles the hover pick to 45 ms with a state-version check
mod: ksa-measuretools
category: ui
techniques: [imgui-drawlist, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, render:imgui]
game_types:
  - KSA.ImGuiHelper
  - KSA.Camera
  - KSA.IGameViewport
  - KSA.ViewportRegistry
  - KSA.DistanceReference
  - KSA.RadianReference
  - KSA.Cursor
tags: [overlay, imgui, draw-list, labels, culling, hover-preview, throttling, zero-alloc]
---

Every measurement, the in-progress rubber band, the snap highlight under the cursor and the
construction plane are drawn as 2D ImGui primitives positioned by projecting 3D points every frame.
Nothing is rendered through Vulkan by the mod; the game renders the draw list in its ImGui pass.[^mt-readme]

## How it works

### Where it draws

`MeasureOverlay.Draw(viewport)` gets its list from `ImGuiHelper.GetOverlayDrawList(viewport)`, which
for the main viewport returns `ImGui.GetBackgroundDrawList(ImGui.GetMainViewport())`, the list the
stock body labels and orbit overlays use. Background-list content sits under every ImGui window, so
the overlay never covers the tool window or stock panels.[^ksa-imguihelper] For any other viewport the
helper returns `ImGui.GetWindowDrawList()`, which is only meaningful inside that viewport's own ImGui
window; by the time AfterGui runs those windows are closed, so `MeasureViewport` restricts the tool to
the main viewport.[^mt-viewport] `MeasureViewport.TryGetActive` scans `ViewportRegistry.GameViews`
for `IsMain()` instead of reading `Program.MainViewport`, because
`ViewportRegistry.MainViewport` throws until the registry has one.[^ksa-viewportregistry]

The overlay draws only while the window is open, the view is Map or Orbit, and a system is loaded.
The whole draw is in a `try/catch` that logs each exception type once, so a projection problem can
never unwind into the game's UI pass.[^mt-overlay]

### Projection and culling

Every 3D point goes through `vpPos + camera.EclToScreen(posEcl)`. `Camera.EgoToScreen` returns NaN
for points behind the camera, and the overlay's `Valid` check (no NaN, no infinity) is the culling
rule for dots, lines and labels: a measurement whose endpoint is behind the camera is skipped, and
sampled curves (3D circles, the construction-plane disc, great-circle arcs) break their polyline at
invalid samples instead of drawing a wild segment.[^ksa-camera] Great-circle arcs additionally drop
samples on the far hemisphere of the planet. There is no label-overlap culling; labels get a dark
rounded plate (`AddRectFilled` behind `AddText`) to stay readable over orbit lines and other labels,
and stacked labels at one anchor are spaced by `ImGui.GetTextLineHeightWithSpacing() + 6` so they
never touch at any font size.

Labels are positioned at the segment midpoint, offset 16 px along the perpendicular that points up
the screen. The protractor arc is drawn in screen space between the two projected arm directions
(20 line segments, since the binding exposes no path-arc helper) with the true 3D angle as its label
along the bisector.

### Allocation-free labels

Numbers are formatted into `stackalloc char[64]` buffers with `DistanceReference.ToNearest(meters,
buffer)` or into a `Core.SpanBuilder` (from `Planet.Render.Core`, referenced only for this) and passed
as `ImString`, which copies span text into ImGui's arena. No per-frame strings are allocated for
labels.[^ksa-distancereference]

### Hover preview and throttling

While armed and the cursor is inside the viewport and not over ImGui, the overlay re-runs the picker
for the cursor and draws the snap marker: a ring and the body name for body centers, a filled dot
and ring for orbit points, the limb ring for surface snaps, a 4-segment "diamond" plus faint dots on
the hovered part's nodes and center for part points, a cross plus the construction plane for free
points. With a pending point it draws a rubber-band line and the live value (distance, arm length,
angle, great-circle distance, face angle, diameter).[^mt-overlay]

The pick is expensive (part mesh raycasts loop over every triangle), so it is cached:

- It re-picks when at least 45 ms of wall-clock time (`Stopwatch`) have passed, about 22 picks per
  second at any refresh rate. A frame-count throttle would scale with the refresh rate.
- It re-picks immediately when `MeasureState.StateVersion` changed (mode, toggles, a placed point)
  or when Ctrl was pressed or released.
- It drops the cache when the tool disarms, the cursor leaves the viewport, or ImGui wants the mouse.

The placement click never uses this cache; it always picks fresh.

### Highlight sync

Measurements draw at 3 px in the palette's line color; the one whose list row is hovered
(`MeasureState.HighlightIndex`, set by the window draw just before) draws at 5.25 px in the
highlight color.[^mt-state][^mt-window]

## Lifecycle and timing

`[StarMapAfterGui]` is StarMap's Harmony postfix on `Program.OnDrawUiViewports`.[^starmap-programpatcher]
`Mod.Draw` runs `MeasureState.Prune()`, then the window, then the overlay. At that point (inside
`frame:draw-ui`) `ImGui.NewFrame` has run, `Cursor` holds this frame's mouse position, the camera for
this frame was computed in `OnFrameViewports`, and the last applied physics results are what the
renderer draws this frame (the next step is already running on workers but writes back only at the
next `ApplyVehicleSolvers`), so projected points match the 3D scene. Two stock values are not yet
refreshed for this frame: `MainViewport.Position` is set from `ImGui.GetMainViewport().Pos` at the
start of `OnDrawUiThreadSafe`, after AfterGui, so `viewport.Position` is last frame's (it only differs
on the frame the OS window moves), and the stock hover state the preview can consult
(`ViewportPartPicker`, `Astronomical.CursorHoverEligible`) is filled in `Universe.OnDrawUi`, also after
AfterGui (see the part-picking and body-snapping concepts). The draw list is consumed in the game's ImGui render pass
(`render:imgui`) after `ImGui.Render`.[^ksa-program] With the HUD hidden (`Program.DrawUI == false`)
AfterGui does not run and the overlay disappears with the rest of the UI; a mod that must keep
drawing then would hook `OnDrawUiConsole` instead.

## Key code

| Path | Role |
|---|---|
| [MeasureTools/Features/Measure/MeasureOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureOverlay.cs#L35-L154) | Throttle state, entry point, per-mode drawing |
| [MeasureTools/Features/Measure/MeasureOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureOverlay.cs#L387-L590) | Hover preview and snap highlight |
| [MeasureTools/Features/Measure/MeasureOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureOverlay.cs#L702-L845) | Arc, labels, plates, validity |
| [MeasureTools/Features/Measure/MeasureViewport.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureViewport.cs) | Main-viewport-only rule |
| [MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs#L57-L78) | AfterGui order |

## Snippets

```csharp
// MeasureTools/Features/Measure/MeasureOverlay.cs L403-L436
float2 mouseViewport = Cursor.GetPosition(viewport);
// ...
if (mouseViewport.X < 0f || mouseViewport.Y < 0f
    || mouseViewport.X > viewport.Size.X || mouseViewport.Y > viewport.Size.Y)
{
    Reset();
    return;
}
// ...
bool eclipticFree = ImGui.GetIO().KeyCtrl;
long now = Stopwatch.GetTimestamp();
if (Stopwatch.GetElapsedTime(_previewLastPickTimestamp, now).TotalMilliseconds >= PreviewPickIntervalMs
    || _previewStateVersion != MeasureState.StateVersion
    || _previewEclipticFree != eclipticFree)
{
    if (MeasureState.Mode == MeasureMode.Circle)
    {
        MapPicker.PickCircle(viewport, mouseViewport, out Anchor? circleCenter, out Anchor? circleRim, reuseStockHover: true);
        _previewCache = circleRim;
        _previewCacheSecondary = circleCenter;
    }
    else
    {
        _previewCache = MapPicker.Pick(viewport, mouseViewport, eclipticFree, reuseStockHover: true);
        _previewCacheSecondary = null;
    }
    _previewLastPickTimestamp = now;
    _previewStateVersion = MeasureState.StateVersion;
    _previewEclipticFree = eclipticFree;
}
```

```csharp
// MeasureTools/Features/Measure/MeasureOverlay.cs L774-L791
private static void Label(ImDrawListPtr dl, float2 pos, ImString text)
{
    // Background plate so labels stay readable over orbit lines, planet discs
    // and other labels.
    float2 size = ImGui.CalcTextSize(text);
    var pMin = new float2(pos.X - 4f, pos.Y - 2f);
    var pMax = new float2(pos.X + size.X + 4f, pos.Y + size.Y + 2f);
    dl.AddRectFilled(in pMin, in pMax, LabelPlate, 3f);
    dl.AddText(in pos, LabelColor, text);
}

// Distance labels without prefix text: ToNearest writes into the stack
// buffer and the span goes straight into the ImGui arena.
private static void LabelDistance(ImDrawListPtr dl, float2 pos, double meters)
{
    Span<char> buffer = stackalloc char[64];
    Label(dl, pos, DistanceReference.ToNearest(meters, buffer));
}
```

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `ImGuiHelper.GetOverlayDrawList`, `GetMainOverlayDrawList`, `DrawTooltip` | [current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L43-L51) | Background list of the main ImGui viewport |
| `Camera.EclToScreen`, `EgoToScreen`, `GetObjectDiameterPixels`, `GetFieldOfView` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L340-L356) | Projection with behind-camera NaN |
| `ViewportRegistry.GameViews`, `MainViewport` | [current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L50-L52) | Find the main viewport without the throwing accessor |
| `DistanceReference.ToNearest` | [current/decomp/KSA/DistanceReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DistanceReference.cs#L167-L180) | Span and SpanBuilder overloads |
| `Program.OnDrawUiViewports`, `DrawUI` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2286) | AfterGui position in the frame; HUD-hidden skip |

## Pitfalls and version notes

- `EclToScreen` results are viewport-local; add `viewport.Position` before drawing into a
  window-wide list.
- Always break polylines at NaN samples; joining across a behind-camera gap draws lines across the
  whole screen.
- Do not throttle expensive per-frame picks by frame count; use wall-clock time plus an explicit
  invalidation counter for state changes.
- The background list is shared with stock overlays; draw order among them follows submission order,
  and AfterGui runs before `Universe.OnDrawUi`, so the stock body outline boxes that
  `IOrbiter.OnDrawUi` adds to the same list later in the frame end up above the measurement lines.
- `ImGuiHelper.GetOverlayDrawList` for a secondary viewport is the current window's list; calling it
  outside that window draws into whatever window is current.

## See also

- [Overview](../overview.md)
- [Measure menu and tool window](./measure-menu-and-tool-window.md)
- [Color palettes](./color-palettes-persistence.md)
- [Surface great-circle tool](./surface-great-circle-tool.md)
- [Circle and face-angle tools](./circle-and-face-angle-tools.md)
- [Cursor unprojection and the construction plane](./cursor-unprojection-and-construction-plane.md)
- [Lines, curves, rings and plated labels for measurements](../../../howto/rendering/overlays/draw-markers-and-annotations-on-world-objects/draw-markers-and-annotations-on-world-objects-2.md) - how-to article demonstrating this feature
- [Behind-camera NaN culling with polyline breaking](../../../howto/rendering/overlays/project-world-positions-to-screen/project-world-positions-to-screen-3.md) - how-to article demonstrating this feature
- [A 45 ms wall-clock throttle with a state-version check for hover picks](../../../howto/testing-and-tooling/in-game-testing/measure-and-budget-mod-performance/measure-and-budget-mod-performance-5.md) - how-to article demonstrating this feature
- [Format per-frame labels on the stack with stackalloc, SpanBuilder and the game unit formatters](../../../howto/ui/draw-lists/draw-a-monospace-text-grid/draw-a-monospace-text-grid-3.md) - how-to article demonstrating this feature
- [Overlays without a window: ImGuiHelper.GetOverlayDrawList](../../../howto/ui/overlays/draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-2.md) - how-to article demonstrating this feature

[^mt-overlay]: MeasureTools/Features/Measure/MeasureOverlay.cs
[^mt-viewport]: MeasureTools/Features/Measure/MeasureViewport.cs
[^mt-state]: MeasureTools/Features/Measure/MeasureState.cs
[^mt-window]: MeasureTools/Features/Measure/MeasureWindow.cs
[^mt-readme]: KSA-MeasureTools README.md
[^ksa-imguihelper]: KSA.ImGuiHelper (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-viewportregistry]: KSA.ViewportRegistry (decompiled, build 2026.10.10.5554)
[^ksa-distancereference]: KSA.DistanceReference (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^starmap-programpatcher]: StarMap.Core/Patches/ProgramPatcher.cs
