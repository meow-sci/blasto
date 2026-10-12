---
type: How-To
title: Template and transform reads for tilt, periods and inclination
description: Read axial tilt, sidereal period, tidal lock, retrograde flag and orbit inclination from the body template, the orbit and the orbital-plane transform, with the corrections the Compendium mod applies for moons
category: celestials
group: systems-and-stars
topic: read-body-properties-and-orbit-elements
approach: 4
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/compendium/features/derived-body-properties]
techniques: [reflection]
hooks: [starmap StarMapAfterGui, reflection KSA.Astronomical.OrbitLineMode]
lifecycle: [frame:draw-ui]
game_types: [KSA.Celestial, KSA.Orbit, KSA.RotationTemplate, KSA.IParentBody, KSA.CelestialTemplate]
tags: [tilt, inclination, sidereal-period, tidal-lock, orbit-elements, moons]
---
Outcome: the number a player expects to see for a body's axial tilt, orbital and sidereal period, tidal lock and inclination, computed on the first UI frame from `Celestial`, `Orbit` and template data. Demonstrated by Compendium ([Derived body properties](../../../../mods/compendium/features/derived-body-properties.md)). Sub-step of reading body data: derived properties of the orbit and rotation. The corrections below are quoted as the mod writes them; verify them against your own expectations before relying on them.

## Background: how the game does it

