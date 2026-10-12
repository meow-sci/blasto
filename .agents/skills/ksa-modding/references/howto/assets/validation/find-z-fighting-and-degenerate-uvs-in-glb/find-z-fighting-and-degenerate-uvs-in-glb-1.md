---
type: How-To
title: Find coplanar faces and zero-area UVs with checkmesh.py and read bounds with meshinfo.py
description: Pure Python readers of the GLB binary that reduce triangles to quantised sign-canonical planes, measure UV area, check node and mesh name pairing and missing materials, run in CI, and list mesh bounds from accessor min and max
category: assets
group: validation
topic: find-z-fighting-and-degenerate-uvs-in-glb
approach: 1
difficulty: intermediate
prerequisites: []
demonstrated_by:
  - mods/ksagolf/features/glb-authoring-and-mesh-checks
  - mods/ksacars/features/art-and-audio-pipeline-tooling
  - mods/ksarmory/features/blender-model-pipeline-and-checks
techniques: [offline-asset-validation, glb-model]
hooks: [offline python checkmesh.py]
lifecycle: [startup:asset-load]
game_types: [KSA.MeshAtlasFileReference, KSA.MeshReference, Brutal.GltfApi.GltfUtility, Brutal.GltfApi.GltfLoader]
tags: [glb, z-fighting, coplanar, uv, tangent, validation, ci, meshinfo]
---

Two defects look identical in game (flickering white speckle crawling over a part) and never show in a Blender preview. One is coplanar faces from different directions: the depth buffer has no basis to choose between them. The other is a triangle with zero UV area: the renderer builds a tangent from the UV derivative, gets a zero-length
vector, and its normalisation is NaN; NaN survives being multiplied by zero, so even a flat normal map cannot save the shading. This article shows how KSAGolf, KSACars and KSArmory find both with pure Python readers of the GLB binary, no Blender and no game, and run them in CI and in deploy scripts. It also covers reading
mesh names and bounds from the GLB JSON chunk.

Demonstrated by [GLB authoring pipeline and mesh checks](../../../../mods/ksagolf/features/glb-authoring-and-mesh-checks.md), [Art and audio pipeline tooling](../../../../mods/ksacars/features/art-and-audio-pipeline-tooling.md) and [Headless Blender model generation and offline GLB checks](../../../../mods/ksarmory/features/blender-model-pipeline-and-checks.md).

## Background: how the game does it

