---
type: Mod Feature
title: One folder as both a content mod and a StarMap code mod
description: A single mod folder whose mod.toml is read by KSA for its explicitly listed asset XML files and by StarMap for the entry assembly, with the XML at the root beside Meshes, Textures and Sounds and a csproj that copies them next to the DLL
mod: ksacars
category: getting-started
techniques: [starmap-lifecycle, toml-config, xml-asset, assembly-load-context]
hooks: [starmap StarMapImmediateLoad, starmap StarMapAllModsLoaded]
lifecycle: [startup:asset-load, startup:immediate-load, startup:all-mods-loaded]
game_types: [KSA.Mod, KSA.ModLibrary, KSA.AssetBundle, KSA.FileReference]
tags: [mod-toml, manifest, starmap, assets, layout, csproj, assembly-load-context]
---

KSACars ships as one folder, `mods/FastAndPurrious/`, that is two mods at once. KSA's own content
system reads `mod.toml`'s `assets` array and loads the part, game-data and sound XML; StarMap reads
the same file's `[StarMap]` table and loads `KSACars.dll` into its own `AssemblyLoadContext`.[^ksacars-modtoml]
Without StarMap the cars still appear in the editor and render; they just do not drive.[^ksacars-notes]

## How it works

**`mod.toml`** (verbatim keys):

| Key | Read by | Meaning |
|---|---|---|
| `name = "FastAndPurrious"` | KSA (`Mod.Name`, `[TomlField("name")]`) | Display name |
| `assets = [ "KSACarsAssets.xml", "KSACarsGameData.xml", "KSACarsSounds.xml" ]` | KSA (`Mod.Assets`, `[TomlField("assets")]`) | Asset bundles to load, in order, relative to the folder |
| `[StarMap] EntryAssembly = "KSACars"` | StarMap (`StarMapConfig.EntryAssembly`) | Loads `<folder>/KSACars.dll`; without the table StarMap assumes `<mod id>.dll` |

The **mod id** is the folder name, which is also the `id` the player adds to `manifest.toml`
(`[[mods]] id = "FastAndPurrious" enabled = true`); dropping the folder in is not enough.[^ksacars-readme]
`Mod.MakeUsing(id, manifestPath)` sets `Id` and `DirectoryPath` from the manifest entry and the
folder, and StarMap's `RuntimeMod.TryCreateMod` looks for `mod.toml` under the game's `Content/<id>`
or the user mods folder `<id>`, then for `<EntryAssembly>.dll` beside it.[^ksa-mod][^starmap-runtimemod]
The assembly name and the entry class name are independent: StarMap instantiates the first type
whose attributes include one *named* `StarMapModAttribute`.

**Why every file is listed.** `Mod.LoadAssetBundles` loops over the `assets` strings and calls
`XmlLoader.Load<AssetBundle>(Path.Combine(DirectoryPath, path), this)` for each; there is no glob
or directory scan. A file left out is never loaded and nothing reports it; anything it declares
resolves to null later, which for a part is a crash inside KSA rather than a missing model (the
mod.toml comment says so). Core's own `Content/Core/mod.toml` lists its asset files the same way.[^ksa-mod][^ksa-core-modtoml]

**Layout.**

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

Paths inside the XML (`Meshes/...`, `Textures/...`, `Sounds/...`) resolve through `Mod.GetPath`,
which is `Path.Combine(DirectoryPath, localPath)`: against the mod folder, not the XML file. The
XML sits at the root "mirroring Core", so the question does not arise either way.[^ksa-mod]

**Build output = install layout.** The csproj names the assembly `KSACars` (it must equal
`EntryAssembly`), copies `mod.toml`, `KSACars*.xml`, `Meshes/**/*.glb`, `Textures/**/*.png` and
`Sounds/**/*.wav` to the output, and references every game and loader DLL (`KSA.dll`,
`StarMap.API.dll`, `Brutal.*`, `0Harmony.dll`, `BepuUtilities.dll`, `Planet.Render.Core.dll`) with
`Private="false"`, so none is copied: a second copy would load into the mod's own load context and
its types would not match the running game's.[^ksacars-csproj] Building, deploying and packaging are
covered in [package, deploy and release](./package-deploy-release.md).

**Entry class.** `KSACarsMod` is `[StarMapMod]`; `[StarMapImmediateLoad]` only logs the mod id and
the KSA build it was compiled for versus the one running; `[StarMapAllModsLoaded]` installs the
nine Harmony patches; the frame and GUI hooks run the cars; `[StarMapUnload]` stops the sounds,
calls each hook's `Remove` and flushes the [log](./mod-log-file.md).[^ksacars-mod] Only the flush
still does anything at that point: StarMap invokes `[StarMapUnload]` from `ModLoader.Dispose`,
after `StarMapCore.DeInit` has called `UnpatchAll()` with no id, which removes every Harmony patch
in the process (StarMap's and every mod's), and after `Program.Dispose` has released the audio
system.[^starmap-core][^starmap-modloader]

## Lifecycle and timing

