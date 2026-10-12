---
type: How-To
title: Format per-frame labels on the stack with stackalloc, SpanBuilder and the game unit formatters
description: Draw distance and angle labels with no per-frame allocation by formatting into stackalloc char buffers, passing spans to ImString, and throttling the expensive pick that feeds them
category: ui
group: draw-lists
topic: draw-a-monospace-text-grid
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-measuretools/features/overlay-drawlist-rendering]
techniques: [imgui-drawlist]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - Brutal.ImGuiApi.ImDrawListPtr
  - Brutal.ImGuiApi.ImString
  - Core.SpanBuilder
  - KSA.DistanceReference
  - KSA.RadianReference
  - KSA.Program
tags: [text, stackalloc, spanbuilder, allocation, labels, throttle, draw-list]
---
KSA-MeasureTools draws a distance, an angle or a diameter next to every measurement, every frame, projected from 3D points. It formats them without allocating: numbers are written into a `stackalloc char[64]` buffer by the game's own unit formatters (`DistanceReference.ToNearest`, `RadianReference.ToStringDegrees`) or into a `Core.SpanBuilder`, and the span is passed to `ImString`, which copies it into ImGui's arena. The same article covers the second half of per-frame cost: the expensive hover pick behind the labels is throttled by wall-clock time. Demonstrated by [World-space measurement overlay on the ImGui background draw list](../../../../mods/ksa-measuretools/features/overlay-drawlist-rendering.md).

## Background: how the game does it

The game uses exactly this pattern for its own frame-time overlay. `Program.DrawFps` allocates a 128-char stack buffer, wraps it in a `SpanBuilder`, appends a number and a suffix, and passes the builder straight to `ImGuiHelper.DrawText` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3248-L3268)[^ksa-program]):

```csharp
// current/decomp/KSA/Program.cs L3260-L3264
Span<char> buffer = stackalloc char[128];
SpanBuilder spanBuilder = new SpanBuilder(buffer);
spanBuilder.Append(App.FramesPerSecond);
spanBuilder.Append(" FPS".AsSpan());
ImGuiHelper.DrawText(mainOverlayDrawList, pos + float5, spanBuilder, Color.White, TextAlignment.Right);
```

