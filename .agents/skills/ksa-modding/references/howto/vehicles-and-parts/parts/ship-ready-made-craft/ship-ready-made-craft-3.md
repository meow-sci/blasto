---
type: How-To
title: Make a furniture part a one-part craft that is never built upon
description: Apply the root-part rule to props, with a Control module only where the prop is flown, and place them from code without the editor
category: vehicles-and-parts
group: parts
topic: ship-ready-made-craft
approach: 3
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksastructures/features/office-furniture-craft]
techniques: [xml-asset]
hooks: [mod.toml assets]
lifecycle: [startup:asset-load]
game_types: [KSA.PartTemplate, KSA.EditorTagDefinition, KSA.VehicleEditor, KSA.IVASeat, KSA.Vehicle]
tags: [root-part, furniture, props, single-part]
---
Outcome: an office chair and a monitor are one-part craft. A chair must be a craft because a kitten sits only in
a seat on a part; both must be craft because the mod places them in the world as vehicles. They are never
meant to have anything built on them.

Prerequisites: [article 2](ship-ready-made-craft-2.md). Demonstrated by
[ksastructures/office-furniture-craft](../../../../mods/ksastructures/features/office-furniture-craft.md).

## Background: how the game does it

- The root-part rule is the same as in article 2 [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L1262-L1280)[^ksa-vehicleeditor].
- A craft is flown only if the tree has a `Control` module [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L589)[^ksa-vehicle]. The chair has `<Control />` (so
  once a kitten boards, the chair is the controlled vehicle); the monitor has none, so it is a passenger that
  can be placed and picked up but never flown.
- Code that builds the vehicle itself does not use the editor rule: the mod creates a `Part` from the template and
  calls `Vehicle.CreateVehicle`; see [Spawn a vehicle from code (from the vehicle library or a hand-built part)](../../vehicles/spawn-a-vehicle-at-runtime/overview.md) article 3.

## Lifecycle and timing

Content only (`startup:asset-load`). The prop is spawned later by C# via `ModLibrary.Get<PartTemplate>(id)`.

## Steps

1. A `Structures` tag with the three Structural flags makes the props eligible and snap targets:

```xml
<!-- src/KSAStructures/KSAStructuresOffice.xml L18-19 -->
  <EditorTagDef Id="Structures" RootPartWhitelist="true" FaceSnapTargetWhitelist="true"
                DiameterFilterlist="true" />
```

2. In the chair's GameData: the tag, a connector (no surface flag), `<Control />`:

```xml
<!-- src/KSAStructures/KSAStructuresOffice.xml L6140-6144 -->
  <PartGameData Id="KSArmory_Prefab_OfficeChair" DisplayName="Office Chair">
    <EditorTag Value="Structures" />
    <Diameter M="1" />
    <Connector Id="_chairConnectorBase" />
    <Control />
```

3. The prop that is never flown omits `<Control />` and keeps the tag, the diameter and a connector:

```xml
<!-- src/KSAStructures/KSAStructuresOffice.xml L6231-6235 -->
  <PartGameData Id="KSArmory_Prefab_OfficeMonitor" DisplayName="Office Computer">
    <EditorTag Value="Structures" />
    <Diameter M="1" />
    <Connector Id="_monitorConnectorBase" />

```

4. The connector in the Assets `Part` is rotated to face down and has no flags:

```xml
<!-- src/KSAStructures/KSAStructuresOffice.xml L6223-6229 -->
    <Connector Id="_monitorConnectorBase">
      <Transform>
        <Position X="0.00000" />
        <Rotation X="3.14159" Z="3.14159" />
      </Transform>
    </Connector>
  </Part>
```

5. If the prop is never meant to be launched by the player at all, omit the whitelist tag: the part then is not
   root-capable and the editor greys it out, while code can still create the craft. KSAStructures keeps the
   whitelist so the pieces can also be launched or built on in the editor.

## Complete example

- [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml#L6110-L6254)[^ksastructures-ksastructuresoffice]
- [src/KSAStructures/Ksa/OfficeFurniture.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeFurniture.cs#L18-L24)[^ksastructures-officefurniture] - the placement table keyed by part Id

## Variations and gotchas

- **Id lifetime.** Furniture vehicles are saved with the universe; the mod strips them from loaded system data
  and respawns them (see `keep-mod-vehicles-out-of-saves`).
- **A `Control` module makes it controllable.** `Vehicle.IsControllable` counts `Control` modules, so add one only
  to props that a kitten should be able to fly.
- **Hidden door subpart.** The chair's door is a mesh-less `Hidden` subpart, which does not affect the root
  rule (the rule reads the part's own tags only); see [Add crew seats and EVA doors to a part](../add-crew-seats-and-eva-doors/overview.md).

## See also

- [Overview](overview.md), [Article 1](ship-ready-made-craft-1.md), [Article 2](ship-ready-made-craft-2.md)
- [ksastructures/office-furniture-craft](../../../../mods/ksastructures/features/office-furniture-craft.md)
- [Spawn a vehicle from code (from the vehicle library or a hand-built part)](../../vehicles/spawn-a-vehicle-at-runtime/overview.md), [Add crew seats and EVA doors to a part](../add-crew-seats-and-eva-doors/overview.md)

[^ksa-vehicleeditor]: current/decomp/KSA/VehicleEditor.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksastructures-officefurniture]: KSAStructures: src/KSAStructures/Ksa/OfficeFurniture.cs
[^ksastructures-ksastructuresoffice]: KSAStructures: src/KSAStructures/KSAStructuresOffice.xml
