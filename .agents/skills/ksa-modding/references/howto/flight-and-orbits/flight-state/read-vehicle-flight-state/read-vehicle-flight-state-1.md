---
type: How-To
title: Build an immutable flight-state snapshot in a main-viewport OnFrame prefix
description: Copy position, velocity, attitude, orbit, navball, environment and SOI encounters out of Vehicle into a record that other threads can read, scrubbing NaN and the UniverseTime sentinels
category: flight-and-orbits
group: flight-state
topic: read-vehicle-flight-state
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/gatos/features/vessel-flight-state-reads]
techniques: [immutable-snapshot, starmap-lifecycle, harmony-prefix]
hooks: [harmony-prefix KSA.GameViewport.OnFrame, starmap StarMapBeforeGui]
lifecycle: [frame:viewports, frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.Orbit
  - KSA.NavBallData
  - KSA.PatchedConic
  - KSA.Encounter
  - KSA.UniverseTime
  - KSA.PhysicsEnvironment
  - KSA.PhysicalAtmosphereReference
tags: [snapshot, telemetry, nan, sentinel, navball, encounters, environment]
---

Outcome: a per-vehicle record built on the main thread once per sample and then read by anything
(HTTP, MQTT, a UI, another thread) without touching the game. gatOS does this for every vessel;[Vessel flight-state reads](../../../../mods/gatos/features/vessel-flight-state-reads.md)
this article shows the guarded reads that make the record safe. Prerequisite: StarMap and Harmony
(only the sampler's hook point needs them; the reader is plain C#).

## Background: how the game does it

The reads in [the overview](overview.md) are all cheap property or method calls on state that
`Universe.ApplyVehicleSolvers` has already written, so a reader is just a list of calls. Three
engine behaviours need guarding:

- **NaN and infinity.** Elements of degenerate orbits and unset navball fields can be non-finite.
- **Hyperbolic orbits.** KSA stores `a(1+e)` for the apoapsis of an unbound orbit, a finite large negative
  radius, so "is it finite" does not detect it; test `Orbit.IsBound()`.
- **`UniverseTime` sentinels.** A missing apsis or patch event is `UniverseTime.EndOfTime`, which converts
  to a finite ~1.7e29 seconds. Test `IsSaturated()` before converting.

`Vehicle.Patch.Encounters` holds SOI encounters only; it never includes planned burns.[^ksa-encounter]
[^ksa-universetime] [current/decomp/KSA/UniverseTime.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UniverseTime.cs#L142-L145)

## Lifecycle and timing

gatOS runs its sampler from a Harmony prefix on the main `GameViewport.OnFrame` (`frame:viewports`), or
from `StarMapBeforeGui` (`frame:draw-ui`) when that patch is off; the state it sees is the step applied in
this frame's `PrepareFrame`, one step behind the step that is computing. Publishing is a single reference
write to the new snapshot, so transport threads see the old or the new one, never a torn one. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Read position, attitude and lat/lon** in one `ReadBasics` pass; the frames are explained in
   [Understand KSA coordinate frames and epochs (ECL, CCE, CCI, CCF, ego, body)](../understand-ksa-coordinate-frames-and-epochs/overview.md) (article 1 shows the code).
2. **Read the orbit through finite-checking helpers.** Apsis radii become altitudes by subtracting
   `Parent.MeanRadius`; apoapsis is 0 when the orbit is not bound; times use the saturation test:

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

3. **Gate apoapsis on `IsBound`**, not on finiteness:

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

4. **Copy `NavBallData` immediately.** It is a `ref readonly` field; read the members out in one place:

```csharp
// gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs L397-L404
private static NavballSnapshot SampleNavball(Vehicle vehicle)
{
    ref readonly var nb = ref vehicle.NavBallData;
    return new NavballSnapshot(
        nb.AttitudeAngles.X, nb.AttitudeAngles.Y, nb.AttitudeAngles.Z,
        Sanitize.Finite(nb.ThrustWeightRatio), Sanitize.Finite(nb.DeltaV),
        EnumText.Of(nb.Frame), Sanitize.Finite(nb.Speed));
}
```

5. **Read the environment and g-load.** Use `PhysicalAtmosphereReference.GetDynamicPressure(vehicle)`, the
   game's own helper (density at altitude times surface speed squared over two), and divide
   `AccelerationBody.Length()` by standard gravity:

```csharp
// gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs L416-L430
private static EnvironmentSnapshot SampleEnvironment(Vehicle vehicle)
{
    ref readonly var env = ref vehicle.PhysicsEnvironment;
    var accel = vehicle.AccelerationBody;
    var gForce = Sanitize.Finite(accel.Length() / StandardGravity);
    return new EnvironmentSnapshot(
        Sanitize.Finite(env.AtmosphericPressure),
        Sanitize.Finite(env.AtmosphericDensity),
        Sanitize.Finite(PhysicalAtmosphereReference.GetDynamicPressure(vehicle)),
        Sanitize.Finite(env.OceanDensity),
        Sanitize.Finite(env.TerrainRadius),
        Vec(accel),
        Vec(vehicle.AngularAccelerationBody),
        gForce);
}
```

   `GetDynamicPressure` returns 0 above the atmosphere (no `AtmosphereReference`).[^ksa-physicalatmospherereference] [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L66-L78)
6. **Read SOI encounters** from the first patch, mapping each `Encounter` to plain data:

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

7. **Fall back to a core record if a detail read throws.** gatOS logs once and keeps the vessel instead of
   dropping it.

## Complete example

[gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs#L41-L135) (`ReadBasics`, core record) and
[gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs#L179-L259) (`BuildFull`); the sampler that calls
them is described in [Telemetry sampler and snapshot store](../../../../mods/gatos/features/telemetry-sampler-snapshot.md).

## Variations and gotchas

- `NavBallData.DeltaV` became the propellant-aware delta-v of the active staging sequence at revision
  5114 and `ThrustWeightRatio` atmosphere-corrected in the same revision; both members exist in 5554.
- `Orbit.ApoapsisAltitude` and `PeriapsisAltitude` exist since revision 5439 but are plain radius minus
  `MeanRadius`; they still leak the hyperbolic value, so keep the `IsBound` gate.
- KSA has no vessel display name: `Vehicle.Id` serves as id and name. EVA kittens (`KittenEva : Vehicle`) and
  debris appear in the same enumeration.
- Reads are not valid from a worker thread; take the snapshot on the main thread and hand the record over.

## See also

- [Overview](overview.md); [after-frame snapshot](read-vehicle-flight-state-2.md); [postfix instrument read](read-vehicle-flight-state-3.md)
- [Read orbit elements, apsides and event countdowns safely](../read-orbit-elements-and-timing/overview.md) for sentinel-safe elements in more detail
- [Vessel flight-state reads](../../../../mods/gatos/features/vessel-flight-state-reads.md)

[^ksa-encounter]: KSA.Encounter (decompiled, build 2026.10.10.5554)
[^ksa-universetime]: KSA.UniverseTime (decompiled, build 2026.10.10.5554)
[^ksa-physicalatmospherereference]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
