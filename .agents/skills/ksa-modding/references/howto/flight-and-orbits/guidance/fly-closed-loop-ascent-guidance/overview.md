---
type: Game Feature
title: Fly a closed-loop ascent to a target orbit
description: Guide a launch to a target orbit with closed-loop ascent guidance, optionally seeded by a propellant-optimal pre-planned trajectory
category: flight-and-orbits
group: guidance
topic: fly-closed-loop-ascent-guidance
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/upfg-ascent-guidance
  - mods/ksa-advancedflightcomputer/features/convex-ascent-scvx
lifecycle: [frame:dispatch-solvers, frame:draw-ui, thread:background]
game_types:
  - KSA.SequencePerformanceList
  - KSA.SequencePerformance
  - KSA.PartTree
  - KSA.SolidMotor
  - KSA.Vehicle
  - KSA.FlightComputer
  - KSA.Orbit
tags: [ascent, launch, upfg, scvx, staging, guidance, target-orbit]
---
KSA flies a launch only when the player steers; there is no stock ascent autopilot. To fly one from a mod you need three things the game does provide: a stage-by-stage model of the vehicle, the live ambient conditions, and a place to run guidance every step that can write the attitude. Both approaches below come from KSA-AdvancedFlightComputer (AFC) and are meant to be used together: the optimal plan flies the dense atmosphere, UPFG finishes the insertion.

## Engine background

- **Staging simulator.** `PartTree.PerformanceSequences` (a `SequencePerformanceList`) holds one `SequencePerformance` per stage-menu entry with a `WetMass` and constant-thrust `Phases` (`SequencePhaseInfo`: `Thrust`, `MassFlowRate`, `Duration`, `DeltaV`, `ActiveEngineCount`). `RecomputeForFlight(float ambientPressure)` refreshes it and `TotalDeltaV` reads the total.[^ksa-parttree] [^ksa-sequenceperformancelist] [^ksa-sequenceperformance]
- **Solid motors.** `SolidMotor` carries grain segments and thrust profiles; the simulator paces them at the mean thrust and flow of the burn they have left.[^ksa-solidmotor]
- **Frames.** Inertial state is `Vehicle.Orbit.StateVectors.PositionCci` / `VelocityCci`. Orbit inclination and LAN are measured against the parent body's pole (CCI +Z), not the ecliptic; air-relative velocity is inertial velocity minus omega cross r with omega about CCI +Z. See [Understand KSA coordinate frames and epochs](../../flight-state/understand-ksa-coordinate-frames-and-epochs/overview.md).
- **Where to run it.** `Universe.PrepareVehicleWorkers` calls `Vehicle.PrepareWorker` on the main thread for every vehicle right before the next physics step is dispatched; both approaches hook there.[^ksa-universe] See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [UPFG with a stage list from SequencePerformance phases](fly-closed-loop-ascent-guidance-1.md) | KSA-AdvancedFlightComputer | custom physics, harmony prefix | `frame:dispatch-solvers`, `frame:draw-ui` | Closed loop, cheap (one solve per second), fixed gravity-turn start; live ambient pressure only for the burning stage |
| [SCvx with Clarabel on its own thread, immutable plan](fly-closed-loop-ascent-guidance-2.md) | KSA-AdvancedFlightComputer | background thread | `frame:dispatch-solvers`, `thread:background` | Minimum propellant for the actual vehicle with drag and max-q; open loop, seconds to solve, needs a drag table and thread discipline |

## See also

- [Compute a suicide-burn start altitude and the thrust it needs](../compute-a-suicide-burn/overview.md) for the engine model both approaches use
- [Run an expensive solve without stalling the frame](../run-an-expensive-solve-without-stalling-the-frame/overview.md)
- [Plan and fly a powered landing](../plan-and-fly-a-powered-landing/overview.md)
- [Build a site frame and compute a launch window](../../transfers/build-a-site-frame-and-compute-a-launch-window/overview.md)
- [Reading staged delta-v, thrust and Isp from the game or from your own stage model](../../../vehicles-and-parts/staging-and-engines/read-staged-delta-v-from-the-game/overview.md)

[^ksa-parttree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
[^ksa-sequenceperformancelist]: KSA.SequencePerformanceList (decompiled, build 2026.10.10.5554)
[^ksa-sequenceperformance]: KSA.SequencePerformance (decompiled, build 2026.10.10.5554)
[^ksa-solidmotor]: KSA.SolidMotor (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
