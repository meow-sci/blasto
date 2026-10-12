---
type: Mod Feature
title: Bounded GLB 2.0 import into unregistered CPU-only meshes with frozen path and hash ids
description: Pebbles reads self-contained GLB files with its own bounded parser, bakes scene node and UV transforms, wraps the result in CPU-only MeshReference objects never added to ModLibrary, and freezes every library choice to an absolute-path plus SHA-256 mesh id
mod: unscience
submod: pebbles
category: assets
techniques: [glb-model, reflection]
hooks:
  - starmap StarMapAfterGui (via unscience ISubmod.RenderFloatingWindows and RenderContent; import and mesh selection)
  - starmap StarMapBeforeGui (via unscience ISubmod.Update; registry discovery and shared-library rescans)
  - reflection KSA.MeshReference.HostPrimitives and PrimitiveMaterialIds (private setters)
  - reflection KSA.ModLibrary.AllMeshes, AllFiles and AllGltfs (internal static collections)
lifecycle: [frame:draw-ui, frame:dispatch-solvers]
game_types:
  - KSA.MeshReference
  - RenderCore.MeshAsset
  - KSA.ModLibrary
  - KSA.SerializedCollection
  - KSA.Gltf2Reference
  - KSA.FileReference
  - KSA.TextureReference
  - Brutal.GltfApi.GltfLoader
  - Brutal.GltfApi.GltfUtility
  - Brutal.Collections.NativeStrideList
tags: [glb, gltf, mesh-import, content-hash, cache, reflection, budgets, shared-library]
---

Players can drop any self-contained `.glb` (for example a Blender export or a downloaded model) into
Pebbles or Sphinx and use it as clutter or as a placed static. The file is copied into the shared
`.unscience/glbs` library, parsed by a managed GLB reader that never touches the network or other
files, and turned into native CPU meshes that the clutter atlas and the Sphinx renderer can
consume. Nothing is registered in the game's asset library, so imports cannot collide with or
replace game content.[^pebbles-readme]

## How it works

**Container.** `GlbDocument.Parse` accepts only a complete GLB 2.0 container: magic `glTF`, version 2,
declared length equal to the byte count, 20 B to 128 MiB, a JSON chunk first (at most 8 MiB, parsed
with `MaxDepth = 64`), exactly one embedded `BIN` chunk, one buffer without a `uri`, 1-512 meshes,
and only whitelisted `extensionsRequired`. The SHA-256 of the whole file becomes its content hash.
Draco/meshopt geometry compression, unknown required extensions and external or data URIs are
rejected with targeted messages
([pebbles.lib/Models/GlbCompatibility.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/GlbCompatibility.cs#L30-L70)).[^glb-document]

**Geometry.** `ReadMesh` accepts 1-256 triangle-list primitives without primitive extensions or morph
targets. Accessors must be dense (no sparse), the declared type must match, strides are 1-252
bytes, and `offset + (count - 1) * stride + elementSize` must fit in the buffer view. Positions and
normals must be floats; UVs may be floats or normalized unsigned bytes/shorts; indices may be
8/16/32-bit or absent (generated). Missing normals are accumulated from face cross products. The UV
set is chosen by the material's primary texture and its `KHR_texture_transform` (scale, rotation,
offset) is baked into the coordinates, because the native clutter and static vertex formats carry a
single UV stream. Every selection is capped at 2048 primitives, 2 million vertices and 12 million
indices.[^glb-geometry][^glb-texture-mapping]

**Scene.** `ReadScene` ("Complete scene", mesh index -1) walks the default scene (or parentless nodes
if there is no scene), with at most 4096 nodes, depth 64 and cycle detection, rejects skins and node
extensions, composes TRS or affine matrices, transforms normals with the inverse transpose and
reverses triangle winding when a node matrix has a negative determinant (mirrored instances).
Individual meshes use raw local coordinates.[^glb-scene]

**Identity.** A mesh id is `pebbles-glb:<base64url(absolute path)>:<sha256>/mesh/<n>`.
`GlbIdentity.Parse` does no file I/O. Recipes, saves and Sphinx placements store this id, so the
exact bytes they were authored against are always known.[^glb-identity]

**Cache.** `GlbImportLibrary` keeps one `Source` per path+hash (at most 16 content versions, 8 million
retained vertices). A cache miss (for example after loading a save) resolves only the copied
filename inside the current shared library, never the stored absolute path, and refuses the file if
its hash differs: "The GLB file has changed. Import it again and explicitly select the new
version." `ResolveMesh` builds one `MeshAsset` per primitive (float3 position and normal, float2 UV,
`uint` indices) and a `MeshReference` whose `HostPrimitives` and `PrimitiveMaterialIds` are set
through their private setters by reflection.[^glb-import-library][^ksa-mesh-reference]

