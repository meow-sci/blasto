---
type: How-To
title: Define furniture as a minimal static part that doubles as a craft
description: The smallest Assets and GameData pair for a prop part that is also a one-part craft, with a hidden door subpart and no control module on the prop
category: vehicles-and-parts
group: parts
topic: define-a-part-with-game-data
approach: 3
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksastructures/features/office-furniture-craft]
techniques: [xml-asset]
hooks: [mod.toml assets]
lifecycle: [startup:asset-load]
game_types:
  - KSA.PartTemplate
  - KSA.PartGameDataReference
  - KSA.SubPartGameDataReference
  - KSA.EditorTagDefinition
  - KSA.IVASeat
  - KSA.EVADoor
tags: [xml, furniture, prop, single-part, minimal]
---
Outcome: an office chair and a monitor defined as parts, each small enough to read in one screen. They are
defined as parts rather than static objects because only a part can carry an `IVASeat` or an emissive screen;
each is then spawned as a one-part craft (see [Spawn a vehicle from code (from the vehicle library or a hand-built part)](../../vehicles/spawn-a-vehicle-at-runtime/overview.md) and [Ship whole-vehicle presets and single-part craft with a part mod](../ship-ready-made-craft/overview.md)).

Prerequisites: [article 1](define-a-part-with-game-data-1.md) for the basics. Demonstrated by
[ksastructures/office-furniture-craft](../../../../mods/ksastructures/features/office-furniture-craft.md).

## Background: how the game does it

- The manifest lists one file here, `KSAStructuresOffice.xml`; its `assets` entry is the only coupling
  [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L157-L169)[^ksa-mod].
- A chair's seat is an `IVASeat` module template (component) and a hidden door `SubPart` with an `EVADoor` in
  `SubPartGameData` points at it by `SeatId`; see [Add crew seats and EVA doors to a part](../add-crew-seats-and-eva-doors/overview.md).
- An `EditorTag Value="Hidden"` subpart has no model: "a door is a place", and `PartTemplate.IsHidden` is true for the `Hidden` tag [current/decomp/KSA/PartTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTemplate.cs#L133)[^ksa-parttemplate].
- A part is a complete craft only if it is root-capable: a whitelisted tag, at least one connector, no surface
  connector, and (to be flown) a `Control` module [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L1262-L1280)[^ksa-vehicleeditor].

## Lifecycle and timing

`startup:asset-load` only. The C# spawns the parts later by Id with `ModLibrary.Get<PartTemplate>` (not
`TryGet`, which has no `PartTemplate` branch; see [Spawn a vehicle from code (from the vehicle library or a hand-built part)](../../vehicles/spawn-a-vehicle-at-runtime/overview.md)).

## Steps

