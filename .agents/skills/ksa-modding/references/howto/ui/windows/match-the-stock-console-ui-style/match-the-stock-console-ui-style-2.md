---
type: How-To
title: "Match the stock UI: a plain ImGui.Begin window plus PushWidgetStyle, with custom tab buttons"
description: Skin a normal saved-placement ImGui window with ConsoleStyle.PushWidgetStyle and ConsoleWidgets, avoiding ConsoleStyle.BeginWindow, and paint tab buttons in the live title-bar colour
category: ui
group: windows
topic: match-the-stock-console-ui-style
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ohmystars/features/modmenu-entry-and-console-style-windows]
techniques: [imgui-window]
hooks: [starmap StarMapBeforeGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.ConsoleStyle, KSA.ConsoleWidgets, KSA.ImGuiDisabledScope, Brutal.ImGuiApi.ImGui]
tags: [console-style, push-widget-style, tabs, window, modmenu, imgui-ini]
---
Oh My Stars has two ordinary ImGui windows ("Oh My Stars" with Settings and Information tabs, and a Star Editor) that look like KSA's own console-styled UI. Rather than use the game's window helper `ConsoleStyle.BeginWindow`, they open a plain `ImGui.Begin` and wrap the content in `ConsoleStyle.PushWidgetStyle()` and
`PopWidgetStyle()`, then draw rows with `ConsoleWidgets`. The reason is persistence and freedom: `BeginWindow` forces `NoSavedSettings` and pins to the main viewport. This article is that recipe plus the hand-painted tab buttons and collapsible sections that the kit does not provide. Demonstrated by OhMyStars. Prerequisite: StarMap.

## Background: how the game does it

- `ConsoleStyle.BeginWindow(id, title, signature, ref open, initialSize, extraFlags, sizeCond, pinToMainViewport = true)` begins a window named `"##" + id` with `NoTitleBar | NoCollapse | NoBackground | NoSavedSettings` plus your flags, a transparent `WindowBg`, constrains the size to the screen, clamps the window into the work area when pinned, and paints the bezel, title strip, title text
  and close button itself. Position and size are therefore never saved to `imgui.ini`.[current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L678-L755)[^ksa-consolestyle]
- `ConsoleStyle.PushWidgetStyle()` is independent of the window helper: it pushes 48 colours, 10 style vars and the label font, and `PopWidgetStyle()` pops exactly that many; it is safe in any window.[current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L575-L650)[^ksa-consolestyle]
- The kit's metrics are public: `ConsoleWidgets.ButtonHorizontalPaddingPx`, `ButtonVerticalPaddingPx`, `ConsoleStyle.FrameU32`, `FrameRoundingPx`, `WindowBorderThicknessPx`, `ButtonHoverLightenU32`, `ApplyAlpha`, `PushLabelFont`/`PopFont`.

## Lifecycle and timing

`[StarMapBeforeGui]` (a Harmony prefix on `Program.OnDrawUiFrame`, `frame:draw-ui`) draws both windows and then persists their open/closed state, so the mod's windows are submitted before the game's alerts, `Popup.DrawAll` and static windows. Both StarMap GUI hooks are inside `if (DrawUI)`, so hiding the HUD hides the windows. The window records its position and size every frame for later
overlay code. ModMenu timing is decided by ModMenu (not in this corpus). See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Begin a plain window**, opaque, with a first-use size so ImGui remembers the rest in `imgui.ini`:

```csharp
// src/OhMyStarsWindow.cs L256-L272
public static void Draw() {
    if(!_showWindow)
        return;

    // ConsoleStyle.BeginWindow forces NoSavedSettings, so a plain ImGui window is used instead to stay
    // resizable/movable/scrollable and to persist position+size in imgui.ini; ConsoleStyle only supplies widget colors.
    ImGui.SetNextWindowSize(DefaultWindowSize, ImGuiCond.FirstUseEver);
    ImGui.SetNextWindowBgAlpha(1f);
    if(!ImGui.Begin("Oh My Stars", ref _showWindow, ImGuiWindowFlags.None)) {
        ImGui.End();
        return;
    }

    _windowPosition = ImGui.GetWindowPos();
    _windowSize = ImGui.GetWindowSize();

    ConsoleStyle.PushWidgetStyle();
```

