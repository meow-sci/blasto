---
type: How-To
title: Evaluate the core engine model at a throttle and pressure, and invert a thrust demand into a throttle
description: Reuse the game's own engine model to get thrust and mass flow at any throttle and ambient pressure, mirror the flight computer's g-load throttle cap, and bracket a throttle for a requested thrust with a saturation status
category: flight-and-orbits
group: guidance
topic: compute-a-suicide-burn
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/engine-performance-and-throttle-inversion]
techniques: [engine-model-reuse, root-finding]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers]
game_types:
  - KSA.Vehicle
  - KSA.EngineController
  - KSA.ActiveEnginePerformance
  - KSA.RocketControllerData
  - KSA.RocketCore
  - KSA.SolidMotor
  - KSA.FlightComputer
tags: [thrust, throttle, engine, inversion, root-finding, g-load, solid-motor, minimum-throttle]
---
Outcome: two functions a guidance loop needs for a landing burn. First, the thrust and mass flow of the burning engines at a throttle and ambient pressure of your choosing, without approximating nozzle physics. Second, the inverse: the throttle that produces a requested thrust, with a status when the demand is below the engine minimum, above the maximum, or the engines cannot follow a throttle at all. KSA-AdvancedFlightComputer wraps both in `KsaEnginePerf` and feeds them from its terminal-hover, G-FOLD, 6-DOF and ascent modes.[Engine performance and throttle inversion](../../../../mods/ksa-advancedflightcomputer/features/engine-performance-and-throttle-inversion.md)
Prerequisites: StarMap and Harmony (the caller is a `Vehicle.PrepareWorker` prefix). Compare with the simpler [approach 1](compute-a-suicide-burn-1.md) and [approach 2](compute-a-suicide-burn-2.md).

## Background: how the game does it

- `EngineController.ComputeActivePerformance(in state, centerOfMassAsmb, ambientPressure, throttle)` returns `RocketControllerData.Zero` for an inactive or unsupplied controller, the cached `VacuumData` for zero pressure at full throttle, and otherwise `RocketControllerData.ComputeFromCores(cores, pressure, throttle)`.[^ksa-enginecontroller] [current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L68-L75)
- `FlightComputer` keeps an `ActiveEnginePerformance` (`Thrust`, `MassFlowRate`, `MinThrottle`, `ExhaustVelocity`) for the burn planner.[^ksa-activeengineperformance] In Manual it limits the throttle to the private `SolveGLoadThrottleCap`: thrust held to `0.9 * MaxGLoad * 9.80665 * mass`, from a linear estimate refined twice against `ComputeActiveEnginePerformance`, clamped to the engine minimum.[^ksa-flightcomputer] [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1067-L1095)
- `SolidMotor` is a `RocketCore` that ignores the throttle, so only `Combustor` and `ThermalCore` cores follow a commanded throttle.[^ksa-solidmotor]

## Lifecycle and timing

- **Where:** called synchronously from a Harmony prefix on `Vehicle.PrepareWorker` (`frame:dispatch-solvers`, main thread, per vehicle). `Universe.PrepareVehicleWorkers` calls `PrepareWorker` for every vehicle before the next step is dispatched,[^ksa-universe] [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2141-L2149) so module states and the flight computer's copy are the committed results of the last step, and the throttle written here is what the worker step reads next.
- **Readouts** in `frame:draw-ui` call the same functions without writing.
- **Why mirror the cap:** the worker applies `SolveGLoadThrottleCap` later, out of reach of the main thread, so a command above the cap would never be delivered. Compute it yourself from the `MaxGLoad` and `TotalMassPropsBody` that `FlightComputer.CopyFrom` brought back.
- See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Run an autopilot for every vehicle each step](../../autopilot/run-an-autopilot-for-every-vehicle-each-step/overview.md).

## Steps

1. **Evaluate performance for one throttle and pressure.** Walk `EngineController` modules with their states, skip inactive or unsupplied engines, clamp the command to `Vehicle.GetMinThrottle()` and call the game's model. When only some cores of an engine have propellant, sum `RocketControllerData.ComputeFromCores` over the supplied cores one at a time, because the game marks the controller supplied when any core is:

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

2. **Mirror the g-load throttle cap** so the commanded throttle is one the worker will deliver:

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

