---
type: How-To
title: Generate a flat-swatch palette atlas with a JSON swatch map
description: Write Diffuse, PBR and Normal PNGs as a grid of flat 128 px swatches plus a JSON of UV centres, then box-project every face into a patch on a swatch so hard-surface parts get a full PBR set with no unwrapping or baking
category: assets
group: models-and-textures
topic: declare-gltf-and-pbr-material-assets
approach: 3
difficulty: intermediate
prerequisites: [blender]
demonstrated_by: [mods/ksarmory/features/blender-model-pipeline-and-checks]
techniques: [texture-pipeline, glb-model]
hooks: [offline python palette.py]
lifecycle: [startup:asset-load]
game_types:
  - KSA.AssetBundle
  - KSA.PbrMaterialReference
  - KSA.TextureReference
  - KSA.TexturePowerReference
  - KSA.FileReference
  - KSA.TextureCategory
tags: [pbr, palette, swatch, atlas, generated-textures, hard-surface, uv]
---

Untextured hard-surface parts (a truck, a launcher, a gun mount) do not need a painted texture. KSArmory generates its three PBR textures from a table of named
swatches and maps every face to a patch on one swatch. The whole vehicle is textured by choosing a swatch name per primitive: no unwrapping, no seams, no baking, and
the result is deterministic and diffable. This article shows the generator, the JSON map the Blender script reads, and the one thing that goes wrong if you collapse a
face onto a single UV.

Demonstrated by [Headless Blender model generation and offline GLB checks](../../../../mods/ksarmory/features/blender-model-pipeline-and-checks.md). Compare the baked approach [Bake diffuse, normal, ORM and emissive maps from Blender and dilate the atlas](declare-gltf-and-pbr-material-assets-2.md).

## Background: how the game does it

