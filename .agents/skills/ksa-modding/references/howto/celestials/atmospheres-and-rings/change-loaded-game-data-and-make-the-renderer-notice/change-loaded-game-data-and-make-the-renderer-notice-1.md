---
type: How-To
title: Snapshot, mutate the public tree and recreate one renderer by reflection
description: Change a body's loaded reference data, then dispose a single renderer and clear its created flag so the game's own rebuild constructs it again from the new data, with a snapshot for exact restore
category: celestials
group: atmospheres-and-rings
topic: change-loaded-game-data-and-make-the-renderer-notice
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/rocky-mcrock-face-ring-asset-swap]
techniques: [reflection]
hooks:
  - reflection KSA.PlanetTransparenciesRenderer._ringRendererCreated
  - reflection KSA.PlanetTransparenciesRenderer._ringsRenderer
lifecycle: [frame:draw-ui, frame:prepare]
game_types: [KSA.Program, KSA.PlanetTransparenciesRenderer, KSA.PlanetaryRingsReference]
tags: [renderer-rebuild, snapshot, restore, reflection]
---
Outcome: a change to loaded data (here, the meshes and textures of Saturn's rings) shows up on screen after one renderer is recreated, and restoring the snapshot brings the stock look back. Demonstrated by unscience's Rocky McRock Face ([Ring asset swap](../../../../mods/unscience/features/rocky-mcrock-face-ring-asset-swap.md)); the full ring recipe is in [add or restyle planetary rings](../add-or-restyle-planetary-rings/add-or-restyle-planetary-rings-2.md). This article is the generic pattern.

## Background: how the game does it

The rings renderer bakes the ring reference tree into per-body render data in its constructor; `RebuildFrameResources` on the planet transparencies renderer calls the rings renderer's own rebuild only while `_ringRendererCreated` is true, and that never re-reads data ([current/decomp/KSA/PlanetTransparenciesRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetTransparenciesRenderer.cs#L374-L430)). If the flag is false and `_anyRings` is true, the game takes its create branch and builds a new renderer from the current references ([current/decomp/KSA/PlanetTransparenciesRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetTransparenciesRenderer.cs#L128-L152)). So the trick is to dispose the one renderer, clear its flag, and let the game's rebuild create it again.

## Lifecycle and timing

The mutation and rebuild run from the panel's button handler in `[StarMapAfterGui]` (`frame:draw-ui`), which is after `PrepareFrame` and before `ImGui.Render`, the swapchain acquire and `RenderGame`: no command buffer for this frame exists. Wait for the device first, because in-flight frames may still reference the old buffers; `RebuildRenderer` waits again itself. On a save load the same path runs at the physics handoff in `PrepareFrame` (`frame:prepare`), before ImGui starts. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [handle renderer rebuilds](../../../rendering/vulkan-passes/handle-renderer-rebuilds/overview.md).

## Steps

1. **Snapshot every field you will touch**, once, keyed by the reference instance, before the first write:

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

2. **Resolve everything first, then write.** A failed lookup leaves the game untouched:

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

3. **Recreate the renderer.** Device wait, dispose, clear the flag, then `Program.RebuildRenderer()`:

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

4. **Restore is the same path** with the snapshot written back.

## Complete example

| File | Role |
|---|---|
| [rocky-mcrock-face.lib/RingSwapController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/rocky-mcrock-face.lib/RingSwapController.cs) | Snapshot, apply, restore, dispose-and-rebuild |

## Variations and gotchas

- **Calling `RebuildRenderer()` alone hitches but changes nothing** while the renderer exists, because only frame resources are rebuilt.
- **Which fields are private** differs per renderer; look at its constructor, its `RebuildFrameResources` and where the data is read, and use the lightest lever that works.
- **Some consumers read every frame** (the planet renderer's ring shadow reads `RingsReference.Texture`), so a band swap shows there without any rebuild.

## See also

- [Overview](overview.md); [approach 2](change-loaded-game-data-and-make-the-renderer-notice-2.md); [approach 3](change-loaded-game-data-and-make-the-renderer-notice-3.md)
- [Ring asset swap](../../../../mods/unscience/features/rocky-mcrock-face-ring-asset-swap.md)
- [Add or restyle planetary rings](../../../celestials/atmospheres-and-rings/add-or-restyle-planetary-rings/overview.md)
