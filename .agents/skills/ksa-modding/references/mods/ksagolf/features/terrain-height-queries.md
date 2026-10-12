---
type: Mod Feature
title: Terrain height queries off the engine's height field
description: How KSAGolf asks the engine where the ground is, through one Celestial height query wrapped as a ground test, a height-field seam, a ridge mask, a slope reading and a local east/north frame, with the sea clamped in
mod: ksagolf
category: celestials
techniques: [custom-physics, closed-form-geometry, decorator-cache, headless-tests]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Celestial
  - KSA.Astronomical
  - KSA.CelestialSystem
  - KSA.Universe
  - KSA.Rendering.Water.Data.OceanReference
  - KSA.TerrainRadiusCache
  - KSA.PhysicsEnvironment
  - KSA.TerrainImpactFinder
  - KSA.TerrainPatch
tags: [terrain, height-field, ground, ocean, line-of-sight, slope, local-frame, ccf, cce]
---

KSA has no terrain raycast and no collider query a mod can use. What it has is one function,
`Celestial.GetTerrainHeightFromDirCcf(direction, accurate)`, returning the height above the mean
sphere along a direction from the body's centre; the `Cce` and `Cci` variants rotate the direction
into the body-fixed frame and call it.[^ksa-celestial] KSAGolf builds everything that needs the
ground on that one call: the ball's ground, the cursor's ground point, the course floor, a ring
draped on a slope, and a set of not-yet-wired helpers for later golf (a ground test for bodies in
flight, a cached variant for trajectory previews, a ridge mask, a slope reading and a local map
frame). The ridge mask's band solver is the one helper already live: the cursor's terrain ray uses it. The repo's `docs/KSA-TERRAIN.md` is an analysis of that function worth reading on its own.[^golf-terrain-doc]

## How it works

### Who asks, and how

