---
type: Mod Feature
title: Content-plus-code mod layout
description: One mod folder that KSA loads as a content mod (asset XML, meshes, textures, sounds) and StarMap loads as a code mod, built so the game assemblies are referenced but never shipped
mod: ksagolf
category: getting-started
techniques: [starmap-lifecycle, toml-config, xml-asset]
hooks: [starmap StarMapImmediateLoad, starmap StarMapAllModsLoaded]
lifecycle: [startup:immediate-load, startup:asset-load, startup:all-mods-loaded]
game_types: [KSA.Mod, KSA.ModLibrary, KSA.AssetBundle, KSA.FileReference]
tags: [mod-toml, manifest, starmap, csproj, msbuild, assets, layout]
---

KSAGolf ships as a single folder that the game reads twice. KSA's own mod library reads `mod.toml`
for its `assets` list and loads the three asset XML files (props, sounds, course pieces) with their
meshes, textures and wav files. StarMap reads the `[StarMap]` table in the same file and loads
`KSAGolf.dll`. Nothing else is needed: Harmony comes with StarMap, and every game assembly the code
compiles against is referenced with `Private="false"` so it never lands in the output.

The pattern is worth copying for any mod that has both art and behaviour: one manifest, one folder,
one archive, and a build that copies the content beside the DLL so the build output *is* the install.

## How it works