**Lazy library choices.** Mesh pickers list `unscience-glb-file:<name>` ids from the shared
`GlbLibrary` catalog (a name scan every two seconds, no decoding). `ClutterAssets.ResolveSelection`
must be called before a choice is stored: it imports the file and returns the frozen
complete-scene id. `ResolveMesh` throws for unfrozen ids.[^clutter-assets][^glb-library]

**Registry meshes.** The same picker also lists game meshes. `ClutterAssets.Refresh` reads the
internal static `ModLibrary.AllMeshes`, `AllFiles` and `AllGltfs` collections by reflection
(`SerializedCollection<T>.GetList()`), lists named meshes of each registered glTF by parsing its JSON
with `GltfUtility.LoadModel`, and loads a chosen one CPU-only with
`MeshReference.Load(loader, index, createDeviceMesh: false)`.[^ksa-mod-library][^ksa-gltf-utility][^ksa-serialized-collection]

**Material slots.** `GlbMaterialSlots` orders slots by (source key, material index) with ordinal
sorting, so material 0 of two different files never merges and the order survives reloads.[^glb-material-slots]

## Lifecycle and timing

- **Import** is synchronous on the main thread inside UI code: the file browser callback in
  `RenderFloatingWindows` and the **Load file** button in `RenderContent`, both under
  StarMapAfterGui (postfix of `Program.OnDrawUiViewports`, `frame:draw-ui`). Parsing a large file
  stalls that frame; there is no worker thread.[^pebbles-import]
- **Selection** (`SelectMesh`) resolves geometry and the CPU material recipes in the same UI call,
  so later steps hit the cache.
- **Apply** resolves meshes again inside the `Universe.ExecuteNextClothSolvers` prefix (cache hits
  in the normal case; a reload after a save is possible) when the private clutter graph is built.
- **Discovery** (`Refresh` every five seconds until the registry is found, library rescan every two
  seconds) runs in `ISubmod.Update` (StarMapBeforeGui or the hidden-HUD replay).[^pebbles-submod]
- **Release** (`ReleaseGlbImports`) is deferred to `Update` until every live clutter body and the
  Workshop preview have retired, because their GPU textures belong to the cache.

## Key code

| Path | Role |
|---|---|
| [pebbles.lib/Models/GlbDocument.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/GlbDocument.cs) | Bounded container parse and buffer views |
| [pebbles.lib/Models/GlbGeometry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/GlbGeometry.cs) | Accessor decoding, normals, UV selection, budgets |
| [pebbles.lib/Models/GlbScene.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/GlbScene.cs) | Node transform baking |
| [pebbles.lib/Models/GlbIdentity.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/GlbIdentity.cs) | Path + hash ids |
| [pebbles.lib/Assets/GlbImportLibrary.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Assets/GlbImportLibrary.cs) | Version cache, CPU `MeshReference` construction |
| [pebbles.lib/Assets/ClutterAssets.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Assets/ClutterAssets.cs) | Public facade: import, freeze, registry discovery, resolution |
| [pebbles.lib/Assets/ClutterGeometry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Assets/ClutterGeometry.cs) | Copies resolved meshes into transformed private clutter meshes with `uint` indices |
| [ksa-abstractions.lib/GlbLibrary.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/GlbLibrary.cs) | Shared catalog and lazy selection ids |
| [pebbles.tests/GlbChecks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.tests/GlbChecks.cs) | Managed parser and transform checks |

## Snippets

Container validation and the content hash:

