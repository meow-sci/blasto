---
type: Game Feature
title: Execute burns with RCS translation and throttle inputs from code
description: Drive throttle and RCS translation inputs from a mod, execute a burn with RCS only, and trim and release several payloads along one line from a bus
category: flight-and-orbits
group: autopilot
topic: execute-burns-with-rcs-and-throttle-inputs
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/rcs-burn-execution
  - mods/ksa-advancedflightcomputer/features/rcs-thruster-allocation
  - mods/ksarmory/features/mirv-bus-trim-and-release
  - mods/gatos/features/manual-throttle-and-rcs-inputs
  - mods/ksarmory/features/fly-another-vehicle
lifecycle:
  - frame:input
  - frame:apply-solvers
  - physics:vehicle-solver
  - frame:viewports
  - frame:draw-ui
  - frame:dispatch-solvers
game_types:
  - KSA.Vehicle
  - KSA.FlightComputer
  - KSA.BurnTarget
  - KSA.BurnPlan
  - KSA.FlightComputerOutput
  - KSA.InputAction
  - KSA.ManualControlInputs
  - KSA.PhysicsBubble
  - KSA.VehicleProperties
  - KSA.Universe
tags: [rcs, throttle, translation, manual-control-inputs, process-input, burn, trim, thrusters]
---

The stock flight computer flies burns with the main engines. A mod that wants to throttle an engine, fire translation thrusters or run a burn on RCS alone has to push
into the same channels the keyboard uses, or take over the worker's pulse output. This topic compares four ways, from raw input injection to a full replacement executor.

## Engine background

| Piece | Role |
|---|---|
| `Vehicle._manualControlInputs` (`ManualControlInputs` struct) | Private. `EngineOn`, `EngineThrottle`, `ThrusterCommandFlags` (`ThrusterMapFlags`), `Sprint`, `GrabHeld`. Copied into the worker state at `PrepareWorker` |
| `Vehicle.GetManualThrottle()`, `GetThrusterFlags()`, `GetMinThrottle()` | Public readers |
| `Vehicle.ProcessInput(InputAction, GlfwKeyAction, GlfwModifier)` | The keyboard's entry: sets and clears persistent engine and thruster flag bits |
| `Vehicle.PrepareWorker(SimStep)` | Moves the throttle by `0.7 * playerDeltaTime` per frame while a throttle key bit is set; clears held input for the controlled vehicle in three cases; zeroes `EngineOn` while `BurnMode` is Auto |
| `FlightComputer.ComputeControl` | Worker: turns inputs into `FlightComputerOutput`; `ComputeRcsControl` reads `ThrusterCommandFlags`, cancels opposing commands and zeroes them unless `RCSMode == Enabled`; an attitude hold strips the rotation bits |
| `ThrusterControllerState.CommandPulseTime` | What a thruster fires this step |
| `PhysicsBubble` | Takes a craft off rails whenever any actuator is commanded (`FlightComputerOutput.AnyActuatorCommanded`) |

Sources: [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2396-L2423), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3536-L3560), [current/decomp/KSA/ManualControlInputs.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ManualControlInputs.cs), [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1439-L1456).[^ksa-thrustermapflags]
The window to write these is the one in [Reach the physics worker through the references FlightComputer.CopyFrom shares](../../../../mods/ksa-advancedflightcomputer/features/flight-computer-worker-copy-channels.md): between `ApplyVehicleSolvers` and the vehicle's `PrepareWorker`, because the
copy-back overwrites flight-computer fields. `_manualControlInputs` is the exception: the apply never writes it, so a write from any main-thread hook is picked up by the next `PrepareWorker`.
See [Mutate simulation state safely](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md) and the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Intercept Auto and drive an RCS state machine](execute-burns-with-rcs-and-throttle-inputs-1.md) | KSA-AdvancedFlightComputer | harmony prefix | `frame:apply-solvers`, `physics:vehicle-solver` | A complete RCS burn that ends like a stock burn; the most code |
| [One-direction-at-a-time trim](execute-burns-with-rcs-and-throttle-inputs-2.md) | KSArmory | closed-loop trim | `frame:draw-ui`, `frame:dispatch-solvers` | Small corrections against an actuator of unknown authority; must go quiet to return to rails |
| [Reflection write into `_manualControlInputs`](execute-burns-with-rcs-and-throttle-inputs-3.md) | gatOS | reflection | `frame:viewports` | Exact values; private-field coupling |
| [ProcessInput injection in the PrepareWorker window](execute-burns-with-rcs-and-throttle-inputs-4.md) | KSArmory | input injection | `frame:dispatch-solvers` | Same path as a key press; a servo, not an assignment |

## See also

- [Execute planned burns hands-free with the stock Auto burn](../execute-planned-burns-with-the-stock-autopilot/overview.md)
- [Run a closed-loop autopilot for every vehicle each step and arbitrate control with the player](../run-an-autopilot-for-every-vehicle-each-step/overview.md)
- [Drive the flight computer and attitude modes from a mod](../drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md)
- Related topics: [Predict impact points with drag and build a ballistic computer](../../guidance/predict-impact-point-and-build-a-ballistic-computer/overview.md)

[^ksa-thrustermapflags]: KSA.ThrusterMapFlags (decompiled, build 2026.10.10.5554)
