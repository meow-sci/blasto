---
type: How-To
title: Write the GLB, textures, collider XML and C# table from one Python script (no Blender)
description: Generate a mesh atlas with a hand-rolled glTF binary writer in numpy, swatch UVs, box colliders approximating a slope as steps, XML spliced between markers and a generated C# table so mesh, colliders and code come from one layout
category: assets
group: models-and-textures
topic: export-glb-from-blender-for-ksa
approach: 4
difficulty: advanced
prerequisites: []
demonstrated_by: [mods/ksastructures/features/generated-deep-ramp]
techniques: [code-generation, glb-model]
hooks: [offline python office-deep.py]
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
tags: [glb, generation, numpy, collider, static-object, markers, no-blender]
---

KSAStructures' deep spiral ramp is not modelled in Blender. A script lays it out from a dozen constants and writes everything the game and the mod need to agree on: the visible mesh, an invisible shadow caster, the textures, the static sub-objects with their
box colliders, the lamp list and a JSON layout. It needs numpy and Pillow, not Blender. This article covers the whole no-Blender generation recipe: a minimal glTF writer, how faces get UV area, why a slope collides as steps, and how the XML is spliced between markers.

Demonstrated by [A generated spiral ramp - mesh, shadow caster, box colliders and lamps from one script](../../../../mods/ksastructures/features/generated-deep-ramp.md). The texture half is [Emit flat-colour textures from the same script that generates the geometry](../declare-gltf-and-pbr-material-assets/declare-gltf-and-pbr-material-assets-4.md); the splice technique is [Edit shadow copies with anchor-verified text patches and generated XML between markers](../../xml-patching/redirect-game-xml-loading/redirect-game-xml-loading-3.md).

## Background: how the game does it

