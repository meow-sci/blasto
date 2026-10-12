---
type: Mod Feature
title: Effect settings window and ModMenu entry
description: One ImGui window with button-driven tabs, a three-column table of collapsing headers per effect with pre/post ImGui rows, a reusable slider-plus-input row helper, color pickers and per-row Reset buttons, opened from a ModMenu attribute entry
mod: mpfx
category: ui
techniques: [imgui-window, imgui-menu, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, modmenu ModMenuEntry("MPFX")]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, KSA.Program, Core.Renderer]
tags: [imgui, ui, settings, modmenu, tables, color-picker, starmap]
---

Most of MPFX's 1,272-line `MPFX.cs` is a single settings window titled "MPFX shader menu". A Profiles,
Shaders and Export profile tab row sits at the bottom. The Shaders tab is a three-column table with
one collapsing header per effect. Each header holds a "pre imgui" row and a "Post imgui" row, each with
an enable checkbox, its sliders and a Reset button. The window opens at startup, can be closed with its
title-bar X, and is reopened from ModMenu.[^mpfx-main]

## How it works

1. **ModMenu entry.** A static method tagged `[ModMenuEntry("MPFX")]` draws `ImGui.Text` and an
   "Open" button that sets `ShowWindow = true` ([MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L42-L47)). The attribute comes from
   the `ModMenu.Attributes` 1.0.2 NuGet package, whose DLL is copied next to `MPFX.dll`
   ([MPFX/MPFX.csproj](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.csproj#L53-L61)). ModMenu is not in this reference set, so how it finds
   and calls the method is not verified here.[^mpfx-csproj]
2. **Window.** `SetNextWindowSizeConstraints(650x500, max)`, then `SetNextWindowPos` at 70% of the
   renderer width with `ImGuiCond.FirstUseEver`. It calls
   `ImGui.Begin("MPFX shader menu", ref ShowWindow, ImGuiWindowFlags.NoSavedSettings)`, so ImGui does not
   remember position or size across runs ([MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L225-L252)). Debug builds add a
   "Rebuild renderers" button that calls `Program.ScheduleRendererRebuild()`.
3. **Tabs without a tab bar.** A `WindowTabs` enum is drawn as three buttons after a `BeginChild` that
   reserves 32 px at the bottom (`new float2(0f, -32f)`). Each button is wrapped in
   `BeginDisabled(CurrentTab == X)`, so the active tab looks pressed
   ([MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L1246-L1265)).
4. **Effect table.** `BeginTable("Shaders", 3, Borders | Resizable)` has columns Enabled (fixed 150),
   Config (stretch) and InputConfig (fixed 150). Each effect starts a row and calls
   `CollapsingHeader(name, ImGuiTreeNodeFlags.SpanAllColumns)`. Inside, `TableNextRow`/`TableNextColumn`
   place the checkbox, the Reset button and the parameter rows. `Indent(20)` shows nesting, and
   `BeginDisabled(!enabled)` greys out the parameters of a side that is off. Every widget is wrapped in
   `PushID`/`PopID` because labels repeat ("pre imgui", "Reset").
5. **Row helper.** `ImguiSliderRow(text, id, min, max, ref value)` fills one table row with a label, a
   full-width `SliderFloat(..., ImGuiSliderFlags.AlwaysClamp)` and a full-width `InputFloat`, and returns
   whether either changed ([MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L49-L69)). The slider clamps but the input field does
   not, so typed values can exceed the slider range.
6. **Per-effect widgets.**

   | Effect | Widgets | Range |
   |---|---|---|
   | Color balance | "Separate RGB" checkbox. Off: one Lift/Gamma/Gain set written to all three channels. On: nested Red/Green/Blue collapsing headers, each with its own set | Lift -1..1, gamma 0.01..2, gain 0.01..2 |
   | Color temperature | "Kelvin" slider row, Reset to 6500 | 1000..40000 |
   | Hue shift | "Degree" slider row | 0..360 |
   | Brightness, contrast, saturation | "Multiplier"/"Adjustment" slider row, Reset to 1 | 0.01..4 |
   | Color overlay | `ColorPicker4` with the `DisplayRGB` and `DisplayHSV` flags, width 200. Alpha is the blend amount | n/a |
   | Vignette | `ColorPicker4` (width 275), "Outer radius" (max = viewport aspect), "Inner radius" (max = outer - 0.001), "Aspectratio" | 0.0001..5 |
   | Film grain | Grainsize, Intensity, Timemultiplier rows plus a "Timewarp multiplier" checkbox | 0.01..12, 0.001..4, 0..12 |
   | Gaussian blur | "Radius" row (`double` in the profile, `float` in the UI) | 0..20 |
   | Fun stuff | Plain `SliderFloat("Percentage")` for RGB2HSV, HSV2RGB and the three SingleColor variants | 0..1 |

7. **Derived values.** The vignette computes its shader `z` term from the user aspect and
   `Program.MainViewport.Size`: `VignettePreAspectRatio * Size.Y / Size.X`. It also clamps the inner radius
   below the outer radius ([MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L868-L897)).

## Lifecycle and timing

The window is drawn in the same `[StarMapAfterGui]` call that uploads the parameters (`frame:draw-ui`,
a postfix on `Program.OnDrawUiViewports`; see
[StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L26-L36)).[^starmap-programpatcher] ImGui calls are legal there because
`PrepareImGui` has run and `ImGui.Render` has not
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2261-L2286)). When the HUD is hidden, the window disappears with the
rest of the UI.[^ksa-program] Because the post-ImGui chain processes the finished swapchain image, the
window itself is graded by post-side effects. That makes the pre/post split visible while you tune.

