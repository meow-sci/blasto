---
type: Mod Feature
title: Terrain ground-impact test against the engine's height field
description: Rounds, sights and cursor rays find the ground through Celestial.GetTerrainHeightFromDirCce(accurate true) clamped to the sea, answered as a centre and a surface radius so a round can test its height at every sub-step for the cost of a subtraction
mod: ksarmory
category: celestials
techniques: [custom-physics, terrain-query, ray-march, sim-boundary, epoch-back-dating]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types:
  - KSA.Celestial
  - KSA.Astronomical
  - KSA.CelestialSystem
  - KSA.Universe
  - KSA.IParentBody
  - KSA.Rendering.Water.Data.OceanReference
  - KSA.PhysicsEnvironment
  - KSA.TerrainRadiusCache
  - KSA.TerrainPatch
  - KSA.TerrainImpactFinder
tags: [terrain, height-field, ground-impact, sea-level, cce, ccf, sub-step, ray-march, epochs]
---

KSArmory's rounds are not KSA vehicles, so nothing in the engine stops them at the ground. The mod
asks the engine's own height field instead: a bomb, a reentry vehicle or a missile flown into a
hill stops on the same surface a landed craft stands on, over the sea it stops on the waterline
rather than the seabed, and the sight, the chase camera and the cursor tools read the same answer.

The mechanism is one call, `Celestial.GetTerrainHeightFromDirCce(dir, accurate: true)`, wrapped so
that it returns **a body centre and a surface radius** rather than an altitude. A round samples it
once per frame and then knows its height at every integration sub-step by subtraction.[^ksarmory-igroundtest]

## How it works

**The seam.** `IGroundTest.TryGround(positionEcl, out centreEcl, out surfaceRadius)` lives in `Sim/`
(no KSA types); `Ksa/GroundTest.cs` is the only engine-backed implementation, shared by every round
in the world as `GroundTest.Shared`.[^ksarmory-groundtest]

| Step in `GroundTest.TryGround` | What it does |
|---|---|
| Pick the body | Nearest by *depth below the mean sphere* (`Len(pos - body) - MeanRadius`) over `Universe.CurrentSystem`, not by distance; the last body is cached and reused only while the point is both ahead of the runner-up and within one `MeanRadius` of that body's surface |
| Direction | `dirCce = unit(positionEcl - body.GetPositionEcl())`; an ecliptic direction is a valid Cce direction because Cce is the ecliptic translated to the body centre |
| Height | `body.GetTerrainHeightFromDirCce(dirCce, accurate: true)` (metres above `MeanRadius`) |
| Sea clamp | If `GetOceanReference()` exists with `Density > 0`, `GroundSurface.Height` returns `max(terrain, sea.Level)` |
| Answer | `centreEcl = body.GetPositionEcl()`, `surfaceRadius = MeanRadius + height`; any exception or non-finite value answers false |

**How a round uses it** (`Slug.Update`, `Interceptor.Update`):[^ksarmory-slug][^ksarmory-interceptor]

