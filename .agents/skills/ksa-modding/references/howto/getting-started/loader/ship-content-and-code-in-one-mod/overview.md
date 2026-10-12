---
type: Game Feature
title: Ship assets and code in one mod folder and find it at runtime
description: How a single folder serves KSA as a content mod and StarMap as a code mod, how asset paths resolve against the mod root, and how code locates that folder at runtime
category: getting-started
group: loader
topic: ship-content-and-code-in-one-mod
demonstrated_by:
  - mods/ksacars/features/mod-folder-content-and-code-mod
  - mods/ksagolf/features/content-and-code-mod-layout
  - mods/ohmystars/features/mod-root-and-packaging
  - mods/starmap/features/per-mod-assembly-load-context
lifecycle: [startup:asset-load, startup:immediate-load, startup:all-mods-loaded]
game_types: [KSA.Mod, KSA.ModLibrary, KSA.FileReference, KSA.AssetBundle]
tags: [mod-toml, assets, content-mod, folder-layout, mod-root]
---

A KSA mod that adds parts, sounds or textures is an XML-and-files folder described by `mod.toml`. A StarMap mod
adds a DLL to the same folder. Putting both in one folder gives players one archive and gives the code easy access
to its own data, but the rules of both loaders apply: assets must be listed explicitly, paths resolve against the
mod root, and the code has to find the folder under unusual installs.

## Engine background

| Member | File | Behavior |
|---|---|---|
| `Mod.LoadAssetBundles` | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L157-L169) | One `XmlLoader.Load<AssetBundle>` per `assets` entry; no globbing, no directory scan |
| `Mod.GetPath`, `Mod.DirectoryPath` | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L389-L392) | `Path.Combine(DirectoryPath, localPath)` with separators corrected |
| `FileReference.ModPath` | [current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L13-L26) | Every `Path=` attribute in asset XML goes through `Mod.GetPath`, so it is relative to the mod folder, not the XML file |
| `ModLibrary.PrepareAll` / `LoadAll` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L522) | Finds `Content/<id>` then `<Documents>/mods/<id>`; loads assets mod by mod in manifest order |
| `ModLibrary.Find(string id)` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L182-L190) | Registered `Mod` by manifest id, or `null` |

Lifecycle vocabulary ([Game loop and mod lifecycle](../../lifecycle/game-loop-and-lifecycle/overview.md)): assets deserialize in `startup:asset-load`; your
`StarMapImmediateLoad` runs just before a mod's own `PrepareSystems` (`startup:immediate-load`); Harmony patches
that need asset ids belong at `startup:all-mods-loaded`.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Flat folder with an explicit assets list](./ship-content-and-code-in-one-mod-1.md) | KSACars | `toml-config` | `startup:asset-load` | The simplest layout: XML at the root beside `Meshes/`, `Textures/`, `Sounds/`; relies on discipline to list every file |
| [Mixed content with csproj copy, assets guard and build stamp](./ship-content-and-code-in-one-mod-2.md) | KSAGolf | `toml-config`, `msbuild` | `startup:immediate-load` | Adds a validation script and a KSA-build stamp; more tooling to maintain |
| [Find your mod's install folder at runtime](./ship-content-and-code-in-one-mod-3.md) | OhMyStars | `reflection` | `startup:all-mods-loaded` | Robust data-file access under instance paths and stale copies; a small helper class |

## Rules shared by all three

- The `assets` list is exhaustive: [src/KSACars/mod.toml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/mod.toml) says a file left out "is simply never loaded, and nothing
  reports it".
- Binary files (glb, png, wav) are not listed in `mod.toml`; asset XML references them by relative `Path`.
- The folder name is the mod id; the entry DLL is named by `EntryAssembly` (see [Write mod.toml - KSA keys and the [StarMap] table](../mod-toml-reference/overview.md)).
- For data files read by code, prefer the assembly's directory: [src/ModPaths.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/ModPaths.cs).

## See also

- [Write mod.toml - KSA keys and the [StarMap] table](../mod-toml-reference/overview.md), [How StarMap launches KSA and loads your mod](../how-starmap-launches-ksa/overview.md), [Package and publish a mod release (zip, CI publishing, installer)](../../build/package-and-publish-a-mod-release/overview.md),
  [Ship and load native libraries from a mod folder](../../dependencies/ship-native-libraries-with-a-mod/overview.md)
- [One folder as both a content mod and a StarMap code mod](../../../../mods/ksacars/features/mod-folder-content-and-code-mod.md), [Content-plus-code mod layout](../../../../mods/ksagolf/features/content-and-code-mod-layout.md),
  [Mod root and packaging](../../../../mods/ohmystars/features/mod-root-and-packaging.md), [Isolate each mod in its own AssemblyLoadContext](../../../../mods/starmap/features/per-mod-assembly-load-context.md)
