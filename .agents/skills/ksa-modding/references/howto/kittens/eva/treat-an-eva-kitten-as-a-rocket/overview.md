---
type: Game Feature
title: Fly an EVA kitten as an ordinary rocket vessel, with vessel input and the flight HUD
description: How a mod makes one chosen kitten take the vessel branches of physics, input and the HUD - flipping the IsKitten snapshot flag, publishing membership to workers, rotating the control frame and swapping the backpack RCS map - and two ways to call the base Vehicle handlers that the kitten overrides
category: kittens
group: eva
topic: treat-an-eva-kitten-as-a-rocket
demonstrated_by:
  - mods/unscience/features/iron-man-kitten-rocket-flight
  - mods/unscience/features/iron-man-equipment-render-ordering
lifecycle:
  - frame:draw-ui
  - frame:prepare
  - frame:dispatch-solvers
  - physics:vehicle-solver
  - frame:input
  - frame:render
  - render:gauges
game_types:
  - KSA.KittenEva
  - KSA.Vehicle
  - KSA.VehicleUpdateState
  - KSA.PhysicsBubble
  - KSA.FlightComputer
  - KSA.ThrusterController
  - KSA.Rocket
  - KSA.EngineController
  - KSA.GaugeCanvas
  - KSA.GaugeButtonFlightComputer
  - KSA.InputEvents
  - KSA.PartModelRenderer
tags: [kitten, eva, rocket, iron-man, harmony, transpiler, flight-computer, hud]
---
An EVA kitten with an engine, tanks and RCS fitted in the vehicle editor is a vessel with a character controller bolted on. Unscience's Iron Man mode flies it as a rocket: nothing is added to the physics, only the *branches* the game takes for one chosen kitten change. This topic is the engine map of those branches and the three ways Iron Man gets around the fact that the kitten overrides the vessel's methods: a flag flip plus patches (the whole solution), a Harmony reverse patch onto a static stub (call the base handlers), and a non-virtual IL call (call the base **and** keep every other mod's patches running). Editing the kitten's tree in the editor is a separate topic: [open the vehicle editor on a live EVA kitten](../open-the-vehicle-editor-on-a-live-eva-kitten/overview.md).

## Engine background

| Branch | Where | What a kitten does | Source |
|---|---|---|---|
| Worker flag | `VehicleUpdateState.PrepareFromVehicle` sets `IsKitten = ReadOnlyVehicle is KittenEva` | The worker takes kitten paths for locomotion, servo, wake, G-load allowance, part failure | [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L296-L306), [servo reference](../kitten-locomotion-and-servo-reference/overview.md) |
| Flight-computer inputs | `PhysicsBubble.FlightComputerInputsFor` | Thruster commands are zeroed unless in MMU mode | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L1822-L1829) |
| Key input | `KittenEva.OnKey` and `KittenEva.ProcessInput` override the vessel handlers and intercept most actions | Vessel keys never reach the base | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L110-L186), [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L187-L280) |
| Control frame | `Vehicle.Ctrl2Body` = control connector or part orientation, else identity | A kitten's body +X is its chest; the head is -Z | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L585) |
| Backpack RCS | `ThrusterController.RecomputeDynamicData` uses the authored `ManualControlMap` when set, else a map derived from geometry in the control frame | The stock backpack carries an authored EVA map | [current/decomp/KSA/ThrusterController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ThrusterController.cs#L120-L135) |
| HUD context | `GaugeCanvas.IsContextVisible`: `EVA` is `controlledVehicle is KittenEva`, `Vehicle` is `!(is KittenEva)` | Kitten HUD, not the vessel flight-computer gauges | [current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L238-L264) |
| Button policy | `GaugeButtonFlightComputer.IsDisabled` and `PackData` call `ControlledVehicle.IsFlightComputerDisabled(_enumValue)`; `KittenEva` overrides it to disable every non-kitten action | Flight-computer buttons are disabled | [current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L150-L165), [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L281-L300) |
| Part render | `UpdateRenderingResources` skips `KittenEva`; the late kitten draw calls the base `UpdateRenderData` after the part-instance upload | Equipment parts are submitted too late to be drawn | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4349-L4357) |

Mode switches must happen when the vehicle worker is idle, so Iron Man queues every one to the `PrepareFrame` handoff ([mutate vehicles at a physics safe point](../../../physics/solver/mutate-vehicles-at-a-physics-safe-point/overview.md)).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1. `IsKitten` postfix, `Ctrl2Body` postfix and queued enable and disable, plus input and HUD transpilers](treat-an-eva-kitten-as-a-rocket-1.md) | unscience | `harmony-postfix` | `frame:draw-ui`, `frame:prepare`, `frame:dispatch-solvers`, `physics:vehicle-solver`, `frame:input` | The whole solution; membership keyed by the `KittenEva` object, published to workers as an array |
| [2. Call the base Vehicle handlers through a Harmony reverse patch onto a static stub](treat-an-eva-kitten-as-a-rocket-2.md) | unscience | `harmony-reverse-patch` | `frame:input` | Stubs get a copy of the base body; fine when no other mod patches the base |
| [3. Call the base through a DynamicMethod non-virtual call so other mods' patches still run](treat-an-eva-kitten-as-a-rocket-3.md) | unscience | `dynamic-method` | `frame:render`, `frame:draw-ui` | Enters Harmony's patched replacement, so other mods' prefixes and transpilers still run |

## See also

- [Kitten locomotion, modes and the servo, from the decompiled game](../kitten-locomotion-and-servo-reference/overview.md)
- [Mutate vehicles at a physics-safe point](../../../physics/solver/mutate-vehicles-at-a-physics-safe-point/overview.md), [Write worker-thread-safe patches inside the vehicle solver](../../../physics/solver/write-worker-thread-safe-solver-patches/overview.md)
- [Open the stock vehicle editor on a live EVA kitten](../open-the-vehicle-editor-on-a-live-eva-kitten/overview.md), [Hide parts or subparts from rendering](../../../rendering/meshes/hide-parts-or-subparts-from-rendering/overview.md)
- [Share the keyboard and mouse between ImGui, mod tools and the game](../../../ui/input/coordinate-imgui-focus-with-game-input/overview.md), [Drive the flight computer and attitude modes from a mod](../../../flight-and-orbits/autopilot/drive-the-flight-computer-and-attitude-modes-from-a-mod/overview.md)
- [Isolate patch failures and fault-proof patch bodies](../../../patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md), [Coexist with other mods patching the same method](../../../patching/harmony/coexist-with-other-mods-patching-the-same-method/overview.md)
- [Game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Add to, drive and hide the stock cockpit gauges and navball](../../../ui/menus/work-with-stock-cockpit-gauges/overview.md) - related topic

