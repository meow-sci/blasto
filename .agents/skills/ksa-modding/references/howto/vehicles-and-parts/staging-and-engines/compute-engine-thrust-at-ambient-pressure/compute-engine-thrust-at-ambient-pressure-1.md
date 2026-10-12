---
type: How-To
title: Evaluate engines at an ambient pressure and throttle, mirror the g-load cap and invert for a thrust demand
description: Call EngineController.ComputeActivePerformance with core-type checks, reproduce the structural throttle cap, and find the throttle for a requested thrust with a bracketed Illinois search
category: vehicles-and-parts
group: staging-and-engines
topic: compute-engine-thrust-at-ambient-pressure
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/engine-performance-and-throttle-inversion]
techniques: [engine-model-reuse]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers, frame:draw-ui]
game_types:
  - KSA.EngineController
  - KSA.RocketControllerData
  - KSA.RocketCore
  - KSA.Combustor
  - KSA.ThermalCore
  - KSA.SolidMotor
  - KSA.FlightComputer
  - KSA.ModuleStateful
tags: [thrust, throttle, inversion, g-load-cap, ambient-pressure]
---
Outcome: for any vehicle you can ask "what thrust and mass flow do my active engines deliver at throttle t and pressure p?" and
"what throttle gives thrust F?", and trust the answer to match what the engines will actually do. KSA-AdvancedFlightComputer's
guidance modes use it. `FlightComputer.VehicleConfigInfo` has no main-engine totals, so AFC calls the game's own engine model
instead of approximating nozzle physics.

Prerequisites: StarMap, and a main-thread hook after the last step is applied. Demonstrated by
[ksa-advancedflightcomputer/engine-performance-and-throttle-inversion](../../../../mods/ksa-advancedflightcomputer/features/engine-performance-and-throttle-inversion.md).

## Background: how the game does it

See the [overview](overview.md). Specifics:

- `VacuumData.ThrustMax` / `MassFlowRateMax` are the 0 Pa configuration totals of every engine, active or not.
- `ComputeActivePerformance` takes the engine's state (propellant availability), the centre of mass (for torque in the full
  data), the pressure and the throttle.
- When only some cores of an engine have propellant, the controller is still marked supplied if any core is (`Rocket.UpdateRockets`),
  so a correct sum visits supplied cores individually.
- In a Manual burn the flight computer caps the throttle with `SolveGLoadThrottleCap`; guidance flies in Manual, so a command
  above the cap is never delivered [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L489-L494)[^ksa-flightcomputer].

## Lifecycle and timing

Called synchronously from the guidance step in the `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`, main thread),
plus panel readouts in `frame:draw-ui`. At that point the module states in `vehicle.Parts.States` and the flight computer's
`MaxGLoad`/mass (brought back by `FlightComputer.CopyFrom`) are the committed results of the last step, and the throttle you
write now is what the next worker step reads. Nothing here runs on the physics worker: the cap is mirrored precisely because
the worker applies it later, out of your reach. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) and
[Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../../vehicles/change-vehicles-without-racing-the-solver/overview.md) article 5.

## Steps

1. **Configuration totals** (vacuum, active or not):

```csharp
// AdvancedFlightComputer/Features/Guidance/Control/EnginePerf.cs L14-29
    private const float StandardGravity = 9.80665f;

    // Configuration totals include inactive engines and do not depend on fuel supply.
    // Thrust is in N and mass flow is in kg/s.
    internal static (double thrust, double massFlow) Vacuum(Vehicle vehicle)
    {
        if (vehicle?.Parts == null)
            return (0.0, 0.0);
        double thrust = 0.0, massFlow = 0.0;
        foreach (EngineController engine in vehicle.Parts.Modules.Get<EngineController>())
        {
            thrust += engine.VacuumData.ThrustMax.Length();
            massFlow += engine.VacuumData.MassFlowRateMax;
        }
        return (thrust, massFlow);
    }
```

