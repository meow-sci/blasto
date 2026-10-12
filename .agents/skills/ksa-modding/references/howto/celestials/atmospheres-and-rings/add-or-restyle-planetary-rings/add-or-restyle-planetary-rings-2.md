---
type: How-To
title: Mutate the public ring reference tree, snapshot originals and recreate the rings renderer
description: Swap the LOD meshes, rock material, band texture and field settings of an existing ring by writing into the public reference tree, cloning interleaved atlas meshes into simple ring-usable meshes, and forcing the rings renderer to rebuild
category: celestials
group: atmospheres-and-rings
topic: add-or-restyle-planetary-rings
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/unscience/features/rocky-mcrock-face-ring-asset-swap
  - mods/unscience/features/rocky-mcrock-face-interleaved-mesh-clone
  - mods/unscience/features/rocky-mcrock-face-asset-catalog
techniques: [reflection]
hooks:
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent)
  - reflection KSA.PlanetTransparenciesRenderer._ringRendererCreated
  - reflection KSA.PlanetTransparenciesRenderer._ringsRenderer
lifecycle: [frame:draw-ui, frame:prepare, render:scene-opaque, render:atmosphere]
game_types:
  - KSA.PlanetaryRingsReference
  - KSA.RingObjectsReference
  - KSA.RingLodReference
  - KSA.MeshFileReference
  - KSA.PbrMaterialReference
  - KSA.MeshReference
  - KSA.TexturePowerReference
  - KSA.DistanceReference
  - KSA.DoubleReference
  - KSA.PlanetTransparenciesRenderer
  - KSA.Program
  - RenderCore.Mesh.SimpleVkMesh
tags: [rings, meshes, textures, snapshot, restore, simplevkmesh]
---
Outcome: Saturn's rock field is drawn with any loaded mesh per LOD (fuel tanks, a kitten), any bound texture, a new band texture and new size, density, draw distance and thickness; Restore Defaults puts the stock ring back. Demonstrated by unscience's Rocky McRock Face ([Ring asset swap](../../../../mods/unscience/features/rocky-mcrock-face-ring-asset-swap.md), [Interleaved mesh clone](../../../../mods/unscience/features/rocky-mcrock-face-interleaved-mesh-clone.md), [Asset catalog](../../../../mods/unscience/features/rocky-mcrock-face-asset-catalog.md)). This is the restyle half of [approach 1](add-or-restyle-planetary-rings-1.md): the ringed body set does not change, so no body repopulation is needed.

## Background: how the game does it