1. Once per frame, before the sub-step loop, ask `TryGround` for the round's pre-step position and
   hold `_groundCentre` / `_groundRadius`. A `Slug` asks only when `MunitionProfile.HitsTerrain`
   (bombs, reentry vehicles, the 5"/54 shell); every `Interceptor` asks.
2. Each sub-step, compute `was = Len(before - centre(t0)) - R` and `now = Len(after - centre(t1)) - R`.
3. A height fuse (`BurstHeightMetres`) fires on the downward crossing of that height; contact fires
   when `now <= 0`. The burst point is interpolated linearly along the sub-step
   (`f = was / (was - now)`) and `DetonationElapsedInFrame` records when in the step it happened.
4. An `Interceptor` only fuses on a crossing from above (`was > 0`), so a tube that sits a hair
   under the field does not burst on its own rail.

**Two epoch corrections.** The body was sampled at the end of the applied step, while the round is
part-way through it. The mod back-dates on the asking side (the interface has no time argument):

| Correction | Callback (WeaponSystem) | Effect |
|---|---|---|
| Centre drift | `GroundCentreDriftIntoFrame(t) = bodyVelocityEcl * t` | Walks the held centre back to the sub-step's instant (~30 km/s of orbital travel) |
| Query drift | `GroundQueryDriftIntoFrame(p, t) = GroundVelocityAt(p) * t` | Walks the *query point* forward by the ground's full velocity (orbit plus `omega x r` spin) so the end-of-step rotation reads the ground the round is really over |

The query drift is only used by a `Slug` with `GroundQueryAtOwnEpoch` set (the ICBM's released
warheads); everything else applies the centre drift only.[^ksarmory-weaponsystem]

**Near the surface, re-read.** A released warhead also sets `ResampleGroundNearImpact`: inside
`GroundResampleBandMetres` (200 m) of the held sphere it re-reads the ground under both ends of every
sub-step, and with `StopOnTheTerrain` it re-queries at the interpolated crossing and takes one
false-position step, so it stops on the terrain rather than on the chord between two samples.

**Other consumers of the same field.**

| Consumer | Wrapper | `accurate` | Why |
|---|---|---|---|
| Bomb sight, store reach | `CoarseGroundTest(GroundTest.Shared)` | true | Reuses the last sample until within `NearMetres` (600 m) of the cached surface or `ResampleMetres` (250 m) from where it was taken[^ksarmory-coarsegroundtest] |
| Radar horizon mask | `TerrainHeights(body)` via `KsaWorld.IsHiddenByTerrain` | false | Tens of samples per contact per scan |
| Cursor, laser spot, designator | `TerrainRay.TryFirstHit` with `TerrainHeights(body, accurate: true)` via `KsaWorld.TryRayGround` | true | Ray-march to the first above-to-below crossing |
| Burst height, height above terrain | `TerrainHeights(body, accurate: true)` | true | Once per burst |

`TerrainRay` walks from the eye in steps as long as the ray's clearance over the ground (slope bound
1:1), clamped to 1-1000 m and never shorter than 0.5% of the range, only within the band below the
body's highest terrain, then bisects the bracketing step to 0.5 m (at most 512 samples).[^ksarmory-terrainray]

## Lifecycle and timing

- **Where it runs.** `GroundTest.TryGround` is called from `WeaponSystem.UpdateRounds`, inside
  `StepSimulation`, which runs once per frame from whichever hook claims the mod's `FrameLatch`:
  `[StarMapAfterGui]` (`frame:draw-ui`, Program.cs L2265-L2269), the `Program.OnFrameCelestials`
  prefix (`frame:celestials`, L2701) when the UI is hidden, or `[StarMapAfterOnFrame]` as a last
  resort. Sights and the chase camera call it from the same pass. All callers are on the main thread;
  the body cache is deliberately not thread safe.[^ksarmory-groundtest]
- **Which instant the field answers for.** `Universe.ApplyVehicleSolvers` ends with
  `_lastSimStep = _nextSimStep` and `CurrentSystem.UpdatePerFrameData()` (Universe.cs
  L1947-L1950).[^ksa-universe] `Celestial.UpdatePerFrameData` sets `_ccf2Cci =
  GetCcf2Cci(Orbit.StateVectors.StateTime)` and `_ccf2Cce` from it (Celestial.cs L594-L608), and
  `GetTerrainHeightFromDirCce` rotates the direction by `GetCcf2Cce().Inverse()` (L777-L781). So
  both the centre and the rotation belong to the end of the step just applied, which is why the
  round back-dates its queries.[^ksa-celestial]
- **What is concurrent.** The next vehicle step is already running on workers when the hook runs
  (dispatched at Program.cs L2220). Celestial state is only rewritten in the next frame's
  `ApplyOrbitSolvers` / `UpdatePerFrameData`, so reading it from the hook is stable for the whole
  pass.
- **Why per frame, not per sub-step.** The height call is the expensive part (bicubic sample plus
  every procedural modifier on bodies that have them); a 150-shell burst cannot afford it per
  sub-step, which is also why most gun profiles leave `HitsTerrain` off.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Sim/IGroundTest.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/IGroundTest.cs#L35-L64) | Centre-and-radius contract; `GroundSurface.Height` sea clamp |
