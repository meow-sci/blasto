---
type: How-To
title: IParentBody.GetAtmosphereReference and GetOceanReference projected into a snapshot
description: Read mass, radius, spin, atmosphere and ocean data of any body through the IParentBody interface into an immutable snapshot, treating a missing atmosphere or ocean as null and caching constants per body
category: celestials
group: systems-and-stars
topic: read-body-properties-and-orbit-elements
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/celestial-body-catalog-reads]
techniques: [immutable-snapshot]
hooks: [starmap StarMapBeforeGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.IParentBody
  - KSA.AtmosphereReference
  - KSA.PhysicalAtmosphereReference
  - KSA.Rendering.Water.Data.OceanReference
  - KSA.Celestial
  - KSA.Orbit
tags: [atmosphere, ocean, snapshot, angular-velocity, orbit]
---
Outcome: a body-data record with `mass`, `radius`, `mu`, `soi`, `rotation_rate`, orbit elements, atmosphere and ocean constants, assembled without touching private state. Demonstrated by gatOS ([Celestial body catalog reads](../../../../mods/gatos/features/celestial-body-catalog-reads.md)).

## Background: how the game does it

`IParentBody` is implemented by both `Celestial` and `StellarBody`, and exposes `Mu`, `SphereOfInfluence`, `GetAtmosphereReference()` and `GetOceanReference()` ([current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L15-L59)); reading through the interface means a body-type rename surfaces in one place. `GetAtmosphereReference()` returns an `AtmosphereReference` whose `Physical` has `Height`, `ScaleHeight`, `SeaLevelPressure` and `SeaLevelDensity` ([current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L12-L23)); these are reference wrappers with implicit `double` conversions, so cast explicitly. Ocean presence means an `OceanReference` that is non-null **and** `Density > 0`. `GetAngularVelocity()` is a rate in rad/s ([current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L78)). Orbit elements are constants for a body on rails; the only live values are position and velocity.

## Lifecycle and timing

The reader runs on the main thread in the telemetry sampler's slot (`frame:viewports` or `frame:draw-ui`). Template data is loaded once with the system; orbit solver results are copied onto bodies by `Universe.ApplyOrbitSolvers` at the start of the next `PrepareFrame`, so reads later in the frame are race-free. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Project atmosphere and ocean through the interface,** keeping null as null and sanitising non-finite values:

```csharp
// gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs L182-L195
private static AtmosphereSnapshot? Atmosphere(AtmosphereReference? reference)
{
    if (reference is null)
        return null;
    var physical = reference.Physical;
    return new AtmosphereSnapshot(
        Sanitize.Finite((double)physical.Height),
        Sanitize.Finite((double)physical.ScaleHeight),
        Sanitize.Finite((double)physical.SeaLevelPressure),
        Sanitize.Finite((double)physical.SeaLevelDensity));
}

private static OceanSnapshot? Ocean(OceanReference? reference)
    => reference is null ? null : new OceanSnapshot(Sanitize.Finite((double)reference.Density));
```

2. **Build statics once per body** (orbit elements with apoapsis gated on `Orbit.IsBound()`, parent and child ids, mass, radius, mu, SOI, rotation rate):

```csharp
// gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs L93-L132
private static BodyStatics BuildCelestialStatics(Celestial c)
{
    OrbitSnapshot? orbit = null;
    if (c.Orbit is { } o && c.Parent is { } parent)
    {
        var parentRadius = parent.MeanRadius;
        orbit = new OrbitSnapshot(
            Sanitize.ApoapsisToAltitude(o.Apoapsis, parentRadius, o.IsBound()),
            Sanitize.RadiusToAltitude(o.Periapsis, parentRadius),
            Sanitize.Finite(o.Eccentricity),
            Sanitize.Finite(o.Inclination * RadToDeg),
            Sanitize.Finite(o.SemiMajorAxis),
            Sanitize.Finite(o.Period))
        {
            LanDeg = Sanitize.Finite(o.LongitudeOfAscendingNode * RadToDeg),
            ArgPeDeg = Sanitize.Finite(o.ArgumentOfPeriapsis * RadToDeg),
        };
    }

    var children = c.Children;
    string[] childIds = children.Count == 0 ? [] : new string[children.Count];
    for (var i = 0; i < childIds.Length; i++)
        childIds[i] = children[i].Id;

    return new BodyStatics
    {
        Id = c.Id,
        Class = c.Class,
        ParentId = c.Parent?.Id,
        ChildIds = childIds,
        Mass = Sanitize.Finite(c.Mass),
        MeanRadius = Sanitize.Finite(c.MeanRadius),
        Mu = Sanitize.Finite(((IParentBody)c).Mu),
        SoiMeters = Sanitize.Finite(c.SphereOfInfluence),
        RotationRateRadS = Sanitize.Finite(c.GetAngularVelocity()),
        Orbit = orbit,
        Atmosphere = Atmosphere(c.GetAtmosphereReference()),
        Ocean = Ocean(c.GetOceanReference()),
    };
}
```

3. **Report apsides as altitudes over the parent** (`Apoapsis - parent radius`, 0 for an unbound orbit), angles in degrees, and angular velocity in rad/s next to them.

4. **Cache constants once and read only ECL state per sample** (`GetPositionEcl`, `GetVelocityEcl`); see [cache and key per-body data](../cache-and-key-body-data-per-loaded-system/overview.md) for the cache choices.

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs#L182-L195) | Atmosphere and ocean projection |
| [gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/BodyReader.cs#L93-L132) | Per-celestial statics |

## Variations and gotchas

- **Do not assume a star is a `Celestial`;** `StellarBody` has its own statics.
- **Ocean presence is `Density > 0`,** not `IsValid()` (`DistanceReference.IsValid` needs more than 100 km, which Earth's sea level fails).
- **Unbound orbits** have no apoapsis; gate on `Orbit.IsBound()`.
- **Cast `double` explicitly** from reference wrappers.

## See also

- [Overview](overview.md); [approach 2](read-body-properties-and-orbit-elements-2.md)
- [Celestial body catalog reads](../../../../mods/gatos/features/celestial-body-catalog-reads.md)
- [Enumerate star systems and bodies](../../../celestials/systems-and-stars/enumerate-star-systems-and-bodies/overview.md)
