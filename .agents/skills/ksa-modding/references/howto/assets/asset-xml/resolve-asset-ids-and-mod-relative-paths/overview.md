---
type: Game Feature
title: Resolve asset ids and file paths correctly across mods
description: How the global asset registry, first-wins Id registration and Mod.GetPath path resolution behave, with a layout for your own assets and a way to reach another mod's or Core's files
category: assets
group: asset-xml
topic: resolve-asset-ids-and-mod-relative-paths
demonstrated_by:
  - mods/ksacars/features/car-part-assets-glb-pbr
  - mods/ksagolf/features/prop-and-course-asset-xml
  - mods/realatmospheres/features/cloud-layers-in-system-xml
  - mods/realatmospheres/features/core-xml-entry-redirect
lifecycle: [startup:asset-load, startup:starmap-before-main]
game_types:
  - KSA.Mod
  - KSA.FileReference
  - KSA.SerializedId
  - KSA.ModLibrary
  - KSA.MeshAtlasFileReference
  - KSA.MeshReference
tags: [xml, ids, paths, registry, mod-folder]
---

Two rules govern every asset XML a mod ships. A `Path` is joined to the mod folder (the folder containing `mod.toml`),
not to the XML file, and a rooted path escapes the folder. An `Id` is a key in a registry shared by all mods and Core,
and the first registration of a key wins silently. This topic turns those into a layout and naming scheme, and shows
how to point XML at files that belong to someone else.

## Engine background

- `FileReference`: `Path` attribute (`LocalPath`), `ModPath` through `Mod.GetPath`, Id defaults to the absolute path
  when none is given, an empty `Path` makes a reference, a refused registration downgrades to a reference
  ([current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L10-L72)).
- `Mod.GetPath` and `DirectoryPath` ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L100-L125), [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L389-L392)).
- `MeshAtlasFileReference.DoLoad`: Id = glTF mesh name, a duplicate reuses the registered mesh
  ([current/decomp/KSA/MeshAtlasFileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshAtlasFileReference.cs#L26-L50)).
- `ModLibrary.Register` / `Get<T>` / `TryGet<T>` and `SerializedCollection.Register` (first wins)
  ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L575-L776), [current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L20-L33)).
- Phase: `startup:asset-load`, see [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Lay out mod-folder-relative paths and prefixed global asset ids](resolve-asset-ids-and-mod-relative-paths-1.md) | KSACars, KSAGolf | Mod-folder-relative paths, mod-prefixed ids, case-exact names, ids looked up by code | `startup:asset-load` | Simple and portable; ids are global, so the prefix is your only protection |
| [Reach another mod's or Core's files with absolute paths and Id-only references](resolve-asset-ids-and-mod-relative-paths-2.md) | RealAtmospheres | Absolute paths to files outside the mod folder, existence gates, Id-only references to Core assets | `startup:starmap-before-main`, `startup:asset-load` | Reaches anything on disk; machine-specific, so only for generated copies |

## See also

- [Know which asset XML elements the game accepts and what they load](../asset-xml-element-reference/overview.md), [Declare glTF meshes and PBR materials in asset XML and author their texture sets](../../models-and-textures/declare-gltf-and-pbr-material-assets/overview.md), [Redirect the game to patched copies of its XML and coordinate edits between mods](../../xml-patching/redirect-game-xml-loading/overview.md),
  [Ship assets and code in one mod folder and find it at runtime](../../../getting-started/loader/ship-content-and-code-in-one-mod/overview.md), [Understand when and in which order KSA loads mod XML](../mod-xml-load-order/overview.md)
- [Car part assets: GLB atlases, PBR materials and subparts](../../../../mods/ksacars/features/car-part-assets-glb-pbr.md), [Prop and course asset XML](../../../../mods/ksagolf/features/prop-and-course-asset-xml.md), [Cloud layers in the system XML](../../../../mods/realatmospheres/features/cloud-layers-in-system-xml.md)
