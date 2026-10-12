---
type: How-To
title: Cache a display string until the shown value changes
description: Keep a small CachedText per label, keyed by the displayed quantity rather than the raw value, so a window that draws every frame builds its strings only when a figure actually moves
category: ui
group: draw-lists
topic: draw-a-monospace-text-grid
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksa-deltavmap/features/subway-map-canvas-rendering]
techniques: [imgui-drawlist, imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImString, Brutal.ImGuiApi.ImDrawListPtr]
tags: [text, cache, allocation, string, key, label]
---
Most per-frame text in a panel does not change per frame: a zoom percentage, a "+12 more" count, a countdown that shows minutes. Building the string anyway (an interpolation or a `ToString` plus a concatenation) allocates every frame. KSA-DeltaVMap keeps the last string in a tiny `CachedText` object and rebuilds it only when a key derived from the displayed quantity changes. The important design choice is the key: it is the rounded number the player sees, not the raw double. Demonstrated by [Pannable, zoomable ImGui canvas that draws the subway map with draw-list primitives](../../../../mods/ksa-deltavmap/features/subway-map-canvas-rendering.md). Prerequisite: a window that draws every frame (see [Organise a mod window: tabs, button tabs, tables and pop-out sections](../../../../howto/ui/windows/build-a-tabbed-imgui-window/overview.md)).

## Background: how the game does it

ImGui text calls accept `ImString`, which is implicitly built from a `string` ([current/decomp/Brutal.ImGuiApi/ImString.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImString.cs#L104)[^brutal-imguiapi-imstring]) by encoding it; holding on to the same `string` instance is therefore enough to avoid garbage from the formatting step. (Encoding to UTF-8 happens inside the call. Approach 3, [stackalloc with SpanBuilder](draw-a-monospace-text-grid-3.md), avoids that too.) The game itself formats its FPS counter without strings; see approach 3.

## Lifecycle and timing

`CachedText` is plain managed state that lives with the window object and is read and written from the UI thread inside `frame:draw-ui` (the window's `OnDrawUi` through `[StarMapAfterGui]`). It has no thread affinity of its own, so do not share one instance with another thread. See [Game loop and mod lifecycle](../../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Write the cache.** `owner` is compared by reference and `key` by value; a caller builds the text only on the stale path:

```csharp
// DeltaVMap/Render/CachedText.cs L1-L36
namespace DeltaVMap.Render;

// A display string kept until what it shows changes, so a panel that draws every frame builds
// its text only when a figure moves. owner is compared by reference and key by value; a
// caller builds the text only on the stale path:
//   string s = cache.IsStale(owner, key) ? cache.Set(owner, key, Build()) : cache.Text;
internal sealed class CachedText
{
    private object? _owner;
    private long _key;
    private bool _valid;

    public string Text { get; private set; } = "";

    public bool IsStale(object? owner, long key)
    {
        return !_valid || key != _key || !ReferenceEquals(owner, _owner);
    }

    public string Set(object? owner, long key, string text)
    {
        _owner = owner;
        _key = key;
        _valid = true;
        Text = text;
        return text;
    }

    public void Clear()
    {
        _valid = false;
        _owner = null;
        Text = "";
    }
}

```

2. **Declare one cache per label** on the window (or per row for a list):

```csharp
// DeltaVMap/Render/MapWindow.cs L170-L171
private readonly CachedText _zoomText = new();
private readonly CachedText _moreText = new();
```

3. **Use the stale-check idiom at the call site.** The zoom percentage is keyed by the rounded percent, so dragging the zoom smoothly allocates one string per percent, not per frame:

```csharp
// DeltaVMap/Render/MapWindow.cs L304-L309
    ZoomAboutCenter(1.0 / 1.25);
ImGui.SameLine(0f, 6f);
long percent = (long)Math.Round(_zoom * 100.0);
ImGui.Text(_zoomText.IsStale(null, percent)
    ? _zoomText.Set(null, percent, percent.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%")
    : _zoomText.Text);
```

4. **Choose the key from what is shown.** For a countdown shown as minutes, hours, days or years, `Format.DurationKey` returns a key that changes exactly when `Duration` would show another string; several values can be packed into one `long` with shifts:

```csharp
// DeltaVMap/Core/Format.cs L63-L81
// A key that changes exactly when Duration shows another string.
public static long DurationKey(double seconds)
{
    if (!double.IsFinite(seconds))
        return long.MinValue;
    if (seconds < 3600.0)
        return Rounded(seconds / 60.0);
    if (seconds < 86400.0)
        return 1L << 56 | Rounded(seconds / 3600.0 * 10.0);
    double days = seconds / 86400.0;
    if (days < JulianYearDays)
        return 2L << 56 | Rounded(days * 10.0);
    double years = days / JulianYearDays;
    if (years < 1000.0)
        return 3L << 56 | Rounded(years * 100.0);
    return 4L << 56 | Rounded(years);
}

private static long Rounded(double value)
```

```csharp
// DeltaVMap/Render/TransferWindowRenderer.cs L296-L303
long decodeKey = Format.DurationKey(emphasisInfo.TimeToWindowSeconds)
    ^ (RoundedDegrees(emphasisInfo.CurrentPhaseAngle) << 44)
    ^ ((long)side << 24);
string decode = _decode.IsStale(emphasisInfo, decodeKey)
    ? _decode.Set(emphasisInfo, decodeKey, BuildDecode(emphasisInfo, side))
    : _decode.Text;
var decodePos = new float2(region.X + 4f, region.Y + side - 17f);
var decodeShadow = new float2(decodePos.X + 1f, decodePos.Y + 1f);
```

5. **Use `owner` when the same cache serves different items.** A tooltip or row cache keyed on the hovered item rebuilds when the item changes; `ReferenceEquals(owner, _owner)` handles that without comparing the contents.

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Render/CachedText.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/CachedText.cs) | The cache |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L304-L309) | Zoom percentage in the menu bar |
| [DeltaVMap/Render/TransferWindowRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/TransferWindowRenderer.cs#L40-L60) | Several caches per row |
| [DeltaVMap/Render/TooltipRenderer.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/TooltipRenderer.cs#L192-L202) | Tooltip lines rebuilt only when the hovered item or a display setting changes |

## Variations and gotchas

- **A wrong key is a stale label.** If two different texts can share a key, the old one shows; include every input the string depends on, or fold in the culture/format setting.
- **Key by display, not by raw value.** Keying by the raw double defeats the cache because it changes every frame.
- **`ToString(CultureInfo.InvariantCulture)`** keeps strings stable across machines; the mod passes the invariant culture explicitly.
- **Not for per-frame values.** A speed that changes every frame gets a new key every frame; use approach 3 there.
- **Version.** No game types involved.

## See also

- [Overview of per-frame text](overview.md)
- [Approach 1: monospace grid](draw-a-monospace-text-grid-1.md)
- [Approach 3: stackalloc plus SpanBuilder](draw-a-monospace-text-grid-3.md)
- Mod feature: [Pannable, zoomable ImGui canvas that draws the subway map with draw-list primitives](../../../../mods/ksa-deltavmap/features/subway-map-canvas-rendering.md)

[^brutal-imguiapi-imstring]: Brutal.ImGuiApi.ImString (decompiled, build 2026.10.10.5554)
