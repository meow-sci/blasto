---
type: How-To
title: Know the folder map and redirect it with a StarMap instance path
description: List every path derived from Constants.DocumentsFolderPath, see how StarMap finds the override from the command line or an environment variable before the manifest is read, and reuse its recipe for patching a static property getter with AccessTools.PropertyGetter and a prefix
category: persistence
group: user-data
topic: choose-where-a-mod-stores-its-files
approach: 5
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/starmap/features/instance-path-override]
techniques: [harmony-prefix]
hooks: [harmony-prefix KSA.Constants.get_DocumentsFolderPath]
lifecycle: [startup:starmap-before-main, event:starmap-unload]
game_types: [KSA.Constants, KSA.GameSettings, KSA.GameSaves, KSA.VehicleSaves, KSA.LayoutSaves, KSA.ModLibrary]
tags: [paths, instance-path, starmap, harmony, property-getter, command-line]
---

Outcome: you can run several independent KSA setups (different mod sets, saves and settings) from one install, and you know which of your mod's paths follow. StarMap does this with a single Harmony prefix on the property every path in the game is built from. Demonstrated by [Redirect the KSA documents folder per instance](../../../../mods/starmap/features/instance-path-override.md); the launch sequence it belongs to is [How StarMap launches KSA and loads your mod](../../../getting-started/loader/how-starmap-launches-ksa/overview.md).

## Background: how the game does it

`KSA.Constants.DocumentsFolderPath` is a static, computed property: `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "My Games", "Kitten Space Agency")` ([current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)). Because it is a getter (not a field, not cached), a prefix that sets `__result` changes every caller. The game derives from it:

| Path | Member |
|---|---|
| `<docs>/logs`, `<docs>/crashdumps`, `<docs>/profilerdumps` | `Constants.LogsFolderPath`, `CrashDumpFolderPath`, `ProfilerDumpFolderPath` ([current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L283-L287)) |
| `<docs>/settings.toml` | `GameSettings.SettingsFilePath` ([current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L1382)) |
| `<docs>/saves` | `GameSaves.SaveFolderPath` ([current/decomp/KSA/GameSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSaves.cs#L124)) |
| `<docs>/Vehicles`, `<docs>/HUDLayouts` | `VehicleSaves.SaveFolderPath` ([current/decomp/KSA/VehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaves.cs#L167)), `LayoutSaves.SaveFolderPath` ([current/decomp/KSA/LayoutSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LayoutSaves.cs#L119)) |
| `<docs>/mods`, `<docs>/manifest.toml` | `ModLibrary.LocalModsFolderPath`, `LocalManifestPath` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L178-L180)) |
| `<docs>/PipelineCache.bin` | loaded in the `Program` constructor ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L795)) |
| `<docs>/exports/screenshots`, `exports/planets`, `imports/biomeMaps` | the screenshot, planet-map and biome-map exporters |

## Lifecycle and timing

`startup:starmap-before-main`: `DocumentsPathPatches.Apply()` is the first statement of `ModLoader.Init`, before `PrepareMods` reads the manifest ([StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L46-L50)), so StarMap's own mod discovery, every mod and the whole game see the redirected folder. The patch stays active until the game's `Main` returns; StarMap's `DeInit` removes it with every other patch (an id-less `UnpatchAll()`) before invoking `[StarMapUnload]`, so reading the constant in `StarMapUnload` returns the default folder. A mod that saves at unload resolves its path in `StarMapAllModsLoaded` and keeps it. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Launch with an instance path.** Either a command-line argument or an environment variable (see [How StarMap launches KSA and loads your mod](../../../getting-started/loader/how-starmap-launches-ksa/overview.md)):

   ```text
   KSA.exe -InstancePath D:\KSA\instances\borea
   ```

   or `STARMAP_INSTANCE_PATH=D:\KSA\instances\borea`. The command line wins; the argument name is compared case-insensitively. The override is used as the documents folder itself (it replaces `.../My Games/Kitten Space Agency`, not a parent of it).

2. **Look at what StarMap does.** The override lookup, then a manual Harmony patch on the getter:

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

   The recipe in four lines, reusable for any static property getter you need to redirect: `AccessTools.PropertyGetter(typeof(T), nameof(T.Prop))`, a `HarmonyMethod` for a static prefix, `harmony.Patch(original, prefix: ...)`, and a prefix that takes `ref string __result`, assigns it and returns `false` so the original is skipped.

3. **Check the order.** The patch is applied before the manifest is read:

   ```csharp
   // StarMap.Core/ModRepository/ModLoader.cs L46-L52
   public void Init()
   {
       DocumentsPathPatches.Apply();
       PrepareMods();
   }

   private void PrepareMods()
   ```

4. **Make your mod follow.** Build every path from `Constants.DocumentsFolderPath` (reference or reflection) and read it at use time or, if you must write at unload, once in `StarMapAllModsLoaded` (see [Store config in a mod-named subfolder under Constants.DocumentsFolderPath](choose-where-a-mod-stores-its-files-2.md)). A path built from `Environment.SpecialFolder.MyDocuments` does not follow.

## Complete example

| File | Role |
|---|---|
| [StarMap.Core/Patches/DocumentsPathPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/DocumentsPathPatches.cs) | Override lookup and manual Harmony patch |
| [StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L46-L52) | Where `Apply` is called |
| [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281) | The patched getter |

## Variations and gotchas

- **The prefix re-reads the command line and the environment on every call**; nothing is cached.
- **Anything computed before the patch** is not redirected. The patch runs before the manifest and before mods are created; a path stored in a static initialiser of the game assembly is evaluated whenever that type first initialises, which is not necessarily before the patch.
- **Unload.** After `UnpatchAll()` the constant returns the default again, so a path recomputed at unload points at the default folder (the bug described in [Persist a settings object with override-only colors and tuning, saved by a debounced dirty flag](../../settings-formats/persist-mod-settings-as-json/persist-mod-settings-as-json-2.md)).
- **Reflection-built paths** (RealAtmospheres) follow because they read the getter at use time.
- **Per-instance data is isolated by design**: a mod that writes outside the constant (`MyDocuments` directly, a folder beside the DLL) shares that data between instances.

## See also

- [Store config in a mod-named subfolder under Constants.DocumentsFolderPath](choose-where-a-mod-stores-its-files-2.md), [Derive the data folder from the mod's log folder, with a temp-directory fallback](choose-where-a-mod-stores-its-files-4.md), [overview](./overview.md)
- [Redirect the KSA documents folder per instance](../../../../mods/starmap/features/instance-path-override.md)
- [How StarMap launches KSA and loads your mod](../../../getting-started/loader/how-starmap-launches-ksa/overview.md), [Persist mod settings as JSON with System.Text.Json](../../settings-formats/persist-mod-settings-as-json/overview.md)
