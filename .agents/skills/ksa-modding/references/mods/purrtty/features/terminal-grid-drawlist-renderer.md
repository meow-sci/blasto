---
type: Mod Feature
title: Terminal grid drawn with ImGui draw lists
description: Paint a full terminal cell grid (backgrounds, glyph runs, block and braille glyphs, decorations, cursor) into an ImGui window draw list every frame with run batching
mod: purrtty
category: ui
techniques: [imgui-drawlist, imgui-font, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:imgui-render, render:imgui]
game_types:
  - Brutal.ImGuiApi.ImDrawListPtr
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImFontPtr
  - KSA.ImGuiBackendVulkanImpl
  - KSA.Program
tags: [imgui, draw-list, terminal, text-rendering, batching, performance]
---

purrTTY shows real shell sessions inside KSA as ordinary ImGui windows. There is no custom Vulkan
pipeline for the 2D terminal: every frame the active session's cell grid is painted into the
window's `ImDrawListPtr` with `AddRectFilled`, `AddText`, `AddCircleFilled`, `AddLine` and `AddRect`,
and KSA's own ImGui Vulkan backend rasterizes it with the rest of the UI. The only Vulkan in the 2D
path is the optional kitty image textures (see [kitty graphics](./kitty-graphics-as-imgui-vulkan-textures.md)).

The renderer is written so that draw-call count scales with visual runs, not with cells: a typical
text row costs one background rect per color change and one `AddText` per font/color run.

## How it works

`FrameGridRenderer.Render` takes a renderer-neutral `TerminalFrame` (rows of cells whose fg/bg are
already resolved to RGBA by the backend, see
[headless surface](./headless-terminal-surface-and-frame-snapshot.md)) plus the cell metrics and the
four font variants, and makes four passes:

| Pass | What it draws | Batching rule |
|---|---|---|
| 1 Backgrounds | `AddRectFilled` per horizontal run of equal non-default bg; then the row's selection rect composited on top | Adjacent cells with the same packed color merge; default-bg cells are skipped (the window background shows through) |
| 2 Glyphs | `AddText(font, size, pos, color, utf8)` | Consecutive narrow cells with the same variant and color merge into one UTF-8 run; blanks inside a run are bridged with spaces |
| 2a Block Elements U+2580-U+259F | Exact `AddRectFilled` strips (halves, eighths, quadrants, shade levels as fg alpha) | Same-height strips merge horizontally, so half-block "pixel" output becomes color strips |
| 2b Braille U+2800-U+28FF | `AddCircleFilled` on a uniform 2x4 dot grid | The bundled Nerd Fonts ship no braille glyphs, so the font path would draw tofu |
| 3 Decorations | `AddLine` underline (single, double, dotted, dashed, curly), strikethrough, overline | Skipped for rows whose `FrameRow.HasDecorations` is false |
| 4 Cursor | Block (fill + glyph redrawn in bg color), hollow box, bar, underline | Unfocused windows always draw a steady hollow box |

Glyph batching is metric-gated. A run of characters drawn by one `AddText` only lines up with the
grid if every advance equals the cell width, so `TerminalWindow` measures, once per (family, size),
the full printable-ASCII string with `ImGui.CalcTextSize` and enables batching for a variant only
when the total is within 0.5 px of `95 * cellWidth` (`IsAsciiMonospace`). Non-ASCII BMP characters
are validated one codepoint at a time by `GlyphBatchCache.CanBatch` (tolerance 0.005 px, cached per
variant). Anything unvalidated falls back to one `AddText` per cell at the exact cell origin, which
guarantees alignment.

Colors are packed as ImGui's ABGR `uint` (`ToU32`), with the window's foreground and cell-background
opacity multiplied into alpha, so translucent terminals cost nothing extra. Selection uses its own
alpha so it stays visible even at zero cell-background opacity.

The window wraps this in a fixed order (`TerminalWindow.Render`): resize the session if the grid
changed, `Surface.BuildFrame()`, draw kitty images with z < 0, `FrameGridRenderer.Render`, draw
kitty images with z >= 0, optional perf HUD, then tick every background tab so their PTY inboxes
drain. `Render` returns `GridRenderStats` (rect, dot, run, per-cell and line counts) that the
"Show performance HUD" overlay prints.

## Lifecycle and timing

- Runs inside `TerminalMod.OnAfterUi`, a `[StarMapAfterGui]` method. StarMap invokes it from a
  Harmony postfix on `Program.OnDrawUiViewports`.[^starmap-program-patcher] In `Program.OnFrame`
  that call sits after `PrepareImGui` (ImGui `NewFrame`) and before `ImGui.Render()`, inside
  `if (DrawUI)`.[^ksa-program] This is `frame:draw-ui`: ImGui calls are legal and draw lists are
  being built.
- `OnAfterUi` calls `_controller.Update(dt)` then `_controller.Render()`; the controller renders
  every open `TerminalWindow` in order.[^terminal-mod]
- The draw lists are turned into vertex data by `ImGui.Render()` (`frame:imgui-render`) and
  recorded by `ImGuiBackend.Vulkan.RenderDrawData` inside the final swapchain render pass after
  the final composite (`render:imgui`).[^ksa-program] Nothing purrTTY draws in 2D can appear under
  world geometry or post-processing.
- `Surface.BuildFrame()` mutates the native terminal engine, so it must run on the thread that
  owns the engine. purrTTY makes that the main thread by calling it only from this hook (and the
  hidden-drain path in the same hook).
- Because the hook is skipped when the HUD is hidden (`Program.DrawUI == false`), terminals are not
  drawn then. That matches the game hiding its own UI.

