---
type: Game Feature
title: Find where KSA keeps user data and choose where a mod stores its config and data files
description: The folder map derived from Constants.DocumentsFolderPath and how StarMap redirects it, then five placements for a mod's own files - beside the DLL, a subfolder of the documents folder, a separate data folder, a path derived from the Logs folder, or the instance-aware constant
category: persistence
group: user-data
topic: choose-where-a-mod-stores-its-files
demonstrated_by:
  - mods/ohmystars/features/mod-root-and-packaging
  - mods/compendium/features/settings-ini-persistence
  - mods/ksa-measuretools/features/color-palettes-persistence
  - mods/ohmystars/features/ini-settings-persistence
  - mods/ksa-aircrafthud/features/ini-settings-and-settings-window
  - mods/ksa-public-telemetry/features/json-config-in-documents
  - mods/purrtty/features/toml-themes-and-settings
  - mods/realatmospheres/features/modmenu-page-and-toml-settings
  - mods/kitten-engineer-redux/features/json-settings-autosave
  - mods/gatos/features/toml-config-seeded-template
  - mods/gatos/features/qcow2-overlay-guest-disks
  - mods/ksagolf/features/course-grid-and-save-file
  - mods/ksastructures/features/mod-log-file
  - mods/starmap/features/instance-path-override
lifecycle: [startup:immediate-load, startup:all-mods-loaded, startup:starmap-before-main]
game_types:
  - KSA.Constants
  - KSA.Mod
  - KSA.ModLibrary
  - KSA.GameSettings
  - KSA.GameSaves
  - KSA.VehicleSaves
  - KSA.LayoutSaves
tags: [paths, documents-folder, instance-path, data-folder, mod-folder, config, updates]
---

A mod needs a place for its files: settings, presets, courses, disks. The right place depends on whether the data belongs to the install (read-only, replaced by updates), to the player (must survive updates), or to the game instance (separate per `-InstancePath`). This topic maps the game's own folders and compares how mods place theirs.

## Engine background

Every per-user path in the game derives from one static property:

| Path | Member | Source |
|---|---|---|
| `<docs>` = `Environment.SpecialFolder.Personal` + `My Games/Kitten Space Agency` | `Constants.DocumentsFolderPath` | [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281) |
| `<docs>/logs`, `<docs>/crashdumps`, `<docs>/profilerdumps` | `Constants.LogsFolderPath`, `CrashDumpFolderPath`, `ProfilerDumpFolderPath` (`logs` and `crashdumps` are created in the `Program` constructor; `profilerdumps` is only used by the profiler capture) | [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L283-L287), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L694-L703) |
| `<docs>/settings.toml` | `GameSettings.SettingsFilePath` | [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L1382) |
| `<docs>/saves` | `GameSaves.SaveFolderPath` | [current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L124) |
| `<docs>/Vehicles`, `<docs>/HUDLayouts` | `VehicleSaves.SaveFolderPath`, `LayoutSaves.SaveFolderPath` | [current/decomp/KSA/VehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaves.cs#L167), [current/decomp/KSA/LayoutSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LayoutSaves.cs#L119) |
| `<docs>/mods`, `<docs>/manifest.toml` | `ModLibrary.LocalModsFolderPath`, `LocalManifestPath` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L178-L180) |
| `<docs>/exports/screenshots`, `<docs>/exports/planets`, `<docs>/imports/biomeMaps` | `ScreenshotCapture`, `PlanetMapExporter`, `BiomeMapsExporter` | [current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L80) |
| `<docs>/PipelineCache.bin` | Vulkan pipeline cache, loaded in the `Program` constructor | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L795) |
| A mod's own folder | `Mod.DirectoryPath` (folder of the manifest that declared it) | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L91), [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L108-L109) |

StarMap can redirect the whole tree per launch: `DocumentsPathPatches.Apply()` is the first statement of `ModLoader.Init`, before the manifest is read, and patches the `DocumentsFolderPath` getter when `-InstancePath <path>` is on the command line (compared case-insensitively) or the environment variable `STARMAP_INSTANCE_PATH` is set ([StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L46-L50), [StarMap.Core/Patches/DocumentsPathPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/DocumentsPathPatches.cs#L12-L45)). Everything derived from the constant follows, including the manifest StarMap reads. The prefix re-reads the command line on every call. StarMap removes all patches with an id-less `UnpatchAll()` before it invokes `[StarMapUnload]`, so at unload the getter returns the default folder again; resolve and keep any path you need at unload during `StarMapAllModsLoaded` (see the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [How StarMap launches KSA and loads your mod](../../../getting-started/loader/how-starmap-launches-ksa/overview.md)).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Beside the mod DLL, resolved from the loaded assembly](./choose-where-a-mod-stores-its-files-1.md) | OhMyStars, Compendium, KSA-MeasureTools, KSA_AircraftHUD | `toml-config` | `startup:immediate-load`, `startup:all-mods-loaded` | Simple and portable; a redeploy or an update replaces the file unless seeded separately |
| [A mod-named subfolder under Constants.DocumentsFolderPath](./choose-where-a-mod-stores-its-files-2.md) | KSA-Public-Telemetry, RealAtmospheres, purrTTY, Kitten-Engineer-Redux | `json-config` | `startup:all-mods-loaded` | Survives updates; follows an instance path only if the constant is used |
| [A data folder under Documents/mods/<mod> kept apart from the install folder](./choose-where-a-mod-stores-its-files-3.md) | gatOS | `toml-config` | `startup:all-mods-loaded` | Updates never touch user data; on a default install the two folders coincide, so naming must keep them apart |
| [Derive the folder from KSA's own Logs folder with a temp fallback](./choose-where-a-mod-stores-its-files-4.md) | KSAGolf, KSAStructures | `json-config` | `startup:all-mods-loaded` | Works with no game types, but guesses paths and misses the game's real log folder on case-sensitive systems |
| [Know the folder map and let StarMap redirect it with an instance path](./choose-where-a-mod-stores-its-files-5.md) | StarMap | `harmony-prefix` | `startup:starmap-before-main` | One patch redirects everything; includes the recipe for patching a static property getter |

## See also

- [Persist mod settings in a hand-rolled ini file](../../settings-formats/persist-mod-settings-ini/overview.md), [Persist mod settings as JSON with System.Text.Json](../../settings-formats/persist-mod-settings-as-json/overview.md), [Persist mod settings as TOML](../../settings-formats/persist-mod-settings-toml/overview.md)
- [Ship assets and code in one mod folder and find it at runtime](../../../getting-started/loader/ship-content-and-code-in-one-mod/overview.md), [Log from a mod: per-mod log file, game log category and log-once](../../../getting-started/lifecycle/write-a-per-mod-log-file/overview.md), [How StarMap launches KSA and loads your mod](../../../getting-started/loader/how-starmap-launches-ksa/overview.md)
