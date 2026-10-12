---
type: Mod Feature
title: Celestial body catalog reads
description: Reading every planet, moon and the star into a body catalog, caching the session-constant data per body so each sample only reads ECL position and velocity
mod: gatos
category: celestials
techniques: [starmap-lifecycle, harmony-prefix, immutable-snapshot, conditional-weak-table-cache]
hooks: [harmony-prefix KSA.GameViewport.OnFrame, starmap StarMapBeforeGui]
lifecycle: [frame:viewports, frame:draw-ui]
game_types:
  - KSA.CelestialSystem
  - KSA.Celestial
  - KSA.StellarBody
  - KSA.IParentBody
  - KSA.Universe
  - KSA.Orbit
  - KSA.AtmosphereReference
  - KSA.PhysicalAtmosphereReference
tags: [telemetry, celestials, bodies, atmosphere, ocean, caching, coordinate-frames]
---

`/sim/bodies/<id>/` and `/sim/system/` expose the whole star system: mass, radius, gravitational
parameter, sphere of influence, rotation rate, parent and children, orbit elements, atmosphere and
ocean parameters, plus live ECL position and velocity. `BodyReader` shows how to walk KSA's
celestial system, which body types to expect, and how to keep the per-sample cost to two vector
reads per body.

## How it works

- **Walk the system.** `Universe.CurrentSystem.All.UnsafeAsList()` holds every `Astronomical`.
  Planets and moons are `Celestial`; the root star is a separate `StellarBody`, which is not a
  `Celestial`, so the reader adds `Universe.WorldSun` explicitly when the walk did not list it
  ([gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs#L56-L76)).
- **Read through `IParentBody`.** Both body types implement `IParentBody`, so `Mu`
  (`Mass * 6.6743E-11`), `GetAtmosphereReference()` and `GetOceanReference()` are read through the
  interface; a body-type rename then surfaces in one place.[^ksa-iparentbody]
- **Static split.** Everything except position and velocity is constant for a session, including
  orbit elements, because bodies are on rails. The reader caches a `BodyStatics` per body in a
  `ConditionalWeakTable<Celestial, BodyStatics>` (collected when the system changes) and assembles
  each sample from the cached statics plus `GetPositionEcl()`/`GetVelocityEcl()`
  ([gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs#L82-L127)). The repo's comment records the
  pre-cache cost as about 84 allocations per tick for a 20-body system.
- **Sub-cadence.** The sampler can resample bodies slower than vessels
  (`telemetry_bodies_rate_hz`); in-between ticks re-publish the same list by reference so consumers
  can reference-compare for "unchanged" ([gatOS.GameMod/Game/TelemetrySampler.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/TelemetrySampler.cs#L304-L316)).
- **System summary.** `CelestialSystem` has no name, so the system is named after its star;
  `HomeBody` gives the launch body.

**Frames.** Body positions are ECL (star-centred inertial root frame). Vessel positions are CCI
relative to the parent body. To combine them, add the parent's ECL position to the vessel's CCE
offset, as `Vehicle.GetPositionEcl()` does internally (`Parent.GetPositionEcl() + _positionCce`).[^ksa-vehicle]
The frame definitions (ECL, ORB, CCE, CCF, CCI) are summarized in
[Vessel flight-state reads](vessel-flight-state-reads.md) from the repo's
[docs/KSA_CELESTIAL_COORDINATE_FRAMES.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/docs/KSA_CELESTIAL_COORDINATE_FRAMES.md).

| Field | Source | Unit |
|---|---|---|
| `class` | `Celestial.Class` (`Planet` when the parent is a `StellarBody`, else `Moon`); `Star` for the root | text |
| `mass`, `radius`, `mu`, `soi` | `Mass`, `MeanRadius`, `IParentBody.Mu`, `SphereOfInfluence` | kg, m, m3/s2, m |
| `rotation_rate` | `GetAngularVelocity()` | rad/s |
| `orbit/*` | `Celestial.Orbit` (apsides as altitudes over the parent, angles in degrees) | m, deg |
| `atmosphere/*` | `AtmosphereReference.Physical.{Height,ScaleHeight,SeaLevelPressure,SeaLevelDensity}` | m, Pa, kg/m3 |
| `ocean/*` | `OceanReference.Density` | kg/m3 |

## Lifecycle and timing

Runs in the sampler slot on the main thread (see
[Telemetry sampler and snapshot store](telemetry-sampler-snapshot.md)). Body positions come from
the rails ephemeris and do not depend on the in-flight vehicle step. The orbit-solver jobs that
`PrepareFrame` dispatches only stage new state vectors in their `CelestialUpdateTask`, and
`Universe.ApplyOrbitSolvers` copies them onto the bodies at the start of the next `PrepareFrame`
([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1905-L1920)), so reads later in the frame are race-free. Statics are
built lazily on first sight of each body.

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs#L56-L76) | system walk, star added explicitly, system summary |
| [gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs#L82-L127) | per-celestial statics cache |
| [gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs#L180-L193) | atmosphere and ocean projection |
| [gatOS.GameMod/Game/TelemetrySampler.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/TelemetrySampler.cs#L304-L316) | bodies sub-cadence |

## Snippets

```csharp
// gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs L56-L86
            + "cached per system instance (GP3) — consumers can reference-compare it for 'unchanged'.")]
    internal static (IReadOnlyList<BodySnapshot> Bodies, SystemSnapshot? System) Sample(CelestialSystem system)
    {
        var all = system.All.UnsafeAsList();
        var bodies = new List<BodySnapshot>(all.Count);
        var sun = Universe.WorldSun;
        var sunListed = false;
        foreach (var astronomical in all)
            if (astronomical is Celestial celestial)
            {
                bodies.Add(FromCelestial(celestial));
                if (sun is not null && celestial.Id == sun.Id)
                    sunListed = true;
            }

        if (sun is not null && !sunListed)
            bodies.Add(FromStar(sun, bodies));

        var summary = SystemCache.GetValue(system,
            s => new SystemSnapshot(Universe.WorldSun?.Id ?? "", s.HomeBody?.Id, Universe.WorldSun?.Id));
        return (bodies, summary);
    }

    [KsaAnchor("Celestial.{Id,Class,Parent,Children,Mass,MeanRadius,SphereOfInfluence,GetAngularVelocity,Orbit}; "
               + "IParentBody.{Mu,GetAtmosphereReference,GetOceanReference}; Orbit.IsBound()",
        SourceFile = "KSA/Celestial.cs / KSA/Orbit.cs:1819", Verified = "2026-09-25",
        GameVersion = "2026.9.22.5482", Risk = ChurnRisk.Low,
        Notes = "The session-constant catalog members (everything but GetPositionEcl/GetVelocityEcl) are "
            + "read once per body instance and cached (GP3); bodies are on rails, so the orbit elements "
            + "are constants too. 5482: apoapsis is gated on Orbit.IsBound() (0 when unbound), the gate "
            + "KSA's own Universe Manifest adopted at rev 5439; Celestial.cs is unchanged.")]
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Universe.WorldSun` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L106-L112) | the root `StellarBody` |
| `CelestialSystem.All`, `HomeBody` | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L64-L66) | body enumeration, home body |
| `Celestial.Class`, `Children`, `SphereOfInfluence`, `GetAngularVelocity()` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs) | catalog statics |
| `StellarBody` | [current/decomp/KSA/StellarBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StellarBody.cs) | star statics |
| `IParentBody.Mu`, `GetAtmosphereReference`, `GetOceanReference` | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L15-L59) | interface reads |
| `PhysicalAtmosphereReference` | [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs) | atmosphere constants |

## Pitfalls and version notes

- Do not assume the star appears as a `Celestial`; it is a `StellarBody` and needs its own branch.
- Atmosphere values are reference wrappers with implicit `double` conversions; cast explicitly.
- Apoapsis of an unbound orbit is forced to 0 (same `Orbit.IsBound()` guard as vessels).
- Cache keys are body instances, so a system reload naturally invalidates the cache.

## See also

- [Vessel flight-state reads](vessel-flight-state-reads.md)
- [Telemetry sampler and snapshot store](telemetry-sampler-snapshot.md)
- [gatOS overview](../overview.md)
- [ConditionalWeakTable of immutable statics per Celestial](../../../howto/celestials/systems-and-stars/cache-and-key-body-data-per-loaded-system/cache-and-key-body-data-per-loaded-system-2.md) - how-to article demonstrating this feature
- [Flat scan of CurrentSystem.All plus an explicit WorldSun](../../../howto/celestials/systems-and-stars/enumerate-star-systems-and-bodies/enumerate-star-systems-and-bodies-3.md) - how-to article demonstrating this feature
- [IParentBody.GetAtmosphereReference and GetOceanReference projected into a snapshot](../../../howto/celestials/systems-and-stars/read-body-properties-and-orbit-elements/read-body-properties-and-orbit-elements-1.md) - how-to article demonstrating this feature

[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
