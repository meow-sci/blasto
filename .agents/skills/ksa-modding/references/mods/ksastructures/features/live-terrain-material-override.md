---
type: Mod Feature
title: Terrain slope displacement held down live in the planet renderer's material buffer
description: While the camera is near the site, reflects into PlanetRenderer's persistently mapped biome-material buffer and clamps the Grass and Beach slope materials' displacementScale to their ground materials', restoring the stored values when the camera leaves
mod: ksastructures
category: celestials
techniques: [reflection, vulkan-buffer]
hooks:
  - starmap StarMapAfterGui
  - reflection KSA.PlanetRenderer._biomeMaterialsMap
  - reflection KSA.PlanetRenderer._biomeMaterialSlot
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types:
  - KSA.PlanetRenderer
  - KSA.BiomeMaterialReference
  - KSA.BiomeMaterialsReference
  - KSA.TerrainReference
  - KSA.Celestial
  - KSA.Camera
  - KSA.Program
tags: [terrain, biome-material, displacement, mapped-buffer, reflection, planet-renderer]
---

The garage pit's rim is drawn by the terrain shader with the slope (cliff) material, and close to the
camera that material is displaced outward far more than grass is: through the ground cover mesh the
mod lays over the dug ground. The mod does not touch any asset to fix this. While the camera is within
300 m of the site it overwrites one float per biome in the planet renderer's live material buffer,
the same buffer KSA's own terrain debug editor writes, and puts the old values back when the camera
leaves.[^mod-terrain][^mod-claude]

## How it works

1. **The buffer.** The `PlanetRenderer` constructor allocates one host-visible, host-coherent
   storage buffer for every body's biome materials, maps it persistently (`_biomeMaterialsMap`), and
   records each body's offset in `_biomeMaterialSlot`. It writes two `PlanetRenderer.BiomeMaterial`
   structs per biome, in the order of `TerrainReference.BiomeMaterials.Materials`: index `k*2` is the
   ground material and `k*2+1` the slope material, each from `BiomeMaterialReference.ToBiomeMaterial`.[^ksa-planetrenderer][^ksa-biomematerialreference]
   Nothing rewrites it per frame; KSA's own terrain editor (`PlanetRenderer.OnDrawUI`, drawn from
   `Program.OnDrawUiFrame`) edits the same mapped memory in place.[^ksa-planetrenderer][^ksa-program]
2. **Finding it.** `TameTheRim` reads both private fields from `Program.GetPlanetRenderer()` by
   reflection and looks up Earth's slot. If either field is missing or the slot is not there, it logs
   once and disables itself for the session (`_tameFailed`).[^mod-terrain]
3. **Near or far.** The site position comes from the tower landmark's `ForwardCcf` through
   `GetSurfacePositionEclFromCce`, and "near" is the main camera within 300 m of it. The function
   only acts when that answer changes (`near == _tamed` returns early), so the buffer is written twice
   per visit, not every frame.[^mod-terrain]
4. **The write.** For each biome named `Grass` or `Beach` it saves the slope entry's
   `displacementScale` and sets it to `min(slope, ground)`; on leaving it writes the saved values
   back. The body's template and the files on disk never change, so a renderer rebuild or a restart
   restores stock values.[^mod-terrain]
5. **Who calls it.** `Office.Update` calls `TameTheRim` every step with the flown craft's parent body
   when that body is Earth, and with `null` otherwise, which counts as "far" and restores the values
   after the player leaves Earth.[^mod-lift]

## Lifecycle and timing

| When | Where | What |
|---|---|---|
| Startup (`Program` constructor) | `new PlanetRenderer` | Buffer allocated, mapped and filled once |
| The mod's step (normally `frame:draw-ui`; `frame:celestials` with the UI hidden; `frame:after-on-frame` as the last fallback) | `StepOnce` -> `Office.Update` -> `TameTheRim` | Clamp or restore when the camera crosses 300 m |
| `render:scene-opaque` | Terrain draw in `RenderGame` | Shaders read the buffer |

The write is a plain CPU store into coherent mapped memory on the main thread, so the next terrain
draw recorded after it sees the new value; when the step lands in the `[StarMapAfterOnFrame]`
fallback, that is the next frame's draw. The buffer is not duplicated per frame in flight, so a frame
still executing on the GPU may also see it; the engine's own editor writes it the same way.[^ksa-planetrenderer]

## Key code

| Path | Role |
|---|---|
| [src/KSAStructures/Ksa/OfficeTerrain.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeTerrain.cs#L150-L216) | `TameTheRim` |
| [src/KSAStructures/Ksa/OfficeLift.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeLift.cs#L110-L111) | Called from `Office.Update` with the flown craft's body |

## Snippets

Holding the slope material's displacement down in the live buffer:

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

The layout it relies on, as the engine fills it:

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

## Game APIs touched

| API | Decomp | Use |
|---|---|---|
| `PlanetRenderer.BiomeMaterial` (public struct, `displacementScale`) | [current/decomp/KSA/PlanetRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetRenderer.cs#L140-L157) | Per-material shader parameters |
| `PlanetRenderer._biomeMaterialsMap`, `_biomeMaterialSlot` (private) | [current/decomp/KSA/PlanetRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetRenderer.cs#L664-L705) | Buffer creation, mapping and fill at construction |
| `PlanetRenderer.OnDrawUI` | [current/decomp/KSA/PlanetRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetRenderer.cs#L2233-L2248) | The engine's own live editor of the same memory |
| `BiomeMaterialReference.ToBiomeMaterial` | [current/decomp/KSA/BiomeMaterialReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BiomeMaterialReference.cs#L53-L98) | Ground and slope entries from the template |
| `Program.GetPlanetRenderer` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L562-L565) | The renderer instance |

## Pitfalls and version notes

- **Planet-wide, not local.** The value applies to every Grass and Beach slope on Earth while the
  camera is near the site. It only works here because no other cliff is within the 50 m at which the
  displacement is drawn (the mod's note).[^mod-claude]
- **Biome names and order are data.** The code matches `BiomeName` strings and assumes the template
  order is the buffer order; a planet pack that renames biomes turns the fix into a no-op.
- **Private fields.** `_biomeMaterialsMap` and `_biomeMaterialSlot` are found by name; the first
  failure disables the feature with one warning.
- **Restore on the way out.** The stored values are written back only when the camera leaves; a mod
  that unloads while near the site leaves the clamped value in the buffer until the renderer is rebuilt.
- **Built against 2026.10.7.5541**; the fields and struct are unchanged in 2026.10.10.5554.

## See also

- [Terrain decal pits](./terrain-decal-pits.md) - the pits whose rim this hides
- [Terrain height roof override](./terrain-height-roof-override.md) - the other per-camera answer over the dug ground
- [Reflect the mapped buffer and slot, clamp when the camera is near, restore on leaving](../../../howto/celestials/atmospheres-and-rings/change-loaded-game-data-and-make-the-renderer-notice/change-loaded-game-data-and-make-the-renderer-notice-4.md) - how-to article demonstrating this feature

[^mod-terrain]: src/KSAStructures/Ksa/OfficeTerrain.cs
[^mod-lift]: src/KSAStructures/Ksa/OfficeLift.cs
[^mod-claude]: KSAStructures CLAUDE.md (engine facts section)
[^ksa-planetrenderer]: KSA.PlanetRenderer (decompiled, build 2026.10.10.5554)
[^ksa-biomematerialreference]: KSA.BiomeMaterialReference (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
