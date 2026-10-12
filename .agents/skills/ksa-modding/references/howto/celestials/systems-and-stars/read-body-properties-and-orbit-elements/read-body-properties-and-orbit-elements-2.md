---
type: How-To
title: Atmosphere height, density and ring templates feeding classification decisions, and angular velocity to a stationary orbit radius
description: Use a body's atmosphere height and radius to classify gas giants, derive a low-orbit radius, mark ringed bodies and compute the synchronous orbit radius from the angular velocity, all from live data with no per-body config
category: celestials
group: systems-and-stars
topic: read-body-properties-and-orbit-elements
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-deltavmap/features/body-state-ladder-and-you-are-here]
techniques: [system-graph]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.IParentBody
  - KSA.AtmosphereReference
  - KSA.PhysicalAtmosphereReference
  - KSA.PlanetaryRingsReference
  - KSA.AstronomicalTemplate
tags: [atmosphere, rings, gas-giant, low-orbit, stationary-orbit, classification]
---
Outcome: for any body, modded or stock, decide whether it has a solid surface, where a sensible parking orbit is, whether it has rings and where the synchronous orbit lies. Demonstrated by KSA-DeltaVMap's body ladders ([Body state ladder](../../../../mods/ksa-deltavmap/features/body-state-ladder-and-you-are-here.md)).

## Background: how the game does it

There is no "gas giant" flag and no "low orbit" value, so the map derives both from data the game does expose: `GetAtmosphereReference()` (null when airless), `Physical.Height`, `MeanRadius`, `SphereOfInfluence`, `GetNearSurfaceRadius()` and `GetAngularVelocity()`. `Astronomical.GetNearSurfaceRadius` returns `MaxTerrainRadius + 1` and `AtmosphericBody` overrides it to mean radius plus atmosphere height plus 1 m when it has an atmosphere ([current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L384), [current/decomp/KSA/AtmosphericBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphericBody.cs#L20-L28)). `BodyTemplate.RingsReference` is non-null only for bodies whose XML declares rings ([current/decomp/KSA/AstronomicalTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AstronomicalTemplate.cs#L66)).

## Lifecycle and timing

The ladder is computed once per loaded system when the graph is built from the draw hook (`frame:draw-ui`, see [enumerate star systems](../enumerate-star-systems-and-bodies/enumerate-star-systems-and-bodies-1.md)); all inputs are template data, mass, radius and SOI that do not change. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Gas giant by data:** an atmosphere plus a radius of at least 15,000 km has no solid surface (stock Neptune, about 24,600 km, is above it; Earth below). Airless bodies always keep their surface:

```csharp
// DeltaVMap/Dv/OrbitalStates.cs L119-L124
public static bool HasSolidSurface(IParentBody body)
{
    if (!HasAtmosphere(body))
        return true;
    return body.MeanRadius < GasGiantRadiusMeters;
}
```

2. **Low orbit:** about 10 km above the atmosphere or the highest terrain, never below 5 percent of the radius for airless bodies and never below `R + 5 km`:

```csharp
// DeltaVMap/Dv/LowOrbitHeuristic.cs L15-L35
public static double Compute(double meanRadius, bool hasAtmosphere, double atmosphereHeight, double nearSurfaceRadius)
{
    double rLo;
    if (hasAtmosphere)
    {
        // About 10 km above the atmosphere boundary (or 10% of its height,
        // whichever is larger, for very tall atmospheres).
        rLo = meanRadius + atmosphereHeight + Math.Max(ClearanceMeters, 0.1 * atmosphereHeight);
    }
    else
    {
        // About 10 km above the highest terrain, but never below 5% of the
        // body radius. The 5% term is a sane minimum parking altitude that
        // wins for larger airless bodies (e.g. Luna lands at ~87 km, not the
        // ~20 km the bare 10 km clearance would give), leaving margin over
        // unmodelled terrain.
        rLo = Math.Max(nearSurfaceRadius + ClearanceMeters, meanRadius * 1.05);
    }

    return Math.Max(rLo, meanRadius + AbsoluteFloorMeters);
}
```

3. **Stationary orbit:** `T = 2 pi / |omega|`, `r = cbrt(mu T^2 / (4 pi^2))`, only when the body rotates and the radius lies inside the sphere of influence:

```csharp
// DeltaVMap/Dv/OrbitalStates.cs L137-L149
public static double? ComputeStationaryRadius(IParentBody body)
{
    double omega = Math.Abs(body.GetAngularVelocity());
    if (omega == 0.0)
        return null;

    double rSync = DeltaVCalculator.SynchronousRadius(body.Mu, omega);
    double soi = body.SphereOfInfluence;
    if (double.IsFinite(soi) && rSync >= soi)
        return null;

    return rSync;
}
```

```csharp
// DeltaVMap/Dv/DeltaVCalculator.cs L202-L206
public static double SynchronousRadius(double mu, double omega)
{
    double rotationPeriod = 2.0 * Math.PI / Math.Abs(omega);
    return Math.Cbrt(mu * rotationPeriod * rotationPeriod / (4.0 * Math.PI * Math.PI));
}
```

4. **Rings:** `body.BodyTemplate.RingsReference != null` marks a ringed body; the map never fabricates rings the game does not model.

5. **Guard tiny moons:** when the SOI is at or below the mean radius the body cannot hold an orbit and becomes a surface-only flyby destination; when the heuristic low orbit lands above the SOI, clamp to `(MeanRadius + SOI) / 2`.

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Dv/OrbitalStates.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/OrbitalStates.cs) | Ladder, surface rule, stationary radius |
| [DeltaVMap/Dv/LowOrbitHeuristic.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/LowOrbitHeuristic.cs) | Low orbit radius |
| [DeltaVMap/Dv/DeltaVCalculator.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/DeltaVCalculator.cs#L202-L206) | Synchronous radius |

## Variations and gotchas

- **15,000 km is a convention,** not an engine constant.
- **Stationary orbit uses the sidereal spin,** which is meaningless for a tidally locked body whose `GetAngularVelocity()` matches its orbit.
- **No per-body config:** modded systems get sensible ladders from the same reads.

## See also

- [Overview](overview.md); [approach 1](read-body-properties-and-orbit-elements-1.md); [approach 3](read-body-properties-and-orbit-elements-3.md)
- [Body state ladder](../../../../mods/ksa-deltavmap/features/body-state-ladder-and-you-are-here.md)
- [Plan multi-leg and interstellar delta-v routes](../../../flight-and-orbits/transfers/plan-multi-leg-and-interstellar-delta-v-routes/overview.md)
