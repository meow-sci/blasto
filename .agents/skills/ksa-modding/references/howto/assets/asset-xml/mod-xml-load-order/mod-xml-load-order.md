---
type: How-To
title: Find out when KSA reads each mod.toml key and where hooks can see the data
description: Per-phase table of the mod.toml keys, the Mod loader each feeds, the ModLibrary call that runs it inside the Program constructor, and the StarMap hook or patch point that sees the result
category: assets
group: asset-xml
topic: mod-xml-load-order
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/kittenextensions/features/gamedata-document
  - mods/realatmospheres/features/core-xml-entry-redirect
  - mods/ksacars/features/mod-folder-content-and-code-mod
techniques: [xml-asset, toml-config, starmap-lifecycle]
hooks: [starmap StarMapImmediateLoad, starmap StarMapAllModsLoaded, harmony-prefix KSA.ModLibrary.PrepareAll]
lifecycle: [startup:asset-load, startup:immediate-load, startup:all-mods-loaded]
game_types: [KSA.ModLibrary, KSA.Mod, KSA.ModManifest, KSA.ModEntry, KSA.XmlLoader, KSA.Program]
tags: [reference, mod-toml, load-order, startup, xml, assets, systems, planet-meshes]
---

A mod's `mod.toml` lists files under a handful of keys. Each key feeds a separate loader method on `KSA.Mod`, and
those methods run at different points of the `Program` constructor. Knowing the order tells you where an XML patch,
a redirect, a StarMap hook or a runtime registration can see the data it needs, and why some mistakes fail silently.
This is the data-side companion of [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md); read that for the surrounding startup sequence (process start, window,
renderer, `AttachGameData`).

Demonstrated by [Combined GameData XML document](../../../../mods/kittenextensions/features/gamedata-document.md) (patching before any loader runs),
[Core XML entry redirect](../../../../mods/realatmospheres/features/core-xml-entry-redirect.md) (swapping list entries before the loaders read them) and
[One folder as both a content mod and a StarMap code mod](../../../../mods/ksacars/features/mod-folder-content-and-code-mod.md) (one folder that is both a content mod and a StarMap code mod).

## Background: how the game does it

`Mod` declares the keys the game reads, as `[TomlField]` attributes. Everything else in `mod.toml` is ignored by the
game (StarMap reads its own `[StarMap]` table; KittenExtensions reads `patches`):

```csharp
// current/decomp/KSA/Mod.cs L56-L72
	[TomlField("planetMeshes")]
	private string[] PlanetMeshCollections = Array.Empty<string>();

	[TomlField("planetRandomHeightmapCollections")]
	private string[] PlanetRandomHeightmapCollections = Array.Empty<string>();

	[TomlField("systems")]
	public string[] SystemTemplates = Array.Empty<string>();

	[TomlField("assets")]
	private string[] Assets = Array.Empty<string>();

	[TomlField("starBinaries")]
	private string[] StarBinaries = Array.Empty<string>();

	[TomlField("fonts")]
	private string[] Fonts = Array.Empty<string>();
```

Core's manifest uses every one of them:

```toml
# current/Content/Core/mod.toml L14-L21
# these xml definitions are loaded for planet LODs
planetMeshes = [ "MeshCollections/DefaultMeshCollection.xml", "MeshCollections/EarthScaleMesh.xml", "MeshCollections/OceanMeshes.xml", "MeshCollections/AsteroidScaleMesh.xml", "MeshCollections/MarsScaleMesh.xml"]

# used for random heightmaps
planetRandomHeightmapCollections = ["TextureCollections/DefaultHeightmapCollection.xml"]

# these xml definitions are loaded for celestial system templates
systems = [ "SolSystem.xml", "SolSystemDense.xml", "EarthSystem.xml" , "EarthOnly.xml", "SolSystemInterstellar.xml", "EarthOnlyInterstellar.xml" ]
```

The `Program` constructor calls the library in this order (the first six lines after the new-mods dialog):

```csharp
// current/decomp/KSA/Program.cs L899-L901
		ModLibrary.PrepareAll();
		ModLibrary.PreloadAssetBundles();
		ModLibrary.PreloadLanguages();
```

```csharp
// current/decomp/KSA/Program.cs L941-L943
		ModLibrary.LoadEditorTags();
		ModLibrary.LoadAll();
		ModLibrary.AssignDefaults();
```

