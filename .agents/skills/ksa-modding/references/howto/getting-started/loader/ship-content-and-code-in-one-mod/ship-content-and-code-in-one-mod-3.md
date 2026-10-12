---
type: How-To
title: "Find your mod's install folder at runtime: Assembly.Location, ModLibrary.Find, Documents"
description: Locate files shipped beside the mod DLL reliably, even under instance paths and stale copies, using the loaded assembly's location first and the game's mod library as a fallback
category: getting-started
group: loader
topic: ship-content-and-code-in-one-mod
approach: 3
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ohmystars/features/mod-root-and-packaging, mods/starmap/features/per-mod-assembly-load-context]
techniques: [reflection, assembly-load-context]
hooks:
  - reflection System.Reflection.Assembly.Location
  - call KSA.ModLibrary.Find
  - starmap StarMapImmediateLoad
lifecycle: [startup:immediate-load, startup:all-mods-loaded]
game_types: [KSA.Mod, KSA.ModLibrary]
tags: [mod-root, assembly-location, modlibrary-find, data-files, instance-path, ohmystars]
---

A code mod usually ships data beside its DLL: catalogs, ini files, fonts, licence texts. Hard-coding
`Documents/My Games/Kitten Space Agency/mods/<Name>` breaks under a StarMap instance path
([Manual Harmony prefix on Constants.DocumentsFolderPath](../how-starmap-launches-ksa/how-starmap-launches-ksa-3.md)), when the mod is loaded from `Content/`, or when a stale second copy sits in the
default folder. OhMyStars resolves its own root in three tiers, which is the pattern to copy. Prerequisite:
the folder layout from [Flat folder with an explicit assets list](./ship-content-and-code-in-one-mod-1.md).

## Background: how the game does it

- StarMap loads the entry DLL by file path into a `ModAssemblyLoadContext` built from an
  `AssemblyDependencyResolver` rooted at that DLL, so `typeof(MyMod).Assembly.Location` is the real file path
  inside the mod folder.[StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L14-L22)
- KSA's own view of the folder is `Mod.DirectoryPath`, set in `Mod.MakeUsing` to the directory of `mod.toml`
  (with separators corrected); `ModLibrary.Find(string id)` returns the registered `Mod` for a manifest id (the
  folder name).[current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L103-L111)[current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L182-L190)
- `StarMapImmediateLoad(Mod)` hands your `KSA.Mod` to your code directly, before assets load.

## Lifecycle and timing

| Source of the path | Available from | Notes |
|---|---|---|
| `Assembly.Location` | any time after the assembly loads (`startup:starmap-before-main`) | needs no game state; correct inside StarMap's context |
| `[StarMapImmediateLoad] OnImmediateLoad(Mod mod)` -> `mod.DirectoryPath` | `startup:immediate-load` | the manifest-driven path, captured once |
| `ModLibrary.Find(id)` | after `ModLibrary.PrepareAll` registered the mod (`startup:immediate-load` onward) | returns `null` for an unknown id (`GetValueOrDefault`); OhMyStars also guards against `Mod.Empty` |

OhMyStars computes the root lazily on first use; the first call is from its `[StarMapAllModsLoaded]` hook when
the renderers load their data.[src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs)

## Steps

