---
type: How-To
title: Persist only your own ImGui window section with SaveIniSettingsToMemory and LoadIniSettingsFromMemory
description: Capture the live ImGui ini text, keep just the [Window] section of the mod's own window, store it with open-header state in the mod's data folder, restore it once at AllModsLoaded when the context already exists, and save on a timer, on demand and at unload
category: persistence
group: user-data
topic: persist-imgui-window-state
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/unscience-supermod-host]
techniques: [imgui-window, toml-config]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapAfterGui, starmap StarMapUnload]
lifecycle: [startup:all-mods-loaded, frame:draw-ui, event:starmap-unload]
game_types: [Brutal.ImGuiApi.ImGui]
tags: [imgui, imgui-ini, window-state, toml, headers, autosave, unscience]
---
Outcome: the mod's toolbox window keeps its position and size across sessions without touching the geometry the game or other mods stored for their windows, and the open or closed state of each collapsing header and each feature's visibility come back too. The mod keeps two files in its own data folder: `window.ini`, holding only the `[Window][Unscience Toolbox]` section of Dear ImGui's ini text, and `state.toml`, holding the UI state ImGui does not know. Demonstrated by [The Unscience Toolbox host window](../../../../mods/unscience/features/unscience-supermod-host.md). The first approach in this topic, [Let imgui.ini restore window geometry with a plain ImGui.Begin](./persist-imgui-window-state-1.md), relies on the game's own ini instead; choose this one when the mod needs its geometry to travel with the mod's data folder, or when its window is created in a way the shared ini would not keep.

## Background: how the game does it

Dear ImGui keeps window settings as ini text. The Brutal bindings expose `ImGui.SaveIniSettingsToMemory()`, returning the whole current ini as a string, and `ImGui.LoadIniSettingsFromMemory(ImString)`, which applies ini text ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8402-L8415)). Both need a context. The game creates its ImGui context in the `Program` constructor, at `ImGui.CreateContext()`, long before `ModLibrary.LoadAll` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L842), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L942)), so the context already exists when `[StarMapAllModsLoaded]` fires. Whether a window's geometry is kept at all depends on its flags: `NoSavedSettings` windows are never written to the ini (see the first approach).

## Lifecycle and timing

| Moment | What | Why there |
|---|---|---|
| `startup:all-mods-loaded` | `LoadImGuiWindowState` and `LoadSubmodState` | The context exists; no window has been drawn yet, so the settings are in place before the window's first `Begin` |
| `frame:draw-ui` | The window draws; a `Stopwatch`-style accumulator calls `SaveAll` every few seconds while the window is visible | A time-based save needs only `dt`; ImGui state is readable here |
| On demand | The *Save window state now* menu item | The player can force a save |
| `event:starmap-unload` | `SaveAll` if auto-save is enabled, then dispose | StarMap invokes unload after `Program.Main` returned and after its id-less `UnpatchAll()`; see below |

At unload `Program.Dispose` has already run (it runs before `StarMapUnload`): it waits for the job systems, disposes the GPU resources, destroys the ImPlot context, calls `ImGuiBackend.Shutdown()` (which disposes the platform and renderer backends), then `Renderer.Dispose`, `Glfw.Terminate` and `GameAudio.Release` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1362-L1487)). I found no `ImGui.DestroyContext` call in the decompiled KSA assembly (only the binding), so the context object is not destroyed by game code, and unscience's unload save writes files from it; but nothing in this repository proves the context is usable at that point, so treat the unload save as a best effort and rely on the timer for correctness. The data folder is built from `Environment.SpecialFolder.MyDocuments`, so it is unaffected by StarMap's instance-path redirect having been removed by then (see [Find where KSA keeps user data and choose where a mod stores its config and data files](../choose-where-a-mod-stores-its-files/overview.md)). See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Pick a data folder and the window's exact title.** The title is the ini key, so the filter in step 3 matches on it:

   ```csharp
   // unscience/UnscienceState.cs L12-L25
   internal static class UnscienceState
   {
       private const string WindowIniFile = "window.ini";
       private const string StateTomlFile = "state.toml";
       private const string WindowName = "Unscience Toolbox";
       private const int DefaultSaveInterval = 5;
       private const bool DefaultAutoSaveEnabled = true;

       private static readonly string _stateDir =
           Path.Combine(KsaPaths.UserDataDir, ".unscience");

       public static int SaveIntervalSeconds { get; set; } = DefaultSaveInterval;
       public static bool AutoSaveEnabled { get; set; } = DefaultAutoSaveEnabled;
       public static bool ShowModTooltips { get; set; } = true;
   ```

