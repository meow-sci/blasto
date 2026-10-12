---
type: Mod Feature
title: Seats and EVA doors
description: Give a mod part crew seats with IVASeat entries placed at the kitten's eye, and let kittens get out and climb back in through EVADoor modules on mesh-less hidden subparts beside each seat
mod: ksacars
category: kittens
techniques: [xml-asset]
hooks: []
lifecycle: [startup:asset-load, event:eva]
game_types:
  - KSA.IVASeat
  - KSA.EVADoor
  - KSA.EVADoorTemplate
  - KSA.KittenEva
  - KSA.PartTemplate
  - KSA.SubPartGameDataReference
  - KSA.KittenLocomotionTuning
tags: [kittens, iva-seat, eva, boarding, xml, subparts, crew]
---

Both KSACars cars carry kittens using only KSA's own crew modules, declared in XML: an
`<IVASeat>` per seat in the part's `<PartGameData>`, and an `<EVADoor>` on a separate mesh-less
subpart beside each front seat. The launch window, the crew window and saves all fill the seats,
and a kitten gets out and boards again through the doors like on any stock craft; no code is
involved.[^ksacars-gamedata] The only mod code that touches seats picks out the driver's seat for
the [hand IK](./driver-hands-steering-ik.md) and draws the
[crew of cars nobody is flying](./seated-crew-of-unflown-craft.md).

## How it works

**Seats.** `<IVASeat Id="...">` maps to `IVASeat.IVASeatTemplate`, a module template registered
under `PartTemplate.Components` by its XML type name. Its children are `<Position>` (the kitten's
**eye** point in part space), `<ForwardAxis>` (the way it faces; default `X=1`) and `<UpAxis>` (the
way its head points; default `Z=-1`).[^ksa-ivaseat] `IVASeat.CreateComponents` turns each into a
module with public `PositionAsmb`, `ForwardAxisAsmb` and `UpAxisAsmb` fields, and the seated kitten
is drawn from them every frame at `GetSeatedModelPointAsmb(SeatedOffset)`, where
`KittenLocomotionTuning.SeatedOffset` is `(0, 0, -0.63)`: the model origin sits 0.63 m below the
eye along the seat's up.[^ksa-kittenlocomotiontuning] The cars' part space is +X up, +Y forward,
+Z left, so every seat uses `<ForwardAxis Y="1" />` and `<UpAxis X="1" />`.

| Car | Seats (eye positions) |
|---|---|
| Beach buggy | driver `(0.99, 0.18, -0.28)` on the right, passenger `(0.99, 0.18, 0.28)` |
| Eldorado | driver `(1.070, 0.202, 0.400)` on the left, passenger, and two rear seats at `X=1.030, Y=-0.720` |

**Doors.** An EVA door is a place, not a mesh. Each car declares two subpart templates with only
`<EditorTag Value="Hidden" />` (nothing drawn), instances them in the `<Part>` at the side of the
tub with a `<Transform><Position>`, and attaches `<EVADoor SeatId="..."/>` to each template through
a `<SubPartGameData Id="<subpart template id>">`.[^ksacars-assets] At load KSA merges every
`PartGameData`/`SubPartGameData` into the part or subpart template with the same Id
(`PartTemplate.ApplyGameData` copies `EVADoor`, `Control`, components and the rest).[^ksa-parttemplate]
`EVADoor.CreateComponents` adds an `EVADoor` module to the subpart, and
`EVADoor.ResolveAlignedSeats` pairs it with the `IVASeat` whose template Id equals `SeatId` on the
same full part.[^ksa-evadoor]

**Boarding and leaving.** A kitten on EVA can board through a door only when the door subpart's
origin is within 1 m (`KittenEva.CanBoardDoor`: `DoorOffsetCce(host, door).Length() < 1.0`, where
the offset uses `door.Parent.PositionVehicleAsmb`).[^ksa-kitteneva] So the door subpart is placed
outboard of the seat, where a kitten standing on the ground can reach it. Going EVA spawns the
kitten at the door aligned with its seat, or the first door when none is aligned.[^ksa-evadoor]
The mod's notes add that kittens only walk on static ground (terrain, clutter, pads), so a door a
kitten can reach from the ground is the whole boarding route.[^ksacars-notes]