```csharp
// pebbles.lib/Models/GlbDocument.cs L27-L61
    public static GlbDocument Parse(byte[] bytes)
    {
        if (bytes.Length is < 20 or > MaximumBytes || U32(bytes, 0) != 0x46546C67 || U32(bytes, 4) != 2 || U32(bytes, 8) != bytes.Length)
            throw new InvalidDataException("Expected a complete GLB 2.0 file.");
// ...
            int offset = 12;
            while (offset < bytes.Length)
            {
                if (bytes.Length - offset < 8) throw new InvalidDataException("Truncated GLB chunk.");
                uint length = U32(bytes, offset), type = U32(bytes, offset + 4); offset += 8;
                if (length % 4 != 0 || length > bytes.Length - offset) throw new InvalidDataException("Invalid GLB chunk length.");
                if (json == null && type != 0x4E4F534A) throw new InvalidDataException("GLB must start with JSON.");
                if (type == 0x4E4F534A)
                {
                    if (json != null || length > 8 * 1024 * 1024) throw new InvalidDataException("Invalid or oversized GLB JSON.");
                    json = JsonDocument.Parse(bytes.AsMemory(offset, (int)length), new JsonDocumentOptions { MaxDepth = 64 });
                }
                else if (type == 0x004E4942)
                {
                    if (binary != null) throw new InvalidDataException("Duplicate GLB binary chunk.");
                    binary = bytes.AsSpan(offset, (int)length).ToArray();
                }
                offset += (int)length;
            }
            if (json == null || binary == null) throw new InvalidDataException("GLB needs JSON and embedded binary geometry.");
// ...
            var buffers = Required(root, "buffers");
            if (buffers.GetArrayLength() != 1 || buffers[0].TryGetProperty("uri", out _) || Required(buffers[0], "byteLength").GetInt64() > binary.Length || Required(buffers[0], "byteLength").GetInt64() < binary.Length - 3)
                throw new InvalidDataException("Use a self-contained GLB with one embedded binary buffer.");
            if (Required(root, "meshes").GetArrayLength() is < 1 or > 512) throw new InvalidDataException("GLB needs 1–512 meshes.");
            GlbCompatibility.RequiredExtensions(root);
            return new(json, binary, Convert.ToHexString(SHA256.HashData(bytes)));
```

Baking a node transform, including mirrored winding:

```csharp
// pebbles.lib/Models/GlbScene.cs L69-L79
    private static GlbPrimitive Transform(GlbPrimitive primitive, Matrix4x4 matrix)
    {
        if (!Matrix4x4.Invert(matrix, out var inverse)) throw new InvalidDataException("GLB node transform is singular.");
        var positions = primitive.Positions.Select(p => Vector3.Transform(p, matrix)).ToArray();
        var normalMatrix = Matrix4x4.Transpose(inverse);
        var normals = primitive.Normals.Select(n => Vector3.Normalize(Vector3.TransformNormal(n, normalMatrix))).ToArray();
        if (positions.Concat(normals).Any(v => !float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))) throw new InvalidDataException("GLB transformed geometry is invalid.");
        var indices = (uint[])primitive.Indices.Clone();
        if (matrix.GetDeterminant() < 0) for (int i = 0; i < indices.Length; i += 3) (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
        return new(positions, normals, primitive.Uvs, indices, primitive.Material);
    }
```

Cache miss: resolve by copied filename, insist on the same hash; then build an unregistered mesh:

```csharp
// pebbles.lib/Assets/GlbImportLibrary.cs L61-L101
    private Source ResolveSource(string id)
    {
        var identity = GlbIdentity.Parse(id);
        if (_sources.TryGetValue(identity.SourceKey, out var source)) return source;
        // Only explicit import, preview refresh or Apply reaches this method, never draft restoration.
        // Save identities may carry a different machine's original library root. Resolve the
        // copied filename under this installation; the content hash still must match exactly.
        var document = GlbDocument.Load(GlbLibrary.Files.FullPath(identity.LibraryFileName));
        if (!document.Hash.Equals(identity.Hash, StringComparison.Ordinal))
        {
            document.Dispose();
            throw new InvalidOperationException("The GLB file has changed. Import it again and explicitly select the new version.");
        }
        return Adopt(document, identity);
    }
// ...
            mesh = new MeshReference { Id = id, PrimitiveCount = hosts.Count };
            typeof(MeshReference).GetProperty(nameof(MeshReference.HostPrimitives))!.SetValue(mesh, hosts.ToArray());
            typeof(MeshReference).GetProperty(nameof(MeshReference.PrimitiveMaterialIds))!.SetValue(mesh, primitives.Select(p => p.Material).ToArray());
            mesh.SetHash(); source.Meshes.Add(index, mesh); return mesh;
```

Copy into the library and freeze a lazy picker choice:

```csharp
// pebbles.lib/Assets/ClutterAssets.cs L17-L28
    public IReadOnlyList<GlbMeshOption> ImportGlb(string path)
    {
        string name = GlbLibrary.Files.Import(path, out string? error)
            ?? throw new IOException(error ?? "GLB import failed.");
        var options = _external.Import(GlbLibrary.Files.FullPath(name));
        RefreshSharedLibrary();
        return options;
    }
    /// <summary>Freeze a lazy library choice to an exact imported content id before storing a recipe.</summary>
    public string ResolveSelection(string id) => GlbLibrary.IsSelection(id)
        ? ImportGlb(GlbLibrary.Files.FullPath(GlbLibrary.FileName(id)))[0].Id : id;
    public static string MeshLabel(string id) => GlbLibrary.IsSelection(id) ? GlbLibrary.Label(id) : GlbIdentity.Label(id);
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `MeshReference.HostPrimitives`, `PrimitiveMaterialIds` (private setters), `PrimitiveCount`, `SetHash` | [current/decomp/KSA/MeshReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshReference.cs#L46-L49) | CPU-only mesh wrapper |
| `MeshReference.Load(GltfLoader, int, bool createDeviceMesh)` | [current/decomp/KSA/MeshReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshReference.cs#L82-L92) | Load registry glTF meshes without a GPU mesh |
| `MeshAsset` (`SetVertexList`, `SetIndexBuffer`, `PositionMinimum/Maximum`) | [current/decomp/RenderCore/MeshAsset.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/MeshAsset.cs) | Primitive storage |
| `ModLibrary.AllMeshes`, `AllFiles`, `AllGltfs` (internal static) | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L68-L80) | Read-only registry discovery |
| `SerializedCollection<T>.GetList()` | [current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L62) | Enumerate a registry collection |
| `GltfUtility.LoadModel(string)` | [current/decomp/Brutal.GltfApi/GltfUtility.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.GltfApi/GltfUtility.cs#L38) | List mesh names in registered glTFs |

## Pitfalls and version notes

- **Reflection on private setters.** If `MeshReference` ever makes `HostPrimitives` init-only or moves
  it to a backing field, the `GetProperty(...).SetValue` calls fail at import time, not at patch time.
- **Internal static collections.** `ModLibrary.AllMeshes`/`AllFiles`/`AllGltfs` are `internal`; the
  lookup throws `MissingFieldException` if renamed.
- **Index width.** Imported primitives always use `uint` indices. The clutter copy also normalises
  stock `ushort` indices to `uint` to avoid an index-width staging hazard the authors observed in
  `SimpleVkMeshAtlas` for mixed atlases.[^clutter-geometry]
- **Not supported** (rejected or approximated): skins, animation (not evaluated), morph targets,
  compressed geometry, vertex colors, authored tangents, external images. See
  [GLB material conversion](./pebbles-glb-material-conversion.md) for material fallbacks.
- **Portable saves** carry the absolute path only as metadata; another machine must have the same
  file (same hash) in its own `.unscience/glbs`.
- **Main-thread cost.** Parsing and hashing a 128 MiB file happens inside an ImGui callback; expect a
  hitch.
- Managed checks cover the parser, transforms and identities; native texture upload is out of their
  reach.[^glb-checks]

## See also

- [GLB material conversion and private texture upload](./pebbles-glb-material-conversion.md)
- [Shared file library](./lib-shared-file-library.md) (the copied `.unscience/glbs` catalog and browser)
- [Per-celestial ground clutter replacement](./pebbles-ground-clutter-replacement.md)
- [Sphinx GLB statics placement](./sphinx-glb-statics-placement.md) (second consumer)
- [Pebbles sub-mod](../submods/pebbles.md) and [mod overview](../overview.md)
- [Parse a GLB with a bounded reader into unregistered CPU meshes with path-plus-hash ids](../../../howto/assets/models-and-textures/import-glb-models-at-runtime/import-glb-models-at-runtime-1.md) - how-to article demonstrating this feature

[^pebbles-readme]: pebbles.lib/README.md
[^glb-document]: pebbles.lib/Models/GlbDocument.cs
[^glb-geometry]: pebbles.lib/Models/GlbGeometry.cs
[^glb-texture-mapping]: pebbles.lib/Models/GlbTextureMapping.cs
[^glb-scene]: pebbles.lib/Models/GlbScene.cs
[^glb-identity]: pebbles.lib/Models/GlbIdentity.cs
[^glb-import-library]: pebbles.lib/Assets/GlbImportLibrary.cs
[^ksa-mesh-reference]: KSA.MeshReference (decompiled, build 2026.10.10.5554)
[^clutter-assets]: pebbles.lib/Assets/ClutterAssets.cs
[^glb-library]: ksa-abstractions.lib/GlbLibrary.cs
[^ksa-mod-library]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-gltf-utility]: Brutal.GltfApi.GltfUtility (decompiled, build 2026.10.10.5554)
[^ksa-serialized-collection]: KSA.SerializedCollection (decompiled, build 2026.10.10.5554)
[^glb-material-slots]: pebbles.lib/Models/GlbMaterialSlots.cs
[^pebbles-import]: pebbles.lib/PebblesSubmod.Import.cs
[^pebbles-submod]: pebbles.lib/PebblesSubmod.cs
[^clutter-geometry]: pebbles.lib/Assets/ClutterGeometry.cs
[^glb-checks]: pebbles.tests/GlbChecks.cs
