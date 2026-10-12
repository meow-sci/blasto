---
type: Mod Feature
title: Body statistics derived from live game data
description: On the first UI frame after a universe loads, Compendium matches its JSON entries to live bodies and computes mass, gravity, escape velocity, tilt, inclination, periods, axes, sphere of influence and atmosphere strings from game data
mod: compendium
category: celestials
techniques: [reflection]
hooks: [starmap StarMapAfterGui, reflection KSA.Astronomical.OrbitLineMode]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Celestial
  - KSA.Astronomical
  - KSA.Orbit
  - KSA.CelestialTemplate
  - KSA.RotationTemplate
  - KSA.AstronomicalTemplate
  - KSA.AtmosphereReference
  - KSA.PhysicalAtmosphereReference
  - KSA.OrbitUiMode
tags: [celestials, statistics, orbit, atmosphere, reflection, units]
---

The body page shows mean radius, mass (with Earth or Moon equivalents), surface gravity, escape
velocity, axial tilt, eccentricity, inclination (relative to the parent's equator and to the star's
plane for moons), orbital period, sidereal period or tidal lock, semi-major and semi-minor axes,
sphere of influence, current orbital speed, and atmosphere height and sea-level pressure. Only the
prose comes from JSON; the numbers come from the game, so a mod that changes a body's system data
changes what Compendium shows.

## How it works

1. **Once, after the universe exists.** The draw hook calls `AttachInfoBodyJsonDict()` on the first
   frame where `Universe.WorldSun` is set, guarded by a static flag
   ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L73-L91)).[^cmp-compendiummain]
2. **Match entries to bodies.** After adding system-key aliases (see
   [Cross-mod JSON catalog](cross-mod-json-catalog.md)), it walks every dictionary key, resolves the
   body under the root named by the key prefix, or under any root, and skips keys that match no loaded
   celestial ([src/CdmAttachInfoBodyJsonData.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmAttachInfoBodyJsonData.cs#L81-L105)).[^cmp-cdmattachinfobodyjsondata]
3. **Fill the computed fields** on the shared `CompendiumData` object
   ([src/CdmAttachInfoBodyJsonData.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmAttachInfoBodyJsonData.cs#L107-L281)):[^ksa-celestial] [^ksa-orbit] [^ksa-rotationtemplate] [^ksa-astronomicaltemplate] [^ksa-atmospherereference] [^ksa-physicalatmospherereference]

   | Shown value | Game source | How |
   |---|---|---|
   | Mean radius, mass | `Celestial.MeanRadius`, `Celestial.Mass` ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L87-L89)) | km; mass with an SI-prefixed unit and Earth or Moon ratios chosen by size |
   | Surface gravity, escape velocity | `Mass`, the public field `Celestial._meanRadius` ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L27)) | `G M / R^2` and `sqrt(2 G M / R)` with G = 6.67430e-11 |
   | Orbital period | `Celestial.Orbit.Period` ([current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1209)) | hours, days or years |
   | Axial tilt | `BodyTemplate.Rotation.Tilt` for bodies orbiting a star; for moons the X angle of `GetCci2Orb().Inverse()` ([current/decomp/KSA/RotationTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RotationTemplate.cs#L10-L26), [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L507-L516)) | degrees |
   | Inclination | `Celestial.Inclination` ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L97-L105)); for moons minus the X angle of `GetOrb2Cci()`, plus a figure relative to the star's plane from the parent's tilt | degrees |
   | Eccentricity, semi-major and semi-minor axes, orbit type | `Eccentricity`, `SemiMajorAxis`, `SemiMinorAxis`, `Orbit.GetType().Name` | km, and AU above 0.1 AU |
   | Sidereal period, tidal lock, retrograde | `Rotation.SiderealPeriod`, `IsTidallyLocked`, `IsRetrograde` | hours or days |
   | Sphere of influence | `Celestial.SphereOfInfluence` ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L81)) | km |
   | Atmosphere | `BodyTemplate.AtmosphereReference?.Physical.Height`, `SeaLevelPressure` ([current/decomp/KSA/AstronomicalTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AstronomicalTemplate.cs#L57), [current/decomp/KSA/AtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereReference.cs#L10-L11), [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L12-L23)) | km and atm |

4. **One live value.** Current orbital speed is not cached; the body page reads
   `celestial.OrbitalSpeed` (the length of the CCI velocity) every frame
   ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L629-L631), [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L130-L137)).
