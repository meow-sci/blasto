---
type: How-To
title: Let imgui.ini restore window geometry with a plain ImGui.Begin and persist the open flag yourself
description: Bypass the stock window helpers that force NoSavedSettings, set the default size with FirstUseEver so Dear ImGui's ini remembers position and size, and compare the open flag every frame so the ModMenu toggle and the close button both reach your own settings file
category: persistence
group: user-data
topic: persist-imgui-window-state
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ohmystars/features/ini-settings-persistence, mods/compendium/features/settings-ini-persistence]
techniques: [imgui-window]
hooks: [starmap StarMapBeforeGui, starmap StarMapAfterGui]
lifecycle: [startup:immediate-load, startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.ImGuiWindow, KSA.ConsoleStyle, Brutal.ImGuiApi.ImGui]
tags: [imgui, windows, imgui-ini, nosavedsettings, open-state, modmenu, persistence]
---
Outcome: a mod window that comes back where the player left it, at the size they dragged it to, and open or closed as they left it. Two things have to be saved, by two different owners: Dear ImGui's own `imgui.ini` remembers a window's position and size, but only for windows that are not created with `NoSavedSettings`, and it never remembers whether a mod's window was open. The mod therefore uses a plain `ImGui.Begin` for the first and its own settings file for the second. OhMyStars does this for two windows and Compendium for one. Demonstrated by [Flat settings.ini and the window-state files](../../../../mods/ohmystars/features/ini-settings-persistence.md) and [Settings ini persistence](../../../../mods/compendium/features/settings-ini-persistence.md). The settings-file mechanics themselves are covered by [Persist mod settings in a hand-rolled ini file](../../settings-formats/persist-mod-settings-ini/overview.md).

## Background: how the game does it

