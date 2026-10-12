---
type: Mod Feature
title: Live flight telemetry sampling
description: Copies speed, altitude, g-load, dynamic pressure, apsides, downrange, thrust and propellant fraction of the controlled vehicle into a plain snapshot object once per frame from StarMapAfterOnFrame, for the UI to draw on the next frame
mod: ksa-public-telemetry
category: flight-and-orbits
techniques: [starmap-lifecycle, snapshot-copy]
hooks: [starmap StarMapAfterOnFrame]
lifecycle: [frame:after-on-frame]
game_types:
  - KSA.Program
  - KSA.Vehicle
  - KSA.ReadOnlyPhysicsStates
  - KSA.PhysicsEnvironment
  - KSA.SituationEx
  - KSA.EngineController
  - KSA.Universe
tags: [telemetry, vehicle, kinematics, snapshot, starmap]
---

The readouts (speed, altitude, g-force, vertical speed, thrust, T/W, dynamic pressure, apoapsis,
periapsis, downrange) all come from one `TelemetrySnapshot` that the mod refills once per frame from
the controlled vehicle's main-thread state. Panel code never touches `Vehicle`; it only reads the
snapshot. The same sampling pass also feeds the engine diagram, the mission clock, the event detector
and the planned-burn markers, which have their own pages.

## How it works

1. **Entry.** `[StarMapAfterOnFrame] OnAfterFrame(currentPlayerTime, dtPlayer)` calls
   `TelemetrySampler.Sample(_snapshot, _config, dtPlayer)` ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L65-L83)).
2. **Pick the vehicle.** `Program.ControlledVehicle`, rejected when null or `IsDisposed`; the tracking
   decision (sample, hold the last frame, or clear) is described in
   [Freeze on signal loss](freeze-on-signal-loss.md) ([Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L40-L91)).
3. **Kinematics** ([Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L308-L345)):

| Snapshot field | Game member | Notes |
|---|---|---|
| `SurfaceSpeed` | `Vehicle.GetSurfaceSpeed()` | CCI velocity minus the parent's rotation at the vehicle position |
| `OrbitalSpeed` | `Vehicle.GetInertialSpeed()` | Length of `Orbit.StateVectors.VelocityCci` |
| `Altitude` | `Vehicle.GetBarometricAltitude()` | Radius minus `Orbit.Parent.MeanRadius` |
| `RadarAltitude` | `Vehicle.GetRadarAltitude()` | Terrain-relative; used when "Terrain-relative altitude" is on |
| `Apoapsis`, `Periapsis` | `Vehicle.Apoapsis` / `Periapsis` minus `Orbit.Parent.MeanRadius` | The game returns radii |
| `GLoad` | `Vehicle.AccelerationBody.Length() / 9.80665` | From the solver's `KinematicMeasurements` |
| `VerticalSpeed` | `GetPhysicsStates().GetStatesCci(...)` | Velocity projected on the radial unit vector |
| `AmbientPressure`, `AmbientDensity`, `LocalGravity` | `ReadOnlyPhysicsStates.Environment` | `AtmosphericPressure`, `AtmosphericDensity`, `GravitationBub.Length()` |
| `DynamicPressure` | `0.5 * density * ComputeAirVelocityBody().Length()^2` | Air-relative speed; max tracked per vehicle |
| `TotalMass`, `PropellantMass` | `Vehicle.TotalMass`, `Vehicle.PropellantMass` | Read for T/W |
| `OnRails`, `HasSurfaceContact` | `Vehicle.Situation.IsOnRails()` / `HasAnyContact()` | `SituationEx` extension methods |

4. **Thrust.** For each active engine controller with throttle above zero, the sampler sums
   `EngineController.ComputeActivePerformance(in state, centreOfMass, ambientPressure, throttle).ThrustMax.Length()`,
   the same per-controller performance call the game uses, at the vehicle's current ambient pressure
   ([Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L500-L508), [current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L68-L75)).[^ksa-enginecontroller]
   T/W is that thrust over `TotalMass * LocalGravity` ([Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L97-L98)).
5. **Downrange.** At detected liftoff the sampler stores the launch point as a CCF (body-fixed) position
   and the parent body id; later frames compute the great-circle distance
   `MeanRadius * acos(dot(from, to))` while the vehicle orbits the same body
   ([Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L199-L234)).
