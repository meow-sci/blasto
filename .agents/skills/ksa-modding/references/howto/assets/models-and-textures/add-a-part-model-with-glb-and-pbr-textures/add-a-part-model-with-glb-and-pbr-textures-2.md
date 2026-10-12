---
type: How-To
title: Share one palette atlas across parts, add _VM preview twins and keep subpart ids apart
description: Eleven GLB atlases, generated parts sharing one flat palette material, hand-modelled parts with a material per body, MeshView _VM twins beside every PartModel and the split between the Assets and GameData files
category: assets
group: models-and-textures
topic: add-a-part-model-with-glb-and-pbr-textures
approach: 2
difficulty: intermediate
prerequisites: [starmap, blender]
demonstrated_by:
  - mods/ksarmory/features/weapon-part-definitions-xml
  - mods/ksarmory/features/blender-model-pipeline-and-checks
techniques: [xml-asset, glb-model]
hooks: [xml Part, xml SubPart]
lifecycle: [startup:asset-load]
game_types:
  - KSA.PartTemplate
  - KSA.SubPartTemplate
  - KSA.PartInstance
  - KSA.PartModelModule
  - KSA.PartModelGlassModule
  - KSA.MeshViewModule
  - KSA.MeshAtlasFileReference
  - KSA.PbrMaterialReference
  - KSA.TransformReference
tags: [part, subpart, palette, atlas, meshview, ids, connector, assets-vs-gamedata]
---

KSArmory ships fourteen parts and none of them carries behaviour in XML. Each is declared as inert structure: art in one file, simulation data in another. The art file shows a
different way to organise the model side from KSACars: eleven GLB atlases, generated hard-surface parts that share one flat-colour palette material, a hand-modelled pod that gets a
material per body, a `MeshView` `_VM` twin beside every `PartModel` as Core does, and subpart ids kept apart from mesh ids. This article lays out those choices.

Demonstrated by [Weapon parts as inert KSA parts split into an Assets file and a GameData file](../../../../mods/ksarmory/features/weapon-part-definitions-xml.md) and [Headless Blender model generation and offline GLB checks](../../../../mods/ksarmory/features/blender-model-pipeline-and-checks.md). Compare [Build a part from one atlas per vehicle with subparts for moving bodies, glass and hidden doors](add-a-part-model-with-glb-and-pbr-textures-1.md).

## Background: how the game does it

