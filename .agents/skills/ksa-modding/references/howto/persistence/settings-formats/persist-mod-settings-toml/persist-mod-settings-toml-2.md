---
type: How-To
title: Persist a structured TOML config with a do-not-touch table, legacy migration and one shared instance
description: Split user-editable settings from UI state into two TOML tables, load a structured or an older flat file through the Tomlyn DOM, write atomically, and keep a single loaded config instance so a second writer cannot save stale values
category: persistence
group: settings-formats
topic: persist-mod-settings-toml
approach: 2
difficulty: intermediate
prerequisites: [starmap, tomlyn]
demonstrated_by: [mods/purrtty/features/toml-themes-and-settings]
techniques: [toml-config]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.Constants]
tags: [settings, toml, tomlyn, migration, shared-instance, atomic-write]
---

Outcome: `purrtty.toml` with a `[settings]` table the player edits and a `[do-not-touch]` table for UI state the mod itself maintains, an older flat file migrated on load, and every write atomic. Prerequisite: Tomlyn. Demonstrated by [TOML settings and alacritty-format themes](../../../../mods/purrtty/features/toml-themes-and-settings.md); the theme files in the same feature are [Load bundled and user color themes from TOML files and let the user's theme win a name clash](../../user-data/persist-user-created-presets-and-content/persist-user-created-presets-and-content-2.md).

## Background: how the game does it

The game offers no per-mod settings location; purrTTY chooses `<KSA documents>/.purrTTY/purrtty.toml`, building the root from `Environment.SpecialFolder.MyDocuments` plus `My Games/Kitten Space Agency` ([purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L241-L257)). That is the default value of `Constants.DocumentsFolderPath` ([current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)) but does not follow a StarMap instance-path override. The class refuses to touch a real folder until production code sets `OverrideConfigDirectory`; unset, it points at a temp folder so tests can never write into a player's documents.

## Lifecycle and timing

Loaded in `InitializeTerminal` at `startup:all-mods-loaded` (`ThemeConfiguration.Load()` and `new ThemeCatalog()`), after the config path override is set ([purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L280-L292)). Saved synchronously on the main thread from UI actions in `frame:draw-ui` (theme dialog edits, hotkey save, hiding the terminal persists geometry) and at dispose; the files are small, so no background I/O is used. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Point the class at the persistent folder before any access.** Default to a temp folder so unconfigured callers (tests) are safe:

   ```csharp
   // purrTTY.GameMod/TerminalMod.cs L236-L257
   /// <summary>
   /// Sets the production configuration directory path for persistent storage.
   /// This ensures theme configuration is saved to the user's Documents folder.
   /// Must be called BEFORE any ThemeConfiguration access.
   /// </summary>
   private void SetProductionConfigPath()
   {
       try
       {
           var myDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
           var productionConfigRoot = Path.Combine(myDocuments, "My Games", "Kitten Space Agency");

           ThemeConfiguration.OverrideConfigDirectory = productionConfigRoot;

           ModLog.Log.Debug($"purrTTY GameMod: Production config path set to: {productionConfigRoot}");
       }
       catch (Exception ex)
       {
           ModLog.Log.Debug($"purrTTY GameMod: WARNING - Failed to set production config path: {ex.Message}");
           ModLog.Log.Debug("Configuration will use temporary directory and settings will not persist.");
       }
   }
   ```

   ```csharp
   // purrTTY.Display/Configuration/ThemeConfiguration.cs L544-L550
   /// <summary>
   /// Override for the configuration directory path.
   /// When null (default), uses a temporary directory for safety.
   /// Production code should explicitly set this to the desired persistent location.
   /// Tests can set this to custom temporary directories for isolation.
   /// </summary>
   public static string? OverrideConfigDirectory { get; set; }
   ```

2. **Model the file as a POCO with two tables.** Property names are mapped with `[TomlPropertyName]`; null values are omitted when writing:

   ```csharp
   // purrTTY.Display/Configuration/ThemeConfiguration.cs L18-L37
   public class ThemeConfiguration
   {
       private static readonly TomlSerializerOptions TomlOptions = new()
       {
           WriteIndented = true,
           IndentSize = 2,
           DefaultIgnoreCondition = TomlIgnoreCondition.WhenWritingNull
       };

       /// <summary>
       /// User-editable purrTTY settings persisted to the [settings] TOML table.
       /// </summary>
       [TomlPropertyName("settings")]
       public ThemeSettingsSection Settings { get; set; } = new();

       /// <summary>
       /// Internal UI state persisted to the [do-not-touch] TOML table.
       /// </summary>
       [TomlPropertyName("do-not-touch")]
       public ThemeUiStateSection DoNotTouch { get; set; } = new();
   ```

   `ThemeSettingsSection` holds the user-editable values (selected theme, opacities, font, cursor, default shell, hotkey); `ThemeUiStateSection` holds window position, size and grid size ([purrTTY.Display/Configuration/ThemeConfiguration.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Configuration/ThemeConfiguration.cs#L702-L846)). Enum-typed values are stored as strings so a renamed enum member does not break parsing.

3. **Sniff the shape on load: structured file, legacy flat file, or defaults.** The DOM (`TomlTable`) is parsed first; a file that has a `settings` or `do-not-touch` table is deserialised directly, anything else goes through a private legacy class and is migrated. Missing, empty and unparseable files all return defaults:

   ```csharp
   // purrTTY.Display/Configuration/ThemeConfiguration.cs L556-L599
   public static ThemeConfiguration Load()
   {
       try
       {
           var configPath = GetConfigFilePath();

           if (!File.Exists(configPath))
           {
               return new ThemeConfiguration();
           }

           var tomlContent = File.ReadAllText(configPath);
           if (string.IsNullOrWhiteSpace(tomlContent))
           {
               return new ThemeConfiguration();
           }

           var tomlTable = TomlSerializer.Deserialize<TomlTable>(tomlContent, TomlOptions);
           if (tomlTable is null)
           {
               return new ThemeConfiguration();
           }

           ThemeConfiguration? config;
           if (IsStructuredFormat(tomlTable))
           {
               config = TomlSerializer.Deserialize<ThemeConfiguration>(tomlContent, TomlOptions);
               config?.Normalize();
           }
           else
           {
               var legacyConfig = TomlSerializer.Deserialize<LegacyThemeConfiguration>(tomlContent, TomlOptions);
               config = FromLegacyConfiguration(legacyConfig);
           }

           return config ?? new ThemeConfiguration();
       }
       catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is TomlException)
       {
           // Log error and return default configuration
           ModLog.Log.Debug($"Error loading theme configuration: {ex.Message}");
           return new ThemeConfiguration();
       }
   }
   ```

   ```csharp
   // purrTTY.Display/Configuration/ThemeConfiguration.cs L657-L700
   private static bool IsStructuredFormat(TomlTable tomlTable)
   {
       return tomlTable.TryGetValue("settings", out var settingsValue) && settingsValue is TomlTable ||
              tomlTable.TryGetValue("do-not-touch", out var doNotTouchValue) && doNotTouchValue is TomlTable;
   }

   private static ThemeConfiguration FromLegacyConfiguration(LegacyThemeConfiguration? legacy)
   {
       if (legacy is null)
       {
           return new ThemeConfiguration();
       }

       var config = new ThemeConfiguration
       {
           SelectedThemeName = legacy.SelectedThemeName,
           BackgroundOpacity = legacy.BackgroundOpacity,
           HideUiWhenNotHovered = legacy.HideUiWhenNotHovered,
           ForegroundOpacity = legacy.ForegroundOpacity,
           CellBackgroundOpacity = legacy.CellBackgroundOpacity,
           FontFamily = legacy.FontFamily,
           FontSize = legacy.FontSize,
           DefaultShellType = legacy.DefaultShellType,
           CustomShellPath = legacy.CustomShellPath,
           DefaultShellArguments = legacy.DefaultShellArguments ?? new List<string>(),
           WslDistribution = legacy.WslDistribution,
           DefaultCustomGameShellId = legacy.DefaultCustomGameShellId,
           GameShellPrompt = legacy.GameShellPrompt,
           ToggleHotkeyKey = legacy.ToggleHotkeyKey,
           ToggleHotkeyShift = legacy.ToggleHotkeyShift,
           ToggleHotkeyCtrl = legacy.ToggleHotkeyCtrl,
           ToggleHotkeyAlt = legacy.ToggleHotkeyAlt,
           ToggleHotkeySuper = legacy.ToggleHotkeySuper,
           TerminalWindowPosX = legacy.TerminalWindowPosX,
           TerminalWindowPosY = legacy.TerminalWindowPosY,
           TerminalWindowWidth = legacy.TerminalWindowWidth,
           TerminalWindowHeight = legacy.TerminalWindowHeight,
           TerminalColumns = legacy.TerminalColumns,
           TerminalRows = legacy.TerminalRows
       };

       config.Normalize();
       return config;
   }
   ```

4. **Normalize, serialise, and write atomically.** Create the directory, make sure the tables exist, serialise and swap the file in:

   ```csharp
   // purrTTY.Display/Configuration/ThemeConfiguration.cs L604-L625
   public void Save()
   {
       try
       {
           var configPath = GetConfigFilePath();
           var configDirectory = Path.GetDirectoryName(configPath);

           // Ensure directory exists
           if (!string.IsNullOrEmpty(configDirectory) && !Directory.Exists(configDirectory))
           {
               Directory.CreateDirectory(configDirectory);
           }

           Normalize();
           var tomlContent = TomlSerializer.Serialize(this, TomlOptions);
           AtomicFile.WriteAllText(configPath, tomlContent);
       }
       catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is TomlException)
       {
           ModLog.Log.Debug($"Error saving theme configuration: {ex.Message}");
       }
   }
   ```

   ```csharp
   // purrTTY.Display/Configuration/AtomicFile.cs L8-L16
   internal static class AtomicFile
   {
       public static void WriteAllText(string path, string contents)
       {
           var tmp = path + ".tmp";
           File.WriteAllText(tmp, contents);
           File.Move(tmp, path, overwrite: true);
       }
   }
   ```

5. **Keep exactly one loaded instance.** The controller owns the loaded config and every writer (display defaults, hotkey, window geometry) mutates that instance before `Save()`. The hotkey writer reads `controller.Configuration` for exactly this reason:

   ```csharp
   // purrTTY.GameMod/TerminalMod.cs L618-L627
   try
   {
       // Use the controller's live configuration instance so a later display
       // settings save does not clobber the new hotkey with stale values.
       var config = _controller?.Configuration ?? ThemeConfiguration.Load();
       _toggleHotkey.WriteToConfiguration(config);
       config.Save();
       ModLog.Log.Debug($"purrTTY GameMod: Toggle hotkey updated to {_toggleHotkey.ToDisplayString()}");
   }
   ```

   The project's own note: "a later save from another path silently reverts fields" if a second copy is loaded and saved ([docs/gotchas.md](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/docs/gotchas.md#L42-L45)).

## Complete example

| File | Role |
|---|---|
| [purrTTY.Display/Configuration/ThemeConfiguration.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Configuration/ThemeConfiguration.cs#L18-L37) and [purrTTY.Display/Configuration/ThemeConfiguration.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Configuration/ThemeConfiguration.cs#L544-L700) | Model, load with migration, save, path |
| [purrTTY.Display/Configuration/AtomicFile.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Configuration/AtomicFile.cs) | Temp-then-rename |
| [purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L236-L257) | Points config at the KSA documents folder |

## Variations and gotchas

- **Read-only copies are fine, writers are not.** `LoadToggleHotkeyFromConfiguration` calls `ThemeConfiguration.Load()` to read the hotkey ([purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L352-L360)); that is safe only because it never saves that copy.
- **Silent fallback.** Only `IOException`, `UnauthorizedAccessException` and `TomlException` are caught, logged at debug level, and answered with defaults; the next `Save()` then replaces a file that did not parse (see [Write config files atomically and load hand-edited ones without losing them](../write-and-load-config-files-safely/overview.md) for a policy that preserves it).
- **UI state next to settings.** A `[do-not-touch]` table is a convention, not protection: it keeps window geometry out of the part of the file players are told to edit, and keeps resets of settings from clearing layout state.
- **Migration is one-way.** The legacy reader maps fields into the structured class and the next save writes the new layout; there is no downgrade path.

## See also

- [Map one flat settings class to gatos.toml with Tomlyn and snake_case names](persist-mod-settings-toml-1.md), [Read and write a one-key settings.toml by hand, with no TOML library](persist-mod-settings-toml-3.md), [overview](./overview.md)
- [TOML settings and alacritty-format themes](../../../../mods/purrtty/features/toml-themes-and-settings.md)
- [Write config files atomically and load hand-edited ones without losing them](../write-and-load-config-files-safely/overview.md), [Persist user-created palettes, presets and content files](../../user-data/persist-user-created-presets-and-content/overview.md), [Build a mod settings window (and developer panel)](../../../ui/windows/build-a-settings-window/overview.md)
