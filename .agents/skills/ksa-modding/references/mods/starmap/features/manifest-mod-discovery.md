---
type: Mod Feature
title: Discover code mods through KSA's mod manifest
description: StarMap reuses KSA's native manifest.toml and mod.toml discovery, reads an optional [StarMap] table from each enabled mod, and loads that mod's entry assembly, so a StarMap mod is a KSA content mod plus a DLL
mod: starmap
category: getting-started
techniques: [toml-config, assembly-load-context]
hooks: [call KSA.ModLibrary.PrepareManifest]
lifecycle: [startup:starmap-before-main, startup:asset-load]
game_types: [KSA.ModLibrary, KSA.Mod, KSA.ModEntry, KSA.ModManifest, KSA.Constants]
tags: [starmap, mod-toml, manifest, content-mod, discovery, tomlet]
---

KSA has its own data-only mod system: folders with a `mod.toml`, listed and toggled in a
`manifest.toml`, whose XML assets, systems and fonts the game loads at startup. StarMap does not
invent a second registry. It asks KSA's `ModLibrary` for the manifest before the game starts,
walks the enabled entries in manifest order, and for each one reads an extra `[StarMap]` table
from the same `mod.toml` to find a DLL. A StarMap mod is therefore a KSA content mod plus an
entry assembly; KSA loads its content, StarMap loads its code. KSACars puts it in one line at the
top of its `mod.toml`: "KSA reads this as a content mod, StarMap reads it as a code mod. One
folder does both."[^ksacars-modtoml]

## How it works

### KSA's native mod system

| Piece | Where | Behavior |
|---|---|---|
| Documents folder | `Constants.DocumentsFolderPath` = `<Personal>/My Games/Kitten Space Agency` | Root for saves, settings, logs, the manifest and local mods[^ksa-constants] |
| Local mods folder | `ModLibrary.LocalModsFolderPath` = `<Documents>/mods` | Player-installed mods, one folder per mod |
| Install mods | `<KSA install>/Content/<id>/mod.toml` | Stock content (`Core`) and anything copied into `Content` |
| Manifest | `ModLibrary.LocalManifestPath` = `<Documents>/manifest.toml`, seeded from `Content/manifest.toml` on first run | `[[mods]]` entries with `id` and `enabled`; order of entries is load order[^ksa-modmanifest] |

`ModLibrary.PrepareManifest` (cached by a `_manifestPrepared` flag) reads the local manifest,
merges entries from `Content/manifest.toml`, marks those as `Core`, then scans `Content/` and the
local mods folder for subfolders that contain a `mod.toml`. A folder not yet in the manifest is
appended as a new `ModEntry` with `Enabled = false` and `New = true`. Non-core entries whose
`mod.toml` no longer exists are removed and the manifest is saved.[^ksa-modlibrary][^ksa-modentry]

The manifest id is the folder name. `Mod.MakeUsing(modEntry.Id, path)` deserializes `mod.toml`
with Tomlet into a `KSA.Mod`, sets `Id` to the folder name and `DirectoryPath` to the folder; the
`name` key only fills `Mod.Name`.[^ksa-mod] Keys KSA reads from `mod.toml` (all `[TomlField]`s on
`KSA.Mod`):

| Key | Used by |
|---|---|
| `name` | Display name (`Mod.Name`) |
| `assets` | `Mod.LoadAssetBundles`: XML asset bundles (parts, sounds, textures, ...) |
| `systems` | `Mod.PrepareSystems` (system picker entries) and `Mod.LoadSystems` (system templates) |
| `fonts` | `Mod.LoadFonts` -> `FontManager.RegisterFont` |
| `planetMeshes`, `planetRandomHeightmapCollections` | `LoadPlanetMeshes`, `LoadPlanetRandomHeightmaps` |
| `starBinaries` | Star catalog binaries (`GetStarCount`, `LoadStarBinaries`) |
| `textureSizeCaps`, `supportedShadowMaps`, `supportedThumbnailSizes`, `simulationSpeeds` | `Mod.LoadSettings` registers settings options and sim speeds |
| `supportedCloudQuality`, `supportedOceanSimQuality`, `supportedExhaustQuality`, `supportedKittenFurQuality`, `supportedShadowSlots` | Declared on `KSA.Mod`; no other reader in the decompiled `KSA` assembly of this build |
| `[console]` with `onBoot` / `onLoad` string lists | Console commands collected into `ModLibrary.OnBoot` / `OnLoad` during `LoadAll` |

