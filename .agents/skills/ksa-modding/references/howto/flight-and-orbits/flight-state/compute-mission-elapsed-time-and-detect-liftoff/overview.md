---
type: Game Feature
title: Show mission elapsed time from actual liftoff and pick the right clock under time warp
description: Start a mission clock at real liftoff instead of scene load, and choose between universe time, the applied simulation step and wall clock so timers, schedulers and cooldowns behave under time warp
category: flight-and-orbits
group: flight-state
topic: compute-mission-elapsed-time-and-detect-liftoff
demonstrated_by:
  - mods/ksa-public-telemetry/features/mission-clock-and-liftoff-detection
  - mods/ksarmory/features/consume-applied-sim-step
  - mods/gatos/features/timed-command-scheduler
lifecycle: [frame:after-on-frame, frame:draw-ui, frame:celestials, frame:viewports]
game_types:
  - KSA.Vehicle
  - KSA.UniverseTime
  - KSA.Universe
  - KSA.CelestialSystem
  - KSA.LookupCollection
  - KSA.SimSpeed
  - KSA.Program
tags: [clock, mission-elapsed-time, liftoff, time-warp, sim-step, universe-time, scheduler]
---

Two separate problems share this topic. A mission clock needs an epoch (when did this rocket actually leave
the pad?) and a time source that follows the game's own time. A timer or scheduler needs a clock whose behaviour
under pause and time warp matches what it is timing. KSA offers four clocks and each is right for some uses.

## Engine background

| Clock | Source | Pause | Time warp | Typical use |
|---|---|---|---|---|
| Universe time | `Universe.GetElapsedTime()` / `GetElapsedSeconds()` = `_lastSimStep.NextTime` | stops | follows | mission elapsed time, orbital events, anything tied to the world |
| Applied step | `Universe.GetLastSimStep()` (`SimStep.PreviousTime`, `NextTime`, `DeltaTime`) | zero step | scaled | integrating mod-simulated objects across exactly the interval the engine simulated |
| Player delta | `dtPlayer` given to StarMap hooks, clamped to `1 / MinTargetFrameRate` | runs | ignores | animation, cinematics |
| Wall clock | a `Stopwatch` | runs | ignores | real-time cooldowns, rate limits |

`Universe.ApplyVehicleSolvers` sets `_lastSimStep = _nextSimStep` right after applying the vehicle results, so
`GetElapsedTime()` is the epoch of every vehicle and celestial sample read afterwards. The step is sized in
the previous frame's `PrepareFrame`: `GetJobSimStep` returns `DeltaTime = dtPlayer * achievedSpeedFraction * SimulationSpeed`
starting at `_lastSimStep.NextTime`, so consecutive steps are contiguous.[^ksa-universe] [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1952) [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2381-L2402)
[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2615-L2627) `Universe.IsPaused()` tests the simulation speed against exactly zero; `GetSimulationSpeed()` and
`GetAchivedSpeedFraction()` expose the speed and the fraction the machine actually achieved.[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1799-L1802)
[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2311-L2319) `dtPlayer` is the wall delta clamped to `1 / MinTargetFrameRate`.[^ksa-app] [current/decomp/KSA/App.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/App.cs#L49-L53)

For the liftoff epoch: `Vehicle.LaunchGameTime` is set to `Universe.GetElapsedTime()` in the vehicle constructor
(vehicle creation, not liftoff) and copied to the new vehicle by `Vehicle.Split`, together with `HasLaunched`, so
every piece of one rocket carries the same value.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1436) [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1666-L1667)

See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md): all three
mods read these clocks from main-thread hooks after `PrepareFrame`, where `GetElapsedTime()` is the epoch of the state they read.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [T-/T+ clock keyed on LaunchGameTime](compute-mission-elapsed-time-and-detect-liftoff-1.md) | KSA-Public-Telemetry | starmap lifecycle | `frame:after-on-frame` | Real liftoff, survives separation and reload; needs persistence |
| [Advance on the applied sim step](compute-mission-elapsed-time-and-detect-liftoff-2.md) | KSArmory | sim clock | `frame:draw-ui`, `frame:celestials` | Pause and warp correct by construction; two-frame delay on speed changes |
| [Render, wall and sim clocks for timed playback](compute-mission-elapsed-time-and-detect-liftoff-3.md) | gatOS | command queue | `frame:viewports` | Choose per schedule; the clamped player delta does not catch up |

## See also

- [Set, limit and request simulation speed from a mod](../set-simulation-speed-from-a-mod/overview.md)
- [Read a vehicle's position, velocity, attitude, orbit and flight instruments](../read-vehicle-flight-state/overview.md)
- [Create, chain and read stock maneuver nodes from code](../../autopilot/create-and-read-maneuver-nodes/overview.md) for burn times on the same clock
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-app]: KSA.App (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
