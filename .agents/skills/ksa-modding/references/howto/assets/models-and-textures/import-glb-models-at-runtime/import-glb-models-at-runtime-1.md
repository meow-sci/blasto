---
type: How-To
title: Parse a GLB with a bounded reader into unregistered CPU meshes with path-plus-hash ids
description: Validate a self-contained GLB 2.0 container with hard limits, bake scene node and UV transforms into vertices, set HostPrimitives and PrimitiveMaterialIds on a MeshReference by reflection and freeze library choices to an id made from the absolute path and a SHA-256 of the contents
category: assets
group: models-and-textures
topic: import-glb-models-at-runtime
approach: 1
difficulty: advanced
prerequisites: [starmap, reflection]
demonstrated_by: [mods/unscience/features/pebbles-glb-import]
techniques: [glb-model, reflection]
hooks: [starmap StarMapAfterGui, reflection KSA.MeshReference.HostPrimitives]
lifecycle: [frame:draw-ui, frame:dispatch-solvers]
game_types:
  - KSA.MeshReference
  - RenderCore.MeshAsset
  - KSA.ModLibrary
  - KSA.SerializedCollection
  - KSA.Gltf2Reference
  - KSA.FileReference
  - Brutal.GltfApi.GltfLoader
  - Brutal.GltfApi.GltfUtility
  - Brutal.Collections.NativeStrideList
  - KSA.DeviceMeshInterleaved
tags: [glb, import, runtime, parser, meshreference, hash, identity, cpu-mesh]
---

Most mods ship their models in an asset XML at build time. Some want to let a player pick any `.glb` (a Blender export, a downloaded model) at run time and use it as a mesh. unscience's Pebbles does that for clutter and Sphinx for placed statics. It never registers the model in `ModLibrary`, so
an import cannot collide with or replace game content. The file is parsed by its own bounded GLB 2.0 reader, node and UV transforms are baked into vertices, and the result is a CPU-only `MeshReference` whose id is made from the file path and a SHA-256 of its bytes, so saves keep pointing at exactly the model they were
authored with.

Demonstrated by [Bounded GLB 2.0 import into unregistered CPU-only meshes with frozen path and hash ids](../../../../mods/unscience/features/pebbles-glb-import.md). The game's own atlas path is [Export or generate a GLB that KSA loads correctly](../export-glb-from-blender-for-ksa/overview.md); the materials half is [Convert glTF materials on the CPU into game maps and upload them as private bindless textures](import-glb-models-at-runtime-2.md).

## Background: how the game does it

`MeshReference` holds a mesh's `HostPrimitives` (CPU `MeshAsset`s) and `PrimitiveMaterialIds`, both with private setters, plus an optional device mesh created when `Load(loader, mesh, createDeviceMesh)` runs; the game's `Load` builds them from a `GltfLoader`. A mesh created without ever calling
`ModLibrary.Register(MeshReference)` is invisible to the registry:

```csharp
// current/decomp/KSA/MeshReference.cs L44-L53

	[XmlIgnore]
	public MeshAsset[] HostPrimitives { get; private set; } = null;

	[XmlIgnore]
	public int[] PrimitiveMaterialIds { get; private set; } = null;

	[XmlIgnore]
	public MeshAsset HostMesh => HostPrimitives[0];

```

