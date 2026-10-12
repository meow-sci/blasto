---
type: How-To
title: "Searchable lists: a filterable colour catalogue list"
description: Serve a cached, name-sorted array of colours to a combo with a clearing filter box, so a picker with nearly a thousand entries stays usable
category: ui
group: windows
topic: build-searchable-lists-and-browsers
approach: 4
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/lib-xkcd-color-catalog]
techniques: [reflection, imgui-window]
hooks: []
lifecycle: [frame:draw-ui]
game_types: [KSA.KSAColor, Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImInputString]
tags: [combo, filter, catalogue, xkcd, colour, unscience, zippo]
---
KSA's XKCD colour survey has hundreds of named colours, far too many for a plain combo. The Zippo (light colour) submod of unscience serves a cached, name-sorted array to a combo whose popup starts with a filter box that is cleared and focused on open. The reflection that builds the array, and the swatch-and-picker variant used by Doh, are covered in [settings window approach 5](../build-a-settings-window/build-a-settings-window-5.md); this article is about the list side. Demonstrated by unscience (Zippo). Prerequisite: StarMap.

## Background: how the game does it

The data is `KSAColor.Xkcd`, a nested static class of `Color.Preset` properties.[current/decomp/KSA/KSAColor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KSAColor.cs#L23-L29)[^ksa-ksacolor] Everything else is Dear ImGui: `BeginCombo` with a custom preview text, `InputTextWithHint` as the filter and `Selectable` rows.

## Lifecycle and timing

Drawn in `frame:draw-ui` by the submod's panel. The catalogue is built once on the first call (reflection plus a sort) and cached for the process; each frame the open combo walks the array and skips non-matching rows. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Build the cached array once** (see the settings-window article for the reflection) and expose a names-only array for lookups:

```csharp
// ksa-abstractions.lib/XkcdColorHelper.cs L51-L59
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

2. **Draw the combo.** The preview shows the chosen name or "(custom)" when the colour was edited by hand; the filter is cleared and focused on the frame the popup appears; each `Selectable` marks the current choice with `SetItemDefaultFocus`:

```csharp
// zippo.lib/ZippoSubmod.cs L255-L275
string xkcdStartPreview = _animStartXkcdName.Length > 0 ? _animStartXkcdName : "(custom)";
ImGui.SetNextItemWidth(-1);
if (ImGui.BeginCombo("##zp_animstart_xkcd", xkcdStartPreview))
{
    if (ImGui.IsWindowAppearing()) { ImGui.SetKeyboardFocusHere(); _animStartColorFilter.Clear(); }
    ImGui.SetNextItemWidth(-1);
    ImGui.InputTextWithHint("##zp_animstart_xflt", "filter..."u8, _animStartColorFilter);
    var allStartColors = XkcdColorHelper.GetAll();
    foreach (var (name, color) in allStartColors)
    {
        if (!MatchesFilter(_animStartColorFilter, name)) continue;
        bool sel = name == _animStartXkcdName;
        if (ImGui.Selectable(name, sel))
        {
            _animStartXkcdName = name;
            _animStartColor4 = color;
        }
        if (sel) ImGui.SetItemDefaultFocus();
    }
    ImGui.EndCombo();
}
```

3. **Filter with a case-insensitive substring** helper, reading the `ImInputString` as a string (the box is a plain `InputTextWithHint`, so the buffer length is refreshed whenever the text is edited):

```csharp
// zippo.lib/ZippoSubmod.cs L623-L627
private static bool MatchesFilter(ImInputString filter, string value)
{
    var filterText = filter.ToString().Trim();
    return filterText.Length == 0 || value.Contains(filterText, StringComparison.OrdinalIgnoreCase);
}
```

4. **Keep the choice and the colour in step.** Picking a row writes both the name and the colour; editing the colour elsewhere clears the name (`_animStartXkcdName = ""`).

## Complete example

| File | Role |
|---|---|
| [zippo.lib/ZippoSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/ZippoSubmod.cs#L246-L275) | Start-colour row with swatch editor and combo |
| [zippo.lib/ZippoSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/ZippoSubmod.cs#L623-L627) | Substring filter helper |
| [ksa-abstractions.lib/XkcdColorHelper.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/XkcdColorHelper.cs#L39-L59) | Name lookup and names array |

## Variations and gotchas

- **Names are C# property names.** `RadioactiveGreen`, not "radioactive green"; the filter matches them as written.
- **Silent empty list.** If the game turns the properties into fields the list is empty with only a log line; check the logged count after a game update.
- **Two combos, two buffers.** Zippo keeps a separate filter buffer for the start and end colours.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of build-searchable-lists-and-browsers](overview.md)
- [Settings window approach 5: reflected XKCD colour catalogue](../build-a-settings-window/build-a-settings-window-5.md)
- [Approach 1: live filter with EvaluateLength](build-searchable-lists-and-browsers-1.md)
- Mod feature: [XKCD colour catalogue](../../../../mods/unscience/features/lib-xkcd-color-catalog.md)

[^ksa-ksacolor]: KSA.KSAColor (decompiled, build 2026.10.10.5554)
