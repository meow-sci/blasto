---
type: Mod Feature
title: Car part game data
description: Make a mod part a drivable root craft in GameData XML with its own EditorTagDef category, a node connector, a Control command source, battery and light switch, a box-shaped mass for yaw inertia and colliders that stay clear of the ground at rest
mod: ksacars
category: vehicles-and-parts
techniques: [xml-asset]
hooks: []
lifecycle: [startup:asset-load]
game_types:
  - KSA.PartGameDataReference
  - KSA.SubPartGameDataReference
  - KSA.PartTemplate
  - KSA.EditorTagDefinition
  - KSA.VehicleEditor
  - KSA.ControlTemplate
  - KSA.Control
  - KSA.BatteryTemplate
  - KSA.PowerConsumerTemplate
  - KSA.SolidSphereMassTemplate
  - KSA.SolidCuboidMassTemplate
  - KSA.ColliderModule
  - KSA.ModLibrary
  - KSA.NarrowPhaseCallbacks
tags: [part-xml, gamedata, editor-tags, root-part, command-source, mass, colliders, battery, light-switch]
---

`KSACarsGameData.xml` holds what KSA simulates of each car; the springs, tyres and engine are not
in it.[^ksacars-gamedata] Each `<PartGameData>` uses the same Id as the `<Part>` in the Assets file
and is merged into it at load. The interesting choices are what it takes for a single part to be a
whole, flyable craft that starts in the editor, and how the mass and colliders are shaped so the
mod's own [wheel model](./wheel-suspension-tyre-model.md) does the driving.

## How it works

**Merging.** `PartGameDataReference` and `SubPartGameDataReference` are `PartTemplate` subclasses
flagged as game data. After every bundle has loaded, `ModLibrary.AttachGameData` finds the
`PartTemplate` with the same Id and calls `ApplyGameData`, which appends editor tags, connectors
(merging flags into an existing connector with the same Id), masses, batteries, power consumers and
module components, and copies `Control`, `EVADoor` and `DisplayName`.[^ksa-modlibrary][^ksa-parttemplate]

**Element by element** (buggy, `KSACars_Prefab_Buggy`):

| XML | What it does |
|---|---|
| `<EditorTagDef Id="Vehicles" RootPartWhitelist="true" FaceSnapTargetWhitelist="true" DiameterFilterlist="true" />` | A new editor category. Core's `Structural` tag carries the same three flags; without `RootPartWhitelist` the car could not start a craft[^ksa-editortagdef][^ksa-core-editortags] |
| `<PartGameData Id="KSACars_Prefab_Buggy" DisplayName="Beach Buggy">` | Name shown in the editor |
| `<EditorTag Value="Vehicles" />` | Puts the car in the new category |
| `<EditorTag Value="NoFaceSnapping" />` | Core tag with `FaceSnapBlacklist`; the hull is modelled +X up and face snapping would lay it on its side |
| `<Diameter M="2" />` | Shows under the 2 m stack-size filter |
| `<Connector Id="_bgConnectorAft" />` | Same Id as the node in the Assets file; a root part needs at least one connector and none flagged `ToSurface`/`FromSurface` |
| `<Control />` | A command source: `Vehicle.IsControllable` is `Parts.Controls.NumModules > 0`, which gates flight keys[^ksa-vehicle] |
| `<Battery><MaximumCapacity J="2000" /></Battery>` | Power for the light switch |
| `<PowerConsumer LightSwitch="true"><Consumed W="0.001" /></PowerConsumer>` | The part's own light switch, saved with the craft; KSA darkens emissive lenses when it is off[^ksa-powerconsumertemplate] |
| `<IVASeat>` | Seats; see [seats and EVA doors](./seats-and-eva-doors.md) |
| `<SolidSphereMass><Mass Kg="650" /><LocationAsmb X="0.60" Y="-0.25" /><Radius M="1.10" /></SolidSphereMass>` | Mass and centre of mass, 0.6 m up and toward the engine, so the car leans and squats |
| `<Collider Id><Box Id><LocationAsmb/><Collider2Asmb/><LengthX/><LengthY/><LengthZ/></Box>...` | Tub, engine and four wheel pads; boxes are axis-aligned in part space unless `Collider2Asmb` (XYZ radians) turns them[^ksa-collidermodule] |
| `<SubPartGameData Id="KSACars_BuggyDoorL_SubPart"><EVADoor SeatId=".."/></SubPartGameData>` | Game data on a subpart template |