`patches` is not a KSA key: it is read by KittenExtensions' own `mod.toml` model for its XML
patching.[^kx-patcher] `version`, `author`, `description` and the `[StarMap]` table have no field
on `KSA.Mod` either; reference mods ship them alongside native keys without issue.

KSA then loads mods in three passes from the `Program` constructor: `PrepareAll` (per enabled
mod: `MakeUsing`, register, `PrepareSystems`, `LoadSettings`), `PreloadAssetBundles`, and
`LoadAll` (fonts, asset bundles, systems, planet meshes, console commands, then all registered
`ILoader`s in parallel).[^ksa-program]

### What StarMap adds

`ModLoader.PrepareMods` runs before KSA's `Main` and calls `ModLibrary.PrepareManifest()`
itself. Because that method caches its result, the `ModLibrary.Manifest` object StarMap reads is
the same one the game uses later. For each manifest entry, in order:[^sm-modloader]

1. Skip it if `Enabled` is false (console: "Not loading mod: X because it is disabled in
   manifest").
2. `RuntimeMod.TryCreateMod` looks for `Content/<id>/mod.toml`, then
   `<LocalModsFolderPath>/<id>/mod.toml`; no file means skip.[^sm-runtimemod]
3. Parse the file with Tomlet into `RootConfig { StarMap }`. If there is no `[StarMap]` table,
   use `EntryAssembly = <id>`.[^sm-config]
4. If `<mod folder>/<EntryAssembly>.dll` does not exist, skip silently: the entry is a
   content-only mod and KSA alone handles it.
5. Create a `ModAssemblyLoadContext` for the mod and load the entry assembly by name.
6. Register the `RuntimeMod`, resolve `ModDependencies`, and either initialize it (find the
   `[StarMapMod]` class, bind hooks, call `StarMapBeforeMain`) or park it until its dependencies
   load.

The `[StarMap]` table (`RootConfig.StarMap` of type `StarMapConfig`):

| Key | Type | Default | Meaning |
|---|---|---|---|
| `EntryAssembly` | string | folder name | Assembly file name without `.dll`, loaded from the mod folder |
| `ExportedAssemblies` | string[] | `[]` | Intended: assemblies other mods may resolve from this mod's load context. Parsed into `StarMapConfig` but never copied to `RuntimeMod.ExportedAssemblies`, so it has no effect at the pinned commit (see [shared contract assemblies](shared-contract-assemblies.md)) |
| `ModDependencies` | array of tables | `[]` | `ModId`, `Optional` (default false), `ImportedAssemblies` |

OhMyStars is an example of the fallback: its `mod.toml` has only `name`, `version` and `author`,
its folder and assembly are both `OhMyStars`, so StarMap loads `OhMyStars.dll` without a
`[StarMap]` table.[^ohmystars-modtoml][^ohmystars-modpaths]

## Lifecycle and timing

- Discovery and assembly loading are `startup:starmap-before-main`: they run inside
  `StarMapCore.Init`, before `Program.Main`, on the main thread.
- KSA's own reading of the same folders happens later, inside the `Program` constructor
  (`startup:asset-load`): `ModLibrary.PrepareAll` at
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899)
  and `ModLibrary.LoadAll` at line 942.
