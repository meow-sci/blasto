---
type: How-To
title: Store config in a mod-named subfolder under Constants.DocumentsFolderPath
description: Put a mod's files in Documents/My Games/Kitten Space Agency/<ModName> so updates leave them alone, build the path from the game constant so a StarMap instance path moves it, and compare the folder names and the mods that use MyDocuments directly
category: persistence
group: user-data
topic: choose-where-a-mod-stores-its-files
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by:
  - mods/ksa-public-telemetry/features/json-config-in-documents
  - mods/purrtty/features/toml-themes-and-settings
  - mods/realatmospheres/features/modmenu-page-and-toml-settings
  - mods/kitten-engineer-redux/features/json-settings-autosave
techniques: [json-config, reflection]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded]
game_types: [KSA.Constants]
tags: [paths, documents-folder, instance-path, subfolder, config, updates]
---

Outcome: config that survives mod updates and, when built from the game constant, follows a StarMap instance path. KSA-Public-Telemetry (`KSATelemetryOverlay`) and RealAtmospheres (`RealAtmospheres`) build their folders from `Constants.DocumentsFolderPath`; purrTTY (`.purrTTY`) and Kitten Engineer Redux (`KittenEngineerReduxData`) build the same location from `Environment.SpecialFolder.MyDocuments`. Demonstrated by [JSON config in the KSA documents folder](../../../../mods/ksa-public-telemetry/features/json-config-in-documents.md), [TOML settings and alacritty-format themes](../../../../mods/purrtty/features/toml-themes-and-settings.md), [ModMenu page and TOML settings](../../../../mods/realatmospheres/features/modmenu-page-and-toml-settings.md) and [JSON settings with autosave](../../../../mods/kitten-engineer-redux/features/json-settings-autosave.md).

## Background: how the game does it

