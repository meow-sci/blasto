---
type: How-To
title: Export a part-space GLB with pivot-centred subparts and preview it headless
description: Follow the export contract (Y-up off, part space, transforms applied, node name equal to mesh name, a _VM twin, each moving body recentred on its pivot) and render the exported file in headless Blender before declaring it
category: assets
group: models-and-textures
topic: export-glb-from-blender-for-ksa
approach: 1
difficulty: intermediate
prerequisites: [blender]
demonstrated_by: [mods/ksacars/features/art-and-audio-pipeline-tooling, mods/ksacars/features/car-part-assets-glb-pbr]
techniques: [glb-model]
hooks: [offline blender export]
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
tags: [glb, blender, export, contract, part-space, pivot, preview, meshview]
---

KSA reads a glTF atlas by mesh name and ignores node transforms, so what leaves Blender has to follow a contract. If it does not, the failures are quiet: a part
that renders nothing, a body on its side, a car three metres long and eight wide. KSACars wrote the contract down (it ships it as a Blender skill, `ksa-blender`), verified it against
all 44 Core atlases (1054 nodes) and added a headless render of the exported file so the export can be judged before it is declared. This article is that contract with the
engine evidence for each rule.

Demonstrated by [Art and audio pipeline tooling](../../../../mods/ksacars/features/art-and-audio-pipeline-tooling.md) and [Car part assets: GLB atlases, PBR materials and subparts](../../../../mods/ksacars/features/car-part-assets-glb-pbr.md). Textures: [Declare glTF meshes and PBR materials in asset XML and author their texture sets](../declare-gltf-and-pbr-material-assets/overview.md); declaring the result:
[Add a part's art with its own GLB model and PBR textures](../add-a-part-model-with-glb-and-pbr-textures/overview.md).

## Background: how the game does it

**Meshes are read by name; node transforms are never read.** `GltfLoader` parses the GLB; `MeshAtlasFileReference.DoLoad` walks `GltfJson.Meshes` and registers a `MeshReference` per mesh
with `Id = Meshes[i].Name`, so the node graph is not consulted:

```csharp
// current/decomp/KSA/MeshAtlasFileReference.cs L26-L45
	internal override void DoLoad()
	{
		using GltfLoader gltfLoader = new GltfLoader(base.ModPath);
		Material[] materials = gltfLoader.GltfJson.Materials;
		MaterialCount = ((materials == null) ? 1 : materials.Length);
		for (int i = 0; i < gltfLoader.GltfJson.Meshes.Length; i++)
		{
			MeshReference meshReference = new MeshReference();
			meshReference.Id = gltfLoader.GltfJson.Meshes[i].Name;
			meshReference.Simple = false;
			meshReference.Interleaved = true;
			meshReference.OnDataLoad(base.Mod);
			if (!ModLibrary.Register(meshReference))
			{
				Meshes.Add(ModLibrary.Get<MeshReference>(meshReference.Id));
				continue;
			}
			meshReference.Load(gltfLoader, i, !HostOnly);
			Meshes.Add(meshReference);
		}
	// ...
```

**The game's own bundler enforces the same shape.** For every atlas node whose name does not start with `_` and does not end with `_VM` it emits a subpart; it warns when a rendered
node carries a translation, rotation or scale ("subpart origins will be offset from the visual mesh locations"):

```csharp
// current/decomp/KSA.GlbImport/PartAssetBundler.cs L44-L62
			// ...
			Node[] array = nodes;
			foreach (Node node in array)
			{
				string text = node.Name ?? "";
				if (!text.StartsWith('_'))
				{
					flag |= WarnIfUnappliedTransform(setPrefix, node, gltfLoader);
				}
				if (!text.StartsWith('_') && !text.EndsWith("_VM"))
				{
					assetBundle.Assets.Add(SubPart(setPrefix, gltfLoader, node, nodes, meshes, names, materialId, set.HeatMap, glbCollisionAtlas));
				}
			}
			if (flag)
			{
				string value = setPrefix + "[WARNING] Mesh atlas '" + meshAtlasGlb2.Name + "' has unapplied mesh transforms. These mean that the subpart origins will be offset from the visual mesh locations, which will make positioning game data like rocket nozzles, tanks, and masses very challenging. This should be fixed in the source art assets by applying transforms to vertices.";
				Console.WriteLine(value);
			}
		}
```

Child nodes named `_glass*`, `_raytrace*`, `_raytraceShadowProxy*` and `_Col*` flag glass, ray tracing and colliders ([current/decomp/KSA.GlbImport/PartAssetBundler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.GlbImport/PartAssetBundler.cs#L273-L305)).

## Lifecycle and timing

Authoring time. In game the GLB is loaded by `FileReference.Load` in the parallel loader pass at `startup:asset-load` and meshes are bound to the GPU later ([Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).

## Steps

1. **State the contract before modelling.** From the ksa-blender skill, with the Core evidence:

   ```text
   # .claude/skills/ksa-blender/SKILL.md L99-L106
   What KSA reads out of a `.glb`, verified against all 44 Core atlases (1054 nodes).

   | Rule | Evidence |
   | --- | --- |
   | **No node transform on a rendered subpart.** Translation, rotation and scale baked into the vertices. | Of 1054 Core nodes the only ones carrying a transform are `_ColPrim_*` helpers and floating-point dust (1e-16, 3e-07). |
   | **Node name == mesh name.** | Holds for every rendered subpart in Core. |
   | **A `_VM` twin of every subpart.** | 419 of Core's 1054 nodes. The editor's preview variant; duplicating the geometry is fine at these poly counts. |
   | **`_ColPrim_*` carries the collider.** A unit box or cylinder with the volume in its node transform, never rendered, no UVs. | Core ships 58. Read `<Collider>` out of it rather than eyeballing one. |
   ```

2. **Model in part space from the start.** +X out of the surface the part attaches to, +Y along the host's long axis, +Z the host's right, origin on the mounting face. A model authored to another
   convention needs a rotation baked into the vertices; it cannot be parked in the XML, because a mod that moves a subpart rewrites its `Transform` every frame.

3. **Make node name equal mesh name, and defeat Blender's `.001`.** `ob.data.name = "X"` silently becomes `"X.001"` if an orphan mesh owns `"X"`. Purge orphans first, then name, then export:

   ```python
   # adapted from KSACars .claude/skills/ksa-blender/SKILL.md section 3 (Blender 4+/5 API names)
   import bpy
   bpy.ops.outliner.orphans_purge(do_local_ids=True, do_linked_ids=True, do_recursive=True)
   for ob in bpy.context.selected_objects:
       if ob.type == 'MESH':
           ob.data.name = ob.name
   ```

   (A purge deletes every unreferenced datablock including bake target images; give images `use_fake_user = True` first.)

4. **Apply transforms and set the exporter.** "+Y Up off" fixes the axes but does not clear object transforms; apply location, rotation and scale (`bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)`):

   ```text
   # .claude/skills/ksa-blender/SKILL.md L137-L143
   Format            glTF Binary (.glb)
   +Y Up             OFF          <-- the one that matters, see §4
   Apply Modifiers   ON
   Include           Selected Objects (or the collection)
   Data              Mesh: UVs, Normals.  No cameras, no lights
   Materials         Export, never Placeholder or None  <-- a <GltfFile> built with no material throws
   Compression       off
   ```

5. **Recentre each moving body on its pivot.** KSA turns a subpart about its own mesh origin. A wheel is exported on its hub, an arm on its chassis bushing, a coil-over on its top mount, the steering wheel on its boss;
   the offset to part space goes back in the `Part` XML `Transform` (see [Add a part's art with its own GLB model and PBR textures](../add-a-part-model-with-glb-and-pbr-textures/overview.md)).

6. **Duplicate each subpart as a `_VM` twin.** The editor's preview variant (419 of Core's 1054 nodes); duplicating geometry is fine at these poly counts. Add `_ColPrim_*` helper nodes for colliders
   if you want the game's bundler to read `<Collider>` from them.

7. **Preview the exported file, not the scene.** Render the `.glb` itself from several angles in headless Blender, node transforms and all, so a body that arrives on its side is seen before it ships.
   The script imports and lights the export:

   ```python
   # tools/model/preview-glb.py L1-L14
   """
   Renders any .glb from a few angles, so an authored asset can be judged before it is declared.

   An authored asset arrives as a file, and otherwise the only way to see it is to ship it and start the
   game. This imports and lights it instead.

   Run through tools/model/preview.sh rather than by hand -- Blender is a Windows binary and needs
   Windows paths for both the script and its output.

       blender --background --python preview-glb.py -- <in.glb> <outdir> [mesh-name-filter]

   It renders what is *in the file*, node transforms and all, which is deliberate: that is what the
   asset looks like to anything reading it naively, and a body that arrives on its side here will
   arrive on its side in game unless something bakes it.
   ```

   Blender is a Windows binary under WSL, so `preview.sh` converts the script and output paths:

   ```bash
   # tools/model/preview.sh L1-L13
   #!/usr/bin/env bash
   #
   # Renders an exported .glb from a few angles, so an authored asset can be judged before it is
   # declared.
   #
   #   ./tools/model/preview.sh src/KSACars/Meshes/KSACars_Eldorado.glb
   #   ./tools/model/preview.sh <atlas.glb> <outdir>          # somewhere other than the temp folder
   #   ./tools/model/preview.sh <atlas.glb> <outdir> <mesh>   # one body out of the atlas
   #
   # This exists because Blender is a Windows binary: it needs a Windows path for the script it runs
   # *and* for everywhere it writes, so calling preview-glb.py by hand means two wslpath conversions
   # and a backslash-quoting trap. The rendered files are reported back as WSL paths.
   #
   ```

8. **Run the checkers** ([Check GLB meshes offline: z-fighting, degenerate UVs, names and bounds](../../validation/find-z-fighting-and-degenerate-uvs-in-glb/overview.md)) and the XML validator ([Validate asset XML ids and paths before launching the game (offline)](../../validation/validate-asset-xml-references/overview.md)) after every export.

## Complete example

- [.claude/skills/ksa-blender/SKILL.md](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/.claude/skills/ksa-blender/SKILL.md) (the full skill), [tools/model/preview-glb.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/model/preview-glb.py), [tools/model/preview.sh](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/model/preview.sh)
- [tools/model/checkmesh.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/model/checkmesh.py) (verifies the name pairing after export)

## Variations and gotchas

- **The `.blend` is the source and is not in the repository** (KSACars keeps it elsewhere); the committed GLB is only checkable at the boundary, so the checkers are the gate.
- **A part that loads but renders nothing** is nearly always a name mismatch: `<Mesh Id>` must be the atlas name exactly.
- **Y-up on swaps Y and Z:** a vehicle exports three metres long and eight wide, and nothing warns.
- **Materials must be exported**, never Placeholder or None; a `<GltfFile>` built with no material throws.
- **An export is not byte-reproducible.** Blender permutes the index buffer; compare with `checkmesh.py --compare`.
- **Other tooling:** a headless MCP connection is only reachable from WSL with mirrored networking; not an engine matter.

## See also

- [Verify the export contract against Core and keep a human sign-off before unwrapping](export-glb-from-blender-for-ksa-2.md), [Reframe hand-modelled assets and generate hard-surface parts with headless Blender](export-glb-from-blender-for-ksa-3.md), [Write the GLB, textures, collider XML and C# table from one Python script (no Blender)](export-glb-from-blender-for-ksa-4.md),
  [Check GLB meshes offline: z-fighting, degenerate UVs, names and bounds](../../validation/find-z-fighting-and-degenerate-uvs-in-glb/overview.md), [Declare glTF meshes and PBR materials in asset XML and author their texture sets](../declare-gltf-and-pbr-material-assets/overview.md), [Define articulated subparts and mounts, animate and hide them from code](../../../vehicles-and-parts/parts/define-and-animate-subparts/overview.md)
- [Art and audio pipeline tooling](../../../../mods/ksacars/features/art-and-audio-pipeline-tooling.md)