- StarMap calls `PrepareManifest` before `ModLibrary.OnApplicationStart` (which creates the
  Documents and mods folders) has run, because that call sits in the `Program` constructor.

## Key code

| Path | Role |
|---|---|
| [StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L46-L104) | `Init` and `PrepareMods`: manifest walk, create, register, initialize |
| [StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L36-L76) | `TryCreateMod`: find `mod.toml`, parse `[StarMap]`, load entry assembly |
| [StarMap.Core/Config/StarMapConfig.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Config/StarMapConfig.cs#L7-L25) | TOML model of the `[StarMap]` table |
| [StarMap.API/README.md](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.API/README.md#L9-L25) | Upstream description of the `mod.toml` contract |

## Snippets

```csharp
// StarMap.Core/ModRepository/ModLoader.cs L52-L86
private void PrepareMods()
{
    var loadedManifest = ModLibrary.PrepareManifest();

    if (!loadedManifest) return;

    var mods = ModLibrary.Manifest.Mods;
    if (mods is null) return;

    foreach (var mod in mods)
    {
        if (!mod.Enabled)
        {
            Console.WriteLine($"StarMap - Not loading mod: {mod.Id} because it is disabled in manifest");
            continue;
        }

        if (!RuntimeMod.TryCreateMod(mod, _coreAssemblyLoadContext, out var runtimeMod))
            continue;

        ModRegistry.Add(runtimeMod);

        if (!runtimeMod.AllDependenciesLoaded(ModRegistry))
        {
            Console.WriteLine($"StarMap - Delaying load of mod: {runtimeMod.ModId} due to missing dependencies: {string.Join(", ", runtimeMod.NotLoadedModDependencies.Keys)}");
            continue;
        }

        if (!runtimeMod.InitializeMod(ModRegistry))
        {
            Console.WriteLine($"StarMap - Failed to initialize mod: {runtimeMod.ModId} from manifest");
            continue;
        }

        Console.WriteLine($"StarMap - Loaded mod: {runtimeMod.ModId} from manifest");
```

```csharp
// StarMap.Core/ModRepository/RuntimeMod.cs L40-L63
var modPath = Path.Combine(_rootContentPath, manifestEntry.Id);
var modTomlPath = Path.Combine(modPath, "mod.toml");
if (!File.Exists(modTomlPath))
{
    modPath = Path.Combine(ModLibrary.LocalModsFolderPath, manifestEntry.Id);
    modTomlPath = Path.Combine(modPath, "mod.toml");
    if (!File.Exists(modTomlPath)) return false;
}
var tomlConfig = TomletMain.To<RootConfig>(File.ReadAllText(modTomlPath));
if (tomlConfig?.StarMap is not StarMapConfig starMapConfig)
{
    starMapConfig = new StarMapConfig
    {
        EntryAssembly = manifestEntry.Id,
    };
}

var modAssemblyFile = Path.Combine(modPath, $"{starMapConfig.EntryAssembly}.dll");
var assemblyExists = File.Exists(modAssemblyFile);

if (!assemblyExists) return false;

var modLoadContext = new ModAssemblyLoadContext(manifestEntry.Id, modAssemblyFile, coreALC);
var modAssembly = modLoadContext.LoadFromAssemblyName(new AssemblyName() { Name = starMapConfig.EntryAssembly });
```

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

## Game APIs touched

| Member | File | Use |
|---|---|---|
| `ModLibrary.PrepareManifest` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L234-L326) | Builds and caches `ModLibrary.Manifest`; called by StarMap before KSA's `Main` |
| `ModLibrary.Manifest` / `ModManifest.Mods` | [current/decomp/KSA/ModManifest.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModManifest.cs#L8-L54) | Ordered `ModEntry` list StarMap iterates |
| `ModEntry.Id` / `Enabled` / `New` | [current/decomp/KSA/ModEntry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModEntry.cs#L6-L55) | Folder id, toggle, and the "found since last start" flag |
| `ModLibrary.LocalModsFolderPath` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L178-L180) | Second search location for `mod.toml` |
| `Mod` TOML fields, `Mod.MakeUsing`, `Mod.PrepareSystems` | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L26-L111) | KSA's view of the same `mod.toml` |
| `ModLibrary.PrepareAll` / `LoadAll` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L522) | KSA's content passes that StarMap later hooks |

