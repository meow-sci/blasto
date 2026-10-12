---
type: Mod Feature
title: Rounds drawn as hidden launcher subparts flown by transform writes
description: Each tube's missile is a pre-declared subpart of the launcher, seated in its tube or shrunk to a millimetre while unfired, then placed every rendered frame at the mod-simulated round's position and carried attitude by writing its parent-relative transform
mod: ksarmory
category: rendering
techniques: [subpart-transform-write, scale-to-hide, xml-asset, sim-ksa-split]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials]
lifecycle: [startup:asset-load, frame:draw-ui, frame:celestials]
game_types:
  - KSA.Part
  - KSA.Vehicle
  - KSA.PartTemplate
  - KSA.PartInstance
  - KSA.PartModel
  - KSA.ModLibrary
  - KSA.PartTreeRenderData
tags: [subpart, projectile, missile, transform, scale, attitude, magazine, render-only]
---

KSArmory's rounds are simulated by the mod, not spawned as KSA vehicles, yet the player sees real
missile meshes leave the tubes, nose over, and fly kilometres to the target. Each tube owns a
round body: a `<SubPart>` of the launcher part, declared in XML once per tube. While a tube is
loaded the body is seated in it; once spent it is shrunk to a millimetre; while its round is in
the air the mod writes the body's parent-relative position, rotation and scale every rendered
frame from the simulated round.[^ksarmory-readme][^ksarmory-weaponsystem]

Nothing is created or destroyed at runtime, so the save never sees a round and there is no
spawning cost; the price is a fixed number of bodies per launcher and a launcher-relative
placement that has to undo the craft's motion.

## How it works

**Declare one body per tube (asset load).** `KSArmoryAssets.xml` declares a template
(`<SubPart Id="KSArmory_Missile_SubPart">` with a `PartModel` and `MeshView`) and, inside the
launcher's `<Part>`, twelve instances `KSArmory_Launcher_Missile00..11 InstanceOf=...`, each
parked at `X="-4"` with `Scale="0.001"`, plus a matching fin-set instance per round. KSA
deserializes each `<SubPart>` under a `<Part>` into `PartTemplate.SubPartInstances`
(`[XmlElement("SubPart")]`, PartTemplate.cs L20-L21), and the `Part` constructor builds one child
`Part` per instance, applying its `<Transform>` (Part.cs L1504-L1524).[^ksarmory-assets-xml][^ksa-parttemplate][^ksa-part]

**Find the bodies.** On first adoption of a launcher, `LauncherPart.FindMissiles` collects every
subpart whose runtime `Id` contains the munition's `BodyMarker` (default `"Missile"`) in
declaration order, so tube N always maps to the same body; `FindFins` does the same with
`FinMarker`.[^ksarmory-launcherpart][^ksarmory-munitionprofile]

**Decide each body's state per frame** (`Magazine.Plan`, pure C#):[^ksarmory-magazine]

| `TubeVisual` | Body treatment |
|---|---|
| `InFlight` | Placed at the round (below) |
| `Loaded` | `TrySeatMissile`: centre half a body length behind the tube mouth, nose along the tube axis, scale 1 |
| `Spent` | Seated first, *then* `HideMissile` (scale `1e-3`), so its transform is never stale when it next fires |

The magazine is authoritative about which tube fires next, and refuses a tube whose previous round
is still flying, because two rounds sharing a body would flip it between their positions.
`SyncRoundBodies` also detects that case and warns once.

**Place a body at its round** (`LauncherPart.TryPlaceMissile`):

1. Position, in the launcher part's frame: the launch anchor (tube mouth in the part frame at
   launch), carried through how far the craft has turned since launch (`TubeGeometry.CarryAnchor`),
   plus the round's travel since launch rotated from ecliptic into the part frame with the craft's
   *current* attitude (`TubeGeometry.BodyPositionPartFrame`).[^ksarmory-tubegeometry]
2. Rotation: `asmb2Part * ecl2Asmb * attitudeEcl` (`BodyRotationPartFrame`), where
   `asmb2Part = Conjugate(launcher.Asmb2VehicleAsmb)` and `ecl2Asmb = Conjugate(platform.Asmb2Ego)`.
3. Write `PositionParentAsmb`, `Asmb2ParentAsmb`, their `*Safe` twins, `Scale = 1`, then
   `ResetCachedPosMatrixValues()`.

