---
type: How-To
title: Draw a monospace cell grid with merged background rects, batched glyph runs and shape-drawn blocks
description: Render a terminal-style grid to a draw list so call count scales with visual runs, gating glyph batching on measured advances and drawing block elements and braille as rectangles and dots
category: ui
group: draw-lists
topic: draw-a-monospace-text-grid
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/purrtty/features/terminal-grid-drawlist-renderer]
techniques: [imgui-drawlist, imgui-font]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:imgui-render, render:imgui]
game_types:
  - Brutal.ImGuiApi.ImDrawListPtr
  - Brutal.ImGuiApi.ImFontPtr
  - Brutal.ImGuiApi.ImGui
  - KSA.ImGuiBackendVulkanImpl
tags: [terminal, grid, batching, monospace, braille, block-elements, draw-list]
---
purrTTY shows real shell sessions inside KSA as ordinary ImGui windows. There is no custom Vulkan pipeline for the 2D terminal: every frame the active session's cell grid is painted into the window's draw list with `AddRectFilled`, `AddText`, `AddCircleFilled` and `AddLine`, and KSA's own ImGui Vulkan backend rasterizes it with the rest of the UI. The renderer keeps the draw-call count proportional to visual runs: a typical text row costs one background rect per colour change and one `AddText` per font and colour run. Demonstrated by [Terminal grid drawn with ImGui draw lists](../../../../mods/purrtty/features/terminal-grid-drawlist-renderer.md). Prerequisite: a monospace font in the atlas (see [Load a custom TTF font into KSA's ImGui atlas and keep it across font rebuilds](../../../../howto/ui/draw-lists/load-a-custom-font-into-imgui/overview.md)).

## Background: how the game does it

The game draws all UI through ImGui's draw lists and renders them with `ImGuiBackendVulkanImpl.RenderDrawData`. The backend declares `RendererHasTextures` ([current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L656-L658)[^ksa-imguibackendvulkanimpl]), so the font atlas is dynamic: glyphs are rasterized on first use and uploaded through `UpdateTexture`, and `ImDrawListExtensions.AddText(font, fontSize, pos, color, text)` ([current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L222)[^brutal-imguiapi-imdrawlistextensions]) draws any font at any pixel size. A run of text is laid out by the font's per-glyph advance, which is why batching must be validated against the grid.

## Lifecycle and timing

The renderer runs inside purrTTY's `[StarMapAfterGui]` method, which StarMap calls from a Harmony postfix on `Program.OnDrawUiViewports`: after `PrepareImGui` and before `ImGui.Render()`, inside `if (DrawUI)` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2262-L2286)[^ksa-program]). That is `frame:draw-ui`; the draw list becomes vertex data in `ImGui.Render()` (`frame:imgui-render`) and is recorded in the final ImGui pass (`render:imgui`), so nothing drawn here can appear under world geometry or post-processing. When the HUD is hidden the hook does not run and terminals are not drawn. See [Game loop and mod lifecycle](../../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Measure the cell once per font and size.** Cell width and height come from `CalcTextSize("M")` under `PushFont(font, size)`, with a fallback if the measurement is degenerate:

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.Fonts.cs L48-L58
private void ComputeCellMetrics(ImFontPtr font, float fontSize)
{
    ImGui.PushFont(font, fontSize);
    var size = ImGui.CalcTextSize("M");
    ImGui.PopFont();

    _cellWidth = size.X > 0.5f ? size.X : fontSize * 0.6f;
    _cellHeight = size.Y > 0.5f ? size.Y : fontSize * 1.2f;
}

private static readonly string AsciiSample = CreateAsciiSample();
```

2. **Validate that batching is safe, and only then batch.** A run drawn by one `AddText` lines up with the grid only if every advance equals the cell width. Measure the whole printable-ASCII string (including the space, because blanks inside a run are bridged with spaces) and require the total to be within 0.5 px of `95 * cellWidth`:

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.Fonts.cs L60-L82
// Includes ' ' (0x20): run batching bridges blank cells with spaces, so the
// space advance must be validated along with the printable glyphs.
private static string CreateAsciiSample()
{
    Span<char> chars = stackalloc char[95];
    for (int i = 0; i < chars.Length; i++)
    {
        chars[i] = (char)(' ' + i);
    }

    return new string(chars);
}

// A variant may batch ASCII runs only when its measured printable-ASCII
// advance matches the grid cell width exactly; otherwise the renderer
// falls back to per-cell placement to keep columns aligned.
private static bool IsAsciiMonospace(ImFontPtr font, float fontSize, float cellWidth)
{
    ImGui.PushFont(font, fontSize);
    var size = ImGui.CalcTextSize(AsciiSample);
    ImGui.PopFont();
    return Math.Abs(size.X - AsciiSample.Length * cellWidth) <= 0.5f;
}
```