## Pitfalls and version notes

- **The id is the folder name.** StarMap keys everything (registry, `ModDependencies.ModId`,
  the `StarMapImmediateLoad` lookup) by the manifest id, which is the folder name, not the `name`
  key. The API README says `ModId` must match the dependency's `name` and also says `name` should
  equal the folder name; only the folder name actually matters in code.[^sm-api-readme]
- **Content wins over Documents.** Both KSA and StarMap check `Content/<id>` before the local mods
  folder, so a stale copy in the install folder shadows a newer one in Documents.
- **New mods start disabled.** A folder found for the first time gets `Enabled = false`,
  `New = true`, so StarMap skips its code on that launch even if the player enables it in KSA's
  "MOD FOUND" popup; see [the restart flow](new-mods-restart-flow.md).
- **Only the entry assembly is scanned** for the `[StarMapMod]` class. Other DLLs in the folder
  load on demand through the mod's load context.
- **Silent skips.** A missing `mod.toml` or entry DLL produces no StarMap message; check that the
  DLL name matches `EntryAssembly` exactly.
- **Two install locations.** The StarMap README lists both `Documents/My Games/Kitten Space
  Agency/mods/<ModName>/` (recommended, survives game updates) and `<KSA Installation>/Content/<ModName>/`.[^sm-readme]

## See also

- [Host KSA in-process from a launcher executable](host-process-launch.md)
- [Isolate each mod in its own AssemblyLoadContext](per-mod-assembly-load-context.md)
- [Order mod loading with a ModDependencies graph](mod-dependency-graph.md)
- [Discover lifecycle hooks by attribute type name](attribute-hook-discovery.md)
- [Build a minimal StarMap mod by hand from the discovery rules](../../../howto/getting-started/loader/create-a-minimal-starmap-mod/create-a-minimal-starmap-mod-1.md) - how-to article demonstrating this feature
- [Follow the boot sequence: host process, load contexts and hook binding](../../../howto/getting-started/loader/how-starmap-launches-ksa/how-starmap-launches-ksa-1.md) - how-to article demonstrating this feature
- [Understand the mod folder, manifest and enable/ordering rules](../../../howto/getting-started/loader/how-starmap-launches-ksa/how-starmap-launches-ksa-2.md) - how-to article demonstrating this feature
- [Write mod.toml: KSA keys and the StarMap table](../../../howto/getting-started/loader/mod-toml-reference/mod-toml-reference.md) - how-to article demonstrating this feature

[^sm-modloader]: StarMap.Core/ModRepository/ModLoader.cs
[^sm-runtimemod]: StarMap.Core/ModRepository/RuntimeMod.cs
[^sm-config]: StarMap.Core/Config/StarMapConfig.cs
[^sm-api-readme]: StarMap.API README
[^sm-readme]: StarMap README
[^kx-patcher]: KittenExtensions/Patch/Patcher.cs
[^ksacars-modtoml]: KSACars src/KSACars/mod.toml
[^ohmystars-modtoml]: OhMyStars packaging/mod.toml
[^ohmystars-modpaths]: OhMyStars src/ModPaths.cs
[^ksa-modlibrary]: KSA.ModLibrary (decompiled)
[^ksa-mod]: KSA.Mod (decompiled)
[^ksa-modentry]: KSA.ModEntry (decompiled)
[^ksa-modmanifest]: KSA.ModManifest (decompiled)
[^ksa-constants]: KSA.Constants (decompiled)
[^ksa-program]: KSA.Program (decompiled)
