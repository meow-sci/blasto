---
type: How-To
title: Define a driveable part with colliders, a mass shape and its own editor category
description: Write an Assets bundle with a GLB atlas, PBR material and subparts, and a GameData bundle with an editor tag, mass shape and colliders that make a single part a flyable craft
category: vehicles-and-parts
group: parts
topic: define-a-part-with-game-data
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksacars/features/car-part-gamedata, mods/ksacars/features/car-part-assets-glb-pbr]
techniques: [xml-asset, glb-model]
hooks: [mod.toml assets]
lifecycle: [startup:asset-load]
game_types:
  - KSA.PartTemplate
  - KSA.PartGameDataReference
  - KSA.EditorTagDefinition
  - KSA.ColliderModule
  - KSA.ModLibrary
  - KSA.SolidSphereMassTemplate
  - KSA.SolidCuboidMassTemplate
  - KSA.NarrowPhaseCallbacks
tags: [xml, part, glb, pbr, collider, mass, editor-tag, friction]
---
Outcome: a car appears in the vehicle editor under its own "Vehicles" category as a single part that can
start a craft, with a GLB model, PBR material, a sensible centre of mass and box colliders that keep it
off the ground at rest. KSACars does this for its two cars; the recipe is generic.

Prerequisites: a mod folder with a `mod.toml`; a GLB authored with the art contract below. No code is needed
for this article. Demonstrated by [ksacars/car-part-gamedata](../../../../mods/ksacars/features/car-part-gamedata.md) and [ksacars/car-part-assets-glb-pbr](../../../../mods/ksacars/features/car-part-assets-glb-pbr.md).

## Background: how the game does it

The bundle format, merge order and the full element table are in the [overview](overview.md). Facts this
article leans on:

- `<MeshAtlas Path=...>` loads a GLB and registers **one `MeshReference` per glTF mesh, Id = the mesh name**
  (not the node name); the first registration of an Id wins and a later duplicate silently reuses the existing
  mesh [current/decomp/KSA/MeshAtlasFileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshAtlasFileReference.cs#L21-L45)[^ksa-meshatlasfilereference]. Paths resolve against the mod folder.
- `PartModelModule.Template` is `<PartModel>` with `<Mesh>`, `<Material>`, `<RayTracing>`, `<ShadowCaster>`,
  `<Internal>`; `<MeshView>` names the editor's preview mesh [current/decomp/KSA/PartModelModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelModule.cs#L17-L45)[^ksa-partmodelmodule]
  [current/decomp/KSA/MeshViewModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshViewModule.cs#L8-L12)[^ksa-meshviewmodule].
- Mass shapes derive from `AsmbVolumetricMassTemplate`: a `<Mass Kg>` (or `Density`/`Material`) plus
  `LocationAsmb` for the centre, and shape fields (`Radius`, or `LengthX/Y/Z`)
  [current/decomp/KSA/AsmbVolumetricMassTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AsmbVolumetricMassTemplate.cs#L1-L30)[^ksa-asmbvolumetricmasstemplate] [current/decomp/KSA/SolidSphereMassTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SolidSphereMassTemplate.cs#L1-L12)[^ksa-solidspheremasstemplate]
  [current/decomp/KSA/SolidCuboidMassTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SolidCuboidMassTemplate.cs#L1-L20)[^ksa-solidcuboidmasstemplate].
- Colliders are `<Collider>` holding `<Box>`, `<Capsule>`, `<Cylinder>`, `<Sphere>` or `<ConvexHull>`, each with
  `LocationAsmb`, `Collider2Asmb` (XYZ radians) and shape lengths [current/decomp/KSA/ColliderModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ColliderModule.cs#L9-L24)[^ksa-collidermodule]
  [current/decomp/KSA/BoxColliderTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BoxColliderTemplate.cs#L1-L25)[^ksa-boxcollidertemplate].
- Every non-kitten collider pair gets the same terrain friction coefficient, `0.95f`
  [current/decomp/KSA/NarrowPhaseCallbacks.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NarrowPhaseCallbacks.cs#L154-L159)[^ksa-narrowphasecallbacks], so a wheel-shaped collider resting on the ground drags like a skid.
- A part can start a craft only if one of its tags is `RootPartWhitelist`, it has at least one connector, and no
  connector has the `ToSurface` (2) or `FromSurface` (4) flag [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L1262-L1280)[^ksa-vehicleeditor]. Failing parts
  are greyed out in the picker; nothing is logged.

## Lifecycle and timing

Pure content (`startup:asset-load`): `Program` calls `ModLibrary.LoadEditorTags()`, then `LoadAll()` (reads the
bundles), and later `AttachGameData()` merges `<PartGameData>` into the `<Part>` with the same Id
[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L935-L990)[^ksa-program]. The tag definitions are read in the first pass, so an `EditorTagDef` can sit in the
same file as the parts that use it. Modules (`Control`, `Battery`, `PowerConsumer`, `Collider`, mass) are
instantiated when a vehicle is built from the template. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for where a code mod sees this.

## Steps

1. List both XML files in `mod.toml`. List them explicitly: a file left out is never loaded, and nothing reports
   it:

```toml
# src/KSACars/mod.toml L1-17
# KSA reads this as a content mod, StarMap reads it as a code mod. One folder does both.

name = "FastAndPurrious"

# Part definitions. These sit at the mod root, next to Meshes/ and Textures/, mirroring the
# layout of Content/Core - the relative paths inside them then resolve the same way whether the
# loader reads them against the mod root or against the XML file's own directory.
# Listed one by one, not globbed: a file left out of this list is simply never loaded, and
# nothing reports it. An asset it declares then resolves to null at the point something asks
# for it, which for a part is a crash inside KSA rather than a missing model.
assets = [
    "KSACarsAssets.xml",
    "KSACarsGameData.xml",
    "KSACarsSounds.xml",
]

[StarMap]
```

2. Author the art: part space is +X up, +Y forward, +Z left (KSACars' convention, not the game's); export with
   Blender's Y-up conversion off; bake transforms into vertices because the atlas loader ignores the node
   graph; give every mesh a mod prefix because mesh Ids are global; keep a `_VM` twin for each mesh used as the
   editor preview.
3. Write the **Assets** bundle: the atlas, the material, one `SubPart` per body and a `Part` that instances the
   subparts and declares the connector:

```xml
<!-- src/KSACars/KSACarsAssets.xml L17-24, L31-39 -->
  <MeshAtlas Path="Meshes/KSACars_Buggy.glb" />

  <PbrMaterial Id="KSACars_Buggy_Material">
    <Diffuse Path="Textures/KSACars_Buggy_Diffuse.png" Category="Vessel" />
    <Normal Path="Textures/KSACars_Buggy_Normal.png" Category="Vessel" />
    <AoRoughMetal Path="Textures/KSACars_Buggy_PBR.png" Category="Vessel" />
    <Emissive Path="Textures/KSACars_Buggy_Emissive.png" Category="Vessel" />
  </PbrMaterial>
<!-- ... -->
  <SubPart Id="KSACars_BuggyBody_SubPart">
    <PartModel Id="KSACars_Subpart_BuggyBody_Model">
      <Mesh Id="KSACars_Subpart_BuggyBody" />
      <Material Id="KSACars_Buggy_Material" />
    </PartModel>
    <MeshView>
      <Mesh Id="KSACars_Subpart_BuggyBody_VM" />
    </MeshView>
  </SubPart>
```

```xml
<!-- src/KSACars/KSACarsAssets.xml L167-169, L171-175, L254-261 -->
  <Part Id="KSACars_Prefab_Buggy" CrashTolerance="1e12">

    <SubPart Id="KSACars_Buggy_Body" InstanceOf="KSACars_BuggyBody_SubPart" />
<!-- ... -->
    <SubPart Id="KSACars_Buggy_WheelFL" InstanceOf="KSACars_BuggyWheelFront_SubPart">
      <Transform>
        <Position X="0.34000" Y="1.15000" Z="0.84000" />
      </Transform>
    </SubPart>
<!-- ... -->
    <!-- Underside attach node, at the ground plane and facing down. -->
    <Connector Id="_bgConnectorAft">
      <Transform>
        <Position X="0.00000" />
        <Rotation Z="3.14159" />
        <Scale X="2.00000" Y="2.00000" Z="2.00000" />
      </Transform>
    </Connector>
```

4. Write the **GameData** bundle. First the editor category: copy the three flags from Core's `Structural` tag,
   or the part silently loses root-part eligibility, face snapping and diameter filtering (compare Core's own
   [current/Content/Core/CoreEditorTagsGameData.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/CoreEditorTagsGameData.xml#L12)[^content-coreeditortagsgamedata]). Then the `PartGameData` with the same Id as the `Part`:

```xml
<!-- src/KSACars/KSACarsGameData.xml L10-31, L37-39 -->
  <!--
    The cars' own editor category. Core's tags that can start a craft carry the same three flags, and
    without them a car silently loses the ability to be a craft's root part.
  -->
  <EditorTagDef Id="Vehicles" RootPartWhitelist="true" FaceSnapTargetWhitelist="true"
                DiameterFilterlist="true" />

  <PartGameData Id="KSACars_Prefab_Buggy" DisplayName="Beach Buggy">
    <EditorTag Value="Vehicles" />
    <!-- +X is up on this hull too, and face snapping would lay it on its side. -->
    <EditorTag Value="NoFaceSnapping" />
    <Diameter M="2" />

    <!-- A node connector, so the buggy can start a craft. -->
    <Connector Id="_bgConnectorAft" />

    <!-- A command source, so a craft that is only this has something to control it. -->
    <Control />

    <Battery>
      <MaximumCapacity J="2000" />
    </Battery>
<!-- ... -->
    <PowerConsumer LightSwitch="true">
      <Consumed W="0.001" />
    </PowerConsumer>
```

5. Choose the mass shape for the inertia you need, and put the centre of mass where the vehicle should lean.
   KSACars uses a sphere for the buggy and a cuboid for the car, because yaw inertia from a car-sized box lets
   the tyres' side grip settle (a sphere gave a third of it and the car rocked at 13 deg/s):

```xml
<!-- src/KSACars/KSACarsGameData.xml L57-65 -->
    <!--
      A fibreglass Manx on a VW pan is about 650 kg, with its centre of mass 0.6 m up and toward the
      engine. At the part origin, on the ground, nothing would lean in a corner or squat under power.
    -->
    <SolidSphereMass>
      <Mass Kg="650" />
      <LocationAsmb X="0.60" Y="-0.25" />
      <Radius M="1.10" />
    </SolidSphereMass>
```

```xml
<!-- src/KSACars/KSACarsGameData.xml L169-181 -->
    <!--
      5,150 lb as shipped; the big V8 and the front drive put the mass forward. A box the car's size
      rather than a sphere, because yaw inertia is what lets the side grip settle: a sphere gave a
      third of it, the grip overcorrected every step, and the car rocked on its wheels at 13 deg/s
      with the drive spent holding the rocking.
    -->
    <SolidCuboidMass>
      <Mass Kg="2336" />
      <LocationAsmb X="0.55" Y="0.25" />
      <LengthX M="1.0" />
      <LengthY M="5.6" />
      <LengthZ M="2.0" />
    </SolidCuboidMass>
```

6. Add colliders that stay clear of the ground at rest, since terrain friction is fixed. The buggy's wheel pads
   end 18 cm above the ground (25 cm on the car), clear of the suspension's bump travel and body roll; at 10 to
   16 cm the outside pad touched in a hard turn and tripped the car:

```xml
<!-- src/KSACars/KSACarsGameData.xml L67-80, L88-94, L116-117 -->
    <!--
      The springs hold it up, not these. KSA's terrain friction is a fixed 0.95 for every collider, so
      a wheel box on the ground would drag like a skid: each tyre's pad ends 18 cm above the ground at
      rest, clear of the springs' 14 cm of bump and the lean in a corner, so it only touches in a crash.
      At 10 cm the outside pad reaches the ground in a hard turn and trips the car.
    -->
    <Collider Id="KSACars_BuggyCollider">
      <Box Id="KSACars_BuggyTubCollider">
        <LocationAsmb X="0.75" Y="0.30" Z="0" />
        <Collider2Asmb X="0" Y="0" Z="0" />
        <LengthX M="0.70" />
        <LengthY M="3.10" />
        <LengthZ M="1.20" />
      </Box>
<!-- ... -->
      <Box Id="KSACars_BuggyPadFLCollider">
        <LocationAsmb X="0.38" Y="1.15" Z="0.84" />
        <Collider2Asmb X="0" Y="0" Z="0" />
        <LengthX M="0.40" />
        <LengthY M="0.60" />
        <LengthZ M="0.22" />
      </Box>
<!-- ... -->
    </Collider>
  </PartGameData>
```

7. Start the game. In the editor your category appears in the part picker and the part is selectable as a
   root. If it is greyed out, check the root-part rule above. Errors in the XML appear as a logged exception
   from `XmlLoader` and the file loads as nothing.

## Complete example

- [src/KSACars/KSACarsAssets.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsAssets.xml)[^ksacars-ksacarsassets] - art and structure
- [src/KSACars/KSACarsGameData.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsGameData.xml)[^ksacars-ksacarsgamedata] - simulation data
- [src/KSACars/mod.toml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/mod.toml)[^ksacars-mod] - the manifest

## Variations and gotchas

- **Id hygiene.** A `SubPart` Id must not equal a mesh Id: the atlas registers every glTF mesh name as a mesh Id
  and the loader keeps the first, so two assets under one key behave as one. KSACars suffixes subparts
  `_SubPart` and prefixes meshes `Subpart_`.
- **Rotations are radians.** `<Rotation Z="3.14159"/>` is 180 degrees.
- **Cars need `Control`.** `Vehicle.IsControllable` is true only when the tree has `Control` modules; a part
  without `<Control />` flies as a prop. Omit it deliberately for pure props (article 3).
- **Light switch.** `<PowerConsumer LightSwitch="true">` plus a `<Battery>` gives the part its own light
  switch; KSA darkens emissive lenses when the switch is off.
- **Emissive.** The `<Emissive>` texture is what lights when the switch is on.
- **Updates.** Subpart lists are matched by position on load, so changing them breaks saves; see
  [Change a shipped part without breaking saves](../evolve-a-shipped-part-without-breaking-saves/overview.md).

## See also

- [Overview](overview.md), [Article 2](define-a-part-with-game-data-2.md), [Article 3](define-a-part-with-game-data-3.md)
- [ksacars/car-part-gamedata](../../../../mods/ksacars/features/car-part-gamedata.md), [ksacars/car-part-assets-glb-pbr](../../../../mods/ksacars/features/car-part-assets-glb-pbr.md)
- [Ship whole-vehicle presets and single-part craft with a part mod](../ship-ready-made-craft/overview.md), [Add crew seats and EVA doors to a part](../add-crew-seats-and-eva-doors/overview.md), [Define articulated subparts and mounts, animate and hide them from code](../define-and-animate-subparts/overview.md)

[^ksa-meshatlasfilereference]: current/decomp/KSA/MeshAtlasFileReference.cs (decompiled, build 2026.10.10.5554)
[^ksa-collidermodule]: current/decomp/KSA/ColliderModule.cs (decompiled, build 2026.10.10.5554)
[^ksacars-mod]: KSACars: src/KSACars/mod.toml
[^ksa-program]: current/decomp/KSA/Program.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditor]: current/decomp/KSA/VehicleEditor.cs (decompiled, build 2026.10.10.5554)
[^ksa-partmodelmodule]: current/decomp/KSA/PartModelModule.cs (decompiled, build 2026.10.10.5554)
[^ksacars-ksacarsgamedata]: KSACars: src/KSACars/KSACarsGameData.xml
[^ksa-asmbvolumetricmasstemplate]: current/decomp/KSA/AsmbVolumetricMassTemplate.cs (decompiled, build 2026.10.10.5554)
[^ksa-boxcollidertemplate]: current/decomp/KSA/BoxColliderTemplate.cs (decompiled, build 2026.10.10.5554)
[^ksa-solidcuboidmasstemplate]: current/decomp/KSA/SolidCuboidMassTemplate.cs (decompiled, build 2026.10.10.5554)
[^ksacars-ksacarsassets]: KSACars: src/KSACars/KSACarsAssets.xml
[^ksa-meshviewmodule]: current/decomp/KSA/MeshViewModule.cs (decompiled, build 2026.10.10.5554)
[^ksa-narrowphasecallbacks]: current/decomp/KSA/NarrowPhaseCallbacks.cs (decompiled, build 2026.10.10.5554)
[^content-coreeditortagsgamedata]: current/Content/Core/CoreEditorTagsGameData.xml (game content, build 2026.10.10.5554)
[^ksa-solidspheremasstemplate]: current/decomp/KSA/SolidSphereMassTemplate.cs (decompiled, build 2026.10.10.5554)
