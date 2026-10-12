---
type: How-To
title: "Build a draw-list widget kit: rows, gauges, toggles and sections with a runtime-editable theme"
description: Write a small PanelKit whose widgets take a draw list, an origin, a width and a running y, hit-test with InvisibleButton, reserve size with Dummy and read colours from public static theme fields
category: ui
group: draw-lists
topic: build-custom-drawn-imgui-widgets
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/kitten-engineer-redux/features/draw-list-widget-kit]
techniques: [imgui-drawlist, harmony-postfix]
hooks: [harmony-postfix KSA.Vehicle.OnDrawUi, harmony-postfix KSA.VehicleEditor.OnDrawUi]
lifecycle: [frame:draw-ui, editor:draw-ui]
game_types:
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImDrawListPtr
  - Brutal.ImGuiApi.ImDrawListExtensions
  - KSA.ViewportBase
tags: [widgets, dashboard, theme, invisible-button, dummy, sections]
---
Both Kitten-Engineer-Redux (KER) windows look like a dashboard rather than stock ImGui: label/value rows with separators, rounded tiles, fuel gauges, segmented toggles, chips and mini-buttons, all in a palette the player can edit while the game runs. None of them is an ImGui widget. `PanelKit` draws each element with `ImDrawListPtr` primitives at an explicit screen position and returns the next y coordinate; interactive elements put an `InvisibleButton` over the drawn shape. `SectionHost` adds collapsible bars whose content can pop out into a separate window. Prerequisite: a draw list to draw on (KER gets one from a Harmony postfix; any `[StarMapAfterGui]` window works). Demonstrated by [Draw-list widget kit with pop-out sections](../../../../mods/kitten-engineer-redux/features/draw-list-widget-kit.md).

## Background: how the game does it

The game has no dashboard kit; its own windows use stock ImGui and the console helpers in `ConsoleWidgets`/`ConsoleStyle` (see [Make mod UI look like the game: console widgets, window skin and global theme](../../../../howto/ui/windows/match-the-stock-console-ui-style/overview.md)), and a few draw-list helpers in `ImGuiHelper` (coloured `DrawButton`, `DrawTextOnScreen`; [current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L440-L474)[^ksa-imguihelper]). A custom kit therefore rests on the Brutal ImGui API only: `InvisibleButton(ImString, in float2, ImGuiButtonFlags)` returns true on click ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L6255-L6262)[^brutal-imguiapi-imgui]), `Dummy(in float2)` reserves layout ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L6090-L6095)[^brutal-imguiapi-imgui]), and `ImDrawListExtensions` supplies the drawing ([current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L51-L81)[^brutal-imguiapi-imdrawlistextensions]).

## Lifecycle and timing

