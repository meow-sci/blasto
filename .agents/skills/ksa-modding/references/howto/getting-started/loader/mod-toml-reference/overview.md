---
type: Game Feature
title: Write mod.toml - KSA keys and the StarMap table
description: How a mod.toml is read twice, by KSA into KSA.Mod and by StarMap into its entry and dependency model, and where the defaults and dead keys are
category: getting-started
group: loader
topic: mod-toml-reference
demonstrated_by:
  - mods/starmap/features/manifest-mod-discovery
  - mods/starmap/features/mod-dependency-graph
  - mods/kitten-engineer-redux/features/dist-zip-and-post-build-deploy
  - mods/ohmystars/features/mod-root-and-packaging
lifecycle: [startup:starmap-before-main, startup:asset-load]
game_types: [KSA.Mod, KSA.ModEntry, KSA.ModManifest]
tags: [mod-toml, manifest, tomlet, starmap, reference]
---

`mod.toml` is one file with two independent readers. KSA deserializes it into `KSA.Mod` (content lists, systems,
fonts, quality options); StarMap parses the same text into `RootConfig { StarMap }` for the entry assembly and
dependencies. Neither rejects keys it does not know, and several declared keys are never read.

## Engine background

| Type | File | Role |
|---|---|---|
| `KSA.Mod` | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L14-L111) | The `[TomlField]` members that form KSA's schema; `MakeUsing` deserializes with Tomlet, sets `Id` (folder name) and `DirectoryPath` |
| `KSA.ModLibrary` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L522) | `PrepareAll` builds the `Mod` and runs `PrepareSystems` and `LoadSettings`; `LoadAll` runs the loaders for fonts, assets, systems, meshes and console lists |
| `StarMapConfig` | [StarMap.Core/Config/StarMapConfig.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Config/StarMapConfig.cs#L7-L25) | `EntryAssembly` (required when the table exists), `ExportedAssemblies`, `ModDependencies` |
| `RuntimeMod.TryCreateMod` | [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L36-L63) | Reads `[StarMap]`, defaults `EntryAssembly` to the manifest id |

Timing: StarMap reads its table at `startup:starmap-before-main`; KSA consumes `systems` and the settings keys in
`ModLibrary.PrepareAll` and `assets` / `fonts` in `ModLibrary.LoadAll` (`startup:asset-load`), see
[Game loop and mod lifecycle](../../lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

One approach: a reference table with the smallest valid files.

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Write mod.toml: KSA keys and the StarMap table](./mod-toml-reference.md) | StarMap, with Kitten-Engineer-Redux and OhMyStars as no-table examples | `toml-config` | `startup:starmap-before-main`, `startup:asset-load` | Complete key list and defaults; the dead keys (`ExportedAssemblies`, the `supported*Quality` lists) are called out |

## See also

- [How StarMap launches KSA and loads your mod](../how-starmap-launches-ksa/overview.md), [Declare a dependency on another StarMap mod, required or optional](../../dependencies/declare-a-mod-dependency/overview.md), [Ship assets and code in one mod folder and find it at runtime](../ship-content-and-code-in-one-mod/overview.md),
  [Generate mod.toml and StarMap dependencies from the project file](../../build/generate-mod-toml-from-msbuild/overview.md)
- [Discover code mods through KSA's mod manifest](../../../../mods/starmap/features/manifest-mod-discovery.md), [Order mod loading with a ModDependencies graph](../../../../mods/starmap/features/mod-dependency-graph.md),
  [Minimal mod.toml, post-build deploy and release zip](../../../../mods/kitten-engineer-redux/features/dist-zip-and-post-build-deploy.md), [Mod root and packaging](../../../../mods/ohmystars/features/mod-root-and-packaging.md)