`MeshAtlasFileReference.DoLoad` is the game's own route: `new GltfLoader(path)`, one `MeshReference` per glTF mesh named after the mesh, registered through `ModLibrary.Register`
([current/decomp/KSA/MeshAtlasFileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshAtlasFileReference.cs#L26-L45)). Pebbles replaces both the reader and the registration; it keeps the `MeshReference` type so downstream clutter and static renderers accept it.

## Lifecycle and timing

Import is synchronous on the main thread inside UI code (the file-browser callback and the Load button under `[StarMapAfterGui]`, `frame:draw-ui`); parsing a large file stalls that frame and there is no worker thread. Selecting a mesh resolves geometry in the same call. Apply resolves the meshes again inside a prefix on
`Universe.ExecuteNextClothSolvers` (`frame:dispatch-solvers`), usually cache hits, when the private clutter graph is built. Release is deferred to the next `Update` until every live borrower has retired. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Validate the container with hard limits before reading anything.** Complete GLB 2.0 only: magic `glTF`, version 2, declared length equal to the byte count, 20 B to 128 MiB, a JSON chunk first (at most 8 MiB, depth 64), exactly one embedded `BIN` chunk, one buffer with no `uri`, 1 to 512 meshes, whitelisted
   `extensionsRequired`. The SHA-256 of the whole file becomes its content hash:

   ```csharp
   // pebbles.lib/Models/GlbDocument.cs L27-L64
       public static GlbDocument Parse(byte[] bytes)
       {
           if (bytes.Length is < 20 or > MaximumBytes || U32(bytes, 0) != 0x46546C67 || U32(bytes, 4) != 2 || U32(bytes, 8) != bytes.Length)
               throw new InvalidDataException("Expected a complete GLB 2.0 file.");
           JsonDocument? json = null; byte[]? binary = null;
           try
           {
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
               var root = json.RootElement;
               if (Required(Required(root, "asset"), "version").GetString() != "2.0") throw new InvalidDataException("Only glTF 2.0 is supported.");
               var buffers = Required(root, "buffers");
               if (buffers.GetArrayLength() != 1 || buffers[0].TryGetProperty("uri", out _) || Required(buffers[0], "byteLength").GetInt64() > binary.Length || Required(buffers[0], "byteLength").GetInt64() < binary.Length - 3)
                   throw new InvalidDataException("Use a self-contained GLB with one embedded binary buffer.");
               if (Required(root, "meshes").GetArrayLength() is < 1 or > 512) throw new InvalidDataException("GLB needs 1–512 meshes.");
               GlbCompatibility.RequiredExtensions(root);
               return new(json, binary, Convert.ToHexString(SHA256.HashData(bytes)));
           }
           catch { json?.Dispose(); throw; }
       }
   ```

2. **Read geometry defensively.** 1 to 256 triangle-list primitives, no sparse accessors or morph targets, floats for positions and normals, bounds-checked strides and offsets, missing normals accumulated from face cross products, a selection capped at 2048 primitives, 2 million vertices and 12 million indices.
   The UV set is chosen by the material's main texture and its `KHR_texture_transform` is baked into the coordinates, because the native vertex has a single UV stream ([pebbles.lib/Models/GlbGeometry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/GlbGeometry.cs)).

3. **Bake node transforms for a "complete scene" selection, including mirrored winding.** The scene reader walks the default scene (4096 nodes, depth 64, cycle detection), composes TRS or affine matrices, transforms normals with the inverse transpose and reverses triangle winding when a matrix has a negative determinant. Individual meshes use raw local coordinates
   ([pebbles.lib/Models/GlbScene.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/GlbScene.cs#L69-L79)).

4. **Build a `MeshReference` that is never registered.** Fill one `MeshAsset` per primitive (float3 position and normal, float2 UV, `uint` indices) and set `HostPrimitives` and `PrimitiveMaterialIds` through their private setters by reflection; call `SetHash()` so lookups by hash work:

   ```csharp
   // pebbles.lib/Assets/GlbImportLibrary.cs L76-L104
       public MeshReference ResolveMesh(string id)
       {
           var source = ResolveSource(id); var identity = GlbIdentity.Parse(id);
           if (!identity.Part.StartsWith("/mesh/", StringComparison.Ordinal) || !int.TryParse(identity.Part[6..], out int index) || index < -1 || index >= source.Document.MeshCount)
               throw new InvalidDataException("Unresolved GLB mesh selection.");
           if (source.Meshes.TryGetValue(index, out var mesh)) return mesh;
           var primitives = index == -1 ? source.Document.ReadScene() : source.Document.ReadMesh(index);
           long retained = _sources.Values.SelectMany(s => s.Meshes.Values).SelectMany(m => m.HostPrimitives).Sum(p => (long)p.VertexCount);
           if (retained + primitives.Sum(p => (long)p.Positions.Length) > 8_000_000) throw new InvalidOperationException("Imported GLB cache exceeds 8 million vertices. Release imports to reclaim it.");
           var hosts = new List<MeshAsset>();
           try
           {
               foreach (var p in primitives)
               {
                   var minimum = p.Positions.Aggregate(Vector3.Min); var maximum = p.Positions.Aggregate(Vector3.Max);
                   var host = new MeshAsset { VertexCount = p.Positions.Length, PositionMinimum = new(minimum.X, minimum.Y, minimum.Z), PositionMaximum = new(maximum.X, maximum.Y, maximum.Z) };
                   hosts.Add(host);
                   host.SetVertexList(MeshAttribute.Position, NativeStrideList.FromSpan<Vector3>(p.Positions));
                   host.SetVertexList(MeshAttribute.Normal, NativeStrideList.FromSpan<Vector3>(p.Normals));
                   host.SetVertexList(MeshAttribute.Uv0, NativeStrideList.FromSpan<Vector2>(p.Uvs));
                   host.SetIndexBuffer(NativeStrideList.FromSpan<uint>(p.Indices));
               }
               mesh = new MeshReference { Id = id, PrimitiveCount = hosts.Count };
               typeof(MeshReference).GetProperty(nameof(MeshReference.HostPrimitives))!.SetValue(mesh, hosts.ToArray());
               typeof(MeshReference).GetProperty(nameof(MeshReference.PrimitiveMaterialIds))!.SetValue(mesh, primitives.Select(p => p.Material).ToArray());
               mesh.SetHash(); source.Meshes.Add(index, mesh); return mesh;
           }
           catch { foreach (var host in hosts) host.Dispose(); throw; }
       }
   ```

5. **Make the id from the path and the contents.** `pebbles-glb:<base64url(absolute path)>:<sha256>/mesh/<n>`. Parsing an id does no file access. Recipes, saves and placements store this id:

   ```csharp
   // pebbles.lib/Models/GlbIdentity.cs L9-L13
   public sealed record GlbIdentity(string Path, string Hash, string Part)
   {
       public const string Prefix = "pebbles-glb:";
       public string SourceKey => Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(Path)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ":" + Hash;
       public string MeshId(int mesh) => SourceKey + "/mesh/" + mesh.ToString(CultureInfo.InvariantCulture);
   // ...
   ```

6. **Resolve a cache miss through the copied library file, not the stored path.** A save may carry another machine's absolute path; resolve only the copied filename under this installation and refuse the file if its hash differs ("The GLB file has changed. Import it again and explicitly select the new version"):

   ```csharp
   // pebbles.lib/Assets/GlbImportLibrary.cs L61-L75
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
   ```

7. **Freeze lazy choices before storing them.** Pickers list `unscience-glb-file:<name>` ids from a shared catalogue (a name scan, no decoding); `ClutterAssets.ResolveSelection` imports the file and returns the frozen complete-scene id, and `ResolveMesh` throws for unfrozen ids:

   ```csharp
   // pebbles.lib/Assets/ClutterAssets.cs L25-L28
       /// <summary>Freeze a lazy library choice to an exact imported content id before storing a recipe.</summary>
       public string ResolveSelection(string id) => GlbLibrary.IsSelection(id)
           ? ImportGlb(GlbLibrary.Files.FullPath(GlbLibrary.FileName(id)))[0].Id : id;
       public static string MeshLabel(string id) => GlbLibrary.IsSelection(id) ? GlbLibrary.Label(id) : GlbIdentity.Label(id);
   ```

8. **Report what you could not honour.** Draco/meshopt compression, unknown required extensions, external or data URIs are rejected with targeted messages; appearance extensions are accepted with warnings (see [Convert glTF materials on the CPU into game maps and upload them as private bindless textures](import-glb-models-at-runtime-2.md)).

## Complete example

- [pebbles.lib/Models/GlbDocument.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/GlbDocument.cs), [pebbles.lib/Models/GlbGeometry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/GlbGeometry.cs), [pebbles.lib/Models/GlbScene.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/GlbScene.cs), [pebbles.lib/Models/GlbIdentity.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/GlbIdentity.cs)
- [pebbles.lib/Assets/GlbImportLibrary.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Assets/GlbImportLibrary.cs), [pebbles.lib/Assets/ClutterAssets.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Assets/ClutterAssets.cs)

## Variations and gotchas

- **Why not the game's loader?** `GltfLoader` reads from a path and `MeshReference.Load` builds device meshes and registers a binder; for arbitrary player files you want size limits, no device mesh and no registry entry. `Load(loader, i, createDeviceMesh: false)` is the game-side switch if you do use the stock loader
  ([current/decomp/KSA/MeshReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshReference.cs#L82-L125)).
- **The cache is bounded.** At most 16 content versions and 8 million retained vertices; beyond that, release imports first. Exact content versions never replace one another.
- **Hash the bytes, not just the path.** Two different files at the same path get different ids; the same file at two paths is one source.
- **`MeshReference` has a finalizer** that disposes device primitives; dispose the host `MeshAsset`s yourself when you drop an import.
- **Reflection on private setters** is a version risk: keep it in one place so a rename in a game update is one edit.

## See also

- [Convert glTF materials on the CPU into game maps and upload them as private bindless textures](import-glb-models-at-runtime-2.md), [Enumerate the game's loaded meshes, textures and glTFs for pickers and id checks](import-glb-models-at-runtime-3.md), [Export or generate a GLB that KSA loads correctly](../export-glb-from-blender-for-ksa/overview.md), [Draw your own meshes through the engine's mesh renderers](../../../rendering/meshes/draw-mod-meshes-with-the-engine-renderers/overview.md),
  [Keep grass, rocks and trees off a custom site, and replace or preserve a planet's ground clutter](../../../celestials/terrain/exclude-or-replace-ground-clutter/overview.md), [Load, validate and unload part mods in a running game](../../../vehicles-and-parts/parts/load-and-unload-part-mods-at-runtime/overview.md)
- [Bounded GLB 2.0 import into unregistered CPU-only meshes with frozen path and hash ids](../../../../mods/unscience/features/pebbles-glb-import.md)
