---
type: Mod Feature
title: Articulated turrets by rewriting subpart transforms every frame
description: A single editor part traverses, elevates, recoils and spins its barrels because the mod finds named subparts at runtime and writes their parent-relative position, rotation and scale from rate-limited drives each frame
mod: ksarmory
category: vehicles-and-parts
techniques: [subpart-transform-write, rate-limited-drive, sim-ksa-split, latch-on-refusal]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials]
lifecycle: [frame:draw-ui, frame:celestials]
game_types: [KSA.Part, KSA.Vehicle, KSA.PartTree, KSA.PartTreeRenderData, KSA.Program]
tags: [subpart, turret, traverse, elevation, recoil, transform, drive, rate-limit, trunnion]
---

KSArmory's mounts move: the Pantsir's turret traverses and its missile pods and cannon elevate,
the Phalanx and Mk 42 train and elevate, the Mk 42 barrel recoils, and the M197 chin turret
swings its yoke, pitches its gun, extends an actuator rod, drags its ammunition belt and spins
its barrel cluster. None of that is a KSA part module. Each mount is one inert part whose moving
pieces are `<SubPart>` entries; the mod finds those subparts by Id at runtime and writes their
parent-relative transform every frame from drives it simulates itself.[^ksarmory-launcherpart]

The trick works because a KSA subpart is a full `Part` object with public, settable
`PositionParentAsmb`, `Asmb2ParentAsmb` and `Scale`, and the part renderer re-reads those on any
frame where one changed.[^ksa-part][^ksa-parttreerenderdata] The mod's notes give the reason for not
writing a real module: registering a new module type in the engine's update lists is not reachable
without patching.[^ksarmory-modding-notes]

## How it works

**1. Declare the moving pieces as sibling subparts.** In `KSArmoryAssets.xml` every moving piece is
a `<SubPart Id="..." InstanceOf="..._SubPart">` directly under the `<Part>`, with a `<Transform>`
putting the mesh back where it belongs. Each mesh is exported recentred on its own pivot, because
KSA rotates a subpart about its own origin.[^ksarmory-assets-xml]

**2. Find them by marker.** A profile names markers (`TurretMarker`, `PodsMarker`, `GunsMarker`,
`GunBarrelMarker`, `GunCylinderMarker`, `GunRodMarker`, `GunFeedMarker`, `GunRotorMarker`,
`RadarMarker`, `OpticBaseMarker`); `LauncherPart.FindSubPart` scans `launcher.SubParts` for the
first subpart whose `Id` contains the marker, case-insensitively.[^ksarmory-launcherprofile] A
runtime subpart's `Id` is the instance Id from the `<Part>`, falling back to the template Id
only when the instance has none (`Part.ResolveRuntimeId`, Part.cs L1484-L1488).[^ksa-part]

**3. Simulate the drives in `Sim/` (no KSA types, unit-tested).**

| Class | Drive model |
|---|---|
| `Turret` | Two named angles: bearing about part +X, elevation about +Z. `Track` converts a part-frame direction to a clamped command; `Update(dt, slew, elev)` steps toward it at a fixed rate[^ksarmory-turret] |
| `Turret` limits | Elevation band per bearing = profile travel, a forward depression floor (`DepressionFloorAt`: plateau then ease), and a `TravelMap` from the craft sweep if one exists |
| `Turret` traverse arc | With an arc (`TraverseLimitDeg` < 180, or a map), `StepWithinArc` moves in unwrapped coordinates, so it turns the long way round instead of through the gap behind it |
| `PointingDrive` | Two-axis head with no named axes: rotates its direction vector toward the command by at most `rate * dt` about the cross product (used by the optical heads, avoiding the look-up singularity)[^ksarmory-pointingdrive] |
| `GunRecoil.Offset` | Metres of barrel run-back from seconds since the last shot: eased fast recoil, smoothstep counter-recoil[^ksarmory-gunrecoil] |
| `GunRotor` | Barrel-cluster spin rate ramps to `rpm / barrels` turns while firing, runs down after; on simulated time, so pause freezes it[^ksarmory-gunrotor] |
| `ActuatorLinkage` | Cylinder pinned to the traverse, rod pinned to the gun: both turn with the line between the pins[^ksarmory-actuatorlinkage] |

