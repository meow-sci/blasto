---
type: How-To
title: Take a plain flight-state snapshot in StarMapAfterOnFrame and draw it next frame
description: Refill one snapshot object per frame from the controlled vehicle after the game has finished the frame, and add thrust, thrust-to-weight, dynamic pressure and downrange to the basic reads
category: flight-and-orbits
group: flight-state
topic: read-vehicle-flight-state
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksa-public-telemetry/features/live-flight-telemetry-sampling]
techniques: [starmap-lifecycle, snapshot-copy]
hooks: [starmap StarMapAfterOnFrame]
lifecycle: [frame:after-on-frame]
game_types:
  - KSA.Vehicle
  - KSA.Program
  - KSA.ReadOnlyPhysicsStates
  - KSA.PhysicsEnvironment
  - KSA.EngineController
  - KSA.SituationEx
tags: [snapshot, telemetry, thrust, dynamic-pressure, downrange, g-load, vertical-speed]
---

Outcome: speed, altitude, g-load, vertical speed, thrust, thrust-to-weight, dynamic pressure, apsides and
downrange for the controlled vehicle, sampled once per frame into a plain object that UI code reads.
KSA-Public-Telemetry demonstrates it.[Live flight telemetry sampling](../../../../mods/ksa-public-telemetry/features/live-flight-telemetry-sampling.md) Prerequisite: StarMap only; no Harmony.

## Background: how the game does it

All members come from the main-thread `Vehicle` that `PrepareFrame` refreshed (see the
[overview](overview.md)). Two groups are not plain properties:

