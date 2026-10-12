---
type: Mod Feature
title: Live and rated engine thrust with TWR
description: Reads actual thrust and mass flow from the applied rocket-nozzle states of active engines, rated full-throttle thrust at an ambient pressure from RocketControllerData or Vehicle.ComputeActiveThrust, and turns both into TWR against gravity computed for any body and altitude
mod: kitten-engineer-redux
category: vehicles-and-parts
techniques: [engine-performance-readout, flight-math]
hooks: [harmony-postfix KSA.Vehicle.OnDrawUi, harmony-postfix KSA.VehicleEditor.OnDrawUi]
lifecycle: [frame:draw-ui, editor:draw-ui]
game_types:
  - KSA.PartTree
  - KSA.EngineController
  - KSA.EngineControllerState
  - KSA.RocketCore
  - KSA.Rocket
  - KSA.RocketNozzle
  - KSA.RocketNozzleState
  - KSA.RocketPerformance
  - KSA.RocketControllerData
  - KSA.SolidMotor
  - KSA.Vehicle
  - KSA.PhysicsEnvironment
  - KSA.IParentBody
  - KSA.Astronomical
  - KSA.PhysicalAtmosphereReference
  - KSA.Constants
tags: [thrust, twr, engines, nozzles, ambient-pressure, gravity, atmosphere, flight, editor]
---

KER shows three different engine numbers, and each comes from a different game read: the thrust the
engines are producing right now (HUD "Thrust (act / max)", actual TWR, telemetry), the full-throttle
thrust of the engines that are lit at the current air pressure (HUD max TWR, suicide burn), and the
thrust and mass flow an engine would produce at an arbitrary pressure (editor and estimator at the
analysis altitude). Two small helpers supply the matching gravity and pressure for any body and
altitude, so every TWR is thrust over local weight rather than over `9.80665`.

## How it works

