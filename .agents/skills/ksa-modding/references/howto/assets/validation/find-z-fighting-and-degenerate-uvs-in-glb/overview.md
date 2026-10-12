---
type: Game Feature
title: "Check GLB meshes offline: z-fighting, degenerate UVs, names and bounds"
description: How mods find coplanar faces, zero-area UVs, unpaired node and mesh names and unmaterialed primitives in a GLB with no Blender and no game, in Python and in C#
category: assets
group: validation
topic: find-z-fighting-and-degenerate-uvs-in-glb
demonstrated_by:
  - mods/ksagolf/features/glb-authoring-and-mesh-checks
  - mods/ksacars/features/art-and-audio-pipeline-tooling
  - mods/ksarmory/features/blender-model-pipeline-and-checks
  - mods/unscience/features/parts-now-bundle-validation
  - mods/unscience/features/rocky-mcrock-face-asset-catalog
lifecycle: [startup:asset-load, frame:draw-ui]
game_types: [KSA.MeshAtlasFileReference, KSA.MeshReference, Brutal.GltfApi.GltfUtility, Brutal.GltfApi.GltfLoader]
tags: [glb, validation, z-fighting, uv, offline]
---

Two defects look the same in game, as flickering white speckle, and appear in no Blender preview: coplanar faces from different directions, and triangles with zero UV area whose zero tangent normalises to NaN. Pure readers of the GLB binary find both, plus unpaired node and mesh names, primitives without a material and missing UVs, with no Blender and no game. The engine validates none of this.

## Engine background

- `MeshAtlasFileReference.DoLoad` registers a `MeshReference` per glTF mesh name and never reads nodes, so a node whose mesh has another name resolves to nothing ([current/decomp/KSA/MeshAtlasFileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshAtlasFileReference.cs#L26-L45)).
- `GltfLoader` / `GltfUtility.LoadModel` parse the JSON model; accessor `min` and `max` give bounds without decoding vertices ([current/decomp/Brutal.GltfApi/GltfLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.GltfApi/GltfLoader.cs#L1-L35)).
- The game's `PartAssetBundler` skips `_` helper nodes and `_VM` twins when generating subparts ([current/decomp/KSA.GlbImport/PartAssetBundler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.GlbImport/PartAssetBundler.cs#L44-L62)).
- Phase: the defects appear at render time; the checks run at build, CI and validation time. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Find coplanar faces and zero-area UVs with checkmesh.py and read bounds with meshinfo.py](find-z-fighting-and-degenerate-uvs-in-glb-1.md) | KSAGolf (also KSACars, KSArmory) | `checkmesh.py` plane bucketing and UV-area test, `meshinfo.py` bounds, run in CI | `startup:asset-load` (authoring) | Catches both invisible defects; thresholds in metres need tuning |
| [Compare exports by surface fingerprint and sweep articulated assemblies for collisions](find-z-fighting-and-degenerate-uvs-in-glb-2.md) | KSArmory | Order-independent fingerprint `--compare`, seeded skin, SAT sweep of articulated poses | `startup:asset-load` (authoring) | Answers "did it change" and "does it clip in motion"; heavier to run |
| [Parse the GLB JSON chunk in C# to validate mesh references](find-z-fighting-and-degenerate-uvs-in-glb-3.md) | unscience | Parse the GLB JSON chunk in C# to validate mesh references and list glTF meshes | `frame:draw-ui`, `startup:asset-load` | Works inside the game; reads names only, not geometry |

## See also

- [Export or generate a GLB that KSA loads correctly](../../models-and-textures/export-glb-from-blender-for-ksa/overview.md), [Validate asset XML ids and paths before launching the game (offline)](../validate-asset-xml-references/overview.md), [Import GLB models and their materials at runtime](../../models-and-textures/import-glb-models-at-runtime/overview.md), [Declare glTF meshes and PBR materials in asset XML and author their texture sets](../../models-and-textures/declare-gltf-and-pbr-material-assets/overview.md)
- [GLB authoring pipeline and mesh checks](../../../../mods/ksagolf/features/glb-authoring-and-mesh-checks.md), [Headless Blender model generation and offline GLB checks](../../../../mods/ksarmory/features/blender-model-pipeline-and-checks.md), [Validate Part asset bundles with fifteen rules before anything is written or registered](../../../../mods/unscience/features/parts-now-bundle-validation.md)