| Caller | Query | Extra |
|---|---|---|
| `Golf.BodyGround` (the ball, the builder's floor) | `GetTerrainHeightFromDirCcf(dirCcf, accurate: true)` | Raised to the waterline, then to a launch site's top ([Launch-site surface raycast](launch-site-surface-raycast.md))[^golf-golf] |
| `Ksa/GroundTest.cs` (`IGroundTest`) | `GetTerrainHeightFromDirCce(unit(pos - centre), accurate: true)` | Picks the body by depth below its mean sphere, caches it, clamps to the waterline; answers a centre and a surface radius[^golf-groundtest] |
| `Ksa/TerrainHeights.cs` (`ITerrainHeights`) | `GetTerrainHeightFromDirCce(dir, accurate)`, `accurate` false unless asked | Settles one body and is sampled many times; the cursor ray asks with `accurate: true`[^golf-terrainheights] |
| `KsaWorld.TrySnapToGround` | `GetTerrainHeightFromDirCce(dir, accurate: true)` | Drapes gizmo rings onto slopes[^golf-ksaworld] |

`IGroundTest` deliberately answers **a centre and a radius**, not an altitude: a body samples the
ground once a frame and then knows its height at every sub-step for the cost of a subtraction,
treating the surface as a sphere of that radius for the frame.[^golf-igroundtest]
`GroundSurface.Height` takes the higher of terrain and sea level, because the height field answers
with the seabed under an ocean. The sea is detected by `GetOceanReference()` being non-null with
`Density > 0`, never by `IsValid()`.

### The Sim-side helpers (no KSA types)

| Type | What it does |
|---|---|
| `CoarseGroundTest` | Decorates an `IGroundTest`: reuses the last sample until the body is within 600 m of the cached surface or has moved 250 m since sampling, so a trajectory preview flown inside one frame pays for lookups only near the ground[^golf-coarse] |
| `ITerrainHeights` | `TryHeight(dir, out metres)`; false means *no claim*, never zero, because reading zero puts a whole horizon on the mean sphere[^golf-iterrainheights] |
| `LineOfSight` | Segment against a sphere, strictly between the endpoints (a craft on the surface does not block itself); `BlockedByTerrain` inflates the sphere by a margin; `HorizonRange` is the sum of both tangent lengths[^golf-lineofsight] |
| `TerrainMask` | Ridge occlusion against the real field (`Blocked`, no game caller yet). `TryBandBelow`, which the cursor's `TerrainRay.TryFirstHit` already uses, solves the quadratic for the part of the segment below `meanRadius + maxTerrain`; only that band is sampled, at a fixed caller-given count of interior points, with a clearance margin[^golf-terrainmask] |
| `GroundSlope` | The slope as the **gradient** from centred east/west/north/south differences at a chosen radius, plus the worst spoke of a ring as roughness; returns the aim loop gain `slope / tan(arrival)` and `1 / (1 - gain)`, infinite at gain 1 or more[^golf-groundslope] |
| `MapFrame` / `TerrainMap` | A local frame from centre, anchor and spin axis: east is `axis x up`, north is `up x east`, so north follows the body's own axis rather than the ecliptic pole (23 degrees apart on Earth); separate position and direction transforms; map spans, relief shading, heading[^golf-terrainmap] |

The ridge mask and the cursor ray need a ceiling that really bounds the terrain.
`KsaWorld.MaxTerrainHeightMetres` uses `Astronomical.MaxTerrainRadius - MeanRadius` (the height
texture's declared maximum) plus 8,000 m of headroom for the procedural modifiers, and avoids
`Celestial.MaxTerrainHeightApprox`, which is a 16,384-direction sample taken in the constructor and
underestimates the peaks.[^golf-ksaworld][^ksa-celestial][^ksa-astronomical]

### What `docs/KSA-TERRAIN.md` establishes, checked against build 2026.10.10.5554

- **`accurate` changes more than interpolation.** `true` uses `SampleHeightBicubic`, `false`
  `SampleHeightMapBilinear`; and the procedural modifier loop calls `modifierReference.Evaluate`
  only when `accurate` is true. On a body with biome materials `false` therefore drops erosion,
  detail textures, dunes and the launch-site levelling decals.[^ksa-celestial][^golf-terrain-doc]
- **Early returns.** No heightmap returns 0; no cubemap normal map, or no biome materials,
  returns the base interpolated height before the modifier loop.[^ksa-celestial]
- **Single-precision modifiers.** The direction is packed with `float3.Pack` before the modifier
  loop, which the doc measures as a staircase of about a third of a metre on Earth.[^golf-terrain-doc]
- **`accurate: true` is what vehicle physics stands on.** `PhysicsEnvironment.RecomputePositionalValues`
  sets `TerrainRadius` through `TerrainRadiusCache.ComputeRadiusCcf`, which calls
  `GetTerrainHeightFromDirCcf` with the default `accurate: true` and reuses a sample within 0.25 m of
  arc. The collision patch grid is 2 m (`TerrainPatch.BaseQuadWidth`). KSA's own
  `TerrainImpactFinder` samples `accurate: false`.[^ksa-physenv][^ksa-terrainradiuscache][^ksa-terrainpatch][^ksa-terrainimpactfinder]
- **`Cce` versus `Ccf`.** `GetTerrainHeightFromDirCce` applies `GetCcf2Cce().Inverse()` for you.
  A direction built from `position - centre` in `Ecl` belongs in the `Cce` entry; a body-fixed one
  (the ball's `PositionCcf`) in the `Ccf` entry. Passing either to the other rotates it by the
  planet's current phase.[^ksa-celestial]

## Lifecycle and timing

Every query is synchronous and runs on the main thread inside the mod's own hooks: the ball and the
builder's floor from the per-frame step (preferred at `[StarMapAfterGui]`, a postfix on
`Program.OnDrawUiViewports`), the cursor's ground point from the craft mover's update in the same
GUI hook. Nothing here is a hook of its own.

The query reads template data (height cubemaps, modifiers, biome maps) that nothing mutates during
flight, plus the body's cached `Ccf`-to-`Cce` rotation for the `Cce` entry point. That rotation is
refreshed once per frame by `Celestial.UpdatePerFrameData`, which `Universe.ApplyVehicleSolvers`
calls in `PrepareFrame` before any mod hook, so every caller in one frame agrees on the planet's
phase.[^ksa-universe][^ksa-celestial] `GroundTest` notes the consequence: its answer is at the
frame's end in both of the body's motions, so a mover sub-stepping part-way through a frame must
walk its query point forward by the ground's velocity itself; the interface has no time argument by
design.[^golf-groundtest]

`GroundTest`, `CoarseGroundTest`, `TerrainMask.Blocked`, `GroundSlope`, `MapFrame`/`TerrainMap` and
`KsaWorld.IsOccluded` (the `LineOfSight` caller) have no callers in the shipping mod at this commit
(`TerrainMask.TryBandBelow` does, through the cursor ray; see
[Cursor picking and the terrain ray](cursor-picking-and-terrain-ray.md));
they are covered by the headless tests and documented as the ground contacts and site judgement
for longer shots and carts.

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Ksa/GroundTest.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/GroundTest.cs#L38-L144) | Nearest body by depth with a cached shortcut, height query, sea clamp |
| [src/KSAGolf/Ksa/TerrainHeights.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/TerrainHeights.cs) | One body's height field behind `ITerrainHeights` |
| [src/KSAGolf/Sim/IGroundTest.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/IGroundTest.cs) | Centre-and-radius ground seam and `GroundSurface.Height` |
| [src/KSAGolf/Sim/CoarseGroundTest.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/CoarseGroundTest.cs) | Resample-near-ground cache for previews |
| [src/KSAGolf/Sim/ITerrainHeights.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/ITerrainHeights.cs) | Height-field seam with "no claim" semantics |
| [src/KSAGolf/Sim/TerrainMask.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/TerrainMask.cs) | Closed-form band plus fixed-budget ridge sampling |
| [src/KSAGolf/Sim/LineOfSight.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/LineOfSight.cs) | Sphere occlusion and horizon range |
| [src/KSAGolf/Sim/GroundSlope.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/GroundSlope.cs) | Gradient slope, roughness, aim loop gain |
| [src/KSAGolf/Sim/TerrainMap.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/TerrainMap.cs) | `MapFrame` local east/north frame and `TerrainMap` helpers |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L339-L417) | `IsOccluded` and `MaxTerrainHeightMetres` (a real bound on the terrain) |
| [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L743-L766) | `BodyGround`, the `Ccf` caller the ball uses |
| [docs/KSA-TERRAIN.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/docs/KSA-TERRAIN.md) | Measurements and analysis of the height field |

## Snippets

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

```csharp
// src/KSAGolf/Sim/TerrainMask.cs L49-L68
        if (!TryBandBelow(eye, target, centre, ceiling, out double from, out double to)) return false;

        double3 along = target - eye;

        // Interior points of the band, evenly spaced and both ends left out.
        for (int i = 1; i <= samples; i++)
        {
            double t = from + (to - from) * i / (samples + 1);

            double3 radial = eye + along * t - centre;
            double radius = Vec.Len(radial);
            if (!(radius > 0.0)) continue;

            if (!heights.TryHeight(radial / radius, out double height)) continue;
            if (!double.IsFinite(height)) continue;

            if (radius + clearance <= meanRadius + height) return true;
        }

        return false;
```

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

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `Celestial.GetTerrainHeightFromDirCcf(dir, accurate = true)` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L789-L882) | The only height query: cubemap (bicubic or bilinear) plus procedural modifiers evaluated only when `accurate` |
| `Celestial.GetTerrainHeightFromDirCce` / `GetTerrainHeightFromDirCci` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L777-L787) | Rotate the direction into `Ccf` with the cached per-frame rotation and delegate |
| `Celestial.MaxTerrainHeightApprox` / `UpdateApproxTerrainAltitudes` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L929-L982) | A 16,384-direction sample, not a bound; avoided as a ceiling |
| `Astronomical.MaxTerrainRadius` / `HasTerrainHeightmap` | [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L114-L132) | `MeanRadius + HeightReference.Maximum` as the base-field bound; whether a body has terrain at all |
| `Universe.CurrentSystem`, `CelestialSystem.Count` / `GetIndex` | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L68-L74) | Walking the bodies to find the one underfoot |
| `PhysicsEnvironment.RecomputePositionalValues` | [current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L83-L117) | The engine's vehicle terrain radius, for comparison |
| `TerrainRadiusCache.ComputeRadiusCcf` | [current/decomp/KSA/TerrainRadiusCache.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TerrainRadiusCache.cs) | The engine's own 0.25 m reuse around the same query |
| `TerrainImpactFinder` | [current/decomp/KSA/TerrainImpactFinder.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TerrainImpactFinder.cs#L64) | Engine impact prediction, sampling `accurate: false` |
| `TerrainPatch.BaseQuadWidth` | [current/decomp/KSA/TerrainPatch.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TerrainPatch.cs#L9) | 2 m collision patch grid over the same field |

## Pitfalls and version notes

- **`accurate: false` is not just cheaper.** It skips every modifier, including the launch-site
  decals, so a coarse sample at a pad answers with unlevelled terrain. Use it only where coarseness
  is acceptable (a ridge mask sampled many times).[^ksa-celestial][^golf-terrainheights]
- **The seabed is not the surface.** Clamp to `OceanReference.Level` where `Density > 0`;
  `OceanReference.IsValid()` is false on Earth because `DistanceReference.IsValid()` needs 100 km.
- **`MaxTerrainHeightApprox` is not a bound.** A cheap reject in front of an exact test needs a
  ceiling that really contains the terrain, or it produces false negatives.[^golf-ksaworld]
- **An unreadable field is not flat ground.** `ITerrainHeights.TryHeight` false means skip the
  sample, never use zero.[^golf-iterrainheights]
- **A ring median halves a planar slope.** A spoke along the contour of a tilted plane reads zero,
  so `GroundSlope` uses the gradient and keeps the ring only for roughness.[^golf-groundslope]
- **Stale line numbers in the repo doc.** `docs/KSA-TERRAIN.md` was written against 2026.8.22.5348
  and cites `Celestial.cs:792` and `PhysicsEnvironment.cs:112`; in 2026.10.10.5554 the query is at
  `Celestial.cs` L789 and vehicle physics goes through `TerrainRadiusCache` (already true in the
  mod's 2026.9.22.5482 build). The behaviour it describes still holds.
- **Version.** The `Celestial` diff between 2026.9.22.5482 and 2026.10.10.5554 touches orbit
  creation and `UpdateTask` only; the height query and its variants are unchanged.

## See also

- [Golf ball physics in the body-fixed frame](golf-ball-physics.md)
- [Launch-site surface by raycast into a copied collider](launch-site-surface-raycast.md)
- [Cursor picking and the terrain ray](cursor-picking-and-terrain-ray.md)
- [Swept contact against a craft's real hull](swept-hull-contact.md)
- [Craft mover](craft-mover.md)
- [Headless simulation tests behind a Sim/Ksa boundary](headless-sim-tests.md)
- [Gradient from centred east, west, north and south differences](../../../howto/celestials/terrain/measure-slope-and-detect-ground-contact-on-terrain/measure-slope-and-detect-ground-contact-on-terrain-1.md) - how-to article demonstrating this feature
- [Ccf versus Cce entry points, sea clamp, no-claim results and a fixed-budget line-of-sight test](../../../howto/celestials/terrain/query-terrain-height-and-line-of-sight/query-terrain-height-and-line-of-sight-1.md) - how-to article demonstrating this feature
- [Cache decorator that reuses a height sample until near the ground or moved](../../../howto/celestials/terrain/query-terrain-height-and-line-of-sight/query-terrain-height-and-line-of-sight-3.md) - how-to article demonstrating this feature
- [Take north from the body's spin axis and query terrain height in the same frame](../../../howto/flight-and-orbits/transfers/build-a-site-frame-and-compute-a-launch-window/build-a-site-frame-and-compute-a-launch-window-3.md) - how-to article demonstrating this feature

[^golf-groundtest]: src/KSAGolf/Ksa/GroundTest.cs
[^golf-terrainheights]: src/KSAGolf/Ksa/TerrainHeights.cs
[^golf-igroundtest]: src/KSAGolf/Sim/IGroundTest.cs
[^golf-coarse]: src/KSAGolf/Sim/CoarseGroundTest.cs
[^golf-iterrainheights]: src/KSAGolf/Sim/ITerrainHeights.cs
[^golf-terrainmask]: src/KSAGolf/Sim/TerrainMask.cs
[^golf-lineofsight]: src/KSAGolf/Sim/LineOfSight.cs
[^golf-groundslope]: src/KSAGolf/Sim/GroundSlope.cs
[^golf-terrainmap]: src/KSAGolf/Sim/TerrainMap.cs
[^golf-ksaworld]: src/KSAGolf/Ksa/KsaWorld.cs
[^golf-golf]: src/KSAGolf/Ksa/Golf.cs
[^golf-terrain-doc]: docs/KSA-TERRAIN.md (where KSA thinks the ground is)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-astronomical]: KSA.Astronomical (decompiled, build 2026.10.10.5554)
[^ksa-physenv]: KSA.PhysicsEnvironment (decompiled, build 2026.10.10.5554)
[^ksa-terrainradiuscache]: KSA.TerrainRadiusCache (decompiled, build 2026.10.10.5554)
[^ksa-terrainimpactfinder]: KSA.TerrainImpactFinder (decompiled, build 2026.10.10.5554)
[^ksa-terrainpatch]: KSA.TerrainPatch (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
