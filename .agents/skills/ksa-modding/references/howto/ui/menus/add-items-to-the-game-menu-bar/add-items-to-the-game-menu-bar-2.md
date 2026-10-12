---
type: How-To
title: Add a menu with BeginMainMenuBar from StarMapBeforeGui, and why it is not KSA's bar
description: The older approach of opening ImGui's main menu bar before the game's UI pass, what it actually produces in KSA (a separate bar window), when it is still acceptable and why the DrawProgramMenusHook postfix is preferred
category: ui
group: menus
topic: add-items-to-the-game-menu-bar
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by:
  - mods/ksagolf/features/developer-panel-and-menu-bar
  - mods/ksastructures/features/settings-menu
  - mods/ksarmory/features/operator-panel-imgui
techniques: [starmap-lifecycle, imgui-menu]
hooks: [starmap StarMapBeforeGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Program, KSA.GameViewport]
tags: [menu-bar, beginmainmenubar, starmapbeforegui, legacy, reopen-button]
---
Three Laurens-de-V mods (KSAGolf, KSAStructures, KSArmory) add their menu with plain Dear ImGui calls from `[StarMapBeforeGui]`: `BeginMainMenuBar`, `BeginMenu("KSAGolf")`, `MenuItem`, `EndMenu`, `EndMainMenuBar`. Their comments say this "opens the main menu bar before KSA fills it, so this mod's entry sits beside File and Universe". That is not what the decompiled game does: KSA never calls `BeginMainMenuBar`, so the entry lands in Dear ImGui's own main-menu-bar window, a different window from KSA's "Menu Bar". The pattern needs no Harmony and is documented here so you can recognize it and decide whether it is acceptable. Demonstrated by [Developer panel and menu-bar entry](../../../../mods/ksagolf/features/developer-panel-and-menu-bar.md), [Settings menu entry, settings window and optional ModMenu listing](../../../../mods/ksastructures/features/settings-menu.md) and [Operator panel in ImGui with a menu-bar entry and ModMenu registration](../../../../mods/ksarmory/features/operator-panel-imgui.md).

## Background: how the game does it

- KSA's bar is a window named "Menu Bar" with `ImGuiWindowFlags.MenuBar`, filled with `ImGui.BeginMenuBar()` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3481-L3487)[^ksa-program]). The decompiled `KSA` namespace contains no call to `BeginMainMenuBar`.
- `ImGui.BeginMainMenuBar()` and `EndMainMenuBar()` are bound in the Brutal wrapper ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7419-L7422)[^brutal-imguiapi-imgui]). Upstream Dear ImGui implements them as a separate bar attached to the top of the main viewport, so what a mod gets is a second strip that is not part of KSA's bar. How it stacks against KSA's window on screen (they occupy the same top-left area) was not tested here.
- Because the entry is not in KSA's window, KSA's menu-bar auto-hide ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3468-L3478)[^ksa-program]) does not hide it, and `SetMenuBarInUse` has no effect on it.
- The mods' comments claim two further things this corpus could not verify: that a later `BeginMainMenuBar` appends to the earlier one in the same frame (which is Dear ImGui behaviour for repeated `Begin` of one window), and that calling it from `[StarMapAfterGui]` returns false. KSArmory also says the mod called ModMenu's approach "transpiling `Program.DrawMenuBar`"; ModMenu is not in this corpus.

## Lifecycle and timing

