---
type: Mod Feature
title: Game menu with a ModMenu entry and a DrawProgramMenusHook fallback
description: Draw the same mod menu through the ModMenu companion's attribute when it is installed, otherwise through a Harmony postfix on KSA's empty Program.DrawProgramMenusHook, keeping the draw path allocation-free and detection-free
mod: purrtty
category: ui
techniques: [imgui-menu, harmony-postfix, background-thread, inter-mod-api]
hooks: [harmony-postfix KSA.Program.DrawProgramMenusHook, attribute ModMenu.ModMenuEntry]
lifecycle: [frame:draw-ui, startup:all-mods-loaded, thread:background]
game_types: [KSA.Program, KSA.IGameViewportLifecycle, KSA.GameViewport, KSA.ModLibrary, Brutal.ImGuiApi.ImGui]
tags: [imgui, menu, modmenu, harmony, menu-bar, performance]
---

purrTTY adds a "purrTTY" menu to KSA's top menu bar: Toggle Terminal (with the hotkey shown),
Toggle Hotkey, In-World Terminals, Layouts, New Tab and New Window (with one item per detected
shell, WSL distribution and other mods' custom shells), Theme and a Window submenu. If the ModMenu
companion mod is installed the menu is registered through its attribute; otherwise purrTTY injects
it itself through the game's own empty extension point. Both paths call the same
`TerminalMenus.DrawMenuContent()`.

## How it works

- **ModMenu path.** `TerminalMod.DrawMenu` is a static method tagged `[ModMenuEntry("purrTTY")]`
  from the `ModMenu.Attributes` NuGet package; the ModMenu mod discovers and calls it. (ModMenu is
  not part of this corpus, so its discovery mechanism is not verified here.)[^terminal-mod][^gamemod-csproj]
- **Fallback path.** `Patch02` is a Harmony postfix on `Program.DrawProgramMenusHook()`, a public
  method with an empty body that `Program.DrawMenuBar` calls right after its own menus.[^ksa-program]
  The postfix returns early when `ModLibrary.Find("ModMenu")` finds the companion (cached in a
  static `bool?` that `unload` resets), else draws `BeginMenu("purrTTY")`. While the menu is open it
  calls `((IGameViewportLifecycle)Program.MainViewport).SetMenuBarInUse(true)` so KSA's auto-hiding
  menu bar stays visible.[^patcher] This replaced an earlier IL transpiler on `DrawMenuBar` (gotcha 21).
- **Static seam.** `TerminalMenus` exposes static delegate fields (`Toggle`, `OpenThemeDialog`,
  `OpenInWorldManager`, `MenuController`, ...) that the mod instance assigns at init and clears on
  dispose; items whose delegate is null simply do not appear.[^menus]
- **Never detect on the draw path.** Shell detection (PATH probes, `wsl --list`, `/etc/shells`) can
  take seconds, so `ShellMenuCache.BeginDetection()` runs it once on a `Task.Run` thread at init and
  publishes an immutable snapshot through a `volatile` field. Until it lands, the menus show
  "Detecting shells..." plus Game Console. A detection failure falls back to Default Shell and Game
  Console.[^shell-menu-cache]
- **Other mods' shells, live.** `DrawRegisteredCustomShellItems` enumerates
  `CustomShellRegistry.Instance.GetAvailableShells()` on every draw, so a shell another mod (gatOS)
  registers after purrTTY's init appears without a refresh hook (see
  [custom shell registry](./custom-shell-registry-and-lifecycle.md)).
- **Allocation-free.** The cached shortcut string, static lambdas that read `MenuController`, and
  indexed loops keep the per-frame menu path from allocating.

## Lifecycle and timing

| Piece | When |
|---|---|
| `Patcher.patch()` applies `Patch02` | `startup:all-mods-loaded` (`OnFullyLoaded`) |
| `ShellMenuCache.BeginDetection()` | Called in `InitializeTerminal`; the work runs on a pool thread (`thread:background`), published via a volatile reference |
| `DrawProgramMenusHook` postfix | `frame:draw-ui`, inside `DrawMenuBar`, which returns early when the auto-hidden bar is not shown. The main bar is drawn from `Program.OnDrawUiThreadSafe` -> `DrawMenuBar(MainViewport, ...)` only when `DrawUI` is true and the hovered viewport is the main one; that is after purrTTY's own `StarMapAfterGui` work, so menu actions take visible effect next frame.[^ksa-program] A hovered secondary game viewport draws its own copy of the bar (and so of the purrTTY menu) from `GameViewport.DrawImGui` inside `OnDrawUiViewports`, which runs before the `StarMapAfterGui` postfix of the same call[^ksa-gameviewport] |

## Key code

