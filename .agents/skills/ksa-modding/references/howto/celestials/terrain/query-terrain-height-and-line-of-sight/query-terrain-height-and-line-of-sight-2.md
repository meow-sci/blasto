---
type: How-To
title: Answer as a body centre and surface radius, pick the body by depth, mask radar by horizon
description: Wrap the height query so a mod-simulated round tests its height every sub-step by subtraction, back-date the body's motion, and mask line of sight with non-accurate samples of the real horizon
category: celestials
group: terrain
topic: query-terrain-height-and-line-of-sight
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/terrain-ground-impact-test]
techniques: [terrain-query, custom-physics]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types:
  - KSA.Celestial
  - KSA.Astronomical
  - KSA.CelestialSystem
  - KSA.Universe
  - KSA.Rendering.Water.Data.OceanReference
tags: [terrain, ground-test, ocean, radar, line-of-sight, sub-step, epoch]
---
Outcome: rounds that the engine does not know about (bombs, shells, missiles) stop on the same surface a landed craft stands on, and a radar scan can ask "is a ridge in the way" for tens of contacts per frame. Demonstrated by KSArmory ([Terrain ground impact test](../../../../mods/ksarmory/features/terrain-ground-impact-test.md)). Read [approach 1](query-terrain-height-and-line-of-sight-1.md) for the entry points and sea clamp; this article adds the *shape* of the answer and the epoch corrections.

## Background: how the game does it

Mod-simulated bodies live outside `Vehicle` physics, so nothing stops them at the ground. The engine query is expensive relative to an integration sub-step (a round may take dozens per frame), and it answers *an altitude along a direction*, which is stale as soon as the round moves. KSArmory therefore asks for **a body centre and a surface radius**: sample once per frame, treat the surface as a sphere of that radius for the frame, and know the height at every sub-step by subtraction (`Len(position - centre) - radius`). The query itself is `Celestial.GetTerrainHeightFromDirCce` ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L777-L781)).

## Lifecycle and timing

