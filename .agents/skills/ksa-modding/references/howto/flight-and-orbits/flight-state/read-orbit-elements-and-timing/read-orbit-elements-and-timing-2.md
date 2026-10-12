---
type: How-To
title: Read orbit and encounter data without leaking NaN or UniverseTime sentinels
description: Convert apsides, patch events and SOI encounters to finite numbers by gating on IsBound and IsSaturated, so an escape or non-intersecting orbit never produces a bogus timestamp
category: flight-and-orbits
group: flight-state
topic: read-orbit-elements-and-timing
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/vessel-flight-state-reads]
techniques: [immutable-snapshot]
hooks: [harmony-prefix KSA.GameViewport.OnFrame]
lifecycle: [frame:viewports]
game_types: [KSA.Orbit, KSA.Vehicle, KSA.UniverseTime, KSA.PatchedConic, KSA.Encounter]
tags: [orbit, sentinel, nan, is-bound, saturated, encounters, patch-event]
---

Outcome: orbit elements and countdowns that are always finite and mean what they say, including on escape trajectories.
gatOS applies the guards to every vessel it publishes.[Vessel flight-state reads](../../../../mods/gatos/features/vessel-flight-state-reads.md) Prerequisite: StarMap for a
main-thread hook; the guards are plain C#.

## Background: how the game does it

