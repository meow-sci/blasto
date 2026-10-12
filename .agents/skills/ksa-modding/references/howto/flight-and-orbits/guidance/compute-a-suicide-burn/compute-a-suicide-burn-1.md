---
type: How-To
title: Closed-form suicide-burn calculator on radar altitude
description: Compute the radar altitude at which a full-thrust landing burn must start, its duration and the countdown to it from live ambient-pressure thrust, mass flow and averaged gravity
category: flight-and-orbits
group: guidance
topic: compute-a-suicide-burn
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/kitten-engineer-redux/features/suicide-burn-calculator]
techniques: [flight-math, harmony-postfix]
hooks: [harmony-postfix KSA.Vehicle.OnDrawUi]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.IParentBody
  - KSA.AtmosphericBody
  - KSA.Astronomical
  - KSA.PhysicsEnvironment
  - KSA.EngineController
  - KSA.RocketControllerData
tags: [landing, suicide-burn, hoverslam, radar-altitude, thrust, gravity]
---
Outcome: a per-frame estimate of the radar altitude at which a descending craft must light its engines to reach zero speed at the ground, how long that burn lasts, and how many seconds remain before it. It is a closed-form estimate, not a simulation, and it is the quickest of the three ways to get a landing number out of the game. Kitten-Engineer-Redux ships it in its flight HUD.[Suicide-burn calculator](../../../../mods/kitten-engineer-redux/features/suicide-burn-calculator.md)
Prerequisites: StarMap (to load the mod) and Harmony (to hook `Vehicle.OnDrawUi`); the maths needs no other dependency.

## Background: how the game does it

The game has no suicide-burn readout, so you assemble four live inputs from the main-thread vehicle.[^ksa-vehicle]

| Input | Game member | Notes |
|---|---|---|
| Height above ground | `Vehicle.GetRadarAltitude()` | Radius minus (mean radius + terrain or ocean height under the craft); falls back to `GetBarometricAltitude()` when the parent is not a `Celestial` |
| Surface speed | `Vehicle.GetSurfaceSpeed()` | Length of the surface-relative velocity |
| Mass | `Vehicle.TotalMass` | `float`, from `_props.TotalMassPropsAsmb.Props.Mass` |
| Ambient pressure | `Vehicle.PhysicsEnvironment.AtmosphericPressure` | `float` Pa at the craft; 0 in vacuum |

Thrust comes from the engine model: `EngineController.IsActive` engines, evaluated by `RocketControllerData.ComputeFromCores(cores, ambientPressure, throttle)`, or the cached `EngineController.VacuumData` when the pressure is zero.[^ksa-enginecontroller] See the radar altitude in [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3065-L3090) and `ComputeActivePerformance` in [current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L68-L75).

## Lifecycle and timing

- **Where:** a Harmony postfix on `Vehicle.OnDrawUi` (`frame:draw-ui`, main thread), filtered to the main viewport and the controlled vehicle ([Patch_FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/Patch_FlightHud.cs)).
- **Why it is safe:** the calculator only reads state the previous `Apply*Solvers` step committed (orbit state vectors, module states, `PhysicsEnvironment`), and writes nothing. The next vehicle step is already running on workers, so never write simulated state from here; see the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).
- **Freshness:** the numbers are one frame old and the HUD is skipped when the game UI is hidden, so do not feed a burn controller from this hook (use the guidance step in [approach 3](compute-a-suicide-burn-3.md) for that).

## Steps

1. **Hook the draw call.** `[HarmonyPatch(typeof(Vehicle), nameof(Vehicle.OnDrawUi))]`, a postfix that returns unless `inViewport == Program.MainViewport` and `Program.ControlledVehicle == __instance`.
2. **Read the geometry and mass.**

```csharp
// SuicideBurnCalculator.cs L18-L27
        double radarAltitude = vehicle.GetRadarAltitude();
        double verticalVelocity = orbit.VerticalVelocity;
        double surfaceSpeed = vehicle.GetSurfaceSpeed();
        
        bool isDescending = verticalVelocity < 0.0;
        double descentRate = -verticalVelocity;

        float ambientPressure = (float)vehicle.PhysicsEnvironment.AtmosphericPressure;
        ActiveEngineThrustInfo engines = ActiveEngineThrust.Compute(vehicle.Parts, ambientPressure);
        double massInitial = vehicle.TotalMass;
```

   `orbit.VerticalVelocity` is the mod's own `OrbitSummary` value (surface-relative velocity projected on the radial direction); compute it yourself as the dot product of the surface velocity with the unit position vector.
3. **Sum active-engine thrust and mass flow at the live pressure.** Full throttle, active engines only:

```csharp
// ActiveEngineThrust.cs L37-L63
    public static ActiveEngineThrustInfo Compute(PartTree parts, float ambientPressure)
    {
        float totalThrust = 0f;
        float totalFlowRate = 0f;

        Span<EngineController> engines = parts.Modules.Get<EngineController>();
        for (int i = 0; i < engines.Length; i++)
        {
            EngineController engine = engines[i];
            if (!engine.IsActive)
                continue;

            if (ambientPressure > 0f)
            {
                var data = RocketControllerData.ComputeFromCores(engine.Cores.AsSpan(), ambientPressure, 1f);
                totalThrust += data.ThrustMax.Length();
                totalFlowRate += data.MassFlowRateMax;
            }
            else
            {
                totalThrust += engine.VacuumData.ThrustMax.Length();
                totalFlowRate += engine.VacuumData.MassFlowRateMax;
            }
        }

        return new ActiveEngineThrustInfo(totalThrust, totalFlowRate);
    }
```

