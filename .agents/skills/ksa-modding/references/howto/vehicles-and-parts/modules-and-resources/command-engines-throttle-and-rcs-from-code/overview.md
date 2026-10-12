---
type: Game Feature
title: Commanding engines, throttle and RCS from code
description: How KSA turns key presses into engine and thruster commands, the input-buffer and private-field routes a mod can use, and four recipes up to a custom RCS allocator
category: vehicles-and-parts
group: modules-and-resources
topic: command-engines-throttle-and-rcs-from-code
demonstrated_by:
  - mods/gatos/features/ignition-staging-and-module-actuators
  - mods/gatos/features/manual-throttle-and-rcs-inputs
  - mods/unscience/features/blinky-scroll-animation-and-patterns
  - mods/ksa-advancedflightcomputer/features/rcs-thruster-allocation
lifecycle:
  - frame:viewports
  - frame:draw-ui
  - frame:dispatch-solvers
  - frame:input
  - frame:apply-solvers
  - physics:vehicle-solver
game_types:
  - KSA.EngineController
  - KSA.ManualControlInputs
  - KSA.ThrusterMapFlags
  - KSA.Vehicle
  - KSA.InputEvents
  - KSA.ThrusterController
  - KSA.FlightComputer
  - KSA.ThrusterControllerState
tags: [engines, throttle, rcs, thrusters, input-events, manual-control, reflection]
---
There are four distinct levers on a vehicle's propulsion, and KSA exposes each differently: the main ignition flag, a
per-engine active flag, the manual throttle and thruster command flags, and the per-thruster pulse the flight computer
computes on the worker. Which one a mod should use depends on whether it wants to act like a player, like the staging
system, or like the flight computer.

## Engine background

- **Main ignition and the manual inputs.** `Vehicle.SetEnum(VehicleEngine.MainIgnite)` sets
  `_manualControlInputs.EngineOn` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6321-L6407)[^ksa-vehicle]. `_manualControlInputs` is a private
  `ManualControlInputs` struct (`EngineOn`, `EngineThrottle`, `ThrusterCommandFlags`, `Sprint`, `GrabHeld`)
  [current/decomp/KSA/ManualControlInputs.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ManualControlInputs.cs#L1-L18)[^ksa-manualcontrolinputs]; `Vehicle.PrepareWorker` clamps `EngineThrottle` to `[GetMinThrottle(), 1]` and
  copies the struct into the worker snapshot [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423). There is no public throttle setter;
  `GetManualThrottle()` reads the field [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1259-L1262).
- **Per-engine activation uses a buffer.** `EngineController.SetIsActive(vehicle, bool)` does not mutate state: it adds
  an `InputEvents.IActivateInputData` to `IActivateInputBuffer` [current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L77-L87)[^ksa-enginecontroller], which
  `ApplyInputEvents` drains in the settled window [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1046-L1112)[^ksa-inputevents] [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1113-L1140).
  `ThrusterController` and `Decoupler` follow the same pattern.
- **Thruster commands.** `ThrusterMapFlags` is a bit set of rotation and translation bits [current/decomp/KSA/ThrusterMapFlags.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ThrusterMapFlags.cs#L1-L21)[^ksa-thrustermapflags];
  `FlightComputer.ComputeRcsControl` reads `inputs.ThrusterCommandFlags`, cancels opposing bits, zeroes everything
  when `RCSMode != Enabled`, and strips rotation bits under an active attitude hold
  [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L742-L819)[^ksa-flightcomputer]. `SelectJetsToFire` chooses jets and writes
  `ThrusterControllerState.CommandPulseTime` per thruster [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L1585) [current/decomp/KSA/ThrusterControllerState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ThrusterControllerState.cs#L24)[^ksa-thrustercontrollerstate].

## Approaches

| Article | Mod | Technique | Lever |
|---|---|---|---|
| [1: activation through input-event buffers](command-engines-throttle-and-rcs-from-code-1.md) | gatOS | `input-event-buffer` | `Vehicle.SetEnum`, `SetIsActive`, `MinimumThrottle` |
| [2: boxed-struct reflection write](command-engines-throttle-and-rcs-from-code-2.md) | gatOS | `reflection` | manual throttle and thruster flags |
| [3: animating a grid of engines](command-engines-throttle-and-rcs-from-code-3.md) | unscience | `input-event-buffer` | many `SetIsActive` calls, diffed |
| [4: choosing which RCS jets fire](command-engines-throttle-and-rcs-from-code-4.md) | KSA-AdvancedFlightComputer | `custom-physics` | wrench table, group or LP allocation on the worker |

## See also

- [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md), [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-4.md)
- [Ignite engines, activate the next stage, fire a decoupler or undock from code](../../staging-and-engines/stage-a-vehicle-from-code/overview.md), [Create and attach parts to a live vessel and feed runtime engines from tanks](../add-parts-to-a-live-vessel/overview.md), [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../../vehicles/change-vehicles-without-racing-the-solver/overview.md)
- Mod features: [gatos/ignition-staging-and-module-actuators](../../../../mods/gatos/features/ignition-staging-and-module-actuators.md), [gatos/manual-throttle-and-rcs-inputs](../../../../mods/gatos/features/manual-throttle-and-rcs-inputs.md), [unscience/blinky-scroll-animation-and-patterns](../../../../mods/unscience/features/blinky-scroll-animation-and-patterns.md), [ksa-advancedflightcomputer/rcs-thruster-allocation](../../../../mods/ksa-advancedflightcomputer/features/rcs-thruster-allocation.md)

[^ksa-manualcontrolinputs]: current/decomp/KSA/ManualControlInputs.cs (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: current/decomp/KSA/InputEvents.cs (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: current/decomp/KSA/FlightComputer.cs (decompiled, build 2026.10.10.5554)
[^ksa-enginecontroller]: current/decomp/KSA/EngineController.cs (decompiled, build 2026.10.10.5554)
[^ksa-thrustercontrollerstate]: current/decomp/KSA/ThrusterControllerState.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksa-thrustermapflags]: current/decomp/KSA/ThrusterMapFlags.cs (decompiled, build 2026.10.10.5554)