The data path is all public fields: `Celestial.BodyTemplate.RingsReference` to `RingObjects` (`Lods`, `MaterialReference`, `Size`, `Thickness`, `RenderDistance`, `Density`) to `Lods[i].MeshFileReference.Get().Mesh` (a `MeshReference`, the swap slot), plus the three PBR material textures and the band `PlanetaryRingsReference.Texture` ([current/decomp/KSA/RingObjectsReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RingObjectsReference.cs#L7-L31), [current/decomp/KSA/MeshFileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshFileReference.cs#L9-L21)). The `PlanetaryRingsRenderData` constructor copies each LOD mesh and its bounding radius, index counts, material bindless handles and field settings into per-frame buffers, binds the band and control image views and sizes the chunk instance buffer from density times draw distance cubed. Without disposing the existing rings renderer, `RebuildRenderer()` hitches but changes nothing: that was the original symptom. The control texture is deliberately not offered, because `UpdateData` CPU-samples it every frame assuming four bytes per texel, while the band is only GPU-sampled and safe to swap.

## Lifecycle and timing

Apply and Restore run from the panel in `[StarMapAfterGui]` (`frame:draw-ui`), calling `Program.RebuildRenderer()` directly from inside the ImGui frame (see the timing analysis in approach 1). Saves replay each applied selection and then rebuild once at the physics handoff in `PrepareFrame` (`frame:prepare`). The rock field is drawn in `render:scene-opaque`; a band swap is visible to the planet's ring shadow and the atmospheric ring shadow on the next frame without any rebuild, because both read `RingsReference.Texture` every frame. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Discover and snapshot once per tree**, keyed by the `PlanetaryRingsReference` instance: per-LOD meshes, the three material textures, the band texture and the four field-setting reference objects:

```csharp
// rocky-mcrock-face.lib/RingSwapController.cs L287-L306
private static Snapshot TakeSnapshot(PlanetaryRingsReference rings)
{
    var objects = rings.RingObjects;
    var lodMeshes = new MeshReference?[objects.NumLods];
    for (int i = 0; i < objects.NumLods; i++)
        lodMeshes[i] = objects.Lods[i].MeshFileReference?.Get().Mesh;

    return new Snapshot
    {
        LodMeshes = lodMeshes,
        Diffuse = objects.MaterialReference.DiffuseReference,
        Normal = objects.MaterialReference.NormalReference,
        Pbr = objects.MaterialReference.PBRMap,
        BandTexture = rings.Texture,
        Size = objects.Size,
        Density = objects.Density,
        RenderDistance = objects.RenderDistance,
        Thickness = objects.Thickness,
    };
}
```

2. **Resolve everything first**, then mutate: a failed lookup leaves the game untouched. Assign `MeshFileReference.Mesh` per LOD and the material textures, and either new `DistanceReference` / `DoubleReference` objects for the field settings or the snapshotted originals (new objects are assigned, never edited in place, so the snapshot stays pristine):

```csharp
// rocky-mcrock-face.lib/RingSwapController.cs L129-L152
for (int i = 0; i < lodCount; i++)
{
    var fileReference = objects.Lods[i].MeshFileReference?.Get();
    if (fileReference != null) fileReference.Mesh = meshes[i];
}
objects.MaterialReference.DiffuseReference = diffuse;
objects.MaterialReference.NormalReference = normal;
objects.MaterialReference.PBRMap = pbr;
if (band != null) rings.Texture = band;

if (selection.OverrideFieldSettings)
{
    objects.Size = new DistanceReference(selection.SizeM);
    objects.Density = DoubleReference.FromValue(selection.DensityPerKm3);
    objects.RenderDistance = new DistanceReference(selection.RenderDistanceKm, DistanceUnit.Kilometers);
    objects.Thickness = new DistanceReference(selection.ThicknessKm, DistanceUnit.Kilometers);
}
else
{
    objects.Size = defaults.Size;
    objects.Density = defaults.Density;
    objects.RenderDistance = defaults.RenderDistance;
    objects.Thickness = defaults.Thickness;
}
```

3. **Make interleaved meshes ring-usable.** The ring field draws each LOD with a `SimpleVkMesh`, which the game only builds for meshes loaded `Simple`. Part meshes come from atlases loaded `Interleaved` and their device mesh slot is empty. Clone: a new `MeshReference { Simple = true, Interleaved = false, PrimitiveCount = 1 }`, point its private-set `HostPrimitives` at the source's retained CPU geometry through the compiler backing field, and call the game's own `Bind` with a private `StagingPool` (disposal submits and waits, so the upload is complete on return). Do not flip the source's flags: it is shared with `PartModel` rendering, and IVA ray tracing throws "Subpart mesh is not interleaved" for such a mesh.

```csharp
// rocky-mcrock-face.lib/RingMeshFactory.cs L37-L88
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
    if (clone.BoundingSphereRadius <= 0.0)
    {
        var host = source.HostPrimitives[0];
        clone.BoundingSphereRadius = Math.Max(host.PositionMaximum.Length(), host.PositionMinimum.Length());
    }
    if (clone.BoundingSphereRadius <= 0.0)
    {
        error = $"mesh '{source.Id}' has a zero bounding radius";
        return null;
    }

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

   Meshes that never enter `ModLibrary.AllMeshes` (kitten and equipment glTF files) are imported with `GltfLoader`, a `Simple` `MeshReference` and `Load(gltfLoader, meshIndex, createDeviceMesh: false)`, then bound the same way; the ring draws only the first primitive.

4. **Recreate the rings renderer** (device wait, `Dispose`, clear `_ringRendererCreated`, then `Program.RebuildRenderer()`); `_anyRings` is already true, so the game takes its create branch:

```csharp
// rocky-mcrock-face.lib/RingSwapController.cs L208-L249
public bool RebuildRenderer(out string message)
{
    try
    {
        var program = Program.Instance;
        if (program == null)
        {
            message = "game renderer not ready";
            return false;
        }
        DisposeRingsRendererForRecreation();
        program.RebuildRenderer();
        message = "renderer rebuilt";
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"rocky-mcrock-face: renderer rebuild failed: {ex}");
        message = $"renderer rebuild failed: {ex.Message}";
        return false;
    }
}

/// <summary>True while the game's planetary rings renderer exists.</summary>
public bool IsRingsRendererCreated()
{
    var transparencies = ReflectionHelpers.GetFieldValue(Program.Instance, "_planetTransparenciesRenderer");
    return ReflectionHelpers.GetFieldValue(transparencies, "_ringRendererCreated") is true;
}

private static void DisposeRingsRendererForRecreation()
{
    var transparencies = ReflectionHelpers.GetFieldValue(Program.Instance, "_planetTransparenciesRenderer");
    if (transparencies == null) return;
    if (ReflectionHelpers.GetFieldValue(transparencies, "_ringRendererCreated") is not true) return;
    if (ReflectionHelpers.GetFieldValue(transparencies, "_ringsRenderer") is not PlanetaryRingsRenderer ringsRenderer)
        return;
    // In-flight frames may still reference ring pipelines/buffers.
    Program.GetRenderer().Device.WaitIdle();
    ringsRenderer.Dispose();
    ReflectionHelpers.SetFieldValue(transparencies, "_ringRendererCreated", false);
}
```

5. **Prune clones** that no selection references, only after a successful rebuild (the device is idle then). `MeshReference.Dispose` disposes only the clone's own `DevicePrimitives`; the shared `HostPrimitives` stay owned by the source.

6. **Restore Defaults** writes the snapshot back and rebuilds the same way.

## Complete example

| File | Role |
|---|---|
| [rocky-mcrock-face.lib/RingSwapController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face.lib/RingSwapController.cs) | Snapshot, apply, restore, dispose-and-rebuild |
| [rocky-mcrock-face.lib/RingMeshFactory.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face.lib/RingMeshFactory.cs) | Interleaved-to-simple mesh clones |
| [rocky-mcrock-face.lib/RingAssetCatalog.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face.lib/RingAssetCatalog.cs) | Meshes and textures loaded in ModLibrary, plus glTF entries |
| [rocky-mcrock-face.lib/RingSelection.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face.lib/RingSelection.cs) | Per-body override model (empty keeps the stock asset) |

## Variations and gotchas

- **Cannot add or remove a ring.** Rocky never touches `PlanetTransparenciesRenderer.PopulatePlanets` or `_anyRings`; use approach 1 for that.
- **Reflection into private-set properties.** `MeshReference.HostPrimitives` is written through `<HostPrimitives>k__BackingField`; a game update that renames it makes the factory report an error instead of failing silently.
- **Staging pool.** A `Bind` without a staging pool is not possible; keep its lifetime to one call.
- **Free late.** A clone's buffers must not be destroyed while a submitted frame still draws them; free only right after a rebuild that has waited for the device.

## See also

- [Overview](overview.md); [approach 1](add-or-restyle-planetary-rings-1.md)
- [Ring asset swap](../../../../mods/unscience/features/rocky-mcrock-face-ring-asset-swap.md), [Interleaved mesh clone](../../../../mods/unscience/features/rocky-mcrock-face-interleaved-mesh-clone.md)
- [Handle renderer rebuilds](../../../rendering/vulkan-passes/handle-renderer-rebuilds/overview.md), [Change loaded game data and make the renderer notice](../../../celestials/atmospheres-and-rings/change-loaded-game-data-and-make-the-renderer-notice/overview.md)
