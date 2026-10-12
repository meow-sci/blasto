---
type: Game Feature
title: Compute a suicide-burn start altitude and the thrust it needs
description: Work out when to light the engines for a just-in-time landing burn from live thrust, mass flow and gravity, and invert a thrust demand into a throttle setting
category: flight-and-orbits
group: guidance
topic: compute-a-suicide-burn
demonstrated_by:
  - mods/kitten-engineer-redux/features/suicide-burn-calculator
  - mods/ksa-public-telemetry/features/live-flight-telemetry-sampling
  - mods/ksa-advancedflightcomputer/features/engine-performance-and-throttle-inversion
lifecycle: [frame:draw-ui, frame:after-on-frame, frame:dispatch-solvers]
game_types:
  - KSA.Vehicle
  - KSA.IParentBody
  - KSA.AtmosphericBody
  - KSA.Astronomical
  - KSA.PhysicsEnvironment
  - KSA.EngineController
  - KSA.ActiveEnginePerformance
  - KSA.RocketControllerData
  - KSA.FlightComputer
  - KSA.SolidMotor
tags: [landing, suicide-burn, hoverslam, thrust, throttle, engine-performance, g-load]
---
A suicide (hoverslam) burn lights the engines as late as possible so the craft reaches zero speed exactly at the ground. KSA ships no readout for it, so a mod needs three things from the game: the height above the terrain, the thrust and mass flow the engines will really deliver at the current ambient pressure, and the gravity the burn must beat. The topic also covers the inverse problem a guidance loop has: given the thrust it wants, which throttle produces it.

## Engine background

- **Height and speed.** `Vehicle.GetRadarAltitude()` is the radius minus (mean radius + terrain or ocean height under the craft); `GetSurfaceSpeed()` is the surface-relative speed; `TotalMass` is the current mass.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3065-L3090)
- **Engine model.** `EngineController` carries `Cores`, a cached `VacuumData` and `SeaLevelData`, and `ComputeActivePerformance(in state, centerOfMassAsmb, ambientPressure, throttle)`, which returns zero for inactive or unsupplied controllers.[^ksa-enginecontroller] The per-core maths lives in `RocketControllerData.ComputeFromCores` (`ThrustMax`, `MassFlowRateMax`, `MinimumThrottle`).[^ksa-rocketcontrollerdata]
- **Flight computer.** `FlightComputer.ActiveEnginePerformanceMax` is an `ActiveEnginePerformance` (`Thrust`, `MassFlowRate`, `MinThrottle`, `ExhaustVelocity`),[^ksa-activeengineperformance] and the private `SolveGLoadThrottleCap` limits Manual throttle to 90 percent of the structural g-load limit.[^ksa-flightcomputer]
- **Not the ground radius.** `IParentBody.GetNearSurfaceRadius()` on an `AtmosphericBody` with an atmosphere is `MeanRadius + atmosphere height + 1`.[^ksa-atmosphericbody]
- **Timing.** All three approaches read main-thread vehicle state committed by the last `ApplyVehicleSolvers`; only the third writes a throttle, and it does so in the `Vehicle.PrepareWorker` prefix, the last settled point before the next step. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Closed-form calculator on radar altitude](compute-a-suicide-burn-1.md) | Kitten-Engineer-Redux | flight math | `frame:draw-ui` | Complete burn-altitude answer; full-throttle rated thrust, averaged gravity, no caps |
| [Sum ComputeActivePerformance per active controller](compute-a-suicide-burn-2.md) | KSA-Public-Telemetry | snapshot copy | `frame:after-on-frame` | Simplest live thrust and T/W; current throttle, not capability |
| [Core-model evaluation with throttle inversion](compute-a-suicide-burn-3.md) | KSA-AdvancedFlightComputer | engine-model reuse | `frame:dispatch-solvers` | Thrust at any throttle and pressure, throttle for a demand, saturation status, g-load cap; most code |

## See also

- [Plan and fly a powered landing](../plan-and-fly-a-powered-landing/overview.md) and [Fly a closed-loop ascent to a target orbit](../fly-closed-loop-ascent-guidance/overview.md)
- [Read vehicle flight state](../../flight-state/read-vehicle-flight-state/overview.md)
- [Reading staged delta-v, thrust and Isp from the game or from your own stage model](../../../vehicles-and-parts/staging-and-engines/read-staged-delta-v-from-the-game/overview.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-enginecontroller]: KSA.EngineController (decompiled, build 2026.10.10.5554)
[^ksa-rocketcontrollerdata]: KSA.RocketControllerData (decompiled, build 2026.10.10.5554)
[^ksa-activeengineperformance]: KSA.ActiveEnginePerformance (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-atmosphericbody]: KSA.AtmosphericBody (decompiled, build 2026.10.10.5554)
