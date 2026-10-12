---
type: How-To
title: Make a single part a whole craft with a RootPartWhitelist tag
description: Give a part an editor tag with RootPartWhitelist, a node connector with no surface flag and a Control module so it can start and fly a craft by itself
category: vehicles-and-parts
group: parts
topic: ship-ready-made-craft
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/car-part-gamedata]
techniques: [xml-asset]
hooks: [mod.toml assets]
lifecycle: [startup:asset-load]
game_types: [KSA.PartTemplate, KSA.EditorTagDefinition, KSA.VehicleEditor, KSA.Vehicle, KSA.ControlTemplate]
tags: [root-part, editor-tag, connector, control, single-part]
---
Outcome: your part is selectable as the first part in the vehicle editor and, on its own, is a flyable craft.
KSACars' cars are single parts that need no command pod. The rule is short, but breaking any clause greys the
part out with no log message.

Prerequisite: a part already defined ([Define a part with its own game data and GLB model](../define-a-part-with-game-data/overview.md)). Demonstrated by
[ksacars/car-part-gamedata](../../../../mods/ksacars/features/car-part-gamedata.md).

## Background: how the game does it

`VehicleEditor.IsAllowedAsRootPart(PartTemplate)` returns true only when:

1. one of the part's tags is on the root-part whitelist, which is the set of `EditorTagDef`s with
   `RootPartWhitelist="true"` [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L7086-L7100)[^ksa-vehicleeditor];
2. `part.Connectors.Count > 0`;
3. no connector has flag bit 2 (`ToSurface`) or 4 (`FromSurface`) set [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L1262-L1280)
   (`Part.Connector.Flag` is `Internal = 1, ToSurface = 2, FromSurface = 4` [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L190-L199)[^ksa-part]).

When the editor is empty, the picker draws parts that fail it disabled (`editor.IsEmpty &&
!editor.IsAllowedAsRootPart(item)`) [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L337-L341). Separately, a category button is shown only
if it has a root-capable part (`CategoryHasRootPart`) [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L487-L498).

Flying it needs one more thing: `Vehicle.IsControllable` is true only when the part tree contains `Control`
modules (`Parts.Controls.NumModules > 0`) [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L589)[^ksa-vehicle]. A one-part craft with no `<Control />` is a
prop.

## Lifecycle and timing

Content only. `EditorTagDef`s are read in a pre-pass (`ModLibrary.LoadEditorTags`) so the tag exists before
parts are loaded, and the whitelist is populated by `MarkEditorTagDefinitionsLoaded`
[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L935-L942)[^ksa-program] [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L7086-L7100). A tag registered later (at runtime) does not
join the whitelist. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Define the tag with the whitelist flag. Copy the three flags from Core's `Structural` tag; omitting any
   silently drops root eligibility, face snapping or diameter filtering:

```xml
<!-- src/KSACars/KSACarsGameData.xml L10-15 -->
  <!--
    The cars' own editor category. Core's tags that can start a craft carry the same three flags, and
    without them a car silently loses the ability to be a craft's root part.
  -->
  <EditorTagDef Id="Vehicles" RootPartWhitelist="true" FaceSnapTargetWhitelist="true"
                DiameterFilterlist="true" />
```

2. Put the tag on the part, and add `NoFaceSnapping` if the part is modelled with +X up (face snapping would lay
   it on its side):

```xml
<!-- src/KSACars/KSACarsGameData.xml L17-21 -->
  <PartGameData Id="KSACars_Prefab_Buggy" DisplayName="Beach Buggy">
    <EditorTag Value="Vehicles" />
    <!-- +X is up on this hull too, and face snapping would lay it on its side. -->
    <EditorTag Value="NoFaceSnapping" />
    <Diameter M="2" />
```

3. Give it a node connector with **no `Flags`** (or only `Internal`), in the `Part` of the Assets bundle and
   in the GameData (the GameData entry merges by Id):

```xml
<!-- src/KSACars/KSACarsAssets.xml L254-261 -->
    <!-- Underside attach node, at the ground plane and facing down. -->
    <Connector Id="_bgConnectorAft">
      <Transform>
        <Position X="0.00000" />
        <Rotation Z="3.14159" />
        <Scale X="2.00000" Y="2.00000" Z="2.00000" />
      </Transform>
    </Connector>
```

```xml
<!-- src/KSACars/KSACarsGameData.xml L23-24 -->
    <!-- A node connector, so the buggy can start a craft. -->
    <Connector Id="_bgConnectorAft" />
```

4. Add `<Control />` so the craft has a command source, and a `<Battery>` if the part needs power for its own
   light switch:

```xml
<!-- src/KSACars/KSACarsGameData.xml L26-31 -->
    <!-- A command source, so a craft that is only this has something to control it. -->
    <Control />

    <Battery>
      <MaximumCapacity J="2000" />
    </Battery>
```

5. Launch the editor with an empty craft: the part should appear under your category and be clickable. If it is
   greyed out, walk the three clauses above. If the category does not appear at all, no part in it passes the
   whitelist.

## Complete example

- [src/KSACars/KSACarsGameData.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsGameData.xml#L10-L31)[^ksacars-ksacarsgamedata] - tag, tags, connector, control
- [src/KSACars/KSACarsAssets.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsAssets.xml#L254-L261)[^ksacars-ksacarsassets] - the connector's transform

## Variations and gotchas

- **Root-capable and radially attached are mutually exclusive.** A part with a `ToSurface` connector (rails,
  racks) can never start a craft, whatever tags it has. KSArmory's launcher has a plain node connector and
  its stores have `ToSurface`.
- **Hidden and sub-part templates never qualify.** `CategoryHasRootPart` skips `IsSubPart` and `IsHidden`
  templates.
- **Code-spawned craft skip the rule.** `new Part(id, template)` + `Vehicle.CreateVehicle` does not consult
  `IsAllowedAsRootPart`; it is purely an editor check (see [Spawn a vehicle from code (from the vehicle library or a hand-built part)](../../vehicles/spawn-a-vehicle-at-runtime/overview.md)).
- **Updates.** The whitelist is data (XML flags), so a game update that renames the private method does not
  break the part; a change to the flag semantics would.

## See also

- [Overview](overview.md), [Article 1](ship-ready-made-craft-1.md), [Article 3](ship-ready-made-craft-3.md)
- [ksacars/car-part-gamedata](../../../../mods/ksacars/features/car-part-gamedata.md)
- [Define a part with its own game data and GLB model](../define-a-part-with-game-data/overview.md), [Spawn a vehicle from code (from the vehicle library or a hand-built part)](../../vehicles/spawn-a-vehicle-at-runtime/overview.md)

[^ksa-program]: current/decomp/KSA/Program.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditor]: current/decomp/KSA/VehicleEditor.cs (decompiled, build 2026.10.10.5554)
[^ksacars-ksacarsgamedata]: KSACars: src/KSACars/KSACarsGameData.xml
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksacars-ksacarsassets]: KSACars: src/KSACars/KSACarsAssets.xml
