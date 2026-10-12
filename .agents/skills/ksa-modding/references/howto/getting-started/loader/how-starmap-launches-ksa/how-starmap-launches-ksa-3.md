---
type: How-To
title: Run separate KSA instances by redirecting the documents folder
description: Point every per-user KSA path at a different folder with a Harmony prefix on Constants.DocumentsFolderPath, driven by -InstancePath or STARMAP_INSTANCE_PATH, and keep your own mod data instance-aware
category: getting-started
group: loader
topic: how-starmap-launches-ksa
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/starmap/features/instance-path-override]
techniques: [harmony-prefix, starmap-lifecycle]
hooks: [harmony-prefix KSA.Constants.DocumentsFolderPath]
lifecycle: [startup:starmap-before-main]
game_types: [KSA.Constants, KSA.ModLibrary]
tags: [instance, documents-folder, user-data, saves, settings, harmony, persistence]
---

KSA keeps everything per-user under one folder. StarMap can point that folder somewhere else per
launch, which gives fully separate **instances** (different mod sets, saves and settings) from one game
install. It does this with a single Harmony prefix on the property getter that every path in the game is
built from. This article shows how it works so you can use it, copy it, and keep your own mod's files
instance-aware. It is the one approach here that uses a manually applied Harmony patch from a
`startup:starmap-before-main` position.

## Background: how the game does it

`Constants.DocumentsFolderPath` is a static computed property: `Path.Combine(Personal, "My Games",
"Kitten Space Agency")`.[current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281-L287) Everything per-user derives from it:

| Path | Member |
|---|---|
| `<docs>/logs`, `crashdumps`, `profilerdumps` | `Constants.LogsFolderPath`, `CrashDumpFolderPath`, `ProfilerDumpFolderPath` |
| `<docs>/settings.toml` | `GameSettings.SettingsFilePath`[current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L1382) |
| `<docs>/saves` | `GameSaves.SaveFolderPath`[current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L124) |
| `<docs>/Vehicles`, `<docs>/HUDLayouts` | `VehicleSaves.SaveFolderPath`, `LayoutSaves.SaveFolderPath` |
| `<docs>/manifest.toml`, `<docs>/mods` | `ModLibrary.LocalManifestPath`, `LocalModsFolderPath`[current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L178-L180) |
| `<docs>/PipelineCache.bin` | Vulkan pipeline cache, loaded in the `Program` constructor[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L795) |

Because it is a *getter that recomputes each time*, patching it redirects the lot, including the
manifest, which means each instance has its own enabled-mods list and its own `mods/` folder.

## Lifecycle and timing

`DocumentsPathPatches.Apply()` is the first statement of `ModLoader.Init`, i.e.
`startup:starmap-before-main`, before the manifest is read and before any mod's `StarMapBeforeMain`.
StarMap's own discovery, every mod and the whole game therefore see the redirected folder. The patch lives
for the whole process; StarMap's `DeInit` removes it with all other patches only after the game has
exited.[StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L46-L50)

## Steps