2. **Push the widget style once for the whole body and pop it on every exit path.** The Information tab returns early, so it pops before it returns:

```csharp
// src/OhMyStarsWindow.cs L272-L290
ConsoleStyle.PushWidgetStyle();

DrawTabButton("Settings", WindowDisplay.Settings);
ImGui.SameLine();
DrawTabButton("Information", WindowDisplay.Information);
ImGui.Separator();

if(_display == WindowDisplay.Information) {
    DrawInformation();
    ConsoleStyle.PopWidgetStyle();
    ImGui.End();
    return;
}
if(_display == WindowDisplay.SpectrumLegend) {
    DrawSpectrumLegend();
    ConsoleStyle.PopWidgetStyle();
    ImGui.End();
    return;
}
```

3. **Draw rows** with `BeginRow`/`Checkbox`/`EndRow` exactly as in [approach 1](match-the-stock-console-ui-style-1.md), `Button`/`PositiveButton` (the "on" state), `SliderFloat`, `Readout`, and `Tooltip` gated on `ConsoleWidgets.RowHovered`; use `ImGuiDisabledScope` (a `using` block) to disable controls:

```csharp
// src/OhMyStarsWindow.cs L292-L306
bool showStarNamesTop = StellariumRenderer.showStarNames;
ConsoleWidgets.BeginRow("Show Star Names");
if(ConsoleWidgets.Checkbox("ShowStarNames", ref showStarNamesTop, pending: false)) {
    StellariumRenderer.showStarNames = showStarNamesTop;
    SaveSettings();
}
ConsoleWidgets.EndRow();

bool showAsterismNamesTop = StellariumRenderer.showAsterismNames;
ConsoleWidgets.BeginRow("Show Constellation Names");
if(ConsoleWidgets.Checkbox("ShowNames", ref showAsterismNamesTop, pending: false)) {
    StellariumRenderer.showAsterismNames = showAsterismNamesTop;
    SaveSettings();
}
ConsoleWidgets.EndRow();
```
```csharp
// src/OhMyStarsWindow.cs L535-L546
private static void DrawDecSpacingButton(int spacingDegrees) {
    string label = spacingDegrees.ToString(CultureInfo.InvariantCulture);
    bool active = StellariumRenderer.decSpacingDegrees == spacingDegrees;
    bool clicked = active
        ? ConsoleWidgets.PositiveButton(label, "DecSpacing" + label, default)
        : ConsoleWidgets.Button(label, "DecSpacing" + label, default);

    if(clicked && !active) {
        StellariumRenderer.decSpacingDegrees = spacingDegrees;
        SaveSettings();
    }
}
```

4. **Hand-paint tab buttons in the live title-bar colour.** An `InvisibleButton` with the same padding as `ConsoleWidgets` buttons, painted with `AddRectFilled` in `ImGuiCol.TitleBgActive` (so the active tab matches the window title bar in any theme), a hover lighten, a border and centred label text:

```csharp
// src/OhMyStarsWindow.cs L680-L711
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

5. **Collapsible sections and a thick rule.** A text button toggles each section; `ImGui.Indent` (not a one-off cursor nudge) indents rows inside because `BeginRow` captures the cursor as the row origin and `EndRow` resets to it:

```csharp
// src/OhMyStarsWindow.cs L519-L531
internal static void DrawThickSeparator() {
    ImDrawListPtr drawList = ImGui.GetWindowDrawList();
    float2 pos = ImGui.GetCursorScreenPos();
    float startX = pos.X + ImGui.GetCursorPosX();
    float endX = startX + ImGui.GetContentRegionAvail().X;
    float y = pos.Y;
    uint color = ConsoleStyle.ApplyAlpha(ConsoleStyle.HairlineU32);

    drawList.AddLine(new float2(startX, y), new float2(endX, y), color, 3f);

    // Reserve the separator's vertical slot so the widgets below start below the rule.
    ImGui.Dummy(new float2(0f, 3f));
}
```
```csharp
// src/OhMyStarsWindow.cs L551-L563
private static void DrawSectionHeader(string title, string helpText, ref bool expanded) {
    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.CalcTextSize("  ").X);

    if(ImGui.Button(expanded ? title + " v" : title + " >")) {
        expanded = !expanded;
    }

    ImGui.SameLine();
    ImGui.Text("(?)");
    if(ImGui.IsItemHovered(ImGuiHoveredFlags.None)) {
        ConsoleWidgets.Tooltip(helpText);
    }
}
```
```csharp
// src/OhMyStarsWindow.cs L575-L585
// Indents every widget row inside an expanded category four text spaces from the left edge.
// ImGui.Indent shifts the cursor origin persistently (unlike a one-off SetCursorPosX), which is
// what makes it stick for ConsoleWidgets.BeginRow rows: BeginRowCore captures the cursor as the
// row origin and EndRow resets back to it, so a pre-BeginRow nudge would only survive one row.
private static float EntryIndentWidth => ImGui.CalcTextSize("    ").X;
private static void BeginEntryIndent() {
    ImGui.Indent(EntryIndentWidth);
}
private static void EndEntryIndent() {
    ImGui.Unindent(EntryIndentWidth);
}
```

6. **Register a ModMenu entry with no dependency**: a public static method on the mod class carrying `[ModMenuEntry("Oh My Stars")]` that draws one `MenuItem` per window. The attribute comes from `ModMenu.Attributes.dll` (committed beside the source because the package is not on nuget.org); how ModMenu discovers the method is in ModMenu itself, not in this corpus:

```csharp
// src/OhMyStarsEntryPoint.cs L34-L49
[ModMenuEntry("Oh My Stars")]
public static void DrawMenu() {
    if(ImGui.MenuItem("Oh My Stars Window")) {
        OhMyStarsWindow.ToggleWindow();
    }
    if(ImGui.MenuItem("Star Editor")) {
        StarsEditWindow.ToggleWindow();
    }
}

[StarMapBeforeGui]
public static void OnBeforeGui(double dt) {
    OhMyStarsWindow.Draw();
    StarsEditWindow.Draw();
    OhMyStarsWindow.PersistWindowOpenStates();
}
```

## Complete example

| File | Role |
|---|---|
| [src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs#L34-L49) | ModMenu entry and window hook |
| [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs) | Settings tab layout, tab buttons, separators, section headers |
| [src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs) | Star Editor layout |

## Variations and gotchas

- **`ConsoleStyle.BeginWindow` is optional.** It gives the full console chrome but forces `NoSavedSettings` and (by default) pins to the main viewport; a plain `Begin` keeps resize, move, scroll and `imgui.ini` placement.
- **Push and pop must balance.** `PopWidgetStyle` pops a fixed 10 vars and 48 colours; an early `return` between push and pop would unbalance the stack for the rest of the frame.
- **`ConsoleWidgets.BeginRow` captures the cursor** as the row origin; use `ImGui.Indent` for persistent indentation.
- **Modals.** The Star Editor's confirmations subclass the game's `Popup` ([show alerts and game-styled popups](../show-alerts-and-popups-in-game/overview.md)).
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of match-the-stock-console-ui-style](overview.md)
- [Approach 1: ConsoleWidgets rows inside an ImGuiWindow subclass](match-the-stock-console-ui-style-1.md)
- [Build a tabbed ImGui window](../build-a-tabbed-imgui-window/overview.md)
- [List a mod in ModMenu with the ModMenuEntry attribute](../../menus/list-a-mod-in-modmenu/overview.md)
- Mod feature: [ModMenu entry and console-style windows](../../../../mods/ohmystars/features/modmenu-entry-and-console-style-windows.md)

[^ksa-consolestyle]: KSA.ConsoleStyle (decompiled, build 2026.10.10.5554)
