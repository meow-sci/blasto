---
type: Game Feature
title: Adding crew seats and EVA doors to a part
description: How IVASeat and EVADoor modules are declared in part XML, paired by SeatId and used for boarding, and two mods' seat and door definitions
category: vehicles-and-parts
group: parts
topic: add-crew-seats-and-eva-doors
demonstrated_by: [mods/ksacars/features/seats-and-eva-doors, mods/ksastructures/features/office-furniture-craft]
lifecycle: [startup:asset-load, frame:render]
game_types: [KSA.IVASeat, KSA.EVADoor, KSA.EVADoorTemplate, KSA.KittenEva, KSA.Vehicle, KSA.PartTemplate]
tags: [seat, eva-door, iva, crew, kitten, boarding]
---
Kittens sit only in a seat that belongs to a part, and leave a craft only through an EVA door. Both are plain
XML module templates, so a part mod can carry crew with no code. The two pieces are a seat (an `IVASeat` in the
part's game data) and a door (an `EVADoor` on a mesh-less hidden subpart), paired by the door's `SeatId`.

## Engine background

- **`IVASeat`** is a module whose template is `<IVASeat Id=...>` with `<Position>` (the kitten's **eye**
  point in part space), `<ForwardAxis>` (default `X=1`) and `<UpAxis>` (default `Z=-1`)
  [current/decomp/KSA/IVASeat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVASeat.cs#L9-L27)[^ksa-ivaseat]. It is registered under `PartTemplate.Components` by its XML type name (see
  [Define a part with its own game data and GLB model](../define-a-part-with-game-data/overview.md)).
- **Seated placement.** The seated kitten is drawn at `GetSeatedModelPointAsmb(SeatedOffset)`; the default
  `KittenLocomotionTuning.SeatedOffset` is `(0, 0, -0.63)`, so the model origin sits 0.63 m below the eye along
  the seat's up [current/decomp/KSA/IVASeat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVASeat.cs#L50-L56) [current/decomp/KSA/KittenLocomotionTuning.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenLocomotionTuning.cs#L220)[^ksa-kittenlocomotiontuning].
- **`EVADoor`** carries `SeatId`, creates its module from the template, and `ResolveAlignedSeats` pairs each
  door with the `IVASeat` whose `TemplateId` equals `SeatId` [current/decomp/KSA/EVADoor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EVADoor.cs#L33-L61)[^ksa-evadoor] (template: [current/decomp/KSA/EVADoorTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EVADoorTemplate.cs#L5-L12)[^ksa-evadoortemplate].
- **Boarding.** A kitten on EVA can board only when the door subpart's origin is within 1 m:
  `KittenEva.CanBoardDoor` = `DoorHasSeat && DoorOffsetCce(host, door).Length() < 1.0`
  [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L888-L891)[^ksa-kitteneva]. The stock UI queues `InputEvents.EvaSpawnBuffer` / `KittenBoardBuffer`, drained in
  `ApplyInputEvents` [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L1113-L1140)[^ksa-inputevents].
- **Drawing.** Seated crew are drawn by `Vehicle.UpdateSeatedCrewRenderData`, and only when the vehicle is
  `Program.ControlledVehicle` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3728-L3741)[^ksa-vehicle].

## Approaches

| Article | Mod | What it shows |
|---|---|---|
| [1: seats in PartGameData, doors on hidden subparts](add-crew-seats-and-eva-doors-1.md) | KSACars | multi-seat cars, eye offsets, door placement, one door per front seat |
| [2: the minimum viable seat and door](add-crew-seats-and-eva-doors-2.md) | KSAStructures | one seat and one door on a chair, one-part craft |

## See also

- [Define a part with its own game data and GLB model](../define-a-part-with-game-data/overview.md), `spawn-an-eva-kitten-from-code`, [Take control of another vehicle: switch the controlled craft or autopilot it from a mod](../../vehicles/take-control-of-another-vehicle/overview.md), `animate-and-draw-seated-kitten-crew` (drawing seated crew on craft that are not being flown)
- Mod features: [ksacars/seats-and-eva-doors](../../../../mods/ksacars/features/seats-and-eva-doors.md), [ksastructures/office-furniture-craft](../../../../mods/ksastructures/features/office-furniture-craft.md)
- [Spawn an EVA kitten from code](../../../kittens/characters/spawn-an-eva-kitten-from-code/overview.md) - related topic
- [Animate seated kittens, draw them on unflown craft and fly another craft from a chair](../../../kittens/animation/animate-and-draw-seated-kitten-crew/overview.md) - related topic

[^ksa-inputevents]: current/decomp/KSA/InputEvents.cs (decompiled, build 2026.10.10.5554)
[^ksa-kitteneva]: current/decomp/KSA/KittenEva.cs (decompiled, build 2026.10.10.5554)
[^ksa-evadoor]: current/decomp/KSA/EVADoor.cs (decompiled, build 2026.10.10.5554)
[^ksa-evadoortemplate]: current/decomp/KSA/EVADoorTemplate.cs (decompiled, build 2026.10.10.5554)
[^ksa-ivaseat]: current/decomp/KSA/IVASeat.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksa-kittenlocomotiontuning]: current/decomp/KSA/KittenLocomotionTuning.cs (decompiled, build 2026.10.10.5554)
