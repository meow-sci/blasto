---
type: How-To
title: Add a menu with a Harmony postfix on Program.DrawProgramMenusHook
description: Patch the empty DrawProgramMenusHook so BeginMenu and MenuItem draw inside KSA's real menu bar, toggle a window from the item, create the window lazily inside the frame and keep the bar visible while the menu is open
category: ui
group: menus
topic: add-items-to-the-game-menu-bar
approach: 1
difficulty: beginner
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/unscience/features/unscience-menu-bar-entry
  - mods/ksa-deltavmap/features/map-window-and-menu-entry
  - mods/ksa-measuretools/features/measure-menu-and-tool-window
  - mods/purrtty/features/game-menu-with-modmenu-fallback
techniques: [harmony-postfix, imgui-menu]
hooks:
  - harmony-postfix KSA.Program.DrawProgramMenusHook
  - starmap StarMapAllModsLoaded
  - starmap StarMapAfterGui
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.Program, KSA.IGameViewportLifecycle, KSA.GameViewport]
tags: [menu-bar, drawprogrammenushook, harmony, postfix, setmenubarinuse, lazy-window]
---
KSA calls an empty public method, `Program.DrawProgramMenusHook()`, inside its menu bar after the last built-in menu. A Harmony postfix on it draws a top-level menu (or a single item) in the real bar, in flight and in the vehicle editor, without depending on the layout of the stock menus. Four reference mods are walked through here (gatOS, Kitten Engineer Redux and KSA-AdvancedFlightComputer patch the same method too): unscience adds a single "Unscience" item, KSA-DeltaVMap a "Delta-V Map" menu, KSA-MeasureTools a "Measure" menu, and purrTTY a full "purrTTY" menu. Prerequisites: a StarMap mod and `HarmonyLib`. Demonstrated by [Add an entry to the game's main menu bar through DrawProgramMenusHook](../../../../mods/unscience/features/unscience-menu-bar-entry.md), [Menu-bar entry and stock ImGuiWindow subclass drawn from AfterGui](../../../../mods/ksa-deltavmap/features/map-window-and-menu-entry.md), [Measure menu and a stock-styled tool window with hover sync and click-to-copy](../../../../mods/ksa-measuretools/features/measure-menu-and-tool-window.md) and [Game menu with a ModMenu entry and a DrawProgramMenusHook fallback](../../../../mods/purrtty/features/game-menu-with-modmenu-fallback.md).

## Background: how the game does it

`Program.DrawMenuBar` opens a window named "Menu Bar" and fills it with `BeginMenuBar` and stock menus. Just before it right-aligns the version string it calls the hook, which the game's source leaves empty:

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

The bar is drawn from `OnDrawUiThreadSafe` only when `DrawUI` is true and the hovered viewport is the main one ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3186-L3189)[^ksa-program]), and by a hovered secondary game viewport from `GameViewport.DrawImGui` ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L252-L257)[^ksa-gameviewport]). With the game's "auto-hide menu bar" setting on, the method returns before the hook is reached unless the bar is visible or "in use" ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3468-L3478)[^ksa-program]); the game's own menus mark the bar in use while they are open with `SetMenuBarInUse(true)` (L3492) so it does not hide under the cursor.

## Lifecycle and timing

- **Apply at `startup:all-mods-loaded`.** The postfix is applied from `[StarMapAllModsLoaded]` (unscience, KSA-DeltaVMap, purrTTY all do), when `Program` and its types exist.
- **Draw at `frame:draw-ui`, late.** The hook runs inside `OnDrawUiThreadSafe`, which is after `[StarMapAfterGui]` in the same frame. A click on a menu item therefore changes state that the mod's window code sees on the next frame; a click in a secondary viewport's bar lands before `[StarMapAfterGui]` and shows the window in the same frame.
- **Hidden HUD.** Nothing here runs while the HUD is hidden (`DrawUI == false`).

