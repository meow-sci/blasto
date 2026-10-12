---
type: Game Feature
title: Set, limit and request simulation speed from a mod
description: Change the time-warp rate from code, request a warp to a time through the game's own auto-warp buffer, and clamp the speed during phases that cannot tolerate warp
category: flight-and-orbits
group: flight-state
topic: set-simulation-speed-from-a-mod
demonstrated_by:
  - mods/gatos/features/debug-cheats-teleport-refuel-warp
  - mods/ksa-advancedflightcomputer/features/launch-window-to-target-plane
  - mods/ksarmory/features/timewarp-policy-during-engagements
lifecycle: [frame:prepare, frame:input, frame:viewports, frame:draw-ui, frame:after-on-frame]
game_types: [KSA.Universe, KSA.SimSpeed, KSA.Program, KSA.InputEvents]
tags: [time-warp, simulation-speed, auto-warp, input-events, control-loop]
---

Time warp in KSA is one number, the simulation speed, that scales how far each physics step advances the world. A mod can write
it directly, ask the game to warp to a time, or hold it down while it needs fine steps. The right choice depends on who else may
write the speed in the same frame: the player's warp keys, the stock auto-warp and the menu pause all do.

## Engine background

| Piece | What it does |
|---|---|
| `Universe.SetSimulationSpeed(SimSpeed)` | assigns `_simulationSpeed` directly |
| `Universe.SetSimulationSpeed(double, bool alert = true)` | assigns the speed; with `alert` true shows a yellow timed alert when it changes |
| `Universe.GetSimulationSpeed()`, `Universe.SimulationSpeed`, `IsPaused()` | read it back (`IsPaused` tests exactly zero) |
| `InputEvents.SimSpeedBuffer` | player warp keys and the `simspeed` console command queue `SimSpeedData` here |
| `InputEvents.AutoWarpBuffer`, `AutoWarpData` | request a warp to a `UniverseTime` (`StopWarp`, `WarpToTime`, `SimTimeMargin`, `IsUncapped`) |
| `Universe.AutoWarpTo`, `AutoWarpStop(bool resetSimulationSpeed)`, `IsAutoWarpActive` | the stock auto-warp |
| `Universe.ProcessAutoWarp(dtPlayer)` | called in `PrepareFrame`; while auto-warp is active it overwrites the speed every frame |

Sources: [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L700-L704), [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2208-L2262), [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2288-L2296),
[current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L854-L874), [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1004-L1012).

**Order in `PrepareFrame`** (build 2026.10.10.5554): `ApplyVehicleSolvers`, then input (`ApplyInputEvents` drains `SimSpeedBuffer` and
`AutoWarpBuffer`), then `UpdateMenuPause`/the editor pause, then `Universe.ProcessAutoWarp(dtPlayer)`, then `GetJobSimStep(dtPlayer)` sizes the
next step with the speed as it stands, then `ExecuteNextVehicleSolvers`.[^ksa-program] [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2200-L2220)
Consequences: a speed written from a frame hook is read by the next frame's `GetJobSimStep`, unless `ProcessAutoWarp` or the menu pause
overwrites it first; and the step you see applied lags the request by two frames (see
[Show mission elapsed time from actual liftoff and pick the right clock under time warp](../compute-mission-elapsed-time-and-detect-liftoff/overview.md)). See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Universe.SetSimulationSpeed(f, alert: false)](set-simulation-speed-from-a-mod-1.md) | gatOS | command queue | `frame:viewports` | One call; no auto-warp guard, no alert |
| [AutoWarpBuffer request](set-simulation-speed-from-a-mod-2.md) | KSA-AdvancedFlightComputer | input buffer | `frame:draw-ui` then `frame:input` | Stock warp UI and limits apply; time target, not a speed |
| [Feedback controller with yield and restore](set-simulation-speed-from-a-mod-3.md) | KSArmory | control loop | `frame:draw-ui` | Fine steps only while needed; complex, two-frame actuator delay |

## See also

- [Show mission elapsed time from actual liftoff and pick the right clock under time warp](../compute-mission-elapsed-time-and-detect-liftoff/overview.md)
- [Build a local site frame and compute a launch window into a target plane](../../transfers/build-a-site-frame-and-compute-a-launch-window/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