`ColliderModule.Template` accepts `Box`, `Capsule`, `Cylinder`, `Sphere` and `ConvexHull` colliders, each with a `LocationAsmb` and a `Collider2Asmb` XYZ-radian rotation ([current/decomp/KSA/ColliderModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ColliderModule.cs#L12-L18), [current/decomp/KSA/ColliderTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ColliderTemplate.cs#L7-L16)), and `StaticObject.BuildCollisionShape` adds every collider of the object and of its sub-objects to one Bepu `BigCompound` ([current/decomp/KSA/StaticObject.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObject.cs#L175-L219)). KSAStructures nevertheless emits only axis-aligned boxes (its script states "everything is axis-aligned, because a static object's colliders are"; the engine code above does not show such a limit, so treat it as the mod's own design choice), which is why a sloped leg is approximated by steps. `StaticObjectTemplate` lists `PartModel`, `Collider`, `SubObject` and footprint elements ([current/decomp/KSA/StaticObjectTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObjectTemplate.cs#L7-L28)). The mesh side is the usual contract: meshes by name and no node transforms
([current/decomp/KSA/MeshAtlasFileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshAtlasFileReference.cs#L26-L45)), and every face needs real UV area so the tangent frame is defined ([Check GLB meshes offline: z-fighting, degenerate UVs, names and bounds](../../validation/find-z-fighting-and-degenerate-uvs-in-glb/overview.md)).

## Lifecycle and timing

Build time only. The outputs load as ordinary assets at `startup:asset-load` (the XML is listed in `mod.toml`); the C# lamp table is compiled into the mod. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Describe the layout as constants** (shaft, core, turns, legs, depth, door, pits) and a frame (x up, y east, z north). The script header states what it writes:

   ```python
   # tools/model/office-deep.py L1-L14
   #!/usr/bin/env python3
   """The office's deep ramp and hall, generated: a square spiral road down a shaft into a hall.

   Writes the mesh atlas, its flat-colour textures, the shadow caster, the static sub-objects with their
   box colliders into KSAStructuresOffice.xml between its markers, and the layout the code and the garage's
   shader pass share. Everything is axis-aligned, because a static object's colliders are.

       ./tools/model/office-deep.py            # from the repository root

   The frame is the building's: x up from the levelled ground, y east, z north.
   """
   import json
   import struct
   import sys
   ```

2. **Make one `solid()` call feed three outputs:** the drawn mesh (inflated by a per-body skin of a few millimetres so two bodies meeting on one plane do not z-fight), an axis-aligned collider box, and an inset box for the shadow-caster mesh:

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

3. **Turn ramps into stairs for physics.** A slope is drawn as one prism but collides as 25 stepped boxes (730 colliders in the generated XML); the steps sit under the slab:

   ```python
   # tools/model/office-deep.py L164-L175
           # Colliders in steps, since a collider is a box: 8 cm a step.
           steps = 25
           along_y = name in ("N", "S")
           a, b = (rect[0], rect[1]) if along_y else (rect[2], rect[3])
           first, last = (top[0], top[1]) if along_y else (top[0], top[3])
           for s in range(steps):
               f0, f1 = s / steps, (s + 1) / steps
               level = first + (last - first) * (f0 + f1) / 2
               ya, yb = (a + (b - a) * f0, a + (b - a) * f1) if along_y else (rect[0], rect[1])
               za, zb = (rect[2], rect[3]) if along_y else (a + (b - a) * f0, a + (b - a) * f1)
               boxes.append((f"Leg{name}{k}_{s}", level - SLAB, level, ya, yb, za, zb))
               n += 1
   ```

4. **Write the GLB by hand.** Pack positions, normals, UVs and indices into one binary buffer, write one node and one mesh per named mesh (node name equals mesh name), pad both chunks to four bytes, and write the header. With `material=True` the primitive gets
   a material slot (needed for any mesh built into a renderable):

   ```python
   # tools/model/office-deep.py L230-L261
   def glb(path, meshes, material=False):
       blob = bytearray()
       views, accessors, gl_meshes, nodes = [], [], [], []

       def view(data, target=None):
           while len(blob) % 4:
               blob.append(0)
           v = {"buffer": 0, "byteOffset": len(blob), "byteLength": len(data)}
           if target:
               v["target"] = target
           blob.extend(data)
           views.append(v)
           return len(views) - 1

       for name, m in meshes:
           p = np.asarray(m.p, dtype=np.float32)
           nm = np.asarray(m.n, dtype=np.float32)
           uv = np.asarray(m.uv, dtype=np.float32)
           idx = np.asarray(m.i, dtype=np.uint32)
           base = len(accessors)
           accessors += [
               {"bufferView": view(p.tobytes(), 34962), "componentType": 5126, "count": len(p), "type": "VEC3",
                "min": p.min(0).tolist(), "max": p.max(0).tolist()},
               {"bufferView": view(nm.tobytes(), 34962), "componentType": 5126, "count": len(nm), "type": "VEC3"},
               {"bufferView": view(uv.tobytes(), 34962), "componentType": 5126, "count": len(uv), "type": "VEC2"},
               {"bufferView": view(idx.tobytes(), 34963), "componentType": 5125, "count": len(idx), "type": "SCALAR"},
           ]
           primitive = {"attributes": {"POSITION": base, "NORMAL": base + 1, "TEXCOORD_0": base + 2}, "indices": base + 3}
           if material:
               primitive["material"] = 0
           gl_meshes.append({"name": name, "primitives": [primitive]})
           nodes.append({"name": name, "mesh": len(gl_meshes) - 1})
   ```

   then the document, the padding and the binary header:

   ```python
   # tools/model/office-deep.py L263-L277
       doc = {"asset": {"version": "2.0", "generator": "office-deep.py"}, "scene": 0,
              "scenes": [{"nodes": list(range(len(nodes)))}], "nodes": nodes, "meshes": gl_meshes,
              "accessors": accessors, "bufferViews": views, "buffers": [{"byteLength": len(blob)}]}
       if material:
           doc["materials"] = [{"name": "caster", "pbrMetallicRoughness": {"baseColorFactor": [0.5, 0.5, 0.5, 1.0]}}]

       text = json.dumps(doc, separators=(",", ":")).encode()
       text += b" " * (-len(text) % 4)
       while len(blob) % 4:
           blob.append(0)
       with open(path, "wb") as f:
           f.write(struct.pack("<III", 0x46546C67, 2, 28 + len(text) + len(blob)))
           f.write(struct.pack("<II", len(text), 0x4E4F534A) + text)
           f.write(struct.pack("<II", len(blob), 0x004E4942) + bytes(blob))

   ```

5. **Call the writer for each atlas:**

   ```python
   # tools/model/office-deep.py L279-L280
   glb(ROOT / "Meshes" / "KSAOfficeDeep.glb", [("KSAOffice_Deep", structure), ("KSAOffice_DeepTop", cover)])
   glb(ROOT / "Meshes" / "KSAOffice_DeepCaster.glb", [("KSAOffice_DeepCaster", caster)], material=True)
   ```

6. **Splice the generated XML between markers** and write the generated C# and JSON ([Edit shadow copies with anchor-verified text patches and generated XML between markers](../../xml-patching/redirect-game-xml-loading/redirect-game-xml-loading-3.md)):

   ```python
   # tools/model/office-deep.py L319-L325
   xml_path = ROOT / "KSAStructuresOffice.xml"
   xml = xml_path.read_text()
   begin, end = "  <!-- office-deep.py: begin -->\n", "  <!-- office-deep.py: end -->\n"
   if begin not in xml:
       sys.exit("KSAStructuresOffice.xml has no office-deep.py markers")
   xml = xml[:xml.index(begin) + len(begin)] + "\n".join(lines) + "\n" + xml[xml.index(end):]
   xml_path.write_text(xml)
   ```

7. **Run the offline validators** (`validate-parts.py`, `checkmesh.py`) in the deploy script, since nothing in the engine reports a bad mesh reference.

## Complete example

- [tools/model/office-deep.py](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/tools/model/office-deep.py) (whole script), [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L37-L50) (generated block)
- [src/KSAStructures/Sim/OfficeDeepLayout.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Sim/OfficeDeepLayout.cs) (generated C# table)

## Variations and gotchas

- **This script emits axis-aligned boxes only.** There is no generated sloped collider here; the steps approximate it. The engine can also place rotated boxes, capsules, cylinders, spheres and convex hulls (see the background), which this mod did not try for slopes.
- **Constants not regenerated** (the C# restatement of shaft bounds, shader copies, the garage's own lamps) must be updated by hand when the layout changes.
- **Each quad gets its own corner of a 4 by 4 swatch atlas**, so every triangle has UV area.
- **The GLB writer is minimal on purpose** (no animations, no multiple primitives per mesh); extend it only as the engine needs.
- **An export is a function of the script.** Commit script and outputs together so a diff shows what changed.

## See also

- [Export a part-space GLB with pivot-centred subparts and preview it headless](export-glb-from-blender-for-ksa-1.md), [Reframe hand-modelled assets and generate hard-surface parts with headless Blender](export-glb-from-blender-for-ksa-3.md), [Emit flat-colour textures from the same script that generates the geometry](../declare-gltf-and-pbr-material-assets/declare-gltf-and-pbr-material-assets-4.md), [Edit shadow copies with anchor-verified text patches and generated XML between markers](../../xml-patching/redirect-game-xml-loading/redirect-game-xml-loading-3.md),
  [Place a static object on a planet surface at a latitude and longitude, and keep it anchored](../../../celestials/terrain/place-static-objects-on-a-planet-surface/overview.md), [Add terrain decals from code and flatten or dig the ground under a custom site](../../../celestials/terrain/add-terrain-decals-and-dig-a-site/overview.md)
- [A generated spiral ramp - mesh, shadow caster, box colliders and lamps from one script](../../../../mods/ksastructures/features/generated-deep-ramp.md)
