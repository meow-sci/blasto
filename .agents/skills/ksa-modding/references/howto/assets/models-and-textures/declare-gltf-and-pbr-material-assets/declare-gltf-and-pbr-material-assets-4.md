---
type: How-To
title: Emit flat-colour textures from the same script that generates the geometry
description: Let one numpy and Pillow script write the mesh, its UVs and the swatch textures so they cannot drift, and give each material its own texture files because a shared path is bound for only the first material
category: assets
group: models-and-textures
topic: declare-gltf-and-pbr-material-assets
approach: 4
difficulty: advanced
prerequisites: []
demonstrated_by: [mods/ksastructures/features/generated-deep-ramp]
techniques: [code-generation, texture-pipeline]
hooks: [offline python office-deep.py]
lifecycle: [startup:asset-load]
game_types: [KSA.AssetBundle, KSA.PbrMaterialReference, KSA.TextureReference, KSA.FileReference, KSA.ModLibrary]
tags: [pbr, generated-textures, numpy, pillow, swatch, shared-texture, static-object]
---

KSAStructures generates its deep spiral ramp (seven turns down a 54 m shaft) with one Python script that needs numpy and Pillow and no Blender. The
script writes the visible mesh, an invisible shadow caster, the collider XML and the textures together, so the geometry, its UVs and the colours it samples
cannot drift. This article covers the texture half: sixteen flat swatches in a 64 by 64 diffuse, flat normal and AO/roughness/metal maps, written as separate files per
material, and the reason for that. The geometry half is [Export or generate a GLB that KSA loads correctly](../export-glb-from-blender-for-ksa/overview.md) approach 4; the marker splice is
[Edit shadow copies with anchor-verified text patches and generated XML between markers](../../xml-patching/redirect-game-xml-loading/redirect-game-xml-loading-3.md).

Demonstrated by [A generated spiral ramp - mesh, shadow caster, box colliders and lamps from one script](../../../../mods/ksastructures/features/generated-deep-ramp.md).

## Background: how the game does it

