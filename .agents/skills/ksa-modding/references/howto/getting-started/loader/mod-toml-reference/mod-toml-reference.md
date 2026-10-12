---
type: How-To
title: "Write mod.toml: KSA keys and the StarMap table"
description: Reference for every mod.toml key KSA reads, the StarMap table and its defaults, the smallest valid files, and which keys are parsed but never acted on
category: getting-started
group: loader
topic: mod-toml-reference
difficulty: beginner
prerequisites: [starmap]
demonstrated_by:
  - mods/starmap/features/manifest-mod-discovery
  - mods/starmap/features/mod-dependency-graph
  - mods/kitten-engineer-redux/features/dist-zip-and-post-build-deploy
  - mods/ohmystars/features/mod-root-and-packaging
techniques: [toml-config]
hooks: [call KSA.Mod.MakeUsing, call StarMap.Core.ModRepository.RuntimeMod.TryCreateMod]
lifecycle: [startup:starmap-before-main, startup:asset-load]
game_types: [KSA.Mod, KSA.ModEntry, KSA.ModManifest]
tags: [mod-toml, manifest, tomlet, starmap, reference]
---

`mod.toml` is read by two independent consumers. KSA deserializes it into `KSA.Mod` with Tomlet
(content, systems, fonts, settings). StarMap parses the same text again into its own model for the
optional `[StarMap]` table (entry assembly, dependencies). Neither complains about keys it does not know,
which is why `version`, `author` and `description` can sit beside real keys. This reference lists what each
key does, the defaults, and the smallest files that work. For how the folder and manifest around it behave,
see [Folder and manifest rules as StarMap consumes them](../how-starmap-launches-ksa/how-starmap-launches-ksa-2.md).

## Background: how the game does it

`Mod.MakeUsing(id, manifestPath)` calls `TomletMain.To<Mod>(File.ReadAllText(manifestPath))`, then sets `Id`
to the id passed in (the folder name) and `DirectoryPath` to the folder containing the file.[current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L103-L111)
Every relative path in `mod.toml` is combined with `DirectoryPath`. StarMap reads the same file in
`RuntimeMod.TryCreateMod` into a `RootConfig { StarMap }` and defaults `EntryAssembly` to the manifest id when
the table is absent.[StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L36-L63)

## Lifecycle and timing

| Reader | When | Keys |
|---|---|---|
| StarMap `RuntimeMod.TryCreateMod` | `startup:starmap-before-main`, inside `ModLoader.PrepareMods` | `[StarMap]` table only |
| `ModLibrary.PrepareAll` -> `Mod.MakeUsing`, `PrepareSystems`, `LoadSettings` | `startup:immediate-load` window | whole file; `systems` and the settings keys are consumed here |
| `ModLibrary.LoadAll` -> `LoadFonts`, `LoadAssetBundles`, `LoadSystems`, planet collections, console lists | `startup:asset-load` | `fonts`, `assets`, `systems`, `planetMeshes`, `planetRandomHeightmapCollections`, `[console]` |

Order across mods is the manifest order ([StarMap lifecycle attributes](../../lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-1.md) has the startup table).

## Steps

1. **Start from the smallest valid file.** Two real examples, neither with a `[StarMap]` table:

   ```toml
   # Kitten-Engineer-Redux/mod.toml
   name = "KittenEngineerRedux"
   ```

   ```toml
   # OhMyStars packaging/mod.toml
   name="Oh My Stars"
   version = "1.3.0"
   author = "nanonestor"
   ```

   Both work only because the folder (`KittenEngineerRedux`, `OhMyStars`) matches the DLL
   (`KittenEngineerRedux.dll`, `OhMyStars.dll`). `name` is only the display name: OhMyStars' `name` has spaces
   and does not matter.[mod.toml](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/mod.toml)[packaging/mod.toml](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/packaging/mod.toml)
2. **Add `[StarMap]` only when you need to.** Typical reasons: the assembly name differs from the folder, or
   you declare dependencies. KSAero is the smallest explicit file:[KSAero/mod.toml](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/mod.toml)

   ```toml
   # KSAero/KSAero/mod.toml
   name = "KSAero"

   [StarMap]
   EntryAssembly = "KSAero"
   ```

