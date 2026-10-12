---
type: Game Feature
title: Add a menu that uses ModMenu when present and the game menu bar otherwise
description: How to show one mod menu through ModMenu or through KSA's own menu bar or a standalone window depending on what is installed, without showing it twice
category: ui
group: menus
topic: add-a-menu-with-modmenu-fallback
demonstrated_by:
  - mods/purrtty/features/game-menu-with-modmenu-fallback
  - mods/gatos/features/modmenu-entry-with-fallback-menu
  - mods/kitten-engineer-redux/features/modmenu-entry-with-standalone-fallback
  - mods/ksa-public-telemetry/features/modmenu-entry-with-standalone-fallback
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types:
  - KSA.Program
  - KSA.ModLibrary
  - KSA.IGameViewportLifecycle
  - KSA.GameViewport
  - KSA.IGameViewport
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImString
tags: [modmenu, fallback, menu-bar, drawprogrammenushook, modlibrary-find, setmenubarinuse, harmony-postfix]
---
ModMenu is optional software, so a mod that lists itself only in ModMenu strands players who do not have it (see [List a mod in ModMenu](../list-a-mod-in-modmenu/overview.md)). The usual cure is to draw the same menu content two ways and pick one at run time: through ModMenu's `[ModMenuEntry]` method when ModMenu is installed, and through the game's own menu bar (or a standalone window) when it is not. Four reference mods do this: purrTTY and gatOS check for the ModMenu mod, Kitten Engineer Redux lets ModMenu say it has taken over, and KSA-Public-Telemetry renders the same settings tree either as a menu or as a window.

**Facts to hold constant** across these articles:

- KSA's menu bar is its own window, named "Menu Bar", built with `BeginMenuBar`. It is **not** Dear ImGui's main menu bar, so a mod that calls `BeginMainMenuBar` does not add to it.
- The supported extension point is the empty public method `Program.DrawProgramMenusHook`, called inside the bar after the last stock menu. A Harmony postfix on it draws in the real bar. It also fires for a hovered secondary game viewport's bar.
- ModMenu's own source is not in this corpus. How it finds and calls entries, and when, is attributed to comments in the mods (marked *unverified*).

## Engine background

| Piece | File | What it gives you |
|---|---|---|
| `Program.DrawMenuBar(IGameViewport, int)` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3455-L3492)[^ksa-ksa-program] | Opens the "Menu Bar" window and `BeginMenuBar`, draws the stock menus; with auto-hide on it returns early (before the hook) unless Alt is held, the mouse is near the top, or the bar is in use |
| `Program.DrawProgramMenusHook()` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4005-L4020) | Empty public method called inside the bar; the postfix target |
| `Program.OnDrawUiThreadSafe` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3178-L3189) | Draws the main viewport's bar only when `DrawUI` is true and the hovered viewport is the main one |
| `GameViewport.DrawImGui` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L252-L257)[^ksa-ksa-gameviewport] | A hovered secondary viewport whose active controller is not the fixed one draws its own copy of the bar |
| `IGameViewportLifecycle.SetMenuBarInUse(bool)` | [current/decomp/KSA/IGameViewportLifecycle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IGameViewportLifecycle.cs#L9)[^ksa-ksa-igameviewportlifecycle] | Keeps an auto-hidden bar visible while your menu is open. `GameViewport.SetMenuBarInUse` itself is `protected` ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L183-L191)); the public route is the interface cast |
| `ModLibrary.Find(string)` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L187-L190)[^ksa-ksa-modlibrary] | Looks a mod up by its id. `PrepareAll` registers every **enabled** manifest entry whose folder and valid `mod.toml` it finds ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L442)) before `LoadAll`, with `Mod.Id` set to the manifest entry id ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L103-L110))[^ksa-ksa-mod], so the probe is valid by `StarMapAllModsLoaded` |

**Presence checks compared**

| Check | Used by | Notes |
|---|---|---|
| `ModLibrary.Find("ModMenu") is not null` | purrTTY, gatOS | Engine API; sees only enabled mods in the manifest; assumes ModMenu's mod id is `ModMenu` (the three mods' shared assumption, not verified) |
| A static `bool` property that ModMenu sets, named in the attribute's second argument | Kitten Engineer Redux, KSA-Public-Telemetry | Needs the 1.0.2 attribute; if ModMenu never sets it, both menus appear (KER's own pitfall note) |
| Scan `AppDomain.CurrentDomain.GetAssemblies()` for one named `ModMenu` | KSAGolf, KSArmory, KSAStructures | No engine dependency; used to suppress a `BeginMainMenuBar` entry |

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [Decide once with ModLibrary.Find("ModMenu")](add-a-menu-with-modmenu-fallback-1.md) | purrTTY, gatOS | `harmony-postfix` | `startup:all-mods-loaded`, `frame:draw-ui` | Deterministic; one cached probe; patch must tolerate the hook disappearing |
| 2 | [ModMenuEntry with an active-flag property name](add-a-menu-with-modmenu-fallback-2.md) | Kitten Engineer Redux | `inter-mod-api` + `harmony-postfix` | `startup:all-mods-loaded`, `frame:draw-ui` | No `ModLibrary` call; depends on ModMenu honouring the second attribute argument |
| 3 | [One settings tree rendered as a menu or as a standalone window](add-a-menu-with-modmenu-fallback-3.md) | KSA-Public-Telemetry | `imgui-menu` | `frame:draw-ui` | No game-bar patch at all; fallback is a hotkey-toggled window, always available |

## Choosing

- Default: approach 1. The probe is an engine call, ModMenu does not need to cooperate, and the fallback sits in the real bar.
- Approach 2 if you already depend on the 1.0.2 attribute and want ModMenu itself to tell you it owns the entry; add the approach 1 probe if you cannot accept a duplicate menu on an older ModMenu.
- Approach 3 for a large settings tree that fits a window better than nested menus (menus close on click-away). It also works with no Harmony and no patch on the bar.
- Whatever you choose, the postfix runs only while the bar is drawn: not with the HUD hidden, not while an auto-hidden bar is hidden, and once per bar drawn (main and hovered secondary viewport). A hotkey ([Rebindable hotkeys](../../input/rebindable-hotkeys/overview.md)) is the safety net.

## See also

- [List a mod in ModMenu with the ModMenuEntry attribute](../list-a-mod-in-modmenu/overview.md)
- [Add a menu to the game's top menu bar](../add-items-to-the-game-menu-bar/overview.md)
- [Rebindable hotkeys](../../input/rebindable-hotkeys/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Isolate patch failures and fault-proof patch bodies](../../../patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md)

[^ksa-ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ksa-ksa-igameviewportlifecycle]: KSA.IGameViewportLifecycle (decompiled, build 2026.10.10.5554)
[^ksa-ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-ksa-mod]: KSA.Mod (decompiled, build 2026.10.10.5554)
