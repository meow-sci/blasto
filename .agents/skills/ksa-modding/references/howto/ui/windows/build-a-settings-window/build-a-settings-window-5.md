---
type: How-To
title: "Settings window: a reflected XKCD colour catalogue behind a filterable picker"
description: Reflect once over KSAColor.Xkcd to get a sorted, cached list of named colours and use it in a filter combo with swatches beside a ColorEdit4, keeping the colour and the name in step
category: ui
group: windows
topic: build-a-settings-window
approach: 5
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/lib-xkcd-color-catalog]
techniques: [reflection, imgui-window]
hooks: []
lifecycle: [frame:draw-ui]
game_types: [KSA.KSAColor, Brutal.Numerics.Color, Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImInputString]
tags: [colour, xkcd, reflection, combo, filter, swatch, color-edit, unscience]
---
Settings that take a colour (a light tint, a kitten tint) are easier for players when they can also pick a name. KSA ships the whole XKCD colour survey as `KSAColor.Xkcd.<Name>`, but each colour is a separate static property, so a picker cannot enumerate them at compile time. The unscience mods reflect over the class once, cache a sorted array of `(Name, float4)` pairs, and serve it to a combo box with a text filter and swatches, next to a `ColorEdit4` that stays in step with it. This catalogue is also the filterable-list example of [searchable lists](../build-searchable-lists-and-browsers/overview.md). Demonstrated by unscience (Zippo and Doh). Prerequisite: StarMap.

## Background: how the game does it

