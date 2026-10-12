---
type: How-To
title: Clone an interleaved part mesh into a Simple mesh for your own renderer
description: Build a Simple MeshReference sharing a part mesh's HostPrimitives and bind a SimpleVkMesh so renderers that cannot draw interleaved meshes can use it
category: vehicles-and-parts
group: part-geometry
topic: get-part-points-in-vehicle-and-world-frames
approach: 5
difficulty: advanced
prerequisites: [starmap, reflection]
demonstrated_by: [mods/unscience/features/rocky-mcrock-face-interleaved-mesh-clone]
techniques: [vulkan-buffer, reflection]
hooks: [starmap StarMapAfterGui, reflection KSA.MeshReference.<HostPrimitives>k__BackingField]
lifecycle: [frame:draw-ui, frame:prepare, render:scene-opaque]
game_types: [KSA.MeshReference, KSA.PartModel, KSA.MeshViewModule]
tags: [mesh, vulkan, simple-mesh, interleaved, reflection]
---
Outcome: take a part (or any) mesh that the game loaded for its interleaved part renderer and make it drawable by a pipeline that expects the game's `Simple` per-attribute
`SimpleVkMesh`, without disturbing the original. Unscience's Rocky McRock Face does this to use part meshes as ring rocks; the recipe applies to any custom renderer.

Prerequisite: StarMap, `System.Reflection`, and a reason to draw part geometry with your own Vulkan pass. Demonstrated by
[unscience/rocky-mcrock-face-interleaved-mesh-clone](../../../../mods/unscience/features/rocky-mcrock-face-interleaved-mesh-clone.md).

## Background: how the game does it

Part and subpart meshes come from glTF mesh atlases that the game loads as `Simple = false, Interleaved = true`:

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
```

`MeshReference.Load` always stores the CPU primitives in `HostPrimitives` (position, normal, uv0, index buffer). `MeshReference.Bind` builds a `SimpleVkMesh` per primitive only for `Simple` meshes:

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

so an interleaved mesh has `DevicePrimitives == null` and its `DeviceMesh` accessor fails. You cannot just flip the flags: `PartModel` still draws it, and IVA ray tracing throws
`"Subpart mesh is not interleaved"` for a subpart whose mesh has `Interleaved == false`:

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

The upload uses its own staging pool and waits, so it can run anywhere on the main thread. Rocky triggers it from a UI button (`frame:draw-ui`) and replays it from a save at the physics handoff (`frame:prepare`); the clone is drawn
by the ring renderer in the scene-opaque pass (`render:scene-opaque`). Free clones only after a renderer rebuild has drained submitted frames; the mod prunes immediately after `RebuildRenderer`. See
[game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Look up the source mesh.** From a live part: `part.Modules.Get<MeshViewModule>()[0].MeshView`; by id: `ModLibrary.Get<MeshReference>(id)`. Rocky enumerates the (internal) `ModLibrary.AllMeshes` registry by reflection.
2. **Return the source if it is already usable, else cache a clone.** Create a `Simple` clone that shares the source's `HostPrimitives` through its private-set backing field, then bind it with a private staging pool:

```csharp
// rocky-mcrock-face.lib/RingMeshFactory.cs L27-L28,L37-L44
private static readonly FieldInfo? HostPrimitivesField = typeof(MeshReference).GetField(
    "<HostPrimitives>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
// ...
public MeshReference? GetRingUsable(MeshReference source, out string? error)
{
    error = null;
    if (source.Simple && source.DevicePrimitives is { Length: > 0 } && source.DevicePrimitives[0] != null)
        return source;

    if (_clones.TryGetValue(source.Id, out var cached))
        return cached;
```

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

```csharp
// rocky-mcrock-face.lib/RingMeshFactory.cs L77-L83
try
{
    var renderer = Program.GetRenderer();
    // StagingPool disposal submits and waits, so the upload is complete on return.
    using var stagingPool = renderer.Allocator.CreateStagingPool(renderer.GraphicsAndCompute, 1);
    clone.Bind(renderer, stagingPool);
}
```

3. **Import straight from a glTF file if the mesh never entered `ModLibrary`.** Use `MeshReference.Load(gltfLoader, meshIndex, createDeviceMesh: false)` and bind the same way:

```csharp
// rocky-mcrock-face.lib/RingMeshFactory.cs L114-L123
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
```

4. **Keep clones alive until you prune them.** `MeshReference` has a finalizer that disposes its device primitives; hold each clone in a dictionary and dispose it only after the renderer no longer references it.

## Complete example

- [rocky-mcrock-face.lib/RingMeshFactory.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face.lib/RingMeshFactory.cs#L25-L100)[^unscience-ringmeshfactory] - the factory.
- [rocky-mcrock-face.lib/RingSwapController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face.lib/RingSwapController.cs#L91-L116)[^unscience-ringswapcontroller] - resolve every mesh before mutating the game, so a failed lookup leaves it untouched.

## Variations and gotchas

- **Vertex layout is an invariant, not a check.** The ring pipeline expects float3 position, float3 normal and float2 uv0 streams; `MeshReference.Load` imports exactly those. A game change on either side gives garbage geometry, not a compile error.
- **Primitive 0 only.** The clone has `PrimitiveCount = 1`; a multi-material part shows only its first group.
- **Backing-field name.** `<HostPrimitives>k__BackingField` is compiler generated; a missing field is reported as an Apply error, not a crash.
- **Shared data.** `Dispose` frees only the clone's own `DevicePrimitives`; the shared `HostPrimitives` stay owned by the source.
- **Meshes not centred on their origin** orbit their own origin when instanced around it.

## See also

- [Overview](overview.md); [Article 4](get-part-points-in-vehicle-and-world-frames-4.md)
- [unscience/rocky-mcrock-face-interleaved-mesh-clone](../../../../mods/unscience/features/rocky-mcrock-face-interleaved-mesh-clone.md)
- [Add or change a part with a GLB model](../../parts/define-a-part-with-game-data/overview.md)

[^unscience-ringmeshfactory]: unscience: rocky-mcrock-face.lib/RingMeshFactory.cs
[^unscience-ringswapcontroller]: unscience: rocky-mcrock-face.lib/RingSwapController.cs
