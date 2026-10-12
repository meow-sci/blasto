---
type: How-To
title: Clone interleaved part meshes into private SimpleVkMeshes for another renderer
description: Reuse a part or glTF mesh in a renderer that needs standalone per-attribute vertex streams by sharing its retained HostPrimitives and uploading a private SimpleVkMesh through MeshReference.Bind
category: rendering
group: meshes
topic: draw-mod-meshes-with-the-engine-renderers
approach: 4
difficulty: advanced
prerequisites: [starmap, reflection, vulkan]
demonstrated_by: [mods/unscience/features/rocky-mcrock-face-interleaved-mesh-clone]
techniques: [vulkan-buffer, reflection]
hooks: [reflection KSA.MeshReference.<HostPrimitives>k__BackingField]
lifecycle: [frame:draw-ui, frame:prepare, render:scene-opaque]
game_types:
  - KSA.MeshReference
  - KSA.MeshAtlasFileReference
  - KSA.DeviceMeshInterleaved
  - RenderCore.Mesh.SimpleVkMesh
  - KSA.Rendering.Rings.Rendering.PlanetaryRingsRenderer
tags: [mesh, simple-vk-mesh, host-primitives, staging-pool, interleaved, rings]
---
Outcome: take a mesh the game has already loaded (a part's mesh from a mesh atlas, or any mesh inside a glTF file such as the kitten or helmet) and turn it into a `MeshReference` with a private `SimpleVkMesh`, so a renderer that wants standalone position, normal and uv streams can draw it. The source mesh is never modified.

Demonstrated by unscience's Rocky McRock Face, which makes any part mesh usable as a ring rock ([Clone an atlas-interleaved part mesh into a Simple mesh the ring renderer can draw](../../../../mods/unscience/features/rocky-mcrock-face-interleaved-mesh-clone.md)). Prerequisites: StarMap, reflection into one compiler-generated field, and a consumer that draws `MeshReference.DeviceMesh` (here the planetary ring renderer; see also the sibling topic on adding or restyling planetary rings).

## Background: how the game does it

`MeshReference` has two GPU representations. A `Simple` mesh gets one `SimpleVkMesh` per primitive, with separate vertex streams per attribute and an index buffer. An `Interleaved` mesh lives in the shared `DeviceMeshInterleaved` buffer. Atlas loading marks every part mesh `Simple = false`, `Interleaved = true` ([current/decomp/KSA/MeshAtlasFileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshAtlasFileReference.cs#L30-L50)), so a part mesh has no `DeviceMesh`. `MeshReference.Bind` only builds the `SimpleVkMesh` for `Simple` meshes:

```csharp
// current/decomp/KSA/MeshReference.cs L137-L155
public void Bind(Renderer renderer, StagingPool stagingPool)
{
	DevicePrimitives = new SimpleVkMesh[PrimitiveCount];
	for (int i = 0; i < PrimitiveCount; i++)
	{
		if (HostPrimitives[i] == null)
		{
			throw new NullReferenceException("Gltf is null for mesh '" + base.Id + "'");
		}
		if (Simple)
		{
			DevicePrimitives[i] = new SimpleVkMesh(renderer, stagingPool, HostPrimitives[i]);
		}
		if (Interleaved && DeviceMeshesInterleaved != null)
		{
			DeviceMeshesInterleaved[i].Bind();
		}
	}
}
```

`MeshReference.Load` always keeps the imported CPU geometry in `HostPrimitives` (position, normal and uv0, plus indices), a property with a private setter ([current/decomp/KSA/MeshReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshReference.cs#L46)). The ring renderer's rock draw binds exactly those three streams of each LOD's `DeviceMesh`:

```csharp
// current/decomp/KSA.Rendering.Rings.Rendering/PlanetaryRingsRenderer.cs L594-L601
{
	SimpleVkMesh deviceMesh = value.MeshLods[i].DeviceMesh;
	deviceMesh.BindIndices(inCommandBuffer);
	deviceMesh.BindVertexStream(inCommandBuffer, MeshAttribute.Position, 1);
	deviceMesh.BindVertexStream(inCommandBuffer, MeshAttribute.Normal, 2);
	deviceMesh.BindVertexStream(inCommandBuffer, MeshAttribute.Uv0, 3);
	inCommandBuffer.DrawIndexedIndirect(_indirectDrawArgsBuffer.VkBuffer, ByteSize.Of<VkDrawIndexedIndirectCommand>(i), 1, ByteSize.Of<VkDrawIndexedIndirectCommand>());
}
```

Why clone rather than flip `Simple` on the source: that `MeshReference` is shared with `PartModel`, and IVA ray tracing throws for a subpart whose mesh is not interleaved:

```csharp
// current/decomp/KSA.Rendering.Raytracing/RaytracingRenderer.cs L1006-L1014
MeshReference mesh = partModel.Template.Mesh;
if (mesh == null)
{
	throw new Exception("Subpart mesh is null");
}
if (!mesh.Interleaved)
{
	throw new Exception("Subpart mesh is not interleaved");
}
```

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Clone and upload | Inside the mod's Apply button handler | `frame:draw-ui` |
| Clone and upload on load | Save replay at the physics handoff | `frame:prepare` |
| Drawn by the consumer | `PlanetTransparenciesRenderer.RenderRingMeshes` inside the offscreen scope | `render:scene-opaque` (row 9 of the [RenderGame table](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence)) |
| Freed | After a renderer rebuild returns | same call as Apply |

The upload uses its own staging pool whose `Dispose` submits and waits, so the data is on the GPU when `Bind` returns and the work is independent of the frame being built. Freeing is the delicate part: a clone's buffers must not be destroyed while a submitted frame still draws them. The mod frees clones only right after `RebuildRenderer`, whose final `Device.WaitIdle()` has drained all frames (see [free Vulkan resources safely](../../vulkan-passes/free-vulkan-resources-safely/overview.md)).

## Steps

1. **Find the backing field of `HostPrimitives`.** The setter is private, so write the field directly:

   ```csharp
// rocky-mcrock-face.lib/RingMeshFactory.cs L27-L28
private static readonly FieldInfo? HostPrimitivesField = typeof(MeshReference).GetField(
    "<HostPrimitives>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
```

2. **Return the source unchanged if it is already usable**, serve a cached clone if you made one, and refuse sources without retained CPU geometry:

   ```csharp
// rocky-mcrock-face.lib/RingMeshFactory.cs L40-L55
if (source.Simple && source.DevicePrimitives is { Length: > 0 } && source.DevicePrimitives[0] != null)
    return source;

if (_clones.TryGetValue(source.Id, out var cached))
    return cached;

if (HostPrimitivesField == null)
{
    error = "MeshReference.HostPrimitives backing field not found (game update?)";
    return null;
}
if (source.HostPrimitives is not { Length: > 0 } || source.HostPrimitives[0] == null)
{
    error = $"mesh '{source.Id}' has no retained CPU-side geometry";
    return null;
}
```

3. **Create the clone and share the CPU arrays** (nothing is copied):

   ```csharp
// rocky-mcrock-face.lib/RingMeshFactory.cs L57-L65
var clone = new MeshReference
{
    Id = $"rocky_mcrock_face/{source.Id}",
    Simple = true,
    Interleaved = false,
    PrimitiveCount = 1,
    BoundingSphereRadius = source.BoundingSphereRadius,
};
HostPrimitivesField.SetValue(clone, source.HostPrimitives);
```

4. **Upload with the game's own `Bind`** and a private staging pool whose disposal submits and waits:

   ```csharp
// rocky-mcrock-face.lib/RingMeshFactory.cs L77-L88
try
{
    var renderer = Program.GetRenderer();
    // StagingPool disposal submits and waits, so the upload is complete on return.
    using var stagingPool = renderer.Allocator.CreateStagingPool(renderer.GraphicsAndCompute, 1);
    clone.Bind(renderer, stagingPool);
}
catch (Exception ex)
{
    error = $"GPU upload failed for mesh '{source.Id}': {ex.Message}";
    return null;
}
```

5. **Cache the clone for the factory's lifetime.** `MeshReference` has a finalizer that disposes its device primitives, so a clone dropped while a frame still uses it would free live buffers on a GC thread. Keep clones in a dictionary until you prune them explicitly:

   ```csharp
// rocky-mcrock-face.lib/RingMeshFactory.cs L90-L99
if (clone.DevicePrimitives is not { Length: > 0 } || clone.DevicePrimitives[0] == null)
{
    error = $"mesh '{source.Id}' produced no device mesh";
    return null;
}

Console.WriteLine($"rocky-mcrock-face: converted mesh '{source.Id}' for ring use " +
                  $"({clone.DeviceMesh.IndexCount} indices, radius {clone.BoundingSphereRadius:F2} m)");
_clones[source.Id] = clone;
return clone;
```

6. **Meshes that never entered `ModLibrary.AllMeshes`** (kitten, helmet, MMU glTF files) are imported straight from the file with the same `MeshReference.Load` the game uses for the stock ring rocks, with `createDeviceMesh: false` so no binder is registered:

   ```csharp
// rocky-mcrock-face.lib/RingMeshFactory.cs L114-L137
try
{
    using var gltfLoader = new GltfLoader(entry.FilePath);
    var clone = new MeshReference
    {
        Id = $"rocky_mcrock_face/{entry.Id}",
        Simple = true,
        Interleaved = false,
    };
    clone.Load(gltfLoader, entry.MeshIndex, createDeviceMesh: false);
    if (clone.HostPrimitives is not { Length: > 0 } || clone.HostPrimitives[0] == null)
    {
        error = $"glTF mesh '{entry.Id}' loaded no geometry";
        return null;
    }
    if (clone.BoundingSphereRadius <= 0.0)
    {
        error = $"glTF mesh '{entry.Id}' has a zero bounding radius";
        return null;
    }

    var renderer = Program.GetRenderer();
    using var stagingPool = renderer.Allocator.CreateStagingPool(renderer.GraphicsAndCompute, 1);
    clone.Bind(renderer, stagingPool);
```

7. **Resolve every mesh before you mutate game state**, so one failed lookup leaves the game untouched:

   ```csharp
// rocky-mcrock-face.lib/RingSwapController.cs L91-L116
// Resolve everything up-front so a failed lookup leaves the game untouched.
var meshes = new MeshReference?[lodCount];
for (int i = 0; i < lodCount; i++)
{
    string id = selection.LodMeshIds[i];
    if (id.Length == 0)
    {
        meshes[i] = defaults.LodMeshes[i];
        continue;
    }
    string? error;
    if (Catalog.TryGetMesh(id, out var source))
        meshes[i] = MeshFactory.GetRingUsable(source, out error);
    else if (Catalog.TryGetGltfMesh(id, out var gltfEntry))
        meshes[i] = MeshFactory.GetRingUsableFromGltf(gltfEntry, out error);
    else
    {
        message = $"unknown mesh '{id}'";
        return false;
    }
    if (meshes[i] == null)
    {
        message = error ?? $"mesh '{id}' is not usable";
        return false;
    }
}
```

## Complete example

| File | Role |
|---|---|
| [rocky-mcrock-face.lib/RingMeshFactory.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face.lib/RingMeshFactory.cs) | Clone, glTF import, cache, prune |
| [rocky-mcrock-face.lib/RingSwapController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face.lib/RingSwapController.cs#L91-L116) | Caller: resolve meshes before applying |
| [bloomin-onion.lib/RingReferenceBuilder.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/RingReferenceBuilder.cs#L181-L217) | Second caller, one mesh per LOD |

## Variations and gotchas

- **The vertex layout match is an invariant.** The ring pipeline expects float3 position, float3 normal and float2 uv0 streams ([current/decomp/KSA.Rendering.Rings.Rendering/PlanetaryRingsRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Rings.Rendering/PlanetaryRingsRenderer.cs#L878-L882)), which `MeshReference.Load` imports. A game change on either side gives garbage geometry, not a compile error.
- **Only primitive 0** of an atlas clone is uploaded and only primitive 0 is drawn per LOD, so a multi-material part shows only its first material group with the ring's rock material.
- **Meshes not centred on their origin** orbit their own origin, because the ring shader instances them around the mesh origin.
- **Polygon budget.** Full-detail part or kitten meshes on every LOD multiply into millions of triangles across the instanced chunks; keep LOD 2 and above light.
- **Compiler-generated name.** `<HostPrimitives>k__BackingField` can change; report a missing field as an error, never crash.
- **Freeing.** `MeshReference.Dispose` frees only the clone's own `DevicePrimitives`; the shared `HostPrimitives` stay owned by the source.

## See also

- [Overview](overview.md)
- [Approach 1](draw-mod-meshes-with-the-engine-renderers-1.md), [approach 2](draw-mod-meshes-with-the-engine-renderers-2.md), [approach 3](draw-mod-meshes-with-the-engine-renderers-3.md)
- [Clone an atlas-interleaved part mesh into a Simple mesh the ring renderer can draw](../../../../mods/unscience/features/rocky-mcrock-face-interleaved-mesh-clone.md)
- [Free Vulkan resources safely](../../vulkan-passes/free-vulkan-resources-safely/overview.md), [Handle renderer rebuilds](../../vulkan-passes/handle-renderer-rebuilds/overview.md)
- The sibling topic on adding or restyling planetary rings (celestials category, not yet written) covers the ring consumer.