| Path | Role |
|---|---|
| [purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L88-L128) | `Patch02`: postfix, ModMenu probe, `SetMenuBarInUse` |
| [purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L58-L62) | `[ModMenuEntry("purrTTY")]` entry |
| [purrTTY.GameMod/TerminalMenus.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMenus.cs#L83-L147) | Shared menu content |
| [purrTTY.GameMod/TerminalMenus.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMenus.cs#L149-L218) | Shell items from the snapshot plus live custom shells |
| [purrTTY.GameMod/ShellMenuCache.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/ShellMenuCache.cs#L34-L107) | One-shot background detection and snapshot |
| [purrTTY.GameMod/purrTTY.GameMod.csproj](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/purrTTY.GameMod.csproj#L97-L101) | `ModMenu.Attributes` package reference |

## Snippets

```csharp
// purrTTY.GameMod/Patcher.cs L95-L127
[HarmonyPatch(typeof(KSA.Program), nameof(KSA.Program.DrawProgramMenusHook))]
static class Patch02
{
  private static bool? s_isModMenuEnabled;

  /// <summary>Drops the cached ModMenu-presence probe (called on unload; see Patcher.unload).</summary>
  internal static void Reset() => s_isModMenuEnabled = null;

  [HarmonyPostfix]
  public static void Postfix()
  {
    if (IsModMenuEnabled())
    {
      return;
    }

    if (ImGui.BeginMenu("purrTTY"))
    {
      // Keep the (possibly auto-hidden) menu bar shown while our menu is open.
      // KSA 5402 made IGameViewport.MenuBarInUse read-only; the setter lives on the
      // public IGameViewportLifecycle interface (same call KSA's DrawMenuBar makes).
      ((IGameViewportLifecycle)Program.MainViewport).SetMenuBarInUse(true);

      TerminalMenus.DrawMenuContent();

      ImGui.EndMenu();
    }
  }

  private static bool IsModMenuEnabled()
  {
    return s_isModMenuEnabled ??= ModLibrary.Find("ModMenu") is not null;
  }
```

```csharp
// current/decomp/KSA/Program.cs L4005-L4020
			DrawProgramMenusHook();
			if (viewport.IsMain())
			{
				ImGui.SetCursorPosY(cursorPosY);
				ImGui.SetCursorPosX((float)windowWidth - ImGui.CalcTextSize(VersionInfo.Current.VersionString).X - 10f);
				ImGui.TextColored(in Color.Gray.AsFloat4, VersionInfo.Current.VersionString);
			}
			ImGui.EndMenuBar();
		}
		ImGui.End();
		ConsoleStyle.PopWidgetStyle();
	}

	public void DrawProgramMenusHook()
	{
	}
```

## Game APIs touched

| API | Where | Purpose |
|---|---|---|
| `Program.DrawProgramMenusHook()` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4018-L4020) | Empty public menu-bar extension point; postfixed |
| `Program.DrawMenuBar(IGameViewport, int)` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3455-L3474) | Auto-hide logic and the call site |
| `Program.OnDrawUiThreadSafe` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3178-L3189) | Calls `DrawMenuBar` for the main viewport when it is hovered |
| `GameViewport.DrawImGui` | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L252-L257) | Calls `DrawMenuBar` for a hovered secondary game viewport (from `OnDrawUiViewports`) |
| `IGameViewportLifecycle.SetMenuBarInUse(bool)` | [current/decomp/KSA/IGameViewportLifecycle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IGameViewportLifecycle.cs) | Keeps the auto-hidden bar open[^ksa-viewport-lifecycle] |
| `ModLibrary.Find(string)` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L187-L190) | Presence check for the ModMenu mod[^ksa-modlibrary] |

## Pitfalls and version notes

- KSA 5402 made `IGameViewport.MenuBarInUse` read-only; the setter is on `IGameViewportLifecycle`
  (comment in `Patch02`). Casting `Program.MainViewport` to that interface is what the game itself does.
- Cache the ModMenu probe. purrTTY also resets it on unload in case a loader reloads the assembly in
  place; StarMap 4dc60f8 never does.
- The game's menu bar is an ordinary ImGui window (`ImGui.Begin("Menu Bar", ... MenuBar)` plus
  `BeginMenuBar`), not `BeginMainMenuBar`, and the same `DrawMenuBar` serves secondary viewports.
  `Patch02` always calls `SetMenuBarInUse(true)` on `Program.MainViewport`, also when its menu was
  opened from a secondary viewport's bar.
- Anything slow in a menu callback freezes the frame; precompute in the background and read a
  snapshot (the WSL probe alone can block for many seconds).
- The fallback postfix is optional in `Patcher`: if `DrawProgramMenusHook` disappears, the mod keeps
  working with only the hotkey (see [failure-isolated patching](./failure-isolated-harmony-patching.md)).

## See also

- [Rebindable toggle hotkey](./rebindable-toggle-hotkey.md)
- [Shell detection and command resolution](./shell-detection-and-command-resolution.md)
- [Custom shell registry and lifecycle](./custom-shell-registry-and-lifecycle.md)
- [Failure-isolated Harmony patching](./failure-isolated-harmony-patching.md)
- [purrTTY overview](../overview.md)
- [Show the same menu through ModMenu or a Harmony postfix, chosen once with ModLibrary.Find](../../../howto/ui/menus/add-a-menu-with-modmenu-fallback/add-a-menu-with-modmenu-fallback-1.md) - how-to article demonstrating this feature
- [Add a menu with a Harmony postfix on Program.DrawProgramMenusHook](../../../howto/ui/menus/add-items-to-the-game-menu-bar/add-items-to-the-game-menu-bar-1.md) - how-to article demonstrating this feature

[^terminal-mod]: purrTTY.GameMod/TerminalMod.cs
[^gamemod-csproj]: purrTTY.GameMod/purrTTY.GameMod.csproj
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^patcher]: purrTTY.GameMod/Patcher.cs
[^menus]: purrTTY.GameMod/TerminalMenus.cs
[^shell-menu-cache]: purrTTY.GameMod/ShellMenuCache.cs
[^ksa-viewport-lifecycle]: KSA.IGameViewportLifecycle (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
