---
type: How-To
title: Ship data beside the DLL with an explicit copy list and resolve the mod root from the assembly
description: Write a mod.toml StarMap understands without a StarMap table, deploy exactly the data files you ship with an overridable game path, and find your own folder from Assembly.Location with fallbacks
category: assets
group: asset-xml
topic: ship-and-share-data-files-between-mods
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ohmystars/features/mod-root-and-packaging]
techniques: [msbuild, toml-config]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded]
game_types: [KSA.Mod, KSA.ModLibrary, KSA.ModManifest, KSA.ModEntry]
tags: [packaging, msbuild, mod-toml, data-files, licenses, mod-root, assembly-location]
---

A code mod with a lot of data beside the DLL (sky cultures, a 69 MB star catalog, ini files, licence texts) needs four things
to be robust: a `mod.toml` StarMap understands, a deploy list of exactly the files to copy, a game path that can be
overridden per developer, and a way for the running assembly to find its own folder even when an external loader launches it
from an instance-specific path or a stale copy sits in the default mods folder. Oh My Stars does all four.

Demonstrated by [Mod root and packaging](../../../../mods/ohmystars/features/mod-root-and-packaging.md). Releasing and zipping are in [Package and publish a mod release (zip, CI publishing, installer)](../../../getting-started/build/package-and-publish-a-mod-release/overview.md);
a build that deploys on every compile is [Deploy a mod on every build and debug it from an IDE](../../../getting-started/build/deploy-a-mod-on-every-build/overview.md).

## Background: how the game does it

The game finds a mod folder from the manifest id: `Content/<id>` first, then `<Documents>/My Games/Kitten Space Agency/mods/<id>`
([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L430), [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L176-L180)). Everything in that folder that the game does not read
(your DLL, ini files, catalog CSVs) is yours. StarMap uses the same folder rule and takes the entry assembly from a
`[StarMap] EntryAssembly` key, or falls back to the manifest id:

```csharp
// StarMap.Core/ModRepository/RuntimeMod.cs L48-L57
            var tomlConfig = TomletMain.To<RootConfig>(File.ReadAllText(modTomlPath));
            if (tomlConfig?.StarMap is not StarMapConfig starMapConfig)
            {
                starMapConfig = new StarMapConfig
                {
                    EntryAssembly = manifestEntry.Id,
                };
            }

            var modAssemblyFile = Path.Combine(modPath, $"{starMapConfig.EntryAssembly}.dll");
```

`ModLibrary.Find(id)` returns the registered `Mod` (with `DirectoryPath`) only for mods the game has loaded
([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L182-L190)).

## Lifecycle and timing

Build time for everything in the csproj. At run time the mod root is resolved lazily and cached; Oh My Stars first asks for it
from `[StarMapAllModsLoaded]` when its renderers load their data, where `ModLibrary.Find` would also work (and, for a mod
that reads data earlier, `[StarMapImmediateLoad]`). See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Write a minimal `mod.toml` with no `[StarMap]` table.** StarMap then uses the manifest id as the assembly name, so the
   folder must be named `OhMyStars` to match `OhMyStars.dll`:

   ```toml
   # packaging/mod.toml L1-L3
   name="Oh My Stars"
   version = "1.3.0"
   author = "nanonestor"
   ```

2. **Make the game path overridable.** Default `KSAGameDir` only when unset, and import an untracked `.user.props`
   file if it exists, so each developer points at their own install without editing the project:

   ```xml
   <!-- src/OhMyStars.csproj L10-L15 -->
     <!-- Path to the KSA install; override via KSAGameDir env var or a local, untracked .user.props file -->
     <PropertyGroup>
       <KSAGameDir Condition="'$(KSAGameDir)' == ''">$(ProgramFiles)\Kitten Space Agency</KSAGameDir>
     </PropertyGroup>

     <Import Project="OhMyStars.csproj.user.props" Condition="Exists('OhMyStars.csproj.user.props')" />
   ```

3. **List the data you ship as items with a target path.** One `ModContent` item per tree, with `TargetPath` metadata that
   keeps the sub-folder layout:

   ```xml
   <!-- src/OhMyStars.csproj L25-L35 -->
     <ItemGroup>
       <ModContent Include="..\licenses\**\*">
         <TargetPath>licenses\%(RecursiveDir)%(Filename)%(Extension)</TargetPath>
       </ModContent>
       <ModContent Include="..\skycultures\**\*">
         <TargetPath>skycultures\%(RecursiveDir)%(Filename)%(Extension)</TargetPath>
       </ModContent>
       <ModContent Include="..\lines_in_20.txt">
         <TargetPath>lines_in_20.txt</TargetPath>
       </ModContent>
     </ItemGroup>
   ```

