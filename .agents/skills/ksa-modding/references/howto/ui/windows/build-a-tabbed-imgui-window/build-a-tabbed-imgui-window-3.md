---
type: How-To
title: "Tabbed window: title-bar-coloured custom tab buttons"
description: Draw hand-rolled tab buttons that match the stock console palette and follow the live title-bar colour, instead of using ImGui's tab bar
category: ui
group: windows
topic: build-a-tabbed-imgui-window
approach: 3
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ohmystars/features/modmenu-entry-and-console-style-windows]
techniques: [imgui-window]
hooks: [starmap StarMapBeforeGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, KSA.ConsoleStyle, KSA.ConsoleWidgets]
tags: [tabs, buttons, custom-drawn, console-style, title-bar-color]
---
ImGui's tab bar has its own look. When a window is dressed with the game's console widgets ([match the stock UI](../match-the-stock-console-ui-style/match-the-stock-console-ui-style-2.md)), an ordinary `ConsoleWidgets.Button` row for the tabs matches the other controls, and the *active* tab only needs to stand out. Oh My Stars does exactly that: the inactive tabs are stock buttons and
the active one is a custom-painted button filled with the live window title-bar colour, so it matches the title bar under any theme. Demonstrated by OhMyStars. Prerequisite: StarMap.

## Background: how the game does it

