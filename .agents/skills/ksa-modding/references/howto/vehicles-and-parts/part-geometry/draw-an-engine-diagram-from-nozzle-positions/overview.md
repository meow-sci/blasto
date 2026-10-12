---
type: Game Feature
title: Engine layout diagrams from nozzle positions and live engine state
description: How to enumerate a stage's engines with their states, locate their nozzles in vehicle space and lay them out as a top-down diagram
category: vehicles-and-parts
group: part-geometry
topic: draw-an-engine-diagram-from-nozzle-positions
demonstrated_by: [mods/ksa-public-telemetry/features/engine-diagram-from-nozzle-positions]
lifecycle: [frame:after-on-frame, frame:draw-ui, frame:viewports, frame:input]
game_types:
  - KSA.PartTree
  - KSA.ModuleList
  - KSA.ModuleStateful
  - KSA.EngineController
  - KSA.SequenceList
  - KSA.Tank
  - KSA.Part
  - KSA.RocketCore
  - KSA.RocketNozzle
  - KSA.Vehicle
  - KSA.Mole
  - KSA.MoleState
  - KSA.SolidMotor
tags: [engines, nozzles, diagram, propellant, telemetry, imgui]
---
A rocket stage is a set of `EngineController` modules, each driving one or more `RocketCore`s whose `RocketNozzle`s know where they sit in their part's own frame. A diagram of "which engines
are lit and where they are" therefore needs three things: the list of engines for the stage you care about, their live state, and each nozzle's position in the vehicle's frame. All of it is readable on the
main thread from the vehicle's `PartTree`; nothing needs the render camera.

## Engine background

- **Modules and states.** `PartTree.States` and the typed state lists (`RocketCores`, `Moles`, `Tanks`) hold module data as struct-of-arrays; `ModuleStateful<EngineController, EngineControllerState, EngineControllerGlobalState, EmptyStruct>.TryGetFrom(parts.States, ...)`
  yields the engine controllers with their states [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L56-L74)[^ksa-parttree] [current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L7)[^ksa-enginecontroller]. The full read recipe is in [Reading live part module state](../../modules-and-resources/read-live-part-module-state/overview.md).
- **Stages.** `PartTree.SequenceList` has `ActiveSequence` (the current staging sequence number) [current/decomp/KSA/SequenceList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SequenceList.cs#L131)[^ksa-sequencelist]; `EngineController.Sequence` and `IsActive` say which stage an engine belongs to and whether it is on
  [current/decomp/KSA/EngineController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EngineController.cs#L36-L46).
- **Cores and nozzles.** `EngineController.Cores` is a `RocketCore[]`; `RocketCore.Rocket.Nozzles` is a `RocketNozzle[]` [current/decomp/KSA/RocketCore.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketCore.cs#L16)[^ksa-rocketcore] [current/decomp/KSA/Rocket.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Rocket.cs#L12)[^ksa-rocket]. A `SolidMotor` is a `RocketCore` [current/decomp/KSA/SolidMotor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SolidMotor.cs#L9)[^ksa-solidmotor], so
  solid boosters appear in the same enumeration. `RocketNozzle.LocationAsmb` is the nozzle position in its part's assembly space and `FxExitRadius` is its exit radius [current/decomp/KSA/RocketNozzle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketNozzle.cs#L24)[^ksa-rocketnozzle].
- **Nozzle in vehicle space.** The game itself computes `ThrustLocationVehicleAsmb = LocationAsmb.Transform(float4x4.Pack(Parent.MatrixAsmb2VehicleAsmb))` in `RocketNozzle.ResetState` and rotates about the gimbal pivot first in `UpdateState`
  [current/decomp/KSA/RocketNozzle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketNozzle.cs#L277-L316). A mod that only wants the layout can use the first form directly.
- **Propellant.** `Tank.Moles` is a `List<Mole>`; the mass is `parts.Moles.States[mole.StatesIdx].Mass` and the capacity `mole.GetStoredMass(mole.ContainerVolume)` [current/decomp/KSA/Tank.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Tank.cs#L78-L81)[^ksa-tank] [current/decomp/KSA/Mole.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mole.cs#L53-L60)[^ksa-mole].

## Approaches

| Article | Mod | Technique | Subject |
|---|---|---|---|
| [Draw an engine diagram](draw-an-engine-diagram-from-nozzle-positions.md) | KSA-Public-Telemetry | `part-tree-modules` | active-stage engines, status, nozzle positions normalised to a unit disc, tank propellant fraction, ImGui draw-list dots and arc |

## See also

- [Game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Reading live part module state](../../modules-and-resources/read-live-part-module-state/overview.md), [Part meshes, attach nodes and part-local points](../get-part-points-in-vehicle-and-world-frames/overview.md), [Keep an anchor on a part through staging, docking, editor changes and saves](../anchor-to-parts-across-vehicle-changes/overview.md)
- [Switch engines on and off, set throttle and RCS commands](../../modules-and-resources/command-engines-throttle-and-rcs-from-code/overview.md), [Refill, drain or top up a vehicle's propellant and batteries](../../modules-and-resources/refill-or-drain-resources/overview.md)
- [Ray cast against part meshes and terrain](../../../physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md)
- Mod feature: [engine-diagram-from-nozzle-positions](../../../../mods/ksa-public-telemetry/features/engine-diagram-from-nozzle-positions.md)

[^ksa-parttree]: current/decomp/KSA/PartTree.cs (decompiled, build 2026.10.10.5554)
[^ksa-enginecontroller]: current/decomp/KSA/EngineController.cs (decompiled, build 2026.10.10.5554)
[^ksa-sequencelist]: current/decomp/KSA/SequenceList.cs (decompiled, build 2026.10.10.5554)
[^ksa-rocketcore]: current/decomp/KSA/RocketCore.cs (decompiled, build 2026.10.10.5554)
[^ksa-rocket]: current/decomp/KSA/Rocket.cs (decompiled, build 2026.10.10.5554)
[^ksa-solidmotor]: current/decomp/KSA/SolidMotor.cs (decompiled, build 2026.10.10.5554)
[^ksa-rocketnozzle]: current/decomp/KSA/RocketNozzle.cs (decompiled, build 2026.10.10.5554)
[^ksa-tank]: current/decomp/KSA/Tank.cs (decompiled, build 2026.10.10.5554)
[^ksa-mole]: current/decomp/KSA/Mole.cs (decompiled, build 2026.10.10.5554)
