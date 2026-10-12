---
type: Game Feature
title: Taking control of another vehicle
description: How KSA decides which vehicle the player flies, and two ways for a mod to fly another craft, by switching Program.ControlledVehicle or by commanding the craft's flight computer from a PrepareWorker prefix
category: vehicles-and-parts
group: vehicles
topic: take-control-of-another-vehicle
demonstrated_by:
  - mods/ksastructures/features/computer-flies-nearest-craft
  - mods/ksarmory/features/fly-another-vehicle
  - mods/ksarmory/features/prepareworker-prefix-command-window
lifecycle: [frame:draw-ui, frame:viewports, event:key, frame:dispatch-solvers]
game_types:
  - KSA.Program
  - KSA.Vehicle
  - KSA.ViewportRegistry
  - KSA.KittenEva
  - KSA.FlightComputer
  - KSA.ManualControlInputs
tags: [control, controlled-vehicle, autopilot, flight-computer, viewport, keys]
---
One vehicle at a time is "the controlled vehicle": `Program.ControlledVehicle`. Keys go to it, the HUD
and gauges read it, and its `PrepareWorker` copies the held input into the worker. A mod can change which
vehicle that is, or leave the player's choice alone and fly another vehicle through the same channels the
keyboard and the flight computer use.

## Engine background

- `Program.ControlledVehicle` is a static property; its setter only calls `ClearHeldPlayerInput()` on the
  outgoing vehicle and stores the new one [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502-L514)[^ksa-program]. It does not move any camera.
- Keys reach the vehicle in `Program.DispatchKeyEvent`: `if (IsControlledVehicleActive)` it calls
  `ControlledVehicle.OnKey(keyEvent)` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1985-L2000); `Vehicle.OnKey` and
  `Vehicle.ProcessInput(InputAction, GlfwKeyAction, GlfwModifier)` set persistent engine and thruster flag
  bits [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3274)[^ksa-vehicle] [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3536).
- `Vehicle.PrepareWorker` reads those flags into `_manualControlInputs` and snapshots the flight computer
  for the worker; see [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../change-vehicles-without-racing-the-solver/overview.md) article 5.
- Spare camera windows are leased through `ViewportRegistry.TryOpenSecondaryViewport` and given back with
  `ReleaseSecondaryViewport` [current/decomp/KSA/ViewportRegistry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportRegistry.cs#L180-L245)[^ksa-viewportregistry].
- `FlightComputer` public fields (`AttitudeMode`, `AttitudeFrame`, `AttitudeTrackTarget`,
  `CustomAttitudeTarget`, `BurnMode`) and `SetManualThrustMode` / `SetAttitudeProfile` are the autopilot
  surface [current/decomp/KSA/FlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlightComputer.cs#L32-L40)[^ksa-flightcomputer].

## Approaches

| Article | Mod | Technique | Trade-off |
|---|---|---|---|
| [1: set `ControlledVehicle`, borrow a window, restore the main view](take-control-of-another-vehicle-1.md) | KSAStructures | `viewport-borrow` | The player really flies the other craft with normal keys; you must record and restore the main view and the craft you left |
| [2: custom attitude target and `ProcessInput` from a `PrepareWorker` prefix](take-control-of-another-vehicle-2.md) | KSArmory | `double-buffer-aware-writes` | The player keeps their own craft; you fly the other one with no UI, but every write must land in the snapshot window |

## See also

- [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md)
- [Add crew seats and EVA doors to a part](../../parts/add-crew-seats-and-eva-doors/overview.md), [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../change-vehicles-without-racing-the-solver/overview.md), [Switch engines on and off, set throttle and RCS commands, and choose which RCS jets fire](../../modules-and-resources/command-engines-throttle-and-rcs-from-code/overview.md)
- Mod features: [ksastructures/computer-flies-nearest-craft](../../../../mods/ksastructures/features/computer-flies-nearest-craft.md), [ksarmory/fly-another-vehicle](../../../../mods/ksarmory/features/fly-another-vehicle.md), [ksarmory/prepareworker-prefix-command-window](../../../../mods/ksarmory/features/prepareworker-prefix-command-window.md)

[^ksa-viewportregistry]: current/decomp/KSA/ViewportRegistry.cs (decompiled, build 2026.10.10.5554)
[^ksa-flightcomputer]: current/decomp/KSA/FlightComputer.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksa-program]: current/decomp/KSA/Program.cs (decompiled, build 2026.10.10.5554)
