---
type: Mod Feature
title: Thrust at ambient pressure, the structural throttle cap and thrust-to-throttle inversion
description: Evaluate active engines through the game's own core model at a given throttle and pressure, mirror the flight computer's g-load throttle cap, and invert a thrust demand into a throttle with a bracketed root find that reports saturation and solid-motor states
mod: ksa-advancedflightcomputer
category: vehicles-and-parts
techniques: [engine-model-reuse, root-finding]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers]
game_types:
  - KSA.EngineController
  - KSA.RocketControllerData
  - KSA.RocketCore
  - KSA.RocketCoreState
  - KSA.Combustor
  - KSA.ThermalCore
  - KSA.SolidMotor
  - KSA.AntimatterRamCore
  - KSA.FlightComputer
  - KSA.VehicleStructuralLimits
  - KSA.PhysicalAtmosphereReference
  - KSA.ModuleStateful
tags: [engines, thrust, throttle, isp, atmosphere, solid-motor, g-load, guidance]
---

Every AFC guidance mode needs the same three answers about the engines: how much thrust and mass flow
they deliver at a given throttle and ambient pressure, how much of that the stock flight computer will
actually let through, and which throttle produces a requested thrust. `FlightComputer.VehicleConfigInfo`
has no main-engine totals, so `KsaEnginePerf` computes them by calling the game's own engine model
instead of approximating nozzle physics ([AdvancedFlightComputer/Features/Guidance/Control/EnginePerf.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/EnginePerf.cs)).

## How it works

### Performance through the game's engine model

| Function | What it returns |
|---|---|
| `Vacuum` | Sum of `EngineController.VacuumData.ThrustMax` / `MassFlowRateMax` over all engines, active or not (configuration totals) |
| `AtPressure`, `AtThrottle` | Active, supplied engines at a throttle and pressure, capped by the structural throttle cap |
| `UncappedAtPressure` | The same without the cap, for planners that receive the acceleration limit as their own bound |
| `AmbientPressureAt(parent, altitude)` | `PhysicalAtmosphereReference.GetAtmosphericPressureAtAltitude`, zero at and above `Physical.Height`, the same cutoff `PhysicsEnvironment.RecomputePositionalValues` uses ([current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L91-L101)) |
| `MinimumPulse`, `RemainingMinimumPulse` | `RocketCore.MinimumPulseTime` and the per-core `RocketCoreState.MinThrustTimeRemaining`, because a started minimum pulse keeps burning after engine-off |

The core loop (`RawPerformance`) walks `EngineController` modules with their states via
`ModuleStateful<...>.TryGetFrom(vehicle.Parts.States, ...)`, skips inactive or unsupplied engines, clamps the
command to `Vehicle.GetMinThrottle()`, and calls `EngineController.ComputeActivePerformance(state, CoM,
pressure, throttle)`, which returns `VacuumData` at 0 Pa and full throttle and otherwise
`RocketControllerData.ComputeFromCores` ([current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L68-L75)).[^ksa-enginecontroller]
When only some cores of an engine have propellant, it sums `ComputeFromCores` over the supplied cores one
at a time instead, because the game marks the controller supplied when any core is.

### Mirroring the g-load throttle cap

In Manual, `FlightComputer.ComputeControl` limits the engine throttle to `SolveGLoadThrottleCap`: thrust
held to `0.9 * MaxGLoad * 9.80665 * mass`, starting from a linear estimate and refined twice against
`ComputeActiveEnginePerformance`, clamped to the minimum throttle ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L488-L494),
[current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1067-L1095)).[^ksa-flightcomputer]
Guidance flies in Manual, so a command above that throttle is never delivered. `StructuralThrottleCap`
reproduces the same solve on the main-thread copy of `MaxGLoad` and `TotalMassPropsBody` (brought back
by `FlightComputer.CopyFrom`), and `StructuralAccelerationLimit` falls back to
`VehicleStructuralLimits.EffectiveMaxGLoad` of the bounding sphere before the worker has measured the
vehicle, the same source `FlightComputer.ReadMeasurements` uses. The autopilot plans with the lower of
the player's g-limit and this limit ([AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs#L757-L765)).

### Inverting thrust to throttle

`InvertThrust` brackets the demand between the minimum-throttle and the capped full-throttle thrust and
runs an Illinois false-position search (bisection fallback, at most 60 iterations, tolerance
`1e-7 * full`), then returns the upper bracket rounded to `float` so the throttle it reports is one the
game can deliver. The result is a `ThrustCommand(Throttle, DeliveredThrust, Status)`; `Status` is one of
`Off`, `Available`, `BelowMinimum`, `AboveMaximum`, `NoAuthority`, `UnsupportedEngine`,
`InvalidDemand`, each with a player-facing message.

### Which engines can follow a throttle