2. **Live performance at a throttle and pressure.** Walk `EngineController` modules with their states, skip inactive or
   unsupplied engines, clamp the command to `Vehicle.GetMinThrottle()`, and call `ComputeActivePerformance`; sum supplied cores
   one by one when only some are supplied:

```csharp
// AdvancedFlightComputer/Features/Guidance/Control/EnginePerf.cs L134-184
    private static (double thrust, double massFlow) RawPerformance(Vehicle vehicle, double throttle, double ambientPressure,
                                                                   bool liquidOnly = false)
    {
        if (vehicle?.Parts?.States == null || !double.IsFinite(throttle) || !double.IsFinite(ambientPressure)
            || !ModuleStateful<EngineController, EngineControllerState, EngineControllerGlobalState, EmptyStruct>
                .TryGetFrom(vehicle.Parts.States, out var states)
            || !ModuleStateful<RocketCore, RocketCoreState, RocketCoreGlobalState, EmptyStruct>
                .TryGetFrom(vehicle.Parts.States, out var coreStates))
            return (0.0, 0.0);

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
    }
```

3. **Mirror the g-load cap.** Reproduce `SolveGLoadThrottleCap` on the main-thread copies of `MaxGLoad` and
   `TotalMassPropsBody` (linear estimate, two fixed-point refinements, clamp to the minimum throttle); return 1 when the flight
   computer has not measured the vehicle yet or the engines cannot reach the limit:

```csharp
// AdvancedFlightComputer/Features/Guidance/Control/EnginePerf.cs L99-126
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

4. **Know which cores follow a throttle.** Only `Combustor` and `ThermalCore`. Refuse throttle control while any solid core is
   burning or ignited (a `SolidMotor` keeps burning after its controller is disabled):

```csharp
// AdvancedFlightComputer/Features/Guidance/Control/EnginePerf.cs L128-130
    // A combustion or thermal core sets its chamber pressure from the throttle, which is what RocketControllerData.ComputeFromCores models.
    // A solid motor ignores the throttle, and an AntimatterRamCore delivers what its ram speed and jet power allow rather than what ComputeConditions reports, so neither can follow a guidance command.
    internal static bool FollowsThrottle(RocketCore core) => core is Combustor or ThermalCore;