A material's textures are `TextureReference` instances declared inside the `PbrMaterial`. `FileReference.OnDataLoad` gives a texture without an `Id` the absolute path as its Id,
and `Load()` downgrades a texture to a reference when that Id is already registered ([current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L39-L72)). The part and static-object render paths read
`Template.Material.DiffuseReference.BindlessHandle` directly ([current/decomp/KSA/StaticObjectModel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObjectModel.cs#L305-L312)), and a texture that lost the registration was never bound
([current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L125-L156)), so its handle stays 0. That explains what the mod observed: KSA gives a texture path to the first material that names it and
hands the rest white. Props drawn through the code-side material system resolve with `Get()` and are not affected (approach [Declare bare GltfFile and PbrMaterial assets and pair them by id in code](declare-gltf-and-pbr-material-assets-1.md)).

## Lifecycle and timing

Build time for the script; the PNGs load as ordinary assets at `startup:asset-load` ([Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).

## Steps

1. **Make the faces sample a swatch.** Each quad gets its own corner of a 4 by 4 swatch atlas, so every triangle has UV area for the engine's tangent frame (see
   [Check GLB meshes offline: z-fighting, degenerate UVs, names and bounds](../../validation/find-z-fighting-and-degenerate-uvs-in-glb/overview.md)). The swatch index is chosen per `solid()` call:

   ```python
   # tools/model/office-deep.py L118-L132
   def skin():
       """A few millimetres, different for every body drawn: two bodies meeting on one plane z-fight."""
       global drawn
       drawn += 1
       return 0.003 + (drawn * 7 % 23) * 0.0009


   def solid(name, x0, x1, ya, yb, za, zb, swatch, top_swatch=None, collide=True):
       e = skin()
       structure.box(x0 - e, x1 + e, ya - e, yb + e, za - e, zb + e, SWATCH[swatch], None if top_swatch is None else SWATCH[top_swatch])
       if collide:
           boxes.append((name, x0, x1, ya, yb, za, zb))
       if min(x1 - x0, yb - ya, zb - za) > 3 * INSET:
           caster.box(x0 + INSET, x1 - INSET, ya + INSET, yb - INSET, za + INSET, zb - INSET, 0)

   ```

2. **Write the textures in the same script, with each material's own files:**

   ```python
   # tools/model/office-deep.py L282-L292
   # Flat colours, sixteen swatches; a material's textures are its own files, because KSA loads a
   # texture path for the first material that names it and hands the rest white.
   srgb = lambda c: int(round(255 * (12.92 * c if c <= 0.0031308 else 1.055 * c ** (1 / 2.4) - 0.055)))
   img = np.zeros((64, 64, 3), dtype=np.uint8)
   img[:] = srgb(0.36)
   for name, s in SWATCH.items():
       r, c = s // 4, s % 4
       img[r * 16:(r + 1) * 16, c * 16:(c + 1) * 16] = [srgb(v) for v in COLOUR[name]]
   Image.fromarray(img).save(ROOT / "Textures" / "KSAOffice_Deep_Diffuse.png")
   Image.fromarray(np.full((8, 8, 3), (255, 217, 0), dtype=np.uint8)).save(ROOT / "Textures" / "KSAOffice_Deep_AoRoughMetal.png")
   Image.fromarray(np.full((8, 8, 3), (128, 128, 255), dtype=np.uint8)).save(ROOT / "Textures" / "KSAOffice_Deep_Normal.png")
   ```

   The diffuse is sRGB-encoded flat colours; the AO/roughness/metal map is a flat (255, 217, 0) (unoccluded, mostly rough, non-metal); the normal map is flat
   (128, 128, 255).

3. **Splice the `PbrMaterial` into the asset XML** between the script's markers, naming those files and `Category="Vessel"`:

   ```python
   # tools/model/office-deep.py L294-L310
   # The static sub-objects, between the markers in the office's asset file.
   lines = ['  <PbrMaterial Id="KSAOffice_DeepMaterial">',
            '    <Diffuse Path="Textures/KSAOffice_Deep_Diffuse.png" Category="Vessel" />',
            '    <Normal Path="Textures/KSAOffice_Deep_Normal.png" Category="Vessel" />',
            '    <AoRoughMetal Path="Textures/KSAOffice_Deep_AoRoughMetal.png" Category="Vessel" />',
            '  </PbrMaterial>',
            '  <StaticSubObject Id="KSAOffice_DeepBody">',
            '    <PartModel Id="KSAOffice_DeepBody_Model">',
            '      <Mesh Id="KSAOffice_Deep" />',
            '      <Material Id="KSAOffice_DeepMaterial" />',
            '    </PartModel>',
            '    <Collider Id="KSAOffice_Deep_Collider">']
   for name, x0, x1, ya, yb, za, zb in boxes:
       lines += [f'      <Box Id="Deep{name}">',
                 f'        <LocationAsmb X="{(x0 + x1) / 2:.4f}" Y="{(ya + yb) / 2:.4f}" Z="{(za + zb) / 2:.4f}" />',
                 f'        <LengthX M="{x1 - x0:.4f}" />', f'        <LengthY M="{yb - ya:.4f}" />',
                 f'        <LengthZ M="{zb - za:.4f}" />', '      </Box>']
   ```

4. **Re-run on any layout change** and commit the output. The constants that the script does not regenerate (the C# restatement of the shaft bounds and the shader's own
   copies) must be moved by hand.

## Complete example

- [tools/model/office-deep.py](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/model/office-deep.py)
- The generated block: [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L37-L50)

## Variations and gotchas

- **One material, one set of files.** Do not point two materials of a part at the same PNG path; give each its own, even if they are identical copies.
- **Flat textures are tiny** (8 by 8 for normal and ORM, 64 by 64 for diffuse) and cost nothing, but they still need to exist for every channel.
- **sRGB.** Encode diffuse colours with the sRGB transfer function if you specify them in linear values, as the script does.
- **UV area.** A swatch quad needs real UV area, not a point, for the tangent frame.

## See also

- [Bake diffuse, normal, ORM and emissive maps from Blender and dilate the atlas](declare-gltf-and-pbr-material-assets-2.md), [Generate a flat-swatch palette atlas with a JSON swatch map](declare-gltf-and-pbr-material-assets-3.md), [Export or generate a GLB that KSA loads correctly](../export-glb-from-blender-for-ksa/overview.md),
  [Edit shadow copies with anchor-verified text patches and generated XML between markers](../../xml-patching/redirect-game-xml-loading/redirect-game-xml-loading-3.md), [Place a static object on a planet surface at a latitude and longitude, and keep it anchored](../../../celestials/terrain/place-static-objects-on-a-planet-surface/overview.md)
- [A generated spiral ramp - mesh, shadow caster, box colliders and lamps from one script](../../../../mods/ksastructures/features/generated-deep-ramp.md)
