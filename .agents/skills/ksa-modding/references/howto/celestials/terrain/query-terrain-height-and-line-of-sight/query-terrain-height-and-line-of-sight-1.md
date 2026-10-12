---
type: How-To
title: Ccf versus Cce entry points, sea clamp, no-claim results and a fixed-budget line-of-sight test
description: Read the engine height field with the right entry point, clamp to the sea, treat unreadable samples as no claim, decide when accurate sampling is worth it and build a sphere-then-band line-of-sight test on top
category: celestials
group: terrain
topic: query-terrain-height-and-line-of-sight
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/terrain-height-queries]
techniques: [custom-physics, closed-form-geometry]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types:
  - KSA.Celestial
  - KSA.Astronomical
  - KSA.CelestialSystem
  - KSA.Universe
  - KSA.Rendering.Water.Data.OceanReference
  - KSA.TerrainRadiusCache
  - KSA.PhysicsEnvironment
  - KSA.TerrainPatch
  - KSA.TerrainImpactFinder
tags: [terrain, height-field, ccf, cce, ocean, line-of-sight, accurate]
---
Outcome: a small set of helpers that answer "how high is the ground here", "where does a body in flight meet it" and "does a ridge hide A from B", using only `Celestial` queries. The code
is KSAGolf's ([Terrain height queries](../../../../mods/ksagolf/features/terrain-height-queries.md)); prerequisite is a StarMap mod whose per-frame code runs from `[StarMapAfterGui]`. Read the [overview](overview.md) first for the engine functions.

## Background: how the game does it

The ground is a cubemap height texture (min and max in `HeightReference`) plus a list of procedural modifiers. `GetTerrainHeightFromDirCcf` takes a unit direction in the body-fixed frame (`Ccf`) and returns metres above `MeanRadius` ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L789-L881)). Three facts shape every caller:

- **Frame.** `GetTerrainHeightFromDirCce(dir)` is `GetTerrainHeightFromDirCcf(dir.Transform(GetCcf2Cce().Inverse()))` ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L777-L781)). A direction built as `position - centre` in `Ecl` is already a `Cce` direction. A body-fixed one belongs in the `Ccf` entry. Passing either to the wrong entry rotates it by the planet's current phase and reads the ground at the wrong longitude.
- **Sea.** The height field answers with the seabed under an ocean, so a caller that wants the surface a ball or round meets has to compare with the sea level itself.
- **Cost.** Each call samples a cubemap and, when `accurate` is true, evaluates every modifier. The collision patch grid is 2 m ([current/decomp/KSA/TerrainPatch.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TerrainPatch.cs#L9)); the engine reuses a vehicle sample within 0.25 m of arc ([current/decomp/KSA/TerrainRadiusCache.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TerrainRadiusCache.cs#L22-L38)).

## Lifecycle and timing

Queries are synchronous and run on the main thread from the mod's own frame step (`frame:draw-ui`, `[StarMapAfterGui]`). With the HUD hidden that hook does not fire, so KSAGolf latches the same step into a Harmony prefix on `Program.OnFrameCelestials` (`frame:celestials`, still before the render) and, if that patch cannot be applied, into `[StarMapAfterOnFrame]` ([src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L113-L134), [src/KSAGolf/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/PreRenderHook.cs#L55-L80)); the queries are the same from any of the three. The height data is template data nothing mutates during flight. The only per-frame input is the cached `Ccf`-to-`Cce` rotation, refreshed by `Celestial.UpdatePerFrameData` when `Universe.ApplyVehicleSolvers` runs inside `PrepareFrame` (`frame:apply-solvers`), so all callers in a frame agree on the planet's phase. The rotation is the one at the end of the applied physics step: a mover sub-stepping part-way through a frame must walk its query point forward by the ground's velocity itself, which is why the interface below has no time argument. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Pick the entry point.** Ecliptic position and body centre in hand: `dirCce = unit(positionEcl - centreEcl)` and call the `Cce` overload. Body-fixed position: call the `Ccf` overload. Never mix them.

2. **Resolve the body underfoot** by depth below the mean sphere, not by distance to the centre (a point low over a moon is closer to the moon's ground than to the planet it orbits). Then ask, clamp to the sea and answer a centre and a surface radius so a body in flight pays one query per frame:

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

   Detect the sea with `GetOceanReference()` non-null and `Density > 0`, never with `IsValid()`. The mod's `GroundSurface.Height` returns `max(terrain, sea level)` when a sea exists ([src/KSAGolf/Sim/IGroundTest.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/IGroundTest.cs#L36-L56)).

3. **Wrap the field for reuse.** When one body is sampled many times, settle the body once and expose only the direction. A `false` answer means *no claim*, never zero: reading zero from an unreadable field puts a whole horizon on the mean sphere.

```csharp
// src/KSAGolf/Sim/ITerrainHeights.cs L13-L23
public interface ITerrainHeights
{
    /// <summary>
    /// Terrain height above the mean sphere along a direction from the body's centre (m).
    ///
    /// <para>False when the field cannot be read. A caller must treat that as <em>no claim</em>
    /// rather than as flat ground: reading zero from an unreadable field puts a sensor's whole
    /// horizon at the mean sphere, which is a planet-sized change nothing announces.</para>
    /// </summary>
    bool TryHeight(double3 dirFromCentre, out double metres);
}
```

4. **Use a real ceiling.** `Celestial.MaxTerrainHeightApprox` is not a bound (a 16,384-direction sample taken before the modifiers exist). Use `Astronomical.MaxTerrainRadius - MeanRadius` plus headroom for the modifiers:

```csharp
// src/KSAGolf/Ksa/KsaWorld.cs L403-L417
private static double MaxTerrainHeightMetres(Celestial body)
{
    try
    {
        double exact = body.MaxTerrainRadius - body.MeanRadius;

        return double.IsFinite(exact) && exact > 0.0
                   ? exact + TerrainModifierHeadroomMetres
                   : body.MaxTerrainHeightApprox;
    }
    catch
    {
        return body.MaxTerrainHeightApprox;
    }
}
```

   `TerrainModifierHeadroomMetres` is 8,000 m in the mod ([src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L388)), the sum of declared modifier amplitudes on Earth plus margin.

5. **Test the sphere first.** A segment that does not pass through the body's mean sphere (strictly between its endpoints, so a craft standing on the surface does not block itself) cannot be hidden by terrain, because a sphere containing the terrain cannot produce a false negative. The horizon range is the sum of both tangent lengths:

```csharp
// src/KSAGolf/Sim/LineOfSight.cs L27-L42
public static bool Blocked(double3 eye, double3 target, double3 centre, double radius)
{
    if (!double.IsFinite(radius) || radius <= 0.0) return false;
    if (!Vec.IsFinite(eye) || !Vec.IsFinite(target) || !Vec.IsFinite(centre)) return false;

    double3 along = target - eye;
    double length2 = Vec.Len2(along);
    if (!double.IsFinite(length2) || length2 < 1e-12) return false;

    // Where along the segment the body's centre is nearest. Outside (0, 1) the sphere is
    // behind the eye or beyond the target, and neither is in the way.
    double t = Vec.Dot(centre - eye, along) / length2;
    if (!double.IsFinite(t) || t <= 0.0 || t >= 1.0) return false;

    return Vec.Len2(eye + along * t - centre) < radius * radius;
}
```

```csharp
// src/KSAGolf/Sim/LineOfSight.cs L55-L60
public static double HorizonRange(double radius, double eyeAltitude, double targetAltitude)
{
    if (!double.IsFinite(radius) || radius <= 0.0) return double.PositiveInfinity;

    return ToHorizon(radius, eyeAltitude) + ToHorizon(radius, targetAltitude);
}
```

6. **Sample only the band below the ceiling.** The distance from the centre to a point on a segment is a quadratic in the parameter, so the part of the segment that dips under `meanRadius + ceiling` is one root pair:

```csharp
// src/KSAGolf/Sim/TerrainMask.cs L81-L108
public static bool TryBandBelow(double3 eye, double3 target, double3 centre, double ceiling,
                                out double from, out double to)
{
    from = to = 0.0;

    if (!Vec.IsFinite(eye) || !Vec.IsFinite(target) || !Vec.IsFinite(centre)) return false;
    if (!double.IsFinite(ceiling) || ceiling <= 0.0) return false;

    double3 along = target - eye;
    double a = Vec.Len2(along);
    if (!double.IsFinite(a) || a < 1e-12) return false;

    double3 offset = eye - centre;
    double b = 2.0 * Vec.Dot(offset, along);
    double c = Vec.Len2(offset) - ceiling * ceiling;

    double discriminant = b * b - 4.0 * a * c;
    if (!double.IsFinite(discriminant) || discriminant <= 0.0) return false;

    double root = Math.Sqrt(discriminant);
    double lo = (-b - root) / (2.0 * a);
    double hi = (-b + root) / (2.0 * a);

    from = Math.Max(0.0, Math.Min(lo, hi));
    to = Math.Min(1.0, Math.Max(lo, hi));

    return to > from;
}
```

   Then take a caller-given, fixed number of interior samples (never the endpoints, which routinely sit on the ground), skipping any sample the field cannot answer:

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

## What `accurate` changes

`accurate: true` is not just finer interpolation:

| | `accurate: true` | `accurate: false` |
|---|---|---|
| Base height | `SampleHeightBicubic` | `SampleHeightMapBilinear` |
| Procedural modifiers | `modifierReference.Evaluate` called | loop runs but `Evaluate` skipped |
| On a body with biome materials | erosion, detail, dunes and levelling decals included | all of those dropped, including launch-site levelling decals |
| Early returns (either mode) | no heightmap returns 0; no normal cubemap or no biome materials returns the base interpolated height | same |
| Modifier render data | the loop runs to `TerrainModifiersRenderData?.NumModifiers`, so while that is null no modifier is applied even with `accurate: true` | same |

`TerrainModifiersRenderData` is built once at load, not per frame: `Universe.SetupRenderData` calls `Astronomical.SetupModifierRenderData` for every body with a mesh collection ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2404-L2427), [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L446-L460)), from `PlanetMeshLibrary.Create` ([current/decomp/KSA/PlanetMeshLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetMeshLibrary.cs#L121-L129)), which the `Program` constructor runs *after* `Universe.LoadDefaultSystem` and its `Universe.OnLoaded` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1066-L1070), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1145-L1149)). A query made from `startup:game-loaded` (a mod `OnLoad` console command) therefore returns heights without modifiers; from the first frame on the data exists and never changes, so editing a body's `TerrainModifiers` list later is not seen by this query.

The engine's vehicle physics stands on `accurate: true`; its impact predictor samples `false`. The mod calls `true` for anything that must agree with where a craft or ball rests, and `false` only where a missed decal or a few metres of detail is harmless (a ridge mask sampled tens of times). The direction is packed to single precision (`float3.Pack`) before the modifier loop, which the mod's analysis measured as a staircase of roughly a third of a metre on Earth.

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/GroundTest.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/GroundTest.cs#L38-L144) | Nearest body by depth with cached shortcut, height query, sea clamp |
| [src/KSAGolf/Ksa/TerrainHeights.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/TerrainHeights.cs) | One body's height field behind `ITerrainHeights` |
| [src/KSAGolf/Sim/LineOfSight.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/LineOfSight.cs) | Sphere occlusion and horizon range |
| [src/KSAGolf/Sim/TerrainMask.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/TerrainMask.cs) | Closed-form band plus fixed-budget ridge sampling |
| [src/KSAGolf/Ksa/Golf.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Golf.cs#L743-L766) | `BodyGround`, the `Ccf` caller the ball uses |
| [docs/KSA-TERRAIN.md](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/docs/KSA-TERRAIN.md) | Measurements and analysis of the height field |

## Variations and gotchas

- **Stale line numbers in the repo doc.** `docs/KSA-TERRAIN.md` was written against an older build; in build 2026.10.10.5554 the query is at `Celestial.cs` L789 and vehicle physics goes through `TerrainRadiusCache`.
- **Under an ocean the field is the seabed.** Clamp, or a ball falls through the waterline.
- **The mod's sea is flat.** The engine uses `OceanRenderer` waves near the surface for vehicles, so the two differ by wave height.
- **Wire the helpers before relying on them.** At this commit several helpers (`TerrainMask.Blocked`, `GroundSlope`, `MapFrame`) have no live callers in KSAGolf; only the band solver is live, through the cursor's terrain ray. They are covered by the mod's headless tests.
- **No claim is not zero.** Skip samples the field cannot answer; never substitute 0.
- **Do not use `MaxTerrainHeightApprox` as a bound.** A cheap reject in front of an exact test needs a ceiling that really contains the terrain.

## See also

- [Overview](overview.md); [approach 2: centre and surface radius](query-terrain-height-and-line-of-sight-2.md); [approach 3: cache decorator](query-terrain-height-and-line-of-sight-3.md); [approach 4: height under the craft](query-terrain-height-and-line-of-sight-4.md)
- [Terrain height queries](../../../../mods/ksagolf/features/terrain-height-queries.md), [Cursor picking and the terrain ray](../../../../mods/ksagolf/features/cursor-picking-and-terrain-ray.md)
- [Measure ground slope and detect ground contact](../../../celestials/terrain/measure-slope-and-detect-ground-contact-on-terrain/overview.md)
- [Understand KSA coordinate frames and epochs](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)
