---
type: How-To
title: Probe for another mod at AllModsLoaded and stand down when a conflicting one is present
description: Detect an optional or superseded mod without a mod.toml dependency, by type name or by a marker registered at immediate load, then enable, skip or warn - and import its settings once
category: getting-started
group: dependencies
topic: declare-a-mod-dependency
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/optional-kittenextensions-patches
  - mods/ksa-advancedflightcomputer/features/superseded-mod-detection-and-settings-import
techniques: [reflection, starmap-lifecycle, toml-config]
hooks:
  - starmap StarMapImmediateLoad
  - starmap StarMapAllModsLoaded
  - reflection HarmonyLib.AccessTools.TypeByName
lifecycle: [startup:immediate-load, startup:all-mods-loaded]
game_types: [KSA.ModLibrary, KSA.Mod]
tags:
  - optional-dependency
  - reflection
  - accesstools
  - conflict-detection
  - settings-import
  - advancedflightcomputer
---

You do not always need a `mod.toml` dependency. KSA-AdvancedFlightComputer (AFC) absorbed three earlier mods; running a
standalone copy next to AFC is harmful (two stagers react to one burnout, two burn-removers delete the next burn).
AFC therefore **looks for the other mod at load**, stands its own copy down or warns, carries the old settings over
once, and declares KittenExtensions only as an *optional* dependency whose absence just skips two XML patches. The
declared forms are [Required dependency with ImportedAssemblies](./declare-a-mod-dependency-1.md) and [Optional = true with vendored fallback copies of the contract](./declare-a-mod-dependency-2.md).

## Background: how the game does it

Nothing in KSA or StarMap reports which other mods are installed except what is already loaded: StarMap does not load
a disabled mod's assembly, and AFC's checks therefore only see enabled mods. Two observable signals exist:

- **A type by name.** `AccessTools.TypeByName` searches every loaded assembly in every context, so a mod's class is
  visible without a reference.
- **A registration side effect.** `StarMapImmediateLoad` is a prefix on `Mod.PrepareSystems`, called per mod inside
  `ModLibrary.PrepareAll`; `StarMapAllModsLoaded` is a postfix on `ModLibrary.LoadAll`, and `Program` calls `PrepareAll`
  (L899) before `LoadAll` (L942). So by the time any `AllModsLoaded` hook runs, every enabled mod's immediate-load
  registrations are done.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899-L942)[current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L522)

AFC's marker lives in the game's private `GaugeButtonFlightComputer.EnumTypes` list, resolved by plain `Type.Name`
with the first hit, which is why AFC names its own marker distinctly (`AfcAutoStageToggle`).[current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L55-L65)

## Lifecycle and timing

`startup:all-mods-loaded`, inside `Mod.OnFullyLoaded`: the AutoStage check gates the AutoStage patch set, and the
AutoRemove warning runs just before its patch set. This is the first hook where both signals are reliable: every
enabled mod's assembly is loaded, and every immediate load has run. Marker registration itself is at
`startup:immediate-load` ([StarMapImmediateLoad - register data before assets deserialize](../../lifecycle/choose-a-starmap-startup-hook/choose-a-starmap-startup-hook-2.md)). The settings import runs from the feature's `Init`
in the same hook, before the first read of the config. No scene exists yet, so nothing per-vehicle is touched.

## Steps

1. **Optional XML dependency.** Declare KittenExtensions as `Optional = true`; its own `patches` key (not a KSA key)
   lists the files it applies:[AdvancedFlightComputer/mod.toml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/mod.toml)

   ```toml
   # AdvancedFlightComputer/mod.toml L1-L9
   name = "AdvancedFlightComputer"
   patches = [ "Patches/HyperbolicBodies.xml", "Patches/AutoStageButton.xml" ]

   [StarMap]
   EntryAssembly = "AdvancedFlightComputer"

   [[StarMap.ModDependencies]]
   ModId = "KittenExtensions"
   Optional = true
   ```

   With it absent nothing applies the XML: the comets keep no sphere of influence (the target-list postfix skips them
   with a one-time log line) and the AUTOSTAGE switch stays reachable on the Mods settings page. Note KittenExtensions
   builds its patch domain from every manifest entry without checking `Enabled`, so a *disabled* mod's XML patches
   still apply; AFC's README tells players to delete, not disable, the old mods.
