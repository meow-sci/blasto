---
type: Mod Feature
title: UPFG ascent to orbit on KSA's staging model
description: A double-precision port of Unified Powered Flight Guidance flown once per guidance second, fed with a stage list built from the game's own SequencePerformance phases at live ambient pressure, with gravity turn, launch window, co-elliptic chase target, g-limit and staging through AutoStage
mod: ksa-advancedflightcomputer
category: flight-and-orbits
techniques: [harmony-prefix, custom-physics]
hooks: [harmony-prefix KSA.Vehicle.PrepareWorker]
lifecycle: [frame:dispatch-solvers, frame:draw-ui]
game_types:
  - KSA.SequencePerformanceList
  - KSA.SequencePerformance
  - KSA.PartTree
  - KSA.SolidMotor
  - KSA.Vehicle
  - KSA.FlightComputer
  - KSA.Orbit
tags: [ascent, upfg, guidance, staging-model, launch-window, gravity-turn]
---

The Ascent tab flies a vehicle from the pad to a target periapsis, apoapsis, inclination and LAN,
optionally holding the argument of periapsis, with an optional launch window, g-limit and booster
reserve; picking a target vehicle flies a co-elliptic chase orbit below it. The steering is UPFG, the
Space Shuttle's closed-loop ascent guidance, ported from PEGAS (the project credits PEGAS by Noiredd
as the reference). The game-specific work is the vehicle model UPFG needs, which AFC builds from the
game's own staging simulator.

## How it works

### Phases