**4. Compose poses in the launcher part's frame.** Subparts are placed against the part, so a
piece that rides the turret and elevates on an offset trunnion needs both its rotation and its
position recomputed whenever the turret turns. `TubeGeometry.ElevatingPose` returns
`(TurretPivot + traverse * pivotFromTurret, traverse * elevate)`; pods, guns, barrel (gun pose
pushed back along the bore), rotor, feed, radar and optic base all derive from it.[^ksarmory-tubegeometry]
Runtime elevation is applied as a rotation away from the elevation the mesh was modelled at, so
a pose the engine never accepts still looks right.

**5. Write the transform.** Every `TryApply*` in `LauncherPart` sets `Asmb2ParentAsmb`,
`PositionParentAsmb` and their `*Safe` twins, then `ResetCachedPosMatrixValues()`, inside a
`try`. A throw returns false.

**6. Latch failures per channel.** `DriveStatus` keeps a bit per `DriveChannel` (Turret, Pods,
Guns, Radar, Optic, Recoil, Linkage). The first refusal latches that channel off, logs once
("... rejected by the engine; that assembly is frozen"), and the drive keeps integrating so the
overlay still shows where the system thinks it points.[^ksarmory-drivestatus][^ksarmory-weaponsystem]

**What the engine does with the write.** In build 5554 the three transform setters each call
`ResetCachedPosMatrixValues()` themselves (Part.cs L756-L781, L819-L831). That method resets the
part's cached matrices and calls `Tree?.RenderData.InvalidateTransforms()` (L1241-L1249), which
sets `_transformsDirty` (PartTreeRenderData.cs L318-L321). On the next render-data build
`RewriteTransforms` repacks every batch slot from `Part.ComputeMatrixAsmb2VehicleAsmb()`, which
multiplies `Scale * rotation * translation` up the `PartParent` chain (Part.cs L1231-L1239,
PartTreeRenderData.cs L755-L764).[^ksa-part][^ksa-parttreerenderdata]

## Lifecycle and timing

| Step | Where | Program.cs (build 5554) |
|---|---|---|
| Drives integrate and transforms are written | `WeaponSystem.Update` -> `UpdateTurret(dt)`, called from `StepSimulation`'s fire loop, inside `StepOnce` | `[StarMapAfterGui]` postfix on `OnDrawUiViewports` (L2268), or the `OnFrameCelestials` prefix (L2320) when the HUD is hidden |
| Clock gate | The fire loop runs only when the engine reported a simulated step (`dtSim > 0`), so a paused game freezes the mounts in place | KSArmoryMod.cs L776[^ksarmory-mod] |
| Engine reads the transforms | `Render` (L2324) -> `UpdateRenderingResources` (L2468) -> `vehicle.UpdateRenderData` per visible viewport (L4355) -> `PartTree.UpdateRenderData` -> `RenderData.EnsureBuilt` | Same frame, before command recording (`RenderGame`, L2473)[^ksa-program] |

`Vehicle.UpdateRenderData` (Vehicle.cs L3713-L3719) only builds when the craft is large enough on
screen, and `EnsureBuilt` runs at most once per renderer frame (`_builtFrame` check,
PartTreeRenderData.cs L444-L450), so every viewport sees the transforms written earlier in the
frame.[^ksa-vehicle][^ksa-parttree] Writing from `[StarMapAfterOnFrame]` instead would land after
`Render` and show one frame late, which is why the mod steps in the GUI postfix or the
`OnFrameCelestials` prefix (see [the dual-hook step](./dual-hook-simulation-step.md)).