1. **Prefer the assembly's own directory.** Ship all data beside the DLL and read it from there:

   ```csharp
   // src/ModPaths.cs L29-L52
       private static string ResolveModRoot() {
           // Prefer the running assembly's own directory: the mod's assets are always
           // deployed next to OhMyStars.dll, and this stays correct even when an external
           // modloader loads the mod from a folder the game's ModLibrary doesn't know about
           // (or when a second, stale copy exists in the default mods folder).
           string? assemblyDirectory = Path.GetDirectoryName(typeof(ModPaths).Assembly.Location);
           if(!string.IsNullOrWhiteSpace(assemblyDirectory)) {
               return assemblyDirectory;
           }

           // Fall back to the game's own mod registry (mod id == mod folder name).
           Mod? mod = ModLibrary.Find("OhMyStars");
           if(mod is not null && mod != Mod.Empty && !string.IsNullOrWhiteSpace(mod.DirectoryPath)) {
               return mod.DirectoryPath;
           }

           // Last resort: the default game mods folder under Documents.
           return Path.Combine(
               Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
               "My Games",
               "Kitten Space Agency",
               "mods",
               "OhMyStars");
       }
   ```

   Tier 1 is `Assembly.Location`; tier 2 is `ModLibrary.Find(<folder name>)`; tier 3 is the default Documents
   path.[src/ModPaths.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/ModPaths.cs#L29-L52)
2. **Cache it and route every open through one helper.**

   ```csharp
   // src/ModPaths.cs L15-L27
       public static string ModRoot {
           get {
               _modRoot ??= ResolveModRoot();
               return _modRoot;
           }
       }

       public static string Combine(params string[] paths) {
           string[] all = new string[paths.Length + 1];
           all[0] = ModRoot;
           Array.Copy(paths, 0, all, 1, paths.Length);
           return Path.Combine(all);
       }
   ```

3. **Open files through it:** `File.ReadAllText(ModPaths.Combine("settings.ini"))`. Because the root is a plain
   directory, it works for `skycultures/` trees, CSV catalogs and licence texts alike.
4. **If you need the `KSA.Mod` object** (for example to match your mod to a game record), capture it at immediate
   load, as KSA-Public-Telemetry does for its font folder:[TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L32-L37)

   ```csharp
   // TelemetryOverlayMod.cs L32-L37
       [StarMapImmediateLoad]
       public void OnImmediateLoad(Mod mod)
       {
           _modDirectory = mod.DirectoryPath;
           Console.WriteLine(LogPrefix + $"attached to KSA mod '{mod.Id}'");
       }
   ```

   or match by directory, as OhMyStars' star editor does: compare each loaded mod's full `DirectoryPath` with
   `ModRoot` first, and fall back to `ModLibrary.Find(id)`, because an external loader may load the mod from a
   folder not named after it.[src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs#L488-L514)

## Complete example

[src/ModPaths.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/ModPaths.cs) is the whole helper; [src/OhMyStarsEntryPoint.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsEntryPoint.cs) shows first use; [src/StarsEditWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StarsEditWindow.cs) the directory matching. The
build side (explicit copy list into the mods folder, overridable game path, licence layout) is in
[Post-build copy with an overridable game directory](../../build/deploy-a-mod-on-every-build/deploy-a-mod-on-every-build-4.md).

## Variations and gotchas

- **`Assembly.Location` can be empty** for assemblies loaded from a byte array or single-file bundles; the fallbacks
  exist for that case. StarMap loads from a path, so it is non-empty here.
- **`mod.DirectoryPath` is the manifest-driven alternative.** It follows `Content/<id>` before the Documents mods
  folder, so a stale install-folder copy would win in KSA's view; `Assembly.Location` tells you which DLL is
  actually running.
- **Do not hard-code the last-resort path in the common case.** OhMyStars' tier 3 ignores StarMap's
  `-InstancePath` because it calls `Environment.GetFolderPath` directly; build it from
  `Constants.DocumentsFolderPath` instead if you copy it.
- **Per-user data is different.** Settings and logs the *player* changes belong under
  `Constants.DocumentsFolderPath`, not in the mod folder, which is replaced on update.
- **Case and separators.** Combine with `Path.Combine`, not string concatenation; Linux is case-sensitive.

## See also

- [Flat folder with an explicit assets list](./ship-content-and-code-in-one-mod-1.md), [Mixed assets list with csproj content copy and build stamp](./ship-content-and-code-in-one-mod-2.md), [Ship assets and code in one mod folder and find it at runtime](./overview.md)
- [Mod root and packaging](../../../../mods/ohmystars/features/mod-root-and-packaging.md), [Isolate each mod in its own AssemblyLoadContext](../../../../mods/starmap/features/per-mod-assembly-load-context.md)
- [Manual Harmony prefix on Constants.DocumentsFolderPath](../how-starmap-launches-ksa/how-starmap-launches-ksa-3.md), [Ship and load native libraries from a mod folder](../../dependencies/ship-native-libraries-with-a-mod/overview.md), [Package and publish a mod release (zip, CI publishing, installer)](../../build/package-and-publish-a-mod-release/overview.md)