The Eldorado uses `<SolidCuboidMass>` (2336 kg, `LengthX/Y/Z` 1.0 x 5.6 x 2.0 m) instead of a
sphere: yaw inertia from a car-sized box is what lets the tyres' side grip settle; a sphere's third
of it made the car rock at 13 deg/s.[^ksacars-claude] Its collider also declares the scoop boxes,
stowed inside the hull and moved out by code (see [scoops](./scoop-colliders-and-loose-clutter.md)).

**Colliders clear of the ground.** Every non-kitten Bepu contact pair gets the same friction
coefficient, 0.95 (`NarrowPhaseCallbacks.ResolveTerrainPairFriction`), so a wheel-shaped collider
resting on the ground would drag like a skid.[^ksa-narrowphase] The hull box and each wheel pad stop
18 cm (buggy) or 25 cm (Eldorado) above the ground at rest, clear of the springs' bump travel and
body roll, and only touch in a crash. At 10-16 cm the outside pad touched in a hard turn and
tripped the car.[^ksacars-gamedata]

**Root-part rule.** `VehicleEditor.IsAllowedAsRootPart` requires a tag whose definition has
`RootPartWhitelist`, at least one connector, and no `ToSurface`/`FromSurface` connector; a part that
fails is simply greyed out with nothing logged.[^ksa-vehicleeditor][^ksacars-notes]

## Lifecycle and timing

