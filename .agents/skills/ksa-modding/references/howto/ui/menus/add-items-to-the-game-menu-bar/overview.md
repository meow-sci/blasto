---
type: Game Feature
title: Add a menu to the game's top menu bar
description: How KSA draws its menu bar as its own Menu Bar window, the supported DrawProgramMenusHook extension point for adding menus, and the older BeginMainMenuBar approach that does not join it
category: ui
group: menus
topic: add-items-to-the-game-menu-bar
demonstrated_by:
  - mods/unscience/features/unscience-menu-bar-entry
  - mods/ksa-deltavmap/features/map-window-and-menu-entry
  - mods/ksa-measuretools/features/measure-menu-and-tool-window
  - mods/purrtty/features/game-menu-with-modmenu-fallback
  - mods/ksagolf/features/developer-panel-and-menu-bar
  - mods/ksastructures/features/settings-menu
  - mods/ksarmory/features/operator-panel-imgui
  - mods/unscience/features/jplrepo-menu-bar-animation
lifecycle: [frame:draw-ui, startup:all-mods-loaded]
game_types: [KSA.Program, KSA.IGameViewportLifecycle, KSA.GameViewport, Brutal.ImGuiApi.ImGui]
tags: [menu-bar, menu, drawprogrammenushook, harmony-postfix, setmenubarinuse, beginmainmenubar]
---
A mod that opens a window needs a way for the player to open it. The game's top bar is the obvious place, and it works in both flight and the vehicle editor. The thing to know is what that bar is: KSA does not use Dear ImGui's main menu bar. `Program.DrawMenuBar` draws a normal window named "Menu Bar" (flags `NoTitleBar | NoResize | NoMove | NoScrollbar | NoBackground | NoSavedSettings | MenuBar`) and fills it with `BeginMenuBar` and `BeginMenu("File")`, then calls an intentionally empty public method, `DrawProgramMenusHook()`, before it right-aligns the version string. That empty method is the supported extension point. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Engine background

| Piece | File | What it gives you |
|---|---|---|
| `Program.DrawMenuBar(IGameViewport, int)` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3455-L3492)[^ksa-program] | The "Menu Bar" window and its stock menus; opens with `BeginMenuBar` |
| `Program.DrawProgramMenusHook()` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4005-L4020)[^ksa-program] | Empty public instance method called inside `BeginMenuBar`, after the last stock menu and before the version string; a postfix draws in menu-bar context |
| `Program.OnDrawUiThreadSafe` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3178-L3189)[^ksa-program] | Calls `DrawMenuBar(MainViewport, ...)` only when `DrawUI` is true and `HoveredViewport.IsMain()` |
| `GameViewport.DrawImGui` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L252-L257)[^ksa-gameviewport] | A hovered secondary game viewport draws its own bar with `Program.Instance.DrawMenuBar(this, Size.X)` |
| `IGameViewportLifecycle.SetMenuBarInUse(bool)` | [current/decomp/KSA/IGameViewportLifecycle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IGameViewportLifecycle.cs#L9)[^ksa-igameviewportlifecycle] | Keeps an auto-hidden bar visible while a menu is open; the game's own menus call it |
| Auto-hide | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3468-L3478)[^ksa-program] | With auto-hide on, the bar returns early (before the hook) unless Alt is held, the mouse is near the top, or the bar is "in use" |

**What this means for a mod.**

- The hook exists only for mods. It runs inside the real bar, so `BeginMenu`/`MenuItem` work directly and the entry sits beside File and the others.
- It is called once per bar drawn: the main bar and the bar of a hovered secondary viewport. Keep the postfix idempotent per call.
- It does not run when the HUD is hidden (`DrawUI == false`), when the bar is auto-hidden, or when the mouse is over another viewport.
- `BeginMainMenuBar` is something KSA never calls (no occurrence in the decompiled `KSA` sources), so a mod that calls it gets Dear ImGui's own bar window, not KSA's (approach 2).
- The bar is drawn in `OnDrawUiThreadSafe`, after `[StarMapAfterGui]` in the same frame, so a click on a menu item takes effect on the next frame's window draw.

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [Harmony postfix on the empty Program.DrawProgramMenusHook](add-items-to-the-game-menu-bar-1.md) | unscience, KSA-DeltaVMap, KSA-MeasureTools, purrTTY | `harmony-postfix`, lazy window, `SetMenuBarInUse` | `frame:draw-ui` | Preferred; needs Harmony and a method name that may change |
| 2 | [BeginMainMenuBar from StarMapBeforeGui](add-items-to-the-game-menu-bar-2.md) | KSAGolf, KSAStructures, KSArmory | `starmap-lifecycle` | `frame:draw-ui` | No Harmony; does not join KSA's bar, fragile |
| 3 | [Animated coloured menu-bar words via transpiler plus prefix](add-items-to-the-game-menu-bar-3.md) | unscience (jplrepo) | `harmony-transpiler` snapshot + hook prefix | `frame:draw-ui` | Restyles the bar; IL anchor can break, timing caveat |

## Choosing

- Almost always approach 1. It is two attributes and a loop of `MenuItem`s.
- Approach 2 only as a historical reference (it appears in three reference mods that predate the verified hook); see its article for what it actually does.
- Approach 3 only when you want to decorate the bar itself, not just add a menu.
- If you also want ModMenu users to see the entry in the shared "Mods" menu, combine approach 1 with the fallback pattern in [Add a menu that uses ModMenu when present and the game menu bar otherwise](../add-a-menu-with-modmenu-fallback/overview.md).

## See also

- [List a mod in ModMenu with the ModMenuEntry attribute](../list-a-mod-in-modmenu/overview.md)
- [Add a menu that uses ModMenu when present and the game menu bar otherwise](../add-a-menu-with-modmenu-fallback/overview.md)
- [Build a mod window on the game's ImGuiWindow base class, with stable ids and saved placement](../../../../howto/ui/windows/subclass-the-stock-imguiwindow/overview.md)
- [Build a mod settings window (and developer panel)](../../../../howto/ui/windows/build-a-settings-window/overview.md)
- [Build a mod window on the game's ImGuiWindow base class, with stable ids and saved placement](../../windows/subclass-the-stock-imguiwindow/overview.md) - related topic

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ksa-igameviewportlifecycle]: KSA.IGameViewportLifecycle (decompiled, build 2026.10.10.5554)
