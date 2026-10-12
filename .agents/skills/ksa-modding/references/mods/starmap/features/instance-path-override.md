---
type: Mod Feature
title: Redirect the KSA documents folder per instance
description: With -InstancePath on StarMap's command line or the STARMAP_INSTANCE_PATH environment variable, a manual Harmony prefix replaces the KSA.Constants.DocumentsFolderPath getter so settings, saves, logs, the manifest and local mods come from another folder
mod: starmap
category: persistence
techniques: [harmony-prefix, reflection]
hooks: [harmony-prefix KSA.Constants.DocumentsFolderPath (getter)]
lifecycle: [startup:starmap-before-main]
game_types: [KSA.Constants, KSA.ModLibrary, KSA.GameSettings, KSA.GameSaves, KSA.VehicleSaves, KSA.LayoutSaves]
tags: [starmap, documents-folder, instances, saves, settings, harmony, cli]
---

KSA keeps everything per-user under one folder, `<Personal>/My Games/Kitten Space Agency`. StarMap
can point that folder somewhere else per launch, which gives fully separate "instances" (different
mod sets, saves and settings) from one game install. It does this with a single Harmony prefix on
the property getter that every path in the game is built from.

## How it works

`KSA.Constants.DocumentsFolderPath` is a static, computed property:
`Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "My Games", "Kitten Space Agency")`.[^ksa-constants]
The game derives from it, among others:

| Path | Member |
|---|---|
| `<docs>/logs`, `<docs>/crashdumps`, `<docs>/profilerdumps` | `Constants.LogsFolderPath`, `CrashDumpFolderPath`, `ProfilerDumpFolderPath` |
| `<docs>/settings.toml` | `GameSettings.SettingsFilePath`[^ksa-gamesettings] |
| `<docs>/saves` | `GameSaves.SaveFolderPath`[^ksa-gamesaves] |
| `<docs>/Vehicles`, `<docs>/HUDLayouts` | `VehicleSaves.SaveFolderPath`, `LayoutSaves.SaveFolderPath` |
| `<docs>/manifest.toml`, `<docs>/mods` | `ModLibrary.LocalManifestPath`, `LocalModsFolderPath`[^ksa-modlibrary] |
| `<docs>/PipelineCache.bin` | Vulkan pipeline cache loaded in the `Program` constructor[^ksa-program] |

`DocumentsPathPatches.Apply()` is the first statement of `ModLoader.Init`, before the manifest is
read.[^sm-modloader] It looks for an override in this order:[^sm-documentspath]

1. `Environment.GetCommandLineArgs()`: the argument following `-InstancePath` (compared
   case-insensitively).
2. The environment variable `STARMAP_INSTANCE_PATH`.

If one is found it creates `new Harmony("com.starmap.core.documentspathoverride")`, gets the getter
with `AccessTools.PropertyGetter(typeof(Constants), nameof(Constants.DocumentsFolderPath))` and
patches it with a prefix that sets `__result` to the override and returns `false` (skip the
original). It logs "StarMap - Using Instance Path: ...". The prefix re-reads the command line or
environment on every call rather than caching the value.

## Lifecycle and timing

`startup:starmap-before-main`, before StarMap reads the manifest and before any mod's
`StarMapBeforeMain`, so StarMap's own mod discovery, every mod, and the whole game see the
redirected folder. The patch stays active until the game's `Main` returns: StarMap's `DeInit` removes
it with all other patches (an id-less `UnpatchAll()`) before it invokes the `[StarMapUnload]` methods,
so `Constants.DocumentsFolderPath` read in `StarMapUnload` returns the default documents folder. A
mod that saves at unload should resolve the path earlier (in `StarMapAllModsLoaded`) and keep it
([unload and shutdown](unload-and-shutdown.md)).

## Key code

| Path | Role |
|---|---|
| [StarMap.Core/Patches/DocumentsPathPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/DocumentsPathPatches.cs#L6-L46) | Override lookup and manual Harmony patch |
| [StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L46-L50) | Applied before `PrepareMods` |

## Snippets

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

## Game APIs touched

| Member | File | Use |
|---|---|---|
| `Constants.DocumentsFolderPath` | [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281-L287) | Patched getter; root of every per-user path |
| `ModLibrary.LocalManifestPath`, `LocalModsFolderPath` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L178-L180) | Manifest and local mods follow the instance |
| `GameSettings.SettingsFilePath` | [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L1382) | Settings follow the instance |
| `GameSaves.SaveFolderPath` | [current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L124) | Saves follow the instance |

## Pitfalls and version notes

- **It reads StarMap's own command line.** Putting `-InstancePath` into `GameArguments` in
  `StarMapConfig.json` does nothing: those arguments go to KSA's `Main`, not to
  `Environment.GetCommandLineArgs()`.[^sm-loaderconfig]
- **The restart flow drops the flag.** A relaunch after enabling new mods passes only
  `--restarted`, so prefer `STARMAP_INSTANCE_PATH` when using instances.[^sm-modlibrarypatches]
- **Create the instance folder before the first launch.** StarMap calls
  `ModLibrary.PrepareManifest` before KSA's `ModLibrary.OnApplicationStart` creates the folder, and
  `PrepareManifest` seeds the instance's `manifest.toml` with `File.Copy` from
  `Content/manifest.toml`, which needs the target folder to exist (inferred from the code, not
  tested). KSA creates `mods/` and the other subfolders itself later.
- **Mods that build their own paths** from `Environment.SpecialFolder.Personal` instead of
  `Constants.DocumentsFolderPath` ignore the instance; build mod data paths from
  `Constants.DocumentsFolderPath` (or the mod folder) to stay instance-aware.
- **A command-line value wins over the environment variable.**
- Added in StarMap PR #80/#81 ("Added Instancing support through CLAs and ENV").

## See also

- [Restart when new mods are enabled](new-mods-restart-flow.md)
- [Discover code mods through KSA's mod manifest](manifest-mod-discovery.md)
- [Host KSA in-process from a launcher executable](host-process-launch.md)
- [Run separate KSA instances by redirecting the documents folder](../../../howto/getting-started/loader/how-starmap-launches-ksa/how-starmap-launches-ksa-3.md) - how-to article demonstrating this feature
- [Know the folder map and redirect it with a StarMap instance path](../../../howto/persistence/user-data/choose-where-a-mod-stores-its-files/choose-where-a-mod-stores-its-files-5.md) - how-to article demonstrating this feature

[^sm-documentspath]: StarMap.Core/Patches/DocumentsPathPatches.cs
[^sm-modloader]: StarMap.Core/ModRepository/ModLoader.cs
[^sm-modlibrarypatches]: StarMap.Core/Patches/ModLibraryPatches.cs
[^sm-loaderconfig]: StarMap/LoaderConfig.cs
[^ksa-constants]: KSA.Constants (decompiled)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled)
[^ksa-gamesettings]: KSA.GameSettings (decompiled)
[^ksa-gamesaves]: KSA.GameSaves (decompiled)
[^ksa-program]: KSA.Program (decompiled)