The game loads a GLB through `GltfLoader`/`GltfUtility` and registers a mesh per glTF mesh name, with no check for degenerate geometry or coplanar surfaces. `MeshAtlasFileReference.DoLoad` uses the mesh name as the `Id` and ignores nodes ([current/decomp/KSA/MeshAtlasFileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshAtlasFileReference.cs#L26-L45)); the part model then looks the mesh up by that name, so a node whose mesh has a
different name resolves to nothing (invisible, no log). A primitive with no material makes a `<GltfFile>`-built renderable throw "Index was outside the bounds of the array". Nothing in the engine validates any of this, so the only gate is a script on the exported file.

## Lifecycle and timing

Authoring time, CI and the deploy script. The defects occur in game at render time (`frame:render`), which is exactly why they must be caught before. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Read the GLB without a library.** A GLB is a header, a JSON chunk and a binary chunk; accessors give typed arrays:

   ```python
   # tools/model/checkmesh.py L50-L73
   def read_glb(path):
       """Returns (gltf json, binary chunk)."""
       with open(path, "rb") as fh:
           magic, version, _ = struct.unpack("<III", fh.read(12))
           if magic != 0x46546C67:
               raise ValueError(f"{path} is not a GLB")
           if version != 2:
               raise ValueError(f"unsupported glTF version {version}")

           gltf = binary = None
           while True:
               header = fh.read(8)
               if len(header) < 8:
                   break
               length, kind = struct.unpack("<II", header)
               payload = fh.read(length)
               if kind == 0x4E4F534A:
                   gltf = json.loads(payload.decode("utf-8"))
               elif kind == 0x004E4942:
                   binary = payload
       if gltf is None:
           raise ValueError("no JSON chunk")
       return gltf, binary

   ```

   ```python
   # tools/model/checkmesh.py L75-L91
   def read_accessor(gltf, binary, index):
       """Decodes one accessor into a list of tuples (or scalars)."""
       acc = gltf["accessors"][index]
       view = gltf["bufferViews"][acc["bufferView"]]
       fmt = COMPONENT[acc["componentType"]]
       n = COUNT[acc["type"]]
       size = struct.calcsize(fmt) * n
       stride = view.get("byteStride") or size
       base = view.get("byteOffset", 0) + acc.get("byteOffset", 0)

       out = []
       for i in range(acc["count"]):
           chunk = binary[base + i * stride: base + i * stride + size]
           values = struct.unpack("<" + fmt * n, chunk)
           out.append(values[0] if n == 1 else values)
       return out

   ```

2. **Test UV area per triangle.** A triangle with UV area below 1e-12 has no usable tangent frame:

   ```python
   # tools/model/checkmesh.py L113-L139
   def degenerate_uvs(gltf, binary, mesh):
       """Counts triangles whose UV area is zero, and the world area they cover.

       A zero-area UV triangle has no usable tangent frame. Renderers that build one from the UV
       derivative get a zero-length tangent; normalizing it yields NaN, and NaN survives being
       multiplied by zero, so even a flat normal map cannot save the shading. The symptom is
       flickering white speckle in game, invisible in any DCC preview.
       """
       bad = 0
       bad_area = 0.0
       total = 0
       missing = False

       for tri, uv in triangles(gltf, binary, mesh, with_uv=True):
           total += 1
           if uv is None:
               missing = True
               continue
           (u0, v0), (u1, v1), (u2, v2) = uv
           uv_area = abs((u1 - u0) * (v2 - v0) - (u2 - u0) * (v1 - v0)) / 2.0
           if uv_area < 1e-12:
               bad += 1
               _, _, area = plane_of(*tri)
               bad_area += area

       return bad, bad_area, total, missing

   ```

3. **Reduce every triangle to a sign-canonical plane**, so a face and its back-to-back neighbour share one key, and bucket by quantised normal and offset:

   ```python
   # tools/model/checkmesh.py L141-L164
   def plane_of(a, b, c):
       """Unit normal and offset, with the sign canonicalised so opposed faces share a key."""
       u = (b[0] - a[0], b[1] - a[1], b[2] - a[2])
       v = (c[0] - a[0], c[1] - a[1], c[2] - a[2])
       n = (u[1] * v[2] - u[2] * v[1],
            u[2] * v[0] - u[0] * v[2],
            u[0] * v[1] - u[1] * v[0])
       length = math.sqrt(n[0] ** 2 + n[1] ** 2 + n[2] ** 2)
       if length < 1e-12:
           return None, 0.0, 0.0
       n = (n[0] / length, n[1] / length, n[2] / length)
       area = length / 2.0

       # Flip so the first significant component is positive; a face and its back-to-back
       # neighbour then land on the same key instead of two mirrored ones.
       flip = 1.0
       for comp in n:
           if abs(comp) > 1e-9:
               flip = -1.0 if comp < 0 else 1.0
               break
       n = (n[0] * flip, n[1] * flip, n[2] * flip)
       d = n[0] * a[0] + n[1] * a[1] + n[2] * a[2]
       return n, d, area

   ```

4. **Find exact coplanar overlaps from different faces** (planes holding triangles from more than one direction, with overlapping extents; vertex-sharing triangles are the same surface and are skipped; overlaps below a threshold are noise):

   ```python
   # tools/model/checkmesh.py L259-L268
   def coplanar_overlaps(gltf, binary, mesh):
       """Coplanar triangles from different primitives that actually overlap on screen.

       The obvious test — "does this plane hold faces pointing both ways" — misses the case that
       matters most here. Two boxes whose outer faces land on the same plane point the *same* way,
       and fight exactly as hard. So this projects each plane's triangles into 2D and looks for
       real overlap, skipping pairs that share vertices (those are the two halves of one quad, or
       a fan, not a conflict).
       """
       return plane_conflicts([(a, b, c, None) for a, b, c in triangles(gltf, binary, mesh)])
   ```

5. **Add the near-coplanar advisory.** Two surfaces 0.3 to 4 mm apart are indistinguishable to the depth buffer once the camera is far; the band's upper bound sits under the modelling skin, so deliberate interpenetration is not flagged. For an authored mesh with no skin, turn the advisory off but keep the two hard checks:

   `./tools/model/checkmesh.py <atlas.glb> --near-max 0`

6. **Check the name pairing and the materials.** A node whose mesh has another name renders nothing; a primitive without a material breaks the renderable; a missing `TEXCOORD_0` means the part will not texture:

   ```python
   # tools/model/checkmesh.py L609-L628
   def unpaired_names(gltf):
       """Nodes whose mesh is not named after them, which KSA renders as nothing.

       Blender will not say when it refuses a name: `ob.data.name = "X"` silently becomes "X.001" if
       an orphaned mesh already holds "X", and deleting an object leaves exactly such an orphan. The
       export then writes a node "X" drawing a mesh "X.001", the asset XML's <Mesh Id="X"> resolves
       to nothing, and the part loads with that body missing and nothing in any log.
       """
       meshes = gltf.get("meshes", [])
       bad = []
       for node in gltf.get("nodes", []):
           index = node.get("mesh")
           if index is None or index >= len(meshes):
               continue
           node_name = node.get("name", "?")
           mesh_name = meshes[index].get("name", "?")
           if node_name != mesh_name:
               bad.append((node_name, mesh_name))
       return bad

   ```

   ```python
   # tools/model/checkmesh.py L630-L670
   def check_atlas(path, only, header=False):
       """Reports one atlas and returns its problem count.

       Taking several paths matters more than it sounds: check-all.sh hands this every authored
       atlas at once, and a main() that read argv[0] alone would check the first and pass the rest
       in silence -- which is worse than not checking them, because the run says "clean".
       """
       gltf, binary = read_glb(path)
       total = 0
       if header:
           print(f"\n--- {path}")

       for node_name, mesh_name in unpaired_names(gltf):
           print(f"\nnode {node_name}: draws mesh {mesh_name} — the names must match, or "
                 f"<Mesh Id=\"{node_name}\"> resolves to nothing and the body is invisible")
           total += 1

       for mesh in gltf["meshes"]:
           name = mesh.get("name", "?")
           if name.endswith("_VM"):
               continue                        # same geometry as its parent; nothing new to say
           if name.startswith("_ColPrim"):
               # A collider primitive, which Core ships 58 of: a box or cylinder carrying the part's
               # collision volume as a node transform. It is never rendered, so it has no UVs and
               # every check here would report it -- starting with "NO TEXCOORD_0".
               continue
           if only and name != only:
               continue

           # A <GltfFile> renderable indexes its primitives' materials: a primitive naming none throws
           # "Index was outside the bounds of the array" when the mod builds it, and nothing is drawn.
           # Blender writes none when exported with Materials set to Placeholder or None.
           unnamed = sum(1 for prim in mesh.get("primitives", []) if "material" not in prim)
           if unnamed:
               print(f"\n{name}: {unnamed} primitive(s) with NO MATERIAL — export with Materials: Export")
               total += 1

           bad, bad_area, tris, missing = degenerate_uvs(gltf, binary, mesh)
           if missing:
               print(f"\n{name}: NO TEXCOORD_0 — the part will not texture")
               total += 1
   ```

7. **Exit non-zero and run it for every atlas in CI.** Take several paths, so one command checks the whole release; the check script must not read `argv[0]` alone and pass the rest in silence:

   ```python
   # tools/model/checkmesh.py L597-L606
       total = 0
       for path in args:
           total += check_atlas(path, only, header=len(args) > 1)

       print()
       if total:
           print(f"FOUND {total} problem(s) — these are what make the part render wrong in game")
           return 1
       print("clean: names pair, every triangle has UV area, and no two faces share a plane")
       return 0
   ```

8. **List mesh names and bounds from the JSON chunk alone** so you lay out parts against facts. `POSITION` accessors carry `min` and `max`, which gives exact bounds without decoding vertices:

   ```python
   # tools/meshinfo.py L18-L58
   def read_glb_json(path):
       """Returns the JSON chunk of a .glb as a dict."""
       with open(path, "rb") as fh:
           magic, version, _total = struct.unpack("<III", fh.read(12))
           if magic != 0x46546C67:  # 'glTF'
               raise ValueError(f"{path} is not a GLB (bad magic)")
           if version != 2:
               raise ValueError(f"unsupported glTF version {version}")

           while True:
               header = fh.read(8)
               if len(header) < 8:
                   raise ValueError("no JSON chunk found")
               length, kind = struct.unpack("<II", header)
               payload = fh.read(length)
               if kind == 0x4E4F534A:  # 'JSON'
                   return json.loads(payload.decode("utf-8"))


   def mesh_bounds(gltf, mesh):
       """Union of the POSITION accessor bounds across a mesh's primitives."""
       lo = [float("inf")] * 3
       hi = [float("-inf")] * 3
       found = False

       for prim in mesh.get("primitives", []):
           idx = prim.get("attributes", {}).get("POSITION")
           if idx is None:
               continue
           accessor = gltf["accessors"][idx]
           amin, amax = accessor.get("min"), accessor.get("max")
           if not amin or not amax:
               continue
           found = True
           for axis in range(3):
               lo[axis] = min(lo[axis], amin[axis])
               hi[axis] = max(hi[axis], amax[axis])

       return (lo, hi) if found else (None, None)


   ```

   Run `./tools/meshinfo.py <atlas.glb> Tube` to filter by name; the same reader works on Core's atlases in the install.

## Complete example

- [tools/model/checkmesh.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/model/checkmesh.py), [tools/meshinfo.py](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/meshinfo.py)
- The same tools in KSACars: [tools/model/checkmesh.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/model/checkmesh.py), [tools/meshinfo.py](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/tools/meshinfo.py) (run by `deploy.sh` and `check-all.sh`)

## Variations and gotchas

- **Units.** Thresholds are in metres; a character attachment authored in centimetres needs `--units-per-metre 100`, which scales the thresholds, not the mesh.
- **Skip helpers.** `_VM` meshes have the same geometry as their parent; `_ColPrim_*` colliders are never rendered and have no UVs. The checker skips both.
- **Blender's preview hides it.** The preview material has no normal map wired in, so neither defect shows there.
- **Quantisation matters.** Planes are bucketed at 0.2 mm (`PLANE_QUANT = 2.0e-4`), far finer than an 8 mm modelling skin, so deliberately separated surfaces land in different buckets. (The script's own comment calls this "coarser"; 0.2 mm is the finer resolution.)
- **Fix at the source.** Give every face real UV area; inflate generated boxes by a per-box seeded skin ([Export or generate a GLB that KSA loads correctly](../../models-and-textures/export-glb-from-blender-for-ksa/overview.md), [Declare glTF meshes and PBR materials in asset XML and author their texture sets](../../models-and-textures/declare-gltf-and-pbr-material-assets/overview.md)).
- **KSAGolf's checker does a different job in CI** than the in-game tests: it cannot see what KSA does with transforms, which still needs a look in game.

## See also

- [Compare exports by surface fingerprint and sweep articulated assemblies for collisions](find-z-fighting-and-degenerate-uvs-in-glb-2.md), [Parse the GLB JSON chunk in C# to validate mesh references](find-z-fighting-and-degenerate-uvs-in-glb-3.md), [Export or generate a GLB that KSA loads correctly](../../models-and-textures/export-glb-from-blender-for-ksa/overview.md), [Validate asset XML ids and paths before launching the game (offline)](../validate-asset-xml-references/overview.md)
- [GLB authoring pipeline and mesh checks](../../../../mods/ksagolf/features/glb-authoring-and-mesh-checks.md), [Art and audio pipeline tooling](../../../../mods/ksacars/features/art-and-audio-pipeline-tooling.md)
