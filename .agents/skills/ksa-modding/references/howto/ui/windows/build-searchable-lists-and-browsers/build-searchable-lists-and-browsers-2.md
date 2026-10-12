---
type: How-To
title: "Searchable lists: SetKeyboardFocusHere on IsWindowAppearing and SetItemDefaultFocus"
description: Make a search-as-you-type star-system combo that focuses its filter box the moment the popup opens and scrolls to the current entry, using a plain InputText whose result is read as a string
category: ui
group: windows
topic: build-searchable-lists-and-browsers
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/compendium/features/celestial-info-window]
techniques: [imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImInputString, KSA.Universe]
tags: [combo, search, focus, default-focus, selectable, compendium]
---
Compendium lets the player choose a star system from a combo that can be long once modded systems are installed. Its selector is the smallest searchable combo in the corpus: a `BeginCombo`, a `Search` text box that grabs keyboard focus the frame the popup appears, a substring filter, and `SetItemDefaultFocus` on the current entry so the popup scrolls to it. Demonstrated by Compendium. Prerequisite: StarMap.

## Background: how the game does it

The three calls are Dear ImGui's combo idiom, exposed by `Brutal.ImGuiApi`: `IsWindowAppearing()` is true for the first frame of the popup window, `SetKeyboardFocusHere()` moves keyboard focus to the next widget, and `SetItemDefaultFocus()` marks the last item as the one the popup should scroll to.[current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7923-L7930)[^brutal-imguiapi-imgui] The list itself comes from the game: `Universe.Roots` is a span of the independent roots, which the mod treats as one per star system (its comment: the parentless `StellarBody` or `Barycenter` entries).[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L110-L110)[^ksa-universe]

## Lifecycle and timing

The selector is drawn inside Compendium's window from `[StarMapAfterGui]` (`frame:draw-ui`). The window waits for a universe first: `[StarMapAllModsLoaded]` runs before any universe exists, so `Universe.WorldSun` is null and `Universe.Roots` empty until a system loads (see [approach 5](build-searchable-lists-and-browsers-5.md)). See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Keep a persistent buffer** for the search text: `ImInputString` with a fixed capacity, as a static field.

2. **Open the combo and focus the search box once.** `SetKeyboardFocusHere()` only on the first frame of the popup, then an `InputText`; read the buffer's `Value` after the call (default flags, so the call returns true on edit and refreshes the length, see [approach 1](build-searchable-lists-and-browsers-1.md)):

```csharp
// src/CdmStarSystems.cs L142-L151
ImGui.SetNextItemWidth(-1f);
if (ImGui.BeginCombo(new ImString("##StarSystemSelector"), new ImString(GetBodyDisplayName(currentRoot))))
{
    if (ImGui.IsWindowAppearing())
    {
        ImGui.SetKeyboardFocusHere();
    }
    ImGui.InputText(new ImString("Search##StarSystemSearch"), systemSearchBuffer);

    string search = systemSearchBuffer.Value;
```

3. **Filter, order and select.** Skip non-matching rows, give each `Selectable` a unique `##system_<id>` id, call `SetItemDefaultFocus()` on the selected one, and act only when the choice actually changes:

```csharp
// src/CdmStarSystems.cs L152-L172
        foreach (var root in GetSystemRoots().OrderBy(root => GetBodyDisplayName(root), StringComparer.OrdinalIgnoreCase))
        {
            string rootName = GetBodyDisplayName(root);
            if (search.Length > 0 && !rootName.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool isSelected = root == currentRoot;
            if (ImGui.Selectable(new ImString($"{rootName}##system_{root.Id}"), isSelected) && !isSelected)
            {
                SelectSystem(root);
            }
            if (isSelected)
            {
                ImGui.SetItemDefaultFocus();
            }
        }
        ImGui.EndCombo();
    }
}
```

4. **Reset dependent state on selection** (`SelectSystem` clears the category tree and the selected body so the window rebuilds for the new system):

```csharp
// src/CdmStarSystems.cs L116-L128
private static void SelectSystem(Astronomical root)
{
    selectedSystemRoot = root;
    buttonsCatsTree = null;
    categoryKeys = new List<string>();
    showWindow = "None";
    selectedCategoryIndex = -1;
    selectedCategoryKey = "None";
    selectedCelestial = null;
    selectedCelestialId = "Compendium Astronomicals Database";
    selectedOrbitGroupKey = null;
    selectedOrbitGroupParentBodyKey = null;
}
```

## Complete example

| File | Role |
|---|---|
| [src/CdmStarSystems.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmStarSystems.cs#L116-L172) | System selection and the combo |
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L312-L312) | Call site in the left pane |

## Variations and gotchas

- **Order the list once per frame at most.** The mod sorts `GetSystemRoots()` by display name each time the popup is drawn, which is fine for tens of systems.
- **The search buffer is shared by all opens of the popup.** It is not cleared when the popup closes, so the filter persists; clear it on `IsWindowAppearing()` (as Zippo does in the colour picker) if you want a fresh list each time.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of build-searchable-lists-and-browsers](overview.md)
- [Approach 1: live filter with EvaluateLength](build-searchable-lists-and-browsers-1.md)
- [Approach 3: ranked search over the whole universe](build-searchable-lists-and-browsers-3.md)
- Mod feature: [Compendium celestial info window](../../../../mods/compendium/features/celestial-info-window.md)

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