`FollowsThrottle` accepts only `Combustor` and `ThermalCore` cores: a `SolidMotor` ignores the throttle and
an `AntimatterRamCore` delivers what its ram speed allows. `GetThrottleControlStatus` refuses throttle
control (`UnsupportedEngine`) while any solid core is burning or ignited, because `SolidMotor` keeps
burning after its controller is disabled; `CommandLiquidFraction` is the variant for a plan whose
throttle is only the liquids', with solids adding their thrust on top (used by the convex ascent).

## Lifecycle and timing

Called synchronously from the guidance step in the `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`,
main thread), plus panel readouts in `frame:draw-ui`. At that point the module states in
`vehicle.Parts.States` and the flight computer's `MaxGLoad`/mass are the committed results of the last
step, and the throttle written now is what the next worker step reads. Nothing here runs on the physics
worker: the cap is mirrored precisely because the worker applies it later, out of reach.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Control/EnginePerf.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/EnginePerf.cs) | All of the above |
| [AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs) | Typical caller: pressure at altitude, then `CommandForThrust` |
| [AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/ConvexAscent.cs) | `UncappedAtPressure` and `CommandLiquidFraction` with solids burning |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceGLoadCapTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceGLoadCapTest.cs) | Lowers `MaxGLoad` on the main-thread copy to exercise the cap |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceAmbientThrustTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceAmbientThrustTest.cs) | Checks thrust against the game's 0 Pa and sea-level engine data |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceThrustTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceThrustTest.cs) | Inversion on the game's nozzle curve, engine selection on synthetic states |

## Snippets

```csharp
// AdvancedFlightComputer/Features/Guidance/Control/EnginePerf.cs L98-L126
    // FlightComputer.ComputeControl caps a Manual engine throttle at FlightComputer.SolveGLoadThrottleCap, and guidance flies in Manual, so a command above this throttle is never delivered.
    // Mirrors that solve from the MaxGLoad and mass FlightComputer.CopyFrom brings back to the main thread, with the linear estimate followed by two fixed-point refinements, clamped to the minimum throttle.
    // Returns 1 when the flight computer has not measured the vehicle yet or the engines cannot reach the limit.
    internal static double StructuralThrottleCap(Vehicle vehicle, double ambientPressure)
    {
        FlightComputer fc = vehicle?.FlightComputer;
        if (fc == null)
            return 1.0;
        double limitN = StockGLoadMargin * fc.MaxGLoad * StandardGravity * fc.TotalMassPropsBody.Mass;
        if (!(limitN > 0.0))
            return 1.0;
        // EngineController.VacuumData is the full-throttle thrust of every engine with no ambient pressure, which only lowers nozzle thrust.
        // Engines whose vacuum total stays under the limit cannot reach it, so the usual case returns here without a performance evaluation.
        if (!(VacuumThrust(vehicle) > limitN))
            return 1.0;
        double full = RawPerformance(vehicle, 1.0, ambientPressure).thrust;
        if (!(full > limitN))
            return 1.0;
        double minimum = Math.Clamp(vehicle.GetMinThrottle(), 0.0, 1.0);
        double cap = Math.Clamp(limitN / full, minimum, 1.0);
        for (int i = 0; i < 2; i++)
        {
            double thrust = RawPerformance(vehicle, cap, ambientPressure).thrust;
            if (!(thrust > 0.0))
                break;
            cap = Math.Clamp(cap * limitN / thrust, minimum, 1.0);
        }
        return cap;
    }
```

```csharp
// AdvancedFlightComputer/Features/Guidance/Control/EnginePerf.cs L144-L183
        // Vehicle.PrepareWorker applies the vehicle floor. ComputeActivePerformance then applies each core's floor through RocketControllerData.ComputeFromCores.
        float command = (float)Math.Clamp(throttle, vehicle.GetMinThrottle(), 1.0);
        float pressure = (float)Math.Clamp(ambientPressure, 0.0, float.MaxValue);
        double thrust = 0.0, massFlow = 0.0;
        foreach (var engine in states.ModulesAndStates)
        {
            if (!engine.Module.IsActive || !engine.State.IsPropellantAvailable
                || engine.Module.Cores == null || engine.Module.Cores.Length == 0)
                continue;
            RocketCore[] cores = engine.Module.Cores;
            if (liquidOnly && Array.Exists(cores, core => !FollowsThrottle(core)))
                continue;
            bool allSupplied = true;
            foreach (RocketCore core in cores)
                allSupplied &= coreStates.States[core.StatesIdx].IsPropellantAvailable;
            RocketControllerData data;
            if (allSupplied)
                data = engine.Module.ComputeActivePerformance(in engine.State, vehicle.TotalMassPropsAsmb.Offset, pressure, command);
            else
            {
                // Rocket.UpdateRockets marks a controller supplied when any core has fuel.
                // Sum only supplied cores, while preserving the controller's vector sum.
                data = RocketControllerData.Zero;
                for (int i = 0; i < cores.Length; i++)
                {
                    if (!coreStates.States[cores[i].StatesIdx].IsPropellantAvailable)
                        continue;
                    RocketControllerData coreData = RocketControllerData.ComputeFromCores(
                        cores.AsSpan(i, 1), pressure, command);
                    data.ThrustMax += coreData.ThrustMax;
                    data.MassFlowRateMax += coreData.MassFlowRateMax;
                }
            }
            double force = data.ThrustMax.Length();
            if (!double.IsFinite(force) || !float.IsFinite(data.MassFlowRateMax))
                return (0.0, 0.0);
            thrust += force;
            massFlow += data.MassFlowRateMax;
        }
        return (thrust, massFlow);
```