`KSAColor.Xkcd` is a nested static class whose members are expression-bodied `public static Color.Preset` properties (`CloudyBlue => float3.Rgb(...)`), not fields.[current/decomp/KSA/KSAColor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KSAColor.cs#L23-L29)[^ksa-ksacolor] `Color.Preset` converts implicitly to `float4`, which the helper uses to produce RGBA. Alpha is whatever that conversion yields (the presets are built from RGB), so callers that want translucency set it themselves (per the mod feature page).

The ImGui side is the stock combo: `BeginCombo`, an `InputTextWithHint` filter at the top, `Selectable` rows and `ColorButton` swatches. The `ImInputString` overload of `InputTextWithHint` re-evaluates the buffer length only when the call returns true, which for normal flags is when the text was edited, so reading `ToString()` afterwards works (see the quirk explained in [searchable lists](../build-searchable-lists-and-browsers/overview.md)).[current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8522-L8538)[^brutal-imguiapi-imgui]

## Lifecycle and timing

Pure main-thread UI helper with no hooks of its own. The first call, typically when a picker first draws in `frame:draw-ui`, pays the reflection cost once and logs the count; later calls return the cached array. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Reflect once and cache.** Read each public static property as a `Color.Preset`, convert to `float4`, wrap each read in a `try`/`catch` so one odd member cannot break the list, sort by name and cache:

```csharp
// ksa-abstractions.lib/XkcdColorHelper.cs L15-L37
private static (string Name, float4 Color)[]? _colors;

/// <summary>Returns all XKCD colors sorted alphabetically. Cached after first call.</summary>
public static (string Name, float4 Color)[] GetAll()
{
    if (_colors != null) return _colors;

    var props = typeof(KSAColor.Xkcd).GetProperties(BindingFlags.Public | BindingFlags.Static);
    var list = new List<(string, float4)>();
    foreach (var prop in props)
    {
        try
        {
            float4 val = (Color.Preset)prop.GetValue(null)!;
            list.Add((prop.Name, val));
        }
        catch { }
    }
    list.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase));
    _colors = list.ToArray();
    Console.WriteLine($"ksa-abstractions: Cached {_colors.Length} XKCD colors");
    return _colors;
}
```

2. **Offer name lookups** for code that has a name from elsewhere (RPC, saved settings), and a names-only array for combos:

```csharp
// ksa-abstractions.lib/XkcdColorHelper.cs L39-L59
/// <summary>Looks up an XKCD color by name (case-insensitive). Returns null if not found.</summary>
public static float4? FindByName(string name)
{
    var all = GetAll();
    foreach (var (n, c) in all)
    {
        if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            return c;
    }
    return null;
}

/// <summary>Returns all color names as a string array (for combo filtering).</summary>
public static string[] GetNames()
{
    var all = GetAll();
    var names = new string[all.Length];
    for (int i = 0; i < all.Length; i++)
        names[i] = all[i].Name;
    return names;
}
```

3. **Pair a colour editor with a name combo.** Doh draws a `ColorEdit4` and the XKCD combo in adjacent table rows; editing the colour by hand is detected by comparing the four channels before and after, and clears the stored name:

```csharp
// doh.lib/DohSubmod.cs L176-L190
ImGui.TableNextRow();
ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); ImGui.Text("Tint");
ImGui.TableNextColumn();
var prevColor = _tintColor;
ImGui.ColorEdit4("##doh_tint", ref _tintColor,
    ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar);
if (_tintColor.X != prevColor.X || _tintColor.Y != prevColor.Y
    || _tintColor.Z != prevColor.Z || _tintColor.W != prevColor.W)
    _selectedXkcdName = "";

// XKCD color combo
ImGui.TableNextRow();
ImGui.TableNextColumn(); ImGui.AlignTextToFramePadding(); ImGui.Text("XKCD Color");
ImGui.TableNextColumn(); ImGui.SetNextItemWidth(-1);
RenderXkcdCombo();
```

4. **Show swatches in the list.** Doh's combo draws a 14 px `ColorButton` before each name, with a per-row id so identical labels do not collide, and sets both the tint and the stored name on a pick:

```csharp
// doh.lib/DohSubmod.cs L567-L596
private void RenderXkcdCombo()
{
    string previewText = _selectedXkcdName.Length > 0 ? _selectedXkcdName : "Pick XKCD color...";
    if (ImGui.BeginCombo("##xkcd_combo", previewText))
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##xkcd_filter", "filter..."u8, _xkcdFilterText);

        string filterStr = _xkcdFilterText.ToString();
        var colors = XkcdColorHelper.GetAll();
        foreach (var (name, color) in colors)
        {
            if (filterStr.Length > 0
                && !name.Contains(filterStr, StringComparison.OrdinalIgnoreCase))
                continue;

            ImGui.ColorButton($"##swatch_{name}", color,
                ImGuiColorEditFlags.NoTooltip, new float2(14, 14));
            ImGui.SameLine();

            if (ImGui.Selectable(name, name == _selectedXkcdName))
            {
                _selectedXkcdName = name;
                _tintColor = color;
                _useCustomColor = true;
            }
        }
        ImGui.EndCombo();
    }
}
```

5. **Or keep the picker compact.** Zippo uses a 32 px colour swatch and a full-width combo on one line with a "(custom)" preview; the filter lives in [searchable lists](../build-searchable-lists-and-browsers/build-searchable-lists-and-browsers-4.md).

## Complete example

| File | Role |
|---|---|
| [ksa-abstractions.lib/XkcdColorHelper.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/XkcdColorHelper.cs#L13-L60) | Reflection, cache and lookups |
| [zippo.lib/ZippoSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/ZippoSubmod.cs#L246-L306) | Light-colour pickers (start and end colour) |
| [doh.lib/DohSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/doh.lib/DohSubmod.cs#L567-L596) | Swatch combo for kitten tints |

## Variations and gotchas

- **Silent failure on game updates.** If the game ever turns these properties into fields or another type, `GetProperties` returns nothing and the list is empty without an error; the logged count is the only clue.
- **Names are C# property names** (`RadioactiveGreen`, not "radioactive green").
- **Building the list is a one-off cost; drawing is per frame.** Every open frame walks the whole array and skips non-matching rows, which is fine for the hundreds of colours here.
- **One filter buffer per combo.** Each combo owns a persistent `ImInputString` (Zippo clears it when the popup appears).
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of build-a-settings-window](overview.md)
- [Approach 1: ColorPicker4 on a float4](build-a-settings-window-1.md)
- [Build searchable lists and browsers](../build-searchable-lists-and-browsers/overview.md)
- Mod feature: [XKCD colour catalogue](../../../../mods/unscience/features/lib-xkcd-color-catalog.md)

[^ksa-ksacolor]: KSA.KSAColor (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