4. **Average gravity between here and the ground.** The mod averages `mu / r^2` at the current radius and at `IParentBody.GetNearSurfaceRadius()` (see the gotcha below before copying it).
5. **Solve for the burn.** Duration with a first-order propellant-loss correction, end mass, average deceleration, stopping distance projected on the vertical:

```csharp
// SuicideBurnCalculator.cs L44-L63
        double thrustToMassRatio = engines.TotalThrust / massInitial;
        double burnDuration = surfaceSpeed / (thrustToMassRatio + (0.5 * massFlowRate * surfaceSpeed / massInitial) - localGravity);

        if (burnDuration < 0.0 || double.IsNaN(burnDuration))
            return new SuicideBurnInfo(false, isDescending, false, 0f, null, 0f);

        double pitchAngle = surfaceSpeed > 0.0 ? System.Math.Asin(descentRate / surfaceSpeed) : System.Math.PI * 0.5;
        
        double massFinal = massInitial - (massFlowRate * burnDuration);
        if (massFinal < 0.1) massFinal = 0.1; 

        double averageThrustAccel = engines.TotalThrust / ((massInitial + massFinal) * 0.5);
        double netDecelDynamic = averageThrustAccel - (localGravity * System.Math.Sin(pitchAngle));

        if (netDecelDynamic <= 0.0)
            return new SuicideBurnInfo(false, isDescending, false, 0f, null, 0f);

        double burnDistance = (surfaceSpeed * surfaceSpeed) / (2.0 * netDecelDynamic);
        double burnAltitude = burnDistance * System.Math.Sin(pitchAngle);
        bool burnNow = radarAltitude <= burnAltitude;
```

6. **Report.** `BurnNow` once `radarAltitude <= burnAltitude`; otherwise, when descending, the time to burn is `(radarAltitude - burnAltitude) / descentRate`. Abort with "insufficient thrust" when thrust, flow or mass is not positive or thrust-to-mass does not exceed gravity.

## Complete example

| File | Role |
|---|---|
| [SuicideBurnCalculator.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/SuicideBurnCalculator.cs#L16-L79) | the estimate and its result record |
| [ActiveEngineThrust.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/ActiveEngineThrust.cs#L37-L63) | active-engine thrust and flow at a pressure |
| [FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/FlightHud.cs#L105-L106) | per-frame call, right after the orbit summary |
| [Patch_FlightHud.cs](https://github.com/BarneyCompiler/Kitten-Engineer-Redux/blob/d5786a0e75203a18a96936fb3537220d44b7a18b/Patch_FlightHud.cs) | the `Vehicle.OnDrawUi` postfix |

## Variations and gotchas

- **Do not copy `GetNearSurfaceRadius()` as the ground radius.** On an `AtmosphericBody` with an atmosphere it returns `MeanRadius + atmosphere height + 1`, and elsewhere the base `Astronomical` version (the highest terrain point plus one).[^ksa-atmosphericbody] [^ksa-astronomical] On an atmospheric body the "surface" gravity sample is taken at the top of the atmosphere, so the averaged `g` is too low and the burn starts late. Use `MeanRadius` (plus the terrain height under the craft) for the lower sample.
- **Rated thrust, not delivered thrust.** The function counts any `IsActive` engine at full throttle; it does not check `IsPropellantAvailable`, so an active engine with empty tanks still counts. With engines off it reports insufficient thrust rather than planning for the next stage. The hovering-engine variants in [approach 2](compute-a-suicide-burn-2.md) and [approach 3](compute-a-suicide-burn-3.md) check supply.
- **Optimistic countdown.** `timeToBurn` divides the remaining height by the current descent rate and ignores gravity speeding the craft up during the coast.
- **Straight-line model.** Thrust is assumed to oppose velocity; there is no gravity-turn or horizontal braking, and no throttle floor or g-load cap (see [approach 3](compute-a-suicide-burn-3.md) for the cap).
- **Pressure is a float.** `PhysicsEnvironment.AtmosphericPressure` is a `float`; the pressure drops as the craft descends, so thrust at the burn altitude is slightly higher in air than the current-altitude reading in the coast phase. Re-evaluating every frame makes this self-correcting.[^ksa-physicsenvironment]

## See also

- [Overview](overview.md); [Sum ComputeActivePerformance per active controller](compute-a-suicide-burn-2.md); [Core-model evaluation with throttle inversion](compute-a-suicide-burn-3.md)
- [Suicide-burn calculator (mod feature)](../../../../mods/kitten-engineer-redux/features/suicide-burn-calculator.md)
- [Plan and fly a powered landing](../plan-and-fly-a-powered-landing/overview.md) for a closed-loop descent that uses the same engine model
- [Read vehicle flight state](../../flight-state/read-vehicle-flight-state/overview.md) for the speed, altitude and frame accessors used here

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-enginecontroller]: KSA.EngineController (decompiled, build 2026.10.10.5554)
[^ksa-atmosphericbody]: KSA.AtmosphericBody (decompiled, build 2026.10.10.5554)
[^ksa-astronomical]: KSA.Astronomical (decompiled, build 2026.10.10.5554)
[^ksa-physicsenvironment]: KSA.PhysicsEnvironment (decompiled, build 2026.10.10.5554)
