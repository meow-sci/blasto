---
type: How-To
title: Reflect the mapped buffer and slot, clamp when the camera is near, restore on leaving
description: Change one biome material value in the planet renderer's persistently mapped storage buffer without a rebuild, only when the camera crosses a threshold, saving the original and restoring it afterwards
category: celestials
group: atmospheres-and-rings
topic: change-loaded-game-data-and-make-the-renderer-notice
approach: 4
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksastructures/features/live-terrain-material-override]
techniques: [reflection]
hooks:
  - starmap StarMapAfterGui
  - reflection KSA.PlanetRenderer._biomeMaterialsMap
  - reflection KSA.PlanetRenderer._biomeMaterialSlot
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame, render:scene-opaque]
game_types:
  - KSA.PlanetRenderer
  - KSA.BiomeMaterialReference
  - KSA.BiomeMaterialsReference
  - KSA.TerrainReference
  - KSA.Celestial
  - KSA.Camera
tags: [biome-materials, mapped-buffer, reflection, in-place, terrain]
---
Outcome: the slope (cliff) displacement near a dug site is held down to the grass's value while the camera is within 300 m of it, and put back when the camera leaves, with no renderer rebuild. Demonstrated by KSAStructures ([Live terrain material override](../../../../mods/ksastructures/features/live-terrain-material-override.md)). This is the in-place alternative to a rebuild.

## Background: how the game does it

The `PlanetRenderer` constructor allocates one host-visible, host-coherent storage buffer for every body's biome materials, maps it persistently as `_biomeMaterialsMap`, and records each body's offset in `_biomeMaterialSlot` ([current/decomp/KSA/PlanetRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetRenderer.cs#L664-L705)). It writes two `PlanetRenderer.BiomeMaterial` structs per biome in the order of `TerrainReference.BiomeMaterials.Materials`: index `k*2` is the ground material and `k*2+1` the slope material, each from `BiomeMaterialReference.ToBiomeMaterial` ([current/decomp/KSA/BiomeMaterialReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BiomeMaterialReference.cs#L53-L98)):

```csharp
// current/decomp/KSA/PlanetRenderer.cs L698-L705
for (int k = 0; k < list.Count; k++)
{
	mappedMemory = _biomeDataMap.Offset(byteSize);
	mappedMemory.AsSpan<BiomeRenderData>()[k] = list[k].ToRenderData(k);
	mappedMemory = _biomeMaterialsMap.Offset(byteSize2);
	mappedMemory.AsSpan<BiomeMaterial>()[k * 2] = list[k].ToBiomeMaterial(k, CelestialRenderData.BiomeMaterialType.Ground);
	mappedMemory = _biomeMaterialsMap.Offset(byteSize2);
	mappedMemory.AsSpan<BiomeMaterial>()[k * 2 + 1] = list[k].ToBiomeMaterial(k, CelestialRenderData.BiomeMaterialType.Slope);
```

Nothing rewrites it per frame, and the game's own terrain editor edits the same mapped memory in place ([current/decomp/KSA/PlanetRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetRenderer.cs#L2233-L2248)). So a mod can do the same.

## Lifecycle and timing

The mod's step calls this every frame with the flown craft's parent body: normally from `[StarMapAfterGui]` (`frame:draw-ui`), from `frame:celestials` when the UI is hidden and from `[StarMapAfterOnFrame]` as the last fallback. It only writes when the near/far answer changes, so the buffer is touched twice per visit. The write is a plain CPU store into coherent mapped memory on the main thread: the next terrain draw (`render:scene-opaque`) sees it. The buffer is not duplicated per frame in flight, so a frame still executing on the GPU may also see it; the engine's editor does the same. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Find the buffer and the body's slot by reflection** from `Program.GetPlanetRenderer()`. If either field is missing or the slot is not there, log once and disable the feature for the session.

2. **Decide near or far** from a stable site position (the landmark's `ForwardCcf` through `GetSurfacePositionEclFromCce`) and the main camera; return early when the answer has not changed.

3. **Write the struct field, saving the original.** For each biome named `Grass` or `Beach` save the slope entry's `displacementScale` and set it to `min(slope, ground)`; on leaving, write the saved values back:

```csharp
// src/KSAStructures/Ksa/OfficeTerrain.cs L176-L209
if (earth?.BodyTemplate?.TerrainReference?.BiomeMaterials is not { } biomes) return;

PlanetRenderer renderer = Program.GetPlanetRenderer();
const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
if (typeof(PlanetRenderer).GetField("_biomeMaterialsMap", Private)?.GetValue(renderer) is not MappedMemory map
    || typeof(PlanetRenderer).GetField("_biomeMaterialSlot", Private)?.GetValue(renderer) is not Dictionary<Celestial, ByteSize> slots
    || !slots.TryGetValue(earth, out ByteSize slot))
{
    _tameFailed = true;
    Log.Warn("office: the planet's material buffer has moved; rock may show at the garage's rim from close by");
    return;
}

// Two entries a biome, ground then slope, in the template's own order.
Span<PlanetRenderer.BiomeMaterial> materials = map.Offset(slot).AsSpan<PlanetRenderer.BiomeMaterial>();
for (int i = 0; i < biomes.Materials.Count; i++)
{
    if (biomes.Materials[i].BiomeName is not ("Grass" or "Beach")) continue;

    int ground = i * 2, slope = ground + 1;
    if (slope >= materials.Length) continue;

    if (near)
    {
        CliffDisplacement[slope] = materials[slope].displacementScale;
        materials[slope].displacementScale = Math.Min(materials[slope].displacementScale, materials[ground].displacementScale);
    }
    else if (CliffDisplacement.TryGetValue(slope, out float was))
    {
        materials[slope].displacementScale = was;
    }
}

_tamed = near;
```

4. **Pass `null` when the player has left the body**; it counts as "far" and restores the values.

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/OfficeTerrain.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeTerrain.cs#L150-L216) | `TameTheRim` |
| [src/KSAStructures/Ksa/OfficeLift.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeLift.cs#L110-L111) | Called from `Office.Update` with the flown craft's body |

## Variations and gotchas

- **Planet-wide, not local.** The value applies to every Grass and Beach slope on Earth while the camera is near; it works here only because no other cliff is within the 50 m at which displacement is drawn.
- **Biome names and order are data.** The code matches `BiomeName` strings and assumes the template order is the buffer order; a planet pack that renames biomes turns the fix into a no-op.
- **A renderer rebuild does not restore stock values.** The buffer is written only in the `PlanetRenderer` constructor (and by its debug editor); `Program.RebuildRenderer` calls `PlanetRenderer.RebuildFrameResources`, which rebuilds pipelines and ground clutter and leaves the mapped buffer alone, so the mod must write the saved values back itself.
- **Restore on the way out.** A mod that unloads while near the site leaves the clamped value in the buffer until the `PlanetRenderer` is constructed again, which in practice means the next game start.
- **Private fields** are found by name; the first failure disables the feature with one warning.

## See also

- [Overview](overview.md); [approach 3](change-loaded-game-data-and-make-the-renderer-notice-3.md)
- [Live terrain material override](../../../../mods/ksastructures/features/live-terrain-material-override.md)
- [Add terrain decals and dig a site](../../../celestials/terrain/add-terrain-decals-and-dig-a-site/overview.md)