Travel is `OffsetFromPlatform - LaunchOffset`, a difference of two platform-relative offsets, so the
craft's ~29.8 km/s of ecliptic motion cancels; the anchor is relative to the physics origin the
subpart is drawn against, not the platform's analytic orbit position.[^ksarmory-interceptor]

**Carry the attitude, do not derive it** (`BodyAttitude.Turn`). The drawn attitude starts at the
release attitude (the tube's pose at launch, at the launcher's roll) and each frame turns toward the
local airflow by at most `720 deg/s * authority * (round.Age - lastAge)`, where authority ramps with
dynamic pressure (density ratio times speed squared, 2 to 40 m/s at sea level). In vacuum nothing
turns; paused, nothing turns; and the turn axis is square to the nose, so no roll is added.[^ksarmory-bodyattitude]

**Fins.** A single-set munition scales its fin subpart radially from stowed to deployed
(`TubeGeometry.FinScale`); a four-blade set (`FinsPerRound = 4`, the B61) places each blade at its
hinge station with a roll and a drawn-only deflection from the steering command.

**Shells are different.** A gun round's `Tube` is the negative barrel number (`RoundLabel`), which
keeps it out of the magazine's index range and off every missile body.[^ksarmory-roundlabel] Shells
get no subpart: `TurnShellBodies` only carries their attitude, and `DrawShellBodies` submits each as
an instance of the munition's `BodyModel` template mesh (`KSArmory_Mk42Shell_SubPart`, a template no
`<Part>` instances), resolved through `ModLibrary.Get<PartTemplate>` and `PartModel.Get`.[^ksarmory-weaponsystem][^ksa-partmodel]

## Lifecycle and timing

| Point | Detail |
|---|---|
| Asset load | Templates and per-tube instances are deserialized from `KSArmoryAssets.xml` (listed in `mod.toml`), so the bodies exist on the part from the moment a craft is built (`startup:asset-load`) |
| Per frame | `SyncRoundBodies` for every system, called from `StepSimulation` *outside* the simulated-step gate (KSArmoryMod.cs L812-L819), inside `StepOnce`: the `[StarMapAfterGui]` postfix (Program.cs L2268) or the `OnFrameCelestials` prefix (L2320)[^ksarmory-mod] |
| Why outside the gate | Placement is drawing: on a frame that advanced no simulated time the craft can still move under the camera, and a body not rewritten would lag it. Placement reads round state and changes none |
| Order | After the fire loop (`WeaponSystem.Update`, L776), so a round launched this frame is already in `_rounds` |
| Engine consumes | `Render` (L2324) -> `UpdateRenderingResources` (L2468) -> `vehicle.UpdateRenderData` (L4355) -> `PartTree.UpdateRenderData` -> `PartTreeRenderData.EnsureBuilt`, which repacks transforms flagged dirty by the writes[^ksa-program][^ksa-parttreerenderdata] |