The mod steps its simulation once per frame from `[StarMapAfterGui]` (`frame:draw-ui`). When the HUD is hidden that hook does not fire, so a frame latch hands the step to a Harmony prefix on `Program.OnFrameCelestials` (`frame:celestials`, the last phase before `OnPreRender` outside the `DrawUI` guard), and only if that patch failed to apply to `[StarMapAfterOnFrame]` (`frame:after-on-frame`, after the render, one frame late) ([src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L180), [src/KSArmory/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/PreRenderHook.cs#L56-L80)), so the round, the ground sample and the anchor it is drawn against share an epoch. The celestial state is the end of the applied physics step (`Universe.ApplyVehicleSolvers`, then `UpdatePerFrameData`), while the round is part-way through its frame, hence the two back-dating callbacks in step 4. The engine's rotation for the `Cce` entry is read at the end of the frame, so a spinning body moves the ground by hundreds of metres per second under the query. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Define the seam** (no game types in the simulation code, so it unit-tests offline). `TryGround` returns false for any failure; a radius of zero would mean ground at the origin:

```csharp
// src/KSArmory/Sim/IGroundTest.cs L35-L41
internal interface IGroundTest
{
    /// <param name="positionEcl">Where the round is now.</param>
    /// <param name="centreEcl">Centre of the body beneath it.</param>
    /// <param name="surfaceRadius">Distance from that centre to the ground under the round.</param>
    bool TryGround(double3 positionEcl, out double3 centreEcl, out double surfaceRadius);
}
```

2. **Implement it once against the engine.** `GroundTest.Shared` picks the body by depth below its mean sphere over `Universe.CurrentSystem`, caches it while the point is both ahead of the runner-up and within one `MeanRadius` of that body, computes `dirCce = unit(positionEcl - body.GetPositionEcl())` (valid because `Cce` shares the ecliptic axes), calls `GetTerrainHeightFromDirCce(dirCce, accurate: true)`, clamps to the sea and returns `centreEcl` and `MeanRadius + height`. Any exception or non-finite value returns false. The body is the same listing as [approach 1](query-terrain-height-and-line-of-sight-1.md) step 2:

```csharp
// src/KSArmory/Ksa/GroundTest.cs L118-L140
centreEcl = nearest.GetPositionEcl();

double3 dirCce = Vec.Unit(positionEcl - centreEcl);
if (!Vec.IsFinite(dirCce) || Vec.Len(dirCce) < 0.5) return false;

double height = nearest.GetTerrainHeightFromDirCce(dirCce, accurate: true);
if (!double.IsFinite(height)) return false;

// The height field answers with terrain, so under an ocean it reports the seabed. A
// round would fall through the waterline and burst on the bottom, unseen. Same query
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

3. **Ask once per frame, test per sub-step.** The round asks for the ground under its pre-step position and holds `_groundCentre` and `_groundRadius`:

```csharp
// src/KSArmory/Sim/Slug.cs L486-L497
double3 atOwnEpoch = GroundQueryAt(PositionEcl, -dt);

_groundSampledAtEcl = atOwnEpoch;
_groundSampledOverSeconds = dt;

_haveGround = munition.HitsTerrain && Ground is not null
              && Ground.TryGround(atOwnEpoch, out _groundCentre, out _groundRadius)
              && double.IsFinite(_groundRadius) && _groundRadius > 0.0;

// The frame's own sample is the ground under where its first sub-step begins.
_radiusBefore = _groundRadius;
_radiusBeforeFresh = _haveGround;
```

   Each sub-step then computes `was` and `now`, fires a height fuse on the downward crossing, and bursts on contact with the point interpolated linearly along the sub-step (`f = was / (was - now)`):

```csharp
// src/KSArmory/Sim/Slug.cs L760-L785
double was = Vec.Len(before - centreWas) - radiusWas;
double now = Vec.Len(PositionEcl - centreNow) - radiusNow;

// The height fuse, crossed on the way down. Linear across the step like the ground
// crossing below; a store already under the height when it arms bursts on the ground.
double fuseAt = munition.BurstHeightMetres;
if (fuseAt > 0.0 && Age >= munition.FuseArmSeconds && was > fuseAt && now <= fuseAt)
{
    double g = Math.Clamp((was - fuseAt) / (was - now), 0.0, 1.0);

    PositionEcl = before + stepEcl * g;
    MissDistance = double.PositiveInfinity;
    BurstAtHeight = true;
    DetonationElapsedInFrame = elapsedInFrame + h * g - frameSeconds;
    State = RoundState.Detonated;
    return;
}

if (now <= 0.0)
{
    // Back to where it crossed, so the burst is on the surface rather than up to a
    // sub-step underneath it. Linear across the step: the round's own drop over 1.5 m
    // is not where the curvature lives.
    double f = Math.Clamp(was > 0.0 ? was / (was - now) : 0.0, 0.0, 1.0);

    // That solves against the CHORD joining two height samples a whole sub-step apart --
    // ...
```

4. **Back-date the body.** The centre was sampled at the frame's end but the round is mid-frame: walk the held centre back by the body's velocity, and walk the *query point* forward by the ground's full velocity (orbit plus `omega x r` spin) when you want the end-of-step rotation to read the ground the round is really over:

```csharp
// src/KSArmory/Ksa/WeaponSystem.cs L3323-L3343
// Where the body was, relative to the sample the ground was read against. Same back-dating as
// the gravity and density callbacks and for the same reason: the celestial sample is at the
// frame's end and the round crosses the frame, so secondsIntoFrame arrives negative and this
// walks the centre BACK to the sub-step's own instant.
private double3 GroundCentreDriftIntoFrame(double secondsIntoFrame)
    => _bodyVelocityEcl * secondsIntoFrame;

private Func<double, double3>? _groundDriftAt;

// The same back-dating for a terrain QUERY, which needs more than the centre does. GroundTest
// resolves a direction and the engine answers it through the frame's END rotation, so the
// body's spin between the sub-step and that instant is part of what has to come off -- ~400 m/s
// at mid latitudes, which is metres of ground within one frame. The centre is exempt because it
// sits on the spin axis, where rotation moves nothing.
//
// Read at the point being asked about rather than once per frame: the spin term is a cross
// product with the radius, so it belongs to the place, not to the body.
private double3 GroundQueryDriftIntoFrame(double3 positionEcl, double secondsIntoFrame)
    => GroundVelocityIntoFrame(positionEcl, secondsIntoFrame) * secondsIntoFrame;

// The air's own motion where the round is, rather than where it was when the frame began. The
```

5. **Re-read near the surface.** For a fast arrival, a sphere of the frame's radius is the height of ground the round has already left. Inside a 200 m band (`Slug.GroundResampleBandMetres`) the round re-reads the ground under both ends of every sub-step, and with `StopOnTheTerrain` re-queries at the interpolated crossing and takes one false-position step. Fuse only on a crossing from above (`was > 0`) for interceptors, so a tube that sits a hair under the field does not burst on its own rail.

## Line of sight with non-accurate samples

A radar asks tens of times per contact per scan, so the horizon mask wraps the field with `accurate: false` (bilinear, no modifiers, see [approach 1](query-terrain-height-and-line-of-sight-1.md)) and hands it to the same sphere-then-band mask:

```csharp
// src/KSArmory/Ksa/TerrainHeights.cs L14-L34
internal sealed class TerrainHeights(Celestial body, bool accurate = false) : ITerrainHeights
{
    private readonly Celestial _body = body;
    private readonly bool _accurate = accurate;

    public bool TryHeight(double3 dirFromCentre, out double metres)
    {
        metres = 0.0;

        try
        {
            metres = _body.GetTerrainHeightFromDirCce(dirFromCentre, accurate: _accurate);

            return double.IsFinite(metres);
        }
        catch
        {
            return false;
        }
    }
}
```

```csharp
// src/KSArmory/Ksa/KsaWorld.cs L849-L881
public static bool IsHiddenByTerrain(double3 eyeEcl, double3 targetEcl, int samples,
                                     double clearance, out string blockedBy)
{
    blockedBy = string.Empty;
    if (samples <= 0) return false;

    try
    {
        if (NearestBody(eyeEcl) is not { } body) return false;

        double3 centre = body.GetPositionEcl();

        if (!TerrainMask.Blocked(eyeEcl, targetEcl, centre, body.MeanRadius,
                                 MaxTerrainHeightMetres(body), samples, clearance,
                                 new TerrainHeights(body)))
        {
            return false;
        }

        blockedBy = body.Id ?? string.Empty;
        return true;
    }
    catch
    {
        return false;
    }
}

/// <summary>
/// The mean sphere of the body under a point: its centre and its radius.
///
/// <para>A radius of zero means no body could be resolved, which every caller has to read as
/// "no ground here" rather than as ground at the origin.</para>
```

The cursor, laser spot and designator use the same field with `accurate: true`, ray-marched to the first above-to-below crossing.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Sim/IGroundTest.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/IGroundTest.cs) | Centre-and-radius seam, `GroundSurface.Height` |
| [src/KSArmory/Ksa/GroundTest.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/GroundTest.cs) | The engine-backed implementation, `GroundTest.Shared` |
| [src/KSArmory/Sim/Slug.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Slug.cs#L486-L497) | Per-frame sample, per-sub-step crossing, interpolation |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L3323-L3343) | Centre and query drift callbacks |
| [src/KSArmory/Ksa/TerrainHeights.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/TerrainHeights.cs) | One body's field behind `ITerrainHeights`, `accurate` selectable |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L849-L881) | `IsHiddenByTerrain` radar mask |

## Variations and gotchas

- **Cce, not Ccf, for an ecliptic direction.** Feeding it to the `Ccf` entry reads the wrong longitude by the body's rotation phase. A caller that already holds body-fixed directions (the ICBM computer) correctly uses `Ccf`.
- **Time argument.** The interface has no time argument by design; the asking side back-dates.
- **Sea.** The mod clamps to a flat `OceanReference.Level`; the engine uses waves near the surface for vehicles, so the two differ by wave height.
- **Do not use `MaxTerrainHeightApprox` as a ceiling** (see approach 1).
- **No claim is not zero.** Any exception or non-finite answer returns false and the caller treats it as "no ground here".

## See also

- [Overview](overview.md); [approach 1](query-terrain-height-and-line-of-sight-1.md); [approach 3](query-terrain-height-and-line-of-sight-3.md)
- [Terrain ground impact test](../../../../mods/ksarmory/features/terrain-ground-impact-test.md)
- [Measure ground slope and detect ground contact](../../../celestials/terrain/measure-slope-and-detect-ground-contact-on-terrain/overview.md), [Simulate bodies outside the vehicle system](../../../physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/overview.md), [Prevent tunnelling of fast bodies](../../../physics/mod-simulation/prevent-tunnelling-of-fast-bodies/overview.md)
