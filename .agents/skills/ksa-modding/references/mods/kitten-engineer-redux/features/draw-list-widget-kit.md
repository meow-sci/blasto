---
type: Mod Feature
title: Draw-list widget kit with pop-out sections
description: A small immediate-mode widget library that lays out rows, tiles, gauges, toggles and buttons by hand on the window draw list with InvisibleButton hit-testing and a running y cursor, plus collapsible sections that can be popped out into their own ImGui windows and docked back
mod: kitten-engineer-redux
category: ui
techniques: [imgui-drawlist, imgui-window]
hooks: [harmony-postfix KSA.Vehicle.OnDrawUi, harmony-postfix KSA.VehicleEditor.OnDrawUi]
lifecycle: [frame:draw-ui, editor:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImDrawListPtr, KSA.GameViewport]
tags: [imgui, drawlist, widgets, layout, theming, pop-out, windows, invisible-button]
---

Both KER windows look like a dashboard rather than stock ImGui: label/value rows with separators,
rounded tiles, fuel gauges, segmented toggles, chips and mini-buttons, all in a user-themable
palette. They are not ImGui widgets. `PanelKit` draws each element with `ImDrawListPtr` primitives at
an explicit screen position and returns the next y coordinate; interactive elements place an
`ImGui.InvisibleButton` over the drawn shape for hit-testing. `SectionHost` adds collapsible section
bars whose content can be moved into a separate window with one click.

## How it works