1. **Choose the override.** StarMap looks, in this order, for:[StarMap.Core/Patches/DocumentsPathPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/DocumentsPathPatches.cs#L12-L45)
   1. the value following `-InstancePath` in `Environment.GetCommandLineArgs()` (compared case-insensitively);
   2. the environment variable `STARMAP_INSTANCE_PATH`.

   A command-line value wins over the environment. Both are read from **StarMap's** own command line, so
   `-InstancePath` placed in `GameArguments` of `StarMapConfig.json` does nothing: those go to KSA's `Main`
   (see [Host process, contexts and hook binding walkthrough](./how-starmap-launches-ksa-1.md)).

   ```bash
   # example launches (not from a mod repo)
   StarMap.exe -InstancePath D:\ksa\instance-a
   STARMAP_INSTANCE_PATH=/home/me/ksa/instance-b ./StarMap
   ```

2. **Create the folder first.** StarMap calls `ModLibrary.PrepareManifest` before KSA's
   `ModLibrary.OnApplicationStart` creates the folders, and `PrepareManifest` seeds the instance's
   `manifest.toml` by `File.Copy` from `Content/manifest.toml`, which needs the target folder to exist
   (inferred from the code, not tested). KSA creates `mods/` and the other subfolders later itself.
3. **See what StarMap does.** The whole mechanism, applied manually because the target is a property
   getter:

   ```csharp
   // StarMap.Core/Patches/DocumentsPathPatches.cs L12-L45
   public static void Apply()
   {
       // Check if an override path is provided via command-line argument or environment variable
       var overridePath = TryGetOverride();
       if (string.IsNullOrEmpty(overridePath))
           return;

       // Apply the Harmony patch to override the DocumentsFolderPath property
       var harmony = new Harmony(HarmonyId);
       var original = AccessTools.PropertyGetter(typeof(Constants), nameof(Constants.DocumentsFolderPath));
       var prefix = new HarmonyMethod(typeof(DocumentsPathPatches), nameof(Prefix));
       harmony.Patch(original, prefix: prefix);
       Console.WriteLine($"StarMap - Using Instance Path: {overridePath}");
   }

   private static bool Prefix(ref string __result)
   {
       __result = TryGetOverride()!;
       return false;
   }

   private static string? TryGetOverride()
   {
       var args = Environment.GetCommandLineArgs();
       for (int i = 0; i < args.Length - 1; i++)
       {
           if (string.Equals(args[i], CliFlag, StringComparison.OrdinalIgnoreCase))
           {
               return args[i + 1];
           }
       }

       return Environment.GetEnvironmentVariable(EnvVarName);
   }
   ```

   Look for the log line `StarMap - Using Instance Path: ...` in the StarMap console to confirm it applied.
4. **Make your mod instance-aware.** Build every path your mod persists from
   `Constants.DocumentsFolderPath` (or from your mod folder), never from
   `Environment.SpecialFolder.Personal`/`MyDocuments` directly:

   ```csharp
   // adapted: how a mod should derive its data folder (Constants.DocumentsFolderPath is public in KSA.dll)
   using KSA;

   internal static class ModPaths
   {
       public static string DataDir =>
           Path.Combine(Constants.DocumentsFolderPath, "MyMod");   // follows -InstancePath
   }
   ```

   For files shipped *beside the DLL*, use the assembly location instead, which is correct under any
   instance (see [Find your mod's install folder at runtime: Assembly.Location, then ModLibrary.Find, then Documents](../ship-content-and-code-in-one-mod/ship-content-and-code-in-one-mod-3.md)).
5. **To copy the technique into your own mod**, apply the same manual patch from `StarMapBeforeMain`
   (the earliest point, before any game code reads the getter) with your own Harmony id, and return
   `false` from the prefix. Mind that two such patches on the same getter chain through `__result`.

## Complete example

Only StarMap implements this: [StarMap.Core/Patches/DocumentsPathPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/DocumentsPathPatches.cs) (the patch) and [StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs) (the call site at
`Init`). KSA code that fans out from the getter: [current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs), [current/decomp/KSA/VehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaves.cs),
[current/decomp/KSA/LayoutSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LayoutSaves.cs).

## Variations and gotchas

- **The restart flow drops the flag.** A relaunch after enabling new mods passes only `--restarted`, so
  `-InstancePath` is lost and the restarted game reads the default folder; `STARMAP_INSTANCE_PATH`
  survives because the child inherits the environment.[StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L30-L76) Prefer the environment
  variable when using instances.
- **`StarMapUnload` sees the default folder.** StarMap's id-less `UnpatchAll()` also removes the instance
  prefix, so code that reads `Constants.DocumentsFolderPath` inside `StarMapUnload` gets the default
  location (KSA-Public-Telemetry's final config save hits this; see [Clean up on game exit and relaunch the game from a mod](../../lifecycle/clean-up-on-game-exit/overview.md)).
- **Hard-coded fallbacks ignore the instance.** OhMyStars' last-resort path uses `SpecialFolder.MyDocuments`
  directly; it is only reached if `Assembly.Location` is empty ([Find your mod's install folder at runtime: Assembly.Location, then ModLibrary.Find, then Documents](../ship-content-and-code-in-one-mod/ship-content-and-code-in-one-mod-3.md)).
- **Log files move too**, because `LogsFolderPath` derives from the getter; see
  [Constants.LogsFolderPath and a developer marker file](../../lifecycle/write-a-per-mod-log-file/write-a-per-mod-log-file-2.md).
- The feature came from StarMap PRs #80 and #81 ("Instancing support through CLAs and ENV").

## See also

- [Host process, contexts and hook binding walkthrough](./how-starmap-launches-ksa-1.md), [Folder and manifest rules as StarMap consumes them](./how-starmap-launches-ksa-2.md), [How StarMap launches KSA and loads your mod](./overview.md)
- [Redirect the KSA documents folder per instance](../../../../mods/starmap/features/instance-path-override.md), [Restart when new mods are enabled](../../../../mods/starmap/features/new-mods-restart-flow.md)
- [Log from a mod: per-mod log file, game log category and log-once](../../lifecycle/write-a-per-mod-log-file/overview.md), [Clean up on game exit and relaunch the game from a mod](../../lifecycle/clean-up-on-game-exit/overview.md)
