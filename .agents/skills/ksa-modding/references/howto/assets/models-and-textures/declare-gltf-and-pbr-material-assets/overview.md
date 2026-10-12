---
type: Game Feature
title: Declare glTF meshes and PBR materials in asset XML and author their texture sets
description: How GltfFile and PbrMaterial assets register meshes and looks without parts, the three-channel rule, and four ways to author the Diffuse, Normal and AoRoughMetal textures
category: assets
group: models-and-textures
topic: declare-gltf-and-pbr-material-assets
demonstrated_by:
  - mods/ksagolf/features/prop-and-course-asset-xml
  - mods/ksacars/features/car-part-assets-glb-pbr
  - mods/ksarmory/features/blender-model-pipeline-and-checks
  - mods/ksastructures/features/generated-deep-ramp
  - mods/ksacars/features/art-and-audio-pipeline-tooling
  - mods/unscience/features/parts-now-bundle-validation
  - mods/ksagolf/features/glb-authoring-and-mesh-checks
lifecycle: [startup:asset-load, startup:all-mods-loaded]
game_types:
  - KSA.AssetBundle
  - KSA.Gltf2Reference
  - KSA.PbrMaterialReference
  - KSA.TextureReference
  - KSA.TexturePowerReference
  - KSA.FileReference
  - KSA.ModLibrary
  - KSA.TextureCategory
tags: [gltf, pbr, material, texture, authoring]
---

A mod that needs a mesh and a look can declare them directly: `<GltfFile>` for the mesh and `<PbrMaterial>` for its textures. Parts and static objects pair them by id in XML; props
and ghost tints pair them in code. Either way the textures are a Diffuse, Normal and AoRoughMetal set (plus optional Emissive, ThinFilm and Alpha), and authoring that set is
where most of the time goes.

## Engine background

- `Gltf2Reference` (`GltfFile`): `Source`, `Skeleton`, `Image`, `Material`, `Usage`; registers in the glTF table under its `Id` ([current/decomp/KSA/Gltf2Reference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Gltf2Reference.cs#L7-L60)).
- `PbrMaterialReference` (`PbrMaterial`): `Diffuse`, `Normal`, `AoRoughMetal`, `Emissive`, `ThinFilm`, `Alpha`; an element with none of the first three is a reference
  ([current/decomp/KSA/PbrMaterialReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PbrMaterialReference.cs#L7-L94)).
- `TextureReference`: `Category`, `IsVirtual`, optional sidecar `.toml`, loaded by `DoLoad` and bound later; a texture that loses id registration is a reference and keeps handle 0
  ([current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L20-L156)).
- The part and static-object render paths read the three main handles without a null check ([current/decomp/KSA/PartModel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModel.cs#L516-L523)); `GpuMaterialSystem` falls back to a default
  texture ([current/decomp/KSA/GpuMaterialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuMaterialSystem.cs#L67-L82)).
- Registries: `ModLibrary.AllGltfs`, `AllMaterials`, `AllMeshes` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L581-L615)).
- Phase: `startup:asset-load`. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Declare bare GltfFile and PbrMaterial assets and pair them by id in code](declare-gltf-and-pbr-material-assets-1.md) | KSAGolf | Bare `GltfFile` and `PbrMaterial`, one shared atlas and material for many meshes, ghost tints, paired by id in code | `startup:asset-load` | Lightest way to get a mesh into the renderer; no parts, no collisions from XML |
| [Bake diffuse, normal, ORM and emissive maps from Blender and dilate the atlas](declare-gltf-and-pbr-material-assets-2.md) | KSACars | Bake diffuse, normal, ORM and emissive from Blender over a key colour and dilate the atlas | `startup:asset-load` | Full painted PBR; many ways to fail quietly, needs offline tools |
| [Generate a flat-swatch palette atlas with a JSON swatch map](declare-gltf-and-pbr-material-assets-3.md) | KSArmory | Generated flat-swatch palette atlas with a JSON swatch map, per-face box projection | `startup:asset-load` | Deterministic and diffable; no surface detail |
| [Emit flat-colour textures from the same script that generates the geometry](declare-gltf-and-pbr-material-assets-4.md) | KSAStructures | Flat-colour textures emitted by the geometry script, one set per material | `startup:asset-load` | Geometry, UVs and textures cannot drift; no Blender; still one set per material |

## See also

- [Know which asset XML elements the game accepts and what they load](../../asset-xml/asset-xml-element-reference/overview.md), [Resolve asset ids and file paths correctly across mods](../../asset-xml/resolve-asset-ids-and-mod-relative-paths/overview.md), [Add a part's art with its own GLB model and PBR textures](../add-a-part-model-with-glb-and-pbr-textures/overview.md),
  [Export or generate a GLB that KSA loads correctly](../export-glb-from-blender-for-ksa/overview.md), [Import GLB models and their materials at runtime](../import-glb-models-at-runtime/overview.md), [Composite an image over a stock texture](../composite-png-over-stock-texture/overview.md),
  [Check GLB meshes offline: z-fighting, degenerate UVs, names and bounds](../../validation/find-z-fighting-and-degenerate-uvs-in-glb/overview.md), [Recolour vessel parts and kittens with a patched part shader](../../../rendering/textures-and-decals/paint-vessel-parts-with-custom-colours/overview.md)
- [Prop and course asset XML](../../../../mods/ksagolf/features/prop-and-course-asset-xml.md), [Car part assets: GLB atlases, PBR materials and subparts](../../../../mods/ksacars/features/car-part-assets-glb-pbr.md), [Headless Blender model generation and offline GLB checks](../../../../mods/ksarmory/features/blender-model-pipeline-and-checks.md),
  [A generated spiral ramp - mesh, shadow caster, box colliders and lamps from one script](../../../../mods/ksastructures/features/generated-deep-ramp.md)
- [Attach a prop to a kitten's hand or another bone at runtime, building the prop mesh from mod assets](../../../kittens/appearance/attach-a-prop-to-a-kitten-bone/overview.md) - related topic
- [Draw your own meshes through the engine's mesh renderers](../../../rendering/meshes/draw-mod-meshes-with-the-engine-renderers/overview.md) - related topic

