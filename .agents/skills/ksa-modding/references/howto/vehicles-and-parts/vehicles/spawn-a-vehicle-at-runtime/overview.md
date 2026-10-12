---
type: Game Feature
title: Spawning a vehicle at runtime
description: How KSA builds a Vehicle from a part tree and an orbit, and three mod recipes for creating one from a library craft, a state vector or a single hand-built part
category: vehicles-and-parts
group: vehicles
topic: spawn-a-vehicle-at-runtime
demonstrated_by:
  - mods/ksacars/features/spawn-parked-craft
  - mods/ksarmory/features/spawn-and-teleport-test-craft
  - mods/ksastructures/features/office-furniture-craft
lifecycle: [frame:after-on-frame, frame:input, frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.Part
  - KSA.PartTree
  - KSA.VehicleSave
  - KSA.KittenEva
  - KSA.Orbit
  - KSA.CelestialSystem
  - KSA.VehicleSaves
  - KSA.DefaultVehicleSaves
  - KSA.ConstraintSim
  - KSA.InputEvents
tags: [spawn, vehicle, vehicle-library, orbit, teleport, shapes-lock, part-tree]
---
A KSA `Vehicle` is a part tree plus a place to be: an `Orbit` (or a `SituationTemplate` from a launch
menu), a body-to-ecliptic rotation and body rates. The stock launch menu, the editor's launch button
and `EVA` all end in `Vehicle.CreateVehicle`; a mod that wants to put a craft into the world without the
player's help repeats the same few calls. This topic compares three recipes that share one skeleton and
differ in where the part tree and the orbit come from.

## Engine background

- `Vehicle.CreateVehicle(CelestialSystem, doubleQuat body2Cce, double3 bodyRates, IParentBody parent, string id, Part root, Orbit orbit)`
  is the public entry point; the protected constructor throws when the parent is null, when both orbit
  and situation are null, when both root part and root part instance are null, and registers the vehicle
  with the system (`CelestialSystem.Register`) unless it is a save-game shell
  [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1421-L1470)[^ksa-vehicle].
- Creation does not attach the craft to the orbiter tree. `InputEvents.CreateVehicleData.Apply` is the
  stock spawn path and shows the extra steps: `parent.Children.Add(vehicle)`, `vehicle.UpdatePerFrameData()`,
  then (stock only) control and camera follow [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L911-L940)[^ksa-inputevents]. It runs from
  `InputEvents.ApplyInputEvents`, which applies `CreateVehicleBuffer` early and `TeleportInputBuffer` late
  [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1113-L1140); that call sits in the settled window of `Program.PrepareFrame`
  (see [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).
- Building a vehicle mutates the Bepu shape registry. The vehicle step holds that registry from
  `ConstraintSim.BeginVehicleUpdate` to `EndVehicleUpdate` [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L261-L298)[^ksa-vehicleupdatetask]; plain
  `ConstraintSim.UnlockShapes` throws while it is held, `UnlockShapesBlocking` spins until it is free
  [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L111-L153)[^ksa-constraintsim]. `Vehicle.UpdateAfterPartTreeModification` takes the plain lock through
  `UpdateCollisionGeometry` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1894-L1950). See [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../change-vehicles-without-racing-the-solver/overview.md).
- Part trees come from `VehicleSave.Load(IViewport)` (nullable) [current/decomp/KSA/VehicleSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSave.cs#L1-L22)[^ksa-vehiclesave], from
  `DefaultVehicleSaves.FindSave(name)` for craft shipped in `Content/Core/defaultvehicles`
  [current/decomp/KSA/DefaultVehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DefaultVehicleSaves.cs#L24-L70)[^ksa-defaultvehiclesaves], or from the player library, which `VehicleSaves.Refresh()` rescans
  from `VehicleSaves.SaveFolderPath` [current/decomp/KSA/VehicleSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleSaves.cs#L280-L290)[^ksa-vehiclesaves]. A single part is built with
  `new Part(id, template)` and `Part.CreateOwnTree()` [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1456-L1459)[^ksa-part].
- `Vehicle.TeleportToLocation(Celestial, lat, lon)` derives a resting kinematic state from the craft's
  bounding box and queues it on `InputEvents.TeleportInputBuffer` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L4219-L4233); the
  teleport is applied on the next `ApplyInputEvents`.
- `KittenEva` is a `Vehicle` subclass with the same constructor shape plus a character id
  [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L78-L83)[^ksa-kitteneva]; spawning an EVA kitten as a feature is covered by `spawn-an-eva-kitten-from-code`.

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Parked copy of a library craft](spawn-a-vehicle-at-runtime-1.md) | KSACars | `starmap-lifecycle` | `frame:after-on-frame` | Smallest recipe; borrows the flown craft's frame for a throw-away orbit, lookup in both libraries, blocks one frame on the vehicle step |
| [Test drones from a state vector](spawn-a-vehicle-at-runtime-2.md) | KSArmory | `vehicle-spawn` | `frame:draw-ui`, `frame:after-on-frame` | Full orbit control (ballistic aim), kitten-vs-craft class choice, many guards; heaviest |
| [A one-part vehicle built in code](spawn-a-vehicle-at-runtime-3.md) | KSAStructures | `xml-asset` | `frame:after-on-frame` | No craft file at all, only a part Id; depends on the part being loadable as a root and on the part Id staying stable |

## See also

- [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for the frame order every article refers to
- [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../change-vehicles-without-racing-the-solver/overview.md), [teleport-a-vessel-by-building-a-new-orbit](../../../physics/kinematics/teleport-a-vessel-by-building-a-new-orbit/overview.md), `keep-mod-vehicles-out-of-saves`, [Ship whole-vehicle presets and single-part craft with a part mod](../../parts/ship-ready-made-craft/overview.md), `spawn-an-eva-kitten-from-code`
- Mod features: [ksacars/spawn-parked-craft](../../../../mods/ksacars/features/spawn-parked-craft.md), [ksarmory/spawn-and-teleport-test-craft](../../../../mods/ksarmory/features/spawn-and-teleport-test-craft.md), [ksastructures/office-furniture-craft](../../../../mods/ksastructures/features/office-furniture-craft.md)
- [Keep mod-only vehicles out of save files](../../../persistence/saves/keep-mod-vehicles-out-of-saves/overview.md) - related topic
- [Spawn an EVA kitten from code](../../../kittens/characters/spawn-an-eva-kitten-from-code/overview.md) - related topic

[^ksa-defaultvehiclesaves]: current/decomp/KSA/DefaultVehicleSaves.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatetask]: current/decomp/KSA/VehicleUpdateTask.cs (decompiled, build 2026.10.10.5554)
[^ksa-inputevents]: current/decomp/KSA/InputEvents.cs (decompiled, build 2026.10.10.5554)
[^ksa-kitteneva]: current/decomp/KSA/KittenEva.cs (decompiled, build 2026.10.10.5554)
[^ksa-constraintsim]: current/decomp/KSA/ConstraintSim.cs (decompiled, build 2026.10.10.5554)
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehiclesaves]: current/decomp/KSA/VehicleSaves.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehiclesave]: current/decomp/KSA/VehicleSave.cs (decompiled, build 2026.10.10.5554)