2. **Detect a predecessor by its marker.** The standalone AutoStage mod registers an enum named `AutoStageToggle` at
   its own immediate load; AFC scans the list at `AllModsLoaded`:[AdvancedFlightComputer/Features/AutoStage/AutoStageFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/AutoStageFeature.cs#L49-L65)

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

3. **Detect a predecessor by type name** when it registers no marker:[AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs#L23-L28)

   ```csharp
   // AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs L23-L28
       internal static void WarnIfStandaloneInstalled()
       {
           if (AccessTools.TypeByName(StandaloneModType) != null)
               DefaultCategory.Log.Warning(
                   "[AFC] The standalone AutoRemoveFinishedBurns mod is installed next to AFC's built-in copy. Remove the standalone mod, because together with the game's own burn removal it also deletes the next planned burn, and AdvancedFlightComputer already removes finished burns.");
       }
   ```

   Here the conflict cannot be prevented from AFC's side, so it only warns and names the consequence.
4. **Gate the patch set on the probe**, one decision per feature (`StandaloneModAbsent()` before `TryApply("AutoStage", ...)`;
   the warning right before `TryApply("AutoRemove", ...)`; see [StarMapAllModsLoaded - work that needs the full mod set](../../lifecycle/choose-a-starmap-startup-hook/choose-a-starmap-startup-hook-3.md)).
5. **Import the old settings once.** Copy the predecessor's files only when your own do not exist yet; a failed copy
   logs a warning and the feature starts from defaults:[AdvancedFlightComputer/Features/AutoStage/StagingConfig.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingConfig.cs#L58-L85)

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

6. **Register your own marker** at immediate load, with a distinct name, if other mods may need to detect you
   ([AdvancedFlightComputer/Features/AutoStage/AfcAutoStageToggle.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/AfcAutoStageToggle.cs): `public enum AfcAutoStageToggle { Enabled }`).

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/mod.toml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/mod.toml) | Optional KittenExtensions dependency, `patches` list |
| [AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs) | `OnImmediateLoad` injects the marker; `OnFullyLoaded` gates the patch sets |
| [AdvancedFlightComputer/Features/AutoStage/AutoStageFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/AutoStageFeature.cs) | Marker detection |
| [AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoRemove/AutoRemoveFeature.cs) | Type-name detection |
| [AdvancedFlightComputer/Features/AutoStage/StagingConfig.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/AutoStage/StagingConfig.cs) | Copy-once import |

## Variations and gotchas

- **Detection only sees enabled mods** (disabled mods' assemblies are never loaded), and it depends on the other mod's
  marker or type name staying unchanged; a renamed predecessor slips through silently.
- **The import is copy-once**: after your own file exists, later edits to the old file are ignored.
- **Instance paths.** AFC builds the legacy directory from `Environment.SpecialFolder.MyDocuments`, so under
  `-InstancePath` it looks at the default folder; derive such paths from `Constants.DocumentsFolderPath`
  ([Manual Harmony prefix on Constants.DocumentsFolderPath](../../loader/how-starmap-launches-ksa/how-starmap-launches-ksa-3.md)).
- **Held-off features still draw.** When AutoStage is held off, the AUTOSTAGE gauge button still renders because its
  enum bound while the game read `Gauges.xml`; guidance reports "Auto-staging unavailable" instead.
- **Prefer a declared dependency** when you need the other mod's types ([Required dependency with ImportedAssemblies](./declare-a-mod-dependency-1.md)); probing is
  for presence checks.

## See also

- [Required dependency with ImportedAssemblies](./declare-a-mod-dependency-1.md), [Optional = true with vendored fallback copies of the contract](./declare-a-mod-dependency-2.md), [Declare a dependency on another StarMap mod, required or optional](./overview.md)
- [Optional KittenExtensions XML patches from mod.toml](../../../../mods/ksa-advancedflightcomputer/features/optional-kittenextensions-patches.md),
  [Stand down for a superseded standalone mod and import its settings](../../../../mods/ksa-advancedflightcomputer/features/superseded-mod-detection-and-settings-import.md)
- [Choose and use the right StarMap lifecycle hook](../../lifecycle/choose-a-starmap-startup-hook/overview.md), [Share types and design an inter-mod API between StarMap mods](../share-types-between-mods/overview.md)