Thread safety: these hooks run after `ExecuteNextVehicleSolvers` dispatched the next physics step.
The subparts the mod moves carry only meshes: colliders and mass are declared on each
`PartGameData`, and the one `SubPartGameData` (the MIRV bus's RCS nozzles) belongs to a subpart
the mod never moves, so a moved turret does not move anything the physics worker integrates. The aim direction fed to `Turret.Track` is read off the craft's applied
attitude (`Vehicle.Asmb2Ego`), the same sample the rest of the frame uses.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/LauncherPart.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/LauncherPart.cs#L685-L704) | `FindSubPart` by marker; `TryApply*` writers (L735-L888) |
| [src/KSArmory/Sim/Turret.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Turret.cs) | Rate-limited traverse and elevation, interlocks, arc stepping |
| [src/KSArmory/Sim/TubeGeometry.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TubeGeometry.cs#L176-L258) | Pose composition for every riding assembly |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L1658-L1826) | `UpdateTurret`: command, integrate, write each channel |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L732-L742) | Subparts resolved each world sample |
| [src/KSArmory/Sim/DriveStatus.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/DriveStatus.cs) | Per-channel refusal latch |
| [src/KSArmory/Sim/PointingDrive.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/PointingDrive.cs) | Vector drive for gimballed heads |
| [src/KSArmory/Sim/GunRecoil.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/GunRecoil.cs), [src/KSArmory/Sim/GunRotor.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/GunRotor.cs), [src/KSArmory/Sim/ActuatorLinkage.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ActuatorLinkage.cs) | Cosmetic secondary motions |
| [src/KSArmory/Sim/LauncherProfile.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/LauncherProfile.cs#L73-L123) | Markers, pivots, rates and limits per mount |
| [src/KSArmory/KSArmory/Weapons.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmory/Weapons.xml#L92-L135) | The Pantsir's markers and pivots as data |
| [src/KSArmory/KSArmoryAssets.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryAssets.xml#L727-L782) | Sibling subparts with rest-pose transforms |
| [tests/KSArmory.Tests/TurretTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/TurretTests.cs) | Drive behaviour tested without the game |

## Snippets

```csharp
// src/KSArmory/Sim/TubeGeometry.cs L248-L257
    public static DrivePose ElevatingPose(LauncherProfile profile, double3 pivotFromTurret,
                                          double referenceElevationRad,
                                          double bearingRad, double elevationRad)
    {
        doubleQuat traverse = TurretRotation(bearingRad);
        doubleQuat elevate = doubleQuat.CreateFromAxisAngle(
            ElevationAxis, referenceElevationRad - elevationRad);

        return new DrivePose(profile.TurretPivot + traverse * pivotFromTurret, traverse * elevate);
    }
```

```csharp
// src/KSArmory/Ksa/LauncherPart.cs L801-L820
    public static bool TryApplyPodAim(Part pods, LauncherProfile profile, double bearingRad, double elevationRad)
    {
        try
        {
            DrivePose pose = TubeGeometry.PodPose(profile, bearingRad, elevationRad);
            (double3 position, doubleQuat rotation) = (pose.Position, pose.Rotation);

            pods.Asmb2ParentAsmb = rotation;
            pods.Asmb2ParentAsmbSafe = rotation;
            pods.PositionParentAsmb = position;
            pods.PositionParentAsmbSafe = position;
            pods.ResetCachedPosMatrixValues();
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"pods: could not write aim ({e.GetType().Name}: {e.Message})");
            return false;
        }
    }
```

```csharp
// src/KSArmory/Sim/Turret.cs L243-L274
    public void Update(double dt, double slewRateRadPerSec, double elevationRateRadPerSec)
    {
        if (!(dt > 0.0)) return;

        if (CommandRad is { } command && slewRateRadPerSec > 0.0)
        {
            // A limited traverse steps without wrapping, so it never takes the short way through
            // the arc behind it.
            double step = slewRateRadPerSec * dt;
            BearingRad = Arc is (double lo, double hi)
                ? StepWithinArc(BearingRad, ClampBearing(command), step, lo, hi)
                : StepToward(BearingRad, command, step);
        }

        if (CommandElevationRad is { } elevation && elevationRateRadPerSec > 0.0)
        {
            // ...
            double step = elevationRateRadPerSec * dt;
            double error = elevation - ElevationRad;
            ElevationRad += Math.Abs(error) <= step ? error : Math.Sign(error) * step;
        }

        // The interlock, enforced against where the turret *is* rather than where it was told
        // to go. Traversing into the forward arc with the pods low has to lift them out of the
        // bodywork on the way round, not once it arrives.
        (double floorNow, double ceilingNow) = ElevationBandAt(BearingRad);
        ElevationRad = Math.Clamp(ElevationRad, floorNow, ceilingNow);

        SecondsOnTarget = OnTarget ? SecondsOnTarget + dt : 0.0;
    }
```

```csharp
// current/decomp/KSA/Part.cs L756-L782
	public double3 PositionParentAsmb
	{
		[CompilerGenerated]
		get
		{
			return _003CPositionParentAsmb_003Ek__BackingField;
		}
		set
		{
			_003CPositionParentAsmb_003Ek__BackingField = value;
			ResetCachedPosMatrixValues();
		}
	}

	public doubleQuat Asmb2ParentAsmb
	{
		[CompilerGenerated]
		get
		{
			return _003CAsmb2ParentAsmb_003Ek__BackingField;
		}
		set
		{
			_003CAsmb2ParentAsmb_003Ek__BackingField = value;
			ResetCachedPosMatrixValues();
		}
	}
```

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `Part.SubParts`, `Part.Id`, `Part.IsSubPart`, `Part.PartParent` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1127-L1129) | Enumerate a launcher's pieces and match markers |
| `Part.PositionParentAsmb`, `Part.Asmb2ParentAsmb`, `Part.Scale` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L756-L831) | The pose written each frame |
| `Part.PositionParentAsmbSafe`, `Part.Asmb2ParentAsmbSafe` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L800-L802) | Written alongside; read only by the editor in 5554 |
| `Part.ResetCachedPosMatrixValues` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1241-L1249) | Invalidate caches and mark the tree's transforms dirty |
| `Part.Asmb2VehicleAsmb`, `Part.PositionVehicleAsmb`, `Part.PositionVehicleAsmbOffset` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L708-L738) | Read back muzzle and tube positions from the moved subparts |
| `Vehicle.Asmb2Ego`, `Vehicle.CenterOfMassAsmb` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L502-L565) | World direction to part frame for `Turret.Track` |
| `PartTreeRenderData.InvalidateTransforms`, `EnsureBuilt`, `RewriteTransforms` | [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L444-L507) | Engine side: where the written pose reaches the GPU batches |
| `PartTree.UpdateRenderData` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1158-L1162) | Engine side: calls `EnsureBuilt` per render frame |

