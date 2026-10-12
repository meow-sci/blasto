---
type: How-To
title: Bake diffuse, normal, ORM and emissive maps from Blender and dilate the atlas
description: Unwrap a part into one atlas, bake lighting-free diffuse, normal, roughness and AO, route metalness through emission, pack ORM, bake over a key colour and dilate the background so lower mips do not bleed
category: assets
group: models-and-textures
topic: declare-gltf-and-pbr-material-assets
approach: 2
difficulty: advanced
prerequisites: [blender]
demonstrated_by:
  - mods/ksacars/features/car-part-assets-glb-pbr
  - mods/ksacars/features/art-and-audio-pipeline-tooling
  - mods/unscience/features/parts-now-bundle-validation
  - mods/ksagolf/features/glb-authoring-and-mesh-checks
techniques: [glb-model, texture-pipeline]
hooks: [xml PbrMaterial, offline blender bake]
lifecycle: [startup:asset-load]
game_types:
  - KSA.AssetBundle
  - KSA.Gltf2Reference
  - KSA.PbrMaterialReference
  - KSA.TextureReference
  - KSA.TexturePowerReference
  - KSA.FileReference
  - KSA.ModLibrary
  - KSA.TextureCategory
tags: [pbr, bake, blender, orm, atlas, dilation, mipmaps, texture-authoring]
---

A part with its own art needs three textures: diffuse, normal and an "AoRoughMetal" map. This article is the authoring recipe KSACars uses and KSAGolf
adopted, from unwrapping to the last texel of background fill: which Blender bake passes to run, how to compose the ORM map, why a bake margin alone is the wrong
fix for mip bleed, and the dilation tool that is. It also ties in the validator rule that rejects a material with a missing mandatory channel before it
crashes the renderer.

Demonstrated by [Car part assets: GLB atlases, PBR materials and subparts](../../../../mods/ksacars/features/car-part-assets-glb-pbr.md), [Art and audio pipeline tooling](../../../../mods/ksacars/features/art-and-audio-pipeline-tooling.md), [GLB authoring pipeline and mesh checks](../../../../mods/ksagolf/features/glb-authoring-and-mesh-checks.md) and
[Validate Part asset bundles with fifteen rules before anything is written or registered](../../../../mods/unscience/features/parts-now-bundle-validation.md). The XML side is [Declare bare GltfFile and PbrMaterial assets and pair them by id in code](declare-gltf-and-pbr-material-assets-1.md); the model side is [Export or generate a GLB that KSA loads correctly](../export-glb-from-blender-for-ksa/overview.md).

## Background: how the game does it