4. **Copy exactly that list after build.** An explicit list (not a glob of the output folder) means game DLLs referenced
   for compilation are never copied into the mods folder, and `settings.ini` is always overwritten:

   ```xml
   <!-- src/OhMyStars.csproj L38-L49 -->
     <Target Name="CopyCustomContent" AfterTargets="AfterBuild">
       <Copy SourceFiles="$(BuildDir)mod.toml" DestinationFolder="$(SendBuildToTestDir)" />
       <Copy SourceFiles="$(BuildDir)$(ModNamespace).deps.json" DestinationFolder="$(SendBuildToTestDir)" />
       <Copy SourceFiles="$(BuildDir)$(ModNamespace).dll" DestinationFolder="$(SendBuildToTestDir)" />
       <Copy SourceFiles="ModMenu.Attributes.dll" DestinationFolder="$(SendBuildToTestDir)" />
       <Copy SourceFiles="$(MSBuildProjectDirectory)\settings.ini" DestinationFolder="$(SendBuildToTestDir)" SkipUnchangedFiles="false" />
       <Copy SourceFiles="$(BuildDir)athyg_32_reduced_m10.csv" DestinationFolder="$(SendBuildToTestDir)" />
       <Copy SourceFiles="$(BuildDir)NOTICE.txt" DestinationFolder="$(SendBuildToTestDir)" />
       <MakeDir Directories="$(BuildDir)licenses;$(BuildDir)skycultures;$(SendBuildToTestDir)\licenses;$(SendBuildToTestDir)\skycultures" />
       <Copy SourceFiles="@(ModContent)" DestinationFiles="@(ModContent->'$(BuildDir)\%(TargetPath)')" SkipUnchangedFiles="true" />
       <Copy SourceFiles="@(ModContent)" DestinationFiles="@(ModContent->'$(SendBuildToTestDir)\%(TargetPath)')" SkipUnchangedFiles="true" />
     </Target>
   ```

5. **Resolve the mod root from the running assembly, not from a hard-coded path.** Prefer the DLL's own directory, then the
   game's registry, then the default Documents path; open every data file through one helper:

   ```csharp
   // src/ModPaths.cs L15-L52
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

6. **Lay out licences with the data.** Oh My Stars ships `LICENSE` (GPL-2.0), a `NOTICE.txt` listing the third-party parts
   with their licences, and full texts under `licenses/`, copied by the same `ModContent` items.

## Complete example

- [packaging/mod.toml](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/packaging/mod.toml), [src/OhMyStars.csproj](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStars.csproj), [src/ModPaths.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/ModPaths.cs)
- [NOTICE.txt](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/NOTICE.txt) for the third-party layout

## Variations and gotchas

- **The folder name is the id.** Without a `[StarMap]` table the DLL name must equal the folder name; with
  `EntryAssembly` the DLL name is free.
- **Assembly location versus the registry.** `ModLibrary.Find(id)` returns null until `PrepareAll` has registered the mod; the
  assembly's directory works from the first line of code. Prefer it, because an external loader may run the mod from a folder
  the game's `ModLibrary` does not know.
- **Do not copy game DLLs.** Mark references `Private=false`; a second copy loads into the mod's own load context and
  its types do not match the running game's.
- **Large data.** The catalog is read by your code, not by KSA, so the game's `assets` loaders never see it; load it off the
  main thread if it is big ([Cache and key per-body data so it survives system reloads, repeated ids and extra star systems](../../../celestials/systems-and-stars/cache-and-key-body-data-per-loaded-system/overview.md) for keying it per system).
- **Other mods contributing more.** See approaches [Let other mods add data with a marker key in JSON files scanned across mod folders](ship-and-share-data-files-between-mods-2.md) and
  [Read a convention folder inside every installed mod with a strict reader](ship-and-share-data-files-between-mods-3.md).

## See also

- [Let other mods add data with a marker key in JSON files scanned across mod folders](ship-and-share-data-files-between-mods-2.md), [Ship assets and code in one mod folder and find it at runtime](../../../getting-started/loader/ship-content-and-code-in-one-mod/overview.md), [Deploy a mod on every build and debug it from an IDE](../../../getting-started/build/deploy-a-mod-on-every-build/overview.md),
  [Package and publish a mod release (zip, CI publishing, installer)](../../../getting-started/build/package-and-publish-a-mod-release/overview.md), [Declare a dependency on another StarMap mod, required or optional](../../../getting-started/dependencies/declare-a-mod-dependency/overview.md)
- [Mod root and packaging](../../../../mods/ohmystars/features/mod-root-and-packaging.md)