## Pitfalls and version notes

- **Markers match the instance Id, not the template Id.** `KSArmory_Ciws_Turret` is found by
  `Ciws_Turret`; the template spelling `KSArmory_CiwsTurret_SubPart` finds nothing and the part
  just draws at its rest pose with no log line.[^ksarmory-modding-notes] Matching is a substring
  test, so markers must not be substrings of each other's Ids (the Pantsir's `Turret` does not
  occur in `Pods`, `Guns`, `Radar` or `Missile`).
- **Recentre meshes on their pivot.** A subpart turns about its own origin; the turret mesh is
  exported centred on its slew axis and put back with `<Position Y="-1.42">`. Keep the XML,
  `TurretPivot`, and `TURRET_PIVOT` in `pantsir.py` in step.[^ksarmory-assets-xml]
- **Riding pieces need their position rewritten too.** Every assembly is a sibling of the turret,
  so pods, guns and the radar turntable get both rotation and position each frame.
- **Mount orientation flips axes.** Directions are converted into the *launcher part's* frame
  (`TryDirectionToPartFrame` undoes `Asmb2VehicleAsmb`); stopping at the vehicle frame points a
  stack-mounted CIWS 180 degrees out.[^ksarmory-launcherpart] The M197 hangs from a nose, so its
  "elevation" is depression and its travel is -10 to +50 degrees with a 110-degree traverse limit.[^ksarmory-arsenal]
