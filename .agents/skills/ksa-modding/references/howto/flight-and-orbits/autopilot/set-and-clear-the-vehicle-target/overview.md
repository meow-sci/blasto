---
type: Game Feature
title: Set or clear a vehicle's target, including bodies the game cannot target
description: Set and clear the vehicle target from mod code through the same queued path the UI uses, and make fixed stars and barycenters selectable as a navigation-only target the stock target list refuses
category: flight-and-orbits
group: autopilot
topic: set-and-clear-the-vehicle-target
demonstrated_by: [mods/compendium/features/native-target-selection, mods/compendium/features/root-navigation-targets]
lifecycle: [frame:input, frame:draw-ui, event:target-changed, event:starmap-unload]
game_types:
  - KSA.Universe
  - KSA.InputEvents
  - KSA.Vehicle
  - KSA.OrbitingStar
  - KSA.IOrbiter
  - KSA.CelestialSystem
  - KSA.FixedStar
  - KSA.Barycenter
  - KSA.IIndependentRoot
  - KSA.NavBallData
  - KSA.BurnPlan
tags: [target, navball, set-target, change-target-data, input-events, fixed-star, barycenter]
---

The vehicle target drives the navball's TGT mode, the Toward/Away attitude modes, target nodes on the flight plan and burn plan, and the closest-approach gauges. Setting it from a mod is a one-liner, but doing it from the wrong place races the vehicle solver; and the stock target cannot be a fixed star or a barycenter at all.

## Engine background

- `Vehicle.Target` is `IOrbiter?` with a private setter. `Vehicle.SetTarget(IOrbiter? target, Part? targetPart = null)` is the only setter: it refuses self-targets and targets inside its own tree, updates `Target`, `TargetPart`, the target's back-reference (`SetTargetOf`/`RemoveTargetOf`) and recalculates target nodes on the flight plan and burn plan.[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2189-L2203)
- `Universe.SetTargetCommand(vehicleId, targetId)` (terminal action `settarget`) and `UnsetTargetCommand(celestialId)` (`unsettarget`) only validate and add an `InputEvents.ChangeTargetData { Vehicle, Target, TargetPart }` to `InputEvents.ChangeTargetBuffer`.[^ksa-universe] [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L433-L515) [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L522-L586)
  `UnsetTargetCommand` returns early when the vehicle has no target.
- `ChangeTargetData.Apply` calls `Universe.SetTarget` or `UnsetTarget` (both `vehicle.SetTarget(...)`) and shows a "target set" or "target unset" alert; `ApplyInputEvents` applies `ChangeTargetBuffer` after the burn buffer and sim-speed buffer.[^ksa-inputevents] [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L780-L801)
- `FixedStar` and `Barycenter` implement `IIndependentRoot` and `IParentBody` but not `IOrbiter`, so they cannot be a `Vehicle.Target`.[^ksa-fixedstar] [^ksa-barycenter]

**Timing.** The click happens in a draw hook (`frame:draw-ui`) while the next vehicle step is already running; changing the target directly would race the solver (the target nodes and the flight computer's navigation both read it). The command only queues, `ApplyInputEvents` runs in the next frame's `PrepareFrame`
(`frame:input`, after `ApplyVehicleSolvers`, before `ExecuteNextVehicleSolvers`), so the next step starts with the new target and the "current target" indicator appears one frame after the click. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Universe.SetTargetCommand queues ChangeTargetData](set-and-clear-the-vehicle-target-1.md) | Compendium | input event buffer | `frame:draw-ui`, `frame:input` | Exactly stock behaviour; only `IOrbiter` targets |
| [Weak tables keyed by Vehicle and BurnPlan, requests drained in a prefix](set-and-clear-the-vehicle-target-2.md) | Compendium | conditional weak table | `frame:input` | Targets stars and barycenters; session-only; needs patches |

## See also

- [Drive the flight computer and attitude modes from a mod](../drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md)
- [Synthesize a NavigationTarget for an object that has no orbit](../synthesize-a-navigation-target-for-the-flight-computer/overview.md)
- [Create, chain and read stock maneuver nodes from code](../create-and-read-maneuver-nodes/overview.md)

[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: KSA.InputEvents (decompiled, build 2026.10.10.5554)
[^ksa-fixedstar]: KSA.FixedStar (decompiled, build 2026.10.10.5554)
[^ksa-barycenter]: KSA.Barycenter (decompiled, build 2026.10.10.5554)
