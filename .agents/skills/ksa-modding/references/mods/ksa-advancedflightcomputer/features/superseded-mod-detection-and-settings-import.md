---
type: Mod Feature
title: Stand down for a superseded standalone mod and import its settings
description: Detect the standalone mods a merged mod replaces, by a gauge-enum marker their immediate load registers or by a type lookup, keep the built-in copy off or warn, and copy the old mod's TOML settings once on first load
mod: ksa-advancedflightcomputer
category: getting-started
techniques: [starmap-lifecycle, reflection, toml-config]
hooks: [starmap StarMapAllModsLoaded, reflection KSA.GaugeButtonFlightComputer.EnumTypes]
lifecycle: [startup:all-mods-loaded]
game_types: [KSA.GaugeButtonFlightComputer, KSA.EnumTypeOption, KSA.ModLibrary, KSA.Constants]
tags: [mod-merge, conflict-detection, migration, settings-import, toml, gauges, starmap]
---

AFC absorbed three earlier mods: AutoStage, AutoRemoveFinishedBurns and PoweredGuidance. Running a
standalone copy next to the merged one is not harmless: two stagers reacting to one burnout activate
two sequences, and the standalone burn remover together with the game's own removal deletes the next
planned burn as well. AFC therefore looks for each predecessor at load, stands its own copy down (or
warns when it cannot), and carries the old mod's settings files over once so players lose nothing by
switching ([README.md](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/README.md)).

## How it works

| Predecessor | How AFC detects it | What AFC does |
|---|---|---|
| AutoStage | The standalone mod adds an enum named `AutoStageToggle` to `GaugeButtonFlightComputer.EnumTypes` at its own immediate load; AFC scans that list by `Type.Name` | Skips the whole AutoStage patch set and logs that the AUTOSTAGE button does nothing this session ([AdvancedFlightComputer/Features/AutoStage/AutoStageFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/AutoStageFeature.cs#L49-L65)) |
| AutoRemoveFinishedBurns | `AccessTools.TypeByName("AutoRemoveFinishedBurns.Mod")` | Warns only: the standalone mod removes the finished burn before `FlightComputer.RaisePendingAlerts` runs and stock `EndAutoBurn` then removes the next one, which AFC cannot prevent from its side ([AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs#L21-L28)) |
| PoweredGuidance | Not detected in code | The README tells players to delete its folder |

### Why the enum marker is visible in time

- StarMap's `[StarMapImmediateLoad]` is a Harmony prefix on `Mod.PrepareSystems`
  ([StarMap.Core/Patches/ModPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModPatches.cs)), and the game calls
  `PrepareSystems` for every mod inside `ModLibrary.PrepareAll`.
- `[StarMapAllModsLoaded]` is a postfix on `ModLibrary.LoadAll`
  ([StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L16-L27)).
- `Program` calls `PrepareAll` before `LoadAll` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899-L942)),[^ksa-program]
  so by the time AFC's `OnFullyLoaded` runs, every enabled mod's immediate-load registrations are in the list.

### Keeping the two markers apart

`GaugeButtonFlightComputer.FindTypeOption` returns the first entry whose `EnumTypeOption.GetName()`
(the plain `Type.Name`) equals the button's `Action` attribute ([current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L55-L65),
[current/decomp/KSA/EnumTypeOption.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EnumTypeOption.cs)).[^ksa-gaugebuttonflightcomputer]
AFC therefore names its own marker `AfcAutoStageToggle`, so neither mod's XML button can bind to the
other's type ([AdvancedFlightComputer/Features/AutoStage/AfcAutoStageToggle.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/AfcAutoStageToggle.cs)).
The private static list is reached through a reflection handle (`GameReflection.GaugeButtonFlightComputer_EnumTypes`).

### One-time settings import

Each config's `Init` copies the predecessor's files only when AFC's own file does not exist yet and the
old one does, then loads as usual; a failed copy logs a warning and the feature starts from defaults.

| AFC file | Imported from (under `Documents/My Games/Kitten Space Agency/mods`) |
|---|---|
| `AdvancedFlightComputer/autostage.toml` | `AutoStage/autostage.toml` |
| `AdvancedFlightComputer/autostage-vehicles/*.toml` | `AutoStage/vehicles/*.toml` (only files not already present) |
| `AdvancedFlightComputer/autoremove.toml` | `AutoRemoveFinishedBurns/autoremovefinishedburns.toml` |

([AdvancedFlightComputer/Features/AutoStage/StagingConfig.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingConfig.cs#L33-L85),
[AdvancedFlightComputer/Features/AutoRemove/AutoRemoveConfig.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoRemove/AutoRemoveConfig.cs#L16-L42))

## Lifecycle and timing

`startup:all-mods-loaded`, inside `Mod.OnFullyLoaded`: the AutoStage check gates
`_patches.TryApply("AutoStage", ...)`, and the AutoRemove warning runs just before its patch set
([AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L83-L101)). This is the first hook where both
signals are reliable: every enabled mod's assembly is already loaded by StarMap (disabled mods are skipped
entirely, [StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L51-L66)), and every immediate
load has run. The settings import runs from the feature's `ApplyPatches` -> `Init` in the same hook, before
the first read of the config. No scene exists yet, so nothing per-vehicle is touched.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs) | Gates the AutoStage block on the check; warns for AutoRemove |
| [AdvancedFlightComputer/Features/AutoStage/AutoStageFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/AutoStageFeature.cs) | `StandaloneModAbsent` enum-marker scan |
| [AdvancedFlightComputer/Features/AutoStage/AfcAutoStageToggle.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/AfcAutoStageToggle.cs) | Distinctly named marker enum |
| [AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs) | Type-name detection and warning |
| [AdvancedFlightComputer/Features/AutoStage/StagingConfig.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingConfig.cs) | AutoStage settings import |
| [AdvancedFlightComputer/Features/AutoRemove/AutoRemoveConfig.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoRemove/AutoRemoveConfig.cs) | AutoRemove settings import |

## Snippets

```csharp
// AdvancedFlightComputer/Features/AutoStage/AutoStageFeature.cs L49-L65
    // Two stagers on one burnout would activate two rows, so the built-in one stands down while the standalone mod is installed.
    // Its marker is visible here because every immediate load runs before any AllModsLoaded hook.
    internal static bool StandaloneModAbsent()
    {
        if (!TryGetEnumTypes(out List<EnumTypeOption> list))
            return true;
        foreach (EnumTypeOption option in list)
        {
            if (option.Type.Name == StandaloneToggleName)
            {
                DefaultCategory.Log.Warning(
                    "[AFC] The standalone AutoStage mod is installed, so AFC's built-in automatic staging stays off and its AUTOSTAGE button does nothing. Remove AutoStage; AdvancedFlightComputer contains it.");
                return false;
            }
        }
        return true;
    }
```

```csharp
// AdvancedFlightComputer/Features/AutoStage/StagingConfig.cs L58-L85
    // The files the standalone AutoStage mod wrote carry over once, on the first load without an AFC copy.
    private static void ImportStandaloneConfig(string legacyDir)
    {
        string legacyConfig = Path.Combine(legacyDir, "autostage.toml");
        if (File.Exists(_configPath) || !File.Exists(legacyConfig))
            return;
        try
        {
            Directory.CreateDirectory(_modDir);
            File.Copy(legacyConfig, _configPath);
            string legacyVehicles = Path.Combine(legacyDir, "vehicles");
            if (Directory.Exists(legacyVehicles))
            {
                Directory.CreateDirectory(_vehiclesDir);
                foreach (string file in Directory.GetFiles(legacyVehicles, "*.toml"))
                {
                    string target = Path.Combine(_vehiclesDir, Path.GetFileName(file));
                    if (!File.Exists(target))
                        File.Copy(file, target);
                }
            }
            DefaultCategory.Log.Info($"[AFC] Imported the AutoStage settings from {legacyDir}.");
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Warning($"[AFC] Could not import the AutoStage settings from {legacyDir}: {ex.Message}");
        }
    }
```

## Game APIs touched

| Member | Use |
|---|---|
| `GaugeButtonFlightComputer.EnumTypes` (private static `List<EnumTypeOption>`), `FindTypeOption` ([current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L15)) | Where both mods register their gauge marker; lookup is by `Type.Name`, first hit |
| `EnumTypeOption.GetName()` ([current/decomp/KSA/EnumTypeOption.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EnumTypeOption.cs)) | Returns `Type.Name`, which is why the marker names must differ |
| `Program` startup order: `ModLibrary.PrepareAll`, `LoadAll`, `AttachGameData` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899-L985)) | Guarantees immediate loads precede `StarMapAllModsLoaded` |
| `Constants.DocumentsFolderPath` ([current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)) | Base of the `mods` folder in `AutoRemoveConfig` (`StagingConfig` builds the same path from `Environment.SpecialFolder.MyDocuments`) |