- **Doc vs build 5554, cache reset.** CLAUDE.md (L785-L786) says the new value is "stored and
  ignored" without `ResetCachedPosMatrixValues()`. In 5554 the setters already call it (Part.cs
  L765-L766, L779-L780, L828-L829), so the explicit call is redundant but harmless.[^ksarmory-claude-md][^ksa-part]
- **Doc vs build 5554, the `*Safe` twins.** `TryApplyTurretBearing`'s comment guesses `*Safe` is
  "a snapshot for another thread". In 5554 they are plain auto-properties read and written only by
  `VehicleEditor` grab and placement code (VehicleEditor.cs L2949-L2950, L3264-L3265).[^ksa-vehicleeditor]
- **Nesting (unverified).** CLAUDE.md says subparts "do not nest". The 5554 `Part` constructor does
  build a subpart's own `SubPartInstances` recursively (Part.cs L1517-L1524), and
  `ComputeMatrixAsmb2VehicleAsmb` walks `PartParent`, but `ResetCachedPosMatrixValues` does not reset
  children's cached `PositionVehicleAsmb`. Nobody has tested nested subparts in flight; the sibling
  layout avoids the question.
- **Pause freezes the drives.** Drives integrate only on simulated time; round bodies are synced
  every frame separately (see [round bodies](./rounds-as-hidden-launcher-subparts.md)).

## See also

- [Self-collision travel map](./self-collision-travel-map.md): the band and arc `Turret` clamps to
- [Rounds drawn as hidden launcher subparts](./rounds-as-hidden-launcher-subparts.md): the same write path for rounds
- [Weapon registry and profiles](./weapon-registry-and-profiles.md): where markers and pivots come from
- [Weapon part definitions in XML](./weapon-part-definitions-xml.md): the `<SubPart>` declarations
- [Blender model pipeline and checks](./blender-model-pipeline-and-checks.md): pivots exported from the model
- [One simulation step per frame](./dual-hook-simulation-step.md): why the writes land before the render
- [Optical director heads](./optical-director-heads.md): the `PointingDrive` consumer
- [Drive articulated turret mounts as sibling subparts with rate-limited drives](../../../howto/vehicles-and-parts/parts/define-and-animate-subparts/define-and-animate-subparts-2.md) - how-to article demonstrating this feature
- [Hide subparts by scale and fly them as projectiles of their launcher](../../../howto/vehicles-and-parts/parts/define-and-animate-subparts/define-and-animate-subparts-4.md) - how-to article demonstrating this feature

[^ksarmory-launcherpart]: src/KSArmory/Ksa/LauncherPart.cs
[^ksarmory-turret]: src/KSArmory/Sim/Turret.cs
[^ksarmory-tubegeometry]: src/KSArmory/Sim/TubeGeometry.cs
[^ksarmory-pointingdrive]: src/KSArmory/Sim/PointingDrive.cs
[^ksarmory-actuatorlinkage]: src/KSArmory/Sim/ActuatorLinkage.cs
[^ksarmory-gunrotor]: src/KSArmory/Sim/GunRotor.cs
[^ksarmory-gunrecoil]: src/KSArmory/Sim/GunRecoil.cs
[^ksarmory-drivestatus]: src/KSArmory/Sim/DriveStatus.cs
[^ksarmory-launcherprofile]: src/KSArmory/Sim/LauncherProfile.cs
[^ksarmory-weaponsystem]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-arsenal]: src/KSArmory/Sim/Arsenal.cs
[^ksarmory-assets-xml]: src/KSArmory/KSArmoryAssets.xml
[^ksarmory-claude-md]: CLAUDE.md (author notes)
[^ksarmory-modding-notes]: docs/KSA-MODDING-NOTES.md
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-parttreerenderdata]: KSA.PartTreeRenderData (decompiled, build 2026.10.10.5554)
[^ksa-parttree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditor]: KSA.VehicleEditor (decompiled, build 2026.10.10.5554)