2. **Restore at `AllModsLoaded`, before the window ever draws.** Load the ini text, then the TOML for headers, visibility and settings, then hand them to the host:

   ```csharp
   // unscience/Mod.cs L115-L124
   // Restore persisted state
   UnscienceState.LoadImGuiWindowState();
   var (savedHeaders, savedVisibility) = UnscienceState.LoadSubmodState();
   foreach (var kvp in savedHeaders)
       _headerOpen[kvp.Key] = kvp.Value;
   foreach (var kvp in savedVisibility)
       if (_submodVisibility.ContainsKey(kvp.Key))
           _submodVisibility[kvp.Key] = kvp.Value;
   _autoSaveEnabled = UnscienceState.AutoSaveEnabled;
   _showModTooltips = UnscienceState.ShowModTooltips;
   ```

   ```csharp
   // unscience/UnscienceState.cs L27-L42
   public static void LoadImGuiWindowState()
   {
       var path = Path.Combine(_stateDir, WindowIniFile);
       if (!File.Exists(path)) return;

       try
       {
           var iniData = File.ReadAllText(path);
           ImGui.LoadIniSettingsFromMemory(iniData);
           Console.WriteLine("unscience: Loaded window state");
       }
       catch (Exception ex)
       {
           Console.WriteLine($"unscience: Failed to load window state: {ex.Message}");
       }
   }
   ```

3. **Filter the ini to the mod's own section.** `SaveIniSettingsToMemory` returns every window the context knows. Walk the lines and keep only the `[Window][Unscience Toolbox]` block, so saving never overwrites restored state for unrelated game windows and loading never injects it:

   ```csharp
   // unscience/UnscienceState.cs L44-L87
   public static void SaveImGuiWindowState()
   {
       try
       {
           var fullIni = ImGui.SaveIniSettingsToMemory().ToString();
           var filtered = FilterIniForUnscienceWindows(fullIni);
           if (string.IsNullOrEmpty(filtered)) return;

           Directory.CreateDirectory(_stateDir);
           File.WriteAllText(Path.Combine(_stateDir, WindowIniFile), filtered);
       }
       catch (Exception ex)
       {
           Console.WriteLine($"unscience: Failed to save window state: {ex.Message}");
       }
   }

   private static string FilterIniForUnscienceWindows(string iniData)
   {
       // Extract only the [Window][Unscience Toolbox] section from ImGui ini data
       // so we don't persist/restore state for unrelated game windows
       var sb = new StringBuilder();
       var lines = iniData.Split('\n');
       bool capturing = false;

       foreach (var line in lines)
       {
           var trimmed = line.TrimEnd('\r');

           if (trimmed.StartsWith("[Window][", StringComparison.Ordinal))
           {
               capturing = trimmed == $"[Window][{WindowName}]";
           }
           else if (trimmed.Length > 0 && trimmed[0] == '[')
           {
               capturing = false;
           }

           if (capturing)
               sb.AppendLine(trimmed);
       }

       return sb.ToString();
   }
   ```

4. **Store the state ImGui does not own, with tolerant parsing.** Header open state and sub-mod visibility are TOML tables keyed by name; the auto-save interval is clamped to 1 to 30 seconds. A parse failure logs the diagnostics and keeps defaults:

   ```csharp
   // unscience/UnscienceState.cs L89-L135
   public static (Dictionary<string, bool> headerOpen, Dictionary<string, bool> visibility) LoadSubmodState()
   {
       var headerOpen = new Dictionary<string, bool>();
       var visibility = new Dictionary<string, bool>();

       var path = Path.Combine(_stateDir, StateTomlFile);
       if (!File.Exists(path)) return (headerOpen, visibility);

       try
       {
           var tomlString = File.ReadAllText(path);
           if (!Toml.TryToModel<TomlTable>(tomlString, out var root, out var diagnostics))
           {
               foreach (var d in diagnostics)
                   Console.WriteLine($"unscience: TOML parse error: {d}");
               return (headerOpen, visibility);
           }

           if (root.TryGetValue("header_open", out var headerObj) && headerObj is TomlTable headerTable)
               foreach (var kvp in headerTable)
                   if (kvp.Value is bool b)
                       headerOpen[kvp.Key] = b;

           if (root.TryGetValue("visibility", out var visObj) && visObj is TomlTable visTable)
               foreach (var kvp in visTable)
                   if (kvp.Value is bool b)
                       visibility[kvp.Key] = b;

           if (root.TryGetValue("settings", out var settingsObj) && settingsObj is TomlTable settings)
           {
               if (settings.TryGetValue("save_interval", out var intervalObj) && intervalObj is long interval)
                   SaveIntervalSeconds = Math.Clamp((int)interval, 1, 30);
               if (settings.TryGetValue("auto_save_enabled", out var autoSaveObj) && autoSaveObj is bool autoSave)
                   AutoSaveEnabled = autoSave;
               if (settings.TryGetValue("show_mod_tooltips", out var tooltipsObj) && tooltipsObj is bool tooltips)
                   ShowModTooltips = tooltips;
           }

           Console.WriteLine("unscience: Loaded submod state");
       }
       catch (Exception ex)
       {
           Console.WriteLine($"unscience: Failed to load submod state: {ex.Message}");
       }

       return (headerOpen, visibility);
   }
   ```

   ```csharp
   // unscience/UnscienceState.cs L137-L169
   public static void SaveSubmodState(
       Dictionary<string, bool> headerOpen,
       Dictionary<string, bool> visibility)
   {
       try
       {
           Directory.CreateDirectory(_stateDir);

           var root = new TomlTable();

           var headerTable = new TomlTable();
           foreach (var kvp in headerOpen)
               headerTable[kvp.Key] = kvp.Value;
           root["header_open"] = headerTable;

           var visTable = new TomlTable();
           foreach (var kvp in visibility)
               visTable[kvp.Key] = kvp.Value;
           root["visibility"] = visTable;

           var settings = new TomlTable();
           settings["save_interval"] = (long)SaveIntervalSeconds;
           settings["auto_save_enabled"] = AutoSaveEnabled;
           settings["show_mod_tooltips"] = ShowModTooltips;
           root["settings"] = settings;

           File.WriteAllText(Path.Combine(_stateDir, StateTomlFile), Toml.FromModel(root));
       }
       catch (Exception ex)
       {
           Console.WriteLine($"unscience: Failed to save submod state: {ex.Message}");
       }
   }
   ```

