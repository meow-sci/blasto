---
type: How-To
title: Plain-struct snapshot sampled after the frame, drawn next frame
description: When every consumer is a main-thread UI, copy kinematics into a plain snapshot object from StarMapAfterOnFrame and let the next frame draw it, with no locks and one frame of latency
category: external-io
group: telemetry
topic: export-game-state-to-other-threads
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksa-public-telemetry/features/live-flight-telemetry-sampling]
techniques: [snapshot-copy, starmap-lifecycle]
hooks: [starmap StarMapAfterOnFrame, starmap StarMapAfterGui]
lifecycle: [frame:after-on-frame, frame:draw-ui]
game_types: [KSA.Program, KSA.Vehicle, KSA.Universe, KSA.ReadOnlyPhysicsStates]
tags: [snapshot, single-thread, telemetry, hud, latency]
---

Outcome: a HUD or broadcast overlay reads one plain `TelemetrySnapshot` object, never the `Vehicle`; the snapshot is refilled once per frame after the frame completes. There is no second thread, so no volatile swap and no lock. Use this when every reader runs on the main thread; use [approach 1](export-game-state-to-other-threads-1.md) the moment a socket, file system or background thread needs the data. Demonstrated by [Live flight telemetry sampling](../../../../mods/ksa-public-telemetry/features/live-flight-telemetry-sampling.md) in KSA-Public-Telemetry.

## Background: how the game does it

By the time `StarMapAfterOnFrame` fires, `PrepareFrame` has applied the finished vehicle step to the main-thread `Vehicle` objects and dispatched the next one to workers that integrate their own copies ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1952), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423)). The main-thread fields are therefore stable for the rest of the frame and match what the game's own gauges showed, and reading them needs no lock.

## Lifecycle and timing

| Piece | Where |
|---|---|
| Sample | `[StarMapAfterOnFrame]` (`frame:after-on-frame`): a Harmony postfix on `Program.OnFrame`, so it runs after the whole frame including render, and also on frames where `PrepareFrame` returned `Exit` |
| Draw | The next frame's UI phase: `[StarMapAfterGui]` (`frame:draw-ui`, HUD shown) or a hidden-HUD hook |

The cost is one frame of latency: the UI of frame N+1 shows the sample taken at the end of frame N. The benefit is that sampling happens once per frame in one place, so the overlay and every standalone window show identical data, and the UI can keep animating with frozen values when no fresh sample was taken. All of it is main-thread. The after-frame hook also fires with the HUD hidden, which is why this mod can keep sampling for a broadcast overlay; see [Draw UI while the HUD is hidden](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md) and [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#the-imgui-window).

## Steps

1. **Define a plain data class** with public fields only and no methods that touch the game, so the UI cannot mutate game state. Start with identity and flags, then the readouts (`SurfaceSpeed`, `Altitude`, `Apoapsis`, `GLoad`, `DynamicPressure`, ...):

   ```csharp
   // Telemetry/TelemetrySnapshot.cs L32-L56
   public sealed class TelemetrySnapshot
   {
       public bool HasVehicle;
       public bool OnRails;
       public bool HasSurfaceContact;
       public bool IsControllable;
       public double MissionElapsedSeconds;
       public bool HasLiftoff;
       public SignalStatus Signal;
       public bool IsFrozen;
       public string VehicleName = string.Empty;
       public double SurfaceSpeed;
       public double OrbitalSpeed;
       public double VerticalSpeed;
       public double Altitude;
       public double RadarAltitude;
       public double Apoapsis;
       public double Periapsis;
       public double GLoad;
       public float TotalMass;
       public float PropellantMass;
       public float Thrust;
       public float ThrustToWeight;
       public float LocalGravity;
       public float AmbientPressure;
   ```

2. **Sample from `StarMapAfterOnFrame`,** guarded so a failing sample disables the feature instead of throwing every frame:

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

3. **Pick the vehicle and decide whether to sample, hold or clear.** `Program.ControlledVehicle` can be null or disposed; the mod holds the last frame or clears rather than reading a dead vehicle.

   ```csharp
   // Telemetry/TelemetrySampler.cs L40-L62
       public static void Sample(TelemetrySnapshot snapshot, OverlayConfig config, double dt)
       {
           Vehicle? vehicle = Program.ControlledVehicle;

           bool unavailable = vehicle is null || vehicle.IsDisposed;

           TrackingDecision decision = DecideTracking(
               holdingFrame: snapshot.HasVehicle,
               vehicleUnavailable: unavailable,
               trackedGone: unavailable && TrackedVehicleIsGone(),
               isControllable: !unavailable && vehicle!.IsControllable,
               wreckHold: _wreckHold);

           _wreckHold = decision.WreckHold;

           if (decision.Action == TrackingAction.Clear)
           {
               ClearTracking(snapshot);
               return;
           }

           if (decision.Action == TrackingAction.Hold)
           {
   ```

4. **Copy values, not references.** The kinematics pass reads the vehicle's accessors and the read-only physics-state view and writes numbers into the snapshot:

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

5. **Draw from the snapshot only.** The overlay is drawn from `StarMapAfterGui`, and from a hidden-HUD hook when "show when game UI hidden" is on; both call `DrawEverything`, which passes `_snapshot` to the windows and renderer and never touches `Vehicle` ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L85-L135)).

## Complete example

| File | Role |
|---|---|
| [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs) | `[StarMapAfterOnFrame]` entry and the draw hooks |
| [Telemetry/TelemetrySampler.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySampler.cs) | Per-frame sampling pipeline |
| [Telemetry/TelemetrySnapshot.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Telemetry/TelemetrySnapshot.cs) | The snapshot object the UI reads |

## Variations and gotchas

- **One frame of latency** is inherent; if the UI must show this frame's state, sample in the same hook that draws, at the cost of losing the after-frame guarantee.
- **Not thread-safe.** A background thread that reads the same object can see a half-written snapshot. The moment a second thread appears, move to [approach 1](export-game-state-to-other-threads-1.md).
- **Everything is a copy,** so the snapshot cannot accidentally mutate game state; keep it that way when extending.
- **Derived values need history** (maximum dynamic pressure, launch origin for downrange): keep them in the snapshot or the sampler, and reset them when the controlled vehicle changes.
- **Detect events from the same pass:** the mod's liftoff and staging callouts diff this snapshot ([Detect flight events when the game has no event bus](../detect-flight-events-from-snapshots/overview.md)).

## See also

- [Overview](overview.md)
- [Approach 1: immutable snapshot with a volatile swap](export-game-state-to-other-threads-1.md)
- [Live flight telemetry sampling](../../../../mods/ksa-public-telemetry/features/live-flight-telemetry-sampling.md)
- [Read vehicle flight state](../../../flight-and-orbits/flight-state/read-vehicle-flight-state/overview.md)
- [Compute mission elapsed time and detect liftoff](../../../flight-and-orbits/flight-state/compute-mission-elapsed-time-and-detect-liftoff/overview.md)