The craft attitude used (`Asmb2Ego`) and the round state are both the ones applied this frame
(`ApplyVehicleSolvers` in `PrepareFrame`), so the body and the overlay drawn in the same GUI hook share
one epoch.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L1914-L2129) | `SyncRoundBodies`: per-round placement, fins, seat/hide pass |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L2134-L2186) | `TurnShellBodies`, `DrawShellBodies` for subpart-less shells |
| [src/KSArmory/Ksa/LauncherPart.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/LauncherPart.cs#L256-L621) | `TrySeatMissile`, fin placement, `HideMissile`, `TryPlaceMissile`; `FindMissiles` at L158-L182 |
| [src/KSArmory/Sim/TubeGeometry.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TubeGeometry.cs#L401-L488) | Anchor carry, part-frame body position and rotation, release attitude, fin scale |
| [src/KSArmory/Sim/Magazine.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Magazine.cs) | Tube ownership and `TubeVisual` plan |
| [src/KSArmory/Sim/BodyAttitude.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BodyAttitude.cs) | Carried attitude, dynamic-pressure authority |
| [src/KSArmory/Sim/FireGeometry.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/FireGeometry.cs#L16-L131) | `NoseAxis` (+X) and `RotationFromNose` |
| [src/KSArmory/Sim/RoundLabel.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/RoundLabel.cs) | Tube vs barrel encoding in one field |
| [src/KSArmory/KSArmoryAssets.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryAssets.xml#L254-L287) | Round and fin templates; per-tube instances at L789-L938 |

## Snippets

```xml
<!-- src/KSArmory/KSArmoryAssets.xml L789-L801 -->
    <!-- Twelve rounds, parked below the vehicle and shrunk away until fired. -->
    <SubPart Id="KSArmory_Launcher_Missile00" InstanceOf="KSArmory_Missile_SubPart">
      <Transform>
        <Position X="-4.00000" Y="0.00000" Z="0.00000" />
        <Scale X="0.00100" Y="0.00100" Z="0.00100" />
      </Transform>
    </SubPart>
    <SubPart Id="KSArmory_Launcher_Missile01" InstanceOf="KSArmory_Missile_SubPart">
      <Transform>
        <Position X="-4.00000" Y="0.00000" Z="0.00000" />
        <Scale X="0.00100" Y="0.00100" Z="0.00100" />
      </Transform>
    </SubPart>
```

```csharp
// src/KSArmory/Ksa/LauncherPart.cs L581-L615
    public static bool TryPlaceMissile(
        Vehicle platform, Part launcher, Part missile,
        double3 launchAnchorPartFrame, double3 travelEcl, doubleQuat attitudeEcl,
        doubleQuat launchAttitude,
        out double3 position, out doubleQuat rotation)
    {
        position = Vec.Zero;
        rotation = doubleQuat.Identity;

        try
        {
            doubleQuat ecl2Asmb = doubleQuat.Conjugate(platform.Asmb2Ego);
            doubleQuat asmb2Part = doubleQuat.Conjugate(launcher.Asmb2VehicleAsmb);

            // ...
            doubleQuat sinceLaunch = doubleQuat.Concatenate(launchAttitude, ecl2Asmb);

            position = TubeGeometry.BodyPositionPartFrame(launchAnchorPartFrame, travelEcl,
                                                          ecl2Asmb, asmb2Part, sinceLaunch);
            if (!Vec.IsFinite(position)) return false;

            rotation = TubeGeometry.BodyRotationPartFrame(attitudeEcl, ecl2Asmb, asmb2Part);

            missile.PositionParentAsmb = position;
            missile.PositionParentAsmbSafe = position;
            missile.Asmb2ParentAsmb = rotation;
            missile.Asmb2ParentAsmbSafe = rotation;
            missile.Scale = Shown;
            missile.ResetCachedPosMatrixValues();
            return true;
        }
```

```csharp
// src/KSArmory/Ksa/LauncherPart.cs L547-L559
    public static void HideMissile(Part missile)
    {
        try
        {
            if (missile.Scale.Equals(Hidden)) return;      // already stowed; skip the cache reset
            missile.Scale = Hidden;
            missile.ResetCachedPosMatrixValues();
        }
        catch
        {
            // Nothing to do; a round that will not hide is cosmetic, not fatal.
        }
    }
```

```csharp
// src/KSArmory/Sim/TubeGeometry.cs L407-L411
    public static double3 BodyPositionPartFrame(double3 anchorPartFrame, double3 travelEcl,
                                                doubleQuat ecl2Asmb, doubleQuat asmb2Part,
                                                doubleQuat sinceLaunchAsmb)
        => CarryAnchor(anchorPartFrame, sinceLaunchAsmb, asmb2Part)
           + asmb2Part * (ecl2Asmb * travelEcl);
```

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `PartTemplate.SubPartInstances` (`<SubPart>`), `PartInstance.InstanceOf`, `PartInstance.Transform` | [current/decomp/KSA/PartTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTemplate.cs#L20-L21), [current/decomp/KSA/PartInstance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartInstance.cs#L15-L37) | Asset-time declaration of one body per tube |
| `Part(string, PartTemplate, PartInstance?, Part?)` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1490-L1524) | Engine builds the subparts and applies the parked transform |
| `Part.SubParts`, `Part.Id` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1127) | Find bodies and fins by marker |
| `Part.PositionParentAsmb`, `Asmb2ParentAsmb`, `Scale`, `*Safe`, `ResetCachedPosMatrixValues` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L756-L831) | Seat, hide and fly the body |
| `Part.Asmb2VehicleAsmb`, `Part.Template` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L724-L738) | Undo the launcher's mounting; read a body's model |
| `Vehicle.Asmb2Ego`, `Vehicle.CenterOfMassAsmb` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L502-L565) | Ecliptic to vehicle-assembly rotation |
| `ModLibrary.Get<PartTemplate>`, `PartModel.Get(PartModelModule.Template)` | [current/decomp/KSA/PartModel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModel.cs#L417) | A template's mesh for subpart-less shells |
| `Vehicle.UpdateRenderData`, `IsLargeEnoughToRender` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3707-L3719) | Engine side: the per-craft cull that hides far rounds with their launcher |

