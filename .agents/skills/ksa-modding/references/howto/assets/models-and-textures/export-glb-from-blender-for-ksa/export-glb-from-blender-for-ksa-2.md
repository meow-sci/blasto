---
type: How-To
title: Verify the export contract against Core and keep a human sign-off before unwrapping
description: Author in a loop that stops before UV unwrapping for a human yes, state the contract table with Core's evidence, choose props in centimetres and parts in metres, purge orphan meshes and never export a primitive without a material
category: assets
group: models-and-textures
topic: export-glb-from-blender-for-ksa
approach: 2
difficulty: intermediate
prerequisites: [blender]
demonstrated_by: [mods/ksagolf/features/glb-authoring-and-mesh-checks]
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
tags: [glb, blender, export, contract, units, props, human-in-the-loop]
---

KSAGolf has fourteen GLBs (a ball, a putter, twelve course pieces) and no parts, authored in Blender driven from an AI session over MCP. It carries the rules that keep that art loadable and an authoring loop built around one cheap
human gate. This article is that loop and the contract table, plus the one rule that differs for non-part art: units.

Demonstrated by [GLB authoring pipeline and mesh checks](../../../../mods/ksagolf/features/glb-authoring-and-mesh-checks.md). The same contract with a pivot-centred part is [Export a part-space GLB with pivot-centred subparts and preview it headless](export-glb-from-blender-for-ksa-1.md).

## Background: how the game does it