| Piece | What it does |
|---|---|
| `mod.toml` `name` | The content-mod name (KSA's `Mod.Name`)[^golf-modtoml] |
| `mod.toml` `assets = [...]` | Each entry is a path relative to the folder holding `mod.toml`; KSA loads each as an `AssetBundle` (root element `<Assets>`)[^ksa-mod] |
| `mod.toml` `[StarMap] EntryAssembly` | StarMap loads `<EntryAssembly>.dll` and instantiates the first `[StarMapMod]` class |
| `manifest.toml` in the user directory | `[[mods]] id = "KSAGolf"`; KSA only discovers mods listed there, and the manifest `id` becomes `Mod.Id`[^ksa-modlibrary] |
| `KSAGolf.csproj` `None Update` items | Copy `mod.toml`, `KSAGolf*.xml`, `Meshes/**/*.glb`, `Textures/**/*.png`, `Sounds/**/*.wav` to the output so `bin/Release/net10.0` mirrors the install |
| `Reference ... Private="false"` | Compile against `KSA.dll`, `StarMap.API.dll`, Brutal, Bepu, `0Harmony.dll` without copying them |
| `Directory.Build.props` | Resolves `$(KsaDllDir)` in tiers: `KSA_DLL_DIR`, then `Import/`, then a sibling `ksa-game-assemblies` checkout, then common install paths; fails the build with a readable message when none exists |
| `KsaBuild` assembly metadata | The csproj regex-reads the `build` line of `ksa-assemblies.lock` and stamps it as `AssemblyMetadata("KsaBuild", ...)`, which the startup log compares to the running game |

Where KSA looks for the folder: `ModLibrary.PrepareAll` walks the manifest, tries `Content/<id>` and
then `ModLibrary.LocalModsFolderPath` (`<Documents>/My Games/Kitten Space Agency/mods/<id>`), and
expects `mod.toml` inside it.[^ksa-modlibrary] Asset paths inside the XML resolve through
`FileReference.ModPath`, which is `Mod.GetPath(LocalPath)` = `Path.Combine(Mod.DirectoryPath,
localPath)`, so `Path="Meshes/..."` is relative to the mod root (the folder of `mod.toml`), not to
the XML file's own directory.[^ksa-filereference] KSAGolf keeps its XML at the mod root, so the
question its comments leave open does not arise.

## Lifecycle and timing

| Moment | What happens for this layout |
|---|---|
| `startup:immediate-load` | StarMap prefixes `Mod.PrepareSystems` and calls `[StarMapImmediateLoad] OnImmediateLoad(Mod)` for this mod; KSAGolf only logs the mod id and the built-for vs running KSA build[^starmap-modpatches] |
| `startup:asset-load` | `ModLibrary.LoadAll` calls `Mod.LoadAssetBundles()` per mod, which runs `XmlLoader.Load<AssetBundle>` on every `assets` entry[^ksa-modlibrary] |
| `startup:all-mods-loaded` | StarMap's postfix on `ModLibrary.LoadAll` calls `[StarMapAllModsLoaded] OnFullyLoaded()`, where KSAGolf installs all its Harmony patches and builds its panel; asset ids such as `KSAGolf_Ball_Glb` are registered by then[^starmap-modlibrarypatches] |

Doing the Harmony work in `StarMapAllModsLoaded` rather than at immediate load means every asset id
the code later asks `ModLibrary.Get<T>` for already exists.

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/mod.toml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/mod.toml) | Content and StarMap manifest in one file |
| [src/KSAGolf/KSAGolf.csproj](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/KSAGolf.csproj) | Content copy rules, `Private="false"` game references, `KsaBuild` stamp |
| [Directory.Build.props](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/Directory.Build.props) | Tiered resolution of the game assembly folder and a friendly missing-assemblies error |
| [src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L14-L65) | `[StarMapMod]` entry class, immediate-load and all-mods-loaded hooks |
| [tools/deploy.sh](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/deploy.sh) | Copies the build output into `<user dir>/mods/KSAGolf` and appends the `[[mods]]` entry to `manifest.toml` |
| [tools/validate-parts.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/validate-parts.py#L244-L268) | Fails when an asset XML at the mod root is missing from `assets`, or `assets` names a file that is not there |

## Snippets

```toml
# src/KSAGolf/mod.toml L1-L11
# KSA reads this as a content mod, StarMap reads it as a code mod. One folder does both.

name = "KSAGolf"

# Asset definitions, listed one by one rather than globbed: a file left out of this list is never
# loaded, and nothing reports it. They sit at the mod root beside Meshes/ and Textures/, so their
# relative paths resolve the same whether read against the mod root or the XML's own directory.
assets = ["KSAGolfProps.xml", "KSAGolfSounds.xml", "KSAGolfCourse.xml"]

[StarMap]
EntryAssembly = "KSAGolf"
```

```xml
<!-- src/KSAGolf/KSAGolf.csproj L56-L60, L75-L77, L83-L94 -->
  <ItemGroup>
    <Reference Include="$(KsaDllDir)KSA.dll" Private="false" />
    <Reference Include="$(KsaDllDir)StarMap.API.dll" Private="false" />
    <Reference Include="$(KsaDllDir)Brutal.Core.Numerics.dll" Private="false" />
    <Reference Include="$(KsaDllDir)Brutal.Core.Common.dll" Private="false" />
    <!-- ... -->
    <!-- Harmony ships with StarMap, so patching the engine asks a player to install nothing. -->
    <Reference Include="$(KsaDllDir)0Harmony.dll" Private="false" />
  </ItemGroup>
  <!-- ... -->
  <PropertyGroup>
    <KsaLockPath>$(MSBuildThisFileDirectory)../../ksa-assemblies.lock</KsaLockPath>
    <KsaBuildLine Condition="Exists('$(KsaLockPath)')">$([System.Text.RegularExpressions.Regex]::Match($([System.IO.File]::ReadAllText('$(KsaLockPath)')), 'build[ \t]+[0-9][^\r\n]*').Value)</KsaBuildLine>
    <KsaBuild Condition="'$(KsaBuildLine)' != ''">$(KsaBuildLine.Substring(6).Trim())</KsaBuild>
  </PropertyGroup>

  <ItemGroup Condition="'$(KsaBuild)' != ''">
    <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute">
      <_Parameter1>KsaBuild</_Parameter1>
      <_Parameter2>$(KsaBuild)</_Parameter2>
    </AssemblyAttribute>
  </ItemGroup>
```

```csharp
// src/KSAGolf/Ksa/KSAGolfMod.cs L32-L37
    [StarMapImmediateLoad]
    public void OnImmediateLoad(Mod mod)
    {
        Log.Info($"loading (mod id: {mod.Id})");
        Log.Info($"KSAGolf {Build.Version} built for KSA {Build.KsaBuild ?? "?"}, running {Build.KsaRunning ?? "?"}");
    }
```

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `Mod` TOML fields `name`, `assets` | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs) | `[TomlField("assets")] private string[] Assets`; `LoadAssetBundles` loads each as an `AssetBundle` |
| `Mod.GetPath` / `Mod.DirectoryPath` | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs) | Every asset `Path` is combined with the folder of `mod.toml` |
| `ModLibrary.PrepareAll` / `LoadAll` / `LocalModsFolderPath` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs) | Manifest walk, folder lookup (`Content/<id>` then `<Documents>/.../mods/<id>`), asset load |
| `FileReference.ModPath` | [current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs) | Path resolution for `<Source Path>`, `<Diffuse Path>`, `<SoundFile Path>` |
| `AssetBundle` (`[XmlRoot("Assets")]`) | [current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs) | The element-name to type table every asset XML is read through |

