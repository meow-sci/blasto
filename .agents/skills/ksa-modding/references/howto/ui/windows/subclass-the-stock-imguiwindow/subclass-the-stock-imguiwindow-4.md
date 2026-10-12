---
type: How-To
title: "Stable window ids: Label###stable-id for renameable windows and a fixed id for plain windows"
description: Keep an ImGui window's identity (and the position and size ImGui remembers for it) constant while its visible title changes, using the id part after the title in the Begin name
category: ui
group: windows
topic: subclass-the-stock-imguiwindow
approach: 4
difficulty: beginner
prerequisites: [starmap]
demonstrated_by:
  - mods/purrtty/features/named-terminal-registry-and-theme-dialog
  - mods/ksastructures/features/settings-menu
techniques: [imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, KSA.ImGuiWindow]
tags: [window, imgui-id, rename, stable-id, imgui-ini, begin]
---
ImGui identifies a window by the string passed to `ImGui.Begin`, and remembers its position and size (in `imgui.ini`, and in its in-memory table) under that identity. If the string changes, ImGui sees a new window. Two mods needed a window whose
**visible title changes while its identity does not**: purrTTY lets the player rename terminal windows, and KSAStructures puts the mod version in its settings window's title. This article is the id convention both use and the one detail that matters: which separator to use.
Demonstrated by purrTTY and KSAStructures. Prerequisite: StarMap.

## Background: how the game does it

- **The stock window class does it for you.** `ImGuiWindow` builds its Begin name as `"Window###" + _debugName` and, when the title is set, `name + "###" + _debugName`, where `_debugName` is the type's full name plus an underscore. The text after `###` is the identity; the text before it is only what is shown. This is why renaming a stock window
  with `SetWindowTitle` keeps it the same window.[current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L89-L97)[^ksa-imguiwindow][current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L140-L144)[^ksa-imguiwindow]
- **Dear ImGui's rule** (library behaviour, not visible in the decompiled managed code): in a label, `##` hides everything after it from the display but keeps it in the hashed id *together with the label*, so `"A##x"` and `"B##x"` are different windows; `###` makes only the part from `###` onward the id, so `"A###x"` and `"B###x"` are the same window with different titles.
- A plain (non-`ImGuiWindow`) mod window gets no `NoSavedSettings` unless you pass it, so ImGui restores its position and size from `imgui.ini` under that id.

## Lifecycle and timing

Windows are drawn each frame from the GUI hook (`frame:draw-ui`) with the same Begin string; an id that changes between frames means a new window every time. Compute the Begin string when the window is created or renamed, not per frame, and hold it in a field. With the HUD hidden these windows
are not drawn. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Use `Title###StableId` where the title can change.** KSAStructures puts the version into the title and a fixed id after `###` so the window keeps its place across versions and builds. `AlwaysAutoResize` gives it a size from its content:

```csharp
// src/KSAStructures/Ksa/Ui/Ui.cs L79-L96
public void Draw()
{
    Current = this;
    if (!Visible) return;

    // ###id so the version can ride in the title without the window losing its place.
    if (ImGui.Begin($"{Title} {Build.Version}###KSAStructures", ref Visible, ImGuiWindowFlags.AlwaysAutoResize))
    {
        ImGui.Checkbox("First-person kitten (V)", ref settings.FirstPerson);
        Tip("See through the eyes of a kitten on foot. Hold the right mouse button and drag to look around.");

        ImGui.Checkbox("Office garage lamps", ref settings.OfficeLamps);
        Tip("The ceiling lamps under the office tower. Off, the garage has only what daylight reaches it.");

        ImGui.Separator();
        ImGui.TextDisabled("In the lift: 1 garage, 2 lobby, 3 office.");
        ImGui.TextDisabled("In the office chair: Enter plays the computer.");
        ImGui.Separator();
```

2. **Give tooltips wrapped text.** A bare `SetTooltip` never wraps; use `BeginItemTooltip` with a wrap width in font-size units:

```csharp
// src/KSAStructures/Ksa/Ui/Ui.cs L111-L123
// The width Dear ImGui's own help markers wrap at.
private const float TipWidthEms = 35f;

// What the control just drawn does, on hover. Wrapped, because a bare tooltip never is.
private static void Tip(string text)
{
    if (!ImGui.BeginItemTooltip()) return;

    ImGui.PushTextWrapPos(ImGui.GetFontSize() * TipWidthEms);
    ImGui.TextWrapped(text);
    ImGui.PopTextWrapPos();
    ImGui.EndTooltip();
}
```

3. **For a renameable window, put the identity in the id part and keep the Begin string in a field.** purrTTY names each terminal window `"{Name}##purrtty_window_{Id}"` and rebuilds the string when the player renames it:

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.cs L233-L248
public bool TryRename(string newName)
{
    string trimmed = newName.Trim();
    if (!TerminalTargetRegistry.IsNameAvailable(trimmed, this))
    {
        return false;
    }

    Name = trimmed;
    _imguiName = BuildImguiName();
    return true;
}

// Visible title carries the Name; the "##purrtty_window_{Id}" suffix is the
// stable ImGui id (unchanged across renames) that keys window position/size.
private string BuildImguiName() => $"{Name}##purrtty_window_{Id}";
```

4. **A subtle point for renaming.** purrTTY's comment says the `##purrtty_window_{Id}` suffix is the stable ImGui id. By the Dear ImGui rule above, `##` keeps the label in the hash, so the rename produces a different window id and ImGui's remembered placement would not follow it; use `###purrtty_window_{Id}` to get the
   behaviour the comment describes. (This is inferred from the ImGui rule and the stock `###` usage; not tested in game. purrTTY also restores geometry itself from its own saved state, which hides the difference when it re-places the window.) Use `##` when the only goal is to tell several same-titled windows apart.
5. **Draw with the stored string every frame:**

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.cs L385-L386
ImGui.Begin(_imguiName, flags);
ImGui.PopStyleVar(2);
```

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Ui/Ui.cs#L79-L123) | Fixed-id settings window and wrapped tooltips |
| [purrTTY.Display/Ghostty/TerminalWindow.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.cs#L233-L248) | Rename and Begin-name construction |

## Variations and gotchas

- **Never put a changing value before `##` if you wanted a fixed identity.** Use `###`.
- **Do not format the Begin string per frame** (clock, counters) unless you use `###`; a string that differs each frame is a new window each frame.
- **Disambiguating same-titled windows.** Use `##` plus a unique suffix, as purrTTY does for its per-window hot zone (`##purrtty_hotzone_{id}`).
- **Stock windows share ids per type.** Two open instances of one `ImGuiWindow` subclass share an id (see [approach 3](subclass-the-stock-imguiwindow-3.md)); name distinct windows distinctly.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of subclass-the-stock-imguiwindow](overview.md)
- [Approach 5: chrome-less plain-ImGui window](subclass-the-stock-imguiwindow-5.md)
- [Build a settings window](../build-a-settings-window/overview.md)
- [Persist ImGui window state and layouts](../../../persistence/user-data/persist-imgui-window-state/overview.md)
- Mod features: [Named terminal registry and theme dialog](../../../../mods/purrtty/features/named-terminal-registry-and-theme-dialog.md),
  [Settings menu](../../../../mods/ksastructures/features/settings-menu.md)

[^ksa-imguiwindow]: KSA.ImGuiWindow (decompiled, build 2026.10.10.5554)
