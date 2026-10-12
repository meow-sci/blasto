---
type: How-To
title: "Settings window: label-gutter form tables and full-width widgets"
description: Lay out a settings form as a two-column table with a fixed label gutter, full-width widgets, a filter combo and a red destructive button, so several sections of one dialog line up
category: ui
group: windows
topic: build-a-settings-window
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/purrtty/features/imgui-filter-combo-and-form-widgets]
techniques: [imgui-window, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImGuiTableFlags, Brutal.ImGuiApi.ImGuiTableColumnFlags]
tags: [form, table, label-gutter, full-width, destructive-button, dialog, purrtty]
---
purrTTY has several settings-like dialogs (Theme, In-World Terminals, Layouts) that are full of labelled inputs. Instead of laying every row out by hand, the mod keeps a small widget library: a two-column table whose first column is a label gutter and whose second column is one full-width widget, a helper that measures the widest label so every section of a dialog shares one gutter, and a red "Destroy" button. This article covers the form layout; the filter combo lives in [build searchable lists](../build-searchable-lists-and-browsers/overview.md) where the `ImInputString` pitfall is explained. Demonstrated by purrTTY. Prerequisite: StarMap.

## Background: how the game does it

Nothing in KSA is required; this is ImGui tables. `ImGuiTableFlags.NoPadOuterX` removes the table's outer horizontal padding and `SizingStretchProp` sizes stretch columns by weight, and `ImGuiTableColumnFlags.WidthFixed`/`WidthStretch` select fixed or stretch columns.[current/decomp/Brutal.ImGuiApi/ImGuiTableFlags.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiTableFlags.cs#L29-L37)[^brutal-imguiapi-imguitableflags] The dialogs are ordinary top-level windows opened with `ImGui.Begin(title, ref visible, flags)`; the title carries a `##` suffix that is hidden from display and keeps the id unique. Dear ImGui hashes the whole string for `##`, so the id changes if the visible text changes; use `###` when the displayed title must change while the id stays fixed.

## Lifecycle and timing

The dialogs are drawn from the mod's `[StarMapAfterGui]` method (`frame:draw-ui`), which pumps the theme dialog, layouts dialog and the in-world manager window every frame, and catches and logs exceptions so a UI bug cannot crash the game. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Pump the dialogs from one StarMap method** with a catch-all so one failing dialog cannot take the frame down:

```csharp
// purrTTY.GameMod/TerminalMod.cs L68-L82
[StarMapAfterGui]
public void OnAfterUi(double dt)
{
    if (!_isInitialized || _isDisposed)
    {
        return;
    }

    try
    {
        bool modalVisible = RenderToggleHotkeyModal();

        // The theme + in-world manager are non-modal windows now (drawn here / in
        // the in-world coordinator); they do not block the toggle hotkey.
        _themeDialog?.Render();
```
```csharp
// purrTTY.GameMod/TerminalMod.cs L119-L124
catch (Exception ex)
{
    ModLog.Log.Debug($"purrTTY GameMod OnAfterUi error: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
    // Don't let exceptions crash the game
}
```

2. **Open a table with a fixed label column.** `BeginFormTableFixed` pushes a 6 px cell padding, creates two columns (the label fixed at the measured width, the widget stretching), and pops the style var itself on failure. `EndFormTable` closes both:

```csharp
// purrTTY.GameMod/UI/ImGuiWidgets.cs L62-L84
public static bool BeginFormTableFixed(string id, float labelWidth)
{
    ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new float2(6f, 6f));
    bool open = ImGui.BeginTable(id, 2, ImGuiTableFlags.NoPadOuterX);
    if (open)
    {
        ImGui.TableSetupColumn($"{id}_l", ImGuiTableColumnFlags.WidthFixed, labelWidth);
        ImGui.TableSetupColumn($"{id}_w", ImGuiTableColumnFlags.WidthStretch, 1f);
    }
    else
    {
        ImGui.PopStyleVar();
    }

    return open;
}

/// <summary>Ends a <see cref="BeginFormTable"/> or <see cref="BeginFormTableFixed"/>.</summary>
public static void EndFormTable()
{
    ImGui.EndTable();
    ImGui.PopStyleVar();
}
```

3. **Measure the gutter once per dialog** from the set of labels so every form section lines up, then emit rows. `FormRow` is a frame-aligned label cell followed by `SetNextItemWidth(-1)`, so draw exactly one widget after it:

```csharp
// purrTTY.GameMod/UI/ImGuiWidgets.cs L92-L121
public static float MeasureLabelWidth(IReadOnlyList<string> labels)
{
    float max = 0f;
    for (int i = 0; i < labels.Count; i++)
    {
        float w = ImGui.CalcTextSize(labels[i]).X;
        if (w > max)
        {
            max = w;
        }
    }

    // Pad past the widest label so it never clips and leaves a gap before the widget
    // column (covers the cell's left/right padding plus a little breathing room).
    return max + 16f;
}

/// <summary>
///     Emits a label cell (frame-aligned) and advances to the full-width widget
///     cell. Draw exactly one widget immediately after.
/// </summary>
public static void FormRow(string label)
{
    ImGui.TableNextRow();
    ImGui.TableNextColumn();
    ImGui.AlignTextToFramePadding();
    ImGui.Text(label);
    ImGui.TableNextColumn();
    ImGui.SetNextItemWidth(-1f);
}
```

