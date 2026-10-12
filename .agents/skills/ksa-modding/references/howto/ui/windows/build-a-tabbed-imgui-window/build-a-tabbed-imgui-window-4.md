---
type: How-To
title: "Tabbed window: button tabs driving a table of collapsing headers"
description: Build a settings window whose tabs are a row of disabled-when-active buttons under a scrolling child, and whose main page is a three-column table with one collapsing header per effect
category: ui
group: windows
topic: build-a-tabbed-imgui-window
approach: 4
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/mpfx/features/effect-settings-window]
techniques: [imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImGuiTableFlags]
tags: [tabs, table, collapsing-header, settings, buttons, pushid, mpfx]
---
Most of MPFX is one settings window, "MPFX shader menu". A Profiles, Shaders and Export profile tab row sits at the **bottom**; the Shaders page is a three-column table with one collapsing header per effect, and each header holds a "pre imgui" row and a "post imgui" row, each with an enable checkbox, its sliders and a Reset button. The tabs are not ImGui's tab bar: they are three buttons, the active one disabled
so it looks pressed, below a child region that reserves their height. Demonstrated by MPFX. Prerequisite: StarMap.

## Background: how the game does it

Nothing in KSA is involved. The pieces are ImGui: `BeginChild` with a negative height reserves space at the bottom, `BeginDisabled`/`EndDisabled` greys out a block, `BeginTable` with `ImGuiTableFlags.Borders | Resizable` and `TableSetupColumn`, `CollapsingHeader` with `ImGuiTreeNodeFlags.SpanAllColumns`, and `PushID`/`PopID` to give identical labels ("pre imgui", "Reset") distinct ids.[current/decomp/Brutal.ImGuiApi/ImGuiTableFlags.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiTableFlags.cs#L5-L12)[^brutal-imguiapi-imguitableflags]
The window is begun with `NoSavedSettings`, so ImGui does not remember position or size across runs.

## Lifecycle and timing

The window is drawn in the same `[StarMapAfterGui]` call that uploads the parameters (`frame:draw-ui`, a postfix on `Program.OnDrawUiViewports`). ImGui calls are legal because `PrepareImGui` has run and `ImGui.Render` has not. When the HUD is hidden the window disappears with the rest of the UI. Because the post-ImGui effect chain processes the finished swapchain image, the window itself is graded by post-side effects, which makes the pre/post split visible while you tune. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Begin the window** with a size constraint and a first-use position; `ShowWindow` starts `true` (L26), and the ModMenu entry's Open button sets it again after the title-bar close button clears it:

```csharp
// MPFX/MPFX.cs L233-L241
ImGui.SetNextWindowSizeConstraints(
    new float2(650f, 500f),
    new float2(float.MaxValue)
);
ImGui.SetNextWindowPos(
    new float2(renderer.Extent.Width * 0.7f - 325f, 75f),
    ImGuiCond.FirstUseEver
);
if (ImGui.Begin("MPFX shader menu", ref ShowWindow, ImGuiWindowFlags.NoSavedSettings))
```

2. **Draw the current tab's page in a child that reserves the tab-button row**, with a `switch` on a `WindowTabs` enum. `new float2(0f, -32f)` leaves 32 px at the bottom:

```csharp
// MPFX/MPFX.cs L252-L256
ImGui.BeginChild("ContentChild", new float2(0f, -32f), ImGuiChildFlags.AlwaysAutoResize);
switch (CurrentTab)
{
    case WindowTabs.Profiles:
        ImGui.Text("Change/create profile:");
```

3. **Build the effect table.** Three columns (Enabled fixed 150, Config stretch, InputConfig fixed 150), headers row, then per effect a `TableNextRow`, `TableSetColumnIndex(0)` and a `CollapsingHeader(name, SpanAllColumns)`:

```csharp
// MPFX/MPFX.cs L308-L318
case WindowTabs.Shaders:
    ImGui.BeginTable("Shaders", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.Resizable);
    ImGui.TableSetupColumn("Enabled", ImGuiTableColumnFlags.WidthFixed, initWidthOrWeight: 150f);
    ImGui.TableSetupColumn("Config", ImGuiTableColumnFlags.WidthStretch);
    ImGui.TableSetupColumn("InputConfig", ImGuiTableColumnFlags.WidthFixed, initWidthOrWeight: 150f);
    ImGui.TableHeadersRow();

    #region ColorBalanceImgui
    ImGui.TableNextRow();
    //ImGui.TableNextColumn();
    ImGui.TableSetColumnIndex(0);
```

4. **Inside a header,** `Indent(20)` shows nesting; a row for "pre imgui" and one for "post imgui", each a checkbox, a block under `BeginDisabled(!enabled)` for its parameters, and a Reset button. Wrap every widget in `PushID`/`PopID` because the labels repeat:

```csharp
// MPFX/MPFX.cs L319-L337,L350-L354
if (ImGui.CollapsingHeader("ColorBalance", TreeFlags | ImGuiTreeNodeFlags.SpanAllColumns))
{
    ImGui.Indent(20f);
    ImGui.TableNextRow();
    ImGui.TableNextColumn();
    ImGui.PushID("ColorBalancePreCheckbox");
    valuesChanged |= ImGui.Checkbox("pre imgui", ref CurrentProfile.ColorBalancePreImgui);
    ImGui.PopID();
    ImGui.Indent(20f);

    ImGui.BeginDisabled(!CurrentProfile.ColorBalancePreImgui);
    ImGui.TableNextColumn();
    ImGui.PushID("ColorBalancePreSeparateRGB");
    valuesChanged |= ImGui.Checkbox("Separate RGB", ref CurrentProfile.ColorBalancePreImguiSeparateRGB);
    ImGui.PopID();

    ImGui.TableNextColumn();
    ImGui.PushID("ColorBalancePreReset");
    if (ImGui.Button("Reset"))
// ...
    if (!CurrentProfile.ColorBalancePreImguiSeparateRGB)
    {
        float LiftPre = CurrentProfile.ColorBalanceMatPreImgui[0, 0];
        valuesChanged |= ImguiSliderRow("Lift", "ColorBalanceCombinedPreLift", -1f, 1f, ref LiftPre);
        CurrentProfile.ColorBalanceMatPreImgui[0, 0] = LiftPre;
```

5. **Share a slider-plus-input row helper** that fills one table row with a label, a full-width `SliderFloat(..., AlwaysClamp)` and a full-width `InputFloat`. The slider clamps but the input does not, so typed values can exceed the range:

```csharp
// MPFX/MPFX.cs L42-L69
[ModMenuEntry("MPFX")]
public static void DrawMenu()
{
    ImGui.Text("MPFX Mod Menu");
    if (ImGui.Button("Open")) ShowWindow = true;
}

private static bool ImguiSliderRow(string text, string id, float min, float max, ref float value)
{
    bool changed = false;
    ImGui.TableNextRow();
    ImGui.TableNextColumn();
    ImGui.PushID($"{id}RowLabel");
    ImGui.Text(text);
    ImGui.PopID();
    ImGui.TableNextColumn();
    ImGui.PushID($"{id}RowSlider");
    float liftPre = CurrentProfile.ColorBalanceMatPreImgui[0, 0];
    ImGui.SetNextItemWidth(-1f);
    changed |= ImGui.SliderFloat("", ref value, min, max, flags: ImGuiSliderFlags.AlwaysClamp);
    ImGui.PopID();
    ImGui.TableNextColumn();
    ImGui.SetNextItemWidth(-1f);
    ImGui.PushID($"{id}Input");
    changed |= ImGui.InputFloat("", ref value);
    ImGui.PopID();
    return changed;
}
```

6. **Draw the tab buttons after the child.** Each is wrapped in `BeginDisabled(CurrentTab == X)`, so the active tab looks pressed:

```csharp
// MPFX/MPFX.cs L1246-L1267
    ImGui.EndChild();

    ImGui.Separator();

    ImGui.BeginDisabled(CurrentTab == WindowTabs.Profiles);
    if (ImGui.Button("Profiles")) CurrentTab = WindowTabs.Profiles;
    ImGui.SameLine();
    ImGui.EndDisabled();

    ImGui.BeginDisabled(CurrentTab == WindowTabs.Shaders);
    if (ImGui.Button("Shaders")) CurrentTab = WindowTabs.Shaders;
    ImGui.SameLine();
    ImGui.EndDisabled();

    ImGui.BeginDisabled(CurrentTab == WindowTabs.ExportProfile);
    if (ImGui.Button("Export profile")) CurrentTab = WindowTabs.ExportProfile;
    ImGui.SameLine();
    ImGui.EndDisabled();

    UpdateValues |= valuesChanged;
}
ImGui.End();
```

## Complete example

| File | Role |
|---|---|
| [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L42-L69) | ModMenu entry and the slider row helper |
| [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L225-L1267) | Window, tabs and the per-effect table |

## Variations and gotchas

- **Tabs at the bottom, from `-32f`.** The reserved height must match the button row, or the buttons clip.
- **Always `PushID`/`PopID` repeated labels.** Without them the second "Reset" button mirrors the first.
- **`BeginDisabled` blocks must be balanced** (`EndDisabled` on every path, including the tab buttons).
- **Typed values bypass `AlwaysClamp`**; clamp in your model if range matters.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of build-a-tabbed-imgui-window](overview.md)
- [Approach 3: title-bar-coloured custom tab buttons](build-a-tabbed-imgui-window-3.md)
- [Build a settings window](../build-a-settings-window/overview.md)
- Mod feature: [Effect settings window](../../../../mods/mpfx/features/effect-settings-window.md)

[^brutal-imguiapi-imguitableflags]: Brutal.ImGuiApi.ImGuiTableFlags (decompiled, build 2026.10.10.5554)
