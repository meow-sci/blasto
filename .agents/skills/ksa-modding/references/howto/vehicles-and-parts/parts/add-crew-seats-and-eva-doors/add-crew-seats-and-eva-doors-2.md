---
type: How-To
title: Give a chair a seat and a door with the minimum XML
description: The smallest working seat and door pair, one IVASeat in PartGameData and a hidden door subpart with an EVADoor, on a one-part furniture craft
category: vehicles-and-parts
group: parts
topic: add-crew-seats-and-eva-doors
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksastructures/features/office-furniture-craft]
techniques: [xml-asset]
hooks: [mod.toml assets]
lifecycle: [startup:asset-load]
game_types: [KSA.IVASeat, KSA.EVADoor, KSA.KittenEva, KSA.Vehicle, KSA.PartTemplate]
tags: [seat, eva-door, chair, minimum, furniture]
---
Outcome: a kitten can climb onto a chair and sit, with the chair as the controlled vehicle. KSAStructures' office
chair is the smallest complete example of the seat and door schema: one seat, one door, one part.

Prerequisites: [article 1](add-crew-seats-and-eva-doors-1.md). Demonstrated by
[ksastructures/office-furniture-craft](../../../../mods/ksastructures/features/office-furniture-craft.md).

## Background: how the game does it

- The chair must be a **part**, because a kitten sits only in a seat that belongs to a part; so it is a one-part
  craft ([Spawn a vehicle from code (from the vehicle library or a hand-built part)](../../vehicles/spawn-a-vehicle-at-runtime/overview.md) article 3, [Ship whole-vehicle presets and single-part craft with a part mod](../ship-ready-made-craft/overview.md)).
- Boarding the chair's door makes the chair the controlled vehicle (boarding disposes the EVA kitten, makes the
  camera follow the host and sets `Program.ControlledVehicle = host`, per the mod's notes), which is why the
  chair carries `<Control />`; the same swap is the foundation of [Take control of another vehicle: switch the controlled craft or autopilot it from a mod](../../vehicles/take-control-of-another-vehicle/overview.md).
- Seat and door pairing, the 1 m boarding reach and the eye offset are as in article 1
  [current/decomp/KSA/EVADoor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EVADoor.cs#L33-L61)[^ksa-evadoor] [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L878-L891)[^ksa-kitteneva] [current/decomp/KSA/IVASeat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVASeat.cs#L50-L56)[^ksa-ivaseat].

## Lifecycle and timing

Content only (`startup:asset-load`). The chair is created at runtime by the mod's spawner; the seat and door are
active as soon as the vehicle's part tree is built. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. A hidden door subpart definition, placed in the `Part` beside the seat (here 0.45 m to one side, so a kitten on
   the ground can reach it):

```xml
<!-- src/KSAStructures/KSAStructuresOffice.xml L6121-6138 -->
  <SubPart Id="KSAOffice_ChairDoor_SubPart">
    <EditorTag Value="Hidden" />
  </SubPart>

  <Part Id="KSArmory_Prefab_OfficeChair">
    <SubPart Id="KSAOffice_Chair_Body" InstanceOf="KSAOffice_ChairBody_SubPart" />
    <SubPart Id="KSAOffice_Chair_Door" InstanceOf="KSAOffice_ChairDoor_SubPart">
      <Transform>
        <Position X="0.35000" Y="0.45000" Z="0.00000" />
      </Transform>
    </SubPart>
    <Connector Id="_chairConnectorBase">
      <Transform>
        <Position X="0.00000" />
        <Rotation X="3.14159" Z="3.14159" />
      </Transform>
    </Connector>
  </Part>
```

2. The seat in `PartGameData`. The comment records the arithmetic: the seat top is 0.32 m up, Position is the
   eye, 0.63 m over the seated kitten's origin, whose hips sit 0.19 m above that origin:

```xml
<!-- src/KSAStructures/KSAStructuresOffice.xml L6140-6152 -->
  <PartGameData Id="KSArmory_Prefab_OfficeChair" DisplayName="Office Chair">
    <EditorTag Value="Structures" />
    <Diameter M="1" />
    <Connector Id="_chairConnectorBase" />
    <Control />

    <!-- The seat top is 0.32 m up; Position is the eye, 0.63 m over the seated kitten's origin, whose hips sit 0.19 m above that origin. -->
    <IVASeat Id="KSAOffice_Chair_Seat">
      <Position X="0.76" Y="0" Z="0" />
      <ForwardAxis Y="1" />
      <UpAxis X="1" />
    </IVASeat>

```

3. The door's `SeatId` names the seat:

```xml
<!-- src/KSAStructures/KSAStructuresOffice.xml L6179-6181 -->
  <SubPartGameData Id="KSAOffice_ChairDoor_SubPart">
    <EVADoor SeatId="KSAOffice_Chair_Seat" />
  </SubPartGameData>
```

4. Check: spawn the chair, send a kitten EVA, walk up to the chair and board through the door. The log shows the
   usual EVA messages; the chair becomes the controlled vehicle.

## Complete example

- [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L6110-L6181)[^ksastructures-ksastructuresoffice]
- [src/KSAStructures/Ksa/OfficeComputer.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeComputer.cs#L42-L46)[^ksastructures-officecomputer] - how the mod detects "seated": the controlled vehicle is the chair

## Variations and gotchas

- **Only one door.** A craft with a single seat needs a single door; the door's `SeatId` is optional but without it
  any free seat matches (`DoorHasSeat` for a door with no aligned seat).
- **The door has no mesh**, so it never shows up in the editor (`Hidden` tag) and cannot be picked; its position
  is its whole meaning.
- **Seated kittens are not drawn when this craft is not the controlled one**; the mod handles that separately
  (`animate-and-draw-seated-kitten-crew`).
- **Pure props.** A chair that kittens only look at does not need `<Control />`; add it only if boarding should
  hand over flight controls.

## See also

- [Overview](overview.md), [Article 1](add-crew-seats-and-eva-doors-1.md)
- [ksastructures/office-furniture-craft](../../../../mods/ksastructures/features/office-furniture-craft.md)
- [Define a part with its own game data and GLB model](../define-a-part-with-game-data/overview.md), [Take control of another vehicle: switch the controlled craft or autopilot it from a mod](../../vehicles/take-control-of-another-vehicle/overview.md)

[^ksastructures-officecomputer]: KSAStructures: src/KSAStructures/Ksa/OfficeComputer.cs
[^ksa-kitteneva]: current/decomp/KSA/KittenEva.cs (decompiled, build 2026.10.10.5554)
[^ksa-evadoor]: current/decomp/KSA/EVADoor.cs (decompiled, build 2026.10.10.5554)
[^ksa-ivaseat]: current/decomp/KSA/IVASeat.cs (decompiled, build 2026.10.10.5554)
[^ksastructures-ksastructuresoffice]: KSAStructures: src/KSAStructures/KSAStructuresOffice.xml
