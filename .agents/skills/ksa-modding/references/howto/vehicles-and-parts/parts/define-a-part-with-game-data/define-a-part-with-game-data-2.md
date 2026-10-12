---
type: How-To
title: Split inert weapon parts into Assets and GameData with real RCS thrusters on subparts
description: Declare art, subparts and structure in one bundle and mass, colliders, connectors, editor tags and engine-driven thrusters in another, with behaviour left to C# that finds the part by Id
category: vehicles-and-parts
group: parts
topic: define-a-part-with-game-data
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/weapon-part-definitions-xml]
techniques: [xml-asset]
hooks: [mod.toml assets]
lifecycle: [startup:asset-load, startup:all-mods-loaded]
game_types:
  - KSA.PartTemplate
  - KSA.SubPartTemplate
  - KSA.SubPartGameDataReference
  - KSA.PartGameDataReference
  - KSA.EditorTagDefinition
  - KSA.CombustorTemplate
  - KSA.DeLavalNozzleTemplate
  - KSA.RocketThrusterControllerTemplate
  - KSA.ConsumerFeedWiring
  - KSA.Tank
tags: [xml, weapons, rcs, subpart, game-data, editor-tag, root-part]
---
Outcome: fourteen weapon parts (a truck, missile rails, a bomb rack, a Phalanx, a MIRV bus...) in the editor, none
of which carries behaviour in XML. KSA has no hook for a mod to register a part module, so each part is declared
as **inert structure** and the C# finds it by Id. The one exception, the MIRV bus, uses real engine modules
(`RocketThrusterController`, `Combustor`, `DeLavalNozzle`, `Tank`) on subparts, and shows how thruster game data
attaches to subpart definitions.

