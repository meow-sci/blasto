---
type: How-To
title: Lay out a flat mod folder with an explicit assets list
description: One folder that is both a KSA content mod and a StarMap code mod - asset XML beside Meshes, Textures and Sounds, every file listed in mod.toml, paths resolved against the mod root
category: getting-started
group: loader
topic: ship-content-and-code-in-one-mod
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/mod-folder-content-and-code-mod]
techniques: [toml-config, xml-asset]
hooks: [starmap StarMapImmediateLoad, starmap StarMapAllModsLoaded, call KSA.Mod.LoadAssetBundles]
lifecycle: [startup:immediate-load, startup:asset-load, startup:all-mods-loaded]
game_types: [KSA.Mod, KSA.ModLibrary, KSA.FileReference, KSA.AssetBundle]
tags: [mod-toml, assets, content-mod, folder-layout, csproj, ksacars]
---

KSACars ships as one folder, `mods/FastAndPurrious/`, that is two mods at once. KSA's own content system reads the
`assets` array of `mod.toml` and loads part, game-data and sound XML; StarMap reads the same file's `[StarMap]`
table and loads `KSACars.dll` into its own load context. This article reproduces that flat layout. Prerequisites:
the skeleton from [Hand-built project from the discovery rules](../create-a-minimal-starmap-mod/create-a-minimal-starmap-mod-1.md). Without StarMap the cars still appear in the editor and
render; they just do not drive.[src/KSACars/mod.toml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/mod.toml) See [Mixed assets list with csproj content copy and build stamp](./ship-content-and-code-in-one-mod-2.md) for a mixed assets list
with a build stamp and [Find your mod's install folder at runtime: Assembly.Location, then ModLibrary.Find, then Documents](./ship-content-and-code-in-one-mod-3.md) for finding the folder at runtime.

## Background: how the game does it

`Mod.LoadAssetBundles` loops over the `assets` strings and calls
`XmlLoader.Load<AssetBundle>(Path.Combine(DirectoryPath, path), this)` for each; there is no glob and no
directory scan.[current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L157-L169) The root element of every asset file is `<Assets>` (`AssetBundle` is
`[XmlRoot("Assets")]`), whose children are typed elements such as `Part`, `SubPart`, `Sound`, `Texture`,
`MeshFile`.[current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L9-L40) Paths inside those elements (`<Source Path="Meshes/x.glb"/>`) become
`FileReference.LocalPath`; `ModPath` is `Mod.GetPath(LocalPath)`, that is
`Path.Combine(Mod.DirectoryPath, localPath)`.[current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L13-L26) So asset paths resolve against the **mod
folder** (the folder of `mod.toml`), not against the XML file's own folder. Keeping the XML at the folder root,
mirroring `Content/Core`, makes the two readings identical.

## Lifecycle and timing

| Moment | What happens for this folder |
|---|---|
| StarMap start (`startup:starmap-before-main`) | `TryCreateMod` loads `KSACars.dll` into a mod context and instantiates the `[StarMapMod]` class |
| `Mod.PrepareSystems` prefix (`startup:immediate-load`) | `OnImmediateLoad(Mod)` logs the mod id and builds |
| `ModLibrary.LoadAll` (`startup:asset-load`) | the three XML bundles load in `assets` order, mod by mod in manifest order |
| `ModLibrary.LoadAll` postfix (`startup:all-mods-loaded`) | `OnFullyLoaded` installs Harmony patches; asset ids now resolve |
| `ModLibrary.AttachGameData` | merges game data onto part templates (later in `Program` startup) |

Doing Harmony work at `StarMapAllModsLoaded` rather than at immediate load means every asset id the code asks for
already exists. See [Choose and use the right StarMap lifecycle hook](../../lifecycle/choose-a-starmap-startup-hook/overview.md) and [StarMap lifecycle attributes](../../lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md).

## Steps

1. **Create the folder layout.** The id is the folder name; the assembly name is independent of it:

   ```
   mods/FastAndPurrious/
     KSACars.dll                    StarMap entry assembly
     mod.toml
     KSACarsAssets.xml              meshes, materials, subparts, parts
     KSACarsGameData.xml            editor tag, seats, doors, mass, colliders
     KSACarsSounds.xml              engine sounds
     Meshes/KSACars_*.glb
     Textures/KSACars_*.png
     Sounds/KSACars_*.wav
   ```

2. **Write `mod.toml` with every asset listed.** One file serves both readers. Comment the reason; the omission
   failure is silent:

   ```toml
   # src/KSACars/mod.toml L1-L18
   # KSA reads this as a content mod, StarMap reads it as a code mod. One folder does both.

   name = "FastAndPurrious"

   # Part definitions. These sit at the mod root, next to Meshes/ and Textures/, mirroring the
   # layout of Content/Core - the relative paths inside them then resolve the same way whether the
   # loader reads them against the mod root or against the XML file's own directory.
   # Listed one by one, not globbed: a file left out of this list is simply never loaded, and
   # nothing reports it. An asset it declares then resolves to null at the point something asks
   # for it, which for a part is a crash inside KSA rather than a missing model.
   assets = [
       "KSACarsAssets.xml",
       "KSACarsGameData.xml",
       "KSACarsSounds.xml",
   ]

   [StarMap]
   EntryAssembly = "KSACars"
   ```

3. **Reference files relative to the folder.** In the XML use `Meshes/...`, `Textures/...`, `Sounds/...` exactly as
   they exist on disk (case-sensitive on Linux).
4. **Make the build output the install layout.** The csproj names the assembly after `EntryAssembly` and copies
   content beside the DLL:[src/KSACars/KSACars.csproj](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACars.csproj#L32-L51)

   ```xml
   <!-- src/KSACars/KSACars.csproj L32-L51 -->
     <ItemGroup>
       <None Update="mod.toml">
         <CopyToOutputDirectory>Always</CopyToOutputDirectory>
       </None>
       <!-- Part definitions, listed in mod.toml's assets array. -->
       <None Update="KSACars*.xml">
         <CopyToOutputDirectory>Always</CopyToOutputDirectory>
       </None>
       <!-- The cars' art, authored in Blender and committed as exported. -->
       <None Update="Meshes\**\*.glb">
         <CopyToOutputDirectory>Always</CopyToOutputDirectory>
       </None>
       <None Update="Textures\**\*.png">
         <CopyToOutputDirectory>Always</CopyToOutputDirectory>
       </None>
       <!-- Cut from recordings by tools/buggy-sounds.py and tools/eldorado-sounds.py. -->
       <None Update="Sounds\**\*.wav">
         <CopyToOutputDirectory>Always</CopyToOutputDirectory>
       </None>
     </ItemGroup>
   ```

   The same project sets `<AssemblyName>KSACars</AssemblyName>` with the comment that StarMap loads
   `<EntryAssembly>.dll`, so the name must match.
5. **Keep game DLLs out of the output.** Reference `KSA.dll`, `StarMap.API.dll`, `Brutal.*`, `0Harmony.dll` with
   `Private="false"`. The KSACars csproj comment says a second copy would load into another context with
   mismatched types; at StarMap 4dc60f8 the mod context hands out the already-loaded `Default` and game-context
   copies first, so a shipped copy is simply never loaded. It still bloats the archive and redistributes game
   files (see [Reference the game's assemblies from a mod project without shipping them](../../build/reference-game-assemblies-from-a-mod-project/overview.md)).
6. **Register and launch.** Add `[[mods]] id = "FastAndPurrious"`, `enabled = true` to `manifest.toml` (or accept
   KSA's "new mod" dialog and the StarMap restart prompt, see [Folder and manifest rules as StarMap consumes them](../how-starmap-launches-ksa/how-starmap-launches-ksa-2.md)) and start through
   `StarMap.exe`.

## Complete example

| File | Role |
|---|---|
| [src/KSACars/mod.toml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/mod.toml) | Both manifests in one file |
| [src/KSACars/KSACars.csproj](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACars.csproj) | Assembly name, content copy, `Private="false"` references |
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs) | `[StarMapMod]` entry class: `StarMapImmediateLoad` logs ids and builds, `StarMapAllModsLoaded` installs patches |

## Variations and gotchas

- **List every file.** No globbing; an unlisted XML is never loaded and nothing is logged. Guard with a check
  (approach 2 shows a script).
- **Names.** `AssemblyName` must equal `EntryAssembly`; the folder (mod id) may differ from both. KSACars keeps
  the id `FastAndPurrious` but the assembly, namespace and part Ids `KSACars`.
- **Load order.** Mods load in manifest order. A mod whose assets depend on stock assets loaded by `Core` is
  safe after it; the KSACars notes warn that characters loaded before Core can fail.
- **Never ship the game's DLLs**; the packaging script refuses (see [Package and publish a mod release (zip, CI publishing, installer)](../../build/package-and-publish-a-mod-release/overview.md)).
- **Case.** Linux file systems are case-sensitive; unzip archives rather than retyping names.

## See also

- [Mixed assets list with csproj content copy and build stamp](./ship-content-and-code-in-one-mod-2.md), [Find your mod's install folder at runtime: Assembly.Location, then ModLibrary.Find, then Documents](./ship-content-and-code-in-one-mod-3.md), [Ship assets and code in one mod folder and find it at runtime](./overview.md)
- [One folder as both a content mod and a StarMap code mod](../../../../mods/ksacars/features/mod-folder-content-and-code-mod.md)
- [Write mod.toml - KSA keys and the [StarMap] table](../mod-toml-reference/overview.md), [How StarMap launches KSA and loads your mod](../how-starmap-launches-ksa/overview.md), [Package and publish a mod release (zip, CI publishing, installer)](../../build/package-and-publish-a-mod-release/overview.md)