- Environment values and the air-relative velocity live in `ReadOnlyPhysicsStates`, returned by
  `Vehicle.GetPhysicsStates()`: `Environment.AtmosphericPressure`, `AtmosphericDensity`, `GravitationBub`,
  `ComputeAirVelocityBody()`, `GetStatesCci(...)` and `GetStatesCcf(...)`.[^ksa-readonlyphysicsstates]
  [current/decomp/KSA/ReadOnlyPhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ReadOnlyPhysicsStates.cs#L15-L70)
- Engine thrust is not stored; `EngineController.ComputeActivePerformance(in state, centerOfMassAsmb,
  ambientPressure, throttle)` returns a `RocketControllerData` whose `ThrustMax` is a vector.[^ksa-enginecontroller]
  [current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L68-L75)

## Lifecycle and timing

`[StarMapAfterOnFrame]` is a Harmony postfix on `Program.OnFrame`, so it runs after the whole frame including
render, and also on frames where `PrepareFrame` returns `Exit` (nothing was drawn then). The main-thread
`Vehicle` fields are stable here: the workers integrate their own copies, so reads need no lock and match
what the game's gauges showed this frame. The cost is one frame of latency: the UI of frame N+1 shows the
sample taken at the end of frame N. Sampling in one place keeps the overlay and every other window identical.
See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Attach the hook.** One attribute on a mod method:

```csharp
// TelemetryOverlayMod.cs L65-L83
[StarMapAfterOnFrame]
public void OnAfterFrame(double currentPlayerTime, double dtPlayer)
{
    if (_disabled || _config is null)
    {
        return;
    }

    try
    {
        TelemetrySampler.Sample(_snapshot, _config, dtPlayer);
        HiddenUiPatch.RecordDelta(dtPlayer);
        Succeeded();
    }
    catch (Exception ex)
    {
        Fail("sampling", ex);
    }
}
```

2. **Pick the vehicle** with `Program.ControlledVehicle` and reject null or `IsDisposed`.
3. **Read kinematics, atmosphere and dynamic pressure** into the snapshot. Note the air-relative
   speed for q, and the apsis radii minus `MeanRadius`:

```csharp
// Telemetry/TelemetrySampler.cs L308-L345
private static void SampleKinematics(Vehicle vehicle, TelemetrySnapshot snapshot)
{
    snapshot.SurfaceSpeed = vehicle.GetSurfaceSpeed();
    snapshot.OrbitalSpeed = vehicle.GetInertialSpeed();
    double meanRadius = vehicle.Orbit?.Parent?.MeanRadius ?? 0.0;
    snapshot.Altitude = vehicle.GetBarometricAltitude();
    snapshot.RadarAltitude = vehicle.GetRadarAltitude();
    snapshot.Apoapsis = vehicle.Apoapsis - meanRadius;
    snapshot.Periapsis = vehicle.Periapsis - meanRadius;
    snapshot.GLoad = vehicle.AccelerationBody.Length() / StandardGravity;

    snapshot.TotalMass = vehicle.TotalMass;
    snapshot.PropellantMass = vehicle.PropellantMass;

    ReadOnlyPhysicsStates physics = vehicle.GetPhysicsStates();

    physics.GetStatesCci(out double3 positionCci, out double3 velocityCci, out _);
    double radius = positionCci.Length();
    snapshot.VerticalSpeed = radius > 0.0
        ? double3.Dot(velocityCci, positionCci / radius)
        : 0.0;

    float ambientPressure = physics.Environment.AtmosphericPressure;
    float ambientDensity = physics.Environment.AtmosphericDensity;
    float airspeed = physics.ComputeAirVelocityBody().Length();
    double gravity = physics.Environment.GravitationBub.Length();

    snapshot.AmbientPressure = ambientPressure;
    snapshot.AboveAtmosphere = ambientPressure <= 0f;
    snapshot.AmbientDensity = ambientDensity;
    snapshot.LocalGravity = (float)gravity;
    snapshot.DynamicPressure = 0.5f * ambientDensity * airspeed * airspeed;

    if (snapshot.DynamicPressure > snapshot.MaxDynamicPressure)
    {
        snapshot.MaxDynamicPressure = snapshot.DynamicPressure;
    }
}
```

4. **Sum thrust from the engine controllers** that are active with throttle above zero, then divide by weight
   for T/W:

```csharp
// Telemetry/TelemetrySampler.cs L500-L505
if (controllerActive && controllerThrottle > 0f)
{
    totalThrust += controller
        .ComputeActivePerformance(in controllerState, centreOfMass, ambientPressure, controllerThrottle)
        .ThrustMax.Length();
}
```

```csharp
// Telemetry/TelemetrySampler.cs L96-L98

double weight = snapshot.TotalMass * snapshot.LocalGravity;
snapshot.ThrustToWeight = weight > 0.0 ? (float)(snapshot.Thrust / weight) : 0f;
```

5. **Compute downrange.** At liftoff store the CCF (body-fixed) position and the parent id; later frames take
   the great-circle distance `MeanRadius * acos(dot(from, to))` while the vehicle orbits the same body:

```csharp
// Telemetry/TelemetrySampler.cs L199-L234
private static LaunchOrigin OriginOf(Vehicle vehicle)
{
    string body = vehicle.Orbit?.Parent?.Id ?? string.Empty;
    vehicle.GetPhysicsStates().GetStatesCcf(out double3 positionCcf, out _, out _);

    return new LaunchOrigin(body, positionCcf.X, positionCcf.Y, positionCcf.Z);
}
private static double DownrangeFrom(Vehicle vehicle, Int128 launchKey)
{
    if (!_missions.TryGetOrigin(launchKey, out LaunchOrigin origin))
    {
        return double.NaN;
    }

    IParentBody? parent = vehicle.Orbit?.Parent;

    if (parent is null || !string.Equals(parent.Id, origin.Body, StringComparison.Ordinal))
    {
        return double.NaN;
    }

    double3 from = new(origin.X, origin.Y, origin.Z);
    vehicle.GetPhysicsStates().GetStatesCcf(out double3 to, out _, out _);

    double fromLength = from.Length();
    double toLength = to.Length();

    if (fromLength <= 0.0 || toLength <= 0.0)
    {
        return double.NaN;
    }

    double cosine = Math.Clamp(double3.Dot(from / fromLength, to / toLength), -1.0, 1.0);

    return parent.MeanRadius * Math.Acos(cosine);
}
```

6. **Format on the UI side** (km/h is `value * 3.6`, altitude in km; "Auto" speed reference switches to orbital
   once ambient pressure is zero) so the sampler keeps SI values only.

## Complete example

[TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L65-L83) (entry), [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L40-L138) (pipeline) and
[Telemetry/TelemetrySnapshot.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySnapshot.cs#L32-L103) (the object the UI reads).

## Variations and gotchas

- **Dynamic pressure has two definitions.** The mod uses `0.5 * density * |ComputeAirVelocityBody()|^2` from the
  physics environment (wind-aware); the game's `PhysicalAtmosphereReference.GetDynamicPressure(vehicle)` uses
  density at altitude and `GetSurfaceSpeed()`. They agree without wind.
- **g-load** is `AccelerationBody.Length() / 9.80665`. `AccelerationBody` already excludes gravity, so do
  not subtract local g (see the [postfix article](read-vehicle-flight-state-3.md)).
- `Vehicle.Apoapsis`/`Periapsis` are radii; subtract `Orbit.Parent.MeanRadius`. Unbound orbits need the guards
  in the [snapshot article](read-vehicle-flight-state-1.md).
- A snapshot taken on an `Exit` frame is drawn next frame; if the HUD is hidden the readouts do not draw.

## See also

- [Overview](overview.md); [immutable snapshot](read-vehicle-flight-state-1.md); [postfix read](read-vehicle-flight-state-3.md)
- [Live flight telemetry sampling](../../../../mods/ksa-public-telemetry/features/live-flight-telemetry-sampling.md)
- [Show mission elapsed time from actual liftoff and pick the right clock under time warp](../compute-mission-elapsed-time-and-detect-liftoff/overview.md) for the liftoff detection this sampler relies on

[^ksa-readonlyphysicsstates]: KSA.ReadOnlyPhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-enginecontroller]: KSA.EngineController (decompiled, build 2026.10.10.5554)
