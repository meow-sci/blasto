---
type: How-To
title: Gradient from centred east, west, north and south differences
description: Measure local ground slope as the magnitude of a centred gradient on a body-fixed east and north frame, with a ring of spokes kept for roughness and an aim-loop gain derived from the arrival angle
category: celestials
group: terrain
topic: measure-slope-and-detect-ground-contact-on-terrain
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/terrain-height-queries]
techniques: [closed-form-geometry, custom-physics]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Celestial, KSA.Astronomical]
tags: [terrain, slope, gradient, local-frame, east-north]
---
Outcome: a number for "how steep is the ground within `metres` of this point" that does not depend on which way the slope faces, plus a local east and north frame to sample in. Demonstrated by KSAGolf ([Terrain height queries](../../../../mods/ksagolf/features/terrain-height-queries.md)), which uses it to judge a site before aiming a shot on it. Prerequisite: the ground query from [the height-query topic](../query-terrain-height-and-line-of-sight/query-terrain-height-and-line-of-sight-1.md).

## Background: how the game does it

KSA gives no slope accessor. Slope has to come from `GetTerrainHeightFromDirCce(dir, accurate)` sampled along nearby directions ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L777-L781)). The answer depends on the distance between samples: the base grid is kilometres, the detail textures 7-20 m a texel on Earth, and below about a third of a metre the single-precision direction packing makes the surface a staircase (KSAGolf's analysis in [docs/KSA-TERRAIN.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/docs/KSA-TERRAIN.md)). So the sampling distance is part of the question, not a detail.

## Lifecycle and timing

Everything runs on the main thread from the mod's frame step (`[StarMapAfterGui]`, `frame:draw-ui`). The queries read static template data and the body's cached rotation from `Celestial.UpdatePerFrameData`, so within one frame all samples agree. In the shipping mod this helper is documented and tested but not wired to a live caller; treat the code as a pattern. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Build a local frame with north from the spin axis, not the ecliptic.** Up is the radial direction; east is `axis x up`; north is `up x east`. A map whose north is the ecliptic pole is wrong by the body's obliquity (23 degrees on Earth). Return nothing at the poles, where axis and up coincide:

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

2. **Turn a metre offset into a direction to query.** The height field is asked along a direction from the centre through a point this far east and north of the anchor:

```csharp
// src/KSAGolf/Sim/TerrainMap.cs L71-L72
public double3 DirectionAt(double east, double north)
    => Vec.Unit(Up * Radius + East * east + North * north);
```

3. **Take the gradient from centred differences.** Centring cancels a constant tilt exactly; the slope is the gradient magnitude, the same number whichever way the plane tilts. A median of a ring of spokes is wrong here: a spoke along a contour reads zero, so the ring median halves a planar slope.

```csharp
// src/KSAGolf/Sim/GroundSlope.cs L79-L93
// The gradient, centred so a constant tilt cancels out of it exactly.
double east = radiusAt(frame.DirectionAt(+metres, 0.0));
double west = radiusAt(frame.DirectionAt(-metres, 0.0));
double north = radiusAt(frame.DirectionAt(0.0, +metres));
double south = radiusAt(frame.DirectionAt(0.0, -metres));

if (!double.IsFinite(east) || !double.IsFinite(west)
    || !double.IsFinite(north) || !double.IsFinite(south))
{
    return false;
}

double dx = (east - west) / (2.0 * metres);
double dy = (north - south) / (2.0 * metres);
double slope = Math.Sqrt(dx * dx + dy * dy);
```

4. **Keep the ring for roughness.** A plane reads the same on every spoke; a gully reads far worse on the worst one:

```csharp
// src/KSAGolf/Sim/GroundSlope.cs L95-L105
// And a ring besides, for what a gradient cannot say: whether the ground is merely tilted
// or actually rough. A plane reads the same on both; a gully reads far worse on this one.
double worst = 0.0;
for (int i = 0; i < spokes; i++)
{
    double a = 2.0 * Math.PI * i / spokes;
    double there = radiusAt(frame.DirectionAt(Math.Cos(a) * metres, Math.Sin(a) * metres));
    if (!double.IsFinite(there) || there <= 0.0) continue;

    worst = Math.Max(worst, Math.Abs(there - here) / metres);
}
```

5. **Convert to a loop gain if you aim at the ground.** A residual miss `d` lands on ground `slope * d` higher or lower, which the arrival angle `gamma` turns into `slope * d / tan(gamma)` of further miss. Gain `slope / tan(gamma)` below one converges to `1 / (1 - gain)` times the flat-ground miss; at one or more there is no fixed point and the ground decides where the ball stops:

```csharp
// src/KSAGolf/Sim/GroundSlope.cs L107-L110
double gain = slope / Math.Tan(arrivalRadians);

reading = new Reading(slope, worst, gain,
                      gain < GainWithNoFixedPoint ? 1.0 / (1.0 - gain) : double.PositiveInfinity);
```

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Sim/GroundSlope.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/GroundSlope.cs) | Gradient, roughness, gain and amplification |
| [src/KSAGolf/Sim/TerrainMap.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/TerrainMap.cs) | `MapFrame` east/north/up frame and `DirectionAt` |
| [src/KSAGolf/Ksa/GroundTest.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/GroundTest.cs) | The `radiusAt` function it is fed (terrain plus sea) |

## Variations and gotchas

- **Choose the radius for your scale** and ask at more than one if unknown; the field answers differently at kilometre, metre and sub-metre scales.
- **Use `accurate: true`** if levelling decals must count; the fast path drops them (see the height-query topic).
- **Sea.** Over water the field is the seabed; clamp as in the ground test or the slope under a lake reads as seabed relief.
- **No claim is not flat.** If any of the four samples is not finite, give no reading rather than a slope of zero.

## See also

- [Overview](overview.md); [approach 2](measure-slope-and-detect-ground-contact-on-terrain-2.md); [approach 3](measure-slope-and-detect-ground-contact-on-terrain-3.md)
- [Terrain height queries](../../../../mods/ksagolf/features/terrain-height-queries.md)
- [Query the terrain height field and test line of sight](../../../celestials/terrain/query-terrain-height-and-line-of-sight/overview.md)