| Number | KER code | Game read |
|---|---|---|
| Actual thrust and mass flow | `ActiveEngineThrust.ComputeActual(parts)` ([ActiveEngineThrust.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/ActiveEngineThrust.cs#L12-L35)) | For every `EngineController` with `IsActive`, for every `RocketCore` in `Cores`, enumerate `PartTree.RocketNozzles.GetModulesAndStates(core.Rocket.Nozzles)` and sum `State.Performance.TotalThrust` (`MassFlowRate * EffectiveExhaustVelocity`) and `State.Performance.MassFlowRate`. These are the per-nozzle results of the last physics step, so throttle, flame-out and back pressure are already in them |
| Rated thrust of lit engines at live pressure (HUD max) | `vehicle.ComputeActiveThrust(PhysicsEnvironment.AtmosphericPressure)` ([FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L130-L141)) | The game's own method: per engine `ComputeActivePerformance(state, com, p, 1f)`, which returns zero unless the engine `IsActive` and its `EngineControllerState.IsPropellantAvailable`, `VacuumData` when `p <= 0`, otherwise `RocketControllerData.ComputeFromCores` |
| Rated thrust and flow at a chosen pressure (suicide burn, stage estimate) | `ActiveEngineThrust.Compute(parts, p)` ([ActiveEngineThrust.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/ActiveEngineThrust.cs#L37-L63)), the same branch inside `SequenceAnalyzer.Analyze` | `RocketControllerData.ComputeFromCores(engine.Cores, p, 1f)` for `p > 0`, else the cached `EngineController.VacuumData`; KER takes `ThrustMax.Length()` and `MassFlowRateMax` |
| Ambient pressure and atmosphere top at an altitude | `EnvironmentHelpers.GetAtmosphericPressureAtAltitude`, `GetAtmosphereHeight` ([EnvironmentHelpers.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/EnvironmentHelpers.cs#L17-L35)) | `Astronomical.GetAtmosphereReference().Physical`: `GetAtmosphericPressureAtAltitude(double)` and `Height` (the analysis slider's maximum) |
| Gravity at an altitude | `EnvironmentHelpers.ComputeSurfaceGravity(body, alt)` ([EnvironmentHelpers.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/EnvironmentHelpers.cs#L7-L15)) | `Constants.GRAVITATIONAL_CONSTANT * IParentBody.Mass / (MeanRadius + alt)^2` |

[^ker-activeenginethrust] [^ker-envhelpers] [^ker-flighthud]

`RocketControllerData.ComputeFromCores` evaluates each core's combustion at the clamped throttle
(`RocketCore.ComputeConditions`) and each nozzle's `ComputePerformance` at the given pressure, and
returns `ThrustMax` as the vector sum over the engine's nozzles in vehicle assembly axes plus the
summed `MassFlowRateMax` ([current/decomp/KSA/RocketControllerData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketControllerData.cs#L21-L64)).
For combustor, thermal and antimatter cores the result depends only on the module configuration;
a `SolidMotor` instead solves its conditions from the tree's current mole states (calling
`PartTree.EnsureDerived(DerivedData.SolidMotorStacks)` first), so solid-motor ratings follow the
remaining grain ([current/decomp/KSA/SolidMotor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SolidMotor.cs#L316-L330)).[^ksa-rocketcontrollerdata] [^ksa-solidmotor]

TWR is always thrust over `mass * g`: in flight `g` is evaluated at the current barometric altitude
of `vehicle.Parent` and the mass is `Vehicle.TotalMass`
([FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L133-L141));
the stage tables use the analysis altitude of the home body (editor) or the current parent (flight)
([EditorPanel.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/EditorPanel.cs#L37-L43)).[^ker-editorpanel]

## Lifecycle and timing

- Everything runs inside the two UI postfixes: `Vehicle.OnDrawUi` for the flight HUD and the
  suicide-burn calculator (`frame:draw-ui`), `VehicleEditor.OnDrawUi` for the editor panel
  (`editor:draw-ui`).
- In flight the nozzle states and `PhysicsEnvironment` read from the main-thread `Vehicle` are the
  values applied this frame. The worker writes the next step's nozzle states into the
  `VehicleUpdateData` updaters, and `Universe.ApplyVehicleSolvers` copies them back through
  `Vehicle.UpdateFromTaskResultsUnsynchronized` -> `PartTree.UpdateFromTaskResults`
  ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2531),
  [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1195-L1198)), so a read during
  the draw sees one consistent step even though the next step is already running.[^ksa-vehicle] [^ksa-parttree]
- `VacuumData` and the gravity and pressure helpers touch no per-step state. `ComputeFromCores` does
  for solid motors (mole states, and a lazy `EnsureDerived` rebuild if the tree is dirty); during the
  draw that is normally a read, because `PrepareFrame` runs `PartTree.FlushDirtyDerived` before it
  dispatches the step (see the threading table in
  [Game loop and mod lifecycle](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).
  In the editor no physics runs for the craft, so all of it is plain main-thread work.

## Key code

| Path | Role |
|---|---|
| [ActiveEngineThrust.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/ActiveEngineThrust.cs) | Actual nozzle thrust and rated thrust at a pressure |
| [EnvironmentHelpers.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/EnvironmentHelpers.cs) | Gravity, atmosphere top and pressure for a body and altitude |
| [FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L130-L142) | Actual and maximum thrust, TWR and acceleration per frame |
| [SuicideBurnCalculator.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SuicideBurnCalculator.cs#L25-L30) | Rated thrust at the live pressure for the landing estimate |

[^ker-suicideburn]

## Snippets

```csharp
// ActiveEngineThrust.cs L12-L35
    public static ActualEnginePerformanceInfo ComputeActual(PartTree parts)
    {
        float totalThrust = 0f;
        float totalFlowRate = 0f;
        Span<EngineController> engines = parts.Modules.Get<EngineController>();
        for (int i = 0; i < engines.Length; i++)
        {
            EngineController engine = engines[i];
            if (!engine.IsActive)
                continue;

            foreach (RocketCore core in engine.Cores)
            {
                var nozzles = parts.RocketNozzles.GetModulesAndStates(core.Rocket.Nozzles.AsSpan()).GetEnumerator();
                while (nozzles.MoveNext())
                {
                    var nozzle = nozzles.Current;
                    totalThrust += nozzle.State.Performance.TotalThrust;
                    totalFlowRate += nozzle.State.Performance.MassFlowRate;
                }
            }
        }
        return new ActualEnginePerformanceInfo(totalThrust, totalFlowRate);
    }
```

```csharp
// EnvironmentHelpers.cs L7-L35
    public static float ComputeSurfaceGravity(IParentBody? body, double altitudeMeters = 0.0)
    {
        if (body == null)
            return 0f;
        double r = body.MeanRadius + Math.Max(0.0, altitudeMeters);
        if (r <= 0.0)
            return 0f;
        return (float)(Constants.GRAVITATIONAL_CONSTANT * body.Mass / (r * r));
    }

    public static float GetAtmosphereHeight(IParentBody? body)
    {
        if (body is not Astronomical astro)
            return 0f;

        var atmosphere = astro.GetAtmosphereReference();
        return atmosphere == null ? 0f : Math.Max(0f, (float)(double)atmosphere.Physical.Height);
    }

    public static float GetAtmosphericPressureAtAltitude(IParentBody? body, double altitudeMeters)
    {
        if (body is not Astronomical astro)
            return 0f;

        var atmosphere = astro.GetAtmosphereReference();
        return atmosphere == null
            ? 0f
            : (float)atmosphere.Physical.GetAtmosphericPressureAtAltitude(Math.Max(0.0, altitudeMeters));
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `PartTree.RocketNozzles` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L76) | Main-thread nozzle state list |
| `ModuleStateful<...>.StateList.GetModulesAndStates(ReadOnlySpan<TModule>)` | [current/decomp/KSA/ModuleStateful.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModuleStateful.cs#L290) | Pair each nozzle module with its state |
| `RocketNozzleState.Performance`, `RocketPerformance.TotalThrust` / `MassFlowRate` | [current/decomp/KSA/RocketNozzleState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketNozzleState.cs#L11), [current/decomp/KSA/RocketPerformance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketPerformance.cs#L5-L15) | Live per-nozzle thrust and flow |
| `RocketCore.Rocket`, `Rocket.Nozzles` | [current/decomp/KSA/RocketCore.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketCore.cs#L16), [current/decomp/KSA/Rocket.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Rocket.cs#L12) | Nozzles fed by a core |
| `EngineController.Cores`, `VacuumData`, `IsActive`, `ComputeActivePerformance` | [current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L36-L75) | Engine list, cached vacuum rating, lit flag, game's rated-thrust path |
| `RocketControllerData.ComputeFromCores(ReadOnlySpan<RocketCore>, float, float)` | [current/decomp/KSA/RocketControllerData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketControllerData.cs#L21-L25) | Thrust and flow at a pressure and throttle |
| `Vehicle.ComputeActiveThrust(float)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6694-L6708) | Full-throttle thrust of lit engines with propellant |
| `Vehicle.PhysicsEnvironment` / `PhysicsEnvironment.AtmosphericPressure` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L543), [current/decomp/KSA/PhysicsEnvironment.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsEnvironment.cs#L22) | Live ambient pressure |
| `Astronomical.GetAtmosphereReference()`, `PhysicalAtmosphereReference.Height` / `GetAtmosphericPressureAtAltitude(double)` | [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L392), [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L23-L80) | Pressure at an analysis altitude |
| `Constants.GRAVITATIONAL_CONSTANT` | [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L25) | `G * M / r^2` |

[^ksa-enginecontroller] [^ksa-rocketnozzlestate] [^ksa-rocketperformance] [^ksa-rocketcore] [^ksa-rocket] [^ksa-modulestateful] [^ksa-physicsenvironment] [^ksa-astronomical] [^ksa-physatmo] [^ksa-constants]

## Pitfalls and version notes

- The two "rated" paths disagree on dry engines: `Vehicle.ComputeActiveThrust` skips an engine whose
  `EngineControllerState.IsPropellantAvailable` is false, while KER's `ActiveEngineThrust.Compute`
  checks only `IsActive`, so a lit engine with empty tanks still counts toward the suicide-burn plan.
- Both rated paths, like the game's own, add `ThrustMax.Length()` per engine, which overstates the net
  thrust of engines canted against each other.
- `ComputeSurfaceGravity` uses `MeanRadius` plus altitude and no rotation term; it is the gravity at
  that radius, not the effective weight at the surface of a rotating body.
- `GetAtmosphereReference()` lives on `Astronomical`, not on `IParentBody`; for a parent that is not an
  `Astronomical`, or a body without an atmosphere, both helpers return 0 (vacuum, no slider range).
- Actual thrust lags the throttle by one physics step, because it reports the last applied nozzle
  states.

## See also

- [Flight HUD on the controlled vehicle](flight-hud-controlled-vehicle-postfix.md)
- [Suicide-burn calculator](suicide-burn-calculator.md)
- [Custom stage delta-v estimator](custom-stage-delta-v-estimator.md)
- [Editor panel injected into the vehicle editor](editor-panel-injection.md)
- [Telemetry history and CSV export](telemetry-history-and-csv-export.md)
- [Game loop and mod lifecycle](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md)

[^ker-activeenginethrust]: ActiveEngineThrust.cs
[^ker-envhelpers]: EnvironmentHelpers.cs
[^ker-flighthud]: FlightHud.cs
[^ker-suicideburn]: SuicideBurnCalculator.cs
[^ker-editorpanel]: EditorPanel.cs
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-enginecontroller]: KSA.EngineController (decompiled, build 2026.10.10.5554)
[^ksa-rocketcontrollerdata]: KSA.RocketControllerData (decompiled, build 2026.10.10.5554)
[^ksa-rocketnozzlestate]: KSA.RocketNozzleState (decompiled, build 2026.10.10.5554)
[^ksa-rocketperformance]: KSA.RocketPerformance (decompiled, build 2026.10.10.5554)
[^ksa-rocket]: KSA.Rocket (decompiled, build 2026.10.10.5554)
[^ksa-rocketcore]: KSA.RocketCore (decompiled, build 2026.10.10.5554)
[^ksa-solidmotor]: KSA.SolidMotor (decompiled, build 2026.10.10.5554)
[^ksa-parttree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
[^ksa-modulestateful]: KSA.ModuleStateful (decompiled, build 2026.10.10.5554)
[^ksa-physicsenvironment]: KSA.PhysicsEnvironment (decompiled, build 2026.10.10.5554)
[^ksa-astronomical]: KSA.Astronomical (decompiled, build 2026.10.10.5554)
[^ksa-physatmo]: KSA.PhysicalAtmosphereReference (decompiled, build 2026.10.10.5554)
[^ksa-constants]: KSA.Constants (decompiled, build 2026.10.10.5554)