`[StarMapBeforeGui]` is StarMap's Harmony prefix on `Program.OnDrawUiFrame`, inside `if (DrawUI)` in `Program.OnFrame` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2262-L2269)[^ksa-program]): `frame:draw-ui`, after `PrepareImGui` and before `ImGui.Render`. It is skipped while the HUD is hidden. KSA draws its own bar later in the same frame, in `OnDrawUiThreadSafe`, so drawing "before KSA fills the bar" does not place the mod's menu in it. See [Game loop and mod lifecycle](../../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Add a `[StarMapBeforeGui]` method** that calls the menu draw and swallows exceptions (it runs inside the game's GUI pass):

```csharp
// src/KSAGolf/Ksa/KSAGolfMod.cs L150-L157
[StarMapBeforeGui]
public void OnBeforeGui(double dt)
{
    if (_disabled || _ui is null) return;

    try { _ui.DrawMenuBarEntry(); }
    catch { /* Cosmetic. Never take KSA's GUI pass down for a menu item. */ }
}
```

2. **Open the bar, draw the menu, close the bar.** Only call `EndMainMenuBar` when `BeginMainMenuBar` returned true, and `EndMenu` only when `BeginMenu` did:

```csharp
// src/KSAGolf/Ksa/Ui/Ui.cs L27-L55
public void DrawMenuBarEntry()
{
    // ModMenu draws this mod's entry when it is installed; a second would list it twice.
    if (ModMenuPresence.Installed) return;

    try
    {
        if (!ImGui.BeginMainMenuBar()) return;

        if (ImGui.BeginMenu("KSAGolf"))
        {
            DrawMenuContents();
            ImGui.EndMenu();
        }

        ImGui.EndMainMenuBar();
    }
    catch (Exception e)
    {
        // An exception here happens inside KSA's own GUI pass; a missing menu is only cosmetic.
        if (_warnedMenuBar) return;

        _warnedMenuBar = true;
        Log.Warn($"menu bar entry failed, use the floating button: {e.Message}");
    }
}

private bool _warnedMenuBar;

```

3. **Skip the entry when ModMenu is loaded** so the mod is not listed twice (the check is covered in [Add a menu that uses ModMenu when present and the game menu bar otherwise](../add-a-menu-with-modmenu-fallback/overview.md)): the first line of the method above.

4. **Always provide a way back that does not depend on the bar.** KSArmory's panel can be closed from inside itself, so when it is closed and the player enabled the option, a small title-less window with one button reopens it. The mod's own comment explains why: appending to KSA's bar is "ImGui behaviour rather than a supported hook":

```csharp
// src/KSArmory/Ksa/Ui/Ui.cs L276-L294
else if (_config.FloatingPanelButton)
{
    // Closing the panel must not strand the operator with no way back, and the button is
    // the only route this mod controls. Both others are somebody else's: appending to
    // KSA's bar is ImGui behaviour rather than a supported hook, and ModMenu's entry is
    // another mod's menu reached by transpiling a private method.
    //
    // Drawn whenever it is wanted rather than being suppressed where ModMenu would
    // provide a route: the recovery cannot be conditional on a third party working,
    // because there is no way back from being wrong -- the setting that would switch this
    // on lives *inside* the panel that is shut.
    if (ImGui.Begin("KSArmory##reopen",
                    ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
    {
        if (ImGui.Button("KSArmory")) Visible = true;
    }

    ImGui.End();
}
```

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Ui/Ui.cs#L21-L55) | Menu-bar entry |
| [src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L147-L189) | `OnBeforeGui` and `OnAfterGui` |
| [src/KSAStructures/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Ui/Ui.cs#L19-L51) | Same entry in KSAStructures |
| [src/KSArmory/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/Ui.cs#L109-L156) | Same entry in KSArmory, with the longest rationale comment |

## Variations and gotchas

- **Prefer the hook.** The approach 1 postfix puts the entry inside KSA's bar, honors its auto-hide, works with `SetMenuBarInUse` and is two attributes long. This approach is shown for completeness and for reading existing mods.
- **When it can still be acceptable.** A mod that must not use Harmony, accepts a second bar strip, and ships a floating reopen button as the guaranteed route. Do not make the strip the only route.
- **Never rely on the claim "beside File and Universe".** The mods' comments are wrong about that for this build; treat the entry as a separate bar.
- **Exceptions.** Wrap the draw; a throw here is inside the GUI pass and, unwrapped, skips later windows.
- **Version.** Verified for 2026.10.10.5554: KSA draws "Menu Bar" itself and never calls `BeginMainMenuBar`.

## See also

- [Overview of the menu bar](overview.md)
- [Approach 1: Harmony postfix on DrawProgramMenusHook](add-items-to-the-game-menu-bar-1.md)
- [Build a mod settings window (and developer panel)](../../../../howto/ui/windows/build-a-settings-window/overview.md)
- Mod feature: [Developer panel and menu-bar entry](../../../../mods/ksagolf/features/developer-panel-and-menu-bar.md)
- Mod feature: [Settings menu entry, settings window and optional ModMenu listing](../../../../mods/ksastructures/features/settings-menu.md)
- Mod feature: [Operator panel in ImGui with a menu-bar entry and ModMenu registration](../../../../mods/ksarmory/features/operator-panel-imgui.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