`AoRoughMetal` is the glTF ORM convention: R = occlusion, G = roughness, B = metalness. The mods cite Core's `default_pbr.png` as `(255, 180, 0)` and
`EmptyAoRoughMetallic.png` as `(255, 255, 0)` (the Core texture binaries are not in the pinned game checkout, so this is quoted from KSArmory's palette
documentation). KSA loads PNG and `.ktx2` and mixes them in one material. The part render path dereferences the Diffuse, Normal and AoRoughMetal handles
without a null check ([current/decomp/KSA/PartModel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModel.cs#L516-L523)), and unscience's validator rule V9 exists because of that:

```csharp
// parts-now.lib/Runtime/BundleValidatorRulesSchema.cs L67-L86
    /// <summary>
    /// V9 — the crash-prevention rule. Every model component needs a <c>&lt;Material&gt;</c>, and that
    /// material must declare all three of <c>&lt;Diffuse&gt;</c>, <c>&lt;Normal&gt;</c> and
    /// <c>&lt;AoRoughMetal&gt;</c>.
    /// <para>
    /// <c>ThumbnailRenderResources.AddDraw</c>, <c>PartModel.WriteInstancesToGpu</c>,
    /// <c>PartModelGlass.WriteInstancesToGpu</c> and <c>PartModelDynamic.WriteInstancesToGpu</c> all
    /// read <c>Material.DiffuseReference.BindlessHandle</c>, <c>.NormalReference.BindlessHandle</c>
    /// and <c>.PBRMap.BindlessHandle</c> with no null check, so a missing channel takes the whole game
    /// down at the first thumbnail or the first frame the part is visible.
    /// </para>
    /// <para>
    /// Read from the object graph, because a material may be declared inline inside the component or
    /// referenced by id — and an id-only <c>&lt;PbrMaterial&gt;</c> is a <i>reference</i>
    /// (<c>PbrMaterialReference.OnDataLoad</c> sets <c>_isReference</c> when all three channels are
    /// null), which has to be resolved against this set and then against the live registry before its
    /// channels can be judged.
    /// </para>
    /// </summary>
    private static void RuleV9MaterialChannels(ValidationContext context)
```

Textures also have an optional sidecar TOML (same base name) with `max`, `mipMaps`, `format` and `scblockformatfamily` keys, and the `Category` picks a size
cap ([current/decomp/KSA/TextureManifest.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureManifest.cs), [current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L84-L108)).

## Lifecycle and timing

Authoring time; nothing runs in the game except `startup:asset-load` reading the XML and files ([Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)). The dilation step must run between bake and export, after the UV layout
is frozen: the game samples lower mip levels in flight, so unfilled background only shows at range.

## Steps

1. **Unwrap the whole part into one atlas, not one set per body.** Select every body, edit them together and let Blender pack them as one set:
   `smart_project`, then `average_islands_scale` so texel density is uniform, then `pack_islands`. Ask `uv.select_overlap` afterwards and count selected faces
   per object (zero, or two islands share texels). KSACars' notes: one material per part, three 2048 sets for three bodies would be nine files and 8.7 MB,
   where one unwrap is three files. Stop before this step until a human has approved the geometry; everything after is welded to the shape.

2. **Bake in this order**, from the ksa-blender skill:

   ```text
   # .claude/skills/ksa-blender/SKILL.md L212-L222
   Then, in order:

   1. **`DIFFUSE` with `pass_filter={'COLOR'}`** — no lighting baked in.
   2. **`ROUGHNESS`** and **`AO`** into scratch images. AO at ~64 samples; the rest need one.
   3. **Metalness has no bake pass.** Route whatever feeds Principled's Metallic into an `Emission`
      shader, point the material output at it, bake `EMIT`, then put the BSDF back.
   4. **Compose ORM yourself**: R = AO, G = roughness, B = metalness. Fill the background with
      `(1.0, 0.5, 0.0)` and copy in only where roughness is non-zero, so unbaked texels are not
      fully occluded black.

   The traps, all of which fail quietly:
   ```

3. **Avoid the four quiet bake traps:** pass `use_clear` on the operator itself (it overrides the scene setting), bake each body with the others hidden,
   compare Blender RNA wrappers with `==` not `is`, and `image.save()` after each pass. Keep each MCP call short and check `min`, `mean` and `max` of the result.

4. **Bake over a key colour with a small margin.** Fill every target with something the bake cannot produce (pure green on a model with no green) and bake with
   `use_clear=False`: whatever is still the key is exactly what was never written. Keep `bake.margin` small; a wide margin writes one body's dilation over another's
   island (measured: 48 px put a neighbour's grey over 12.95 % of an island, 8 px over 0.01 %).

5. **Dilate the atlas.** Each empty texel takes the colour of its nearest baked one, so a mip only ever averages an island with more of itself:

   ```python
   # tools/model/dilate-atlas.py L50-L67
   def fill(path, mask):
       """Replaces everything outside *mask* with its nearest texel inside it."""
       image = Image.open(path)
       mode = image.mode
       pixels = np.asarray(image.convert("RGB"))

       step = mask.shape[0] // pixels.shape[0]
       m = mask[::step, ::step] if step > 1 else mask
       if m.shape[0] != pixels.shape[0]:
           raise SystemExit(f"error: {Path(path).name} is {pixels.shape[0]}px, "
                            f"which the {mask.shape[0]}px mask does not divide")

       # One pass: the nearest in-mask texel for every texel. In-mask ones map to themselves, so
       # baked pixels are copied through untouched and this can never damage them.
       _, (yi, xi) = ndimage.distance_transform_edt(~m, return_distances=True, return_indices=True)
       Image.fromarray(pixels[yi, xi], "RGB").convert(mode).save(path)
       return int((~m).sum()), int(m.size)

   ```

   Run it over the baked PNGs in one go (diffuse first, since the key mask comes from it, then normal and ORM, which share the UVs and margin):

   ```python
   # tools/model/dilate-atlas.py L69-L104
   def main(argv):
       if "--key" not in argv or len(argv) < 4:
           print(__doc__.strip(), file=sys.stderr)
           return 2

       at = argv.index("--key")
       key = parse_key(argv[at + 1])
       paths = [a for i, a in enumerate(argv[1:], 1) if i not in (at, at + 1)]

       first = np.asarray(Image.open(paths[0]).convert("RGB")).astype(np.int16)
       # Tolerant, because an 8-bit round trip moves a key by a unit or two.
       mask = np.abs(first - key).max(axis=2) > 8
       if not mask.any():
           raise SystemExit(f"error: {Path(paths[0]).name} is entirely the key colour -- "
                            "nothing was baked into it")
       print(f"  mask from key {tuple(int(v) for v in key)}: "
             f"{(~mask).mean() * 100:.1f}% of {Path(paths[0]).name} was never baked")

       for path in paths:
           filled, total = fill(path, mask)
           print(f"  {Path(path).name:34} {Image.open(path).size[0]:>5}px  "
                 f"filled {filled} of {total} texels")

       # Verify rather than assume: any key colour left means the fill propagated the key instead of
       # replacing it, which is the failure this script exists to have caught once already.
       after = np.asarray(Image.open(paths[0]).convert("RGB")).astype(np.int16)
       left = int((np.abs(after - key).max(axis=2) <= 8).sum())
       if left:
           print(f"  FAILED: {left} key-coloured texels survive the fill", file=sys.stderr)
           return 1

       print("  clean: no key colour survives, so every texel now holds real paint")
       return 0


   if __name__ == "__main__":
   ```

6. **Declare the material and use PNG in every slot** with `Category="Vessel"` (the Vessel cap in the graphics settings applies):

   ```xml
   <!-- src/KSACars/KSACarsAssets.xml L17-L24 -->
     <MeshAtlas Path="Meshes/KSACars_Buggy.glb" />

     <PbrMaterial Id="KSACars_Buggy_Material">
       <Diffuse Path="Textures/KSACars_Buggy_Diffuse.png" Category="Vessel" />
       <Normal Path="Textures/KSACars_Buggy_Normal.png" Category="Vessel" />
       <AoRoughMetal Path="Textures/KSACars_Buggy_PBR.png" Category="Vessel" />
       <Emissive Path="Textures/KSACars_Buggy_Emissive.png" Category="Vessel" />
     </PbrMaterial>
   ```

   The `Emissive` slot is what lights when the part's light switch is on; it is optional (the part path falls back to -1).

7. **Validate before launching.** The validator rule above, plus case-exact paths and matching Ids, belong in the deploy script ([Validate asset XML ids and paths before launching the game (offline)](../../validation/validate-asset-xml-references/overview.md)).
   Also run the mesh checks ([Check GLB meshes offline: z-fighting, degenerate UVs, names and bounds](../../validation/find-z-fighting-and-degenerate-uvs-in-glb/overview.md)).

## Complete example

- [tools/model/dilate-atlas.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/model/dilate-atlas.py), [.claude/skills/ksa-blender/SKILL.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/.claude/skills/ksa-blender/SKILL.md) (the full authoring skill)
- [src/KSACars/KSACarsAssets.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsAssets.xml#L17-L48) (material and subparts)
- [tools/model/dilate-atlas.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/model/dilate-atlas.py) (the same tool in KSAGolf)

## Variations and gotchas

- **Background is a colour you choose.** Left black it shows as dark speckle crawling over the paint at range, which cannot be reproduced anywhere but in game.
- **glTF's V runs from the top, Blender's from the bottom.** Compare exported UVs with a baked PNG using `v` directly, not `1 - v`, or the atlas reads upside down.
- **A mask rasterised from the UVs is wrong.** It over-covers, leaves unbaked texels inside the mask and a nearest-neighbour fill spreads them as a stain; the tool
  verifies its own output and exits non-zero if any key survives.
- **Several atlases and materials per mod are fine.** Core does it.
- **Recentring a mesh after the bake needs a rebake** when the material is built from object-space coordinates.
- **Same texture path in two materials of a part** (see approach 4).

## See also

- [Declare bare GltfFile and PbrMaterial assets and pair them by id in code](declare-gltf-and-pbr-material-assets-1.md), [Generate a flat-swatch palette atlas with a JSON swatch map](declare-gltf-and-pbr-material-assets-3.md), [Emit flat-colour textures from the same script that generates the geometry](declare-gltf-and-pbr-material-assets-4.md),
  [Export or generate a GLB that KSA loads correctly](../export-glb-from-blender-for-ksa/overview.md), [Check GLB meshes offline: z-fighting, degenerate UVs, names and bounds](../../validation/find-z-fighting-and-degenerate-uvs-in-glb/overview.md), [Add a part's art with its own GLB model and PBR textures](../add-a-part-model-with-glb-and-pbr-textures/overview.md)
- [Car part assets: GLB atlases, PBR materials and subparts](../../../../mods/ksacars/features/car-part-assets-glb-pbr.md), [Art and audio pipeline tooling](../../../../mods/ksacars/features/art-and-audio-pipeline-tooling.md)
