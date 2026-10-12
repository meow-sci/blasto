---
type: Mod Feature
title: ModMenu entry with a fallback menu
description: Draw the same mod menu through ModMenu's ModMenuEntry attribute when ModMenu is installed and through a Harmony postfix on KSA's empty Program.DrawProgramMenusHook when it is not, plus a status window and a confirmation modal that never block on background work
mod: gatos
category: ui
techniques: [imgui-menu, imgui-window, harmony-postfix, starmap-lifecycle]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-postfix KSA.Program.DrawProgramMenusHook
  - starmap StarMapAfterGui
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.Program, KSA.ModLibrary, KSA.IGameViewportLifecycle]
tags: [imgui, menu, modmenu, harmony, status-window, optional-dependency]
---

gatOS has a "gatOS" menu (Status, Start VM, Shut Down VM, Telemetry rates and gates, Restart SimFs,
Open Data Folder, Reset Disk) and a status window with VM state, accelerator, ports, uptime,
transport counters and perf stats. ModMenu is optional, so the mod draws the identical menu two
ways and picks one at runtime. The pattern also shows how to keep the menu alive when
initialization fails so the player can see why.

## How it works

- **ModMenu path.** A static method marked `[ModMenuEntry("gatOS")]` (from the
  `ModMenu.Attributes` NuGet package) calls the shared `DrawMenuContentSafe()`
  ([gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L160-L184)).
- **Fallback path.** At load, `InstallMenuFallback` patches `Program.DrawProgramMenusHook()` with a
  postfix on a dedicated `Harmony("gatos.menu")` ([gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L901-L923)).
  KSA declares this method empty and calls it from `Program.DrawMenuBar` right before the version
  label and `ImGui.EndMenuBar()` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3994-L4020)), which makes it a
  purpose-built extension point. The menu bar is its own borderless ImGui window ("Menu Bar", with
  `ImGuiWindowFlags.MenuBar`, positioned over the main viewport) whose items sit between
  `ImGui.BeginMenuBar()` and `EndMenuBar()`, so `ImGui.BeginMenu` inside the hook adds a top-level
  menu next to KSA's own ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3455-L3488)). The postfix returns immediately if
  `ModLibrary.Find("ModMenu")` is non-null (cached); otherwise it opens `ImGui.BeginMenu("gatOS")`,
  calls `SetMenuBarInUse(true)` on the main viewport so an auto-hidden menu bar stays visible while
  the menu is open, and draws the shared content ([gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L192-L213)).
- **Survives failed init.** The fallback is installed at the top of `OnFullyLoaded`, before any
  early return, and the static `_instance` is published first; if init failed, the menu shows "gatOS
  did not initialize (see the game log)".
- **Never blocks.** Menu items start background tasks (VM start/stop, disk reset, SimFs restart);
  the status window reads only volatile cached state such as `VmHost.Status`
  ([gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L1060-L1072)). Reset Disk uses `ImGui.BeginPopupModal` with
  an explicit confirmation.
- **Fault isolation.** Per-frame draw errors are swallowed and logged at debug level; an error that
  prevents entering the UI disables it for the session (`_uiDead`).

## Lifecycle and timing

- `[StarMapAllModsLoaded]` installs the Harmony postfix.
- The menu draws inside `Program.DrawMenuBar`, which `Program.OnDrawUiThreadSafe` calls only when
  `DrawUI` is true and the hovered viewport is the main one (`frame:draw-ui`,
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3178-L3189)). That is after the `StarMapAfterGui` postfix on
  `OnDrawUiViewports`, so in the ImGui window order the status window and modal (drawn from
  `[StarMapAfterGui]` by `DrawGameUi`) are submitted before the menu bar. `DrawMenuBar` returns
  before calling the hook when auto-hide is on, Alt is not held and either the mouse is below the bar
  (with no menu in use) or the main viewport is in the free camera mode, so the fallback menu is only
  drawn while the bar is visible.
- `[StarMapUnload]` calls `RemoveMenuFallback`, which unpatches `gatos.menu` and resets the cached
  ModMenu probe. In StarMap 4dc60f8 the unpatch is a no-op at shutdown: `StarMapCore.DeInit` runs
  `Harmony.UnpatchAll()` with no id, removing every mod's patches, before `ModLoader.Dispose`
  invokes the `StarMapUnload` methods ([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35)).

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L160-L213) | ModMenu entry, shared safe draw, fallback postfix, ModMenu probe |
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L901-L938) | install and remove the fallback patch |
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L968-L1060) | menu content and telemetry submenu |
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L1326-L1366) | reset-disk confirmation modal |
| [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L212-L226) | install order at the top of `OnFullyLoaded` |

## Snippets

```csharp
// gatOS.GameMod/Game/Mod.Game.cs L192-L213
    private static void MenuFallbackPostfix()
    {
        try
        {
            if (IsModMenuPresent())
                return;

            if (ImGui.BeginMenu("gatOS"))
            {
                // Keep the (possibly auto-hidden) menu bar shown while our menu is open.
                ((IGameViewportLifecycle)Program.MainViewport).SetMenuBarInUse(true);
                DrawMenuContentSafe();
                ImGui.EndMenu();
            }
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS fallback menu draw error: {ex.Message}");
        }
    }

    private static bool IsModMenuPresent() => _modMenuPresent ??= ModLibrary.Find("ModMenu") is not null;
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Program.DrawProgramMenusHook()` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4018-L4020) | empty hook called inside the menu bar; Harmony postfix target |
| `Program.DrawMenuBar(IGameViewport, int)` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3455-L3460) | the caller, inside `ImGui.BeginMenuBar` |
| `ModLibrary.Find(string)` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L187-L190) | detect ModMenu |
| `IGameViewportLifecycle.SetMenuBarInUse(bool)` | [current/decomp/KSA/IGameViewportLifecycle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IGameViewportLifecycle.cs) | keep an auto-hidden menu bar open |
| `Program.MainViewport` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs) | viewport cast to `IGameViewportLifecycle` |

## Pitfalls and version notes

- Without the `ModLibrary.Find("ModMenu")` check both paths would draw and the menu would appear twice.
- `SetMenuBarInUse` is protected on `GameViewport`; gatOS reaches it through the public
  `IGameViewportLifecycle` interface cast.
- `DrawProgramMenusHook` exists unchanged in 5554.

## See also

- [TOML config seeded from a shipped template](toml-config-seeded-template.md)
- [Logging shim and perf stats](logging-and-perfstats.md)
- [Compile-gated game seam](compile-gated-game-seam.md)
- [Frame hooks with an F2-proof fallback](frame-hooks-and-f2-fallback.md)
- [gatOS overview](../overview.md)
- [Show the same menu through ModMenu or a Harmony postfix, chosen once with ModLibrary.Find](../../../howto/ui/menus/add-a-menu-with-modmenu-fallback/add-a-menu-with-modmenu-fallback-1.md) - how-to article demonstrating this feature