`startup:asset-load`. During startup `Program` calls `ModLibrary.LoadEditorTags()` (each mod's
`LoadEditorTagDefinitions`, which registers tags with the editor), then `ModLibrary.LoadAll()` (each
mod's `LoadAssetBundles`), and later, after the renderer is bound, `ModLibrary.AttachGameData()`
merges game data into part templates (Program.cs L941-L985). StarMap's `[StarMapAllModsLoaded]` is a
postfix on `LoadAll`, so it runs **before** `AttachGameData`: a mod inspecting `PartTemplate`s there
sees them without their game data merged.[^ksa-program] Modules (`Control`, `Battery`, `PowerConsumer`, `ColliderModule`,
mass) are instantiated when a vehicle is built from the template; nothing here needs code. The mod
reads back two runtime results: `Part.LightSwitch` for the headlights and `ColliderModule` entries
by `TemplateId` for the scoops.

## Key code

| Path | Role |
|---|---|
| [src/KSACars/KSACarsGameData.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsGameData.xml#L10-L31) | `EditorTagDef`, tags, diameter, connector, `Control`, battery |
| [src/KSACars/KSACarsGameData.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsGameData.xml#L57-L116) | Buggy mass and colliders with the ground-clearance reasoning |
| [src/KSACars/KSACarsGameData.xml](https://github.com/LaurensDeV/KSACars/blob/1f0a84f623ef7a8edb0792d5c46c0fecfc2abd7f/src/KSACars/KSACarsGameData.xml#L169-L195) | Eldorado cuboid mass and hull collider |

## Snippets

```xml
<!-- src/KSACars/KSACarsGameData.xml L10-L39 -->
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

    <!--
      The lamps' switch: KSA lights the emissive lenses while it is on. Next to nothing is drawn,
      because nothing on a car charges the battery and a flat one puts the lamps out.
    -->
    <PowerConsumer LightSwitch="true">
      <Consumed W="0.001" />
    </PowerConsumer>
```

```xml
<!-- src/KSACars/KSACarsGameData.xml L67-L94 -->
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
      <Box Id="KSACars_BuggyEngineCollider">
        <LocationAsmb X="0.72" Y="-1.60" Z="0" />
        <Collider2Asmb X="0" Y="0" Z="0" />
        <LengthX M="0.70" />
        <LengthY M="0.70" />
        <LengthZ M="0.94" />
      </Box>
      <Box Id="KSACars_BuggyPadFLCollider">
        <LocationAsmb X="0.38" Y="1.15" Z="0.84" />
        <Collider2Asmb X="0" Y="0" Z="0" />
        <LengthX M="0.40" />
        <LengthY M="0.60" />
        <LengthZ M="0.22" />
      </Box>
```

```csharp
// current/decomp/KSA/NarrowPhaseCallbacks.cs L154-L159
	private float ResolveTerrainPairFriction(CollidablePair pair)
	{
		if (!TryGetKittenWalkingSurfacePair(pair, out VehicleUpdateState vehicleState))
		{
			return 0.95f;
		}
```

## Game APIs touched

| API | Purpose |
|---|---|
| `PartGameDataReference` ([current/decomp/KSA/PartGameDataReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartGameDataReference.cs)) | `<PartGameData>`; registers or merges by Id[^ksa-partgamedata] |
| `ModLibrary.AttachGameData` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1863-L1875)), `PartTemplate.ApplyGameData` ([current/decomp/KSA/PartTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTemplate.cs)) | Merge of game data into part and subpart templates |
| `EditorTagDefinition` ([current/decomp/KSA/EditorTagDefinition.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/EditorTagDefinition.cs#L5-L30)) | `RootPartWhitelist`, `FaceSnapTargetWhitelist`, `DiameterFilterlist`, `FaceSnapBlacklist`, `NotaCategory` |
| `VehicleEditor.IsAllowedAsRootPart` ([current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L1262-L1280)) | Root-part gate |
| `Vehicle.IsControllable` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L589)) | `Parts.Controls.NumModules > 0` |
| `PowerConsumerTemplate` ([current/decomp/KSA/PowerConsumerTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PowerConsumerTemplate.cs#L6-L16)) | `LightSwitch`, `LightIsActive`, `Consumed` |
| `BatteryTemplate.MaximumCapacity`, `SolidSphereMassTemplate`, `SolidCuboidMassTemplate` | Battery and mass shapes |
| `ColliderModule.Template` ([current/decomp/KSA/ColliderModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ColliderModule.cs#L11-L19)) | `Box`, `Capsule`, `Cylinder`, `Sphere`, `ConvexHull` |
| `NarrowPhaseCallbacks.ResolveTerrainPairFriction` ([current/decomp/KSA/NarrowPhaseCallbacks.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/NarrowPhaseCallbacks.cs#L154-L172)) | 0.95 friction for every non-kitten pair |

## Pitfalls and version notes

- **All three gates of the editor fail silently.** Missing `RootPartWhitelist`, no connector, or a
  surface-attach connector each just grey the part out.[^ksacars-notes]
- **Connector Ids must match** between the Assets `<Connector>` (with the transform) and the
  GameData `<Connector>` (flags); `ApplyGameData` merges them by Id.
- **A part with no `<Control />` is not controllable**: no flight keys, so no driving.
- **Mass shape changes handling.** The inertia comes from the mass template's geometry, not the
  mesh.
- **Every collider has the same 0.95 friction** in this build, so wheels cannot be colliders.
  `docs/BLOCKED-ON-KSA.md` lists per-collider friction as what would change this.
- **`CrashTolerance`** is an attribute on the Assets `<Part>`, not in GameData; see
  [crash veto](./crash-veto.md).

## See also

- [Car part assets: GLB, PBR materials and subparts](./car-part-assets-glb-pbr.md)
- [Seats and EVA doors](./seats-and-eva-doors.md)
- [Wheel, suspension and tyre model](./wheel-suspension-tyre-model.md)
- [Headlights as spotlights](./headlights-as-spotlights.md)
- [Scoop colliders and loose clutter](./scoop-colliders-and-loose-clutter.md)
- [Vehicle library presets](./vehicle-library-presets.md)
- [Define a driveable part with colliders, a mass shape and its own editor category](../../../howto/vehicles-and-parts/parts/define-a-part-with-game-data/define-a-part-with-game-data-1.md) - how-to article demonstrating this feature
- [Make a single part a whole craft with a RootPartWhitelist tag](../../../howto/vehicles-and-parts/parts/ship-ready-made-craft/ship-ready-made-craft-2.md) - how-to article demonstrating this feature

[^ksacars-gamedata]: src/KSACars/KSACarsGameData.xml
[^ksacars-notes]: docs/KSA-MODDING-NOTES.md
[^ksacars-claude]: CLAUDE.md
[^ksa-partgamedata]: KSA.PartGameDataReference (decompiled)
[^ksa-parttemplate]: KSA.PartTemplate (decompiled)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled)
[^ksa-editortagdef]: KSA.EditorTagDefinition (decompiled)
[^ksa-vehicleeditor]: KSA.VehicleEditor (decompiled)
[^ksa-core-editortags]: Core CoreEditorTagsGameData.xml
[^ksa-program]: KSA.Program (decompiled)
[^ksa-vehicle]: KSA.Vehicle (decompiled)
[^ksa-narrowphase]: KSA.NarrowPhaseCallbacks (decompiled)
[^ksa-powerconsumertemplate]: KSA.PowerConsumerTemplate (decompiled)
[^ksa-collidermodule]: KSA.ColliderModule (decompiled)