- `UniverseTime` stores nanoseconds in an `Int128`; `EndOfTime` and `BeginningOfTime` are `MaxValue` and `MinValue`,
  and `IsSaturated()` is true for either.[^ksa-universetime] [current/decomp/KSA/UniverseTime.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UniverseTime.cs#L36-L38) [current/decomp/KSA/UniverseTime.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UniverseTime.cs#L132-L145)
  `Seconds()` of `EndOfTime` is a finite ~1.7e29, which a finite check happily passes.
- `Vehicle` caches `NextPeriapsisTime`, `NextApoapsisTime` and `NextPatchEventTime` in `UpdatePerFrameData`, defaulting to
  `UniverseTime.EndOfTime`; the patch event is only set when the first patch ends in an escape or an encounter.[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2666-L2677)
- For unbound orbits `GetNextApoapsisTime` returns the query time and `Apoapsis` is `a(1+e)`: finite and negative.
  Neither is a sentinel, so `IsSaturated()` does not catch them. `Orbit.IsBound()` does.[^ksa-orbit] [current/decomp/KSA/Orbit.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Orbit.cs#L1858-L1861)

## Lifecycle and timing

Read on the main thread after `PrepareFrame` (gatOS uses a prefix on the main `GameViewport.OnFrame`, `frame:viewports`).
The values are those of the step applied this frame; the cached countdowns are refreshed in the same
apply. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Wrap every double** that may be non-finite in a `Finite` helper that returns 0 (gatOS: `Sanitize.Finite`).
2. **Convert radii to altitudes and gate apoapsis on `IsBound`:**

```csharp
// gatOS.SimFs/Telemetry/Sanitize.cs L20-L31
public static double RadiusToAltitude(double radiusMeters, double meanRadiusMeters)
    => double.IsFinite(radiusMeters) && double.IsFinite(meanRadiusMeters)
        ? radiusMeters - meanRadiusMeters
        : 0;

/// <summary>
///     The apoapsis altitude, or 0 when the orbit has no apoapsis (<paramref name="isBound"/>
///     false: hyperbolic/parabolic). Mirrors the gate KSA's own Universe Manifest adopted at
///     rev 5439 (<c>Orbit.IsBound()</c>) and the <c>time_to_ap</c> "0 when none" contract.
/// </summary>
public static double ApoapsisToAltitude(double apoapsisRadiusMeters, double meanRadiusMeters, bool isBound)
    => isBound ? RadiusToAltitude(apoapsisRadiusMeters, meanRadiusMeters) : 0;
```

3. **Convert event times with a saturation test.** Treat saturation as "no such event" and report 0, and clamp
   countdowns at 0:

```csharp
// gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs L341-L378
private static OrbitSnapshot? FullOrbit(Vehicle vehicle, IParentBody? parent, double utSeconds)
{
    if (vehicle.Orbit is not { } o || parent is null)
        return null;
    return new OrbitSnapshot(
        Sanitize.ApoapsisToAltitude(o.Apoapsis, parent.MeanRadius, o.IsBound()),
        Sanitize.RadiusToAltitude(o.Periapsis, parent.MeanRadius),
        Sanitize.Finite(o.Eccentricity),
        Sanitize.Finite(o.Inclination * RadToDeg),
        Sanitize.Finite(o.SemiMajorAxis),
        Sanitize.Finite(o.Period))
    {
        LanDeg = Sanitize.Finite(o.LongitudeOfAscendingNode * RadToDeg),
        ArgPeDeg = Sanitize.Finite(o.ArgumentOfPeriapsis * RadToDeg),
        TrueAnomalyDeg = Sanitize.Finite(o.StateVectors.TrueAnomaly.Degrees),
        TimeToApoapsis = TimeUntil(vehicle.NextApoapsisTime, utSeconds),
        TimeToPeriapsis = TimeUntil(vehicle.NextPeriapsisTime, utSeconds),
        NextPatchEventUt = UtOrZero(vehicle.NextPatchEventTime),
    };
}

/// <summary>
///     A <see cref="UniverseTime"/> as absolute UT seconds, or 0 when it carries the
///     "no such event" sentinel. KSA saturates missing times to
///     <c>UniverseTime.EndOfTime</c>/<c>BeginningOfTime</c> (Int128 Max/Min nanoseconds); unlike
///     the old <c>SimTime.PositiveInfinity</c> those convert to a finite ~1.7e29 seconds, which
///     <see cref="Sanitize.Finite"/> would happily pass through as a real timestamp.
/// </summary>
private static double UtOrZero(UniverseTime target)
    => target.IsSaturated() ? 0 : Sanitize.Finite(target.Seconds());

private static double TimeUntil(UniverseTime target, double utSeconds)
{
    if (target.IsSaturated())
        return 0; // no such event on this orbit (KSA's EndOfTime sentinel)
    var dt = Sanitize.Finite(target.Seconds()) - utSeconds;
    return dt > 0 ? dt : 0;
}
```

4. **Read encounters from the first patch** (`vehicle.Patch.Encounters`), mapping the `Encounter` (`Body`, `GameTime`,
   `ClosestDistance`) to plain data; empty means no SOI encounter, not "none planned":

```csharp
// gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs L908-L922
private static IReadOnlyList<EncounterSnapshot> SampleEncounters(Vehicle vehicle)
{
    var patch = vehicle.Patch;
    var encounters = patch.Encounters;
    if (encounters.Length == 0)
        return [];
    var result = new List<EncounterSnapshot>(encounters.Length);
    foreach (var e in encounters)
    {
        var body = (e.Body as Astronomical)?.Id ?? "?";
        result.Add(new EncounterSnapshot(body, Sanitize.Finite(e.GameTime.Seconds()),
            Sanitize.Finite(e.ClosestDistance)));
    }

    return result;
```

5. **Report angles consistently**: the elements are radians (`o.Inclination * RadToDeg` in the snapshot).

## Complete example

[gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs#L309-L378) (orbit elements, apsis times, guards) and
[gatOS.SimFs/Telemetry/Sanitize.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Telemetry/Sanitize.cs).

## Variations and gotchas

- **Apsis times for unbound orbits** are finite and wrong; gate on `IsBound()` before showing `NextApoapsisTime`.
- **Encounters are SOI encounters only**; they never include planned burns and are not the target gauge's closest-approach list.
- **Revision drift.** `Orbit.ApoapsisAltitude`/`PeriapsisAltitude` (rev 5439) are plain subtractions and still leak the
  hyperbolic value; closest-approach sampling changed again at 5438, so `closest_distance` and time can differ across builds.
- A countdown of 0 can mean both "now" and "never"; publish a separate boolean when callers need the difference.

## See also

- [Overview](overview.md); [CCI readouts](read-orbit-elements-and-timing-1.md); [Mu and cache](read-orbit-elements-and-timing-3.md)
- [Vessel flight-state reads](../../../../mods/gatos/features/vessel-flight-state-reads.md)
- [Read a vehicle's position, velocity, attitude, orbit and flight instruments](../read-vehicle-flight-state/overview.md)

[^ksa-universetime]: KSA.UniverseTime (decompiled, build 2026.10.10.5554)
[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
