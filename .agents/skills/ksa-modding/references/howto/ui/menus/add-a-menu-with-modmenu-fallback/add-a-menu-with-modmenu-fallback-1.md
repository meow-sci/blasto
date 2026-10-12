---
type: How-To
title: Show the same menu through ModMenu or a Harmony postfix, chosen once with ModLibrary.Find
description: Share one menu-content function between a ModMenuEntry method and a postfix on Program.DrawProgramMenusHook that draws only when ModMenu is not loaded, keeping the menu bar visible and the draw path allocation-light
category: ui
group: menus
topic: add-a-menu-with-modmenu-fallback
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/purrtty/features/game-menu-with-modmenu-fallback
  - mods/gatos/features/modmenu-entry-with-fallback-menu
techniques: [harmony-postfix, imgui-menu, inter-mod-api]
hooks:
  - harmony-postfix KSA.Program.DrawProgramMenusHook
  - modmenu ModMenuEntry
  - starmap StarMapAllModsLoaded
  - starmap StarMapUnload
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.Program, KSA.ModLibrary, KSA.IGameViewportLifecycle, KSA.GameViewport]
tags: [modmenu, fallback, drawprogrammenushook, modlibrary-find, setmenubarinuse, harmony, menu-bar]
---
The goal: a mod's menu is always reachable, and never shown twice. purrTTY and gatOS both write the menu once, expose it through `[ModMenuEntry]` for ModMenu users, and install a Harmony postfix on `Program.DrawProgramMenusHook` that draws the same content in KSA's own menu bar only when the ModMenu mod is not loaded. The presence test is one cached `ModLibrary.Find("ModMenu")`. Prerequisites: a StarMap mod and `HarmonyLib`. Demonstrated by [Game menu with a ModMenu entry and a DrawProgramMenusHook fallback](../../../../mods/purrtty/features/game-menu-with-modmenu-fallback.md) and [ModMenu entry with a fallback menu (gatOS)](../../../../mods/gatos/features/modmenu-entry-with-fallback-menu.md).

## Background: how the game does it

`Program.DrawMenuBar` opens a window named "Menu Bar" and `BeginMenuBar`, draws the stock menus, then calls the empty public `DrawProgramMenusHook()` before the version string, and ends the bar:

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

([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3455-L3492)[^ksa-ksa-program]). Because the hook runs inside `BeginMenuBar`, `ImGui.BeginMenu` and `MenuItem` placed there become top-level entries beside File. The bar is skipped when the HUD is hidden, when auto-hide has hidden it, and when the mouse is over another viewport; a hovered secondary viewport draws its own bar and calls the hook again ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L252-L257)).[^ksa-ksa-gameviewport]

`ModLibrary.Find(string)` returns the loaded `Mod` with that id or null:

```csharp
// current/decomp/KSA/ModLibrary.cs L187-L190
public static Mod? Find(string id)
{
	return Find(KeyHash.Make(id.AsSpan()));
}
```

`PrepareAll` registers every enabled manifest entry whose folder and valid `mod.toml` it finds (id = the manifest entry id) long before the first frame, so the probe is valid from `StarMapAllModsLoaded` on ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L442)).[^ksa-ksa-modlibrary]

ModMenu itself is not in this corpus: that it lists `[ModMenuEntry]` methods under its own menu, and that it does so in the same bar, is as the mods describe it (unverified).

## Lifecycle and timing

| Piece | When |
|---|---|
| Apply the postfix | `startup:all-mods-loaded` (`[StarMapAllModsLoaded]`); gatOS installs it first thing, before any early return |
| The probe `ModLibrary.Find("ModMenu")` | First draw, cached in a static `bool?` |
| The fallback menu | `frame:draw-ui`, inside `Program.DrawMenuBar`, which `OnDrawUiThreadSafe` calls after `[StarMapAfterGui]` of the same frame |
| Remove the patch and reset the cache | `[StarMapUnload]` (purrTTY and gatOS) |

A menu click therefore takes visible effect on the next frame's window draw. Heavy work must not run in the menu callback (it would stall the frame). See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Write the menu content once.** purrTTY keeps it in a static `TerminalMenus.DrawMenuContent()`; items whose delegate is not wired simply do not appear:

```csharp
// purrTTY.GameMod/TerminalMenus.cs L80-L100
    /// <summary>
    ///     Draws the shared menu content used by both ModMenu and the fallback injected menu.
    /// </summary>
    internal static void DrawMenuContent()
    {
        var isVisible = GetIsVisible?.Invoke() ?? false;
        var hotkeyShortcut = GetToggleHotkeyShortcut?.Invoke() ?? DefaultToggleShortcut;

        if (ImGui.MenuItem("Toggle Terminal", hotkeyShortcut, isVisible))
        {
            Toggle?.Invoke();
        }

        if (ImGui.MenuItem("Toggle Hotkey"))
        {
            OpenToggleHotkeySettings?.Invoke();
        }

        if (OpenInWorldManager != null || OpenLayoutManager != null)
        {
            ImGui.Separator();
```

