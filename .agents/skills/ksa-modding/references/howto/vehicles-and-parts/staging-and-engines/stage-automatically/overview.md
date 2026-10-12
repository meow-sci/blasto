---
type: Game Feature
title: Staging automatically when engines burn out, spent stages ride along, or a delay is wanted
description: How a mod can decide when to stage around the vehicle solver apply, keep an Auto burn alive across the new engines' first tick, drop spent stages early and hold decouplers and engines back by sim time
category: vehicles-and-parts
group: staging-and-engines
topic: stage-automatically
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/automatic-staging
  - mods/ksa-advancedflightcomputer/features/spent-stage-drop
  - mods/ksa-advancedflightcomputer/features/staging-delays
lifecycle: [frame:apply-solvers, frame:input, frame:dispatch-solvers, frame:draw-ui]
game_types:
  - KSA.SequenceList
  - KSA.Sequence
  - KSA.Part
  - KSA.ISequenced
  - KSA.EngineController
  - KSA.Decoupler
  - KSA.FlightComputer
  - KSA.BurnTarget
  - KSA.Universe
  - KSA.Vehicle
tags: [staging, auto-burn, burnout, jettison, delays, harmony]
---
Stock KSA stages only when the player asks. Staging on burnout looks trivial ("when the engines are dry, activate the next
sequence") but three engine facts make a naive version break an Auto burn, so every approach here is a layer of the same
state machine in KSA-AdvancedFlightComputer (AFC).

## Engine background

- **Where it can run safely.** `Program.PrepareFrame` joins the vehicle workers, calls `Universe.ApplyVehicleSolvers` (results
  copied to the main thread), then polls input, runs `InputEvents.ApplyInputEvents` and finally
  `FlightComputer.RaisePendingAlerts` for each vehicle [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2163-L2181)[^ksa-program] [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2186-L2196). A postfix on
  `ApplyVehicleSolvers` is therefore a settled main-thread point with no worker running (`frame:apply-solvers`), and anything it
  queues into the input buffers is applied later in the same frame.
- **The stock burnout stop.** In `FlightComputer.ComputeControl`, an Auto burn whose active engines all report no propellant is
  dropped to Manual and sets `AutoBurnStoppedOutOfPropellant`; two denied ignitions in a row do the same without the flag
  [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L498-L513)[^ksa-flightcomputer]. `RaisePendingAlerts` then clears the flag and ends the burn [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L542-L573).
  A freshly lit engine reads "no propellant" until the first worker pass over it, so staging an Auto burn looks like a burnout
  unless the stop is taken back or the engine is seeded as fueled.
- **What staging does.** `SequenceList.ActivateNextSequence` marks the row activated, calls `ActivateSubtreeInStage` per part
  in reverse and removes spent rows [current/decomp/KSA/SequenceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequenceList.cs#L171-L206)[^ksa-sequencelist]; decouplers only queue an `IActivateInputData`
  [current/decomp/KSA/Decoupler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Decoupler.cs#L149-L161)[^ksa-decoupler] (see [Ignite engines, activate the next stage, fire a decoupler or undock from code](../stage-a-vehicle-from-code/overview.md)).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: burnout, requested and cascade staging with a per-vehicle state machine](stage-automatically-1.md) | KSA-AdvancedFlightComputer | `harmony-prefix`, `harmony-postfix` | `frame:apply-solvers` | the baseline; must defend Auto burns |
| [2: drop spent stages while still under thrust](stage-automatically-2.md) | KSA-AdvancedFlightComputer | `jettison-prediction` | `frame:apply-solvers` | needs a model of `Vehicle.Split`; several refusal gates |
| [3: delays between trigger and decoupler or engine](stage-automatically-3.md) | KSA-AdvancedFlightComputer | `sequence-reimplementation`, `toml-config` | `frame:apply-solvers`, `frame:draw-ui` | reimplements stock activation with reflection |

## See also

- [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md)
- Calling stage entry points from code: [Ignite engines, activate the next stage, fire a decoupler or undock from code](../stage-a-vehicle-from-code/overview.md)
- Reading staged engine data: [Read staged delta-v, thrust and Isp from the game or from your own stage model](../read-staged-delta-v-from-the-game/overview.md), [Compute engine thrust, Isp and mass flow at ambient pressure and invert for throttle](../compute-engine-thrust-at-ambient-pressure/overview.md)
- Mod features: [ksa-advancedflightcomputer/automatic-staging](../../../../mods/ksa-advancedflightcomputer/features/automatic-staging.md), [ksa-advancedflightcomputer/spent-stage-drop](../../../../mods/ksa-advancedflightcomputer/features/spent-stage-drop.md), [ksa-advancedflightcomputer/staging-delays](../../../../mods/ksa-advancedflightcomputer/features/staging-delays.md)

[^ksa-flightcomputer]: current/decomp/KSA/FlightComputer.cs (decompiled, build 2026.10.10.5554)
[^ksa-decoupler]: current/decomp/KSA/Decoupler.cs (decompiled, build 2026.10.10.5554)
[^ksa-sequencelist]: current/decomp/KSA/SequenceList.cs (decompiled, build 2026.10.10.5554)
[^ksa-program]: current/decomp/KSA/Program.cs (decompiled, build 2026.10.10.5554)
