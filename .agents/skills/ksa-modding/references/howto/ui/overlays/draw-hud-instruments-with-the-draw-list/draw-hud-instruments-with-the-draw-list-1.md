---
type: How-To
title: "HUD instruments: PathArcTo plus PathStroke arcs and vertex-alpha gradients"
description: Build sweeping arc gauges with the draw list's path API and fake gradient polygons by rescaling vertex alpha after AddConvexPolyFilled
category: ui
group: overlays
topic: draw-hud-instruments-with-the-draw-list
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-public-telemetry/features/foreground-drawlist-broadcast-overlay]
techniques: [imgui-drawlist]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImDrawListPtr, Brutal.ImGuiApi.ImDrawListExtensions, Brutal.ImGuiApi.ImDrawVert]
tags: [hud, gauge, arc, gradient, draw-list, vertex-buffer, broadcast]
---
KSA-Public-Telemetry's bottom bar is built from ImGui draw-list primitives only: round speed and altitude gauges with 270 degree sweeping arc fills, a dark gradient band behind the bar and sloped "shelf" plates behind the gauge groups
that fade toward the floor. ImGui has no arc stroke function with a rotated origin, no gradient polygon fill and no smooth multi-stop vertical fade; this article is the three tricks that solve those. The overlay structure (foreground list,
anchors, menu-bar inset) is in [overlays approach 3](../draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-3.md). Demonstrated by KSA-Public-Telemetry.

## Background: how the game does it

All the tricks use the public Brutal ImGui wrapper (`Brutal.ImGuiApi`), not game code:

- `ImDrawListExtensions.PathClear`, `PathArcTo(center, radius, aMin, aMax, numSegments)` and `PathStroke(color, flags, thickness)` build and stroke an arc path.[current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L348-L359)[^brutal-imguiapi-imdrawlistextensions]
- `AddRectFilledMultiColor` fills a rectangle with four corner colours; `AddConvexPolyFilled(points, color)` fills a convex polygon with one colour.[current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L84-L92)[^brutal-imguiapi-imdrawlistextensions][current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L284-L290)[^brutal-imguiapi-imdrawlistextensions]
- `ImDrawListPtr.VtxBuffer` exposes the vertex array, and `ImDrawVert` is `{ float2 pos; float2 uv; uint col }` with the colour packed ABGR in a `uint`.[current/decomp/Brutal.ImGuiApi/ImDrawListPtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListPtr.cs#L17)[^brutal-imguiapi-imdrawlistptr][current/decomp/Brutal.ImGuiApi/ImDrawVert.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawVert.cs#L7-L17)[^brutal-imguiapi-imdrawvert]
- The game itself uses the same API for its FPS text, sky grid and map markers (`ImGuiHelper.GetMainOverlayDrawList`, `GetForegroundDrawList`).

## Lifecycle and timing

Drawn once per frame in `frame:draw-ui` from `[StarMapAfterGui]` (or the `DrawFps` postfix when the HUD is hidden), into `ImGui.GetForegroundDrawList()`; the draw list is consumed in the ImGui render pass. The value shown is one frame old (the
`TelemetrySnapshot` sampled at `[StarMapAfterOnFrame]`) and is exponentially smoothed per frame, `alpha = 1 - exp(-dt / tau)`, so the arcs move at the same speed at any frame rate:

```csharp
// Rendering/ReadoutPanel.cs L140-L152
private void UpdateSmoothing(in PanelContext context)
{
    double tau = Math.Max(context.Config.SmoothingSeconds, 1e-4);
    double alpha = _initialised ? 1.0 - Math.Exp(-context.DeltaTime / tau) : 1.0;

    for (int i = 0; i < Kinds.Length; i++)
    {
        float target = RawValue(Kinds[i], context.Snapshot, context.Config);
        _smoothed[i] += (float)((target - _smoothed[i]) * alpha);
    }

    _initialised = true;
}
```

See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Arc helper with the origin at 12 o'clock.** ImGui's path angles start at 3 o'clock; subtract a quarter turn so callers pass angles from the top. Pick the segment count from the sweep (4 to 128). Always `PathClear` first:

```csharp
// Rendering/Gfx.cs L421-L450
public static void Arc(
    ImDrawListPtr drawList,
    float2 center,
    float radius,
    float startAngle,
    float endAngle,
    uint color,
    float thickness,
    float opacity,
    int segments = 0)
{
    if (radius <= 0f || Math.Abs(endAngle - startAngle) < 1e-4f)
    {
        return;
    }

    const float quarterTurn = MathF.PI * 0.5f;
    float a0 = startAngle - quarterTurn;
    float a1 = endAngle - quarterTurn;

    if (segments <= 0)
    {
        float sweep = Math.Abs(a1 - a0);
        segments = Math.Clamp((int)(sweep / (MathF.PI / 45f)), 4, 128);
    }

    drawList.PathClear();
    drawList.PathArcTo(in center, radius, a0, a1, segments);
    drawList.PathStroke(OverlayStyle.WithOpacity(color, opacity), ImDrawFlags.None, thickness);
}
```

2. **Use the helper for tracks, fills, rims and brackets.** A gauge plate is a filled circle; its rim is either a full `AddCircle` or, while the intro runs, a partial arc; readout brackets are two short hairline arcs at the top and bottom:

```csharp
// Rendering/Gfx.cs L200-L241
public const float SweepStartAngle = -MathF.PI * 0.75f;

public static void GaugePlate(
    ImDrawListPtr drawList, float2 center, float radius, float opacity,
    bool rim = false, float scale = 1f, float rimPhase = 1f)
{
    drawList.AddCircleFilled(in center, radius,
        OverlayStyle.WithOpacity(OverlayStyle.GaugePlate, opacity), 48);

    if (!rim || rimPhase <= 0f)
    {
        return;
    }

    float thickness = MathF.Max(1f, scale);
    uint color = OverlayStyle.WithOpacity(OverlayStyle.GaugeRim, opacity);

    if (rimPhase >= 1f)
    {
        drawList.AddCircle(in center, radius, color, 48, thickness);
        return;
    }

    Arc(drawList, center, radius,
        SweepStartAngle, SweepStartAngle + MathF.Tau * rimPhase,
        OverlayStyle.GaugeRim, thickness, opacity);
}

public static void ReadoutBrackets(
    ImDrawListPtr drawList, float2 center, float radius, float opacity, float scale)
{
    const float arcHalfSweep = MathF.PI * 0.28f;
    float thickness = MathF.Max(1f, 1.5f * scale);

    Arc(drawList, center, radius,
        MathF.PI * 1.5f - arcHalfSweep, MathF.PI * 1.5f + arcHalfSweep,
        OverlayStyle.Hairline, thickness, opacity);

    Arc(drawList, center, radius,
        MathF.PI * 0.5f - arcHalfSweep, MathF.PI * 0.5f + arcHalfSweep,
        OverlayStyle.Hairline, thickness, opacity);
}
```

3. **Fake a smooth vertical fade with stacked strips.** One two-colour rect shows banding; 20 `AddRectFilledMultiColor` strips with a quadratic ease-out of alpha read much smoother:

```csharp
// Rendering/Gfx.cs L22-L63
private const int FadeStops = 20;
public static void BottomFade(
    ImDrawListPtr drawList,
    float2 viewportPos,
    float2 viewportSize,
    float height,
    float opacity)
{
    float bottom = viewportPos.Y + viewportSize.Y;
    float top = bottom - height;

    float left = viewportPos.X;
    float right = viewportPos.X + viewportSize.X;

    if (right <= left || height <= 0f)
    {
        return;
    }

    float topAlpha = ((OverlayStyle.ShelfTop >> 24) & 0xFFu) / 255f;
    float bottomAlpha = ((OverlayStyle.ShelfBottom >> 24) & 0xFFu) / 255f;
    uint Tone(float t)
    {
        float eased = 1f - (1f - t) * (1f - t);
        float a = topAlpha + (bottomAlpha - topAlpha) * eased;
        return OverlayStyle.WithOpacity((OverlayStyle.ShelfBottom & 0x00FFFFFFu) | 0xFF000000u, opacity * a);
    }

    for (int i = 0; i < FadeStops; i++)
    {
        float t0 = i / (float)FadeStops;
        float t1 = (i + 1) / (float)FadeStops;

        float2 min = new(left, top + height * t0);
        float2 max = new(right, top + height * t1);

        uint c0 = Tone(t0);
        uint c1 = Tone(t1);

        drawList.AddRectFilledMultiColor(in min, in max, c0, c0, c1, c1);
    }
}
```

4. **Gradient-fill a convex polygon by editing the vertices you just emitted.** Record `VtxBuffer.Count` before and after `AddConvexPolyFilled`, then walk those vertices and rescale each one's alpha by its height (here: the plate fades toward the floor). The sloped-shoulder polygon with a quadratic fillet is built from a few samples first:

```csharp
// Rendering/Gfx.cs L108-L148
    Span<float2> points = stackalloc float2[FilletSamples + 4];
    int n = 0;

    points[n++] = new float2(edgeX, bottom);
    points[n++] = new float2(edgeX, top);

    float filletEnd = MathF.Min(radius, runEnd);
    for (int i = 0; i <= FilletSamples; i++)
    {
        float past = filletEnd * i / FilletSamples;
        points[n++] = new float2(
            edgeX + direction * (flatWidth + past),
            MathF.Min(top + Drop(past), bottom));
    }

    if (runEnd > filletEnd)
    {
        points[n++] = new float2(edgeX + direction * (flatWidth + runEnd), bottom);
    }

    if (!onLeft)
    {
        points[..n].Reverse();
    }

    int vertexStart = drawList.VtxBuffer.Count;
    drawList.AddConvexPolyFilled(points[..n], OverlayStyle.WithOpacity(OverlayStyle.ShelfBox, opacity));
    int vertexEnd = drawList.VtxBuffer.Count;

    float floorFraction = Math.Clamp(Tuning.ShelfFloorFraction, 0f, 1f);
    Span<ImDrawVert> vertices = drawList.VtxBuffer.Span;

    for (int i = vertexStart; i < vertexEnd; i++)
    {
        ref ImDrawVert vertex = ref vertices[i];
        float t = Math.Clamp((vertex.pos.Y - top) / height, 0f, 1f);
        float fade = 1f - t * (1f - floorFraction);
        uint alpha = (uint)(((vertex.col >> 24) & 0xFFu) * fade);
        vertex.col = (vertex.col & 0x00FFFFFFu) | (alpha << 24);
    }
}
```

5. **Fit text to the ring** by measuring with the numeric font and shrinking the value to a fraction of the radius (`Gfx.ReadoutText`); the mod keeps its colours in one `OverlayStyle` class.

## Complete example

| File | Role |
|---|---|
| [Rendering/Gfx.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/Gfx.cs) | Arc helper, plates, brackets, fade strips, shelf polygon |
| [Rendering/ReadoutPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/ReadoutPanel.cs) | Readout capsules and value smoothing |
| [Rendering/PanelHost.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/PanelHost.cs) | Anchor and stacking layout |

## Variations and gotchas

- **Vertex editing is order-sensitive.** The shelf gradient relies on `AddConvexPolyFilled` appending its vertices at the end of `VtxBuffer`; anything drawn in between would be recoloured too. Do both calls back to back.
- **Convex polygons only.** `AddConvexPolyFilled` assumes convex input and a consistent winding; the shelf reverses its point order when drawn on the right.
- **Arc origin.** `PathArcTo` angles are radians from 3 o'clock; wrap them once in a helper so no caller does the quarter-turn arithmetic.
- **One draw list, many shapes.** A busy HUD adds hundreds of vertices per frame; strips and circle segment counts are the main cost.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of draw-hud-instruments-with-the-draw-list](overview.md)
- [Approach 2: staged intro and outro animation](draw-hud-instruments-with-the-draw-list-2.md)
- [Approach 3: scrolling tapes, clip rects and endpoint rotation](draw-hud-instruments-with-the-draw-list-3.md)
- [Overlay on the foreground draw list](../draw-a-fullscreen-click-through-overlay/draw-a-fullscreen-click-through-overlay-3.md)
- [Read a vehicle's position, velocity, attitude, orbit and flight instruments](../../../flight-and-orbits/flight-state/read-vehicle-flight-state/overview.md)
- Mod feature: [Foreground draw-list broadcast overlay](../../../../mods/ksa-public-telemetry/features/foreground-drawlist-broadcast-overlay.md)

[^brutal-imguiapi-imdrawlistextensions]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imdrawlistptr]: Brutal.ImGuiApi.ImDrawListPtr (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imdrawvert]: Brutal.ImGuiApi.ImDrawVert (decompiled, build 2026.10.10.5554)