## Key code

| Path | Role |
|---|---|
| [purrTTY.Display/Ghostty/FrameGridRenderer.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/FrameGridRenderer.cs#L51-L423) | The four-pass renderer and `GridRenderStats` |
| [purrTTY.Display/Ghostty/FrameGridRenderer.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/FrameGridRenderer.cs#L596-L632) | Braille dot matrix (`DrawBrailleCell`) |
| [purrTTY.Display/Ghostty/FrameGridRenderer.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/FrameGridRenderer.cs#L675-L750) | Cursor shapes (`DrawCursor`) |
| [purrTTY.Display/Ghostty/TerminalWindow.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.cs#L413-L521) | Grid sizing, `BuildFrame`, image bands, renderer call, background-tab ticking |
| [purrTTY.Display/Ghostty/TerminalWindow.Fonts.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.Fonts.cs#L48-L82) | Cell metrics from `CalcTextSize("M")` and the ASCII monospace check |
| [purrTTY.Display/Ghostty/FrameFonts.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/FrameFonts.cs#L13-L47) | `GlyphBatchCache`: per-codepoint advance validation |
| [purrTTY.Display/Ghostty/TerminalWindow.PerfHud.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.PerfHud.cs) | Perf HUD overlay (build/submit ms, dirty state, MB/s, draw-call breakdown) |
| [purrTTY.Terminal/Rendering/TerminalFrame.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Rendering/TerminalFrame.cs#L41-L74) | The frame snapshot the renderer consumes |

## Snippets

Background runs: one rect per run of equal color, default background skipped.

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

Glyph runs only batch when the advance was validated; otherwise each cell is placed exactly.

```csharp
// purrTTY.Display/Ghostty/FrameGridRenderer.cs L291-L342
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
                    // ...
                }
                // ...
                drawList.AddText(
                    fonts.SelectVariant(variant), fontSize,
                    new float2(origin.X + c * cellWidth, y), fg32, grapheme);
                stats.GlyphCells++;
```

The measurement that gates ASCII batching.

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.Fonts.cs L48-L82
    private void ComputeCellMetrics(ImFontPtr font, float fontSize)
    {
        ImGui.PushFont(font, fontSize);
        var size = ImGui.CalcTextSize("M");
        ImGui.PopFont();

        _cellWidth = size.X > 0.5f ? size.X : fontSize * 0.6f;
        _cellHeight = size.Y > 0.5f ? size.Y : fontSize * 1.2f;
    }

    private static readonly string AsciiSample = CreateAsciiSample();

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

## Game APIs touched

| API | Where | Purpose |
|---|---|---|
| `ImDrawListPtr.AddRectFilled` / `AddText(ImFontPtr, float, ...)` / `AddCircleFilled` / `AddLine` / `PushClipRect` | [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs) | Primitive submission; `AddText` accepts a font plus an explicit pixel size, so one atlas font serves every size[^ksa-drawlist-ext] |
| `ImGui.GetWindowDrawList`, `ImGui.CalcTextSize`, `ImGui.PushFont(ImFontPtr, float)` | `Brutal.ImGuiApi.ImGui` | Draw target and metric measurement |
| `Program.OnFrame` / `OnDrawUiViewports` / `DrawUI` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2287) | Where the hook runs and when it is skipped |
| `ImGuiBackendVulkanImpl.RenderDrawData` | [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs) | KSA's ImGui Vulkan renderer that turns the draw list into GPU work[^ksa-imgui-vulkan] |

## Pitfalls and version notes

- Never pass raw float pixel positions for a run unless the font is proven monospace at that size;
  fractional cell widths make runs drift by a pixel per few columns. purrTTY's per-variant and
  per-codepoint validation is the fix.
- `FrameRow` arrays are owned and reused by the producing surface; read them only within the tick
  that produced the frame (comment on `TerminalFrame`).[^terminal-frame]
- The batching scratch buffer is a static `byte[]` and therefore render-thread-only.
- Chrome hiding fills the grid area with the theme background itself because the window background
  becomes transparent; layout must not change between hidden and shown chrome or the grid (and PTY
  size) would jump (gotcha 8).[^gotchas]
- No version-specific breakage found against build 2026.10.10.5554: the draw-list extension methods
  used here exist with the same shapes.

## See also

- [Nerd fonts in the ImGui atlas](./nerd-font-loading-into-imgui-atlas.md)
- [Window chrome, tabs and resize snap](./terminal-window-chrome-tabs-and-resize-snap.md)
- [Kitty graphics as ImGui Vulkan textures](./kitty-graphics-as-imgui-vulkan-textures.md)
- [Headless terminal surface and frame snapshot](./headless-terminal-surface-and-frame-snapshot.md)
- [Off-screen ImGui context render-to-texture](./offscreen-imgui-context-render-to-texture.md) (the same renderer drawn into a world-space texture)
- [purrTTY overview](../overview.md)
- [Draw a monospace cell grid with merged background rects, batched glyph runs and shape-drawn blocks](../../../howto/ui/draw-lists/draw-a-monospace-text-grid/draw-a-monospace-text-grid-1.md) - how-to article demonstrating this feature

[^starmap-program-patcher]: StarMap.Core/Patches/ProgramPatcher.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^terminal-mod]: purrTTY.GameMod/TerminalMod.cs
[^ksa-drawlist-ext]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
[^ksa-imgui-vulkan]: KSA.ImGuiBackendVulkanImpl (decompiled, build 2026.10.10.5554)
[^terminal-frame]: purrTTY.Terminal/Rendering/TerminalFrame.cs
[^gotchas]: purrTTY docs/gotchas.md
