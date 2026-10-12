---
type: Game Feature
title: Understand when and in which order KSA loads mod XML
description: The mod.toml keys feed separate Mod loader methods that run in a fixed order inside the Program constructor, which decides where patches, redirects, StarMap hooks and runtime registrations can see the data
category: assets
group: asset-xml
topic: mod-xml-load-order
demonstrated_by:
  - mods/kittenextensions/features/gamedata-document
  - mods/realatmospheres/features/core-xml-entry-redirect
  - mods/ksacars/features/mod-folder-content-and-code-mod
lifecycle: [startup:asset-load, startup:immediate-load, startup:all-mods-loaded]
game_types: [KSA.ModLibrary, KSA.Mod, KSA.ModManifest, KSA.ModEntry, KSA.XmlLoader, KSA.Program]
tags: [reference, mod-toml, load-order, startup, xml]
---

KSA does not read all of a mod's XML in one pass. `assets`, `systems`, `planetMeshes`,
`planetRandomHeightmapCollections`, `fonts` and `starBinaries` each feed a `KSA.Mod` method, and `ModLibrary` calls
those in a fixed order from the `Program` constructor: `PrepareAll`, `PreloadAssetBundles`, `LoadEditorTags`,
`LoadAll`, and later `AttachGameData`. Between them sit the StarMap hooks.

## Engine background

- `ModLibrary.PrepareManifest`, `PrepareAll` (per enabled mod: `MakeUsing`, `RegisterTo`, `PrepareSystems`,
  `LoadSettings`), `PreloadAssetBundles`, `LoadEditorTags`, `LoadAll` and `AttachGameData`
  ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L234-L535), [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1863-L1880)).
- `Mod` loader methods: `LoadAssetBundles`, `LoadEditorTagDefinitions`, `LoadSystems`, `PrepareSystems`, `LoadPlanetMeshes`,
  `LoadPlanetRandomHeightmaps`, `LoadFonts`, `LoadStarBinaries`, each `Path.Combine(DirectoryPath, entry)`
  ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L128-L390)).
- The call sequence in `Program`'s constructor ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L880-L985)).
- Phases: `startup:immediate-load` (`PrepareSystems`), `startup:asset-load` (the rest), `startup:all-mods-loaded`
  (postfix on `LoadAll`). The full startup table is in [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Understand when and in which order KSA loads mod XML](mod-xml-load-order.md) | KittenExtensions, RealAtmospheres, KSACars | Per-phase table from the combined document, the Core redirect and a one-folder content-and-code mod | `startup:asset-load`, `startup:immediate-load`, `startup:all-mods-loaded` | Reference only; patches and redirects have their own topics |

## See also

- [Know which asset XML elements the game accepts and what they load](../asset-xml-element-reference/overview.md), [Patch Core or another mod's XML data without copying files](../../xml-patching/patch-game-xml-data/overview.md), [Redirect the game to patched copies of its XML and coordinate edits between mods](../../xml-patching/redirect-game-xml-loading/overview.md),
  [Choose and use the right StarMap lifecycle hook](../../../getting-started/lifecycle/choose-a-starmap-startup-hook/overview.md), [Ship assets and code in one mod folder and find it at runtime](../../../getting-started/loader/ship-content-and-code-in-one-mod/overview.md), [game-loop-and-lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Combined GameData XML document](../../../../mods/kittenextensions/features/gamedata-document.md), [Core XML entry redirect](../../../../mods/realatmospheres/features/core-xml-entry-redirect.md),
  [One folder as both a content mod and a StarMap code mod](../../../../mods/ksacars/features/mod-folder-content-and-code-mod.md)