**Driver seat in code.** `Buggies.DriverSeat` picks the `IVASeat` module nearest the profile's
`DriverEye` (within 0.25 m), so the profile does not need the seat's Id.[^ksacars-buggies]

## Lifecycle and timing

Pure content: the XML is parsed during `ModLibrary.LoadAll` (`startup:asset-load`), game data is
merged into templates by `ModLibrary.AttachGameData`, and the modules are created when a vehicle's
part tree is built. Boarding and EVA go through KSA's own input buffers (`InputEvents.EvaSpawnBuffer`,
`KittenBoardBuffer`) applied in `InputEvents.ApplyInputEvents` during `PrepareFrame` (`event:eva`).
No StarMap hook or patch is needed for any of it.

## Key code

| Path | Role |
|---|---|
| [src/KSACars/KSACarsGameData.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsGameData.xml#L41-L55) | Buggy seats |
| [src/KSACars/KSACarsGameData.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsGameData.xml#L144-L167) | Eldorado seats |
| [src/KSACars/KSACarsGameData.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsGameData.xml#L119-L124) | `SubPartGameData` with `EVADoor SeatId` (also L325-L330 for the Eldorado) |
| [src/KSACars/KSACarsAssets.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsAssets.xml#L148-L154) | Mesh-less hidden door subpart templates |
| [src/KSACars/KSACarsAssets.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsAssets.xml#L242-L252) | Door subparts placed outboard of each seat |
| [src/KSACars/Ksa/Buggies.cs](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/Ksa/Buggies.cs#L849-L865) | `DriverSeat` by eye position |

## Snippets

```xml
<!-- src/KSACars/KSACarsGameData.xml L41-L55 -->
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

```xml
<!-- src/KSACars/KSACarsAssets.xml L148-L154 and L242-L252 -->
  <!-- Where a kitten climbs in and out, one beside each seat. Nothing is drawn: a door is a place. -->
  <SubPart Id="KSACars_BuggyDoorL_SubPart">
    <EditorTag Value="Hidden" />
  </SubPart>
  <SubPart Id="KSACars_BuggyDoorR_SubPart">
    <EditorTag Value="Hidden" />
  </SubPart>
  <!-- ... -->
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

```xml
<!-- src/KSACars/KSACarsGameData.xml L119-L124 -->
  <SubPartGameData Id="KSACars_BuggyDoorL_SubPart">
    <EVADoor SeatId="KSACars_Buggy_SeatPassenger" />
  </SubPartGameData>
  <SubPartGameData Id="KSACars_BuggyDoorR_SubPart">
    <EVADoor SeatId="KSACars_Buggy_SeatDriver" />
  </SubPartGameData>
```

```csharp
// current/decomp/KSA/KittenEva.cs L883-L891
	private double3 DoorOffsetCce(Vehicle host, EVADoor door)
	{
		return host.GetPositionEcl() - GetPositionEcl() + (door.Parent.PositionVehicleAsmb - host.CenterOfMassAsmb).Transform(host.Body2Cce);
	}

	public bool CanBoardDoor(Vehicle host, EVADoor door)
	{
		return DoorHasSeat(host, door) && DoorOffsetCce(host, door).Length() < 1.0;
	}
```

## Game APIs touched

| API | Purpose |
|---|---|
| `IVASeat.IVASeatTemplate` ([current/decomp/KSA/IVASeat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVASeat.cs#L9-L26)) (`<IVASeat>`: `Position`, `ForwardAxis`, `UpAxis`) | Seat declaration; `IVASeatData AssignedKittenName` in saves |
| `IVASeat.CreateComponents`, `UpdateSeatedRenderable` ([current/decomp/KSA/IVASeat.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IVASeat.cs#L80-L140)) | Seat module and seated kitten drawing |
| `EVADoorTemplate.SeatId` ([current/decomp/KSA/EVADoorTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EVADoorTemplate.cs)) (`<EVADoor SeatId>`) | Which seat a door serves[^ksa-evadoortemplate] |
| `EVADoor.CreateComponents`, `ResolveAlignedSeats`, `SpawnEva` ([current/decomp/KSA/EVADoor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EVADoor.cs#L20-L175)) | Door module, seat pairing, where an EVA kitten appears |
| `KittenEva.CanBoardDoor`, `TryFindBoardTarget` ([current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L883-L910)) | 1 m boarding radius from the door subpart's origin |
| `PartTemplate.ApplyGameData`, `EVADoor` and `Components` fields ([current/decomp/KSA/PartTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTemplate.cs)) | Merging `SubPartGameData` into the subpart template by Id |
| `KittenLocomotionTuning.SeatedOffset` ([current/decomp/KSA/KittenLocomotionTuning.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenLocomotionTuning.cs#L220)) | 0.63 m from eye to seated model origin |

## Pitfalls and version notes

- **`Position` is the eye, not the cushion.** The kitten is drawn 0.63 m below it; the mod's notes
  record that a seat point on the surface sinks the kitten to its neck.[^ksacars-notes]
- **`SubPartGameData` Id is the subpart *template* Id** (`KSACars_BuggyDoorL_SubPart`), not the
  instance Id inside `<Part>` (`KSACars_BuggyDoorL`).
- **Put the door where a kitten can stand.** Boarding needs the door subpart's origin within 1 m;
  a door inside the hull or above the ground cannot be used. KSACars puts them at the tub's sides,
  slightly above the ground.
- **Seated crew are drawn for the flown craft only** in stock KSA; see the crew postfix.
- **Fill Seats can seat a kitten already out on EVA**, and leaving the car then collides with the
  existing EVA vehicle's Id; the docs mark this as KSA's to fix.[^ksacars-blocked]
- The driver must be able to reach the wheel; seat and steering-wheel positions are tuned together
  and checked by `SteeringGripTests`.

## See also

- [Car part game data](./car-part-gamedata.md)
- [Car part assets: GLB, PBR materials and subparts](./car-part-assets-glb-pbr.md)
- [Seated crew of unflown cars](./seated-crew-of-unflown-craft.md)
- [Driver kitten's hands on the steering wheel](./driver-hands-steering-ik.md)
- [Attach a pose processor to a seat's kitten each time the seat rebuilds it, and stop the idle actions fighting the hands](../../../howto/kittens/animation/animate-and-draw-seated-kitten-crew/animate-and-draw-seated-kitten-crew-1.md) - how-to article demonstrating this feature
- [Add IVASeat entries in PartGameData and EVADoor modules on hidden subparts](../../../howto/vehicles-and-parts/parts/add-crew-seats-and-eva-doors/add-crew-seats-and-eva-doors-1.md) - how-to article demonstrating this feature

[^ksacars-gamedata]: src/KSACars/KSACarsGameData.xml
[^ksacars-assets]: src/KSACars/KSACarsAssets.xml
[^ksacars-buggies]: src/KSACars/Ksa/Buggies.cs
[^ksacars-notes]: docs/KSA-MODDING-NOTES.md
[^ksacars-blocked]: docs/BLOCKED-ON-KSA.md
[^ksa-ivaseat]: KSA.IVASeat (decompiled)
[^ksa-evadoor]: KSA.EVADoor (decompiled)
[^ksa-evadoortemplate]: KSA.EVADoorTemplate (decompiled)
[^ksa-kitteneva]: KSA.KittenEva (decompiled)
[^ksa-parttemplate]: KSA.PartTemplate (decompiled)
[^ksa-kittenlocomotiontuning]: KSA.KittenLocomotionTuning (decompiled)