Every loader builds `Path.Combine(DirectoryPath, entry)` and hands it to `XmlLoader.Load<T>`; mods are visited in
`ModLibrary.Lookup` order, which is the order `PrepareAll` registered them: manifest order, Core first.

## Lifecycle and timing

The phases, in the order they run on the main thread. "Preload" is `Mod.Preload == true`, set by
`PreloadAssetBundles` and cleared by `LoadAll`.

| # | Call | Reads (mod.toml key) | What it does | StarMap hook or patch point | Lifecycle |
|---|---|---|---|---|---|
| 0 | KittenExtensions prefix on `PrepareAll` | `planetMeshes`, `systems`, `assets`, `patches` (its own parse) | Builds the combined document and runs XPath patches | Registered in `StarMapBeforeMain`; runs before StarMap's own prefix on `PrepareAll` | `startup:asset-load` |
| 1 | `ModLibrary.PrepareAll` -> `Mod.PrepareSystems` per enabled mod | `systems` | Deserializes each file as `SystemInfo` (system picker headers); stores the entry as `XmlName`; then `Mod.LoadSettings` registers settings tables | `StarMapImmediateLoad(Mod)` is a prefix on `Mod.PrepareSystems`, once per mod in manifest order | `startup:immediate-load` |
| 2 | `ModLibrary.PreloadAssetBundles` | `assets`, `systems` | `Preload = true`: `LoadAssetBundles` (only celestials, vehicles and situations get `OnDataLoad`) and `LoadSystems` (every system file) | none | `startup:asset-load` |
| 3 | `ModLibrary.PreloadLanguages` | `assets` (`LanguageFile`) | Loads language files early | none | `startup:asset-load` |
| 4 | System picker or last-used system | | Sets `SystemLibrary.Default` | none | |
| 5 | `ModLibrary.LoadEditorTags` | `assets` | Re-deserializes every `assets` file to register `EditorTagDef` entries before the editor reads them | none | `startup:asset-load` |
| 6 | `ModLibrary.LoadAll` | per mod: `fonts`, `assets`, `systems` (selected only), `planetMeshes`, `planetRandomHeightmapCollections` | `Preload = false`; clears each mod's bundles and `SystemLibrary.Lookup`; loads in that order; collects `[console]` command lists | postfix: `StarMapAllModsLoaded` | `startup:asset-load`, then `startup:all-mods-loaded` |
| 7 | Same call, after the loop | | Every queued `ILoader` (`FileReference.Load`: GLB, textures, shaders, sounds) runs in parallel on thread-pool threads, then `OnDataLoaded` | none | `startup:asset-load` |
| 8 | `ModLibrary.Bind(renderer)`, `PopulateMeshCollections`, `AttachGameData` | | GPU upload of textures and meshes; planet mesh collections; part game data merged into templates | none | later in the constructor |
| 9 | Star renderer init | `starBinaries` | `GetStarCount` and `PopulateStars` read the binary files | none | |