- `ConsoleWidgets.Button(label, id, size)` draws a console-style button; its metrics are public (`ButtonHorizontalPaddingPx`, `ButtonVerticalPaddingPx`), as are `ConsoleStyle.FrameU32`, `FrameRoundingPx`, `WindowBorderThicknessPx`, `ButtonHoverLightenU32`, `ApplyAlpha`, `PushLabelFont`/`PopFont`.[current/decomp/KSA/ConsoleWidgets.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleWidgets.cs#L142-L167)[^ksa-consolewidgets]
- The title-bar colour is an ordinary ImGui style colour, `ImGuiCol.TitleBgActive`, read live with `ImGui.GetStyleColorVec4`, so the button follows any global theme change ([re-theme the whole UI](../match-the-stock-console-ui-style/match-the-stock-console-ui-style-4.md)).

## Lifecycle and timing

Drawn from `[StarMapBeforeGui]` (a Harmony prefix on `Program.OnDrawUiFrame`, `frame:draw-ui`), so the mod's windows are submitted before the game's alerts, popups and static windows; both StarMap GUI hooks are inside `if (DrawUI)`, so the window hides with the HUD. A tab click flips a static field and persists the choice; the new tab draws the same frame because the choice is read after the buttons. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Keep the selection in a field** and draw the buttons side by side with `SameLine()`. The selected tab uses the custom painter, the others the stock button; a click on an unselected tab switches and persists:

```csharp
// src/OhMyStarsWindow.cs L272-L277
ConsoleStyle.PushWidgetStyle();

DrawTabButton("Settings", WindowDisplay.Settings);
ImGui.SameLine();
DrawTabButton("Information", WindowDisplay.Information);
ImGui.Separator();
```
```csharp
// src/OhMyStarsWindow.cs L651-L676
// The active tab is shaded with a filled button in the same color the game uses for ImGui
// window title bars, read live from the ImGui style so it follows any theme change.
private static void DrawTabButton(string label, WindowDisplay tab) {
    bool selected = _display == tab;
    bool clicked = selected
        ? DrawColoredTabButton(label)
        : ConsoleWidgets.Button(label, label, default);

    if(clicked && !selected) {
        _display = tab;
        SaveSettings();
    }
}

// Same layout as DrawTabButton but toggles the Information tab's star selection mode instead
// of the top-level window tab, and isn't persisted to settings.
private static void DrawSelectionModeButton(string label, StarSelectionMode mode) {
    bool selected = _starSelectionMode == mode;
    bool clicked = selected
        ? DrawColoredTabButton(label)
        : ConsoleWidgets.Button(label, label, default);

    if(clicked && !selected) {
        _starSelectionMode = mode;
    }
}
```

2. **Paint the active tab.** Lay out an `InvisibleButton` with the same padding as `ConsoleWidgets` buttons for hit-testing, then draw the fill (the live `TitleBgActive`, forced opaque), a hover lighten, a border and centred label text on the window draw list with the console label font:

```csharp
// src/OhMyStarsWindow.cs L678-L711
// Same layout/rounding/padding as ConsoleWidgets' filled buttons (see PrimaryButton in
// ConsoleWidgets.DrawButtonCore), but filled with the window title bar color and white text.
internal static bool DrawColoredTabButton(string label) {
    ConsoleStyle.PushLabelFont();
    float2 textSize = ImGui.CalcTextSize(label);
    float fontSize = ImGui.GetFontSize();
    ConsoleStyle.PopFont();

    float2 size = new float2(
        textSize.X + ConsoleWidgets.ButtonHorizontalPaddingPx * 2f,
        fontSize + ConsoleWidgets.ButtonVerticalPaddingPx * 2f);
    float2 pMin = ImGui.GetCursorScreenPos();
    float2 pMax = pMin + size;

    bool clicked = ImGui.InvisibleButton(new ImString($"##Tab_{label}"), in size, ImGuiButtonFlags.None);
    bool hovered = ImGui.IsItemHovered();

    float4 titleBarColor = ImGui.GetStyleColorVec4(ImGuiCol.TitleBgActive);
    uint fill = ImGui.ColorConvertFloat4ToU32(new float4(titleBarColor.X, titleBarColor.Y, titleBarColor.Z, 1f));
    uint border = ConsoleStyle.FrameU32;
    uint textColor = ImGui.ColorConvertFloat4ToU32(new float4(1f, 1f, 1f, 1f));

    ImDrawListPtr drawList = ImGui.GetWindowDrawList();
    drawList.AddRectFilled(in pMin, in pMax, ConsoleStyle.ApplyAlpha(fill), ConsoleStyle.FrameRoundingPx);
    if(hovered) {
        drawList.AddRectFilled(in pMin, in pMax, ConsoleStyle.ApplyAlpha(ConsoleStyle.ButtonHoverLightenU32), ConsoleStyle.FrameRoundingPx);
    }
    drawList.AddRect(in pMin, in pMax, ConsoleStyle.ApplyAlpha(border), ConsoleStyle.FrameRoundingPx, ImDrawFlags.None, ConsoleStyle.WindowBorderThicknessPx);

    ConsoleStyle.PushLabelFont();
    drawList.AddText(pMin + (size - textSize) * 0.5f, ConsoleStyle.ApplyAlpha(textColor), label);
    ConsoleStyle.PopFont();
    return clicked;
}
```

3. **Reuse the helper for any exclusive choice.** The Information tab's star selection mode uses the same painter (`DrawSelectionModeButton`).
4. **Separate tabs from content** with a rule (`ImGui.Separator()` here; the mod also paints a thicker 3 px rule for category breaks) and branch on the field to draw the page.

## Complete example

| File | Role |
|---|---|
| [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L256-L290) | Window, tab buttons, page dispatch |
| [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L651-L711) | Tab button helpers |

## Variations and gotchas

- **No keyboard navigation or close buttons**, unlike ImGui tab items; this is just buttons.
- **The label doubles as the widget id**, so tabs need distinct labels or an explicit id.
- **Persisting the tab** (`SaveSettings`) is the mod's choice; the tab does not have to be saved.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of build-a-tabbed-imgui-window](overview.md)
- [Approach 1: closeable ImGui tab bar](build-a-tabbed-imgui-window-1.md)
- [Approach 4: button tabs driving a table of collapsing headers](build-a-tabbed-imgui-window-4.md)
- [Plain ImGui.Begin plus PushWidgetStyle](../match-the-stock-console-ui-style/match-the-stock-console-ui-style-2.md)
- Mod feature: [ModMenu entry and console-style windows](../../../../mods/ohmystars/features/modmenu-entry-and-console-style-windows.md)

[^ksa-consolewidgets]: KSA.ConsoleWidgets (decompiled, build 2026.10.10.5554)