Rotation data is on the template: `CelestialTemplate.Rotation` is a `RotationTemplate` with `Tilt`, `Azimuth`, `SiderealPeriod`, `IsTidallyLocked`, `IsRetrograde` and `DefinitionFrame` ([current/decomp/KSA/RotationTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RotationTemplate.cs#L7-L25), [current/decomp/KSA/CelestialTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialTemplate.cs#L16-L17)). `Celestial` exposes the orbit's `Inclination`, `Eccentricity`, `SemiMajorAxis`, `SemiMinorAxis` and `Period` ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L97-L123)), and `OrbitalSpeed`, the length of the CCI velocity ([current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L130-L137)). `Orbit.Inclination` is measured against the parent's equator (CCI +Z), not the ecliptic. The orientation transforms `GetOrb2Cci()` and `GetCci2Orb()` are on `IParentBody` ([current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L47-L49)); `GetCci2Orb().Inverse()` carries the body's axis tilt relative to its orbital plane.

## Lifecycle and timing

The attach runs inside `[StarMapAfterGui]` (`frame:draw-ui`) on the first frame `Universe.WorldSun` is set, guarded by a static flag. Compendium's JSON was loaded much earlier, at `startup:immediate-load`, when no celestial existed; this is the first point where both exist. Templates are load-time data, mass, radius, orbit elements and SOI are fixed for celestials on Keplerian orbits, so only the orbital speed is read live. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Orbital period** from `Orbit.Period` (seconds), shown in years above two years, days above two days, hours otherwise:

```csharp
// src/CdmAttachInfoBodyJsonData.cs L140-L158
// Orbital period
// Figures out the timescale we want to use for displaying the orbital period.  If it's more than 2 years, use years.  If it's more than 2 days, use days.  Otherwise use hours.
double orbitalPeriodSeconds = bodyCelestial.Orbit.Period;

if (orbitalPeriodSeconds >= 63072000) // More than 2 years
{
    double orbitalVal = orbitalPeriodSeconds / 31536000;
    bodyJsonData.OrbitalPeriod = orbitalVal.ToString("F2") + " years";
}
else if (orbitalPeriodSeconds >= 172800) // More than 2 days
{
    double orbitalVal = orbitalPeriodSeconds / 86400;
    bodyJsonData.OrbitalPeriod = orbitalVal.ToString("F2") + " days";
}
else // Use hours
{
    double orbitalVal = orbitalPeriodSeconds / 3600;
    bodyJsonData.OrbitalPeriod = orbitalVal.ToString("F2") + " hours";
}
```

2. **Axial tilt.** For a body whose parent is not a `Celestial` (orbiting a star) take `BodyTemplate.Rotation.Tilt.ToDegrees()`; for a moon take the X angle of `GetCci2Orb().Inverse()`, which is relative to the parent:

```csharp
// src/CdmAttachInfoBodyJsonData.cs L160-L174
// Axial tilt
// Gets the axial tilt values depending on whether the selected celestial's parent is the sun or another body.
if (bodyCelestial.Parent is not Celestial)
{ 
    //thisTilt = selectedCelestial.GetCce2Cci().ToXyzRadians().X * (180.0 / Math.PI);
    double thisTilt = bodyCelestial.BodyTemplate.Rotation.Tilt.ToDegrees();
    ImString thisTiltText = new ImString($"{thisTilt:F2}°");
    bodyJsonData.ThisTiltText = new ImString($"Axial Tilt: {thisTiltText}");
}
else
{
    double thisTilt = bodyCelestial.GetCci2Orb().Inverse().ToXyzRadians().X * (180.0 / Math.PI);
    //string parentName = bodyCelestial.Parent.Id;
    ImString thisTiltText = new ImString($"{thisTilt:F2}° ( Relative to {(bodyCelestial.Parent is Astronomical tiltParent ? GetBodyDisplayName(tiltParent) : bodyCelestial.Parent.Id)} )");
    bodyJsonData.ThisTiltText = new ImString($"Axial Tilt: {thisTiltText}");
```

3. **Inclination.** `Celestial.Inclination` is relative to the parent's equator; for moons the mod subtracts the X angle of `GetOrb2Cci()` and also derives a figure relative to the star's plane from the parent's template tilt:

```csharp
// src/CdmAttachInfoBodyJsonData.cs L179-L201
// Inclination 

// First gets the inclination in degrees from radians.
double inclinationDeg = bodyCelestial.Inclination * (180.0 / Math.PI);
// Gets a string for the WorldSun's ID for display purposes.
string worldsunId = GetHostStarId(bodyCelestial);

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

4. **Sidereal period and tidal lock** from `Rotation.SiderealPeriod`, `IsTidallyLocked` and `IsRetrograde` (a zero period is shown as tidally locked):

```csharp
// src/CdmAttachInfoBodyJsonData.cs L203-L239
// Sidereal period and tidal locking
// Gets the sidereal period in hours or days depending on length, and checks for tidal locking
// For now uses .ToNearest() to return back the closest range to report the stat as..
string siderealPeriod;
// ImString siderealPeriodText;
if (bodyCelestial.BodyTemplate.Rotation.IsTidallyLocked.Value == true || bodyCelestial.BodyTemplate.Rotation.SiderealPeriod == 0)
{
    bodyJsonData.TidalLockText = new ImString("Tidally locked rotation");
}
else
{
    //siderealPeriod = bodyCelestial.BodyTemplate.Rotation.SiderealPeriod.ToNearest();
    siderealPeriod = bodyCelestial.BodyTemplate.Rotation.SiderealPeriod.GetSeconds().ToString();
    if (double.TryParse(siderealPeriod, out double siderealSeconds))
    {
        if (siderealSeconds >= 259200) // More than 3 days
        {
            double siderealVal = siderealSeconds / 86400;
            siderealPeriod = $"{siderealVal:F2} days";
        }
        else if (siderealSeconds >= 3600) // Use hours
        {
            double siderealVal = siderealSeconds / 3600;
            siderealPeriod = $"{siderealVal:F2} hours";
        }
    }

    if (siderealPeriod != null)
    {
        bodyJsonData.TidalLockText = new ImString("False");

        if (!bodyCelestial.BodyTemplate.Rotation.IsRetrograde)
            { bodyJsonData.SiderealPeriodText = new ImString($"Sidereal Period: {siderealPeriod:F2}"); }
        else
            { bodyJsonData.SiderealPeriodText = new ImString($"Sidereal Period: {siderealPeriod:F2} ( Retrograde )"); }
    }
}
```

5. **Read one fragile property by reflection inside a `try`** so a rename costs one field, not the page. `OrbitLineMode` is a public abstract property of `Astronomical` in build 5554 ([current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L98)), overridden per class:

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

6. **Read live values live.** The current orbital speed is not cached; the page reads `celestial.OrbitalSpeed` every frame.

## Complete example

| File | Role |
|---|---|
| [src/CdmAttachInfoBodyJsonData.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmAttachInfoBodyJsonData.cs#L81-L281) | Key-to-body matching and the derived fields |
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L73-L91) | Once-per-universe attach guard |

## Variations and gotchas

- **Inclination labels.** The decompiled code measures `Orbit.Inclination` against the parent's equator; a mod that labels it as ecliptic is wrong for moons (see [coordinate frames](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)).
- **Zero sidereal period** means no data, not infinitely slow rotation; treat it with `IsTidallyLocked`.
- **Moons need the parent's tilt** to put inclination on a common plane.
- **Run once.** Attach after the universe exists and never again; reload invalidates object references (see [cache and key per-body data](../cache-and-key-body-data-per-loaded-system/overview.md)).

## See also

- [Overview](overview.md); [approach 3](read-body-properties-and-orbit-elements-3.md)
- [Derived body properties](../../../../mods/compendium/features/derived-body-properties.md)
- [Read orbit elements and timing](../../../flight-and-orbits/flight-state/read-orbit-elements-and-timing/overview.md), [Understand KSA coordinate frames and epochs](../../../flight-and-orbits/flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md)
