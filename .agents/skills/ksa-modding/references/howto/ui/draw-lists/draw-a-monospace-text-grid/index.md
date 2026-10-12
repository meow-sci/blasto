# Game Feature

* [Draw per-frame text efficiently: monospace grids and allocation-free labels](overview.md) - Rendering a terminal-style cell grid with run batching, and formatting per-frame ImGui text without allocations using cached strings, stackalloc buffers and span builders

# How-To

* [Cache a display string until the shown value changes](draw-a-monospace-text-grid-2.md) - Keep a small CachedText per label, keyed by the displayed quantity rather than the raw value, so a window that draws every frame builds its strings only when a figure actually moves
* [Draw a monospace cell grid with merged background rects, batched glyph runs and shape-drawn blocks](draw-a-monospace-text-grid-1.md) - Render a terminal-style grid to a draw list so call count scales with visual runs, gating glyph batching on measured advances and drawing block elements and braille as rectangles and dots
* [Format per-frame labels on the stack with stackalloc, SpanBuilder and the game unit formatters](draw-a-monospace-text-grid-3.md) - Draw distance and angle labels with no per-frame allocation by formatting into stackalloc char buffers, passing spans to ImString, and throttling the expensive pick that feeds them