## Game APIs touched

| Member | Use |
|---|---|
| `EngineController.ComputeActivePerformance`, `VacuumData`, `IsActive`, `Cores` ([current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L36-L75)) | Thrust and mass flow at a throttle and pressure |
| `RocketControllerData.ComputeFromCores`, `Zero`, `ThrustMax`, `MassFlowRateMax` ([current/decomp/KSA/RocketControllerData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketControllerData.cs#L8-L21)) | Per-core evaluation for partly supplied engines |
| `RocketCore.MinimumPulseTime`, `StatesIdx`; `RocketCoreState.IsPropellantAvailable`, `MinThrustTimeRemaining`, `IsNonzero()` ([current/decomp/KSA/RocketCore.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketCore.cs#L10), [current/decomp/KSA/RocketCoreState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketCoreState.cs#L11-L41)) | Supply, minimum pulse, solid burning state |
| `FlightComputer.MaxGLoad`, `TotalMassPropsBody`, `SolveGLoadThrottleCap` (private), `ReadMeasurements`, `CopyFrom` ([current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1067-L1095)) | The cap being mirrored |
| `VehicleStructuralLimits.EffectiveMaxGLoad` ([current/decomp/KSA/VehicleStructuralLimits.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleStructuralLimits.cs#L17)) | Limit before the first measurement |
| `PhysicalAtmosphereReference.GetAtmosphericPressureAtAltitude`, `Height` ([current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L80)) | Ambient pressure at an altitude |
| `Vehicle.GetMinThrottle()` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1264)) | Vehicle throttle floor |
| `ModuleStateful<TModule, TState, ...>.TryGetFrom` ([current/decomp/KSA/ModuleStateful.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleStateful.cs#L840)) | Module and state pairs from `Parts.States` |

## Pitfalls and version notes

- The cap formula, the 0.9 margin and the two refinement passes are copied from the decompiled
  `SolveGLoadThrottleCap`; if the game changes them, AFC's commands and the delivered throttle diverge
  silently. The harness test `afc-guidance-gload-cap` is the tripwire.
- `MaxGLoad` is rewritten from `EffectiveMaxGLoad` on the worker in `ReadMeasurements`, so a test that
  lowers it must not let a step run in between (the test's own comment).
- `SequencePerformance` and the stage menu use each sequence's `Environment` (vacuum or sea level), not
  the craft's pressure; use this module for the burning engines' real thrust (see
  [stage model from the staging simulator](stage-model-from-staging-simulator.md)).
- Zero demand switches the engine off instead of commanding the minimum throttle; a hover that cannot
  throttle below its weight is refused upstream (see [terminal hover and touchdown](terminal-hover-touchdown.md)).

## See also

- [Guidance attitude and gimbal actuation](guidance-attitude-and-gimbal-actuation.md)
- [Stage model from the staging simulator](stage-model-from-staging-simulator.md)
- [UPFG ascent guidance](upfg-ascent-guidance.md)
- [Minimum-propellant convex ascent](convex-ascent-scvx.md)
- [Custom attitude in an inertial frame plus turning-rate feedforward, gimbal commands and thrust inversion](../../../howto/flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/drive-the-flight-computer-and-attitude-modes-from-a-mod-4.md) - how-to article demonstrating this feature
- [Evaluate the core engine model at a throttle and pressure, and invert a thrust demand into a throttle](../../../howto/flight-and-orbits/guidance/compute-a-suicide-burn/compute-a-suicide-burn-3.md) - how-to article demonstrating this feature
- [Evaluate engines at an ambient pressure and throttle, mirror the g-load cap and invert for a thrust demand](../../../howto/vehicles-and-parts/staging-and-engines/compute-engine-thrust-at-ambient-pressure/compute-engine-thrust-at-ambient-pressure-1.md) - how-to article demonstrating this feature

[^ksa-enginecontroller]: KSA.EngineController (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