`Constants.DocumentsFolderPath` is `Personal` + `My Games/Kitten Space Agency` ([current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)), the root of `saves`, `mods`, `Vehicles` and the rest (see the [overview](./overview.md#engine-background)). The mod folder lives under it (`<docs>/mods/<Mod>`), which is why a sibling folder such as `<docs>/RealAtmospheres` is outside the install and untouched by an update. StarMap's `-InstancePath` redirect works by patching this getter ([StarMap.Core/Patches/DocumentsPathPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/DocumentsPathPatches.cs#L12-L45)), so a path built from the constant moves with the instance; a path built from `Environment.SpecialFolder.MyDocuments` does not.

## Lifecycle and timing

The folder is needed when the mod first loads or saves its config: Telemetry loads in `StarMapAllModsLoaded` (`startup:all-mods-loaded`); RealAtmospheres loads on first use; KER on its first draw. All of it is plain `System.IO`. The one timing trap is shutdown: the instance-path patch is removed before `StarMapUnload`, and a property that recomputes the path (`DirectoryPath => Path.Combine(Constants.DocumentsFolderPath, ...)`) then returns the default folder (see the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).

## Steps

1. **Pick a folder name that cannot clash.** Conventions seen: the mod's id (`RealAtmospheres`), a namespaced name (`KSATelemetryOverlay`), a dot-folder (`.purrTTY`, hidden on Unix), or a descriptive name (`KittenEngineerReduxData`). Put the files directly inside; add subfolders for kinds of data (`exports`, `themes`, `layouts`).

2. **Build the path from the game constant (references the game).** Telemetry:

   ```csharp
   // Config/ConfigStore.cs L11-L30
   private const string LogPrefix = "[KSATelemetryOverlay] ";
   private const string FolderName = "KSATelemetryOverlay";
   private const string FileName = "config.json";
   private const double SaveIntervalSeconds = 1.0;

   private static readonly JsonSerializerOptions _options = new()
   {
       WriteIndented = true,
       IncludeFields = true,
       AllowTrailingCommas = true,
       ReadCommentHandling = JsonCommentHandling.Skip,
       Converters = { new JsonStringEnumConverter() },
   };

   private static bool _dirty;
   private static double _sinceChange;
   private static OverlayConfig? _tracked;
   public static string DirectoryPath => Path.Combine(Constants.DocumentsFolderPath, FolderName);

   public static string FilePath => Path.Combine(DirectoryPath, FileName);
   ```

3. **Or read the constant by reflection if the mod has no game reference.** RealAtmospheres is built without references, so it looks up `KSA.Constants.DocumentsFolderPath` by name and caches the result; failure logs once and means "settings are not kept":

   ```csharp
   // Settings.cs L26-L44
   /// <summary>The file, under the game's documents folder (KSA.Constants.DocumentsFolderPath).</summary>
   internal static string? FilePath
   {
       get
       {
           if (_path != null) return _path;
           try
           {
               Type? constants = AccessTools.TypeByName("KSA.Constants");
               if (AccessTools.Property(constants, "DocumentsFolderPath")?.GetValue(null) is string documents)
                   _path = Path.Combine(documents, "RealAtmospheres", "settings.toml");
           }
           catch (Exception ex)
           {
               ShadowBuilder.Log("WARN: the game's documents folder was not found; settings are not kept: " + ex.Message);
           }
           return _path;
       }
   }
   ```

4. **Or use `MyDocuments` (does not follow an instance path).** purrTTY builds the root once and assigns it to its config class; KER builds a static `BaseDirectory`:

   ```csharp
   // purrTTY.GameMod/TerminalMod.cs L241-L257
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
   // SettingsStore.cs L46-L60
   private static readonly string BaseDirectory = Path.Combine(
       Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
       "My Games", "Kitten Space Agency", "KittenEngineerReduxData");

   private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
   private static readonly Stopwatch Timer = Stopwatch.StartNew();
   private static readonly Dictionary<string, SectionStateData> SectionStates = new();

   private static string _lastJson = string.Empty;
   private static bool _loaded;
   private static bool _saveErrorLogged;

   public static string DataDirectory => BaseDirectory;
   public static string ExportDirectory => Path.Combine(BaseDirectory, "exports");
   private static string SettingsPath => Path.Combine(BaseDirectory, "settings.json");
   ```

   This produces the same folder as the constant on a default install, but under `-InstancePath` the mod's data stays in the default documents folder while the saves and mods move.

5. **Create the folder on first write** (`Directory.CreateDirectory(DirectoryPath)` in the save routine, as in Telemetry's `SaveNow`) rather than at startup; a mod that never saves leaves no empty folder.

6. **Resolve once if you will write at unload.** Cache the directory string in `StarMapAllModsLoaded`; do not recompute it from the constant in `StarMapUnload` (see the pitfall in [Persist a settings object with override-only colors and tuning, saved by a debounced dirty flag](../../settings-formats/persist-mod-settings-as-json/persist-mod-settings-as-json-2.md)).

## Complete example

| File | Role |
|---|---|
| [Config/ConfigStore.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/ConfigStore.cs#L28-L30) | Constant-based path |
| [Settings.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Settings.cs#L26-L44) | Reflection-based path |
| [purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L241-L257) | `MyDocuments`-based root for `.purrTTY` |
| [SettingsStore.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SettingsStore.cs#L46-L60) | `MyDocuments`-based `BaseDirectory` |

## Variations and gotchas

- **The mods in this set that use `MyDocuments` directly** (and so ignore `-InstancePath`) are Kitten-Engineer-Redux, purrTTY, gatOS ([Keep user data in Documents/mods/<mod> apart from the install, and seed the live config from a template](choose-where-a-mod-stores-its-files-3.md)), KSA-AdvancedFlightComputer and OhMyStars' saves-directory lookup; those that use the constant are KSA-Public-Telemetry and RealAtmospheres. Prefer the constant.
- **Same folder on a default install.** `Environment.SpecialFolder.Personal` (the game's choice) and `MyDocuments` resolve to the same path; the difference appears only with an override.
- **Name clashes between mods** are a matter of convention; there is no registry. Include your mod's name.
- **Case.** On a case-sensitive file system `.purrTTY` and `.purrtty` differ; keep the exact casing everywhere.
- **Uninstalling** leaves the folder behind; document it if it holds user-created content (see [Persist user-created palettes, presets and content files](../persist-user-created-presets-and-content/overview.md)).

## See also

- [Keep settings beside the mod DLL, resolved from the loaded assembly](choose-where-a-mod-stores-its-files-1.md), [Keep user data in Documents/mods/<mod> apart from the install, and seed the live config from a template](choose-where-a-mod-stores-its-files-3.md), [Know the folder map and redirect it with a StarMap instance path](choose-where-a-mod-stores-its-files-5.md), [overview](./overview.md)
- [JSON config in the KSA documents folder](../../../../mods/ksa-public-telemetry/features/json-config-in-documents.md), [ModMenu page and TOML settings](../../../../mods/realatmospheres/features/modmenu-page-and-toml-settings.md)
- [Persist mod settings as JSON with System.Text.Json](../../settings-formats/persist-mod-settings-as-json/overview.md), [Write config files atomically and load hand-edited ones without losing them](../../settings-formats/write-and-load-config-files-safely/overview.md)
