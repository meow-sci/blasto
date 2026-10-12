---
type: How-To
title: "Copy to the clipboard from ImGui: selectable rows that build text only on click"
description: Copy a list row to the system clipboard with ImGui.SetClipboardText, building the text only when the row is clicked and showing a hover tooltip
category: ui
group: input
topic: read-typed-text-and-key-chords-from-imgui
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksa-measuretools/features/measure-menu-and-tool-window]
techniques: [imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, KSA.ImGuiBackendGlfwImpl, KSA.DistanceReference]
tags: [clipboard, copy, selectable, table, tooltip, imgui]
---
KSA-MeasureTools lists the player's measurements in a table. Clicking a row copies the full data line to the system clipboard, and hovering shows a "Click to copy" tooltip. This is the
write-only side of clipboard use: no key handling, no paste, just `ImGui.Selectable` plus `ImGui.SetClipboardText`. The detail worth copying is that the text is built **only on the click**, not
every frame. Demonstrated by KSA-MeasureTools.

## Background: how the game does it

- `ImGui.SetClipboardText(ImString)` and `ImGui.GetClipboardText()` are wrapped in Brutal's ImGui API.[current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8294-L8301)[^brutal-imguiapi-imgui]
- KSA's GLFW backend installs the platform clipboard callbacks, so `SetClipboardText` ends in `Glfw.SetClipboard(...)` and the clipboard is the operating system's.[current/decomp/KSA/ImGuiBackendGlfwImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendGlfwImpl.cs#L410-L424)[^ksa-imguibackendglfwimpl]
- Stock unit formatters such as `DistanceReference.ToNearest` give strings that match the rest of the UI.

## Lifecycle and timing

The window is drawn from `[StarMapAfterGui]` (`frame:draw-ui`); `Selectable` returns true on the frame the row is clicked, and the copy happens in that same frame. Hover state is gathered in the same pass
and the overlay drawn right after reads it. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Make the cells `Selectable`s** inside an `ImGui.BeginTable`. Right-align a numeric cell by pushing `ImGuiStyleVar.SelectableTextAlign` around it. Either cell's click copies the row:

```csharp
// MeasureTools/Features/Measure/MeasureWindow.cs L481-L495
ImGui.TableNextColumn();
string value = FormatValue(m, buffer);
string endpoints = m.Anchors.Length == 2
    ? m.Anchors[0].Label + " - " + m.Anchors[1].Label
    : m.Anchors[0].Label + " - " + m.Anchors[1].Label + " - " + m.Anchors[2].Label;

// Right-aligned value cell.
ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new float2(1f, 0f));
bool copy = ImGui.Selectable(value);
ImGui.PopStyleVar();
hovered |= CopyTooltipOnHover(endpoints);

ImGui.TableNextColumn();
copy |= ImGui.Selectable(endpoints);
hovered |= CopyTooltipOnHover(endpoints);
```

2. **Build the text only when needed.** The full data set resolves anchors and concatenates several strings, which is waste on the frames where no row is clicked:

```csharp
// MeasureTools/Features/Measure/MeasureWindow.cs L497-L506
if (copy)
{
    // Built only on the click: the full data set resolves anchors and
    // concatenates several strings, waste on the 99.9% of frames
    // where no row is clicked.
    string copyText = BuildCopyText(m, value, endpoints, buffer);
    ImGui.SetClipboardText(copyText);
    if (DebugConfig.Measure)
        DefaultCategory.Log.Debug($"[MeasureTools] Copied measurement #{i + 1} to clipboard: {copyText}");
}
```

3. **Compose the text from stock formatters**, richer than the visible cell (arm lengths for an angle, chord and bearing for a surface measurement, radius and circumference for a circle):

```csharp
// MeasureTools/Features/Measure/MeasureWindow.cs L551-L555,L562-L568,L581-L584
private static string BuildCopyText(Measurement m, string value, string endpoints, Span<char> buffer)
{
    switch (m.Mode)
    {
        case MeasureMode.Angle:
// ...
        case MeasureMode.Surface:
        {
            string chord = new string(DistanceReference.ToNearest(m.DistanceMeters(), buffer));
            return value + ", chord " + chord + ", bearing "
                + m.BearingDegrees().ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                + " deg  (" + endpoints + ")";
        }
// ...
        default:
            return value + "  (" + endpoints + ")";
    }
}
```

4. **Tell the player a click copies.** A tooltip on hover with the full text (the column truncates long part names):

```csharp
// MeasureTools/Features/Measure/MeasureWindow.cs L520-L528
// Tooltip for the last drawn item; returns whether it was hovered. Shows the
// full endpoints text, since the Points column truncates long part names.
private static bool CopyTooltipOnHover(string endpoints)
{
    if (!ImGui.IsItemHovered())
        return false;
    ImGuiHelper.DrawTooltip(endpoints + "\nClick to copy");
    return true;
}
```

## Complete example

| File | Role |
|---|---|
| [MeasureTools/Features/Measure/MeasureWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureWindow.cs#L434-L584) | List, hover sync, copy text |

## Variations and gotchas

- **Copy happens on click, not on release.** `Selectable` returns true on the click edge.
- **Pasting** needs `ImGui.GetClipboardText()`; for a paste chord in a text-input surface see [approach 1](read-typed-text-and-key-chords-from-imgui-1.md).
- **The text is not normalised.** Numbers use the game's formatters (current unit and precision), so the copy changes with the player's units.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of read-typed-text-and-key-chords-from-imgui](overview.md)
- [Approach 1: character queue versus IsKeyPressed, with paste chords](read-typed-text-and-key-chords-from-imgui-1.md)
- [Make mod UI look like the game: console widgets, window skin and global theme](../../windows/match-the-stock-console-ui-style/overview.md)
- Mod feature: [Measure menu and tool window](../../../../mods/ksa-measuretools/features/measure-menu-and-tool-window.md)

[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^ksa-imguibackendglfwimpl]: KSA.ImGuiBackendGlfwImpl (decompiled, build 2026.10.10.5554)
