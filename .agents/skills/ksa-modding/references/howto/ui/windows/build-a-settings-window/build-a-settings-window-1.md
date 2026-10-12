---
type: How-To
title: "Settings window: slider-plus-input rows, ColorPicker4 on a float4 and Reset buttons"
description: Edit a profile of float parameters with a reusable slider-and-input table row, colour pickers bound directly to float4 fields, per-effect enable checkboxes, Reset buttons and derived values
category: ui
group: windows
topic: build-a-settings-window
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/mpfx/features/effect-settings-window]
techniques: [imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImGuiColorEditFlags, Brutal.Numerics.float4, KSA.Program]
tags: [settings, slider, input-float, color-picker, reset, table, mpfx]
---
MPFX is a settings window for a post-processing profile: dozens of float parameters, a few colours and an enable switch per side. The ImGui patterns that carry it are small and reusable: one table-row helper that gives every number both a slider and a typed input, colour pickers bound straight to `float4` fields of the profile, a per-effect Reset button, and derived values recomputed from the viewport and from each other. The window and tab structure is covered in [button tabs over a table of collapsing headers](../build-a-tabbed-imgui-window/build-a-tabbed-imgui-window-4.md); this article is about the controls. Demonstrated by MPFX. Prerequisite: StarMap.

## Background: how the game does it

Nothing in KSA is involved beyond the ImGui binding: `ImGui.ColorPicker4(label, ref float4, ImGuiColorEditFlags)` edits a `float4` in place,[current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7143-L7143)[^brutal-imguiapi-imgui] and `SliderFloat`/`InputFloat` take the value by `ref`. They return true on the frame the value changes, which is the signal this mod ORs into `valuesChanged` and uses to decide when to upload parameters. `Program.MainViewport.Size` supplies the viewport size used to derive the vignette aspect.

## Lifecycle and timing

Drawn in `[StarMapAfterGui]` (`frame:draw-ui`) in the same method that uploads the profile each frame, so a change shown this frame reaches the shader on the next. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Write one row helper.** A table row of label, full-width slider and full-width input bound to the same `ref float`; `PushID` per cell because the slider and input both have empty labels. `AlwaysClamp` constrains only the slider; the input field accepts anything:

```csharp
// MPFX/MPFX.cs L49-L69
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

2. **Bind colour pickers to `float4` fields.** `SetNextItemWidth` sets the picker size; `BeginDisabled(!enabled)` greys the whole block while its side is off. The display flags choose RGB and HSV readouts; alpha is the effect's blend amount:

```csharp
// MPFX/MPFX.cs L836-L850
if (ImGui.CollapsingHeader("ColorOverlay", TreeFlags | ImGuiTreeNodeFlags.SpanAllColumns))
{
    ImGui.TableNextRow();
    ImGui.TableNextColumn();
    ImGui.PushID("ColorOverlayPreCheckbox");
    valuesChanged |= ImGui.Checkbox("pre imgui", ref CurrentProfile.ColorOverlayPreImgui);
    ImGui.PopID();

    ImGui.BeginDisabled(!CurrentProfile.ColorOverlayPreImgui);
    ImGui.TableNextColumn();
    ImGui.PushID("ColorOverlayPreStrength");
    ImGui.SetNextItemWidth(200f);
    valuesChanged |= ImGui.ColorPicker4("Color", ref CurrentProfile.ColorOverlayFloatPreImgui, ImGuiColorEditFlags.DisplayRGB | ImGuiColorEditFlags.DisplayHSV | ImGuiColorEditFlags.DisplayRGB);
    ImGui.PopID();
    ImGui.EndDisabled();
```

3. **Derive dependent values after the widgets run.** The inner radius is clamped below the outer radius, and the shader's aspect term is computed from the user value and the viewport size:

```csharp
// MPFX/MPFX.cs L889-L896
float rPre = CurrentProfile.VignetteFloatPreImgui.X;
valuesChanged |= ImguiSliderRow("Outer radius", "VignettePreOuterRadius", 0f, (float)Program.MainViewport.Size.X / Program.MainViewport.Size.Y, ref rPre);
CurrentProfile.VignetteFloatPreImgui.X = rPre;
float gPre = CurrentProfile.VignetteFloatPreImgui.Y;
valuesChanged |= ImguiSliderRow("Inner radius", "VignettePreInnerRadius", 0f, rPre - 0.001f, ref gPre);
CurrentProfile.VignetteFloatPreImgui.Y = Math.Min(gPre, rPre - 0.001f);
valuesChanged |= ImguiSliderRow("Aspectratio", "VignettePreAspectratio", 0.0001f, 5f, ref CurrentProfile.VignettePreAspectRatio);
CurrentProfile.VignetteFloatPreImgui.Z = CurrentProfile.VignettePreAspectRatio * Program.MainViewport.Size.Y / Program.MainViewport.Size.X;
```

4. **Give every effect a Reset button** that writes defaults back to the profile and sets `valuesChanged`:

```csharp
// MPFX/MPFX.cs L937-L951
ImGui.PushID("FilmGrainPreCheckbox");
valuesChanged |= ImGui.Checkbox("pre imgui", ref CurrentProfile.FilmGrainPreImgui);
ImGui.PopID();
ImGui.TableNextColumn();
ImGui.TableNextColumn();
ImGui.PushID("FilmGrainPreReset");
if (ImGui.Button("Reset"))
{
    valuesChanged = true;
    CurrentProfile.FilmGrainData.X = 2f;
    CurrentProfile.FilmGrainData.Y = 1f;
    CurrentProfile.FilmGrainPreTimeMultiplier = 1f;
    CurrentProfile.FilmGrainPreTimeWarpMultiplier = false;
}
ImGui.PopID();
```

5. **Track change.** `valuesChanged |= widget(...)` through the whole window, then `UpdateValues |= valuesChanged` after the last widget, so the profile is re-uploaded only on change.

## Complete example

| File | Role |
|---|---|
| [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L49-L69) | Slider-plus-input row helper |
| [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L836-L928) | Colour overlay and vignette sections |
| [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L930-L960) | Film grain with a Reset button |

## Variations and gotchas

- **Identical labels need `PushID`/`PopID`** ("pre imgui", "Reset", empty slider labels); otherwise widgets share state.
- **Typed input is not clamped.** If a range matters, clamp in your model (as the vignette does for the inner radius).
- **Return values matter.** Per the mod feature page, the pre-side Green sliders and the pre-side temperature Reset do not report changes into `valuesChanged`; any widget you forget to OR in is silently not applied.
- **Scale with the viewport.** The vignette aspect uses `Program.MainViewport.Size` at draw time.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of build-a-settings-window](overview.md)
- [Tabbed window: button tabs driving a table of collapsing headers](../build-a-tabbed-imgui-window/build-a-tabbed-imgui-window-4.md)
- [Approach 3: ColorButton popups and INI persistence](build-a-settings-window-3.md)
- [Persist mod settings as TOML](../../../persistence/settings-formats/persist-mod-settings-toml/overview.md)
- [List a mod in ModMenu with the ModMenuEntry attribute](../../menus/list-a-mod-in-modmenu/overview.md)
- Mod feature: [Effect settings window](../../../../mods/mpfx/features/effect-settings-window.md)

[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