The engine facts are the same as approach 1: meshes by name, no node transforms ([current/decomp/KSA/MeshAtlasFileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshAtlasFileReference.cs#L26-L45)), subparts for undecorated nodes ([current/decomp/KSA.GlbImport/PartAssetBundler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.GlbImport/PartAssetBundler.cs#L44-L62)).
Props that are not parts are different in one way: a character attachment is drawn through `CharacterAvatar.Core.Scale = 0.01`, so the same mesh is 100 times smaller than a part at the same unit. The mod's notes record that
Core's own attachments measure 80.6 glTF units (helmet) and 48.3 (MMU), and that a mesh at metre scale loads, registers, draws every frame, is invisible and logs nothing. The engine does not read the glTF scene graph
for renderables (`GltfPbrAssetRef.SceneGraph` is assigned and never read), so a scale left on the Blender object is discarded and must be baked into the vertices.

## Lifecycle and timing

Authoring and CI time. The GLB is read at `startup:asset-load`, turned into a GPU mesh when code first asks. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Run the authoring loop.** The skill's first rule is a stop sign:

   ```text
   # .claude/skills/ksa-blender/SKILL.md L13-L26
   > ## Stop before you bake
   >
   > **Ask the human to look at the geometry in Blender, and wait for a yes, before unwrapping,
   > baking or exporting.** Frame it for them with `jump_to_view3d_object_by_name`, say what you have
   > built and what is next, then stop.
   >
   > Ask them to *look*, do not send them pictures. They have the document open, so the model is
   > right there: orbitable, at any angle, lit however they like, and their view of it beats any
   > render you could hand over. Framing it in their viewport is the whole of your job here.
   >
   > The gate is not politeness. Everything downstream of the unwrap is welded to the geometry:
   > bodies sharing an atlas pack together, so changing one body afterwards forces a re-unwrap and a
   > re-bake of *all* of them. A joint two primitives share and a 2.5 mm recentre both cost what a
   > redesign costs, and both are free right up until the unwrap.
   ```

2. **State the contract as a table, with evidence.** The rules, verified by the mod against Core's 1054 atlas nodes, are: glTF binary with "+Y Up" off; transforms applied; node name equals mesh name; materials exported; a `_VM`
   twin and `_ColPrim_*` colliders for parts; PNG textures with ORM channel order. The table is in [.claude/skills/ksa-blender/SKILL.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/.claude/skills/ksa-blender/SKILL.md#L95-L110), and the coordinate system in
   [tools/model/README.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/model/README.md#L24-L31).

3. **Pick units by who draws the mesh.** Parts and course pieces are in metres; held props (drawn on a kitten's hand) in centimetres, with the scale baked into the vertices:

   ```text
   # docs/KSA-MODDING-NOTES.md L726-L733
   ### A character attachment is authored in centimetres, a part in metres

   The kitten is drawn
   through `CharacterAvatar.Core.Scale = 0.01`, and `GetBoneTransform` returns a bone matrix that
   already carries it — so a mesh exported in metres arrives a hundred times too small. Core's own
   attachments measure 80.6 glTF units (helmet) and 48.3 (MMU); a mesh at metre scale renders a
   hundredth of that and is buried in the fur: it loads, registers, draws every frame, is
   invisible, and puts nothing in any log.
   ```

4. **Name nodes after meshes, after purging orphans** (Blender appends `.001` to a name an orphan mesh owns), and **never export a primitive without a material**: a `<GltfFile>` built from a primitive with no material throws
   "Index was outside the bounds of the array", and the checker fails the mesh.

5. **Export, then check.** Run the mesh checker in CI and in the deploy script ([Check GLB meshes offline: z-fighting, degenerate UVs, names and bounds](../../validation/find-z-fighting-and-degenerate-uvs-in-glb/overview.md)), plus the XML validator ([Validate asset XML ids and paths before launching the game (offline)](../../validation/validate-asset-xml-references/overview.md)). CI runs `checkmesh.py --near-max 0` over every GLB in the release job.

6. **Ignore byte differences.** An unchanged model exports a permuted index buffer. Ask whether it changed with `checkmesh.py --compare` and revert the file if the surface is the same:

   ```text
   # tools/model/README.md L70-L82
   ## An export is not byte-reproducible

   Blender's glTF exporter does not emit triangles in a stable order, so exporting unchanged geometry
   produces identical positions, normals and UVs and a **permuted index buffer**. `git status` showing an
   atlas as modified means nothing on its own. Ask

   ```bash
   ./tools/model/checkmesh.py <new.glb> --compare <old.glb>
   ```

   which compares the surface itself — every triangle canonicalised and sorted — and **revert the atlas**
   if it reports the same geometry.

   ```

## Complete example

- [.claude/skills/ksa-blender/SKILL.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/.claude/skills/ksa-blender/SKILL.md), [tools/model/README.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/model/README.md), [docs/KSA-MODDING-NOTES.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/docs/KSA-MODDING-NOTES.md#L726-L742)
- [tools/model/checkmesh.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/model/checkmesh.py), [tools/validate-parts.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/validate-parts.py)

## Variations and gotchas

- **The `.blend` sources are not in the repository**, so the exported GLB is the only artefact and the checkers are the only gate on it.
- **Do not unwrap before the shape is approved.** Everything after the unwrap (atlas packing, baking) is welded to the shape.
- **An attachment's axes** are composed in a different order from the body's, so a right-sized attachment can still arrive rotated; the `<Rotation>` in the attachment XML corrects it.
- **One mesh, one primitive, one material slot** for held props.
- **Chiral pieces** need a separate mirrored GLB (a mirrored draw turns triangles inside out).

## See also

- [Export a part-space GLB with pivot-centred subparts and preview it headless](export-glb-from-blender-for-ksa-1.md), [Reframe hand-modelled assets and generate hard-surface parts with headless Blender](export-glb-from-blender-for-ksa-3.md), [Declare glTF meshes and PBR materials in asset XML and author their texture sets](../declare-gltf-and-pbr-material-assets/overview.md), [Attach a prop to a kitten's hand or another bone at runtime, building the prop mesh from mod assets](../../../kittens/appearance/attach-a-prop-to-a-kitten-bone/overview.md),
  [Check GLB meshes offline: z-fighting, degenerate UVs, names and bounds](../../validation/find-z-fighting-and-degenerate-uvs-in-glb/overview.md)
- [GLB authoring pipeline and mesh checks](../../../../mods/ksagolf/features/glb-authoring-and-mesh-checks.md)