```

5. **Invert thrust to throttle.** Bracket the demand between the minimum-throttle and capped full-throttle thrust, run an
   Illinois false-position search (bisection fallback, at most 60 iterations, tolerance `1e-7 * full`), and return the upper
   bracket rounded to `float` so the throttle is one the game can deliver. Report a status (`Off`, `Available`,
   `BelowMinimum`, `AboveMaximum`, `NoAuthority`, ...) instead of a bare number:

```csharp
// AdvancedFlightComputer/Features/Guidance/Control/EnginePerf.cs L278-332
    internal static ThrustCommand InvertThrust(double demandN, double minimumThrottle, Func<double, double> thrustAtThrottle,
                                               double maximumThrottle = 1.0)
    {
        if (!double.IsFinite(demandN))
            return new(0.0, 0.0, ThrustStatus.InvalidDemand);
        if (demandN <= 0.0)
            return new(0.0, 0.0, ThrustStatus.Off);
        if (!double.IsFinite(minimumThrottle) || minimumThrottle < 0.0 || minimumThrottle > 1.0
            || !double.IsFinite(maximumThrottle))
            return new(0.0, 0.0, ThrustStatus.NoAuthority);

        double lo = Math.Max(minimumThrottle, float.Epsilon), hi = Math.Clamp(maximumThrottle, lo, 1.0);
        double full = thrustAtThrottle(hi), minimum = thrustAtThrottle(lo);
        if (!double.IsFinite(full) || !double.IsFinite(minimum) || full <= 0.0 || minimum < 0.0 || minimum > full)
            return new(0.0, 0.0, ThrustStatus.NoAuthority);
        if (demandN < minimum)
            return new(lo, minimum, ThrustStatus.BelowMinimum);
        if (demandN >= full)
            return new(hi, full, demandN > full ? ThrustStatus.AboveMaximum : ThrustStatus.Available);

        // Illinois false position: keeps the bracket thrust(lo) < demand <= thrust(hi) like bisection, and takes the midpoint when the secant point is degenerate or the bracket stops halving.
        double fLo = minimum - demandN, fHi = full - demandN;
        double tolerance = full * 1e-7;
        int lastSide = 0, stalls = 0;
        for (int i = 0; i < 60 && hi - lo > 1e-9; i++)
        {
            double width = hi - lo;
            double mid = stalls >= 2 ? 0.5 * (lo + hi) : hi - fHi * width / (fHi - fLo);
            if (!(mid > lo && mid < hi))
                mid = 0.5 * (lo + hi);
            double f = thrustAtThrottle(mid) - demandN;
            if (!double.IsFinite(f))
                return new(0.0, 0.0, ThrustStatus.NoAuthority);
            if (f < 0.0)
            {
                lo = mid;
                fLo = f;
                if (lastSide < 0) fHi *= 0.5;
                lastSide = -1;
            }
            else
            {
                hi = mid;
                fHi = f;
                if (lastSide > 0) fLo *= 0.5;
                lastSide = 1;
                if (f <= tolerance)
                    break;
            }
            stalls = hi - lo > 0.5 * width ? stalls + 1 : 0;
        }
        // Return the realizable upper endpoint, including the game's float rounding.
        float throttle = (float)hi;
        return new(throttle, thrustAtThrottle(throttle), ThrustStatus.Available);
    }
```

6. **Ambient pressure from altitude**, with the same cutoff the game uses so vacuum reads exactly zero:

```csharp
// AdvancedFlightComputer/Features/Guidance/Control/EnginePerf.cs L334-342
    internal static double AmbientPressureAt(IParentBody parent, double altitudeAslM)
    {
        PhysicalAtmosphereReference physical = parent?.GetAtmosphereReference()?.Physical;
        // Same cutoff as PhysicsEnvironment.RecomputePositionalValues, so vacuum reads exactly zero.
        if (physical == null || altitudeAslM >= (double)physical.Height)
            return 0.0;
        double pressure = physical.GetAtmosphericPressureAtAltitude(altitudeAslM);
        return double.IsFinite(pressure) && pressure > 0.0 ? pressure : 0.0;
    }
```

7. Cache per vehicle if you evaluate repeatedly; AFC refreshes at a fixed interval, not per frame.

## Complete example

- [AdvancedFlightComputer/Features/Guidance/Control/EnginePerf.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/EnginePerf.cs)[^ksa-advancedflightcomputer-engineperf]

## Variations and gotchas

- **Needs a monotone thrust curve.** Zero demand switches the engine off instead of commanding the minimum throttle.
- **`double` vs `float`.** The game computes in float; round the answer through `float` so what you request is realisable.

## See also

- [Overview](overview.md), [Article 2](compute-engine-thrust-at-ambient-pressure-2.md)
- [ksa-advancedflightcomputer/engine-performance-and-throttle-inversion](../../../../mods/ksa-advancedflightcomputer/features/engine-performance-and-throttle-inversion.md)
- [Read staged delta-v, thrust and Isp from the game or from your own stage model](../read-staged-delta-v-from-the-game/overview.md), [Switch engines on and off, set throttle and RCS commands, and choose which RCS jets fire](../../modules-and-resources/command-engines-throttle-and-rcs-from-code/overview.md)

[^ksa-advancedflightcomputer-engineperf]: KSA-AdvancedFlightComputer: AdvancedFlightComputer/Features/Guidance/Control/EnginePerf.cs
[^ksa-flightcomputer]: current/decomp/KSA/FlightComputer.cs (decompiled, build 2026.10.10.5554)