A material needs Diffuse, Normal and AoRoughMetal channels for the part render path ([current/decomp/KSA/PartModel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModel.cs#L516-L523)). A flat swatch grid gives each channel its own PNG. The
renderer builds a tangent frame from the screen-space derivatives of position and UV (`cotangent_frame` in [current/Content/Core/Shaders/Common/SharedFrag.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/SharedFrag.glsl#L8-L26), which divides by the larger of the squared tangent and bitangent lengths); a face whose loops share one UV has a zero derivative, a zero tangent and a NaN after normalisation, which
poisons shading even with a flat normal map. That is the same defect [Check GLB meshes offline: z-fighting, degenerate UVs, names and bounds](../../validation/find-z-fighting-and-degenerate-uvs-in-glb/overview.md) checks for.

## Lifecycle and timing

Authoring and CI time (`palette.py --check` can gate CI). In game the PNGs are ordinary `Vessel` textures read at `startup:asset-load` ([Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).

## Steps

1. **Define swatches as data.** Each has a grid cell, a diffuse colour, roughness and metalness. Row 0 is the bottom row, matching UV space, so no V flip is needed
   anywhere in the chain:

   ```python
   # tools/model/palette.py L47-L68
   SWATCHES = [
       # name          col row  colour             rough metal
       ("hull",          0, 0, (78, 88, 62),        0.82, 0.0),   # Russian olive drab
       ("hull_dark",     1, 0, (52, 60, 42),        0.85, 0.0),   # shadowed panels, turret sides
       ("deck",          2, 0, (64, 72, 52),        0.88, 0.0),   # walkway plate
       ("rubber",        3, 0, (26, 26, 28),        0.95, 0.0),   # tyres

       ("tube",          0, 1, (96, 104, 80),       0.80, 0.0),   # missile containers
       ("tube_cap",      1, 1, (172, 170, 160),     0.70, 0.0),   # frangible muzzle covers
       ("metal",         2, 1, (150, 152, 155),     0.35, 1.0),   # bare steel, hubs, frames
       ("steel_dark",    3, 1, (80, 82, 86),        0.45, 1.0),   # gun barrels, trunnions

       ("radar",         0, 2, (196, 196, 190),     0.65, 0.0),   # array housings
       ("array",         1, 2, (118, 126, 132),     0.45, 0.15),  # the radiating faces
       ("glass",         2, 2, (30, 42, 52),        0.12, 0.0),   # cab glazing, EO window
       ("detail",        3, 2, (110, 116, 96),      0.80, 0.0),   # stowage, light housings

       ("warn",          0, 3, (176, 58, 42),       0.78, 0.0),   # hazard markings
       ("black",         1, 3, (12, 12, 14),        0.90, 0.0),   # grilles, recesses, gaps
       ("bronze",        2, 3, (148, 94, 44),       0.55, 0.7),   # missile booster casing
       ("missile",       3, 3, (190, 193, 196),     0.40, 0.2),   # sustainer body
   ]
   ```

2. **Render the three textures and the swatch table.** The PBR image is R = AO (255, unoccluded), G = roughness, B = metalness; the normal map is flat (128, 128, 255);
   unmapped diffuse is magenta so a UV that missed is obvious:

   ```python
   # tools/model/palette.py L84-L101
   def build():
       """Renders the three textures and the swatch table, without touching disk."""
       diffuse = Image.new("RGB", (SIZE, SIZE), (255, 0, 255))   # magenta = a UV that missed
       pbr = Image.new("RGB", (SIZE, SIZE), (255, 255, 0))
       normal = Image.new("RGB", (SIZE, SIZE), (128, 128, 255))

       table = {}
       for name, col, row, rgb, rough, metal in SWATCHES:
           box = cell_box(col, row)
           diffuse.paste(rgb, box)
           pbr.paste((255, round(rough * 255), round(metal * 255)), box)
           u, v = uv_centre(col, row)
           table[name] = {"uv": [u, v], "rgb": list(rgb), "roughness": rough, "metalness": metal}

       return {"KSArmory_Diffuse.png": diffuse,
               "KSArmory_PBR.png": pbr,
               "KSArmory_Normal.png": normal}, table

   ```

3. **Write them next to the mod's assets, plus the JSON for Blender**, so the UV coordinates and the pixels come from one source:

   ```python
   # tools/model/palette.py L133-L150
   def main():
       if "--check" in sys.argv:
           return check()

       TEXTURES.mkdir(parents=True, exist_ok=True)

       images, table = build()
       for name, image in images.items():
           image.save(TEXTURES / name)

       PALETTE_JSON.write_text(json.dumps({"grid": GRID, "swatches": table}, indent=2) + "\n")

       print(f"wrote {len(table)} swatches at {SIZE}x{SIZE} ({CELL} px cells)")
       for name in table:
           u, v = table[name]["uv"]
           print(f"  {name:<12} uv = ({u:.4f}, {v:.4f})  rgb = {tuple(table[name]['rgb'])}")
       print(f"\n  {TEXTURES.relative_to(REPO)}/KSArmory_{{Diffuse,PBR,Normal}}.png")
       print(f"  {PALETTE_JSON.relative_to(REPO)}")
   ```

4. **Box-project each face into a small patch centred on its swatch**, with real UV area (not all loops on the centre), within a reach that leaves half a cell of margin
   against mip-level bleed:

   ```python
   # tools/model/pantsir.py L216-L229
   # UV scale for the per-face projection, and how far from a swatch's centre it may reach.
   #
   # Faces are given real UV *area* rather than being collapsed onto the swatch centre. A face
   # whose loops all share one UV has a zero UV derivative, so the tangent basis degenerates,
   # normalize() on it returns NaN, and NaN * 0 poisons the shading even though the normal map is
   # flat. In game that is a vehicle crawling with white speckle; Blender's preview does not show
   # it because the preview material has no normal map wired in.
   #
   # 0.012 UV/m keeps the largest face on the model (the 8 m deck, 4 m from its centre) inside
   # 0.048, comfortably within the 0.08 limit, which itself leaves half a cell of margin against
   # mip-level bleed.
   UV_PER_METRE = 0.012
   SWATCH_REACH = 0.08

   ```

   ```python
   # tools/model/pantsir.py L239-L267
   def project_to_swatch(mesh, uv, centre):
       """Box-projects each face into a small patch centred on one palette swatch.

       The patch is what matters. Collapsing a face's loops onto a single UV would give the same
       flat colour and no seams, but it also gives the face zero UV area - and a renderer deriving
       a tangent frame from that gets a zero-length tangent, whose normalize() is NaN. See
       UV_PER_METRE.

       Everything inside the patch is one flat colour, so the projection's orientation and scale
       are cosmetically irrelevant; they only need to be non-degenerate and consistent.
       """
       cu, cv = centre

       for poly in mesh.polygons:
           normal = poly.normal
           # Any reference not parallel to the face normal gives a usable in-plane basis.
           reference = Vector((0.0, 0.0, 1.0)) if abs(normal.z) < 0.9 else Vector((1.0, 0.0, 0.0))
           tangent = normal.cross(reference)
           if tangent.length < 1e-9:
               tangent = Vector((1.0, 0.0, 0.0))
           tangent.normalize()
           bitangent = normal.cross(tangent)

           origin = poly.center
           for loop_index in poly.loop_indices:
               offset = mesh.vertices[mesh.loops[loop_index].vertex_index].co - origin
               du = max(-SWATCH_REACH, min(SWATCH_REACH, offset.dot(tangent) * UV_PER_METRE))
               dv = max(-SWATCH_REACH, min(SWATCH_REACH, offset.dot(bitangent) * UV_PER_METRE))
               uv.data[loop_index].uv = (cu + du, cv + dv)
   ```

5. **Reuse the existing UV layer** (`mesh.uv_layers.get("UVMap")`) instead of creating a second one; the exporter writes the first layer as `TEXCOORD_0`, so a stray
   second layer smears the atlas over every face.

6. **Declare one material** with the three PNGs, `Category="Vessel"`, and use it for every subpart ([Add a part's art with its own GLB model and PBR textures](../add-a-part-model-with-glb-and-pbr-textures/overview.md) approach 2).

7. **Gate CI on `palette.py --check`.** It compares decoded pixels, not file bytes, because PNG encoders differ between Pillow versions:

   ```python
   # tools/model/palette.py L103-L129
   def check():
       """Verifies the committed textures still match what this script generates.

       Compares decoded pixels, not file bytes. PNG encoders differ between Pillow versions, so a
       byte comparison fails on a machine with a different Pillow even though the images are
       identical - which as a CI gate is pure noise.
       """
       images, _ = build()
       problems = 0

       for name, image in images.items():
           path = TEXTURES / name
           if not path.is_file():
               print(f"  MISSING {path}", file=sys.stderr)
               problems += 1
               continue
           with Image.open(path) as committed:
               if committed.convert("RGB").tobytes() != image.tobytes():
                   print(f"  STALE {name}: does not match palette.py", file=sys.stderr)
                   problems += 1
               else:
                   print(f"  ok {name}")

       if problems:
           print("\nrerun ./tools/model/palette.py and commit the result", file=sys.stderr)
           return 1
       print("textures match the palette definition")
   ```

## Complete example

- [tools/model/palette.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/model/palette.py), [tools/model/palette.json](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/model/palette.json), [tools/model/pantsir.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/model/pantsir.py)
- [src/KSArmory/KSArmoryAssets.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryAssets.xml) (materials and parts)

## Variations and gotchas

- **Large cells (128 px in 512)** keep mipmapping from bleeding one swatch into its neighbour at any viewing distance.
- **Zero UV area is the trap.** Collapsing every loop onto the centre gives the same flat colour with no seams, but a NaN tangent and white speckle in game that Blender's
  preview does not show.
- **Channel order is not a guess.** The ORM order matches `<AoRoughMetal>` and Core's own defaults.
- **Edit the swatch table, rerun, commit the PNGs and the JSON.** The JSON is the single source for both pixels and UVs.
- **Not for organic or textured art.** It gives no detail; use the baked recipe for painted parts.

## See also

- [Bake diffuse, normal, ORM and emissive maps from Blender and dilate the atlas](declare-gltf-and-pbr-material-assets-2.md), [Emit flat-colour textures from the same script that generates the geometry](declare-gltf-and-pbr-material-assets-4.md), [Export or generate a GLB that KSA loads correctly](../export-glb-from-blender-for-ksa/overview.md),
  [Check GLB meshes offline: z-fighting, degenerate UVs, names and bounds](../../validation/find-z-fighting-and-degenerate-uvs-in-glb/overview.md)
- [Headless Blender model generation and offline GLB checks](../../../../mods/ksarmory/features/blender-model-pipeline-and-checks.md)