## Pitfalls and version notes

- An asset XML that is not in `assets` is never loaded and nothing logs it; KSAGolf's
  `validate-parts.py` checks both directions, and `deploy.sh` refuses to deploy when it fails.[^golf-validate-parts]
- Dropping the folder into `mods/` is not enough; the `manifest.toml` entry is what makes KSA (and
  StarMap, which walks the same list) see it. `deploy.sh` appends the entry and keeps a `.bak`.[^golf-deploy]
- `Mod.Id` comes from the manifest entry, not from `mod.toml`'s `name`; the mod logs `mod.Id` at
  immediate load so a mismatch is visible.[^ksa-modlibrary]
- A second copy of a game assembly in the mod folder would load into the mod's own load context
  and break type identity; `Private="false"` keeps them out of the build output, and the packaging script refuses any foreign DLL (see [Packaging and release](packaging-and-release.md)).[^golf-csproj]
- The repo targets `net10.0` and KSA build `2026.9.22.5482` (from `ksa-assemblies.lock`); this
  page was checked against build `2026.10.10.5554`, where the `Mod`/`ModLibrary` members above are
  unchanged in shape.

## See also

- [Prop and course asset XML](prop-and-course-asset-xml.md)
- [World-positioned sounds](world-positioned-sounds.md)
- [Packaging, deploy and release pipeline](packaging-and-release.md)
- [API surface record and assembly lock](api-surface-and-assembly-lock.md)
- [Dual-hook frame stepping on simulated time](dual-hook-frame-stepping.md)
- [Resolve the game folder in tiers from Directory.Build.props](../../../howto/getting-started/build/reference-game-assemblies-from-a-mod-project/reference-game-assemblies-from-a-mod-project-2.md) - how-to article demonstrating this feature
- [Understand the mod folder, manifest and enable/ordering rules](../../../howto/getting-started/loader/how-starmap-launches-ksa/how-starmap-launches-ksa-2.md) - how-to article demonstrating this feature
- [Ship mixed content with a csproj content copy, an assets guard and a build stamp](../../../howto/getting-started/loader/ship-content-and-code-in-one-mod/ship-content-and-code-in-one-mod-2.md) - how-to article demonstrating this feature

[^golf-modtoml]: src/KSAGolf/mod.toml
[^golf-csproj]: src/KSAGolf/KSAGolf.csproj
[^golf-deploy]: tools/deploy.sh
[^golf-validate-parts]: tools/validate-parts.py
[^ksa-mod]: KSA.Mod (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-filereference]: KSA.FileReference (decompiled, build 2026.10.10.5554)
[^starmap-modpatches]: StarMap.Core/Patches/ModPatches.cs
[^starmap-modlibrarypatches]: StarMap.Core/Patches/ModLibraryPatches.cs