See [Game loop and mod lifecycle](../../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Write the patch class.** A static class with `[HarmonyPatch(typeof(Program), nameof(Program.DrawProgramMenusHook))]` and a `[HarmonyPostfix]` method. The method needs no parameters: Harmony does not require `__instance`. The simplest version is one menu item:

```csharp
// unscience/MenuBarPatch.cs L8-L33
[HarmonyPatch(typeof(Program), nameof(Program.DrawProgramMenusHook))]
internal static class MenuBarPatch
{
    public static Action? ToggleWindow { get; set; }

    public static void Apply(Harmony harmony)
    {
        harmony.CreateClassProcessor(typeof(MenuBarPatch)).Patch();
        Console.WriteLine("unscience: MenuBarPatch applied");
    }

    public static void Remove(Harmony harmony)
    {
        harmony.Unpatch(
            AccessTools.Method(typeof(Program), nameof(Program.DrawProgramMenusHook)),
            HarmonyPatchType.Postfix,
            harmony.Id);
    }

    [HarmonyPostfix]
    static void Postfix()
    {
        if (ImGui.MenuItem("Unscience"))
            ToggleWindow?.Invoke();
    }
}
```

2. **Or draw a menu with an item that toggles your window.** `BeginMenu` returns whether the menu is open; always `EndMenu` after a true result, never after a false one. Pass the current state as the `selected` argument of `MenuItem` so the item shows a tick:

```csharp
// DeltaVMap/Patches/Patch_MenuBar.cs L13-L32
[HarmonyPatch(typeof(Program), nameof(Program.DrawProgramMenusHook))]
internal static class Patch_MenuBar
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        if (!ImGui.BeginMenu("Delta-V Map"u8))
            return;

        bool shown = MapWindow.Instance.IsShown;
        if (ImGui.MenuItem("Show Map"u8, default(ImString), shown))
        {
            if (shown)
                MapWindow.Instance.Close();
            else
                MapWindow.Instance.Open();
        }
        ImGui.EndMenu();
    }
}
```

3. **Create the window lazily inside the frame.** `MapWindow.Instance` is `_instance ??= new MapWindow()`. The first access happens inside the menu postfix, which matters because the `ImGuiWindow` base constructor calls `ImGui.GetMainViewport().GetCenter()` to compute the initial position, so it must run inside an ImGui frame (see [Build a mod window on the game's ImGuiWindow base class, with stable ids and saved placement](../../../../howto/ui/windows/subclass-the-stock-imguiwindow/overview.md)). The draw side never creates the instance:

```csharp
// DeltaVMap/Render/MapWindow.cs L229-L239
public static MapWindow Instance => _instance ??= new MapWindow();

// Never creates the instance: the draw hook must not touch ImGui before the user opens the map.
public static void DrawActive(IGameViewport viewport)
{
    if (_instance != null && _instance.IsShown)
    {
        _instance.EnsureTitle();
        _instance.OnDrawUi(viewport);
    }
}
```

4. **Keep the bar visible while your menu is open.** Without it, an auto-hidden bar hides when the mouse moves down into your dropdown. The setter lives on the public `IGameViewportLifecycle` interface, the same call the game's menus make:

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

5. **Apply the patch from `[StarMapAllModsLoaded]` inside a try/catch.** A missing menu hook should leave the mod loaded. Use a dedicated Harmony instance and `CreateClassProcessor(...).Patch()` so only this class is applied:

```csharp
// DeltaVMap/Mod.cs L22-L44
[StarMapAllModsLoaded]
public void OnFullyLoaded()
{
    string gameVersion = VersionInfo.Current.VersionString;
    DefaultCategory.Log.Info($"[DvMap] Game version: {gameVersion}");
    if (gameVersion != TestedGameVersion)
        DefaultCategory.Log.Warning(
            $"[DvMap] Tested against {TestedGameVersion}, current is {gameVersion}. " +
            "Some features may not work correctly.");

    _harmony = new Harmony("com.maxi.deltavmap");
    try
    {
        _harmony.CreateClassProcessor(typeof(Patch_MenuBar)).Patch();
    }
    catch (Exception ex)
    {
        // A missing menu hook leaves the map unreachable but must not unload the mod.
        LogHelper.ErrorOnce("patch-" + nameof(Patch_MenuBar), $"[DvMap] Failed to apply the menu bar patch: {ex}");
    }

    DefaultCategory.Log.Info("[DvMap] Loaded.");
}
```

6. **Draw the window from `[StarMapAfterGui]`.** The menu toggles; the window draws every frame while shown:

```csharp
// DeltaVMap/Mod.cs L50-L53
[StarMapAfterGui]
public void Draw(double dt)
{
    MapWindow.DrawActive(Program.MainViewport);
```

7. **Unpatch on unload.** `harmony.UnpatchAll(harmony.Id)` removes only your patches and leaves other mods' postfixes on the same hook intact (unscience unpatches the one postfix explicitly):

```csharp
// unscience/MenuBarPatch.cs L19-L25
public static void Remove(Harmony harmony)
{
    harmony.Unpatch(
        AccessTools.Method(typeof(Program), nameof(Program.DrawProgramMenusHook)),
        HarmonyPatchType.Postfix,
        harmony.Id);
}
```

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Patches/Patch_MenuBar.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Patches/Patch_MenuBar.cs) | The postfix |
| [DeltaVMap/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Mod.cs) | Apply at all-mods-loaded, draw at after-GUI, unload |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L229-L258) | Lazy singleton and `DrawActive` |
| [unscience/MenuBarPatch.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/MenuBarPatch.cs) | Single-item variant with apply/remove |
| [purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L88-L128) | Menu with `SetMenuBarInUse` |
| [MeasureTools/Patches/Patch_MenuBar.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_MenuBar.cs) | "Measure" menu with Show Window |

## Variations and gotchas

- **Idempotent per call.** The hook is called for every bar drawn (main window and hovered secondary viewport), and `SetMenuBarInUse` on `Program.MainViewport` marks the main viewport even if the hovered one is another; fine for a menu that only opens windows.
- **The bar is not always on screen.** It is drawn only while the mouse is over that viewport and, with auto-hide, only when visible, so never make the menu the only way to reach a feature; add a hotkey or a floating button (see [Build a mod settings window (and developer panel)](../../../../howto/ui/windows/build-a-settings-window/overview.md) for the "never strand the player" button).
- **JIT inlining (unverified).** Being empty, the method is a candidate for inlining at the call site. unscience's notes record the postfix as working through several builds and the method is still public and empty in 2026.10.10.5554; whether inlining ever bypasses a postfix in a release build was not checkable here.
- **Exceptions inside the postfix** run inside the game's UI pass and would skip `ImGui.End`; wrap risky work, and unbalanced `BeginMenu` without `EndMenu` corrupts the frame.
- **Hook exists in 2026.10.10.5554.** Re-check `DrawProgramMenusHook` after each game update; an earlier version of purrTTY used an IL transpiler on `DrawMenuBar` and replaced it with this postfix because opcode matching drifts.

## See also

- [Overview of the menu bar](overview.md)
- [Approach 2: BeginMainMenuBar from StarMapBeforeGui](add-items-to-the-game-menu-bar-2.md)
- [Approach 3: animated menu-bar words](add-items-to-the-game-menu-bar-3.md)
- [Add a menu that uses ModMenu when present and the game menu bar otherwise](../add-a-menu-with-modmenu-fallback/overview.md)
- [List a mod in ModMenu with the ModMenuEntry attribute](../list-a-mod-in-modmenu/overview.md)
- Mod feature: [Add an entry to the game's main menu bar through DrawProgramMenusHook](../../../../mods/unscience/features/unscience-menu-bar-entry.md)
- Mod feature: [Menu-bar entry and stock ImGuiWindow subclass drawn from AfterGui](../../../../mods/ksa-deltavmap/features/map-window-and-menu-entry.md)
- Mod feature: [Measure menu and a stock-styled tool window with hover sync and click-to-copy](../../../../mods/ksa-measuretools/features/measure-menu-and-tool-window.md)
- Mod feature: [Game menu with a ModMenu entry and a DrawProgramMenusHook fallback](../../../../mods/purrtty/features/game-menu-with-modmenu-fallback.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
