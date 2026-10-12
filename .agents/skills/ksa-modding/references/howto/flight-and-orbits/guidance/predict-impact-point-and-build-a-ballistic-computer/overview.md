---
type: Game Feature
title: Predict impact points with drag and build a ballistic computer
description: Predict where a falling vehicle will hit the ground, measure lift and drag under the active aero model, and compute the velocity to be gained for a powered flight to a ballistic target
category: flight-and-orbits
group: guidance
topic: predict-impact-point-and-build-a-ballistic-computer
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/boostback-impact-prediction
  - mods/ksa-advancedflightcomputer/features/drag-table-measurement
  - mods/ksarmory/features/guided-bomb-tail-kit-and-sight
  - mods/ksarmory/features/icbm-ballistic-computer
lifecycle: [frame:dispatch-solvers, frame:draw-ui, frame:celestials]
game_types:
  - KSA.Vehicle
  - KSA.PhysicsStates
  - KSA.Celestial
  - KSA.IParentBody
  - KSA.Program
  - KSA.PhysicsEnvironment
  - KSA.CelestialFrameMath
  - KSA.FlightComputer
  - KSA.ActiveEnginePerformance
  - KSA.StructuralLoad
tags: [impact-point, drag, ballistics, lambert, velocity-to-be-gained, aero-measurement, rotating-body]
---
KSA gives a modder the state of a falling craft but no landing prediction: there is no stock "impact marker". Three problems recur once you build one: the atmosphere turns with the planet, so drag must act on the air-relative velocity; the drag model is whatever the game (or an aero mod) says it is, so it should be measured and not re-implemented; and the ground the craft lands on is itself moving, so a point has to be carried back to a body-fixed place before it can be compared with a target. The topic also covers the next step, a ballistic computer that turns a landing site into the velocity a powered vehicle still has to gain.

## Engine background

- **State to predict from.** The vehicle's orbit state vectors are in the parent's inertial frame (`Vehicle.Orbit.StateVectors.PositionCci` and `VelocityCci`).[^ksa-orbit] [^ksa-vehicle] The parent body spins about CCI +Z only: `CelestialFrameMath.ComputeCcf2Cci` is a rotation about `UnitZ`, and `IParentBody.GetAngularVelocityCci` is `(0, 0, GetAngularVelocity())`.[^ksa-celestialframemath] [^ksa-iparentbody] CCI +Z (the parent pole) is also the axis that inclination and longitude of ascending node are measured against in this corpus, so a predictor written in CCI needs a scalar spin rate and no tilt term.
- **The game's drag.** `PhysicsStates.ComputeAirVelocityBody` subtracts `cross(parent.GetAngularVelocityCci(), position)` from the bubble velocity when the bubble frame is CCI, so drag acts on air-relative velocity.[^ksa-physicsstates] The static `PhysicsStates.ComputeDerivatives(in BubbleOrigin, in KinematicStates, in VehicleProperties, in PhysicsEnvironment, double baseIntraStepTime, double dt, ...)` returns a `Disturbances` value, which makes it callable on copies of a vehicle's state.[^ksa-physicsstates] `Vehicle` exposes those structs as `ref readonly` properties.[^ksa-vehicle]
- **Ground.** `Celestial.GetTerrainHeightFromDirCcf` answers the terrain height under a body-fixed direction, and `IParentBody.Mu`, `MeanRadius` and `GetCci2Ccf` give the constants and the frame change.[^ksa-celestial] [^ksa-iparentbody]
- **Engine figures** for the ballistic computer come from `FlightComputer.ActiveEnginePerformanceMax`, `Vehicle.TotalMass` and `StructuralLoad.MaxGLoad`.[^ksa-flightcomputer] [^ksa-structuralload]
- **Timing.** Every approach reads main-thread vehicle state that the last `ApplyVehicleSolvers` committed, and writes no simulated state itself. The AFC predictor runs in the `Vehicle.PrepareWorker` prefix and in panel overlays; the KSArmory sight and computer run from `StarMapAfterGui`, with a `Program.OnFrameCelestials` prefix keeping them alive while the HUD is hidden. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Gravity plus drag on air-relative velocity with a measured drag table](predict-impact-point-and-build-a-ballistic-computer-1.md) | KSA-AdvancedFlightComputer | custom physics, automatic differentiation | `frame:dispatch-solvers`, `frame:draw-ui` | Matches the active aero model and gives a Jacobian for targeting; needs a sweep and an atmosphere mirror |
| [Throwaway copy flown in a ground-carried frame](predict-impact-point-and-build-a-ballistic-computer-2.md) | KSArmory | ground-carried frame | `frame:draw-ui`, `frame:celestials` | Same code as the real round, so the sight cannot disagree with the flight; costs thousands of steps per solve, rate-limited |
| [Flown impact check against a closed-form solution](predict-impact-point-and-build-a-ballistic-computer-3.md) | KSArmory | Lambert plus flown check | `frame:draw-ui`, `frame:dispatch-solvers` | Closed-form aim with a flown self-check; velocity-to-be-gained steering needs a cutoff countdown |

## See also

- [Fly a boostback burn and glide a booster to its landing site](../fly-a-boostback-and-glide-to-a-landing-site/overview.md) uses the first approach to close the loop
- [Steer by predicted miss](../steer-by-zero-effort-miss/overview.md) uses a predicted impact as its guidance input
- [Read flight state in a local horizon frame](../../flight-state/read-flight-state-in-a-local-horizon-frame/overview.md)
- [Aim a transfer at a flyby periapsis](../../transfers/aim-a-transfer-at-a-flyby-periapsis/overview.md)
- [Execute burns with RCS and throttle inputs](../../autopilot/execute-burns-with-rcs-and-throttle-inputs/overview.md)

[^ksa-orbit]: KSA.Orbit (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-celestialframemath]: KSA.CelestialFrameMath (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: KSA.FlightComputer (decompiled, build 2026.10.10.5554)
[^ksa-structuralload]: KSA.StructuralLoad (decompiled, build 2026.10.10.5554)