| Moment | What happens for this folder |
|---|---|
| StarMap start-up (`StarMapCore` -> `ModLoader.Init`) | Manifest read; `RuntimeMod.TryCreateMod` creates the load context, `InitializeMod` instantiates `KSACarsMod` |
| `ModLibrary.PrepareAll` -> `Mod.PrepareSystems` (StarMap prefix = `StarMapImmediateLoad`) | `OnImmediateLoad(Mod)`: logs `mod.Id` and builds |
| `ModLibrary.LoadEditorTags`, `ModLibrary.LoadAll` (`startup:asset-load`) | `EditorTagDef` registered; the three XML bundles loaded in `assets` order, mod by mod in manifest order |
| `ModLibrary.LoadAll` postfix (`StarMapAllModsLoaded`) | Harmony patches installed |
| `ModLibrary.AttachGameData`, `ModLibrary.PopulateSounds` (later in `Program` startup) | GameData merged into the part templates; sounds populated |

Mods load in the order of the local `manifest.toml`; KSACars declares no `<Character>`, so it is not
exposed to the "loaded before Core" failure the mod's notes describe for character mods.[^ksacars-notes][^ksa-modlibrary]

## Key code

| Path | Role |
|---|---|
| [src/KSACars/mod.toml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/mod.toml) | Both manifests in one file, with the reasons in comments |
| [src/KSACars/KSACars.csproj](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACars.csproj) | Assembly name, content copy, `Private="false"` references, `KsaBuild` metadata |
| [src/KSACars/Ksa/KSACarsMod.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/KSACarsMod.cs) | `[StarMapMod]` entry class and its six hooks |
| [README.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/README.md#L22-L52) | Install layout, `manifest.toml` entry, launch through StarMap |

## Snippets

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

```xml
<!-- src/KSACars/KSACars.csproj L32-L51 and L63-L65 -->
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
  <!-- ... -->
  <ItemGroup>
    <Reference Include="$(KsaDllDir)KSA.dll" Private="false" />
    <Reference Include="$(KsaDllDir)StarMap.API.dll" Private="false" />
```

```csharp
// current/decomp/KSA/Mod.cs L157-L169
	public void LoadAssetBundles()
	{
		string[] assets = Assets;
		foreach (string path in assets)
		{
			string filePath = Path.Combine(DirectoryPath, path);
			AssetBundle assetBundle = XmlLoader.Load<AssetBundle>(filePath, this);
			if (assetBundle != null)
			{
				Register(assetBundle);
			}
		}
	}
```

## Game APIs touched

| API | Purpose |
|---|---|
| `Mod` TOML fields (`name`, `assets`, `systems`, `fonts`, ...) ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L26-L75)) | What KSA reads from `mod.toml` |
| `Mod.LoadAssetBundles`, `Mod.GetPath`, `Mod.MakeUsing` ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L103-L392)) | Explicit asset list; folder-relative paths |
| `ModLibrary.LoadAll` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L478-L520)) | Per-mod load in manifest order; StarMap's `StarMapAllModsLoaded` postfix target |
| `RuntimeMod.TryCreateMod`, `InitializeMod` ([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L36-L140)) (StarMap) | `mod.toml` `[StarMap]`, entry assembly, load context, `[StarMapMod]` lookup |

## Pitfalls and version notes

- **List every asset file.** No globbing; a missing entry fails silently and late.
- **Register in `manifest.toml`** and launch through StarMap (`StarMap.exe` / `dotnet StarMap.dll`),
  not `KSA.exe`.[^ksacars-readme]
- **Names are case-sensitive on Linux**; the README tells players to unzip rather than retype.
- **`AssemblyName` must equal `EntryAssembly`**, and the mod id (folder) may differ from both:
  here the folder is `FastAndPurrious` and the DLL is `KSACars.dll`. The assembly, namespace, part
  Ids and asset Ids stay `KSACars` because saves pair parts by Id.[^ksacars-claude]
- **Never ship the game's DLLs** (`Private="false"`); `tools/package.sh` refuses to.
- The content half works without any loader; a CKAN-like package manager only distributes the
  archive.[^ksacars-notes]

## See also

- [Car part assets: GLB, PBR materials and subparts](./car-part-assets-glb-pbr.md)
- [Car part game data](./car-part-gamedata.md)
- [Engine sound crossfade](./engine-sound-crossfade.md) (the Sounds XML)
- [Package, deploy and release](./package-deploy-release.md)
- [Mod log file](./mod-log-file.md)
- [Mod overview](../overview.md)
- [Find out when KSA reads each mod.toml key and where hooks can see the data](../../../howto/assets/asset-xml/mod-xml-load-order/mod-xml-load-order.md) - how-to article demonstrating this feature
- [Lay out a flat mod folder with an explicit assets list](../../../howto/getting-started/loader/ship-content-and-code-in-one-mod/ship-content-and-code-in-one-mod-1.md) - how-to article demonstrating this feature

[^ksacars-modtoml]: src/KSACars/mod.toml
[^ksacars-csproj]: src/KSACars/KSACars.csproj
[^ksacars-readme]: README.md
[^ksacars-claude]: CLAUDE.md
[^ksacars-notes]: docs/KSA-MODDING-NOTES.md
[^ksacars-mod]: src/KSACars/Ksa/KSACarsMod.cs
[^starmap-runtimemod]: StarMap.Core/ModRepository/RuntimeMod.cs
[^ksa-mod]: KSA.Mod (decompiled)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled)
[^ksa-core-modtoml]: Core mod.toml
[^starmap-core]: StarMap.Core/StarMapCore.cs
[^starmap-modloader]: StarMap.Core/ModRepository/ModLoader.cs