The game's own `PartAssetBundler` shows the shape every part file should have: a `MeshAtlas` for the GLB, a `PbrMaterial` named `<stem>_Material`, one `SubPart` per node with a
`PartModel` (or `PartModelGlass`/`PartModelDynamic`), a `MeshView` when a `<name>_VM` node exists, and a `Part` per prefab file
([current/decomp/KSA.GlbImport/PartAssetBundler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.GlbImport/PartAssetBundler.cs#L22-L62), [current/decomp/KSA.GlbImport/PartAssetBundler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.GlbImport/PartAssetBundler.cs#L124-L188)). `PartTemplate.Components` accepts module elements by `[XmlType]` name
([Know which asset XML elements the game accepts and what they load](../../asset-xml/asset-xml-element-reference/overview.md)). `SubPart` and mesh ids go to separate registries (`AllParts`, `AllMeshes`), so they never collide in the game
([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L581-L676)).

## Lifecycle and timing

`startup:asset-load` for both files; `ModLibrary.AttachGameData` merges `PartGameData` and `SubPartGameData` into the matching templates afterwards, after `StarMapAllModsLoaded`, so a mod that
reads a `PartTemplate` there sees the Assets definition but not the merged game data. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Pick one material for generated parts.** Every face of the generated vehicle is UV-mapped to a swatch of one palette, so one material serves the whole atlas
   ([Generate a flat-swatch palette atlas with a JSON swatch map](../declare-gltf-and-pbr-material-assets/declare-gltf-and-pbr-material-assets-3.md)):

   ```xml
   <!-- src/KSArmory/KSArmoryAssets.xml L38-L48 -->
     <!--
       One material for the whole vehicle. Every face is UV-mapped to the centre of a flat swatch
       in a 4x4 palette, so surface variety comes from UVs rather than from painted texels - see
       tools/model/palette.py. AoRoughMetal is R=occlusion, G=roughness, B=metalness, matching the
       element name, the glTF ORM convention, and Core's own default_pbr.png of (255, 180, 0).
     -->
     <PbrMaterial Id="KSArmory_Material">
       <Diffuse Path="Textures/KSArmory_Diffuse.png" Category="Vessel" />
       <Normal Path="Textures/KSArmory_Normal.png" Category="Vessel" />
       <AoRoughMetal Path="Textures/KSArmory_PBR.png" Category="Vessel" />
     </PbrMaterial>
   ```

2. **Give hand-modelled bodies their own atlas and a material per body.** A shape that was observed, not generated, is unwrapped and baked, and each body carries its own maps. Several atlases and
   materials in one file are fine; Core does the same:

   ```xml
   <!-- src/KSArmory/KSArmoryAssets.xml L62-L81 -->
     <MeshAtlas Path="Meshes/KSArmory_Litening.glb" />

     <PbrMaterial Id="KSArmory_Litening_Body_Material">
       <Diffuse Path="Textures/KSArmory_Litening_Body_Diffuse.png" Category="Vessel" />
       <Normal Path="Textures/KSArmory_Litening_Body_Normal.png" Category="Vessel" />
       <AoRoughMetal Path="Textures/KSArmory_Litening_Body_PBR.png" Category="Vessel" />
     </PbrMaterial>

     <PbrMaterial Id="KSArmory_Litening_Roll_Material">
       <Diffuse Path="Textures/KSArmory_Litening_Roll_Diffuse.png" Category="Vessel" />
       <Normal Path="Textures/KSArmory_Litening_Roll_Normal.png" Category="Vessel" />
       <AoRoughMetal Path="Textures/KSArmory_Litening_Roll_PBR.png" Category="Vessel" />
     </PbrMaterial>

     <PbrMaterial Id="KSArmory_Litening_Window_Material">
       <Diffuse Path="Textures/KSArmory_Litening_Window_Diffuse.png" Category="Vessel" />
       <Normal Path="Textures/KSArmory_Litening_Window_Normal.png" Category="Vessel" />
       <AoRoughMetal Path="Textures/KSArmory_Litening_Window_PBR.png" Category="Vessel" />
     </PbrMaterial>

   ```

3. **Name subparts apart from meshes.** KSArmory names subparts `KSArmory_<name>_SubPart` and atlas meshes `KSArmory_Subpart_<name>`. The game does not need the split (Core uses one name for both), but the
   author's offline validator and an external importer do ([Resolve asset ids and file paths correctly across mods](../../asset-xml/resolve-asset-ids-and-mod-relative-paths/overview.md)). A `SubPart` pairs `PartModel` with its `MeshView` twin:

   ```xml
   <!-- src/KSArmory/KSArmoryAssets.xml L326-L334 -->
     <SubPart Id="KSArmory_CiwsBase_SubPart">
       <PartModel Id="KSArmory_Subpart_CiwsBase_Model">
         <Mesh Id="KSArmory_Subpart_Mk15Base" />
         <Material Id="KSArmory_Ciws_Material" />
       </PartModel>
       <MeshView>
         <Mesh Id="KSArmory_Subpart_Mk15Base_VM" />
       </MeshView>
     </SubPart>
   ```

4. **Put every moving assembly in its own `SubPart`.** Turrets, pods, guns, radar and every round are separate subparts with pivot-recentred meshes so the C# can rewrite their transforms per frame.
   The `Part` instances them with `Transform/Position`; the comments record why each offset exists:

   ```xml
   <!-- src/KSArmory/KSArmoryAssets.xml L1286-L1311 -->
     <Part Id="KSArmory_Prefab_Ciws">

       <SubPart Id="KSArmory_Ciws_Base" InstanceOf="KSArmory_CiwsBase_SubPart" />

       <!--
         Traverses about the stack centreline. Its mesh is exported recentred on the pivot, so KSA
         spins it in place; the offset comes back here.
       -->
       <SubPart Id="KSArmory_Ciws_Turret" InstanceOf="KSArmory_CiwsTurret_SubPart">
         <Transform>
           <Position X="0.80000" Y="0.00000" Z="0.00000" />
         </Transform>
       </SubPart>

       <!--
         The whole head, meaning housing, barrels and the radome above them, swinging on the
         trunnion between the cheeks while riding the traverse. The mod rewrites both its rotation
         and its position every frame. The dome carries the track antenna, which stays boresighted
         with the barrels, so it is one rigid body with them rather than something the traverse
         holds upright while the barrels alone move.
       -->
       <SubPart Id="KSArmory_Ciws_Guns" InstanceOf="KSArmory_CiwsGuns_SubPart">
         <Transform>
           <Position X="2.25000" Y="0.00000" Z="0.00000" />
         </Transform>
       </SubPart>
     <!-- ... -->
   ```

5. **Size the attach node.** A stacking connector's `Transform/Scale` is the node size. A part mates a tank's `Internal` node only if its own is no larger; without a scale the mount sinks onto the nested node
   (8 cm into a 2 m tank, 24.5 cm into a 3 m one):

   ```xml
   <!-- src/KSArmory/KSArmoryAssets.xml L1313-L1324 -->
       <!--
         Node attach at the mounting face. No ToSurface flag: this one stacks. Scale is the node's
         size, and a part mates a tank's Internal node only if its own is no larger: without it the
         mount sinks onto that nested node, 8 cm into a 2 m tank and 24.5 cm into a 3 m one.
       -->
       <Connector Id="_ciwsConnectorBase">
         <Transform>
           <Position X="0.00000" />
           <Rotation X="3.14159" Z="3.14159" />
           <Scale X="3.00000" Y="3.00000" Z="3.00000" />
         </Transform>
       </Connector>
   ```

6. **Keep art and simulation in two files.** The Assets file holds `MeshAtlas`, `PbrMaterial`, `SubPart`, `Part`. The GameData file holds `EditorTagDef`, `PartGameData` (mass, colliders, connectors, tags),
   `SubPartGameData` and friends. The detail is [Define a part with its own game data and GLB model](../../../vehicles-and-parts/parts/define-a-part-with-game-data/overview.md). Both are listed in `mod.toml` `assets`, explicitly.

7. **Run the offline checks on every change.** `validate-parts.py` resolves each `InstanceOf`, `Material` and `Mesh` id and checks the namespaces ([Validate asset XML ids and paths before launching the game (offline)](../../validation/validate-asset-xml-references/overview.md)).

## Complete example

- [src/KSArmory/KSArmoryAssets.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryAssets.xml) (14 parts, 11 atlases, 14 materials) and [src/KSArmory/KSArmoryGameData.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryGameData.xml)
- [tools/model/palette.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/model/palette.py), [tools/model/pantsir.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/model/pantsir.py) (the generated parts)

## Variations and gotchas

- **Uniform material, different roles.** The palette material is deliberately generic so any generated part can reuse it; do not use it for a shape that needs painted detail.
- **A material per body, with its own PNGs.** KSArmory never points two materials at the same texture path (see [Emit flat-colour textures from the same script that generates the geometry](../declare-gltf-and-pbr-material-assets/declare-gltf-and-pbr-material-assets-4.md)).
- **`_VM` twins are cheap.** The editor preview mesh may be a lower-detail copy; PartAssetBundler adds the `MeshView` only when the node exists.
- **Pivot and mount frame.** Meshes carry final part-space coordinates, X is the vehicle's up, and the origin sits on the mounting face; no `Transform` is needed for a single rigid body.
- **Removing a subpart breaks saves** ([Change a shipped part without breaking saves](../../../vehicles-and-parts/parts/evolve-a-shipped-part-without-breaking-saves/overview.md)).

## See also

- [Build a part from one atlas per vehicle with subparts for moving bodies, glass and hidden doors](add-a-part-model-with-glb-and-pbr-textures-1.md), [Define a part with its own game data and GLB model](../../../vehicles-and-parts/parts/define-a-part-with-game-data/overview.md), [Define articulated subparts and mounts, animate and hide them from code](../../../vehicles-and-parts/parts/define-and-animate-subparts/overview.md),
  [Export or generate a GLB that KSA loads correctly](../export-glb-from-blender-for-ksa/overview.md), [Declare glTF meshes and PBR materials in asset XML and author their texture sets](../declare-gltf-and-pbr-material-assets/overview.md)
- [Weapon parts as inert KSA parts split into an Assets file and a GameData file](../../../../mods/ksarmory/features/weapon-part-definitions-xml.md), [Headless Blender model generation and offline GLB checks](../../../../mods/ksarmory/features/blender-model-pipeline-and-checks.md)
