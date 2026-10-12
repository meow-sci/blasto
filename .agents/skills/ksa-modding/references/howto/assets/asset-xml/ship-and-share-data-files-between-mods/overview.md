---
type: Game Feature
title: Ship data files with a mod and let other mods contribute more
description: How a code mod packages and finds its own data files, scans other mods for contributions, reads a convention folder inside every installed mod, and writes a new mod folder from the game
category: assets
group: asset-xml
topic: ship-and-share-data-files-between-mods
demonstrated_by:
  - mods/ohmystars/features/mod-root-and-packaging
  - mods/compendium/features/cross-mod-json-catalog
  - mods/ksarmory/features/weapon-packs-from-mod-folders
  - mods/unscience/features/parts-now-paste-xml-mod-writer
lifecycle: [startup:all-mods-loaded, startup:immediate-load, frame:draw-ui]
game_types:
  - KSA.Mod
  - KSA.ModLibrary
  - KSA.ModManifest
  - KSA.ModEntry
  - KSA.Universe
  - KSA.CelestialSystem
  - KSA.PartTemplate
tags: [data-files, packaging, cross-mod, manifest, convention-folder]
---

A mod folder is the unit KSA and StarMap understand: `mod.toml`, the files it lists, and whatever else sits beside them. A code mod
with a lot of data (sky cultures, catalogs, JSON, presets, weapon definitions) has to decide how its files get there, how the running
assembly finds them, and whether other mods may add to them. The game's only mechanisms are the mod folders and the manifest; the rest is
convention.

## Engine background

- Folder lookup: `Content/<id>` then `<Documents>/My Games/Kitten Space Agency/mods/<id>`; `LocalModsFolderPath`,
  `LocalManifestPath`, `Find`, `PrepareManifest`, `AddMods` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L176-L190), [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L234-L371)).
- `Mod.DirectoryPath` and `GetPath` ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L100-L110), [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L389-L392)); `ModManifest.Mods` and the `Save()` that
  writes only `id` and `enabled` ([current/decomp/KSA/ModManifest.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModManifest.cs#L8-L48)); `ModEntry` and its constructors ([current/decomp/KSA/ModEntry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModEntry.cs#L8-L40)).
- Phases: `startup:immediate-load` (per-mod, before systems), `startup:all-mods-loaded` (every mod's assets loaded), `frame:draw-ui` for
  in-game writes. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Ship data beside the DLL with an explicit copy list and resolve the mod root from the assembly](ship-and-share-data-files-between-mods-1.md) | Oh My Stars | Explicit `ModContent` copy list, `mod.toml` without a StarMap table, mod root from `Assembly.Location` | build time, `startup:all-mods-loaded` | Simple and robust; only your own data |
| [Let other mods add data with a marker key in JSON files scanned across mod folders](ship-and-share-data-files-between-mods-2.md) | Compendium | Marker key in JSON files found by scanning every mod folder; own files first, others override | `startup:immediate-load`, `frame:draw-ui` | Zero coupling; scans everything, disabled mods contribute |
| [Read a convention folder inside every installed mod with a strict reader](ship-and-share-data-files-between-mods-3.md) | KSArmory | A `KSArmory/` convention folder in any manifest mod, strict reader, namespaced keys, post-load audit | `startup:all-mods-loaded` | Respects enabled state and refuses typos; needs a versioned schema |
| [Write a new content mod folder and manifest entry from inside the game](ship-and-share-data-files-between-mods-4.md) | unscience (parts-now) | ModFolderWriter with ModIdValidator, atomic writes and an enabled manifest stamp | `frame:draw-ui` | Creates mods from inside the game; the game's own manifest save drops extra keys |

## See also

- [Ship assets and code in one mod folder and find it at runtime](../../../getting-started/loader/ship-content-and-code-in-one-mod/overview.md), [Deploy a mod on every build and debug it from an IDE](../../../getting-started/build/deploy-a-mod-on-every-build/overview.md), [Package and publish a mod release (zip, CI publishing, installer)](../../../getting-started/build/package-and-publish-a-mod-release/overview.md),
  [Declare a dependency on another StarMap mod, required or optional](../../../getting-started/dependencies/declare-a-mod-dependency/overview.md), [Cache and key per-body data so it survives system reloads, repeated ids and extra star systems](../../../celestials/systems-and-stars/cache-and-key-body-data-per-loaded-system/overview.md), [Load, validate and unload part mods in a running game](../../../vehicles-and-parts/parts/load-and-unload-part-mods-at-runtime/overview.md),
  [How StarMap launches KSA and loads your mod](../../../getting-started/loader/how-starmap-launches-ksa/overview.md), [Understand when and in which order KSA loads mod XML](../mod-xml-load-order/overview.md), [Validate asset XML ids and paths before launching the game (offline)](../../validation/validate-asset-xml-references/overview.md)
- [Mod root and packaging](../../../../mods/ohmystars/features/mod-root-and-packaging.md), [Cross-mod JSON catalog](../../../../mods/compendium/features/cross-mod-json-catalog.md), [Asset-only weapon packs read from a KSArmory folder in every installed mod](../../../../mods/ksarmory/features/weapon-packs-from-mod-folders.md),
  [Turn pasted Part XML into a real, enabled KSA mod folder from inside the game](../../../../mods/unscience/features/parts-now-paste-xml-mod-writer.md)
