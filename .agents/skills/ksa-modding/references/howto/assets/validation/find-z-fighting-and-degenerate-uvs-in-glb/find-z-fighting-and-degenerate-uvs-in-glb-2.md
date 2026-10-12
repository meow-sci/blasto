---
type: How-To
title: Compare exports by surface fingerprint and sweep articulated assemblies for collisions
description: Compare two GLBs by an order-independent triangle fingerprint because the exporter permutes the index buffer, seed the box skin at generation time, and test convex primitives with SAT through every pose of an articulated mount
category: assets
group: validation
topic: find-z-fighting-and-degenerate-uvs-in-glb
approach: 2
difficulty: advanced
prerequisites: [blender]
demonstrated_by: [mods/ksarmory/features/blender-model-pipeline-and-checks]
techniques: [offline-asset-validation, glb-model]
hooks: [offline python checkmesh.py --compare]
lifecycle: [startup:asset-load]
game_types: [KSA.MeshAtlasFileReference, KSA.MeshReference, Brutal.GltfApi.GltfUtility, Brutal.GltfApi.GltfLoader]
tags: [glb, fingerprint, compare, sat, swept, articulated, collision, validation]
---

Two more questions the basic checker cannot answer. "Did my edit change anything?" is hard because Blender's glTF exporter does not emit triangles in a stable order, so an unchanged model exports a different file. "Do the pods sweep through the gun mounts?" is a question about two bodies at a pose neither file describes. KSArmory answers the first with an
order-independent surface fingerprint and `--compare`, and the second with `checkswept.py`, which tests convex primitives with the separating-axis theorem through every pose of an articulated mount. It also prevents the first problem class at generation time with a seeded box skin.

Demonstrated by [Headless Blender model generation and offline GLB checks](../../../../mods/ksarmory/features/blender-model-pipeline-and-checks.md). The basic checker is [Find coplanar faces and zero-area UVs with checkmesh.py and read bounds with meshinfo.py](find-z-fighting-and-degenerate-uvs-in-glb-1.md).

## Background: how the game does it