`SpanBuilder` is a `ref struct` over a caller-supplied `Span<char>` ([current/decomp/Core/SpanBuilder.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/SpanBuilder.cs#L7-L9)[^core-spanbuilder]) and converts implicitly to `ImString` ([current/decomp/Core/SpanBuilder.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/SpanBuilder.cs#L82-L85)[^core-spanbuilder]). `ImString` itself is a `ref struct` ([current/decomp/Brutal.ImGuiApi/ImString.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImString.cs#L14)[^brutal-imguiapi-imstring]), so it cannot be stored in a field or captured by a lambda. The unit formatters come in two shapes, one returning a `ReadOnlySpan<char>` over your buffer and one appending to a builder ([current/decomp/KSA/DistanceReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DistanceReference.cs#L167-L176)[^ksa-distancereference]); `RadianReference.ToStringDegrees(Span<char>)` is the angle analogue ([current/decomp/KSA/RadianReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RadianReference.cs#L76)[^ksa-radianreference]).

## Lifecycle and timing

The overlay is drawn from `[StarMapAfterGui]` (`Mod.Draw` runs `MeasureState.Prune()`, then the window, then the overlay): inside `frame:draw-ui`, after `ImGui.NewFrame` and before `ImGui.Render()`. The camera for the frame was computed in `OnFrameViewports`, so projected points match the 3D scene. The draw list is the background list of the main viewport (`ImGuiHelper.GetOverlayDrawList`, [current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L43-L52)[^ksa-imguihelper]), consumed in `render:imgui`. With the HUD hidden the hook does not run; a mod that must keep drawing then hooks `OnDrawUiConsole` instead. See [Game loop and mod lifecycle](../../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **One `Label` helper takes `ImString`.** Because `ImString` accepts strings, char spans and a `SpanBuilder` alike, every call site picks the cheapest form. The helper draws a dark plate behind the text so labels stay readable over orbit lines:

```csharp
// MeasureTools/Features/Measure/MeasureOverlay.cs L771-L783
// ImString accepts strings, char spans and a SpanBuilder alike, and copies span
// text into ImGui's arena, so callers that format into a stack buffer skip the
// per-frame string allocation entirely.
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
```

2. **Distance: stack buffer plus the unit formatter.**

```csharp
// MeasureTools/Features/Measure/MeasureOverlay.cs L785-L791
// Distance labels without prefix text: ToNearest writes into the stack
// buffer and the span goes straight into the ImGui arena.
private static void LabelDistance(ImDrawListPtr dl, float2 pos, double meters)
{
    Span<char> buffer = stackalloc char[64];
    Label(dl, pos, DistanceReference.ToNearest(meters, buffer));
}
```

3. **Prefix text: use a `SpanBuilder`.** Append the prefix, let the formatter append the number, pass the builder:

```csharp
// MeasureTools/Features/Measure/MeasureOverlay.cs L793-L811
private static void LabelDiameter(ImDrawListPtr dl, float2 pos, double meters)
{
    Span<char> buffer = stackalloc char[64];
    var sb = new SpanBuilder(buffer);
    sb.Append("d ");
    DistanceReference.ToNearest(meters, ref sb);
    Label(dl, pos, sb);
}

private static void LabelDegrees(ImDrawListPtr dl, float2 pos, double angleRadians)
{
    if (double.IsNaN(angleRadians))
    {
        Label(dl, pos, "undefined");
        return;
    }
    Span<char> buffer = stackalloc char[64];
    Label(dl, pos, RadianReference.FromRadians(angleRadians).ToStringDegrees(buffer));
}
```

4. **Space stacked labels from the font, not constants**, so plates never touch at any font size:

```csharp
// MeasureTools/Features/Measure/MeasureOverlay.cs L763-L769
// Vertical step between labels stacked at the same anchor: one text line
// plus the plate padding (2 px top and bottom) and a small gap, so the
// plates never touch at any font size.
private static float LabelStackStep()
{
    return ImGui.GetTextLineHeightWithSpacing() + 6f;
}
```

5. **Skip invalid projections.** A point behind the camera projects to NaN; a `Valid` check is the culling rule for labels and lines:

```csharp
// MeasureTools/Features/Measure/MeasureOverlay.cs L828-L831
private static bool Valid(float2 s)
{
    return !float.IsNaN(s.X) && !float.IsNaN(s.Y) && !float.IsInfinity(s.X) && !float.IsInfinity(s.Y);
}
```

6. **Throttle the expensive work behind the labels by wall-clock time.** The hover pick loops over mesh triangles, so it runs at most every 45 ms (about 22 times a second at any refresh rate; a frame-count throttle would scale with the refresh rate), immediately when the measurement state changes or Ctrl is toggled, and the cache is dropped when the cursor leaves the viewport:

```csharp
// MeasureTools/Features/Measure/MeasureOverlay.cs L403-L436
float2 mouseViewport = Cursor.GetPosition(viewport);
// A cursor outside the viewport cannot place a point, and a cursor that
// left the OS window reports far outside it; either way picking over
// that position is wasted work.
if (mouseViewport.X < 0f || mouseViewport.Y < 0f
    || mouseViewport.X > viewport.Size.X || mouseViewport.Y > viewport.Size.Y)
{
    Reset();
    return;
}
// Ctrl previews (and places) a free point on the ecliptic plane even where
// snapping would win; a modifier change re-picks immediately so the preview
// flips with the key.
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

## Complete example

| File | Role |
|---|---|
| [MeasureTools/Features/Measure/MeasureOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureOverlay.cs#L702-L845) | Arc, labels, plates, validity |
| [MeasureTools/Features/Measure/MeasureOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureOverlay.cs#L387-L590) | Hover preview and throttled pick |
| [MeasureTools/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Mod.cs#L57-L78) | AfterGui order |

## Variations and gotchas

- **`stackalloc` in a loop.** `stackalloc` inside a loop grows the stack each iteration; allocate once per method (as here, once per label helper) and reuse.
- **Never store the span.** The buffer dies with the method; `ImString` copies span text into ImGui's arena on the call, so pass it directly and keep nothing.
- **Reference to Core.** `Core.SpanBuilder` lives in the game's `Core` assembly; the mod references it only for this.
- **A path-arc helper does exist.** The mod's comment says the binding exposes no path-arc helper and draws a 20-segment arc with `AddLine` ([MeasureTools/Features/Measure/MeasureOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureOverlay.cs#L722-L741)); in build 2026.10.10.5554 `PathArcTo` and `PathStroke` exist on `ImDrawListExtensions` ([current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L348-L359)[^brutal-imguiapi-imdrawlistextensions]), as used in [Draw HUD instruments: arc gauges, gradients, scrolling tapes and animated intros](../../../../howto/ui/overlays/draw-hud-instruments-with-the-draw-list/overview.md).
- **Version.** `ToNearest` overloads and `SpanBuilder` are game types; check their signatures against the new decomp after an update.

## See also

- [Overview of per-frame text](overview.md)
- [Approach 1: monospace grid](draw-a-monospace-text-grid-1.md)
- [Approach 2: cached strings](draw-a-monospace-text-grid-2.md)
- Mod feature: [World-space measurement overlay on the ImGui background draw list](../../../../mods/ksa-measuretools/features/overlay-drawlist-rendering.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^core-spanbuilder]: Core.SpanBuilder (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imstring]: Brutal.ImGuiApi.ImString (decompiled, build 2026.10.10.5554)
[^ksa-distancereference]: KSA.DistanceReference (decompiled, build 2026.10.10.5554)
[^ksa-radianreference]: KSA.RadianReference (decompiled, build 2026.10.10.5554)
[^ksa-imguihelper]: KSA.ImGuiHelper (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imdrawlistextensions]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