## Pitfalls and version notes

- Detection only sees enabled mods: StarMap does not load a disabled mod's assembly, so its code cannot
  conflict, but KittenExtensions still applies a disabled mod's XML patches (see
  [optional KittenExtensions patches](optional-kittenextensions-patches.md)). That is why the README
  says to delete the old folders rather than disable them.
- The enum-marker check depends on the predecessor's marker name staying `AutoStageToggle`; the
  type-name check depends on `AutoRemoveFinishedBurns.Mod`. A renamed predecessor slips through silently.
- The import is copy-once: after AFC's own file exists, later edits to the old mod's file are ignored.
- When AutoStage is held off, the AUTOSTAGE gauge button still draws, because its enum was bound while
  the game read `Gauges.xml` (see [AUTOSTAGE gauge button](autostage-gauge-button.md)); guidance reports
  "Auto-staging unavailable" instead of staging.

## See also

- [Feature-gated Harmony patch sets](feature-gated-patch-sets.md)
- [Automatic staging](automatic-staging.md)
- [Remove the last finished Auto burn](auto-remove-finished-burns.md)
- [Staging delays](staging-delays.md)
- [Probe for another mod at AllModsLoaded and stand down when a conflicting one is present](../../../howto/getting-started/dependencies/declare-a-mod-dependency/declare-a-mod-dependency-3.md) - how-to article demonstrating this feature
- [StarMapImmediateLoad: register data before assets deserialize](../../../howto/getting-started/lifecycle/choose-a-starmap-startup-hook/choose-a-starmap-startup-hook-2.md) - how-to article demonstrating this feature

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-gaugebuttonflightcomputer]: KSA.GaugeButtonFlightComputer (decompiled, build 2026.10.10.5554)