| [src/KSArmory/Ksa/GroundTest.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/GroundTest.cs#L39-L146) | Body choice and cache, the engine height call, the sea |
| [src/KSArmory/Sim/Slug.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Slug.cs#L464-L497) | Once-per-frame sample at the round's own epoch |
| [src/KSArmory/Sim/Slug.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Slug.cs#L720-L859) | Height fuse, contact crossing, near-impact re-read, crossing refinement |
| [src/KSArmory/Sim/Interceptor.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Interceptor.cs#L578-L597) | Missile ground crossing, from above only |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L3322-L3355) | Centre and query drift callbacks; wired into `RoundFields` at L3464-L3471 |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L818-L875) | Terrain ceiling bound and horizon mask |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L1011-L1052) | `TryRayGround`: nearest body a ray meets |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L2045-L2066) | `GroundVelocityAt`: orbit plus `GetAngularVelocityCce() x r` |
| [src/KSArmory/Sim/TerrainRay.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TerrainRay.cs) | Clearance-stepped ray march and bisection |
| [src/KSArmory/Sim/CoarseGroundTest.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/CoarseGroundTest.cs) | Sample reuse for whole-trajectory predictions |
| [src/KSArmory/Ksa/TerrainHeights.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/TerrainHeights.cs) | `ITerrainHeights` over one body, `accurate` selectable |
| [docs/KSA-TERRAIN.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/KSA-TERRAIN.md) | Author's analysis of the height field (build 5348) |

## Snippets

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

```csharp
// current/decomp/KSA/Celestial.cs L777-L781
	public double GetTerrainHeightFromDirCce(double3 positionDirCce, bool accurate = true)
	{
		double3 positionDirCcf = positionDirCce.Transform(GetCcf2Cce().Inverse());
		return GetTerrainHeightFromDirCcf(positionDirCcf, accurate);
	}
```

## Game APIs touched

| API | Decompiled file | Used for |
|---|---|---|
| `Celestial.GetTerrainHeightFromDirCce(dir, accurate)` (L777) and `GetTerrainHeightFromDirCcf` (L789) | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L777-L881) | The height field; `accurate` picks bicubic plus modifier `Evaluate` (true) or bilinear without them (false) |
| `Celestial.MaxTerrainHeightApprox` (L145), `UpdateApproxTerrainAltitudes` (L929) | same | Fallback terrain ceiling only |
| `Celestial.GetPositionEcl`, `MeanRadius`, `GetVelocityEcl` | same | Body centre and radius; centre drift |
| `Astronomical.MaxTerrainRadius` (L114), `HasTerrainHeightmap` (L132), `GetOceanReference()` (L387) | [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L114-L139) | Ceiling bound for masks and rays; the sea |
| `OceanReference.Level`, `Density` (L9, L12) | [current/decomp/KSA.Rendering.Water.Data/OceanReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Water.Data/OceanReference.cs) | Sea clamp |
| `IParentBody.GetAngularVelocityCce()` (L81) | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L81-L84) | Spin term of the query drift |
| `Universe.CurrentSystem`, `CelestialSystem.Count` / `GetIndex` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs) | Body scan |

## Pitfalls and version notes

- **Cce, not Ccf, for an ecliptic direction.** `GetTerrainHeightFromDirCce` applies the body's
  rotation for you; feeding an ecliptic direction to the `Ccf` entry point reads the ground at the
  wrong longitude by the body's whole rotation phase. The ICBM computer, which already holds
  body-fixed directions, correctly uses `Ccf`.[^ksarmory-ksa-terrain]
- **The engine's physics reads the same field, through a cache.** In build 5554
  `PhysicsEnvironment.RecomputePositionalValues` sets `TerrainRadius` through
  `TerrainRadiusCache.ComputeRadiusCcf(celestial, dir, 0.25)` (PhysicsEnvironment.cs L109), which
  calls `GetTerrainHeightFromDirCcf` with the default `accurate: true` and reuses the answer while the
  direction moves less than 0.25 m of arc (TerrainRadiusCache.cs L22-L38).[^ksa-physicsenvironment][^ksa-terrainradiuscache]
  `docs/KSA-TERRAIN.md` (written against 5348) still describes a direct call at
  `PhysicsEnvironment.cs:112`. The collision patch also uses the default (TerrainPatch.cs
  L518-L522), and KSA's own `TerrainImpactFinder` samples `accurate: false` (L64).[^ksa-terrainpatch][^ksa-terrainimpactfinder]
- **The sea is flat here and waves in the engine.** The mod clamps to `OceanReference.Level`; KSA's
  `ComputeOceanRadius` uses `OceanRenderer.GetOceanHeightAtPositionCcf` near the surface
  (PhysicsEnvironment.cs L120-L139), so the two differ by wave height.
- **`MaxTerrainHeightApprox` is not a bound.** It is computed in the `Celestial` constructor
  (Celestial.cs L235) before `Astronomical.SetupModifierRenderData` creates the modifier data, from
  16,384 Fibonacci directions, so it leaves out the procedural modifiers. `KsaWorld` therefore bounds
  terrain with `MaxTerrainRadius - MeanRadius + 8,000 m` (KsaWorld.cs L818-L846).
  `docs/KSA-TERRAIN.md` "Two smaller things" still says `KsaWorld` passes `MaxTerrainHeightApprox`;
  the code no longer does.[^ksa-celestial][^ksa-astronomical][^ksarmory-ksaworld]
- **Line drift in the doc.** `docs/KSA-TERRAIN.md` cites `Celestial.cs:780/:792/:833/:1077/:1585`,
  `:594` and `Universe.cs:1683`; in 5554 these are L777/L789/L832/L1025/L1501, L594-L608 and
  Universe.cs L1950. Earth's height range (`-10.930` to `8.631` km) is at Astronomicals.xml L549-L551,
  not L522.[^ksa-astronomicals-xml] The texel-spacing, quantisation and float-staircase figures in
  that doc come from measuring `Earth_Height.ktx2` and were not re-measured here (unverified).
- **Structures are not ground.** Only terrain is tested; a bomb dropped on a launch pad bursts
  beside the pedestal, and a 20 mm shell passes through a hill because gun profiles leave
  `HitsTerrain` off.
- **One frame of stale ground.** Without the near-impact re-read, a round stops on the sphere sampled
  at the frame's start, up to one frame of ground track behind; the error grows with the step, so
  timewarp makes it worse.

## See also

- [Mod-simulated rounds](./mod-simulated-rounds.md)
- [Analytic sub-frame fuses](./analytic-subframe-fuses.md)
- [Point gravity and atmosphere model](./point-gravity-and-atmosphere-model.md)
- [Ecliptic frames and epochs](./ecliptic-frames-and-epochs.md)
- [Guided bomb tail kit and sight](./guided-bomb-tail-kit-and-sight.md)
- [Radar search and lock](./radar-search-and-lock.md)
- [Cursor-ray world click tools](./cursor-ray-world-click-tools.md)
- [ICBM ballistic computer](./icbm-ballistic-computer.md)
- [Per-frame centre and radius, crossing test, interpolation and near-surface resample](../../../howto/celestials/terrain/measure-slope-and-detect-ground-contact-on-terrain/measure-slope-and-detect-ground-contact-on-terrain-3.md) - how-to article demonstrating this feature
- [Answer as a body centre and surface radius, pick the body by depth, mask radar by horizon](../../../howto/celestials/terrain/query-terrain-height-and-line-of-sight/query-terrain-height-and-line-of-sight-2.md) - how-to article demonstrating this feature
- [Cache decorator that reuses a height sample until near the ground or moved](../../../howto/celestials/terrain/query-terrain-height-and-line-of-sight/query-terrain-height-and-line-of-sight-3.md) - how-to article demonstrating this feature

[^ksarmory-igroundtest]: src/KSArmory/Sim/IGroundTest.cs
[^ksarmory-groundtest]: src/KSArmory/Ksa/GroundTest.cs
[^ksarmory-slug]: src/KSArmory/Sim/Slug.cs
[^ksarmory-interceptor]: src/KSArmory/Sim/Interceptor.cs
[^ksarmory-weaponsystem]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksarmory-coarsegroundtest]: src/KSArmory/Sim/CoarseGroundTest.cs
[^ksarmory-terrainray]: src/KSArmory/Sim/TerrainRay.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-ksa-terrain]: docs/KSA-TERRAIN.md (author notes, build 5348)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-astronomical]: KSA.Astronomical (decompiled, build 2026.10.10.5554)
[^ksa-physicsenvironment]: KSA.PhysicsEnvironment (decompiled, build 2026.10.10.5554)
[^ksa-terrainradiuscache]: KSA.TerrainRadiusCache (decompiled, build 2026.10.10.5554)
[^ksa-terrainpatch]: KSA.TerrainPatch (decompiled, build 2026.10.10.5554)
[^ksa-terrainimpactfinder]: KSA.TerrainImpactFinder (decompiled, build 2026.10.10.5554)
[^ksa-astronomicals-xml]: Content/Core/Astronomicals.xml (build 2026.10.10.5554)