**Window frame** ([PanelKit.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/PanelKit.cs#L33-L65)):[^ker-panelkit]

- `BeginWindow` sets `FirstUseEver` position and size, size constraints, pushes rounding and five
  colour overrides, calls `ImGui.Begin`, records the title bar height and pushes the current font at
  `GetFontSize() * TextScale`. `EndWindow` pops in reverse.
- Callers then read `ImGui.GetWindowDrawList()` and `GetCursorScreenPos()` as the origin, draw with a
  running `y`, and finish with `EndContent`, which resets the cursor to the origin and submits an
  `ImGui.Dummy(width, y - origin.Y)` so ImGui knows the content height for scrolling and auto-size.
- `DrawAccentStrip` draws a 3 px accent bar down the window's left edge under the title bar, clipped
  with `PushClipRect`.

**Widgets** (each takes `dl, origin, width, y` and returns the new `y`):

| Widget | Technique |
|---|---|
| `DrawRow` | label left, value right-aligned; wraps to two lines when they do not fit; 1 px separator |
| `DrawStatTiles` | grid of rounded rectangles, label plus larger value via `PushFont(font, size)` |
| `DrawGauge` | label, value text and a rounded progress bar that turns warning-coloured below a threshold |
| `DrawTwoWayToggle` | two `InvisibleButton`s side by side with filled backgrounds for a segmented control |
| `DrawMiniButton`, `DrawChips` | `InvisibleButton` hit area plus drawn background and centred label |
| `DrawSliderRow`, `DrawInputDoubleRow` | drawn label plus a real `SliderFloat` / `InputDouble` positioned with `SetCursorScreenPos` |
| `DrawTableRow`, `DrawTotalRow` | fixed column fractions, right-aligned numeric cells |

**Theme.** Fourteen `float4` colours, corner radius and text scale are public static fields, edited
live in the HUD's Settings popup with `ImGui.ColorEdit4` and `SliderFloat`, reset with one button,
and saved by the settings store ([PanelKit.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/PanelKit.cs#L397-L444)).[^ker-settingsstore]

**Sections** ([SectionHost.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SectionHost.cs)):[^ker-sectionhost]

- A `HudSection` holds an id, title, a `SectionDraw` delegate with the same `(dl, origin, width, y)`
  shape as the widgets, and `Open` / `Popped` flags. Its constructor builds a stable window title
  `"{title}###KerSection_{id}"` once and registers itself in `SectionRegistry`, which immediately
  applies any saved state.
- `DrawDocked` draws the bar (an `InvisibleButton` over the bar toggles `Open`, a second one over the
  "Pop out" button toggles `Popped`) and, if open and not popped, calls the delegate inline.
- `DrawFloating` is called for every section after the main window has ended; for popped sections it
  opens a separate `PanelKit` window, cascaded 28 px per section index, whose bar shows "Dock".
- The flight HUD defines nine sections, the editor panel one; their callbacks read cached frame data,
  so the same delegate renders identically in either host.

## Lifecycle and timing

- All of it runs inside the two Harmony postfixes (`Vehicle.OnDrawUi` and `VehicleEditor.OnDrawUi`),
  in `frame:draw-ui`, between `ImGui.NewFrame` and `ImGui.Render`.
- Popped-out windows are separate top-level `ImGui.Begin` calls made after the parent window's
  `End`, inside the same postfix, so they share the parent's frame data and disappear with it.
- Hit-testing happens during the draw. `DrawDocked` applies a bar click before deciding whether to
  draw the content, so the change shows in the same frame; the widget picker instead records the click
  and applies it after its loop, so the list is never mutated while being iterated.

## Key code

| Path | Role |
|---|---|
| [PanelKit.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/PanelKit.cs) | Window frame, widgets, formatters, theme and appearance settings |
| [SectionHost.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SectionHost.cs) | `HudSection`, `SectionRegistry`, docked and floating hosts |
| [StageListView.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/StageListView.cs) | Example composite: clickable stage rows with expandable details and fuel bars |
| [FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L36-L47) | Section table for the flight HUD |

## Snippets

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

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `ImGui.Begin`, `End`, `SetNextWindowPos/Size/SizeConstraints`, `PushStyleVar/Color` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs) | Window frame and theme |
| `ImGui.InvisibleButton`, `SetCursorScreenPos`, `Dummy`, `CalcTextSize` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs) | Hit-testing and manual layout |
| `ImGui.PushFont(ImFontPtr, float)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5872) | Text scale without extra fonts |
| `ImDrawListPtr.AddText`, `AddLine`, `AddRectFilled`, `PushClipRect` | [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L10-L211) | Drawing and clipping |
| `ViewportBase.Position` (`float2`), `Size` (`int2`) | [current/decomp/KSA/ViewportBase.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportBase.cs#L50-L56) | Default window placement |

The colours are converted with `ImGui.ColorConvertFloat4ToU32`, whose `uint` result converts
implicitly to `ImColor8`.[^brutal-imgui] [^brutal-drawlistext]

## Pitfalls and version notes

- `BeginWindow`/`EndWindow` must always be paired even when `Begin` returns false, because the style
  and font pushes happen before `Begin`; the mod does this correctly.
- Every `InvisibleButton` needs a unique ID within the window; the kit derives them from section ids
  and stage numbers (`##KerStageRow{id}`), which also keeps state stable when rows reorder.
- Drawing directly on the draw list bypasses ImGui's item system: drawn-only elements have no hover,
  focus or keyboard navigation unless you add an item over them.
- Popped windows get only a `FirstUseEver` default position (cascaded by section index; the editor
  panel's single section uses index 0), so after the first use their position is whatever ImGui
  remembers for the `###KerSection_<id>` window name, while the popped state itself is persisted by
  KER's settings store.

## See also

- [Draw-list line graph](draw-list-line-graph.md)
- [HUD stat tiles and threshold alerts](hud-widgets-and-threshold-alerts.md)
- [JSON settings with autosave](json-settings-autosave.md)
- [Flight HUD on the controlled vehicle](flight-hud-controlled-vehicle-postfix.md)
- [Editor panel injected into the vehicle editor](editor-panel-injection.md)
- [Build a draw-list widget kit: rows, gauges, toggles and sections with a runtime-editable theme](../../../howto/ui/draw-lists/build-custom-drawn-imgui-widgets/build-custom-drawn-imgui-widgets-1.md) - how-to article demonstrating this feature
- [Tabbed window: collapsible sections that pop out into their own windows](../../../howto/ui/windows/build-a-tabbed-imgui-window/build-a-tabbed-imgui-window-5.md) - how-to article demonstrating this feature

[^ker-panelkit]: PanelKit.cs
[^ker-settingsstore]: SettingsStore.cs
[^ker-sectionhost]: SectionHost.cs
[^brutal-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^brutal-drawlistext]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
