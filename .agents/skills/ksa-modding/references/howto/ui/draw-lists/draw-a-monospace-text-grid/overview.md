---
type: Game Feature
title: "Draw per-frame text efficiently: monospace grids and allocation-free labels"
description: Rendering a terminal-style cell grid with run batching, and formatting per-frame ImGui text without allocations using cached strings, stackalloc buffers and span builders
category: ui
group: draw-lists
topic: draw-a-monospace-text-grid
demonstrated_by:
  - mods/purrtty/features/terminal-grid-drawlist-renderer
  - mods/ksa-deltavmap/features/subway-map-canvas-rendering
  - mods/ksa-measuretools/features/overlay-drawlist-rendering
lifecycle: [frame:draw-ui, frame:imgui-render, render:imgui]
game_types:
  - Brutal.ImGuiApi.ImDrawListPtr
  - Brutal.ImGuiApi.ImFontPtr
  - KSA.ImGuiBackendVulkanImpl
  - KSA.Program
  - Core.SpanBuilder
  - KSA.DistanceReference
  - KSA.RadianReference
  - Brutal.ImGuiApi.ImString
  - Brutal.ImGuiApi.ImGui
tags: [text, monospace, terminal, grid, allocation, stackalloc, spanbuilder, draw-list]
---
Text is the most expensive thing a mod draws every frame. Each `AddText` call shapes and emits a vertex run, and each `string` built for a label is garbage the collector must clear. Two problems, three solutions: a cell grid (a terminal) must be drawn so that draw-call count scales with visual runs rather than cells, and ordinary labels must be formatted without per-frame allocations. All of it happens in `frame:draw-ui`, and the draw list is rasterized in `frame:imgui-render` and recorded in `render:imgui`; see [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Engine background

| Piece | File | What it gives you |
|---|---|---|
| `ImDrawListExtensions.AddText` (two overloads) | [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L211-L222)[^brutal-imguiapi-imdrawlistextensions] | `AddText(pos, color, ImString)` and `AddText(font, fontSize, pos, color, ImString, ...)`, so one atlas font serves any size |
| `ImString` | [current/decomp/Brutal.ImGuiApi/ImString.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImString.cs#L14)[^brutal-imguiapi-imstring] | A `ref struct` view over UTF-8 or UTF-16 text; implicit conversion from `string`, `ReadOnlySpan<char>`, `ReadOnlySpan<byte>` and `SpanBuilder` (L82-L104 in the same file) |
| `Core.SpanBuilder` | [current/decomp/Core/SpanBuilder.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/SpanBuilder.cs#L7-L9)[^core-spanbuilder] | A `ref struct` over a caller-supplied `Span<char>`; `Append` numbers and spans, implicitly an `ImString` (L82) |
| `Program.DrawFps` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3248-L3268)[^ksa-program] | The game's own allocation-free label: `stackalloc char[128]` plus `SpanBuilder` |
| `DistanceReference.ToNearest`, `RadianReference.ToStringDegrees` | [current/decomp/KSA/DistanceReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DistanceReference.cs#L167-L176)[^ksa-distancereference], [current/decomp/KSA/RadianReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RadianReference.cs#L76)[^ksa-radianreference] | Unit-aware formatting into a caller buffer |
| `ImGuiBackendVulkanImpl` | [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L656-L658)[^ksa-imguibackendvulkanimpl] | Advertises `RendererHasTextures`: the atlas is dynamic, glyphs are rasterized on demand and uploaded by `UpdateTexture` (L920) |

**Why a run is cheaper than a cell.** One `AddText` with 80 characters costs one call and one contiguous vertex run; 80 calls cost 80 of each. The catch is alignment: a run is laid out by the font's advances, so it only lines up with a cell grid if every advance equals the cell width. The first approach measures that instead of assuming it.

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [Merge equal-colour rects and same-font runs](draw-a-monospace-text-grid-1.md) | purrTTY | Metric-gated run batching, block elements and braille as shapes | `frame:draw-ui` | Fastest grid; per-codepoint validation logic |
| 2 | [Cache the string until the shown value's key changes](draw-a-monospace-text-grid-2.md) | KSA-DeltaVMap | `CachedText` keyed by the displayed quantity | `frame:draw-ui` | Trivial to adopt; the caller must pick a correct key |
| 3 | [stackalloc plus SpanBuilder and unit formatters](draw-a-monospace-text-grid-3.md) | KSA-MeasureTools | Format into the stack, pass as `ImString`; throttle expensive picks | `frame:draw-ui` | Zero allocation; spans cannot be stored across calls |

## Choosing

- A fixed grid of cells (terminal, table, matrix): approach 1.
- Panel text that changes rarely (a zoom percentage, a count): approach 2.
- Text that changes every frame (distances, angles, speeds): approach 3, the same pattern the game uses for its FPS counter.
- Approaches 2 and 3 combine: cache when the value is stable, format on the stack when it is not.

## See also

- [Load a custom TTF font into KSA's ImGui atlas and keep it across font rebuilds](../../../../howto/ui/draw-lists/load-a-custom-font-into-imgui/overview.md)
- [Read typed text, key chords and the clipboard from ImGui](../../../../howto/ui/input/read-typed-text-and-key-chords-from-imgui/overview.md)
- [Build pannable, zoomable and 3D canvases inside an ImGui window](../../../../howto/ui/draw-lists/build-a-pan-zoom-canvas-in-imgui/overview.md)
- [Draw charts, scopes and relief maps with the ImGui draw list](../../../../howto/ui/draw-lists/draw-a-chart-with-imdrawlist/overview.md)
- [Load a custom TTF font into KSA's ImGui atlas and keep it across font rebuilds](../load-a-custom-font-into-imgui/overview.md) - related topic
- [Read typed text, key chords and the clipboard from ImGui](../../input/read-typed-text-and-key-chords-from-imgui/overview.md) - related topic
- [Build pannable, zoomable and 3D canvases inside an ImGui window](../build-a-pan-zoom-canvas-in-imgui/overview.md) - related topic

[^brutal-imguiapi-imdrawlistextensions]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imstring]: Brutal.ImGuiApi.ImString (decompiled, build 2026.10.10.5554)
[^core-spanbuilder]: Core.SpanBuilder (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-distancereference]: KSA.DistanceReference (decompiled, build 2026.10.10.5554)
[^ksa-radianreference]: KSA.RadianReference (decompiled, build 2026.10.10.5554)
[^ksa-imguibackendvulkanimpl]: KSA.ImGuiBackendVulkanImpl (decompiled, build 2026.10.10.5554)