Prerequisites: StarMap (for the C# half only). Demonstrated by [ksarmory/weapon-part-definitions-xml](../../../../mods/ksarmory/features/weapon-part-definitions-xml.md). Basic
part XML is in [article 1](define-a-part-with-game-data-1.md).

## Background: how the game does it

- Both files are plain `<Assets>` documents listed in `mod.toml`; relative paths in the XML resolve against the
  mod folder, which is why KSArmory keeps both at the mod root [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L157-L169)[^ksa-mod].
- A `<PartGameData Id=X>` is matched to the `<Part Id=X>` after all bundles load; a `<SubPartGameData Id=Y>`
  is matched to the `<SubPart Id=Y>` **definition**, not to an instance, so every instance of that subpart
  inherits the engine/thruster declaration [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1863-L1876)[^ksa-modlibrary] [current/decomp/KSA/SubPartGameDataReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SubPartGameDataReference.cs#L1-L9)[^ksa-subpartgamedatareference]
  [current/decomp/KSA/PartTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTemplate.cs#L265-L351)[^ksa-parttemplate]. `ApplyGameData` also merges `SubPart` instances by Id.
- Thruster declarations reuse modules the engine already registers (`RocketThrusterController`, `Rocket`,
  `Combustor`, `DeLavalNozzle`) [current/decomp/KSA/PartTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTemplate.cs#L19-L45). `CombustorTemplate.OnDataLoad` clamps
  `MinimumPulseTime` to at least 0.001 s [current/decomp/KSA/CombustorTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CombustorTemplate.cs#L84-L91)[^ksa-combustortemplate].
- A part that starts a craft must pass `VehicleEditor.IsAllowedAsRootPart` (whitelisted tag, a connector, no
  `ToSurface`/`FromSurface` flag) [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L1262-L1280)[^ksa-vehicleeditor]; the editor rejects any part with a
  surface connector whatever its tags say, so "root-capable" and "radially attached" are mutually exclusive.

## Lifecycle and timing

`startup:asset-load`. `LoadEditorTags` reads the bundles once to register `EditorTagDef`s, `LoadAll` loads
them, and `AttachGameData` merges later [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L935-L990)[^ksa-program]. A mod reading a `PartTemplate` from
`StarMapAllModsLoaded` sees the Assets definition without the merged game data, because that hook is a postfix
on `LoadAll`; if your C# inspects tags, rockets or connectors, run it later (first frame or first vehicle). See
[game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. List the files, one by one (here also particles and sounds):

```toml
# src/KSArmory/mod.toml L11-16
assets = [
    "KSArmoryAssets.xml",
    "KSArmoryGameData.xml",
    "KSArmoryParticles.xml",
    "KSArmorySounds.xml",
]
```

2. **Assets bundle**: atlas, material, a subpart per moving or separately-addressed body, and the part. Give each
   `SubPart` a `PartModel` (mesh + material) and a `MeshView` with the `_VM` twin KSA's own bundler pairs with
   it:

```xml
<!-- src/KSArmory/KSArmoryAssets.xml L160-166, L293-301, L315-323 -->
  <MeshAtlas Path="Meshes/KSArmory_MirvBus.glb" />

  <PbrMaterial Id="KSArmory_MirvBus_Material">
    <Diffuse Path="Textures/KSArmory_MirvBus_Diffuse.png" Category="Vessel" />
    <Normal Path="Textures/KSArmory_MirvBus_Normal.png" Category="Vessel" />
    <AoRoughMetal Path="Textures/KSArmory_MirvBus_PBR.png" Category="Vessel" />
  </PbrMaterial>
<!-- ... -->
  <SubPart Id="KSArmory_MirvBus_SubPart">
    <PartModel Id="KSArmory_Subpart_MirvBus_Model">
      <Mesh Id="KSArmory_Subpart_MirvBus" />
      <Material Id="KSArmory_MirvBus_Material" />
    </PartModel>
    <MeshView>
      <Mesh Id="KSArmory_Subpart_MirvBus_VM" />
    </MeshView>
  </SubPart>
<!-- ... -->
  <SubPart Id="KSArmory_MirvRcs_SubPart">
    <PartModel Id="KSArmory_Subpart_MirvRcs_Model">
      <Mesh Id="KSArmory_Subpart_RcsNozzle" />
      <Material Id="KSArmory_MirvBus_Material" />
    </PartModel>
    <MeshView>
      <Mesh Id="KSArmory_Subpart_RcsNozzle_VM" />
    </MeshView>
  </SubPart>
```

3. Instance the subparts in the `Part` with transforms. Rotations are XYZ radians; a nozzle fires along its own
   -X:

```xml
<!-- src/KSArmory/KSArmoryAssets.xml L1345-1347, L1390-1396 -->
  <Part Id="KSArmory_Prefab_MirvBus">

    <SubPart Id="KSArmory_Mirv_Bus" InstanceOf="KSArmory_MirvBus_SubPart" />
<!-- ... -->
    -->
    <SubPart Id="KSArmory_Mirv_Rcs00" InstanceOf="KSArmory_MirvRcs_SubPart">
      <Transform>
        <Position X="0.42000" Y="1.10309" Z="1.10309" />
        <Rotation Z="-3.14159" />
      </Transform>
    </SubPart>
```

4. Put the attach node on the part, with no surface flag when the part must be a root:

```xml
<!-- src/KSArmory/KSArmoryAssets.xml L1538-1545 -->
    <Connector Id="_mirvConnectorBase">
      <Transform>
        <Position X="0.00000" />
        <Rotation X="3.14159" Z="3.14159" />
      </Transform>
    </Connector>

  </Part>
```

5. **GameData bundle**: an editor category with the three Structural flags (a generic name lets another mod
   share the tab), then the part:

```xml
<!-- src/KSArmory/KSArmoryGameData.xml L31-32 -->
  <EditorTagDef Id="Weapons" RootPartWhitelist="true" FaceSnapTargetWhitelist="true"
                DiameterFilterlist="true" />
```

```xml
<!-- src/KSArmory/KSArmoryGameData.xml L47-48, L56-57, L70, L76-87 -->
  <PartGameData Id="KSArmory_Prefab_Launcher6" DisplayName="Pantsir-S1 Point Defence System">
    <EditorTag Value="Weapons" />
<!-- ... -->
    <EditorTag Value="NoFaceSnapping" />
    <Diameter M="3" />
<!-- ... -->
    <Connector Id="_adConnectorAft" />
<!-- ... -->
    <Control />

    <!-- Onboard cell, so the unit is not dead weight when detached from a host craft. -->
    <Battery>
      <MaximumCapacity J="2000" />
    </Battery>

    <!-- Combat weight of a real Pantsir-S1 on its KAMAZ-6560: about thirty tonnes. -->
    <SolidSphereMass>
      <Mass Kg="30000" />
      <Radius M="2.00" />
    </SolidSphereMass>
```

6. For a part that must steer after separation, put the engine modules on the **subpart definition** and wire
   the feed on the part. The thruster controller names a rocket, the rocket names a core and a nozzle; the
   combustor feeds from its parent unless wired:

```xml
<!-- src/KSArmory/KSArmoryGameData.xml L479-493, L508, L522-535 -->
  <SubPartGameData Id="KSArmory_MirvRcs_SubPart">
    <RocketThrusterController Id="MirvACS">
      <RocketReference Id="Thruster" />
    </RocketThrusterController>
    <Rocket Id="Thruster">
      <Core Id="Chamber" />
      <Nozzle Id="Nozzle" />
    </Rocket>
    <Combustor Id="Chamber">
      <FeedsFrom Parent="true" />
      <Plumbing>Service</Plumbing>
      <Reaction Id="MMH_NTO">
        <MixtureRatio>1.6</MixtureRatio>
      </Reaction>
      <MaxPressure Bar="7" />
<!-- ... -->
    <DeLavalNozzle Id="Nozzle">
<!-- ... -->
      <ExitDiameter M="0.13" />
      <FxExitDiameter M="0.11" />
      <AreaRatio Value="40" />
      <FlowEfficiency Value="1" />
      <ExpansionEfficiency Value="0.70" />
      <!-- The bell exit, in the nozzle subpart's own frame. It fires along its own -X. -->
      <ExhaustLocation X="-0.090" Y="0.0" Z="0.0" />
      <ExhaustDirection X="-1.0" Y="0.0" Z="0.0" />
      <ReactionPlume Default="true">
        <VolumetricExhaust Id="RCS" />
      </ReactionPlume>
      <SoundEvent Action="On" SoundId="DefaultRcsThruster" />
    </DeLavalNozzle>
  </SubPartGameData>
```

```xml
<!-- src/KSArmory/KSArmoryGameData.xml L550-558, L567-569 -->
    <Tank Id="MirvPropellant">
      <SphericalTank>
        <LocationAsmb X="0.42" Y="0" Z="0" />
        <Material Id="Aluminum.2014(s)" />
        <OuterRadius M="0.34" />
        <WallThickness Mm="4" />
        <RoleAffinity>Thruster</RoleAffinity>
      </SphericalTank>
    </Tank>
<!-- ... -->
    <ConsumerFeedWiring Id="Chamber">
      <FeedsFrom Container="MirvPropellant" />
    </ConsumerFeedWiring>
```

7. Give the part its own `<Control />` and `<Battery>` so the thrusters answer to the bus after separation; a
   rail or director deliberately has neither. Add `Collider` boxes in part space (X up, Y along, Z across).
8. In C#, find the part on a vehicle by Id (`Vehicle.Parts`), after `AttachGameData`. Nothing else in the XML
   refers to code.

## Complete example

- [src/KSArmory/KSArmoryAssets.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryAssets.xml)[^ksarmory-ksarmoryassets] - 11 atlases, 14 materials, 14 parts
- [src/KSArmory/KSArmoryGameData.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryGameData.xml)[^ksarmory-ksarmorygamedata] - editor tags, masses, colliders, the MIRV bus
- [tools/validate-parts.py](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tools/validate-parts.py)[^ksarmory-validate-parts] - a build-time validator for Ids, paths and mesh names

## Variations and gotchas

- **Id hygiene.** A `SubPart` Id must not equal a mesh Id (the atlas registers glTF mesh names as mesh Ids).
  KSArmory uses `KSArmory_<name>_SubPart` against meshes `KSArmory_Subpart_<name>` and its validator fails the
  build if the namespaces touch; it also resolves every `InstanceOf`, `Material` and mesh Id and checks texture
  paths exist.
- **No wiring means no thrust.** The `Combustor`'s `FeedsFrom Parent="true"` is half the declaration; the part
  must also carry a `ConsumerFeedWiring` naming the container, or KSA logs "no ConsumerFeedWiring wiring for it"
  at load and nothing fires.
- **Stacking vs radial.** A stacking mount uses a plain `<Connector>`; rails, racks and dispensers use
  `<Flags>ToSurface</Flags>` plus the `Radial` and `NoFaceSnapping` tags (and cannot be roots). A node's
  `Transform/Scale` is its size: without it a mount sinks onto a tank's smaller internal node.
- **Comments drift.** The GameData comments describe sixteen nozzles; the Assets file instances twenty
  (`Rcs00` to `Rcs19`). Trust the instance list.
- **Removing a subpart breaks saves.** Subparts are paired by position on load; see
  [Change a shipped part without breaking saves](../evolve-a-shipped-part-without-breaking-saves/overview.md).

## See also

- [Overview](overview.md), [Article 1](define-a-part-with-game-data-1.md), [Article 3](define-a-part-with-game-data-3.md)
- [ksarmory/weapon-part-definitions-xml](../../../../mods/ksarmory/features/weapon-part-definitions-xml.md)
- [Define articulated subparts and mounts, animate and hide them from code](../define-and-animate-subparts/overview.md), [Change a shipped part without breaking saves](../evolve-a-shipped-part-without-breaking-saves/overview.md)

[^ksarmory-validate-parts]: KSArmory: tools/validate-parts.py
[^ksa-modlibrary]: current/decomp/KSA/ModLibrary.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-ksarmoryassets]: KSArmory: src/KSArmory/KSArmoryAssets.xml
[^ksa-program]: current/decomp/KSA/Program.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditor]: current/decomp/KSA/VehicleEditor.cs (decompiled, build 2026.10.10.5554)
[^ksa-parttemplate]: current/decomp/KSA/PartTemplate.cs (decompiled, build 2026.10.10.5554)
[^ksa-mod]: current/decomp/KSA/Mod.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-ksarmorygamedata]: KSArmory: src/KSArmory/KSArmoryGameData.xml
[^ksa-combustortemplate]: current/decomp/KSA/CombustorTemplate.cs (decompiled, build 2026.10.10.5554)
[^ksa-subpartgamedatareference]: current/decomp/KSA/SubPartGameDataReference.cs (decompiled, build 2026.10.10.5554)