2. **Expose it to ModMenu.** The entry is a one-liner into the shared function:

```csharp
// purrTTY.GameMod/TerminalMod.cs L58-L62
    [ModMenuEntry("purrTTY")]
    public static void DrawMenu()
    {
        TerminalMenus.DrawMenuContent();
    }
```

   gatOS wraps the shared body so a half-initialised mod still shows a menu that explains the failure, and swallows per-frame errors so a menu bug never breaks the frame:

```csharp
// gatOS.GameMod/Game/Mod.Game.cs L160-L184
    [ModMenuEntry("gatOS")]
    public static void DrawMenu() => DrawMenuContentSafe();

    /// <summary>
    ///     The shared menu-item body reused by both the ModMenu entry and the fallback injected
    ///     menu. Resolves the live mod instance (kept alive even when init failed so the player can
    ///     see why) and swallows per-frame draw errors so a UI bug never breaks the game's frame.
    /// </summary>
    private static void DrawMenuContentSafe()
    {
        if (_instance is not { } mod)
        {
            ImGui.TextDisabled("gatOS did not initialize (see the game log)");
            return;
        }

        try
        {
            mod.DrawMenuContent();
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS menu draw error: {ex.Message}");
        }
    }
```

3. **Write the postfix.** It returns immediately if ModMenu is present, otherwise opens its own top-level menu, keeps the bar alive and draws the same content. `SetMenuBarInUse` is `protected` on `GameViewport`; the public route is the `IGameViewportLifecycle` interface, the same cast `DrawMenuBar` makes:

```csharp
// purrTTY.GameMod/Patcher.cs L95-L128
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
}
```

   gatOS's version is the same shape with a try/catch around the body:

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

4. **Install the patch from `[StarMapAllModsLoaded]`, isolated from the rest of the mod.** purrTTY classifies the fallback as an *optional* patch: a failure to apply it logs and leaves the mod usable (the hotkey still works):

```csharp
// purrTTY.GameMod/Patcher.cs L56-L71
  private static void ApplyPatch(Harmony harmony, Type type, bool required)
  {
    try
    {
      harmony.CreateClassProcessor(type).Patch();
    }
    catch (Exception ex)
    {
      string severity = required ? "REQUIRED" : "optional";
      string consequence = required
        ? "terminal input gating may be degraded; the terminal will still initialize"
        : "the mod remains usable without it";
      ModLog.Log.Error(
        $"purrTTY: {severity} Harmony patch '{type.Name}' failed to apply ({consequence}): {ex.Message}");
    }
  }
```

   gatOS looks the method up with `AccessTools.Method` and degrades with a log line if `DrawProgramMenusHook` is gone:

```csharp
// gatOS.GameMod/Game/Mod.Game.cs L902-L923
    partial void InstallMenuFallback()
    {
        try
        {
            _menuHarmony = new Harmony("gatos.menu");
            var original = AccessTools.Method(typeof(Program), nameof(Program.DrawProgramMenusHook));
            if (original is null)
            {
                ModLog.Log.Warn("Menu fallback: Program.DrawProgramMenusHook not found; "
                                + "the gatOS menu is only available via the ModMenu mod.");
                return;
            }

            var postfix = new HarmonyMethod(typeof(Mod), nameof(MenuFallbackPostfix));
            _menuHarmony.Patch(original, postfix: postfix);
            ModLog.Log.Info("gatOS fallback menu hook installed.");
        }
        catch (Exception ex)
        {
            ModLog.Log.Error($"gatOS menu fallback failed to install (menu only via ModMenu): {ex.Message}");
        }
    }
```

   Call the install at the top of `OnFullyLoaded`, before any early return, so the menu can still surface an init failure:

```csharp
// gatOS.GameMod/Mod.cs L212-L224
    [StarMapAllModsLoaded]
    public void OnFullyLoaded()
    {
        try
        {
            TryInstallGameLogging();
            _instance = this;

            // Install the fallback "gatOS" menu now (before any early return below) so the menu is
            // reachable even when ModMenu is absent and even when init fails — mirroring the way the
            // [ModMenuEntry] path keeps the menu alive to surface the failure (partial seam: drops
            // out without the KSA assemblies).
            InstallMenuFallback();
```

5. **Keep slow work out of the menu.** purrTTY detects installed shells (PATH probes, `wsl --list`) once on a pool thread and publishes an immutable snapshot through a `volatile` field; the menu only reads `Current`, and until it lands shows a minimal fallback:

```csharp
// purrTTY.GameMod/ShellMenuCache.cs L24-L62
    private static volatile Snapshot? s_snapshot;
    private static int s_detectionStarted;

    /// <summary>The detected snapshot, or null while background detection is still running.</summary>
    public static Snapshot? Current => s_snapshot;

    /// <summary>
    ///     Kicks off shell detection on a background thread (idempotent; called once
    ///     from mod init). Until it completes the menus render a minimal fallback.
    /// </summary>
    public static void BeginDetection()
    {
        if (Interlocked.Exchange(ref s_detectionStarted, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                s_snapshot = Detect();
            }
            catch (Exception ex)
            {
                ModLog.Log.Debug($"purrTTY ShellMenuCache: shell detection failed: {ex.Message}");

                // Detection failing must not strand the player with only the
                // Game Console: Auto needs no detection and is the one shell
                // type valid on every platform.
                s_snapshot = new Snapshot
                {
                    Entries = [("Default Shell", ShellType.Auto), ("Game Console", ShellType.CustomGame)],
                    WslDistributions = [],
                    UnixShells = [],
                };
            }
        });
    }
```

6. **Unpatch and reset on unload.** Statics survive a StarMap reload without a context unload, so purrTTY drops its Harmony instance and resets the cached probe:

```csharp
// purrTTY.GameMod/Patcher.cs L73-L85
  public static void unload()
  {
    m_harmony?.UnpatchAll("purrTTY");
    m_harmony = null;

    // Statics survive a StarMap reload without an ALC unload (same reason
    // m_harmony is recreated lazily above), so cached environment probes must
    // be re-evaluated by the next patch() — the mod set may have changed — and
    // the held-key model must not carry stale entries across a reload.
    Patch02.Reset();
    Patch01.Reset();
    Patch04_MouseBindingGate.Reset();
  }
```

7. **Verify both paths.** Run without ModMenu: the menu appears in the game's bar after the stock menus. Install ModMenu: your bar entry disappears and the same items are under ModMenu's menu. The log shows no errors either way.

## Complete example

| File | Role |
|---|---|
| [purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L88-L128) | `Patch02` fallback postfix, optional-patch isolation, unload |
| [purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L58-L62) | `[ModMenuEntry("purrTTY")]` |
| [purrTTY.GameMod/TerminalMenus.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMenus.cs#L83-L147) | Shared menu content |
| [purrTTY.GameMod/ShellMenuCache.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/ShellMenuCache.cs#L34-L107) | Background snapshot |
| [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L160-L213) | ModMenu entry, safe shared body, fallback postfix, probe; install/remove at [gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L901-L938) |
| [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L212-L226) | Install order at the top of `OnFullyLoaded` |

## Variations and gotchas

- **Without the probe both menus appear.** The postfix always draws when ModMenu is also listing the entry; that is the failure the `ModLibrary.Find` check exists to prevent.
- **Cache the probe.** The postfix runs every frame the bar is drawn; the answer cannot change at run time. Reset it on unload.
- **Disabled is absent.** `PrepareAll` skips manifest entries that are not enabled (`if (!modEntry.Enabled) continue;`), so a disabled ModMenu is correctly treated as not installed.
- **Id assumption.** `Find("ModMenu")` matches the manifest id; both mods assume ModMenu's id is exactly `ModMenu`. If ModMenu's folder is renamed, the probe fails and both menus show (unverified).
- **Per-bar calls.** The postfix runs once per bar drawn, so a hovered secondary viewport gets the menu too. `SetMenuBarInUse(true)` is applied to `Program.MainViewport` even then; harmless for a menu that only opens windows.
- **Hook may vanish.** `DrawProgramMenusHook` exists in 2026.10.10.5554. A failed patch should degrade (purrTTY: hotkey only; gatOS: ModMenu only) instead of aborting the mod. See [Isolate patch failures and fault-proof patch bodies](../../../patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md).
- **gatOS notes StarMap's shutdown order**: `StarMapCore.DeInit` runs `Harmony.UnpatchAll()` with no id before `StarMapUnload` methods, so the explicit unpatch is a no-op at shutdown (feature page, StarMap `4dc60f8`); keep it for in-process reloads.

## See also

- [Overview](overview.md)
- [Approach 2: ModMenuEntry with an active-flag property name](add-a-menu-with-modmenu-fallback-2.md)
- [Approach 3: one settings tree rendered as a menu or a window](add-a-menu-with-modmenu-fallback-3.md)
- [Add a menu to the game's top menu bar](../add-items-to-the-game-menu-bar/overview.md)
- [List a mod in ModMenu with the ModMenuEntry attribute](../list-a-mod-in-modmenu/overview.md)
- Mod features: [purrTTY](../../../../mods/purrtty/features/game-menu-with-modmenu-fallback.md), [gatOS](../../../../mods/gatos/features/modmenu-entry-with-fallback-menu.md)

[^ksa-ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ksa-ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