## Pitfalls and version notes

- **Bodies are culled with the launcher.** `Vehicle.UpdateRenderData` skips every part of a craft
  under a pixel across (`IsLargeEnoughToRender`, Vehicle.cs L3707-L3719), and a round body is a subpart of the launcher, so a
  shell kilometres downrange vanishes when the launcher shrinks. The mod patches around this; see
  [round body draw hooks](./round-body-draw-hooks.md).[^ksa-vehicle][^ksarmory-roundbodydrawhook]
- **Never zero scale.** Hidden is `1e-3`, not 0: a zero-scaled matrix is singular. `HideMissile`
  skips the write when already hidden, avoiding a needless transform rebuild.
- **Seat before hiding.** `Magazine` has no "hide without seating" state: an unseated hidden body
  keeps a stale transform and, per the code comment, can draw one frame at the old pose when its
  tube fires. In 5554 every transform setter invalidates the cache itself (Part.cs L766, L780, L829),
  so whether that artifact still reproduces is unverified; seating is cheap either way.[^ksarmory-magazine][^ksa-part]
- **Body count vs tube count come from two files.** The art declares bodies, the profile declares
  tubes; `SyncRoundBodies` loops over the minimum (the system warns once at adoption when they
  differ), because an out-of-range index thrown from the frame hook ten times trips the mod's fault
  limit.
- **Use local velocity for attitude.** An ecliptic velocity carries the planet's orbital motion and
  would point every round the same way.[^ksarmory-bodyattitude]
- **A refused write falls back to tracers.** A placement exception sets `RoundBodiesWork = false`
  for the system and announces it; the operator switch `UseRoundBodies` hides every body.
- **Fixed capacity.** Twelve bodies means at most twelve rounds of that launcher in the air;
  belt-fed guns avoid the limit by drawing shells as instances instead.

## See also

- [Articulated turrets by rewriting subpart transforms](./articulated-mount-subparts.md): the same write path for mounts
- [Round body draw hooks](./round-body-draw-hooks.md): drawing bodies past the launcher's cull, and loose bodies
- [Mod-simulated rounds](./mod-simulated-rounds.md): where `TravelSinceLaunch` and the launch anchor come from
- [Consume the applied sim step](./consume-applied-sim-step.md): why placement and stepping are separated
- [Weapon part definitions in XML](./weapon-part-definitions-xml.md): the `<SubPart>` declarations
- [Per-part store mass](./per-part-store-mass.md): the mass a fired round takes with it
- [Scale a pre-declared subpart to nothing until it is needed](../../../howto/rendering/meshes/hide-parts-or-subparts-from-rendering/hide-parts-or-subparts-from-rendering-2.md) - how-to article demonstrating this feature
- [Anchor to the engine's own render position and add travel since launch](../../../howto/rendering/overlays/choose-a-render-frame-and-anchor-effects/choose-a-render-frame-and-anchor-effects-2.md) - how-to article demonstrating this feature
- [Hide subparts by scale and fly them as projectiles of their launcher](../../../howto/vehicles-and-parts/parts/define-and-animate-subparts/define-and-animate-subparts-4.md) - how-to article demonstrating this feature

[^ksarmory-weaponsystem]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksarmory-launcherpart]: src/KSArmory/Ksa/LauncherPart.cs
[^ksarmory-magazine]: src/KSArmory/Sim/Magazine.cs
[^ksarmory-bodyattitude]: src/KSArmory/Sim/BodyAttitude.cs
[^ksarmory-roundlabel]: src/KSArmory/Sim/RoundLabel.cs
[^ksarmory-tubegeometry]: src/KSArmory/Sim/TubeGeometry.cs
[^ksarmory-munitionprofile]: src/KSArmory/Sim/MunitionProfile.cs
[^ksarmory-interceptor]: src/KSArmory/Sim/Interceptor.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-assets-xml]: src/KSArmory/KSArmoryAssets.xml
[^ksarmory-roundbodydrawhook]: src/KSArmory/Ksa/RoundBodyDrawHook.cs
[^ksarmory-readme]: README.md
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-parttemplate]: KSA.PartTemplate (decompiled, build 2026.10.10.5554)
[^ksa-partmodel]: KSA.PartModel (decompiled, build 2026.10.10.5554)
[^ksa-parttreerenderdata]: KSA.PartTreeRenderData (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