6. **Units.** The UI formats speed as km/h (`value * 3.6`), altitude in km, and picks surface or orbital
   speed per the "Speed reference" setting, with an Auto mode that switches to orbital once ambient
   pressure is zero ([Rendering/ReadoutPanel.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/ReadoutPanel.cs#L154-L206)).

## Lifecycle and timing

- **`frame:after-on-frame`.** StarMap invokes `[StarMapAfterOnFrame]` from a Harmony postfix on
  `Program.OnFrame` ([StarMap.Core/Patches/ProgramPatcher.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ProgramPatcher.cs#L38-L48)), so it runs after the whole
  frame, render included.[^starmap-programpatcher] A Harmony postfix runs on every return path, so it
  also fires on the frames where `PrepareFrame` returns `Exit` (window close, font rebuild) and no UI
  or render ran ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2243-L2248)); the sample taken then is simply
  drawn on the next frame that reaches the UI phase.
- **What state that is.** Earlier in the frame `PrepareFrame` applied the finished vehicle step to every
  `Vehicle` (`Universe.ApplyVehicleSolvers`; for example `UpdateFromTaskResultsUnsynchronized` copies
  `NewStructuralLoad` and the kinematic measurements, [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2524-L2531)) and then
  dispatched the next step to worker threads.[^ksa-vehicle] The workers integrate their own copies, so the
  main-thread `Vehicle` fields are stable here and match what the game's own gauges showed this frame.
  Reading them needs no lock.
- **Why after the frame and not in the UI hook.** Sampling once per frame in one place keeps the data
  identical for the overlay and for every standalone window, and lets the UI keep animating (and draw
  frozen values) even when no fresh sample was taken. The cost is one frame of latency: the UI of frame
  N+1 shows the sample taken at the end of frame N.
- **Main thread only.** Everything runs on the main thread; nothing here touches worker state.

## Key code

| Path | Role |
|---|---|
| [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L65-L83) | `[StarMapAfterOnFrame]` entry |
| [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L40-L138) | Per-frame sampling pipeline |
| [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L308-L345) | Kinematics, atmosphere, q |
| [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs#L199-L234) | Launch origin and downrange |
| [Telemetry/TelemetrySnapshot.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySnapshot.cs#L32-L103) | Snapshot object the UI reads |

## Snippets

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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Program.ControlledVehicle` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502) | The tracked vehicle |
| `Vehicle.GetSurfaceSpeed`, `GetInertialSpeed` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2949-L2963) | Speeds |
| `Vehicle.GetBarometricAltitude`, `GetRadarAltitude` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3060-L3075) | Altitudes |
| `Vehicle.Apoapsis`, `Periapsis`, `AccelerationBody`, `TotalMass`, `PropellantMass` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L399-L401), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L567-L573) | Apsides, g-load, mass |
| `Vehicle.GetPhysicsStates` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L916-L919) | Read-only view of bubble, kinematics, props, environment |
| `ReadOnlyPhysicsStates.GetStatesCci`, `GetStatesCcf`, `ComputeAirVelocityBody`, `Environment` | [current/decomp/KSA/ReadOnlyPhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ReadOnlyPhysicsStates.cs#L15-L70) | Position/velocity frames and air velocity |
| `PhysicsEnvironment.GravitationBub`, `AtmosphericPressure`, `AtmosphericDensity` | [current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L18-L24) | Gravity and atmosphere at the vehicle |
| `SituationEx.HasAnyContact`, `IsOnRails` | [current/decomp/KSA/SituationEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SituationEx.cs#L18-L64) | Contact and rails flags |
| `EngineController.ComputeActivePerformance` | [current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L68-L75) | Current thrust per controller |
| `IRadius.MeanRadius` | [current/decomp/KSA/IRadius.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IRadius.cs#L5) | Radius-to-altitude and downrange |

## Pitfalls and version notes

- **G-load is the felt acceleration.** `Vehicle.AccelerationBody` is the step's non-gravitational
  acceleration in body axes: the Verlet path stores `ForceBody * TotalMassInv` with gravity kept apart
  in `AccelPhys` ([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L1132-L1139)), the contact path adds the velocity change
  minus `dt` times gravity ([current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L927-L932)), and a vehicle resting on rails
  gets `GM / r^2` along the radial ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L2585-L2591)). The readout is
  therefore about 0 g while coasting and the local surface gravity on the pad (derived from the code,
  not tested). The game's structural g-load uses the same measurement
  ([current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L2437)).[^ksa-physicsstates] [^ksa-constraintsim] [^ksa-physicsbubble]
- **`ComputeActivePerformance` returns zero** when the controller is inactive or its propellant is not
  available, so starved engines drop out of the thrust total automatically.
- **Downrange is only known after the mod saw liftoff** (or loaded a saved origin); before that it shows
  "--".
- **Everything is a copy.** The snapshot holds plain values so the UI cannot accidentally mutate game
  state; keep it that way if you extend it.

## See also

- [Engine diagram from real nozzle positions](engine-diagram-from-nozzle-positions.md)
- [Mission clock and liftoff detection](mission-clock-and-liftoff-detection.md)
- [Freeze on signal loss](freeze-on-signal-loss.md)
- Frame order reference: [Game loop and lifecycle](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Plain-struct snapshot sampled after the frame, drawn next frame](../../../howto/external-io/telemetry/export-game-state-to-other-threads/export-game-state-to-other-threads-2.md) - how-to article demonstrating this feature
- [Take a plain flight-state snapshot in StarMapAfterOnFrame and draw it next frame](../../../howto/flight-and-orbits/flight-state/read-vehicle-flight-state/read-vehicle-flight-state-2.md) - how-to article demonstrating this feature
- [Sum ComputeActivePerformance thrust per active controller for a live thrust and T/W readout](../../../howto/flight-and-orbits/guidance/compute-a-suicide-burn/compute-a-suicide-burn-2.md) - how-to article demonstrating this feature

[^ksa-enginecontroller]: KSA.EngineController (decompiled, build 2026.10.10.5554)
[^starmap-programpatcher]: StarMap StarMap.Core/Patches/ProgramPatcher.cs
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-constraintsim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^ksa-physicsbubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