The game's window helpers turn the saved geometry off. `ConsoleStyle.BeginWindowCore` builds its flags as `NoTitleBar | NoCollapse | NoBackground | NoSavedSettings | extraFlags` ([current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L703)), and `ImGuiWindow.OnDrawUi`, the base class behind stock windows, starts from `NoCollapse | NoSavedSettings` ([current/decomp/KSA/ImGuiWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiWindow.cs#L188)). A mod window built on either does not get position and size from the ini. The ImGui IO block carries an `IniFilename` field ([current/decomp/Brutal.ImGuiApi/ImGuiIO.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiIO.cs#L30)); where the game points it was not located in the decompiled game code, so the file's location is treated here as the host's business. The game's own HUD layouts are a separate mechanism (`LayoutSaves`, one folder per layout under `<docs>/HUDLayouts`, capturing gauge canvases rather than windows, [current/decomp/KSA/LayoutSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LayoutSaves.cs#L119)).

## Lifecycle and timing

| Moment | What | Notes |
|---|---|---|
| `startup:immediate-load` (Compendium) or `startup:all-mods-loaded` (OhMyStars) | Read the settings file, set the open flags | No ImGui state is needed to read a file; both mods read it before the first draw |
| `frame:draw-ui` | `ImGui.Begin(title, ref open)`, then a comparison of the flag with the last saved value | OhMyStars draws and compares in `[StarMapBeforeGui]`, Compendium in `[StarMapAfterGui]` |

Both StarMap GUI hooks are skipped while the HUD is hidden, so nothing is written then. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Draw UI while the HUD is hidden](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md).

## Steps

1. **Call `ImGui.Begin` directly, not a stock helper.** OhMyStars uses the game's console style only for widget colors and says why in a comment. The default size uses `ImGuiCond.FirstUseEver`, so it applies only when the ini has no entry for the window:

   ```csharp
   // src/OhMyStarsWindow.cs L256-L270
   public static void Draw() {
       if(!_showWindow)
           return;

       // ConsoleStyle.BeginWindow forces NoSavedSettings, so a plain ImGui window is used instead to stay
       // resizable/movable/scrollable and to persist position+size in imgui.ini; ConsoleStyle only supplies widget colors.
       ImGui.SetNextWindowSize(DefaultWindowSize, ImGuiCond.FirstUseEver);
       ImGui.SetNextWindowBgAlpha(1f);
       if(!ImGui.Begin("Oh My Stars", ref _showWindow, ImGuiWindowFlags.None)) {
           ImGui.End();
           return;
       }

       _windowPosition = ImGui.GetWindowPos();
       _windowSize = ImGui.GetWindowSize();
   ```

   Compendium does the same, positioning and sizing on first use only:

   ```csharp
   // src/CompendiumMain.cs L56-L63, L129-L133
   [StarMapAfterGui]
   public void OnAfterUi(double dt)
   {
       PersistWindowOpenState();
       DrawBodyPointer();
       pointerWindowVisible = false;
       ImGui.SetNextWindowPos(defaultWindowPos, ImGuiCond.FirstUseEver, (float2?)null);
       ImGui.SetNextWindowSize(defaultWindowSize, ImGuiCond.FirstUseEver);

           if (!ImGui.Begin("Compendium", ref CompendiumWindow))
           {
               ImGui.End();
               return;
           }
   ```

2. **Keep a "last saved" copy of each open flag.** The ini cannot hold it, so the mod's own file does. OhMyStars records both its windows:

   ```csharp
   // src/OhMyStarsWindow.cs L120-L129
   // Open/closed state last written to settings.ini; ImGui's imgui.ini only keeps position/size, not visibility.
   private static bool _savedShowWindow = true;
   private static bool _savedShowStarEditor = true;

   // Called every frame so toggles from the mod menu and the windows' close (X) buttons both get persisted.
   public static void PersistWindowOpenStates() {
       if(_showWindow != _savedShowWindow || StarsEditWindow.IsOpen != _savedShowStarEditor) {
           SaveSettings();
       }
   }
   ```

3. **Compare every frame instead of hooking each toggle.** The flag changes from three places: the ModMenu entry, the window's close button (`ref open`), and code. A per-frame comparison catches all of them and costs one boolean test:

   ```csharp
   // src/CdmSettings.cs L91-L98
   // Called every frame so both the mod menu toggle and the window's close (X) button get persisted.
   private static void PersistWindowOpenState()
   {
       if (CompendiumWindow != savedCompendiumWindow)
       {
           SaveSettings();
       }
   }
   ```

   ```csharp
   // src/OhMyStarsEntryPoint.cs L34-L49
   [ModMenuEntry("Oh My Stars")]
   public static void DrawMenu() {
       if(ImGui.MenuItem("Oh My Stars Window")) {
           OhMyStarsWindow.ToggleWindow();
       }
       if(ImGui.MenuItem("Star Editor")) {
           StarsEditWindow.ToggleWindow();
       }
   }

   [StarMapBeforeGui]
   public static void OnBeforeGui(double dt) {
       OhMyStarsWindow.Draw();
       StarsEditWindow.Draw();
       OhMyStarsWindow.PersistWindowOpenStates();
   }
   ```

4. **Write the flag with the rest of the settings.** `SaveSettings` rewrites the whole file and updates the saved copies. Compendium's version is small enough to read at once:

   ```csharp
   // src/CdmSettings.cs L61-L89
   private static void SaveSettings()
   {
       string? settingsPath = GetSettingsFilePath();
       if (settingsPath == null)
       {
           return;
       }

       savedCompendiumWindow = CompendiumWindow;
       try
       {
           File.WriteAllLines(settingsPath, new[]
           {
               "[Window]",
               $"ShowWindow={CompendiumWindow.ToString(CultureInfo.InvariantCulture)}",
               "[Pointer]",
               $"ShowPointer={showBodyPointer.ToString(CultureInfo.InvariantCulture)}",
               $"PointerDualLines={pointerDualLines.ToString(CultureInfo.InvariantCulture)}",
               $"PointerRainbow={pointerRainbow.ToString(CultureInfo.InvariantCulture)}",
               $"PointerColorR={pointerColor.X.ToString(CultureInfo.InvariantCulture)}",
               $"PointerColorG={pointerColor.Y.ToString(CultureInfo.InvariantCulture)}",
               $"PointerColorB={pointerColor.Z.ToString(CultureInfo.InvariantCulture)}"
           });
       }
       catch (Exception ex)
       {
           Console.WriteLine($"Compendium: Failed to write {settingsPath}: {ex.Message}");
       }
   }
   ```

5. **Read it back before the first draw.** Missing or malformed values keep their defaults; the saved copies are primed so the first frame does not trigger a pointless write:

   ```csharp
   // src/OhMyStarsWindow.cs L131-L147
   public static void LoadSettings() {
       string? settingsPath = GetSettingsFilePath();
       if(string.IsNullOrWhiteSpace(settingsPath)) {
           return;
       }

       if(!File.Exists(settingsPath)) {
           SaveSettings();
           return;
       }

       Dictionary<string, string> values = ReadSettingsFile(settingsPath);

       _showWindow = ReadBool(values, "ShowWindow", true);
       StarsEditWindow.IsOpen = ReadBool(values, "ShowStarEditor", true);
       _savedShowWindow = _showWindow;
       _savedShowStarEditor = StarsEditWindow.IsOpen;
   ```

6. **Persist a window's sub-view the same way.** OhMyStars stores the active tab as a `Tab=` key; the Spectrum legend sub-view is saved as its parent tab ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L1569-L1571)), so a restart does not reopen a mode that needs live state.

## Complete example

| File | Role |
|---|---|
| [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L120-L147) | Saved copies, change detection, load |
| [src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L256-L270) | The plain `ImGui.Begin` window |
| [src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs#L34-L49) | ModMenu entry and the per-frame call |
| [src/CdmSettings.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmSettings.cs#L61-L98) | Save and change detection |
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L45-L63) | ModMenu entry and the window draw |

## Variations and gotchas

- **Bypassing the helper costs you its look.** `ConsoleStyle.BeginWindow` also gives the stock title bar, rounding and transparent background; a plain `ImGui.Begin` window needs those restyled by hand. Subclassing the stock window and clearing its flags is the alternative, covered by [Subclass the stock ImGuiWindow](../../../ui/windows/subclass-the-stock-imguiwindow/overview.md).
- **Titles are ini keys.** Dear ImGui keys the saved geometry by the window title (or the text after `###`), so renaming a window resets its position; use a stable `###id` suffix if the visible title may change.
- **Settings writes on every change.** Both mods write the file from the change branch of each widget, so dragging a slider rewrites it every frame; debounce as in [Write config files atomically and load hand-edited ones without losing them](../../settings-formats/write-and-load-config-files-safely/overview.md).
- **The file sits beside the DLL.** Both mods keep `settings.ini` in the mod folder, so an update that replaces the folder resets the open state; the geometry survives because it lives elsewhere. See [Find where KSA keeps user data and choose where a mod stores its config and data files](../choose-where-a-mod-stores-its-files/overview.md).
- **A game font-settings change rebuilds the font atlas.** `Program.PrepareFrame` calls `FontManager.RegenerateFonts`, which runs `ImGui.GetIO().Fonts.Clear()` and reloads fonts at the new size ([current/decomp/KSA/FontManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FontManager.cs#L55-L60), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2228-L2233)). Window rectangles in the ini are pixels and survive, but a window sized to fit its text is only as good as the metrics it measured before the change.
- **Hidden HUD.** With the HUD hidden neither hook runs, so a flag toggled by a hotkey while hidden is saved on the next visible frame.

## See also

- Sibling approaches: [Filtered ImGui ini section saved on a timer and at unload](./persist-imgui-window-state-2.md), [One TOML file per layout, applied on request](./persist-imgui-window-state-3.md), [overview](./overview.md)
- [Flat settings.ini and the window-state files](../../../../mods/ohmystars/features/ini-settings-persistence.md), [Settings ini persistence](../../../../mods/compendium/features/settings-ini-persistence.md)
- [Subclass the stock ImGuiWindow](../../../ui/windows/subclass-the-stock-imguiwindow/overview.md), [Persist mod settings in a hand-rolled ini file](../../settings-formats/persist-mod-settings-ini/overview.md)