1. Manifest: `assets = ["KSAStructuresOffice.xml"]`, with the `[StarMap] EntryAssembly` table if there is code
   [src/KSAStructures/mod.toml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/mod.toml#L1-L12)[^ksastructures-mod].
2. An editor tag whose three flags make furniture eligible as a root and a snap target:

```xml
<!-- src/KSAStructures/KSAStructuresOffice.xml L18-19 -->
  <EditorTagDef Id="Structures" RootPartWhitelist="true" FaceSnapTargetWhitelist="true"
                DiameterFilterlist="true" />
```

3. Assets: a body subpart (mesh + material + `_VM` view), a mesh-less hidden door subpart, and the `Part` with
   one connector rotated to face down:

```xml
<!-- src/KSAStructures/KSAStructuresOffice.xml L6110-6138 -->
  <SubPart Id="KSAOffice_ChairBody_SubPart">
    <PartModel Id="KSAOffice_ChairBody_Model">
      <Mesh Id="KSAOffice_Chair" />
      <Material Id="KSAOffice_Material" />
    </PartModel>
    <MeshView>
      <Mesh Id="KSAOffice_Chair_VM" />
    </MeshView>
  </SubPart>

  <!-- Where a kitten climbs on and off. Nothing is drawn: a door is a place. -->
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

4. GameData for the chair: tag, diameter, connector, `Control` (so the chair is the controlled vehicle once a
   kitten boards), a seat (`Position` is the eye), a cuboid mass and two collider boxes:

```xml
<!-- src/KSAStructures/KSAStructuresOffice.xml L6140-6177 -->
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

    <SolidCuboidMass>
      <Mass Kg="40" />
      <LocationAsmb X="0.10" />
      <LengthX M="0.2" />
      <LengthY M="0.6" />
      <LengthZ M="0.6" />
    </SolidCuboidMass>

    <Collider Id="KSAOffice_ChairCollider">
      <Box Id="KSAOffice_ChairSeatCollider">
        <LocationAsmb X="0.16" Y="0" Z="0" />
        <Collider2Asmb X="0" Y="0" Z="0" />
        <LengthX M="0.32" />
        <LengthY M="0.50" />
        <LengthZ M="0.50" />
      </Box>
      <Box Id="KSAOffice_ChairBackCollider">
        <LocationAsmb X="0.56" Y="-0.27" Z="0" />
        <Collider2Asmb X="0" Y="0" Z="0" />
        <LengthX M="0.48" />
        <LengthY M="0.06" />
        <LengthZ M="0.50" />
      </Box>
    </Collider>
  </PartGameData>
```

5. Wire the door to the seat on the subpart definition:

```xml
<!-- src/KSAStructures/KSAStructuresOffice.xml L6179-6181 -->
  <SubPartGameData Id="KSAOffice_ChairDoor_SubPart">
    <EVADoor SeatId="KSAOffice_Chair_Seat" />
  </SubPartGameData>
```

6. A pure prop is even smaller: the monitor carries no `Control`, so it is a part that can be a craft but is
   never flown. All of its mass is in the foot so a nudge does not tip it off the desk:

```xml
<!-- src/KSAStructures/KSAStructuresOffice.xml L6231-6254 -->
  <PartGameData Id="KSArmory_Prefab_OfficeMonitor" DisplayName="Office Computer">
    <EditorTag Value="Structures" />
    <Diameter M="1" />
    <Connector Id="_monitorConnectorBase" />

    <!-- All of it in the foot, so a nudge does not tip it off the desk. -->
    <SolidCuboidMass>
      <Mass Kg="8" />
      <LocationAsmb X="0.03" />
      <LengthX M="0.06" />
      <LengthY M="0.2" />
      <LengthZ M="0.5" />
    </SolidCuboidMass>

    <Collider Id="KSAOffice_MonitorCollider">
      <Box Id="KSAOffice_MonitorBoxCollider">
        <LocationAsmb X="0.26" Y="-0.01" Z="0" />
        <Collider2Asmb X="0" Y="0" Z="0" />
        <LengthX M="0.52" />
        <LengthY M="0.20" />
        <LengthZ M="0.72" />
      </Box>
    </Collider>
  </PartGameData>
```

## Complete example

- [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L6110-L6254)[^ksastructures-ksastructuresoffice] - chair and monitor
- [src/KSAStructures/Ksa/CraftSpawner.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/CraftSpawner.cs)[^ksastructures-craftspawner] - how the part Id becomes a craft

## Variations and gotchas

- **Why a part, not a static object.** Per the file's comment, a static object's shader has no emissive term
  and a part's has, so only as a part does the monitor screen glow.
- **Seats and doors need a hidden subpart.** The door has no mesh: "a door is a place".
- **Id naming.** The part Ids are `KSArmory_Prefab_OfficeChair` and `KSArmory_Prefab_OfficeMonitor` in this
  mod, a historical prefix; whatever the Id, it is permanent once a save contains the craft.
- **A static prop part still needs a connector** to pass the root rule when launched through the editor.

## See also

- [Overview](overview.md), [Article 1](define-a-part-with-game-data-1.md), [Article 2](define-a-part-with-game-data-2.md)
- [ksastructures/office-furniture-craft](../../../../mods/ksastructures/features/office-furniture-craft.md)
- [Add crew seats and EVA doors to a part](../add-crew-seats-and-eva-doors/overview.md), [Ship whole-vehicle presets and single-part craft with a part mod](../ship-ready-made-craft/overview.md), [Spawn a vehicle from code (from the vehicle library or a hand-built part)](../../vehicles/spawn-a-vehicle-at-runtime/overview.md)

[^ksa-vehicleeditor]: current/decomp/KSA/VehicleEditor.cs (decompiled, build 2026.10.10.5554)
[^ksa-parttemplate]: current/decomp/KSA/PartTemplate.cs (decompiled, build 2026.10.10.5554)
[^ksa-mod]: current/decomp/KSA/Mod.cs (decompiled, build 2026.10.10.5554)
[^ksastructures-mod]: KSAStructures: src/KSAStructures/mod.toml
[^ksastructures-craftspawner]: KSAStructures: src/KSAStructures/Ksa/CraftSpawner.cs
[^ksastructures-ksastructuresoffice]: KSAStructures: src/KSAStructures/KSAStructuresOffice.xml