## Key code

| Path | Role |
|---|---|
| [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L42-L69) | ModMenu entry and the slider row helper |
| [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L224-L1269) | Window, tabs and the per-effect table |

## Snippets

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

```csharp
// MPFX/MPFX.cs L319-L356
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
    {
        valuesChanged = true;
        CurrentProfile.ColorBalancePreImguiSeparateRGB = false;
        CurrentProfile.ColorBalanceMatPreImgui = new float4x4(
            0, 0, 0, 0, // Lift
            1, 1, 1, 0, // Gamma
            1, 1, 1, 0, // Gain
            0, 0, 0, 1  // last value used as on/off toggle
        );
    }
    ImGui.PopID();

    if (!CurrentProfile.ColorBalancePreImguiSeparateRGB)
    {
        float LiftPre = CurrentProfile.ColorBalanceMatPreImgui[0, 0];
        valuesChanged |= ImguiSliderRow("Lift", "ColorBalanceCombinedPreLift", -1f, 1f, ref LiftPre);
        CurrentProfile.ColorBalanceMatPreImgui[0, 0] = LiftPre;
        CurrentProfile.ColorBalanceMatPreImgui[0, 1] = LiftPre;
        CurrentProfile.ColorBalanceMatPreImgui[0, 2] = LiftPre;
        // ...
```

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

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `ImGui.BeginTable`, `TableSetupColumn`, `TableNextRow`, `TableNextColumn`, `TableSetColumnIndex` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs) | Effect table layout[^imgui-api] |
| `ImGui.CollapsingHeader(ImString, ImGuiTreeNodeFlags)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7260) | One header per effect, `SpanAllColumns` |
| `ImGui.ColorPicker4(ImString, ref float4, ImGuiColorEditFlags, ...)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7143) | Overlay and vignette color, edited in place on the profile's `float4` |
| `ImGui.SliderFloat`, `InputFloat`, `Checkbox`, `BeginDisabled`, `PushID`, `Indent` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs) | Parameter rows |
| `Program.MainViewport.Size` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L484) | Vignette aspect and slider bounds |
| `Program.ScheduleRendererRebuild()` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5248-L5251) | Debug-only rebuild button |

## Pitfalls and version notes

- Color balance edits matrix elements through `float4x4[row, col]` copies (read into a local, slide, write
  back). Writing back all three channels in the combined mode is what keeps them in sync.
- The pre-side Green sliders and the pre-side temperature Reset do not report changes. See
  [Per-frame effect parameter upload](per-frame-parameter-upload.md).
- `ImguiSliderRow` reads `CurrentProfile.ColorBalanceMatPreImgui[0, 0]` into an unused local on every
  call. This is leftover code with no effect.
- With `NoSavedSettings`, the window reopens at the default position on every launch, and
  `ShowWindow` is `true` at startup, so the window opens on every launch.

## See also

- [Per-frame effect parameter upload](per-frame-parameter-upload.md)
- [Effect profiles as XML assets with clipboard export](xml-effect-profiles.md)
- [Color grading shader math](color-grading-math.md)
- [Reference ModMenu.Attributes, tag a static method and ship the DLL beside your mod](../../../howto/ui/menus/list-a-mod-in-modmenu/list-a-mod-in-modmenu-1.md) - how-to article demonstrating this feature
- [Settings window: slider-plus-input rows, ColorPicker4 on a float4 and Reset buttons](../../../howto/ui/windows/build-a-settings-window/build-a-settings-window-1.md) - how-to article demonstrating this feature
- [Tabbed window: button tabs driving a table of collapsing headers](../../../howto/ui/windows/build-a-tabbed-imgui-window/build-a-tabbed-imgui-window-4.md) - how-to article demonstrating this feature

[^mpfx-main]: MPFX/MPFX.cs
[^mpfx-csproj]: MPFX/MPFX.csproj
[^starmap-programpatcher]: StarMap.Core/Patches/ProgramPatcher.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^imgui-api]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