KER draws from two postfixes, `Vehicle.OnDrawUi` for the flight HUD and `VehicleEditor.OnDrawUi` for the editor panel, so everything is `frame:draw-ui` (the editor one is `editor:draw-ui`), between `ImGui.NewFrame` and `ImGui.Render` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2262-L2286)[^ksa-program]). Popped-out sections are separate `ImGui.Begin` calls made after the parent window's `End`, inside the same postfix, so they share the parent's frame data and vanish with it. Hit-testing happens during the draw: `DrawDocked` applies a bar click before deciding whether to draw the content, so the change shows in the same frame. See [Game loop and mod lifecycle](../../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Keep the theme in public static fields.** Fourteen `float4` colours, a corner radius and a text scale; anything can read them and the settings popup can edit them live:

```csharp
// PanelKit.cs L9-L29
public static float RowHeight => Math.Max(22f, ImGui.GetTextLineHeight() + 6f);

public static readonly float2 MinWindowSize = new(240f, 140f);
public static readonly float2 MaxWindowSize = new(1600f, 4000f);

public static float4 WindowBgColor = new(0.07f, 0.08f, 0.10f, 0.96f);
public static float4 BorderColor = new(0.22f, 0.24f, 0.28f, 1f);
public static float4 HeaderColor = new(0.55f, 0.78f, 0.95f, 1f);
public static float4 LabelColor = new(0.72f, 0.74f, 0.78f, 1f);
public static float4 ValueColor = new(0.95f, 0.96f, 0.98f, 1f);
public static float4 AccentColor = new(0.30f, 0.62f, 0.90f, 1f);
public static float4 ToggleOffColor = new(0.16f, 0.17f, 0.20f, 1f);
public static float4 WarningColor = new(0.92f, 0.45f, 0.30f, 1f);
public static float4 GoodColor = new(0.45f, 0.85f, 0.55f, 1f);
public static float4 RowSeparatorColor = new(0.22f, 0.24f, 0.28f, 0.35f);
public static float4 HighlightColor = new(0.30f, 0.62f, 0.90f, 0.22f);
public static float4 GraphBgColor = new(0.04f, 0.05f, 0.07f, 1f);
public static float4 GraphLineColor = new(0.45f, 0.85f, 0.55f, 1f);
public static float4 GraphGridColor = new(0.22f, 0.24f, 0.28f, 0.6f);
public static float CornerRadius = 4f;
public static float TextScale = 1f;
```

2. **Wrap the window.** `BeginWindow` sets first-use placement, pushes rounding and colour overrides, calls `ImGui.Begin` and scales the font; `EndContent` is what makes layout work, resetting the cursor to the origin and submitting a `Dummy` of the height the widgets consumed:

```csharp
// PanelKit.cs L33-L65
public static bool BeginWindow(ReadOnlySpan<byte> title, float2 defaultPos, float2 defaultSize)
{
    ImGui.SetNextWindowPos(defaultPos, ImGuiCond.FirstUseEver, null);
    ImGui.SetNextWindowSize(defaultSize, ImGuiCond.FirstUseEver);
    ImGui.SetNextWindowSizeConstraints(MinWindowSize, MaxWindowSize, (ImGuiSizeCallback?)null);

    ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, CornerRadius);
    ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, CornerRadius);
    ImGui.PushStyleColor(ImGuiCol.WindowBg, ToColor(WindowBgColor));
    ImGui.PushStyleColor(ImGuiCol.Border, ToColor(BorderColor));
    ImGui.PushStyleColor(ImGuiCol.TitleBg, ToColor(WindowBgColor));
    ImGui.PushStyleColor(ImGuiCol.TitleBgActive, ToColor(WindowBgColor));
    ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, ToColor(WindowBgColor));

    bool open = ImGui.Begin(title, ImGuiWindowFlags.None);
    _titleBarHeight = ImGui.GetFrameHeight();
    ImGui.PushFont(ImGui.GetFont(), ImGui.GetFontSize() * TextScale);
    return open;
}

public static void EndWindow()
{
    ImGui.PopFont();
    ImGui.End();
    ImGui.PopStyleColor(5);
    ImGui.PopStyleVar(2);
}

public static void EndContent(float2 origin, float width, float y)
{
    ImGui.SetCursorScreenPos(origin);
    ImGui.Dummy(new float2(width, Math.Max(0f, y - origin.Y)));
}
```

3. **Give every widget the same signature**: `(dl, origin, width, y, ...)` returning the new `y`. A row, for example, right-aligns the value, wraps to two lines when label and value do not fit, and ends with a separator:

```csharp
// PanelKit.cs L109-L143
public static float DrawRow(ImDrawListPtr dl, float2 origin, float width, float y, string label, string value, bool warning = false)
{
    float lineHeight = ImGui.GetTextLineHeight();
    float rowHeight = RowHeight;
    float availableWidth = Math.Max(0f, width - 8f);
    float2 labelSize = ImGui.CalcTextSize(label);
    float2 valueSize = ImGui.CalcTextSize(value);
    float gap = ImGui.GetStyle().ItemSpacing.X;
    ImColor8 valueColor = ToColor(warning ? WarningColor : ValueColor);

    float height;
    if (labelSize.X + valueSize.X + gap <= availableWidth)
    {
        float textY = y + (rowHeight - lineHeight) * 0.5f;
        float2 labelPos = new float2(origin.X, textY);
        dl.AddText(in labelPos, ToColor(LabelColor), label);
        float2 valuePos = new float2(origin.X + width - valueSize.X, textY);
        dl.AddText(in valuePos, valueColor, value);
        height = rowHeight;
    }
    else
    {
        float2 labelPos = new float2(origin.X, y + 2f);
        dl.AddText(in labelPos, ToColor(LabelColor), label);
        float valueX = Math.Max(origin.X, origin.X + width - valueSize.X);
        float2 valuePos = new float2(valueX, y + 2f + lineHeight + 2f);
        dl.AddText(in valuePos, valueColor, value);
        height = lineHeight * 2f + 8f;
    }

    float2 sepStart = new float2(origin.X, y + height - 1f);
    float2 sepEnd = new float2(origin.X + width, y + height - 1f);
    dl.AddLine(in sepStart, in sepEnd, ToColor(RowSeparatorColor));
    return y + height;
}
```

4. **Draw bars as two rounded rectangles.** A gauge is a label, a value, a track and a fill clamped to `[0, 1]` (NaN guarded), and switches to the warning colour when asked:

```csharp
// PanelKit.cs L145-L168
public static float DrawGauge(ImDrawListPtr dl, float2 origin, float width, float y,
    string label, float fraction, string valueText, bool warning)
{
    float lineHeight = ImGui.GetTextLineHeight();
    float clamped = Math.Clamp(float.IsNaN(fraction) ? 0f : fraction, 0f, 1f);

    float2 labelPos = new float2(origin.X, y + 2f);
    dl.AddText(in labelPos, ToColor(LabelColor), label);
    float2 valueSize = ImGui.CalcTextSize(valueText);
    float2 valuePos = new float2(Math.Max(origin.X, origin.X + width - valueSize.X), y + 2f);
    dl.AddText(in valuePos, ToColor(warning ? WarningColor : ValueColor), valueText);

    float barTop = y + 2f + lineHeight + 3f;
    float barHeight = 8f;
    float2 trackMin = new float2(origin.X, barTop);
    float2 trackMax = new float2(origin.X + width, barTop + barHeight);
    dl.AddRectFilled(in trackMin, in trackMax, ToColor(GraphBgColor), CornerRadius);
    if (clamped > 0.002f)
    {
        float2 fillMax = new float2(origin.X + width * clamped, barTop + barHeight);
        dl.AddRectFilled(in trackMin, in fillMax, ToColor(warning ? WarningColor : GoodColor), CornerRadius);
    }
    return barTop + barHeight + 8f;
}
```

5. **Make interactive shapes with `InvisibleButton`.** Position the cursor, submit the button, then paint the background according to state. The return value is the click:

```csharp
// PanelKit.cs L170-L180
public static bool DrawMiniButton(ImDrawListPtr dl, string label, string id, float2 pos, float width, float height, bool active = false)
{
    ImGui.SetCursorScreenPos(pos);
    bool clicked = ImGui.InvisibleButton(id, new float2(width, height));
    float2 max = new float2(pos.X + width, pos.Y + height);
    dl.AddRectFilled(in pos, in max, ToColor(active ? AccentColor : ToggleOffColor), CornerRadius);
    float2 textSize = ImGui.CalcTextSize(label);
    float2 textPos = new float2(pos.X + (width - textSize.X) * 0.5f, pos.Y + (height - textSize.Y) * 0.5f);
    dl.AddText(in textPos, ToColor(ValueColor), label);
    return clicked;
}
```

A segmented two-way toggle is two of these side by side with the active half painted in the accent colour ([PanelKit.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/PanelKit.cs#L348-L378)).

6. **Mix in real ImGui controls where they are better.** `DrawSliderRow` and `DrawInputDoubleRow` draw their label on the draw list, then place a real `SliderFloat` or `InputDouble` with `SetCursorScreenPos` and `SetNextItemWidth`, so text editing and dragging keep ImGui's behaviour ([PanelKit.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/PanelKit.cs#L295-L329)).

7. **Make the theme editable.** The settings popup exposes each field with `ColorEdit4` and `SliderFloat`, with one reset button:

```csharp
// PanelKit.cs L397-L424
public static void DrawAppearanceSettings()
{
    ImGui.Text("Panel colors"u8);
    ImGui.ColorEdit4("Background"u8, ref WindowBgColor, ImGuiColorEditFlags.NoInputs);
    ImGui.ColorEdit4("Accent"u8, ref AccentColor, ImGuiColorEditFlags.NoInputs);
    ImGui.ColorEdit4("Border"u8, ref BorderColor, ImGuiColorEditFlags.NoInputs);
    ImGui.ColorEdit4("Tile and button background"u8, ref ToggleOffColor, ImGuiColorEditFlags.NoInputs);
    ImGui.ColorEdit4("Row separator"u8, ref RowSeparatorColor, ImGuiColorEditFlags.NoInputs);
    ImGui.ColorEdit4("Active stage highlight"u8, ref HighlightColor, ImGuiColorEditFlags.NoInputs);

    ImGui.Text("Text colors"u8);
    ImGui.ColorEdit4("Header text"u8, ref HeaderColor, ImGuiColorEditFlags.NoInputs);
    ImGui.ColorEdit4("Label text"u8, ref LabelColor, ImGuiColorEditFlags.NoInputs);
    ImGui.ColorEdit4("Value text"u8, ref ValueColor, ImGuiColorEditFlags.NoInputs);
    ImGui.ColorEdit4("Warning and alert"u8, ref WarningColor, ImGuiColorEditFlags.NoInputs);
    ImGui.ColorEdit4("Good and fuel gauge"u8, ref GoodColor, ImGuiColorEditFlags.NoInputs);

    ImGui.Text("Graph colors"u8);
    ImGui.ColorEdit4("Graph background"u8, ref GraphBgColor, ImGuiColorEditFlags.NoInputs);
    ImGui.ColorEdit4("Graph line"u8, ref GraphLineColor, ImGuiColorEditFlags.NoInputs);
    ImGui.ColorEdit4("Graph grid"u8, ref GraphGridColor, ImGuiColorEditFlags.NoInputs);

    ImGui.Text("Layout"u8);
    ImGui.SliderFloat("Rounded corners"u8, ref CornerRadius, 0f, 12f, "%.0f px");
    ImGui.SliderFloat("Text scale"u8, ref TextScale, 0.75f, 1.5f, "%.2f x");
    if (ImGui.Button("Reset appearance"u8, (float2?)null))
        ResetAppearance();
}
```

8. **Build sections from the same signature.** A `HudSection` holds an id, a title and a `SectionDraw` delegate with the widget shape; its `###` window title is built once. Docked, it draws a bar and inline content; popped, it opens a window of its own:

```csharp
// SectionHost.cs L8-L29
internal delegate float SectionDraw(ImDrawListPtr dl, float2 origin, float width, float y);

internal sealed class HudSection
{
    public HudSection(string id, string title, SectionDraw draw)
    {
        Id = id;
        Title = title;
        PoppedTitle = $"{title} (separate window)";
        Draw = draw;
        WindowTitle = Encoding.UTF8.GetBytes($"{title}###KerSection_{id}");
        SectionRegistry.Register(this);
    }

    public string Id { get; }
    public string Title { get; }
    public string PoppedTitle { get; }
    public SectionDraw Draw { get; }
    public byte[] WindowTitle { get; }
    public bool Open { get; set; }
    public bool Popped { get; set; }
}
```

```csharp
// SectionHost.cs L47-L86
public static float DrawDocked(HudSection section, ImDrawListPtr dl, float2 origin, float width, float y)
{
    y = DrawBar(dl, origin, width, y, section, false, out bool toggleOpen, out bool togglePopped);
    if (toggleOpen)
        section.Open = !section.Open;
    if (togglePopped)
        section.Popped = !section.Popped;

    if (section.Open && !section.Popped)
    {
        y = section.Draw(dl, origin, width, y);
        y += 4f;
    }
    return y;
}

public static void DrawFloating(HudSection section, GameViewport viewport, int index)
{
    if (!section.Popped)
        return;

    float2 defaultPos = viewport.Position + new float2(380f + index * 28f, 80f + index * 28f);
    bool open = PanelKit.BeginWindow(section.WindowTitle, defaultPos, new float2(FloatingWidth, FloatingHeight));
    if (open)
    {
        float width = ImGui.GetContentRegionAvail().X;
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        float2 origin = ImGui.GetCursorScreenPos();
        PanelKit.DrawAccentStrip(dl, origin, ImGui.GetWindowSize().Y);

        float y = origin.Y;
        y = DrawBar(dl, origin, width, y, section, true, out _, out bool dock);
        if (dock)
            section.Popped = false;

        y = section.Draw(dl, origin, width, y);
        PanelKit.EndContent(origin, width, y);
    }
    PanelKit.EndWindow();
}
```

## Complete example

| File | Role |
|---|---|
| [PanelKit.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/PanelKit.cs) | Window frame, widgets, formatters, theme and appearance settings |
| [SectionHost.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SectionHost.cs) | `HudSection`, `SectionRegistry`, docked and floating hosts |
| [StageListView.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/StageListView.cs) | Composite example: clickable stage rows with expandable details and fuel bars |
| [FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L155-L195) | One window using the kit: window, settings popup, banner, tiles, sections, `EndContent` |

## Variations and gotchas

- **Always end with `EndContent` (or your own `Dummy`).** Without it the window thinks it holds no content and cannot size or scroll.
- **IDs must be unique.** Each `InvisibleButton` needs its own string id (KER uses `##Ker...` names or `idPrefix + i`); loops of identical widgets should also use `PushID`.
- **Text scale is yours.** KER multiplies `GetFontSize()` by `TextScale` through `PushFont(font, size)`; row heights come from `GetTextLineHeight()` so they follow.
- **Manual `y` bookkeeping.** Any mistake in a returned height shows as overlapping rows, not an exception.
- **Hover is same-frame in your own shapes**: you paint after `InvisibleButton`, so `IsItemHovered`/`IsItemActive` already answer for this frame (the ImGui item is the last submitted); painting before the button would have to use last frame's state.
- **Version.** The kit uses only Brutal ImGui calls; a change of their signatures (`ImColor8`, `ImString`) is what would break it on a game update.

## See also

- [Overview of custom-drawn widgets](overview.md)
- [Approach 5: alert rules driving tiles and banners](build-custom-drawn-imgui-widgets-5.md)
- [Approach 2: icon buttons](build-custom-drawn-imgui-widgets-2.md)
- [Draw charts, scopes and relief maps with the ImGui draw list](../../../../howto/ui/draw-lists/draw-a-chart-with-imdrawlist/overview.md)
- [Make mod UI look like the game: console widgets, window skin and global theme](../../../../howto/ui/windows/match-the-stock-console-ui-style/overview.md)
- Mod feature: [Draw-list widget kit with pop-out sections](../../../../mods/kitten-engineer-redux/features/draw-list-widget-kit.md)

[^ksa-imguihelper]: KSA.ImGuiHelper (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imdrawlistextensions]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
