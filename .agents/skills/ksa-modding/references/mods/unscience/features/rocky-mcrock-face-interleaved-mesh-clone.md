---
type: Mod Feature
title: Clone an atlas-interleaved part mesh into a Simple mesh the ring renderer can draw
description: Rocky McRock Face clones interleaved part and subpart meshes into private Simple MeshReferences that share the retained CPU HostPrimitives and uploads them as SimpleVkMesh for the ring pipeline, and imports glTF-file meshes the same way
mod: unscience
submod: rocky-mcrock-face
category: rendering
techniques: [reflection, vulkan-buffer, glb-model]
hooks:
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent; conversion happens on Apply)
  - reflection KSA.MeshReference.<HostPrimitives>k__BackingField
lifecycle: [frame:draw-ui, frame:prepare, render:scene-opaque]
game_types:
  - KSA.MeshReference
  - KSA.MeshAtlasFileReference
  - KSA.DeviceMeshInterleaved
  - RenderCore.Mesh.SimpleVkMesh
  - RenderCore.MeshAsset
  - Brutal.GltfApi.GltfLoader
  - Brutal.VulkanApi.Abstractions.StagingPool
  - KSA.Rendering.Raytracing.RaytracingRenderer
  - KSA.Rendering.Rings.Rendering.PlanetaryRingsRenderer
tags: [meshes, glb, gltf, vulkan, reflection, rings]
---

The ring rock field draws each LOD with a `SimpleVkMesh`: one GPU vertex stream per attribute
(position, normal, uv0) and a 32-bit index buffer. The game only builds that object for meshes
loaded as `Simple`, such as the stock ring rocks. Part and subpart meshes come from mesh atlases
loaded `Interleaved` into a shared buffer, so their `DeviceMesh` slot is empty. Rocky McRock Face
makes any part mesh (and any mesh inside a character or equipment glTF file) usable as a ring rock
without touching the original.[^rocky-readme]

Mechanism in one line: construct a new `MeshReference { Simple = true, Interleaved = false,
PrimitiveCount = 1 }`, point its private-set `HostPrimitives` at the source mesh's retained CPU
geometry through `<HostPrimitives>k__BackingField`, and call the game's own `MeshReference.Bind`
with a private `StagingPool`.

## How it works

`RingMeshFactory.GetRingUsable(source)`:[^ring-mesh-factory]

1. A `Simple` source that already has `DevicePrimitives[0]` is returned unchanged (the stock
   rocks).
2. A cached clone for the source id is returned if one exists.
3. Otherwise the source must have `HostPrimitives[0]`. `MeshReference.Load` always stores the
   imported CPU primitives (position, normal and uv0 via `VertexImportFlags.Normals | UVs`, plus
   an index buffer), and atlas meshes keep them, so this is the case for loaded part meshes.[^ksa-mesh-reference]
4. A clone is created with `Id = "rocky_mcrock_face/<source id>"`, `Simple = true`,
   `Interleaved = false`, `PrimitiveCount = 1` and the source's `BoundingSphereRadius` (falling back
   to the length of `PositionMaximum` / `PositionMinimum` when it is zero, and refusing zero).
5. `HostPrimitivesField.SetValue(clone, source.HostPrimitives)` shares the array; nothing is
   copied on the CPU.
6. `clone.Bind(renderer, stagingPool)` with `renderer.Allocator.CreateStagingPool(renderer.GraphicsAndCompute, 1)`
   creates `SimpleVkMesh` objects for `PrimitiveCount` primitives (here only 0). The staging pool's
   `Dispose` submits and waits, so the upload is finished when the call returns.[^ksa-staging-pool]
7. The clone is cached by source id for the factory's lifetime.

Why clone instead of flipping the source's flags: the source `MeshReference` is shared with
`PartModel` rendering, and IVA ray tracing throws `"Subpart mesh is not interleaved"` for a subpart
whose mesh has `Interleaved == false`.[^ksa-raytracing-renderer] Atlas loading sets `Simple = false`
and `Interleaved = true` on every part mesh it creates.[^ksa-mesh-atlas]

`GetRingUsableFromGltf(entry)` handles meshes that never enter `ModLibrary.AllMeshes` (kitten,
helmet, MMU glTF files): it opens a `GltfLoader` on the file, creates a `Simple` `MeshReference`,
calls `Load(gltfLoader, meshIndex, createDeviceMesh: false)` (the same import the stock ring rocks
use; skinned meshes come out in bind pose) and binds it. All primitives of that mesh are uploaded,
but the ring draws only the first.

