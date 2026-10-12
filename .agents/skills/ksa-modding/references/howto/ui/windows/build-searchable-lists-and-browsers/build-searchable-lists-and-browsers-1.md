---
type: How-To
title: "Searchable lists: a live substring filter inside a combo, with ImInputString length evaluated"
description: Put a text filter at the top of a BeginCombo popup that narrows the items as the player types, and work around Brutal's ImInputString length caching by evaluating the length every frame
category: ui
group: windows
topic: build-searchable-lists-and-browsers
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/purrtty/features/imgui-filter-combo-and-form-widgets]
techniques: [imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImInputString]
tags: [combo, filter, search, iminputstring, evaluate-length, selectable, purrtty]
---
purrTTY's dialogs choose from long lists: themes, shells, vehicles, parts. Its `FilterCombo` helper adds a text box at the top of a combo popup that filters the list as the player types, focuses it the instant the popup opens, and hides rows whose label does not contain the text. The interesting part is a Brutal ImGui quirk the helper guards against: the `ImInputString` buffer caches its length, and `ImGui.InputText` refreshes that cache only when the native call returns true. Demonstrated by purrTTY. Prerequisite: StarMap.

## Background: how the game does it

`Brutal.ImGuiApi` wraps Dear ImGui's text input around a fixed-capacity UTF-8 buffer, `ImInputString`. `ImGui.InputText(label, ImInputString, flags, ...)` writes into the buffer natively and calls `buffer.EvaluateLength()` only if the native call returned true; `ImInputString.ToString()` and `Length` read the cached value.[current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8487-L8501)[^brutal-imguiapi-imgui] `EvaluateLength()` recomputes it from the bytes.[current/decomp/Brutal.ImGuiApi/ImInputString.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImInputString.cs#L76-L80)[^brutal-imguiapi-iminputstring] With default flags native `InputText` returns true when the text was edited, so a plain read after the call is normally fine (the other approaches in this topic rely on that); with `ImGuiInputTextFlags.EnterReturnsTrue` it returns true only on Enter, so the cache stays stale while typing. purrTTY avoids the question by not passing `EnterReturnsTrue` and calling `EvaluateLength()` itself every frame.

## Lifecycle and timing

Pure immediate-mode widget, called from dialogs the mod pumps every frame from `[StarMapAfterGui]` (`frame:draw-ui`). The caller owns one persistent `ImInputString` per combo so the typed text survives between frames. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Keep one buffer per combo** as a field of the dialog (`new ImInputString(capacity)`); recreating it each frame loses the text.

2. **Write the combo helper.** `BeginCombo`, focus the filter on the popup's first frame (`IsWindowAppearing` then `SetKeyboardFocusHere`), a full-width `InputText` without `EnterReturnsTrue`, then `EvaluateLength()` before reading:

```csharp
// purrTTY.GameMod/UI/ImGuiWidgets.cs L165-L190
public static bool FilterCombo(
    string id,
    string preview,
    ImInputString filter,
    IReadOnlyList<(string Key, string Label)> items,
    out string? selectedKey)
{
    selectedKey = null;

    if (!ImGui.BeginCombo(id, preview))
    {
        return false;
    }

    // Focus the filter box the frame the popup opens so the user can type at once.
    if (ImGui.IsWindowAppearing())
    {
        ImGui.SetKeyboardFocusHere();
    }

    ImGui.SetNextItemWidth(-1f);
    ImGui.InputText($"{id}_filter", filter, ImGuiInputTextFlags.None);
    filter.EvaluateLength();
    string needle = filter.ToString().Trim();

    ImGui.Separator();
```

3. **List the matching items.** Skip rows whose label does not contain the needle (ordinal, case-insensitive), and give each `Selectable` a unique id with an index suffix so duplicate labels (for example parts that share a template name) never collide; clear the filter once a choice is made:

```csharp
// purrTTY.GameMod/UI/ImGuiWidgets.cs L192-L220
    bool chose = false;
    for (int i = 0; i < items.Count; i++)
    {
        (string key, string label) = items[i];
        if (needle.Length > 0 && label.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
        {
            continue;
        }

        // Disambiguate the ImGui id with the row index so duplicate labels (e.g.
        // parts sharing a template id) never collide.
        if (ImGui.Selectable($"{label}##{id}_{i}", false))
        {
            selectedKey = key;
            chose = true;
        }
    }

    ImGui.EndCombo();

    // Reset the filter once a choice is made so the next open starts from the full
    // list rather than a stale needle.
    if (chose)
    {
        filter.Clear();
    }

    return chose;
}
```

4. **Call it with stable keys.** The list is `(key, label)` pairs; the helper returns true on the frame an item is chosen:

```csharp
// purrTTY.GameMod/InWorld/UI/InWorldManagerUI.cs L233-L238
ImGuiWidgets.FormRow("Shell");
if (ImGuiWidgets.FilterCombo("##iw_shell", _draftShell?.Label ?? "Default Shell", _shellFilter, shellItems, out string? shellKey)
    && shellKey != null && int.TryParse(shellKey, out int shellIdx) && shellIdx < shells.Count)
{
    _draftShell = shells[shellIdx];
}
```

## Complete example

| File | Role |
|---|---|
| [purrTTY.GameMod/UI/ImGuiWidgets.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/UI/ImGuiWidgets.cs#L143-L220) | `FilterCombo` |
| [purrTTY.GameMod/InWorld/UI/InWorldManagerUI.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/UI/InWorldManagerUI.cs#L216-L251) | Caller inside a form table |

## Variations and gotchas

- **Read `ImInputString` only after `EvaluateLength()`** unless the call that wrote to it returned true; this is a property of the Brutal binding in build 2026.10.10.5554.
- **Unique `Selectable` ids.** Duplicate labels share an id and the second row mirrors the first; append an index.
- **Reset the filter after a pick** so the next open starts from the full list.
- **Focus once.** `SetKeyboardFocusHere()` every frame would keep stealing focus; gate it with `IsWindowAppearing()`.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of build-searchable-lists-and-browsers](overview.md)
- [Approach 2: SetKeyboardFocusHere and SetItemDefaultFocus](build-searchable-lists-and-browsers-2.md)
- [Settings window: label-gutter form tables](../build-a-settings-window/build-a-settings-window-2.md)
- Mod feature: [Filter combo and form widgets](../../../../mods/purrtty/features/imgui-filter-combo-and-form-widgets.md)

[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-iminputstring]: Brutal.ImGuiApi.ImInputString (decompiled, build 2026.10.10.5554)