3. **Add content keys** (KSA side) only for content mods; see the table below. All paths are relative to the
   `mod.toml` folder; asset lists are not globbed (see [Ship assets and code in one mod folder and find it at runtime](../ship-content-and-code-in-one-mod/overview.md)).
4. **Declare dependencies** with `[[StarMap.ModDependencies]]` (see [Declare a dependency on another StarMap mod, required or optional](../../dependencies/declare-a-mod-dependency/overview.md)):

   ```toml
   # KSA-AdvancedFlightComputer: AdvancedFlightComputer/mod.toml L1-L9
   name = "AdvancedFlightComputer"
   patches = [ "Patches/HyperbolicBodies.xml", "Patches/AutoStageButton.xml" ]

   [StarMap]
   EntryAssembly = "AdvancedFlightComputer"

   [[StarMap.ModDependencies]]
   ModId = "KittenExtensions"
   Optional = true
   ```

   Here `patches` is not a KSA key: it is read by KittenExtensions' own model of `mod.toml`.
5. **Keep it generated if it must stay in sync with the project** ([Generate mod.toml and StarMap dependencies from the project file](../../build/generate-mod-toml-from-msbuild/overview.md)).

## Reference: keys KSA reads (`KSA.Mod` `[TomlField]` members)

| Key | Type | Reader | Effect |
|---|---|---|---|
| `name` | string | `Mod.Name` | Display name only |
| `assets` | string[] | `LoadAssetBundles`, `LoadEditorTagDefinitions` | XML asset bundles (parts, sounds, textures, shaders), loaded in listed order; unlisted files are never loaded |
| `systems` | string[] | `PrepareSystems`, `LoadSystems` | System templates: `PrepareSystems` registers picker entries (startup), `LoadSystems` loads templates for the selected system |
| `fonts` | string[] | `LoadFonts` | Files registered with `FontManager.RegisterFont`; a missing file logs `error font '...' not found` and is skipped |
| `planetMeshes` | string[] | `LoadPlanetMeshes` | `PlanetMeshCollection` XML for planet LODs |
| `planetRandomHeightmapCollections` | string[] | `LoadPlanetRandomHeightmaps` | Random heightmap collection XML |
| `starBinaries` | string[] | `GetStarCount`, `LoadStarBinaries` | Binary star catalogs added as star instances |
| `textureSizeCaps`, `supportedShadowMaps`, `supportedThumbnailSizes` | ushort[] | `LoadSettings` | Register options in the graphics settings |
| `simulationSpeeds` | double[] | `LoadSettings` | Register sim speeds with `Universe` |
| `supportedCloudQuality`, `supportedOceanSimQuality`, `supportedExhaustQuality`, `supportedKittenFurQuality`, `supportedShadowSlots` | list of `UshortOption` | none | Declared on `KSA.Mod`; no reader in the decompiled `KSA` assembly of this build |
| `[console] onBoot`, `onLoad` | string lists | `LoadAll` | Console commands collected into `ModLibrary.OnBoot` / `OnLoad` |

Other public members (`Preload`, `AssetBundles`, `Id`, `DirectoryPath`) carry no `[TomlField]` and are set by KSA itself (`Id` is the folder name); do not put them in `mod.toml`. `version`,
`author`, `description` and the `[StarMap]` table have no field on `KSA.Mod`; Tomlet ignores them.[current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L26-L89)
The stock `Core` file is the fullest example of the content keys.[current/Content/Core/mod.toml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/mod.toml)

## Reference: the `[StarMap]` table