Sources: [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L535), [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L128-L180), [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L231-L284),
[current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L287-L312), [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L338-L390), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899-L985),
[StarMap.Core/Patches/ModPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModPatches.cs#L11-L22).

## Steps

1. **List every XML file explicitly.** There is no globbing: a file not named in `assets`, `systems` or
   `planetMeshes` is never read. KSACars says so in its own `mod.toml`:

   ```toml
   # src/KSACars/mod.toml L1-L19
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

2. **Pick the key by what the file contains.** `assets` holds `<Assets>` bundles (parts, materials, sounds, bodies);
   `systems` holds `<System>` templates (read twice: headers at step 1, templates at step 2/6); `planetMeshes` holds
   mesh collections for planet levels of detail; `planetRandomHeightmapCollections` holds random heightmap
   collections; `fonts` lists `.ttf` files for the font picker; `starBinaries` lists star catalogue `.bin` files.

3. **Choose the hook by what must already exist.**

   | You need to... | Do it at | Why |
   |---|---|---|
   | Change another file's XML | A `ModLibrary.PrepareAll` prefix registered in `StarMapBeforeMain` (KittenExtensions patches, [Patch Core or another mod's XML data without copying files](../../xml-patching/patch-game-xml-data/overview.md)) | Before any `Mod` exists |
   | Replace a file entirely | `StarMapBeforeMain` plus prefixes on the `Mod` loaders ([Redirect the game to patched copies of its XML and coordinate edits between mods](../../xml-patching/redirect-game-xml-loading/overview.md)) | Before `Mod.PrepareSystems` first reads `SystemTemplates` |
   | Register code an XML attribute names by type (an enum, a custom `KxAsset` class) | `StarMapImmediateLoad` or earlier | Gauge XML binds by name during `LoadAll` |
   | Read a mod's own loaded assets | `StarMapAllModsLoaded` | Postfix on `LoadAll`; bundles are loaded but `AttachGameData` has not run |
   | Read merged part game data | After `AttachGameData` (first frame or `startup:game-loaded`) | Game data is merged by `AttachGameData` |
   | Register assets from code | Before `LoadAll` ends, or accept that GPU binding (`ModLibrary.Bind`) happens later | `FileReference.OnDataLoad` queues a loader only when not preloading |

   See [Choose and use the right StarMap lifecycle hook](../../../getting-started/lifecycle/choose-a-starmap-startup-hook/overview.md) for the hook signatures.

4. **Account for the parallel file pass.** XML is read serially, per mod, but the files it names (GLB, PNG, KTX2,
   WAV, GLSL) are loaded afterwards in `Parallel.ForEachAsync` on thread-pool threads
   ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L508-L520)). A bad path therefore errors at step 7, with a log line such as
   "mod file ... not found at ...", not at the point the XML was read; patches to the loader path must be thread-safe.

5. **Remember what is not reported.** A missing XML file is not an error: `XmlLoader.Deserialize` returns null when
   `File.Exists` is false and `Load` returns null without logging ([current/decomp/KSA/XmlLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlLoader.cs#L1-L52)). A malformed XML
   file logs the exception and is skipped, so its assets simply do not exist. A missing font or star binary logs an
   error ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L287-L312)).

## Complete example

- [src/KSACars/mod.toml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/mod.toml) (one folder, both a content mod and a code mod)
- [current/Content/Core/mod.toml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/mod.toml) (every key)
- [KittenExtensions/Patches.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patches.cs#L13-L28) (the earliest hook on the sequence)

## Variations and gotchas

- **Disabled mods and the document.** Disabled mods are skipped by `PrepareAll`, so they never get a `Mod` object, but
  KittenExtensions still imports their XML into its document ([Patch Core or another mod's XML data without copying files](../../xml-patching/patch-game-xml-data/overview.md)).
- **Preload sees fewer assets.** Between steps 2 and 5 only celestials, vehicles and situations have run `OnDataLoad`;
  do not look up a part or material there.
- **Order is manifest order.** `ModLibrary.Lookup` iterates in registration order, and registration is first-wins
  ([current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L20-L33)), so an earlier mod's id wins a collision, and Core always comes first.
- **`systems` is read twice.** Headers at step 1 (so the system picker can list them), the full template at step 6 only
  for the selected system, but in preload for all of them.
- **StarMap vs KSA keys.** The `[StarMap]` table, `patches` and any custom keys are invisible to the game; see
  [Ship assets and code in one mod folder and find it at runtime](../../../getting-started/loader/ship-content-and-code-in-one-mod/overview.md).

## See also

- [Know which asset XML elements the game accepts and what they load](../asset-xml-element-reference/overview.md), [Patch Core or another mod's XML data without copying files](../../xml-patching/patch-game-xml-data/overview.md), [Redirect the game to patched copies of its XML and coordinate edits between mods](../../xml-patching/redirect-game-xml-loading/overview.md),
  [Choose and use the right StarMap lifecycle hook](../../../getting-started/lifecycle/choose-a-starmap-startup-hook/overview.md), [Ship assets and code in one mod folder and find it at runtime](../../../getting-started/loader/ship-content-and-code-in-one-mod/overview.md), [game-loop-and-lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Combined GameData XML document](../../../../mods/kittenextensions/features/gamedata-document.md), [Core XML entry redirect](../../../../mods/realatmospheres/features/core-xml-entry-redirect.md),
  [One folder as both a content mod and a StarMap code mod](../../../../mods/ksacars/features/mod-folder-content-and-code-mod.md)