3. **Validate non-ASCII glyphs one codepoint at a time**, with a tight tolerance because drift accumulates along a run, and cache the answer per font variant:

```csharp
// purrTTY.Display/Ghostty/FrameFonts.cs L13-L47
internal sealed class GlyphBatchCache
{
    private readonly Dictionary<uint, bool>[] _byVariant =
    {
        new(), new(), new(), new(),
    };

    public void Clear()
    {
        foreach (var map in _byVariant)
        {
            map.Clear();
        }
    }

    /// <summary>
    /// Whether <paramref name="codepoint"/> advances by exactly one cell in
    /// the given variant font. Measured once per codepoint and cached.
    /// </summary>
    public bool CanBatch(uint codepoint, int variant, ImFontPtr font, float fontSize, float cellWidth)
    {
        var map = _byVariant[variant & 3];
        if (!map.TryGetValue(codepoint, out bool ok))
        {
            ImGui.PushFont(font, fontSize);
            var size = ImGui.CalcTextSize(char.ConvertFromUtf32((int)codepoint));
            ImGui.PopFont();
            // Tight per-char tolerance: drift accumulates along a run.
            ok = Math.Abs(size.X - cellWidth) <= 0.005f;
            map[codepoint] = ok;
        }

        return ok;
    }
}
```

4. **Pass 1: merge backgrounds.** Walk the row once; extend the current run while the packed colour is equal and the cells are adjacent, flush otherwise, and skip cells that match the default background so the window background shows through:

```csharp
// purrTTY.Display/Ghostty/FrameGridRenderer.cs L94-L131
for (int c = 0; c < cols; c++)
{
    ref readonly var cell = ref cells[c];
    if (cell.Width == CellWidth.Spacer)
    {
        continue; // covered by the preceding wide cell's span
    }

    int span = cell.Width == CellWidth.Wide ? 2 : 1;
    uint color = ToU32(cell.Bg, cellBackgroundOpacity);
    bool draw = color != defaultBg32;

    if (draw && runStart >= 0 && c == runEnd && color == runColor)
    {
        runEnd = c + span;
        continue;
    }

    if (runStart >= 0)
    {
        drawList.AddRectFilled(
            new float2(origin.X + runStart * cellWidth, y),
            new float2(origin.X + runEnd * cellWidth, y2),
            runColor);
        stats.BackgroundRects++;
    }

    if (draw)
    {
        runStart = c;
        runEnd = c + span;
        runColor = color;
    }
    else
    {
        runStart = -1;
    }
}
```

5. **Pass 2: merge glyph runs.** Consecutive narrow cells with the same font variant and colour join one UTF-8 run in a reusable scratch buffer; gaps are bridged with spaces; anything unvalidated falls back to one `AddText` per cell at the exact cell origin, which guarantees alignment:

```csharp
// purrTTY.Display/Ghostty/FrameGridRenderer.cs L291-L301, L303-L329, L339-L342
int variant = FrameFonts.VariantIndex(cell.Flags);
uint fg32 = ToU32(cell.Fg, foregroundOpacity);

// ASCII batches when the variant's measured advance matches the
// cell width; other single-unit glyphs batch when their
// individual advance has been validated.
bool batchable = cell.Width == CellWidth.Narrow
    && fonts.CanBatchVariant(variant)
    && (ch > ' ' && ch < (char)127
        || (ch >= (char)127 && !char.IsSurrogate(ch)
            && fonts.BatchCache?.CanBatch(ch, variant, fonts.SelectVariant(variant), fontSize, cellWidth) == true));
// ...
if (batchable)
{
    if (runStart >= 0 && variant == runVariant && fg32 == runColor && c >= runNext)
    {
        for (int gap = runNext; gap < c; gap++)
        {
            runScratch[runLen++] = (byte)' ';
        }

        runLen += AppendUtf8(runScratch, runLen, ch);
        runNext = c + 1;
        continue;
    }

    if (runStart >= 0)
    {
        stats.GlyphRuns++;
        FlushRun(drawList, fonts, fontSize, origin, y, cellWidth, runStart, runVariant, runColor, runScratch, runLen);
    }

    runStart = c;
    runNext = c + 1;
    runVariant = variant;
    runColor = fg32;
    runLen = AppendUtf8(runScratch, 0, ch);
    continue;
}
// ...
drawList.AddText(
    fonts.SelectVariant(variant), fontSize,
    new float2(origin.X + c * cellWidth, y), fg32, grapheme);
stats.GlyphCells++;
```

The flush is one call with an explicit font and size:

```csharp
// purrTTY.Display/Ghostty/FrameGridRenderer.cs L656-L673
private static void FlushRun(
    ImDrawListPtr drawList,
    in FrameFonts fonts,
    float fontSize,
    float2 origin,
    float y,
    float cellWidth,
    int startCol,
    int variant,
    uint color,
    byte[] buffer,
    int length)
{
    drawList.AddText(
        fonts.SelectVariant(variant), fontSize,
        new float2(origin.X + startCol * cellWidth, y), color,
        (ReadOnlySpan<byte>)buffer.AsSpan(0, length));
}
```

6. **Draw Block Elements (U+2580 to U+259F) as rectangles.** Exact coverage, no hinting seams between cells, and same-height strips merge horizontally so half-block "pixel" output becomes a few colour strips:

```csharp
// purrTTY.Display/Ghostty/FrameGridRenderer.cs L543-L580
/// <summary>Draws the partial-width / quadrant Block Elements. Returns the rect count.</summary>
private static int DrawBlockCell(
    ImDrawListPtr drawList, char ch, float x, float y, float cellWidth, float cellHeight, uint fg)
{
    switch (ch)
    {
        case >= '▉' and <= '▏': // ▉..▏ left fractions 7/8..1/8
        {
            float w = (0x2590 - ch) / 8f * cellWidth;
            drawList.AddRectFilled(new float2(x, y), new float2(x + w, y + cellHeight), fg);
            return 1;
        }

        case '▐': // ▐ right half
            drawList.AddRectFilled(new float2(x + cellWidth * 0.5f, y), new float2(x + cellWidth, y + cellHeight), fg);
            return 1;

        case '▕': // ▕ right eighth
            drawList.AddRectFilled(new float2(x + cellWidth * 0.875f, y), new float2(x + cellWidth, y + cellHeight), fg);
            return 1;

        case >= '▖' and <= '▟': // quadrants
        {
            int mask = QuadrantMasks[ch - 0x2596];
            float mx = x + cellWidth * 0.5f;
            float my = y + cellHeight * 0.5f;
            int rects = 0;
            if ((mask & 1) != 0) { drawList.AddRectFilled(new float2(x, y), new float2(mx, my), fg); rects++; }
            if ((mask & 2) != 0) { drawList.AddRectFilled(new float2(mx, y), new float2(x + cellWidth, my), fg); rects++; }
            if ((mask & 4) != 0) { drawList.AddRectFilled(new float2(x, my), new float2(mx, y + cellHeight), fg); rects++; }
            if ((mask & 8) != 0) { drawList.AddRectFilled(new float2(mx, my), new float2(x + cellWidth, y + cellHeight), fg); rects++; }
            return rects;
        }

        default:
            return 0;
    }
}
```

7. **Draw Braille (U+2800 to U+28FF) as a 2 by 4 dot matrix.** The bundled fonts ship no braille glyphs, so going through the font would draw replacement-character boxes. The low eight bits of the codepoint are the dot pattern:

```csharp
// purrTTY.Display/Ghostty/FrameGridRenderer.cs L596-L632
private static int DrawBrailleCell(
    ImDrawListPtr drawList, char ch, float x, float y, float cellWidth, float cellHeight, uint fg)
{
    int pattern = ch - 0x2800;
    if (pattern == 0)
    {
        return 0; // U+2800 BRAILLE PATTERN BLANK — nothing to draw.
    }

    float colW = cellWidth * 0.5f;
    float rowH = cellHeight * 0.25f;
    // Radius capped at half the smaller dot pitch so dots never overlap;
    // floored so they stay visible in tiny cells.
    float radius = Math.Max(0.75f, 0.42f * Math.Min(colW, rowH));

    int dots = 0;
    for (int col = 0; col < 2; col++)
    {
        float cx = x + (col + 0.5f) * colW;
        for (int row = 0; row < 4; row++)
        {
            // Rows 0–2 are bits col*3+row (left 0,1,2 / right 3,4,5);
            // the bottom row is bit 6 (left, dot 7) or 7 (right, dot 8).
            int bit = row < 3 ? col * 3 + row : 6 + col;
            if ((pattern & (1 << bit)) == 0)
            {
                continue;
            }

            float cy = y + (row + 0.5f) * rowH;
            drawList.AddCircleFilled(new float2(cx, cy), radius, fg);
            dots++;
        }
    }

    return dots;
}
```

8. **Pack colours yourself.** The renderer converts RGBA to ImGui's ABGR `uint` and multiplies opacity into alpha, so translucent grids cost nothing extra:

```csharp
// purrTTY.Display/Ghostty/FrameGridRenderer.cs L44-L50
public static uint ToU32(RgbaColor c)
    => c.R | ((uint)c.G << 8) | ((uint)c.B << 16) | ((uint)c.A << 24);

public static uint ToU32(RgbaColor c, float opacity)
    => c.R | ((uint)c.G << 8) | ((uint)c.B << 16)
       | ((uint)(byte)Math.Clamp(c.A * opacity, 0f, 255f) << 24);

```

## Complete example

| File | Role |
|---|---|
| [purrTTY.Display/Ghostty/FrameGridRenderer.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/FrameGridRenderer.cs#L51-L423) | The four-pass renderer (backgrounds, glyphs, decorations, cursor) |
| [purrTTY.Display/Ghostty/FrameGridRenderer.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/FrameGridRenderer.cs#L596-L632) | Braille dot matrix |
| [purrTTY.Display/Ghostty/TerminalWindow.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.cs#L413-L521) | Grid sizing, frame build, renderer call |
| [purrTTY.Display/Ghostty/TerminalWindow.Fonts.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.Fonts.cs#L48-L82) | Cell metrics and the ASCII monospace check |
| [purrTTY.Display/Ghostty/FrameFonts.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/FrameFonts.cs#L13-L47) | `GlyphBatchCache` |

## Variations and gotchas

- **Never batch on faith.** Fractional cell widths make runs drift by a pixel every few columns; the validation exists to prevent that.
- **Scratch buffer is static.** The run buffer is a static `byte[]`, so rendering is single-threaded (render thread only).
- **Reused frame arrays.** The producing surface owns and reuses the row arrays; read them only in the tick that produced the frame.
- **Font rebuilds.** A game font-settings change clears the whole ImGui atlas and no mod re-adds its fonts, so cached `ImFontPtr`s and the `GlyphBatchCache` should be reset when the font is lost (see [Load a custom TTF font into KSA's ImGui atlas and keep it across font rebuilds](../../../../howto/ui/draw-lists/load-a-custom-font-into-imgui/overview.md)).
- **Layout stability.** Hiding window chrome must not change the grid size, or the PTY size would jump.
- **Version.** Checked against build 2026.10.10.5554; the draw-list extension methods have the same shapes there.

## See also

- [Overview of per-frame text](overview.md)
- [Approach 2: cache the string until the key changes](draw-a-monospace-text-grid-2.md)
- [Approach 3: stackalloc plus SpanBuilder](draw-a-monospace-text-grid-3.md)
- [Load a custom TTF font into KSA's ImGui atlas and keep it across font rebuilds](../../../../howto/ui/draw-lists/load-a-custom-font-into-imgui/overview.md)
- Mod feature: [Terminal grid drawn with ImGui draw lists](../../../../mods/purrtty/features/terminal-grid-drawlist-renderer.md)

[^ksa-imguibackendvulkanimpl]: KSA.ImGuiBackendVulkanImpl (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imdrawlistextensions]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