| Key | Type | Default | Effect |
|---|---|---|---|
| `EntryAssembly` | string | the manifest id (folder name) | Assembly file name without `.dll`, loaded from the mod folder; a missing file skips the mod silently |
| `ModDependencies` | array of tables | none | Each entry: `ModId` (the dependency's folder name), `Optional` (default `false`), `ImportedAssemblies` (default empty) |
| `ExportedAssemblies` | string[] | none | **Parsed but never acted on at the pinned StarMap commit** |

The model is three small classes:[StarMap.Core/Config/StarMapConfig.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Config/StarMapConfig.cs#L7-L25)

```csharp
// StarMap.Core/Config/StarMapConfig.cs L7-L25
internal class RootConfig 
{ 
    public required StarMapConfig StarMap { get; set; }
}


internal class StarMapConfig
{
    public required string EntryAssembly { get; set; }
    public List<string> ExportedAssemblies { get; set; } = [];
    public List<StarMapModDependency> ModDependencies { get; set; } = [];
}

internal class StarMapModDependency
{
    public required string ModId { get; set; }
    public bool Optional { get; set; } = false;
    public List<string> ImportedAssemblies { get; set; } = [];
}
```

`EntryAssembly` is declared `required`, so a `[StarMap]` table **must** include it: the default applies only
when the whole table is missing. Tomlet does not enforce C# `required`, so a table without the key leaves it
null, StarMap looks for `<mod folder>/.dll` and skips the mod silently (inferred from the code). The loader never copies `Config.ExportedAssemblies` to `RuntimeMod.ExportedAssemblies`
(the property `CalculateUseableAssemblies` reads), so exports have no effect today. The API README still
describes them as active; trust the code, list `ImportedAssemblies` on the importing side
([Share types and design an inter-mod API between StarMap mods](../../dependencies/share-types-between-mods/overview.md)).[StarMap.API/README.md](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/README.md)

## Complete example

| File | Shape |
|---|---|
| [mod.toml](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/mod.toml), [packaging/mod.toml](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/packaging/mod.toml) | No `[StarMap]` table; folder name carries the id |
| [KSAero/mod.toml](https://github.com/cairn5/KSAero/blob/3e7e26c9c694a563048261bd6e426e817377d404/KSAero/mod.toml) | Explicit `EntryAssembly` |
| [AdvancedFlightComputer/mod.toml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/mod.toml) | Optional dependency, KittenExtensions `patches` key |
| [gatOS.GameMod/mod.toml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/mod.toml) | Optional dependency with `ImportedAssemblies` |
| [purrTTY.GameMod/mod.toml](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/mod.toml) | `ExportedAssemblies` (documentation only today) |
| [src/KSAGolf/mod.toml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/mod.toml) | Content `assets` plus `[StarMap]` in one file |

## Variations and gotchas

- **`ModId` must equal the dependency's folder name.** The API README says it must match the dependency's
  `name`; code compares against the manifest id only.
- **`EntryAssembly` is case-sensitive on Linux and must equal the csproj `AssemblyName`.**
- **A TOML syntax error** in `mod.toml` makes Tomlet throw in StarMap's `TryCreateMod` or KSA's `PrepareAll`;
  neither call site wraps the parse in a `try` (inferred from the code), so validate generated files.
- **Trailing newline is not required** (KER ships without one).
- **Unknown keys are silent**; typos in `assets`/`systems` produce a mod that loads nothing. Check the spelling
  against the table.
- **KittenExtensions adds keys** (`patches`) that only it reads.

## See also

- [How StarMap launches KSA and loads your mod](../how-starmap-launches-ksa/overview.md), [Declare a dependency on another StarMap mod, required or optional](../../dependencies/declare-a-mod-dependency/overview.md), [Ship assets and code in one mod folder and find it at runtime](../ship-content-and-code-in-one-mod/overview.md),
  [Generate mod.toml and StarMap dependencies from the project file](../../build/generate-mod-toml-from-msbuild/overview.md)
- [Discover code mods through KSA's mod manifest](../../../../mods/starmap/features/manifest-mod-discovery.md), [Order mod loading with a ModDependencies graph](../../../../mods/starmap/features/mod-dependency-graph.md),
  [Minimal mod.toml, post-build deploy and release zip](../../../../mods/kitten-engineer-redux/features/dist-zip-and-post-build-deploy.md), [Mod root and packaging](../../../../mods/ohmystars/features/mod-root-and-packaging.md)
