---
type: How-To
title: Add IVASeat entries in PartGameData and EVADoor modules on hidden subparts
description: Declare driver and passenger seats with eye-point positions, place mesh-less door subparts beside them and pair each door to its seat by SeatId
category: vehicles-and-parts
group: parts
topic: add-crew-seats-and-eva-doors
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/seats-and-eva-doors]
techniques: [xml-asset]
hooks: [mod.toml assets]
lifecycle: [startup:asset-load, frame:render]
game_types:
  - KSA.IVASeat
  - KSA.EVADoor
  - KSA.EVADoorTemplate
  - KSA.KittenEva
  - KSA.Vehicle
  - KSA.PartTemplate
  - KSA.KittenLocomotionTuning
tags: [seat, eva-door, iva, car, kitten, hidden-subpart]
---
Outcome: your part carries kittens. They appear in the launch and crew windows, sit in the right place, and can
step out and back in through doors like on any stock craft, with no mod code. KSACars' two cars use this: two or
four seats each, one door beside each front seat.

Prerequisite: a part defined in XML ([Define a part with its own game data and GLB model](../define-a-part-with-game-data/overview.md)). Demonstrated by
[ksacars/seats-and-eva-doors](../../../../mods/ksacars/features/seats-and-eva-doors.md).

## Background: how the game does it

See the [overview](overview.md). Key facts:

- A seat's `<Position>` is the kitten's **eye**; the model origin is drawn 0.63 m below it along the seat's up
  axis (`SeatedOffset = (0, 0, -0.63)`), and the hips sit 0.19 m above the model origin
  [current/decomp/KSA/IVASeat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVASeat.cs#L50-L56)[^ksa-ivaseat] [current/decomp/KSA/KittenLocomotionTuning.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenLocomotionTuning.cs#L220)[^ksa-kittenlocomotiontuning]. `ForwardAxis` defaults to `(1,0,0)` and `UpAxis` to
  `(0,0,-1)` [current/decomp/KSA/IVASeat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVASeat.cs#L9-L27); KSACars' part space is +X up, +Y forward, so each seat sets
  `ForwardAxis Y="1"` and `UpAxis X="1"`.
- A door is `<EVADoor SeatId="...">` in `SubPartGameData` on a **subpart definition**
  [current/decomp/KSA/EVADoorTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EVADoorTemplate.cs#L5-L12)[^ksa-evadoortemplate]; `EVADoor.CreateComponents` adds the module to the subpart
  [current/decomp/KSA/EVADoor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EVADoor.cs#L20-L31)[^ksa-evadoor].
- `ResolveAlignedSeats` links each door to the `IVASeat` whose template Id equals `SeatId`. Note the match
  compares the seat's full part (or its `TreeParent`) with the door's full part, so on a multi-part craft keep
  seat and door on the same root part [current/decomp/KSA/EVADoor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EVADoor.cs#L33-L61).
- A kitten boards only within 1 m of the door subpart's origin
  (`KittenEva.CanBoardDoor`) [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L878-L891)[^ksa-kitteneva], so place the door where a kitten standing on the
  ground can reach it. If the door has an aligned seat, boarding requires that seat to be unoccupied
  (`DoorHasSeat`); without an aligned seat, any free seat will do.
- Going EVA spawns the kitten at the door; the button is queued on `InputEvents.EvaSpawnBuffer`
  [current/decomp/KSA/EVADoor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EVADoor.cs#L84-L90).

## Lifecycle and timing

Content only: XML parsed in `startup:asset-load`, game data merged by `ModLibrary.AttachGameData`, modules
created when a vehicle's part tree is built. Boarding and EVA go through `InputEvents` buffers drained in
`ApplyInputEvents` (`frame:input`). Drawing the seated crew is `frame:render`:
`Vehicle.UpdateSeatedCrewRenderData` draws seated kittens only for the controlled vehicle
[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3728-L3741)[^ksa-vehicle]. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Declare the seats in the part's `PartGameData`. Position is the eye point; choose it so the model origin
   (0.63 m lower) puts the hips on the cushion:

```xml
<!-- src/KSACars/KSACarsGameData.xml L41-55 -->
    <!--
      Two seats, the driver on the right, behind the wheel. Position is the eye; the engine draws the
      seated kitten's origin 0.63 m below it along UpAxis, and its hips sit 0.19 m above that origin,
      which puts them on the cushion.
    -->
    <IVASeat Id="KSACars_Buggy_SeatDriver">
      <Position X="0.99" Y="0.18" Z="-0.28" />
      <ForwardAxis Y="1" />
      <UpAxis X="1" />
    </IVASeat>
    <IVASeat Id="KSACars_Buggy_SeatPassenger">
      <Position X="0.99" Y="0.18" Z="0.28" />
      <ForwardAxis Y="1" />
      <UpAxis X="1" />
    </IVASeat>
```

2. Declare the door subpart definitions in the Assets bundle. Nothing is drawn: a door is a place, so they carry
   only the `Hidden` editor tag:

```xml
<!-- src/KSACars/KSACarsAssets.xml L148-154 -->
  <!-- Where a kitten climbs in and out, one beside each seat. Nothing is drawn: a door is a place. -->
  <SubPart Id="KSACars_BuggyDoorL_SubPart">
    <EditorTag Value="Hidden" />
  </SubPart>
  <SubPart Id="KSACars_BuggyDoorR_SubPart">
    <EditorTag Value="Hidden" />
  </SubPart>
```

3. Instance them in the `Part`, outboard of each seat at the tub's side. A kitten boards within a metre of the
   door's origin:

```xml
<!-- src/KSACars/KSACarsAssets.xml L242-252 -->
    <!-- Outboard of each seat, at the tub's side: a kitten boards within a metre of its door. -->
    <SubPart Id="KSACars_BuggyDoorL" InstanceOf="KSACars_BuggyDoorL_SubPart">
      <Transform>
        <Position X="0.55000" Y="0.18000" Z="0.75000" />
      </Transform>
    </SubPart>
    <SubPart Id="KSACars_BuggyDoorR" InstanceOf="KSACars_BuggyDoorR_SubPart">
      <Transform>
        <Position X="0.55000" Y="0.18000" Z="-0.75000" />
      </Transform>
    </SubPart>
```

4. Bind each door to its seat in `SubPartGameData`, keyed on the **subpart definition** Id so every instance
   inherits it:

```xml
<!-- src/KSACars/KSACarsGameData.xml L119-124 -->
  <SubPartGameData Id="KSACars_BuggyDoorL_SubPart">
    <EVADoor SeatId="KSACars_Buggy_SeatPassenger" />
  </SubPartGameData>
  <SubPartGameData Id="KSACars_BuggyDoorR_SubPart">
    <EVADoor SeatId="KSACars_Buggy_SeatDriver" />
  </SubPartGameData>
```

5. Test: launch the craft, assign a kitten in the launch or crew window, go EVA from the door context menu,
   walk to within a metre of the door and board. If boarding does nothing, check the door's distance, that the
   `SeatId` matches the `IVASeat Id` exactly, and that the seat is free.

## Complete example

- [src/KSACars/KSACarsGameData.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsGameData.xml#L41-L55)[^ksacars-ksacarsgamedata] - the seats; the Eldorado adds two rear seats
- [src/KSACars/KSACarsAssets.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsAssets.xml#L148-L154)[^ksacars-ksacarsassets] - door definitions
- [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs)[^ksacars-buggies] - the only code that touches seats: it picks the `IVASeat` nearest the driver eye for hand IK

## Variations and gotchas

- **Cars are +X up.** The default seat axes assume a different part orientation; set `ForwardAxis` and `UpAxis`
  for your part's frame.
- **One door per seat or per side.** The cars give each front seat its own door; rear-seat passengers use the first
  door when their seat has none aligned.
- **Seats on non-root parts.** `ResolveAlignedSeats` pairs by the seat's full part or its `TreeParent`; test
  multi-part craft rather than assuming.
- **Seated crew of craft the player is not flying are not drawn**, because `UpdateSeatedCrewRenderData` is
  gated on `ControlledVehicle`; the topic `animate-and-draw-seated-kitten-crew` covers drawing them.
- **Updates.** All of this is data; the engine can change the 0.63 m eye offset
  (`KittenLocomotionTuning.SeatedOffset`) and every authored eye point would shift with it.

## See also

- [Overview](overview.md), [Article 2](add-crew-seats-and-eva-doors-2.md)
- [ksacars/seats-and-eva-doors](../../../../mods/ksacars/features/seats-and-eva-doors.md)
- [Define a part with its own game data and GLB model](../define-a-part-with-game-data/overview.md), `spawn-an-eva-kitten-from-code`, [Take control of another vehicle: switch the controlled craft or autopilot it from a mod](../../vehicles/take-control-of-another-vehicle/overview.md)

[^ksacars-buggies]: KSACars: src/KSACars/Ksa/Buggies.cs
[^ksa-kitteneva]: current/decomp/KSA/KittenEva.cs (decompiled, build 2026.10.10.5554)
[^ksacars-ksacarsgamedata]: KSACars: src/KSACars/KSACarsGameData.xml
[^ksa-evadoor]: current/decomp/KSA/EVADoor.cs (decompiled, build 2026.10.10.5554)
[^ksa-evadoortemplate]: current/decomp/KSA/EVADoorTemplate.cs (decompiled, build 2026.10.10.5554)
[^ksa-ivaseat]: current/decomp/KSA/IVASeat.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksacars-ksacarsassets]: KSACars: src/KSACars/KSACarsAssets.xml
[^ksa-kittenlocomotiontuning]: current/decomp/KSA/KittenLocomotionTuning.cs (decompiled, build 2026.10.10.5554)