5. **Reflection for one property.** `OrbitLineMode` is read with
   `GetType().GetProperty("OrbitLineMode")?.GetValue(...)` inside a `try`, so a rename only loses that
   one field ([src/CdmAttachInfoBodyJsonData.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmAttachInfoBodyJsonData.cs#L8-L19)). In build 2026.10.10.5554 it is a public
   abstract property of `Astronomical`, overridden per body class, for example `Never` for barycenters
   and `Near` for terrestrial bodies and comets ([current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L98),
   [current/decomp/KSA/Barycenter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Barycenter.cs#L13), [current/decomp/KSA/TerrestrialBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TerrestrialBody.cs#L7)).[^ksa-astronomical] [^ksa-barycenter] [^ksa-terrestrialbody]

## Lifecycle and timing

- **`frame:draw-ui`, first frame with a universe.** The attach runs inside `[StarMapAfterGui]`, on the
  main thread, before the window draws. Compendium's JSON was loaded much earlier, at
  `startup:immediate-load`, when no celestial existed; this is the first point where both exist and
  ImGui is not required for the work itself.
- **What it reads.** Templates (`BodyTemplate`, rotation, atmosphere) are load-time data; mass, radius,
  orbit elements and sphere of influence are fixed for celestials on Keplerian orbits. Reading them on
  the main thread during the UI pass is safe; only the orbital speed changes over time, and it is read
  live.

## Key code

| Path | Role |
|---|---|
| [src/CdmAttachInfoBodyJsonData.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmAttachInfoBodyJsonData.cs#L8-L19) | Reflection with a logged fallback |
| [src/CdmAttachInfoBodyJsonData.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmAttachInfoBodyJsonData.cs#L81-L138) | Key-to-body matching, radius, mass, gravity, escape velocity |
| [src/CdmAttachInfoBodyJsonData.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmAttachInfoBodyJsonData.cs#L140-L281) | Period, tilt, inclination, sidereal period, axes, sphere of influence, atmosphere |
| [src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L767-L796) | Mass unit formatting |
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L541-L660) | Where the strings are shown |

## Snippets

```csharp
// src/CdmAttachInfoBodyJsonData.cs L8-L19
private static string? TryGetOrbitLineMode(Celestial bodyCelestial)
{
    try
    {
        return bodyCelestial.GetType().GetProperty("OrbitLineMode")?.GetValue(bodyCelestial)?.ToString();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Compendium: Unable to read OrbitLineMode for {bodyCelestial.Id}: {ex.Message}");
        return null;
    }
}
```

```csharp
// src/CdmAttachInfoBodyJsonData.cs L107-L138
bodyJsonData.OrbitLineMode = TryGetOrbitLineMode(bodyCelestial);
// Mean radius in kilometers
bodyJsonData.RadiusKm = (float)(bodyCelestial.MeanRadius / 1000f);
// Mass values
bodyJsonData.Mass = bodyCelestial.Mass;


string massWithSIPrefix = FormatMassWithUnit(bodyCelestial.Mass);
double earthMasses = bodyCelestial.Mass / 5.972168e24;
double lunarMasses = bodyCelestial.Mass / 7.342e22;

// Depending on the mass of the body, display different mass text formats.  If the body mass falls within certain ranges show it in relation to Earth and/or Luna masses.
if (earthMasses > 0.1) { bodyJsonData.MassText = new ImString($"Mass: {bodyCelestial.Mass:E2} Kg / ({massWithSIPrefix}) / ({earthMasses:F3} Earths)"); }
else if (earthMasses > 0.02) { bodyJsonData.MassText = new ImString($"Mass: {bodyCelestial.Mass:E2} Kg / ({massWithSIPrefix}) / ({earthMasses:F3} Earths) / ({lunarMasses:F3} Lunas)"); }
else if (lunarMasses > 0.1) { bodyJsonData.MassText = new ImString($"Mass: {bodyCelestial.Mass:E2} Kg / ({massWithSIPrefix}) / ({lunarMasses:F3} Lunas)"); }
else if (lunarMasses > 0.001) { bodyJsonData.MassText = new ImString($"Mass: {bodyCelestial.Mass:E2} Kg / ({massWithSIPrefix}) / ({lunarMasses:F3} Lunas)"); }
else { bodyJsonData.MassText = new ImString($"Mass: {bodyCelestial.Mass:E2} Kg / ({massWithSIPrefix})"); }

// Gravity values
// Calculates gravity using the formula: g = G * M / R^2 and then what the surface gravity would be.
// where G is the gravitational constant (6.67430 × 10^-11 m^3 kg^-1 s^-2), M is the mass in kg, R is the radius in meters.
double gravity = 6.67430e-11 * bodyCelestial.Mass / (bodyCelestial._meanRadius * bodyCelestial._meanRadius);
// if the gravity just found is less than 0.001 m/s², display it as up to six decimal places.
if (gravity < 0.001) { bodyJsonData.GravityText = new ImString($"Gravity (Surface): {gravity:F6} m/s²");}
else { bodyJsonData.GravityText = new ImString($"Gravity (Surface): {gravity:F3} m/s²"); }

// Escape velocity
// calculates the escape velocity using the formula: v = sqrt(2 * G * M / R)
var escapeVelocity = Math.Sqrt(2 * 6.67430e-11 * bodyCelestial.Mass / bodyCelestial._meanRadius);
// If the escape velocity is less than 1000 m/s, display it in m/s with one decimal place, otherwise display in km/s with three decimal places.
if (escapeVelocity < 1000) { bodyJsonData.EscapeVelocityText = new ImString($"Escape Velocity: {escapeVelocity:F1} m/s"); }
else { bodyJsonData.EscapeVelocityText = new ImString($"Escape Velocity: {escapeVelocity / 1000f:F3} km/s"); }
```

```csharp
// src/CdmAttachInfoBodyJsonData.cs L186-L201
// If a body is not a child of the sun, it is a satellite of another body, so we need to get the inclination relative to its parent body's orbital plane.
// We need to calculate the relative inclination by subtracting the parent's axial tilt from the body's inclination.
if (bodyCelestial.Parent is Celestial parentCelestial)
{
    double parentTiltDeg = bodyCelestial.GetOrb2Cci().ToXyzRadians().X * (180.0 / Math.PI);

    double relativeInclination = inclinationDeg - parentTiltDeg;
    // The solar ecliptic inclination we can get from the Inclination value which is inclination with respect to the parent body's equatorial plane - plus the parent's tilt.
    // So to get the inclination relative to the solar ecliptic, we add the parent's tilt to the body's inclination to the parent.
    double solarEclipticInclination = Math.Abs(inclinationDeg - parentCelestial.BodyTemplate.Rotation.Tilt.ToDegrees());

    string parentId = GetBodyDisplayName(parentCelestial);
    bodyJsonData.InclinationText = new ImString($"Inclination: {relativeInclination:F2}° ( Relative to {parentId} equator )\nInclination: {solarEclipticInclination:F2}° ( Relative to {worldsunId} plane )");
}
else
{ bodyJsonData.InclinationText = new ImString($"Inclination: {inclinationDeg:F2}°"); }
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Celestial.MeanRadius`, `_meanRadius`, `Mass`, `SphereOfInfluence`, `BodyTemplate` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L27-L89) | Size, mass, sphere of influence, templates |
| `Celestial.SemiMinorAxis`, `SemiMajorAxis`, `Inclination`, `Eccentricity`, `OrbitalSpeed` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L97-L137) | Orbit elements |
| `Celestial.GetOrb2Cci`, `GetCci2Orb` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L507-L516) | Tilt and inclination of moons |
| `RotationTemplate.Tilt`, `SiderealPeriod`, `IsTidallyLocked`, `IsRetrograde` | [current/decomp/KSA/RotationTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RotationTemplate.cs#L10-L26) | Rotation facts |
| `AstronomicalTemplate.AtmosphereReference`, `AtmosphereReference.Physical` | [current/decomp/KSA/AtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereReference.cs#L10-L11) | Atmosphere presence |
| `PhysicalAtmosphereReference.Height`, `SeaLevelPressure` | [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L12-L23) | Atmosphere height and pressure |
| `Astronomical.OrbitLineMode` | [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L98) | Read by reflection |

## Pitfalls and version notes

- **Computed once per session.** The flag is never reset, so a body added or changed after the first
  frame (another system loaded later) keeps stale or missing strings.
- **Shared objects.** The same `CompendiumData` can sit under several aliased keys; the computed fields
  are written into that shared instance, so the last matching body wins if two bodies alias one entry.
- **Unit labels.** `FormatMassWithUnit` divides kilograms by powers of ten but labels the results as if
  they were grams (a yottagram is 1e21 kg, yet `kg / 1e24` is labelled "Yg"), and it uses "Yg" for both
  the 1e30 and 1e24 branches and "Zg" for both 1e27 and 1e21 ([src/CdmHelpers.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmHelpers.cs#L767-L796)). The
  plain kilogram figure printed next to it is correct.
- **Reflection is not needed in this build.** `OrbitLineMode` is public; the reflective read only guards
  against a future rename.

## See also

- [Cross-mod JSON catalog](cross-mod-json-catalog.md)
- [Atmosphere density and pressure graphs](atmosphere-profile-graph.md)
- [Celestial compendium window](celestial-info-window.md)
- [One-shot attach on the first UI frame with a universe](../../../howto/celestials/systems-and-stars/cache-and-key-body-data-per-loaded-system/cache-and-key-body-data-per-loaded-system-3.md) - how-to article demonstrating this feature
- [Prefix-keyed JSON files discovered in every installed mod folder](../../../howto/celestials/systems-and-stars/cache-and-key-body-data-per-loaded-system/cache-and-key-body-data-per-loaded-system-5.md) - how-to article demonstrating this feature
- [Sample the live density and pressure model by altitude](../../../howto/celestials/systems-and-stars/read-body-properties-and-orbit-elements/read-body-properties-and-orbit-elements-3.md) - how-to article demonstrating this feature
- [Template and transform reads for tilt, periods and inclination](../../../howto/celestials/systems-and-stars/read-body-properties-and-orbit-elements/read-body-properties-and-orbit-elements-4.md) - how-to article demonstrating this feature

[^cmp-compendiummain]: Compendium: src/CompendiumMain.cs
[^cmp-cdmattachinfobodyjsondata]: Compendium: src/CdmAttachInfoBodyJsonData.cs
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^ksa-rotationtemplate]: KSA.RotationTemplate (decompiled, build 2026.10.10.5554)
[^ksa-astronomicaltemplate]: KSA.AstronomicalTemplate (decompiled, build 2026.10.10.5554)
[^ksa-atmospherereference]: KSA.AtmosphereReference (decompiled, build 2026.10.10.5554)
[^ksa-physicalatmospherereference]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
[^ksa-astronomical]: KSA.Astronomical (decompiled, build 2026.10.10.5554)
[^ksa-barycenter]: KSA.Barycenter (decompiled, build 2026.10.10.5554)
[^ksa-terrestrialbody]: KSA.TerrestrialBody (decompiled, build 2026.10.10.5554)
