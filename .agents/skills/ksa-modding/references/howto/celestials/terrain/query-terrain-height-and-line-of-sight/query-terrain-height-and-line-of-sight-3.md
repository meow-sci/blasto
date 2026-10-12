---
type: How-To
title: Cache decorator that reuses a height sample until near the ground or moved
description: Keep trajectory previews affordable by wrapping the ground test in a decorator that re-samples only within 600 m of the cached surface or 250 m from where it last sampled
category: celestials
group: terrain
topic: query-terrain-height-and-line-of-sight
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/terrain-ground-impact-test, mods/ksagolf/features/terrain-height-queries]
techniques: [decorator-cache, terrain-query]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Celestial, KSA.Universe, KSA.CelestialSystem]
tags: [terrain, cache, decorator, accurate, preview, performance]
---
Outcome: a sight or planner that flies a whole trajectory inside one frame (up to 2,048 integration steps) pays for terrain lookups only near the ground. The decorator is KSArmory's `CoarseGroundTest` ([Terrain ground impact test](../../../../mods/ksarmory/features/terrain-ground-impact-test.md)); KSAGolf ships the same class for its trajectory previews ([Terrain height queries](../../../../mods/ksagolf/features/terrain-height-queries.md)). It assumes the ground-test seam from [approach 2](query-terrain-height-and-line-of-sight-2.md).

## Background: how the game does it

The engine query costs a cubemap fetch and, when `accurate` is true, every procedural modifier ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L789-L881)). The engine itself caches: `TerrainRadiusCache.ComputeRadiusCcf` returns the previous radius while the direction has moved less than `maxSampleArcMeters / MeanRadius`, and the physics path passes 0.25 m ([current/decomp/KSA/TerrainRadiusCache.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TerrainRadiusCache.cs#L22-L38), [current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L109)). A mod previewing a trajectory kilometres above the ground can be far lazier: the surface cannot affect the outcome until the point is near it.

## Lifecycle and timing

`BombSightOverlay` solves every frame from `[StarMapAfterGui]` (`frame:draw-ui`), on the main thread, flying the prediction inside the call. The cache must therefore be reset at the start of each solve, not between frames: its job is to skip lookups down *one* trajectory, and a sample kept from the previous frame would be trusted from the wrong place. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Write the decorator** over the `IGroundTest` seam: reuse the last answer unless the point is within `NearMetres` of the cached surface or has moved `ResampleMetres` from where it was sampled.

```csharp
// src/KSArmory/Sim/CoarseGroundTest.cs L21-L69
internal sealed class CoarseGroundTest(IGroundTest inner) : IGroundTest
{
    /// <summary>Within this of the cached surface, every step samples again.</summary>
    public const double NearMetres = 600.0;

    /// <summary>And regardless of height, once it has moved this far from where it last sampled.</summary>
    public const double ResampleMetres = 250.0;

    private bool _have;
    private double3 _sampledAt;
    private double3 _centre;
    private double _radius;

    /// <summary>Samples taken, and samples avoided. Diagnostic only.</summary>
    public int Sampled { get; private set; }

    /// <inheritdoc />
    public bool TryGround(double3 positionEcl, out double3 centreEcl, out double radius)
    {
        if (_have && !MustResample(positionEcl))
        {
            centreEcl = _centre;
            radius = _radius;
            return true;
        }

        Sampled++;
        _have = inner.TryGround(positionEcl, out _centre, out _radius);
        _sampledAt = positionEcl;

        centreEcl = _centre;
        radius = _radius;
        return _have;
    }

    /// <summary>Forgets the cached sample, so a fresh trajectory starts by asking.</summary>
    public void Reset()
    {
        _have = false;
        Sampled = 0;
    }

    // Near the surface, or far from where the surface was last looked at.
    private bool MustResample(double3 positionEcl)
        => Vec.Len(positionEcl - _centre) - _radius <= NearMetres
           || Vec.Len(positionEcl - _sampledAt) >= ResampleMetres;
}

```

2. **Give each solver its own instance** and reset it per solve; two sights solving at once must not share one another's last sample:

```csharp
// src/KSArmory/Ksa/BombSightOverlay.cs L47-L51
private double _sinceTrace;

// Its own, because it caches across a trajectory and two sights solving at once must not share
// one another's last sample.
private readonly CoarseGroundTest _ground = new(GroundTest.Shared);
```

```csharp
// src/KSArmory/Ksa/BombSightOverlay.cs L251-L258

// Reset per solve: the cache exists to skip lookups down one trajectory, not to remember the
// last one, and a sample kept from the previous frame's fall would be trusted from the wrong
// place.
private IGroundTest Ground()
{
    _ground.Reset();
    return _ground;
```

3. **Pass it where the real test went.** The prediction code takes an `IGroundTest` and neither knows nor cares that it is cached. `StoreReach` and `TailKitReach` do the same and call `Reset()` before each pass.

## What `accurate` true and false change, and how to cache around it

| Use | `accurate` | Why |
|---|---|---|
| Round stopping, bomb sight, cursor and designator | `true` | Must agree with the surface a craft stands on; includes levelling decals and detail |
| Radar horizon mask, tens of samples per contact per scan | `false` | Bilinear, no modifiers: cheaper, and the question is "is a ridge in the way" |
| Engine impact predictor (`TerrainImpactFinder`) | `false` | Same trade the engine made ([current/decomp/KSA/TerrainImpactFinder.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TerrainImpactFinder.cs#L64)) |

A cache is the other way to save cost without giving up modifiers: with the thresholds above, the terminal phase (the part that decides where the bomb lands) is sampled as densely as before, while the kilometres above it cost almost nothing. Choose the thresholds from how fast the answer can change: 250 m protects against ground rising under a stale sample, 600 m against a stale surface being trusted near contact.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Sim/CoarseGroundTest.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/CoarseGroundTest.cs) | The decorator |
| [src/KSArmory/Ksa/BombSightOverlay.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BombSightOverlay.cs#L47-L51) | Owner and per-solve reset |
| [src/KSArmory/Sim/BombSight.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BombSight.cs#L95-L125) | The trajectory loop that benefits |

## Variations and gotchas

- **A cache is only valid for one body.** `CoarseGroundTest` stores the centre and radius from the inner test, so a trajectory that leaves one body for another keeps the old answer until the 250 m movement rule fires; keep trajectories within one body's neighbourhood or reset when the body changes.
- **Never share across consumers** that fly different trajectories.
- **Tune from measurements.** The `Sampled` counter on the decorator exists to measure how many lookups were avoided.

## See also

- [Overview](overview.md); [approach 1](query-terrain-height-and-line-of-sight-1.md); [approach 2](query-terrain-height-and-line-of-sight-2.md)
- [Terrain ground impact test](../../../../mods/ksarmory/features/terrain-ground-impact-test.md), [Terrain height queries](../../../../mods/ksagolf/features/terrain-height-queries.md)
- [Simulate bodies outside the vehicle system](../../../physics/mod-simulation/simulate-bodies-outside-the-vehicle-system/overview.md)