The engine turns a subpart about its own mesh origin and places bodies from the part XML; the atlas is a pose-invariant library (every body in its own pivot-local frame at its modelled reference pose). The mod rewrites subpart transforms every frame. So any pose is reconstructible from the atlas plus a table of pivots (`muzzles.json`), with no Blender and no game; the engine does not check
whether those bodies intersect ([current/decomp/KSA/MeshAtlasFileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshAtlasFileReference.cs#L26-L45)).

## Lifecycle and timing

Build and CI time (`check-all.sh`, `build.sh`). Nothing runs in game; the defects it prevents appear at `frame:render`. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Fingerprint the surface, not the bytes.** Every triangle becomes `((position, normal, uv) x3)`, rotated so its lowest-sorting corner comes first (which preserves winding while removing the arbitrary choice of starting vertex), and the triangles are sorted. Two fingerprints compare equal exactly when the meshes describe the same surface:

   ```python
   # tools/model/checkmesh.py L441-L467
   def fingerprint(gltf, binary, mesh):
       """A canonical, order-independent description of one mesh's surface.

       Every triangle becomes ((position, normal, uv) x3), rotated so its lowest-sorting corner
       comes first — which preserves winding while removing the arbitrary choice of starting
       vertex — and the triangles are then sorted. Two fingerprints compare equal exactly when the
       two meshes describe the same surface, whatever order the exporter happened to emit it in.
       """
       out = []
       for prim in mesh["primitives"]:
           if prim.get("mode", 4) != 4:
               continue
           attrs = prim["attributes"]
           pos = read_accessor(gltf, binary, attrs["POSITION"])
           nrm = read_accessor(gltf, binary, attrs["NORMAL"]) if "NORMAL" in attrs else None
           uvs = read_accessor(gltf, binary, attrs["TEXCOORD_0"]) if "TEXCOORD_0" in attrs else None
           idx = list(read_accessor(gltf, binary, prim["indices"])
                      if "indices" in prim else range(len(pos)))

           for i in range(0, len(idx) - 2, 3):
               tri = tuple((pos[v], nrm[v] if nrm else None, uvs[v] if uvs else None)
                           for v in idx[i:i + 3])
               start = tri.index(min(tri))
               out.append(tri[start:] + tri[:start])
       out.sort()
       return out

   ```

2. **Compare by name.** Report identical, changed (with a count) or present on one side only, plus node transform differences, and exit non-zero on a real difference. If it reports the same geometry, revert the file:

   ```python
   # tools/model/checkmesh.py L469-L509
   def compare(path_a, path_b):
       """Reports whether two atlases describe the same geometry.

       Blender's glTF exporter does not emit triangles in a stable order, so rebuilding the model
       from unchanged sources produces a *different file* — same positions, normals and UVs, only
       the index buffer permuted. Byte comparison therefore says "changed" on every rebuild and is
       useless for answering the question you actually have, which is whether an edit to
       pantsir.py moved anything. This compares the surface instead.
       """
       ga, ba = read_glb(path_a)
       gb, bb = read_glb(path_b)

       by_name_a = {m.get("name", "?"): m for m in ga["meshes"]}
       by_name_b = {m.get("name", "?"): m for m in gb["meshes"]}

       changed = node_transforms(ga, gb, path_a, path_b)
       for name in sorted(set(by_name_a) | set(by_name_b)):
           if name not in by_name_a or name not in by_name_b:
               side = "only in " + (path_a if name in by_name_a else path_b)
               print(f"  {name:<34} {side}")
               changed += 1
               continue

           fa = fingerprint(ga, ba, by_name_a[name])
           fb = fingerprint(gb, bb, by_name_b[name])
           if fa == fb:
               print(f"  {name:<34} identical ({len(fa)} triangles)")
           else:
               moved = sum(1 for x, y in zip(fa, fb) if x != y)
               note = (f"{len(fa)} vs {len(fb)} triangles" if len(fa) != len(fb)
                       else f"{moved} triangle(s) differ")
               print(f"  {name:<34} CHANGED — {note}")
               changed += 1

       print()
       if changed:
           print(f"{changed} difference(s) that are not triangle order")
           return 1
       print("same geometry — the files differ only in triangle order, which is expected")
       return 0

   ```

3. **Prevent z-fighting at generation time.** Grow every box by a skin plus a per-box, per-axis seeded jitter so abutting parts interpenetrate instead of sharing a plane. A uniform skin only separates faces that point at each other; faces pointing the same way stay exactly coplanar, and only a different inflation per box separates those
   ([Export or generate a GLB that KSA loads correctly](../../models-and-textures/export-glb-from-blender-for-ksa/overview.md) approach 3, `pantsir.py` `box()`).

4. **Sweep articulated assemblies.** The checker answers four questions: does every piece reach the chassis, does each articulated body touch what it hangs off, does any cover stand proud, and does any assembly pass through another anywhere in its travel. Bodies are split into convex primitives by connected component and tested with SAT, giving the metres one body would have to move to leave the other:

   ```python
   # tools/model/checkswept.py L1-L30
   #!/usr/bin/env python3
   """
   Checks the assembled vehicle for the geometry defects no other tool can see.

   Four questions, none of which the mesh, the pivots or a preview render can answer:

     1. does every piece reach the chassis, or has something come adrift?
     2. does each articulated body actually touch what it hangs off?
     3. does any cover stand proud of the thing it covers?
     4. does any assembly pass through another anywhere in its travel?

   checkmesh.py answers "does this body render cleanly"; validate-parts.py answers "do the three
   copies of each pivot agree". Neither can answer "do the pods sweep through the gun sponsons",
   because that is a question about two bodies at a pose neither file describes. Renders cannot
   answer it either: they show the poses someone thought to ask for.

   The atlas is a pose-invariant library — every body sits in its own pivot-local frame at its
   modelled reference pose — so any pose is reconstructible from the atlas plus muzzles.json, with
   no Blender and no game. Bodies are split into their convex primitives by connected component,
   which is exactly how pantsir.py builds them, and tested with SAT: the result is the metres one
   body would have to move to leave the other.

       ./tools/model/checkswept.py
       ./tools/model/checkswept.py --atlas <path> --step 2

   Exits non-zero if anything is adrift, standing proud, or passing through something else.
   """

   import argparse
   import json
   ```

   ```python
   # tools/model/checkswept.py L204-L233
   def sat(a, b):
       """Minimum translation distance for two convex polyhedra, or 0.0 if they are disjoint."""
       best = float("inf")
       axes = list(a.normals) + list(b.normals)
       seen = set()
       for ea in a.edges:
           for eb in b.edges:
               x = norm(cross(ea, eb))
               if x is None or (x := canon(x)) is None:
                   continue
               k = tuple(round(c, 3) for c in x)
               if k not in seen:
                   seen.add(k)
                   axes.append(x)

       for ax in axes:
           alo = ahi = dot(a.verts[0], ax)
           for v in a.verts[1:]:
               d = dot(v, ax)
               alo, ahi = min(alo, d), max(ahi, d)
           blo = bhi = dot(b.verts[0], ax)
           for v in b.verts[1:]:
               d = dot(v, ax)
               blo, bhi = min(blo, d), max(bhi, d)
           overlap = min(ahi, bhi) - max(alo, blo)
           if overlap <= 0.0:
               return 0.0
           best = min(best, overlap)
       return best

   ```

5. **Keep an allowance table honest.** Pairs that are structurally allowed to interpenetrate (a trunnion in its bearing) are listed with a reason and a depth; an entry says "this is a joint", not "this overlaps". Splitting a body out of an assembly is what makes a clash visible at all, because a body cannot intersect itself.

6. **Wire the checks into the build.** `build.sh` runs `checkmesh.py` after export and fails the build; `check-all.sh` runs `checkmesh.py`, `checkswept.py`, `palette.py --check` and the XML validator ([Validate asset XML ids and paths before launching the game (offline)](../validate-asset-xml-references/overview.md)).

## Complete example

- [tools/model/checkmesh.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/model/checkmesh.py), [tools/model/checkswept.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/model/checkswept.py), [tools/model/build.sh](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/model/build.sh)

## Variations and gotchas

- **Byte comparison says "changed" on every rebuild.** Use the fingerprint; this applies to any exporter that permutes the index buffer.
- **Hand-placed offsets are invisible to per-mesh checks.** Two subparts that are fine alone can overlap as the XML places them; the XML-level check for shared planes is in [Validate asset XML ids and paths before launching the game (offline)](../validate-asset-xml-references/overview.md) approach 2.
- **Renders show only poses someone thought to ask for.** Sweeping every pose is the point.
- **Cost.** SAT over every pair at many poses is slow in pure Python; the `--step` option controls the angular step.

## See also

- [Find coplanar faces and zero-area UVs with checkmesh.py and read bounds with meshinfo.py](find-z-fighting-and-degenerate-uvs-in-glb-1.md), [Parse the GLB JSON chunk in C# to validate mesh references](find-z-fighting-and-degenerate-uvs-in-glb-3.md), [Export or generate a GLB that KSA loads correctly](../../models-and-textures/export-glb-from-blender-for-ksa/overview.md),
  [Validate asset XML ids and paths before launching the game (offline)](../validate-asset-xml-references/overview.md)
- [Headless Blender model generation and offline GLB checks](../../../../mods/ksarmory/features/blender-model-pipeline-and-checks.md)
