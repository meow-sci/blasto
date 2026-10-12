---
type: How-To
title: Take north from the body's spin axis and query terrain height in the same frame
description: Build an east-north-up frame at a surface point from the rotation axis (not the ecliptic pole) and query terrain height with Celestial.GetTerrainHeightFromDirCce, with the sea clamp and the accuracy flag handled
category: flight-and-orbits
group: transfers
topic: build-a-site-frame-and-compute-a-launch-window
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/terrain-height-queries]
techniques: [frame-conversion]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Celestial, KSA.IParentBody, KSA.Astronomical, KSA.Double3Ex]
tags: [terrain, east-north-up, spin-axis, height-field, ground-test, map-frame]
---

Outcome: a local map frame at a surface point (metres east, north, up) whose north follows the body's own spin axis, and ground height along any direction from the body's centre. KSAGolf builds its ball ground, cursor ground point, course floor and draped rings on these.[Terrain height queries off the engine's height field](../../../../mods/ksagolf/features/terrain-height-queries.md) Prerequisite: StarMap.

## Background: how the game does it

KSA has no terrain raycast a mod can use. `Celestial.GetTerrainHeightFromDirCcf(direction, accurate = true)` returns the height above the mean sphere along a direction from the body's centre (0 with no heightmap); `GetTerrainHeightFromDirCce` applies `GetCcf2Cce().Inverse()` for you, and `GetTerrainHeightFromDirCci` likewise.[^ksa-celestial] [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L777-L795)
`accurate` changes more than interpolation: `true` uses bicubic sampling and runs the procedural modifiers (erosion, detail, dunes, launch-site levelling), `false` uses bilinear and drops them on a body with biome materials. Vehicle physics stands on `accurate: true` (`PhysicsEnvironment.TerrainRadius` through `TerrainRadiusCache`).[^ksa-physicsenvironment]
The spin axis: `Celestial.GetRotationAxisCce()` is `UnitZ.Transform(GetCcf2Cce())`.[current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L614-L617)

## Lifecycle and timing

Every query is synchronous and runs on the main thread inside the mod's GUI hook (`[StarMapAfterGui]`, `frame:draw-ui`). It reads template data nothing mutates during flight plus the body's cached `Ccf`-to-`Cce` rotation, refreshed once per frame by `Celestial.UpdatePerFrameData` in `Universe.ApplyVehicleSolvers`, so every caller in one frame agrees on the planet's phase. The answer is at the frame's end in both of the body's motions, so a mover
sub-stepping part-way through a frame must walk its query point forward by the ground's velocity itself. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Build the frame** with east = `axis x up`, north = `up x east`, up radial. North comes from the body's spin axis in ECL, not from the ecliptic pole (23 degrees apart on Earth); return null at the poles where east is undefined rather than picking an arbitrary perpendicular:

```csharp
// src/KSAGolf/Sim/TerrainMap.cs L26-L40
public static MapFrame? TryAt(double3 centreEcl, double3 anchorEcl, double3 axisEcl)
{
    double3 radial = anchorEcl - centreEcl;
    double radius = Vec.Len(radial);
    if (!(radius > 1.0) || !Vec.IsFinite(radial)) return null;

    double3 up = radial / radius;

    double3 east = Vec.Cross(axisEcl, up);
    if (Vec.Len2(east) < 1e-12 || !Vec.IsFinite(east)) return null;

    east = Vec.Unit(east);

    return new MapFrame(centreEcl, up, east, Vec.Cross(up, east), radius);
}
```

2. **Map points as differences against the anchor,** sampled in the same frame, so the ecliptic motion cancels (see [Understand KSA coordinate frames and epochs (ECL, CCE, CCI, CCF, ego, body)](../../flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)):

```csharp
// src/KSAGolf/Sim/TerrainMap.cs L48-L53
public double3 ToLocal(double3 positionEcl)
{
    double3 from = positionEcl - (Centre + Up * Radius);

    return new double3(Vec.Dot(from, East), Vec.Dot(from, North), Vec.Dot(from, Up));
}
```

3. **Choose the right entry point.** A direction built from `position - centre` in ECL belongs in the `Cce` entry; a body-fixed one in the `Ccf` entry. Passing either to the other rotates it by the planet's current phase.
4. **Query the height and clamp to the sea.** The height field answers with the seabed under an ocean, so a ball would fall through the waterline; take the higher of terrain and sea level when `GetOceanReference()` is non-null with `Density > 0`:

```csharp
// src/KSAGolf/Ksa/GroundTest.cs L116-L138
centreEcl = nearest.GetPositionEcl();

double3 dirCce = Vec.Unit(positionEcl - centreEcl);
if (!Vec.IsFinite(dirCce) || Vec.Len(dirCce) < 0.5) return false;

double height = nearest.GetTerrainHeightFromDirCce(dirCce, accurate: true);
if (!double.IsFinite(height)) return false;

// The height field answers with terrain, so under an ocean it reports the seabed. A
// ball would fall through the waterline and land on the bottom, unseen. Same query
// KsaWorld.MediumDensityRatioAt uses to know it is in water.
double seaLevel = 0.0;
bool hasSea = false;
if (nearest.GetOceanReference() is { } sea && sea.Density > 0.0)
{
    hasSea = true;
    seaLevel = sea.Level;
}

height = GroundSurface.Height(height, seaLevel, hasSea);

surfaceRadius = nearest.MeanRadius + height;
return surfaceRadius > 0.0;
```

5. **Answer a centre and a radius, not an altitude,** so a body samples the ground once a frame and then knows its height at every sub-step for the cost of a subtraction.
6. **Bound the terrain correctly.** For ridge occlusion use `Astronomical.MaxTerrainRadius - MeanRadius` plus headroom for the procedural modifiers; avoid `Celestial.MaxTerrainHeightApprox`, a 16,384-direction sample taken in the constructor that underestimates peaks.

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/GroundTest.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/GroundTest.cs#L38-L144) | nearest body by depth with a cached shortcut, height query, sea clamp |
| [src/KSAGolf/Ksa/TerrainHeights.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/TerrainHeights.cs) | one body's height field behind `ITerrainHeights` |
| [src/KSAGolf/Sim/TerrainMap.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/TerrainMap.cs) | `MapFrame` local east/north frame and helpers |
| [src/KSAGolf/Sim/GroundSlope.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/GroundSlope.cs) | gradient slope, roughness |
| [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L743-L766) | `BodyGround`, the `Ccf` caller the ball uses |

## Variations and gotchas

- **`ITerrainHeights` returns "no claim", never zero:** reading zero puts a whole horizon on the mean sphere.
- **Single-precision modifiers.** The direction is packed with `float3.Pack` before the modifier loop, a staircase of about a third of a metre on Earth.
- **Early returns:** no heightmap returns 0; no cubemap normal map or no biome materials returns the base interpolated height.
- **Collision patch grid is 2 m**; do not expect finer ground than that for contact.

## See also

- [Overview](overview.md); [launch window](build-a-site-frame-and-compute-a-launch-window-1.md); [site-frame bridge](build-a-site-frame-and-compute-a-launch-window-2.md)
- [Terrain height queries off the engine's height field](../../../../mods/ksagolf/features/terrain-height-queries.md)
- [Understand KSA coordinate frames and epochs (ECL, CCE, CCI, CCF, ego, body)](../../flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)

[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-physicsenvironment]: KSA.PhysicsEnvironment (decompiled, build 2026.10.10.5554)