5. **Apply header state once.** `ImGuiCond.Once` seeds each collapsing header from the saved value on its first appearance, then lets the user's clicks win; the host records the live state back each frame so the next save sees it:

   ```csharp
   // unscience/Mod.cs L304-L313
   if (_expandAll)
       ImGui.SetNextItemOpen(true, ImGuiCond.Always);
   else if (_collapseAll)
       ImGui.SetNextItemOpen(false, ImGuiCond.Always);
   else
       ImGui.SetNextItemOpen(_headerOpen.GetValueOrDefault(submod.Name, false), ImGuiCond.Once);

   var headerLabel = _showModTooltips ? $"{submod.Name}  (?)" : submod.Name;
   bool isOpen = ImGui.CollapsingHeader(headerLabel, ImGuiTreeNodeFlags.None);
   _headerOpen[submod.Name] = isOpen;
   ```

6. **Save on a timer while the window is visible, and at unload.** The accumulator runs only when the window is drawn, so a closed window costs nothing:

   ```csharp
   // unscience/Mod.cs L179-L192
   if (_windowVisible)
   {
       RenderWindow();

       if (_autoSaveEnabled)
       {
           _timeSinceLastSave += dt;
           if (_timeSinceLastSave >= UnscienceState.SaveIntervalSeconds)
           {
               _timeSinceLastSave = 0;
               SaveAll();
           }
       }
   }
   ```

   ```csharp
   // unscience/Mod.cs L208-L216, L335-L339
   [StarMapUnload]
   public void Unload()
   {
       try
       {
           if (_autoSaveEnabled)
               SaveAll();

           foreach (var submod in _submods)
   ..
   private void SaveAll()
   {
       UnscienceState.SaveImGuiWindowState();
       UnscienceState.SaveSubmodState(_headerOpen, _submodVisibility);
   }
   ```

## Complete example

| File | Role |
|---|---|
| [unscience/UnscienceState.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/UnscienceState.cs) | Filtered ini save and load, TOML state, data folder |
| [unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L108-L125) | Restore at `AllModsLoaded` |
| [unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L179-L192) | Timer-driven auto-save |
| [unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L208-L216) | Unload save |
| [unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs#L335-L339) | `SaveAll` |

## Variations and gotchas

- **Early loads are unverified against the host's own ini.** Dear ImGui expects `LoadIniSettingsFromMemory` after `CreateContext` and before the first `NewFrame`, as here. I did not verify, in the native library, how an early partial load interacts with the file the host loads for its own windows (the game's ini-file path was not located in the decompiled game code); test with a stock window's remembered position before shipping.
- **Persisting only your own section is a choice, not a requirement.** The filter keeps your state independent of whichever mod or game version writes other windows' geometry.
- **The window title is the key.** Rename the window and the saved geometry no longer matches; use a `###` stable id if the visible title can change.
- **Header state needs two sides.** Restoring with `ImGuiCond.Once` and recording the return value of `CollapsingHeader` each frame are both required; restoring alone is lost at the next save.
- **Saving on a timer costs two file writes.** Both files are tiny, but if the interval is short and the folder is synced by a cloud client, prefer writing only when the filtered text changed.
- **An auto-save toggle belongs in the state file.** unscience stores `auto_save_enabled` and `save_interval` beside the header tables, so the player's choice survives too.
- **A font-settings change rebuilds the atlas.** If the game's font settings change during a session, `FontManager.RegenerateFonts` clears the ImGui font atlas ([current/decomp/KSA/FontManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FontManager.cs#L55-L57)); pixel geometry in the ini is unaffected, but measurements taken from the old fonts are stale.

## See also

- Sibling approaches: [Plain ImGui.Begin so imgui.ini restores geometry](./persist-imgui-window-state-1.md), [One TOML file per layout](./persist-imgui-window-state-3.md), [overview](./overview.md)
- [The Unscience Toolbox host window](../../../../mods/unscience/features/unscience-supermod-host.md)
- [Persist mod settings as TOML](../../settings-formats/persist-mod-settings-toml/overview.md), [Subclass the stock ImGuiWindow](../../../ui/windows/subclass-the-stock-imguiwindow/overview.md)