Vertical rise to the turn-start altitude; a pitch-over at a fixed rate toward the launch azimuth until
the commanded pitch meets UPFG's (or a failsafe altitude); closed-loop UPFG; at `tgo <= 10 s` a terminal
phase freezes the attitude and counts down to cutoff ([AdvancedFlightComputer/Features/Guidance/Modes/Ascent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Ascent.cs#L1-L60)).

### One UPFG solve per guidance second

UPFG is a once-per-cycle recursive algorithm (its `rbias` term integrates across cycles). Called every
physics step it winds up 60 times too fast and feeds wobble to the flight computer; AFC paces solves to
`GuidanceCycle = 1.0` simulated seconds while the phase machine and the attitude command still run
every step ([AdvancedFlightComputer/Features/Guidance/Modes/Ascent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Ascent.cs#L424-L432)). UPFG itself
(`UpfgGuidance`, `CseRoutine` for conic state extrapolation, `UpfgTarget`) takes plain inertial CCI
inputs and has no game dependency.

### Insertion point

With the argument of periapsis free, `UpfgInsertionSearch` measures where on the target ellipse the
insertion costs least instead of assuming periapsis (a vehicle still climbing pays to arrive level). With
it fixed, the aimed true anomaly follows the predicted cutoff and is frozen 45 s before cutoff to stop a
divergence the author observed in harness flights ([AdvancedFlightComputer/Features/Guidance/Upfg/UpfgGuidance.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Upfg/UpfgGuidance.cs#L1-L40)).

### Stage list from the game's staging simulator

- `PartTree.PerformanceSequences` is the game's `SequencePerformanceList` behind the stage menu's
  delta-v and TWR readout. `RefreshStageModel` calls `RecomputeForFlight(ambientPressure)` on it from
  the `PrepareWorker` prefix, about four times a second on the wall clock (so warp does not multiply it),
  and builds the UPFG model with `KsaVehicleAdapter.Build`; a failure keeps the previous snapshot
  ([AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs#L447-L495)).
- The adapter reads `SequencePerformance.Phases` from index 0, not the headline thrust and Isp: a
  sequence is split into constant-thrust phases wherever the number of burning engines changes (solid
  boosters burning out under a core), which maps one-to-one onto UPFG stages. Solid motors are paced by
  the game at the mean thrust and flow of the burn they have left (from
  `SolidMotor.VacuumThrustProfile`/`AtmosphericThrustProfile`). Only the burning stage is scaled to the
  craft's ambient pressure; dust phases under 1 m/s are dropped; the game's `TotalDeltaV` is latched
  so the panel can cross-check the stage table ([AdvancedFlightComputer/Features/Guidance/Upfg/KsaVehicleAdapter.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Upfg/KsaVehicleAdapter.cs#L1-L40)).

### Limits, chase target and staging

- G-limit and the structural load cap come from `EnginePerf` (see
  [attitude and gimbal actuation](guidance-attitude-and-gimbal-actuation.md)).
- `ChaseOrbit` computes the launch-to-target geometry (plane reachability from the launch latitude,
  co-elliptic orbit below the target) and the launch window. A LAN seeded from the pad is placed over
  where the pad will be 180 s later, so the plane is right for the part of the flight that matters.
- Staging is AutoStage's: guidance arms it once per flight and requests a row only for cues the plan
  knows (cold ignition, the reserve boundary); a stage that separates with a command pod can get its own
  landing site (see [boostback and impact prediction](boostback-impact-prediction.md))
  ([AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs#L109-L200)).

## Lifecycle and timing

The whole ascent runs in the per-vehicle `Vehicle.PrepareWorker` prefix (`frame:dispatch-solvers`, main
thread; see [guidance vehicle step loop](guidance-vehicle-step-loop.md)). The author chose this point for
the stage model because it can recompute `PerformanceSequences` without racing the game's own recompute
on its worker. The Ascent tab and overlays draw in `frame:draw-ui`.

## Key code

| Path | Role |
|---|---|
| [AdvancedFlightComputer/Features/Guidance/Modes/Ascent.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/Ascent.cs) | Phase machine, solve pacing, attitude command |
| [AdvancedFlightComputer/Features/Guidance/Upfg/UpfgGuidance.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Upfg/UpfgGuidance.cs) | UPFG port |
| [AdvancedFlightComputer/Features/Guidance/Upfg/CseRoutine.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Upfg/CseRoutine.cs) | Conic state extrapolation |
| [AdvancedFlightComputer/Features/Guidance/Upfg/UpfgInsertionSearch.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Upfg/UpfgInsertionSearch.cs) | Cheapest free insertion |
| [AdvancedFlightComputer/Features/Guidance/Upfg/KsaVehicleAdapter.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Upfg/KsaVehicleAdapter.cs) | Stage list from `SequencePerformance.Phases` |
| [AdvancedFlightComputer/Features/Guidance/Modes/ChaseOrbit.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/ChaseOrbit.cs) | Target-vehicle chase orbit and window |
| [AdvancedFlightComputer/Features/Guidance/Modes/GravityTurn.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/Guidance/Modes/GravityTurn.cs) | Turn shaping |
| [tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceAscentStagingTest.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/tests/AdvancedFlightComputer.HarnessTests/Features/Guidance/GuidanceAscentStagingTest.cs) | Headless ascent-with-staging test |

## Snippets

```csharp
// AdvancedFlightComputer/Features/Guidance/Modes/Autopilot.cs L464-L470
            // This pressure moves only the headline figures of the active sequence, not the phases Build reads, so Build scales the burning stage itself.
            SequencePerformanceList performance = vehicle.Parts?.PerformanceSequences;
            if (performance == null || vehicle.Parts.SequenceList == null)
                return;
            float ambientPressure = vehicle.PhysicsEnvironment.AtmosphericPressure;
            performance.RecomputeForFlight(ambientPressure);
            _s.StageModel = KsaVehicleAdapter.Build(vehicle, ambientPressure);
```

## Game APIs touched

| Member | Use |
|---|---|
| `PartTree.PerformanceSequences` ([current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L58)) | The game's staging simulator |
| `SequencePerformanceList.RecomputeForFlight(float)`, `TotalDeltaV` ([current/decomp/KSA/SequencePerformanceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePerformanceList.cs#L103)) | Recompute at ambient pressure, cross-check |
| `SequencePerformance.Phases` ([current/decomp/KSA/SequencePerformance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequencePerformance.cs#L31)) | Constant-thrust phases |
| `SolidMotor.VacuumThrustProfile`, `AtmosphericThrustProfile` ([current/decomp/KSA/SolidMotor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SolidMotor.cs#L188-L190)) | Grain-paced solid thrust (read by the game's sim) |
| `Vehicle.PhysicsEnvironment.AtmosphericPressure` | Ambient pressure for the burning stage |

## Pitfalls and version notes

- Never combine the headline `Thrust`/`MassFlowRate` (live throttled values for the active sequence) with
  `DeltaV` (design-condition drain simulation).
- Run recursive guidance at its design cadence, not per physics step.
- The drain simulation uses each sequence's own Environment setting, not the pressure passed to
  `RecomputeForFlight`; a sequence the player set to sea level is flagged in the UI.
- Of the guidance modes, the author reports in-game flights only for single-craft ascent.

## See also

- [Convex ascent with SCvx](convex-ascent-scvx.md)
- [Automatic staging](automatic-staging.md)
- [Guidance attitude and gimbal actuation](guidance-attitude-and-gimbal-actuation.md)
- [Guidance panel and overlays](guidance-panel-and-overlays.md)
- [Fly a UPFG ascent using a stage list built from the game's SequencePerformance phases](../../../howto/flight-and-orbits/guidance/fly-closed-loop-ascent-guidance/fly-closed-loop-ascent-guidance-1.md) - how-to article demonstrating this feature

