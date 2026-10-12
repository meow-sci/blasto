---
type: Game Feature
title: Export or generate a GLB that KSA loads correctly
description: The glTF export contract KSA reads (Y-up off, part space, transforms applied, node name equal to mesh name, a _VM twin, pivot-centred moving bodies) and four ways mods meet it
category: assets
group: models-and-textures
topic: export-glb-from-blender-for-ksa
demonstrated_by:
  - mods/ksacars/features/art-and-audio-pipeline-tooling
  - mods/ksacars/features/car-part-assets-glb-pbr
  - mods/ksagolf/features/glb-authoring-and-mesh-checks
  - mods/ksarmory/features/blender-model-pipeline-and-checks
  - mods/ksastructures/features/generated-deep-ramp
lifecycle: [startup:asset-load]
game_types:
  - KSA.MeshAtlasFileReference
  - KSA.MeshReference
  - KSA.Gltf2Reference
  - Brutal.GltfApi.GltfLoader
  - KSA.MeshViewModule
  - KSA.PartModelModule
  - KSA.StaticObjectTemplate
  - KSA.StaticSubObjectTemplate
tags: [glb, blender, export, contract, generation]
---

KSA reads meshes out of a glTF binary by name. It does not read the node graph, so a transform left on a node is silently dropped, and a body modelled in the wrong frame or off its pivot is wrong in game in ways nothing reports. The export has to follow a contract, and
Core's own assets are generated from GLBs that follow it.

## Engine background

- `GltfLoader` (`Brutal.GltfApi`) parses the GLB; `MeshAtlasFileReference.DoLoad` registers a `MeshReference` per glTF mesh with `Id` = mesh name, ignoring nodes ([current/decomp/Brutal.GltfApi/GltfLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.GltfApi/GltfLoader.cs#L1-L35), [current/decomp/KSA/MeshAtlasFileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshAtlasFileReference.cs#L26-L45)).
- `KSA.GlbImport.PartAssetBundler`: the game's GLB-to-XML generator; a node not starting with `_` and not ending in `_VM` becomes a subpart, `<name>_VM` its `MeshView`, `_glass`, `_raytrace` and `_Col*` children set flags, a non-identity node transform produces a warning
  ([current/decomp/KSA.GlbImport/PartAssetBundler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.GlbImport/PartAssetBundler.cs#L44-L62), [current/decomp/KSA.GlbImport/PartAssetBundler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.GlbImport/PartAssetBundler.cs#L273-L345)).
- `StaticObjectTemplate` and `StaticSubObjectTemplate` for non-part world objects ([current/decomp/KSA/StaticObjectTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObjectTemplate.cs#L7-L28)).
- Phase: `startup:asset-load`. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Export a part-space GLB with pivot-centred subparts and preview it headless](export-glb-from-blender-for-ksa-1.md) | KSACars | Part-space export from Blender, pivot-centred subparts, `_VM` twins, headless preview of the export | `startup:asset-load` | Full Blender workflow; source `.blend` is outside the repo |
| [Verify the export contract against Core and keep a human sign-off before unwrapping](export-glb-from-blender-for-ksa-2.md) | KSAGolf | Contract verified against Core, human geometry sign-off before unwrapping, units by drawer | `startup:asset-load` | Cheap human gate; props need centimetres and baked scale |
| [Reframe hand-modelled assets and generate hard-surface parts with headless Blender](export-glb-from-blender-for-ksa-3.md) | KSArmory | Headless Blender: reframe authored models, generate hard-surface parts from primitives, report muzzles | `startup:asset-load` | Reproducible and diffable; Blender as a Windows binary under WSL |
| [Write the GLB, textures, collider XML and C# table from one Python script (no Blender)](export-glb-from-blender-for-ksa-4.md) | KSAStructures | One Python script writes GLB, textures, collider XML and a C# table, no Blender | `startup:asset-load` | Everything in sync; the script emits axis-aligned box colliders only (slopes become steps) |

## See also

- [Check GLB meshes offline: z-fighting, degenerate UVs, names and bounds](../../validation/find-z-fighting-and-degenerate-uvs-in-glb/overview.md), [Declare glTF meshes and PBR materials in asset XML and author their texture sets](../declare-gltf-and-pbr-material-assets/overview.md), [Add a part's art with its own GLB model and PBR textures](../add-a-part-model-with-glb-and-pbr-textures/overview.md),
  [Define articulated subparts and mounts, animate and hide them from code](../../../vehicles-and-parts/parts/define-and-animate-subparts/overview.md), [Redirect the game to patched copies of its XML and coordinate edits between mods](../../xml-patching/redirect-game-xml-loading/overview.md), [Place a static object on a planet surface at a latitude and longitude, and keep it anchored](../../../celestials/terrain/place-static-objects-on-a-planet-surface/overview.md), [Add terrain decals from code and flatten or dig the ground under a custom site](../../../celestials/terrain/add-terrain-decals-and-dig-a-site/overview.md)
- [Art and audio pipeline tooling](../../../../mods/ksacars/features/art-and-audio-pipeline-tooling.md), [GLB authoring pipeline and mesh checks](../../../../mods/ksagolf/features/glb-authoring-and-mesh-checks.md), [Headless Blender model generation and offline GLB checks](../../../../mods/ksarmory/features/blender-model-pipeline-and-checks.md),
  [A generated spiral ramp - mesh, shadow caster, box colliders and lamps from one script](../../../../mods/ksastructures/features/generated-deep-ramp.md)