3. **Get ambient pressure at a predicted altitude** (for a planner that looks ahead): `PhysicalAtmosphereReference.GetAtmosphericPressureAtAltitude`, forced to zero at and above `Physical.Height` so vacuum reads exactly zero (`KsaEnginePerf.AmbientPressureAt`).
4. **Check that the engines can follow a throttle.** `FollowsThrottle(core)` is `core is Combustor or ThermalCore`; `GetThrottleControlStatus` returns `UnsupportedEngine` while any solid core is burning, ignited or supplied, because a `SolidMotor` keeps burning after its controller is disabled.
5. **Invert thrust to throttle.** `CommandForThrust(vehicle, demandN, ambientPressure)` brackets the demand between the minimum-throttle thrust and the capped full-throttle thrust and runs a false-position search:

```csharp
// AdvancedFlightComputer/Features/Guidance/Control/EnginePerf.cs L278-L296, L329-L331
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
// ...
    // Return the realizable upper endpoint, including the game's float rounding.
    float throttle = (float)hi;
    return new(throttle, thrustAtThrottle(throttle), ThrustStatus.Available);
```

6. **Use the result.** A suicide-burn controller asks for `thrust = mass * (g + a_cmd)` and writes the returned `ThrustCommand.Throttle`; `Status` is `Off`, `Available`, `BelowMinimum`, `AboveMaximum`, `NoAuthority`, `UnsupportedEngine` or `InvalidDemand`, each with a message:

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs L165-L170
var local = new double3(Math.Max(aUp, 0.0), aE, aN);
double pressure = KsaEnginePerf.AmbientPressureAt(parent, r.Length() - parent.MeanRadius);
KsaEnginePerf.ThrustCommand command = KsaEnginePerf.CommandForThrust(
    vehicle, local.Length() * vehicle.TotalMass, pressure);
_s.GfoldThrottle = command.Throttle;
_s.GfoldThrustStatus = command.Message;
```

## Complete example

| File | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Control/EnginePerf.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Control/EnginePerf.cs) | all of the above (`KsaEnginePerf`) |
| [AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/GuidanceFeature.cs#L26-L62) | the `Vehicle.PrepareWorker` prefix that steps guidance |
| [AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/TerminalHover.cs#L150-L172) | a typical caller: pressure at altitude, then `CommandForThrust` |

## Variations and gotchas

- **Copied constants.** The 0.9 margin and the two refinement passes are copied from the decompiled `SolveGLoadThrottleCap`; if the game changes them, the commanded and delivered throttle diverge silently.
- **Zero demand switches the engine off** rather than commanding the minimum throttle; a hover that cannot throttle below its weight is refused upstream with `BelowMinimum`.
- **Needs a monotone thrust curve** in throttle; the search keeps the bracket `thrust(lo) < demand <= thrust(hi)` and falls back to bisection when the secant point degenerates, with at most 60 iterations.
- **Solid motors** ignore the throttle; `CommandLiquidFraction` is the variant for a plan whose throttle is only the liquids' share (solids add their thrust on top).
- **Minimum pulse.** A started minimum pulse keeps burning after engine-off (`RocketCore.MinimumPulseTime`, `RocketCoreState.MinThrustTimeRemaining`); account for it near touchdown.
- **Pressure is `float`.** Clamp ambient pressure to non-negative finite values before calling the game's model.

## See also

- [Overview](overview.md); [Closed-form calculator](compute-a-suicide-burn-1.md); [Thrust sum per active controller](compute-a-suicide-burn-2.md)
- [Engine performance and throttle inversion (mod feature)](../../../../mods/ksa-advancedflightcomputer/features/engine-performance-and-throttle-inversion.md)
- [Plan and fly a powered landing](../plan-and-fly-a-powered-landing/overview.md) and [Fly a closed-loop ascent](../fly-closed-loop-ascent-guidance/overview.md), the callers of this module
- [Execute burns with RCS and throttle inputs](../../autopilot/execute-burns-with-rcs-and-throttle-inputs/overview.md)
- [Reading staged delta-v, thrust and Isp from the game or from your own stage model](../../../vehicles-and-parts/staging-and-engines/read-staged-delta-v-from-the-game/overview.md)

[^ksa-enginecontroller]: KSA.EngineController (decompiled, build 2026.10.10.5554)
[^ksa-activeengineperformance]: KSA.ActiveEnginePerformance (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-solidmotor]: KSA.SolidMotor (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