Clones are released by `PruneExcept(keepSourceIds)` after a successful renderer rebuild, and by
`Dispose` after the controller has restored defaults and rebuilt. `MeshReference.Dispose` disposes
only the clone's own `DevicePrimitives`; the shared `HostPrimitives` remain owned by the source.

## Lifecycle and timing

| Step | Where | Phase |
|---|---|---|
| Conversion and upload | Inside `RingSwapController.Apply` (Rocky) or `RingReferenceBuilder.ResolveLods` (Bloomin' Onion), from an Apply button | `frame:draw-ui` |
| Conversion on load | Save replay of either sub-mod at the physics handoff | `frame:prepare` |
| Drawn | `PlanetaryRingsRenderer.RenderMeshes` binds `MeshLods[i].DeviceMesh` streams | `render:scene-opaque` |
| Freed | `PruneExcept` right after `RebuildRenderer` returns | Same call as Apply |

The upload uses its own command pool and waits, so it is independent of the frame being built and
can run anywhere on the main thread. Freeing is the delicate part: a clone's `SimpleVkMesh` buffers
must not be destroyed while a submitted frame still draws them. The mods free clones only right
after `RebuildRenderer`, whose final `Device.WaitIdle()` has drained all frames, and whose new ring
render data references exactly the clones resolved for it.

## Key code

| Path | Role |
|---|---|
| [rocky-mcrock-face.lib/RingMeshFactory.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face.lib/RingMeshFactory.cs) | Clone, glTF import, cache, prune |
| [rocky-mcrock-face.lib/RingSwapController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face.lib/RingSwapController.cs#L91-L116) | Rocky caller: resolve meshes before mutating |
| [bloomin-onion.lib/RingReferenceBuilder.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/RingReferenceBuilder.cs#L181-L217) | Bloomin' Onion caller: one mesh per LOD |

## Snippets

The clone shares the source's CPU primitives and gets its own device mesh:

```csharp
// rocky-mcrock-face.lib/RingMeshFactory.cs L37-L83
    public MeshReference? GetRingUsable(MeshReference source, out string? error)
    {
        error = null;
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

        var clone = new MeshReference
        {
            Id = $"rocky_mcrock_face/{source.Id}",
            Simple = true,
            Interleaved = false,
            PrimitiveCount = 1,
            BoundingSphereRadius = source.BoundingSphereRadius,
        };
        HostPrimitivesField.SetValue(clone, source.HostPrimitives);
        // ...
        try
        {
            var renderer = Program.GetRenderer();
            // StagingPool disposal submits and waits, so the upload is complete on return.
            using var stagingPool = renderer.Allocator.CreateStagingPool(renderer.GraphicsAndCompute, 1);
            clone.Bind(renderer, stagingPool);
        }
```

The game's `Bind` only builds per-attribute `SimpleVkMesh` objects for `Simple` meshes (build 5554):

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

Why the source's flags must not be flipped instead (build 5554):

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

Importing a mesh straight out of a glTF file, without registering a binder:

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

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `MeshReference.{Simple, Interleaved, PrimitiveCount, BoundingSphereRadius}` | [current/decomp/KSA/MeshReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshReference.cs#L17-L35) | Clone configuration |
| `MeshReference.HostPrimitives` (private setter) | [current/decomp/KSA/MeshReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshReference.cs#L46) | Written through `<HostPrimitives>k__BackingField` |
| `MeshReference.DevicePrimitives` / `DeviceMesh` | [current/decomp/KSA/MeshReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshReference.cs#L58-L61) | `DeviceMesh` is `DevicePrimitives[0]` |
| `MeshReference.Load(GltfLoader, int, bool)` | [current/decomp/KSA/MeshReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshReference.cs#L82-L135) | Import with position, normal, uv0; `createDeviceMesh: false` skips `RegisterBinder` |
| `MeshReference.Bind`, `Dispose`, finalizer | [current/decomp/KSA/MeshReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshReference.cs#L137-L171) | Upload; `Dispose` frees only `DevicePrimitives` |
| `MeshAtlasFileReference.DoLoad` | [current/decomp/KSA/MeshAtlasFileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshAtlasFileReference.cs#L25-L48) | Part meshes are `Simple = false`, `Interleaved = true` |
| `SimpleVkMesh(IVulkanContext, StagingPool, MeshAsset)` | [current/decomp/RenderCore.Mesh/SimpleVkMesh.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Mesh/SimpleVkMesh.cs#L69-L74) | Per-attribute GPU streams |
| `RaytracingRenderer` subpart check | [current/decomp/KSA.Rendering.Raytracing/RaytracingRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Raytracing/RaytracingRenderer.cs#L1011-L1014) | Reason not to mutate source flags |
| `PlanetaryRingsRenderer.RenderMeshes` / `MakeMeshesVertexInput` | [current/decomp/KSA.Rendering.Rings.Rendering/PlanetaryRingsRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Rings.Rendering/PlanetaryRingsRenderer.cs#L593-L601) | Binds indices plus Position, Normal, Uv0 streams of `DeviceMesh`; layout at L878-L882 |
| `StagingPool.Dispose` | [current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs#L214-L218) | Submit and wait |

## Pitfalls and version notes

- The vertex layout match is an invariant, not a check: the ring pipeline expects float3 position,
  float3 normal and float2 uv0 streams, and `MeshReference.Load` imports exactly those. A game
  change on either side gives garbage geometry, not a compile error.[^ksa-rings-renderer]
- `MeshReference` has a finalizer that disposes its device primitives. A clone that is dropped
  while the renderer still draws it would free live buffers on a GC thread; the factory therefore
  keeps clones in a dictionary until it prunes them explicitly after a rebuild.
- Only primitive 0 is uploaded for atlas clones and only primitive 0 is drawn for any LOD; a
  multi-material part shows only its first material group, textured with the ring's rock material.
- Meshes not centered on their origin visibly orbit their own origin, because the ring shader
  instances them around the mesh origin.
- Full-detail part or kitten meshes on every LOD multiply into millions of triangles across the
  instanced chunks; the panel shows index counts (from `DeviceMeshesInterleaved[0]` or
  `DevicePrimitives[0]`) so users keep LOD 2 and above light.
- `<HostPrimitives>k__BackingField` is a compiler-generated name; a missing field is reported as an
  Apply error, never a crash.

## See also

- [Ring asset swap](./rocky-mcrock-face-ring-asset-swap.md) and [runtime planetary rings](./bloomin-onion-runtime-planetary-rings.md) (the callers)
- [Ring asset catalog](./rocky-mcrock-face-asset-catalog.md) (which meshes are offered)
- [Rocky McRock Face sub-mod](../submods/rocky-mcrock-face.md) and [mod overview](../overview.md)
- [Mutate the public ring reference tree, snapshot originals and recreate the rings renderer](../../../howto/celestials/atmospheres-and-rings/add-or-restyle-planetary-rings/add-or-restyle-planetary-rings-2.md) - how-to article demonstrating this feature
- [Clone interleaved part meshes into private SimpleVkMeshes for another renderer](../../../howto/rendering/meshes/draw-mod-meshes-with-the-engine-renderers/draw-mod-meshes-with-the-engine-renderers-4.md) - how-to article demonstrating this feature
- [Clone an interleaved part mesh into a Simple mesh for your own renderer](../../../howto/vehicles-and-parts/part-geometry/get-part-points-in-vehicle-and-world-frames/get-part-points-in-vehicle-and-world-frames-5.md) - how-to article demonstrating this feature

[^rocky-readme]: rocky-mcrock-face/README.md
[^ring-mesh-factory]: rocky-mcrock-face.lib/RingMeshFactory.cs
[^ksa-mesh-reference]: KSA.MeshReference (decompiled, build 2026.10.10.5554)
[^ksa-staging-pool]: Brutal.VulkanApi.Abstractions.StagingPool (decompiled, build 2026.10.10.5554)
[^ksa-raytracing-renderer]: KSA.Rendering.Raytracing.RaytracingRenderer (decompiled, build 2026.10.10.5554)
[^ksa-mesh-atlas]: KSA.MeshAtlasFileReference (decompiled, build 2026.10.10.5554)
[^ksa-rings-renderer]: KSA.Rendering.Rings.Rendering.PlanetaryRingsRenderer (decompiled, build 2026.10.10.5554)
