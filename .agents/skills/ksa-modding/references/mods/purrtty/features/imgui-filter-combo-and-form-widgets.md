---
type: Mod Feature
title: Filterable combo and form-table widgets for Brutal ImGui
description: A combo box with a live substring filter that works around ImInputString only refreshing its length on a true InputText return, plus label-gutter form tables and a destructive button
mod: purrtty
category: ui
techniques: [imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImInputString, Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImGuiTableFlags]
tags: [imgui, widgets, combo, text-input, forms, brutal-imgui]
---

purrTTY's dialogs (Theme, In-World Terminals, Layouts) pick from long lists: themes, fonts, shells,
vehicles, parts. `ImGuiWidgets.FilterCombo` puts a text box at the top of a combo popup that narrows
the list as the player types. The interesting part is a Brutal ImGui quirk it works around: the
`ImInputString` buffer's `Length` field is only refreshed when `ImGui.InputText` returns true, so a
naive live read of the typed text sees an empty string.

## How it works

- **The quirk.** `ImGui.InputText(ImString, ImInputString, ...)` writes into the fixed byte buffer
  natively and calls `buffer.EvaluateLength()` only if the native call returned true.[^ksa-imgui]
  `ImInputString.ToString()` and `.Length` read the cached length.[^ksa-iminputstring] With
  `ImGuiInputTextFlags.EnterReturnsTrue` the call returns true only on Enter, so the cached length
  stays stale while typing.
- **The fix.** `FilterCombo` does not pass `EnterReturnsTrue` and calls `filter.EvaluateLength()`
  itself every frame before `filter.ToString()`. The same pattern is used for the name fields in
  `ThemeDialog` and `InWorldManagerUI` (gotcha 24).[^gotchas]
- **Combo mechanics.** `BeginCombo`, focus the filter on the popup's first frame
  (`IsWindowAppearing` -> `SetKeyboardFocusHere`), full-width `InputText`, then one `Selectable` per
  item whose label contains the needle (ordinal, case-insensitive). Labels get `##{id}_{index}` so
  duplicate labels (parts sharing a template name) never collide. The filter is cleared after a
  pick. The caller owns one persistent `ImInputString` per combo.
- **Form tables.** `BeginFormTable` (two stretch columns, 1:3) and `BeginFormTableFixed` (fixed label
  column from `MeasureLabelWidth`, the widest `CalcTextSize` + 16 px) with `NoPadOuterX` and 6 px cell
  padding; `FormRow(label)` emits a frame-aligned label and sets the next item to full width.
- **Destructive button.** Four pushed style colors for a red "Destroy" button.

## Lifecycle and timing

Pure immediate-mode widgets, called from the dialogs that `TerminalMod.OnAfterUi` pumps every frame
(`[StarMapAfterGui]`, `frame:draw-ui`). The persistent `ImInputString` objects live in the dialog
instances across frames.

## Key code

| Path | Role |
|---|---|
| [purrTTY.GameMod/UI/ImGuiWidgets.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/UI/ImGuiWidgets.cs#L143-L220) | `FilterCombo` |
| [purrTTY.GameMod/UI/ImGuiWidgets.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/UI/ImGuiWidgets.cs#L31-L121) | Form tables, label measurement, `FormRow` |
| [purrTTY.GameMod/UI/ImGuiWidgets.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/UI/ImGuiWidgets.cs#L123-L141) | `DestructiveButton` |
| [purrTTY.GameMod/UI/ThemeDialog.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/UI/ThemeDialog.cs#L160-L240) | Callers: target picker, rename, palette, save, delete |
| [purrTTY.GameMod/InWorld/UI/InWorldManagerUI.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/UI/InWorldManagerUI.cs#L225-L235) | Name field with per-frame `EvaluateLength` |

## Snippets

```csharp
// purrTTY.GameMod/UI/ImGuiWidgets.cs L174-L210
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
```

The binding that causes the quirk:

```csharp
// current/decomp/Brutal.ImGuiApi/ImGui.cs L8487-L8501
	public unsafe static bool InputText(ImString label, ImInputString buffer, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None, ImGuiInputTextCallback? callback = null, nint userData = 0)
	{
		fixed (byte* buffer2 = buffer.Buffer)
		{
			fixed (byte* label2 = label)
			{
				Bool8 bool5 = PInvoke.InputText(label2, buffer2, (nuint)buffer.Capacity, flags, (callback == null) ? 0 : Marshal.GetFunctionPointerForDelegate(callback), (void*)userData);
				if ((bool)bool5)
				{
					buffer.EvaluateLength();
				}
				return bool5;
			}
		}
	}
```

## Game APIs touched

| API | Where | Purpose |
|---|---|---|
| `ImInputString` (`Buffer`, `Length`, `EvaluateLength`, `Clear`, `ToString`) | [current/decomp/Brutal.ImGuiApi/ImInputString.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImInputString.cs#L7-L80) | Brutal's fixed-capacity UTF-8 text buffer |
| `ImGui.InputText(ImString, ImInputString, ...)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8487-L8501) | Only re-evaluates length on a true return |
| `ImGui.BeginCombo`, `Selectable`, `IsWindowAppearing`, `SetKeyboardFocusHere`, `BeginTable`, `TableSetupColumn` | `Brutal.ImGuiApi.ImGui` | Combo and table plumbing |

## Pitfalls and version notes

- Any mod that reads an `ImInputString` outside the frame `InputText` returned true must call
  `EvaluateLength()` first; this is a property of Brutal's binding in build 2026.10.10.5554.
- Keep one `ImInputString` per field for the dialog's lifetime; recreating it each frame loses the text.
- Selectable ids must be unique within the popup; append an index when labels can repeat.

## See also

- [Named terminal registry and theme dialog](./named-terminal-registry-and-theme-dialog.md)
- [Saved terminal layouts](./saved-terminal-layouts.md)
- [Anchor quad to part or camera](./anchor-quad-to-part-or-camera.md) (vehicle and part pickers)
- [purrTTY overview](../overview.md)
- [Settings window: label-gutter form tables and full-width widgets](../../../howto/ui/windows/build-a-settings-window/build-a-settings-window-2.md) - how-to article demonstrating this feature
- [Searchable lists: a live substring filter inside a combo, with ImInputString length evaluated](../../../howto/ui/windows/build-searchable-lists-and-browsers/build-searchable-lists-and-browsers-1.md) - how-to article demonstrating this feature

[^ksa-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^ksa-iminputstring]: Brutal.ImGuiApi.ImInputString (decompiled, build 2026.10.10.5554)
[^gotchas]: purrTTY docs/gotchas.md