4. **Build the form**: each row is `FormRow("Label")` plus one widget:

```csharp
// purrTTY.GameMod/InWorld/UI/InWorldManagerUI.cs L225-L251
float labelWidth = ImGuiWidgets.MeasureLabelWidth(CreateLabels);

if (ImGuiWidgets.BeginFormTableFixed("##iw_create", labelWidth))
{
    ImGuiWidgets.FormRow("Name");
    ImGui.InputText("##iw_name", _nameInput, ImGuiInputTextFlags.None);
    _nameInput.EvaluateLength();

    ImGuiWidgets.FormRow("Shell");
    if (ImGuiWidgets.FilterCombo("##iw_shell", _draftShell?.Label ?? "Default Shell", _shellFilter, shellItems, out string? shellKey)
        && shellKey != null && int.TryParse(shellKey, out int shellIdx) && shellIdx < shells.Count)
    {
        _draftShell = shells[shellIdx];
    }

    // Optional command auto-run as stdin once the shell starts (e.g. a gatOS flight-computer TUI).
    // Draws its own two rows: the input, then a blank-label row for the History combo.
    DrawStartupCommandInput();

    ImGuiWidgets.FormRow("Columns");
    ImGui.DragInt("##iw_cols", ref _draftCols, 0.5f, 8, 400, "%d");

    ImGuiWidgets.FormRow("Rows");
    ImGui.DragInt("##iw_rows", ref _draftRows, 0.5f, 4, 200, "%d");

    ImGuiWidgets.FormRow("Theme");
    DrawThemePicker("##iw_create_theme", ref _draftThemeName);
```

5. **Mark destructive actions** with a red button that pushes four colours and pops them in the same helper, used for "Destroy" in the instance list:

```csharp
// purrTTY.GameMod/UI/ImGuiWidgets.cs L129-L141
public static bool DestructiveButton(string label, float2? size = null)
{
    ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(new float4(0.70f, 0.18f, 0.18f, 1f)));
    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, ImGui.GetColorU32(new float4(0.85f, 0.25f, 0.25f, 1f)));
    ImGui.PushStyleColor(ImGuiCol.ButtonActive, ImGui.GetColorU32(new float4(0.60f, 0.12f, 0.12f, 1f)));
    ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(new float4(0.97f, 0.97f, 0.97f, 1f)));
    bool clicked = ImGui.Button(label, size);
    ImGui.PopStyleColor();
    ImGui.PopStyleColor();
    ImGui.PopStyleColor();
    ImGui.PopStyleColor();
    return clicked;
}
```

6. **Open the window** with a first-use size, wider window padding, and a visible flag the close button clears:

```csharp
// purrTTY.GameMod/InWorld/UI/InWorldManagerUI.cs L111-L126
if (_requestFocus)
{
    _requestFocus = false;
    ImGui.SetNextWindowFocus();
}

ImGui.SetNextWindowSize(new float2(DialogWidth, 560f), ImGuiCond.FirstUseEver);
ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(16f, 16f));
bool visible = ImGui.Begin(PopupId, ref _visible, ImGuiWindowFlags.None);
ImGui.PopStyleVar();

if (!visible)
{
    ImGui.End();
    return;
}
```

## Complete example

| File | Role |
|---|---|
| [purrTTY.GameMod/UI/ImGuiWidgets.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/UI/ImGuiWidgets.cs#L12-L141) | Form-table, label-measure, row and button helpers |
| [purrTTY.GameMod/InWorld/UI/InWorldManagerUI.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/UI/InWorldManagerUI.cs#L105-L274) | A window using them |
| [purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L68-L125) | `[StarMapAfterGui]` pump |

## Variations and gotchas

- **Exactly one widget per `FormRow`.** The row consumes the label column and the next cell; two widgets overflow into the next row's cells.
- **`BeginFormTable` returns false** if the table cannot open; do not emit rows or call `EndFormTable` then.
- **Use `BeginFormTable` (stretch 1:3) for a single section and `BeginFormTableFixed`** when several sections in one window should share a gutter.
- **Text input in forms** needs `EvaluateLength()` after `InputText` before reading it; see [searchable lists](../build-searchable-lists-and-browsers/overview.md).
- **Push/pop discipline.** The destructive button pushes and pops four colours inside the helper; the tables pop their `CellPadding` in `EndFormTable`.
- **Version.** Checked against 2026.10.10.5554; the table flags are core ImGui.

## See also

- [Overview of build-a-settings-window](overview.md)
- [Approach 1: slider-plus-input rows and colour pickers](build-a-settings-window-1.md)
- [Build searchable lists and browsers](../build-searchable-lists-and-browsers/overview.md)
- [Persist mod settings as TOML](../../../persistence/settings-formats/persist-mod-settings-toml/overview.md)
- [List a mod in ModMenu with the ModMenuEntry attribute](../../menus/list-a-mod-in-modmenu/overview.md)
- Mod feature: [Filter combo and form widgets](../../../../mods/purrtty/features/imgui-filter-combo-and-form-widgets.md)

[^brutal-imguiapi-imguitableflags]: Brutal.ImGuiApi.ImGuiTableFlags (decompiled, build 2026.10.10.5554)
