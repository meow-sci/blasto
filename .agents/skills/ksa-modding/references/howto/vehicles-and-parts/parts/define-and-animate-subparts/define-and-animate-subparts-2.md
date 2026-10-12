---
type: How-To
title: Drive articulated turret mounts as sibling subparts with rate-limited drives
description: Declare each moving assembly as a sibling subpart, simulate rate-limited drives in plain C#, compose chained poses against the launcher part's frame and latch a channel off when the engine refuses a write
category: vehicles-and-parts
group: parts
topic: define-and-animate-subparts
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/articulated-mount-subparts]
techniques: [subpart-transform-write, xml-asset]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials]
lifecycle: [startup:asset-load, frame:draw-ui, frame:celestials, frame:render]
game_types: [KSA.Part, KSA.SubPartTemplate, KSA.PartTree, KSA.Vehicle]
tags: [subpart, turret, traverse, elevation, drive, latch, composition]
---
Outcome: a truck's turret traverses, its missile pods and cannon elevate, a barrel recoils, a radar spins. KSArmory
does all of it with inert subparts and per-frame transform writes; none of it is a KSA part module.

Prerequisites: StarMap; the subpart posing basics from [article 1](define-and-animate-subparts-1.md).
Demonstrated by [ksarmory/articulated-mount-subparts](../../../../mods/ksarmory/features/articulated-mount-subparts.md).

## Background: how the game does it

- A subpart is a `Part` with public `PositionParentAsmb`, `Asmb2ParentAsmb` and `Scale`; the setters invalidate
  its cached matrices and the tree's render transforms [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L756-L781)[^ksa-part] [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1241-L1249). Each
  `RewriteTransforms` repacks the batch from `ComputeMatrixAsmb2VehicleAsmb`, which multiplies up the
  `PartParent` chain [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1231-L1239) [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L755)[^ksa-parttreerenderdata].
- `Part.SubParts` lists children; `Part.Id` is the instance Id from the `<Part>` [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1127)
  [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1485-L1488).
- KSArmory declares the moving pieces as **siblings** directly under the `<Part>`, not nested, and composes the
  chain itself in the launcher part's frame (a piece that rides the turret and elevates on an offset trunnion
  needs both its rotation and its position recomputed whenever the turret turns).
- Mass and colliders are declared on the whole part, not the subparts, so moving a subpart does not move
  anything the physics worker integrates.

## Lifecycle and timing

| Step | Where | Frame |
|---|---|---|
| declare subparts | `KSArmoryAssets.xml` | `startup:asset-load` |
| drives integrate, transforms written | `WeaponSystem.Update` -> `UpdateTurret(dt)` inside the mod's simulation step | `StarMapAfterGui` (`frame:draw-ui`), or the `OnFrameCelestials` prefix (`frame:celestials`) when the HUD is hidden |
| clock gate | the fire loop runs only when the engine reported a simulated step, so a paused game freezes the mounts in place | |
| engine reads transforms | `Render` -> `UpdateRenderingResources` -> `vehicle.UpdateRenderData` -> `PartTree.UpdateRenderData` | `frame:render`, same frame |

Writing from `StarMapAfterOnFrame` would land after `Render` and show a frame late, which is why the mod steps in
the GUI postfix or the celestials prefix. Thread safety: those hooks run after the next vehicle step has been
dispatched, but the moved subparts carry only meshes. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md).

## Steps

1. **Declare each moving piece as a sibling `SubPart`** with a `Transform` that puts the pivot-recentred mesh back
   where it belongs (see [Define a part with its own game data and GLB model](../define-a-part-with-game-data/overview.md) article 2 for the XML).
2. **Find them by marker.** A profile names markers; the first subpart whose `Id` contains the marker
   (case-insensitive) is the body. Guard against a subpart list that is mid-rebuild during staging or docking:

```csharp
// src/KSArmory/Ksa/LauncherPart.cs L685-706
    private static Part? FindSubPart(Part launcher, string? marker)
    {
        if (string.IsNullOrEmpty(marker)) return null;

        try
        {
            ReadOnlySpan<Part> subParts = launcher.SubParts;
            for (int i = 0; i < subParts.Length; i++)
            {
                if (subParts[i] is { } sub && sub.Id is { } id
                    && id.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return sub;
                }
            }
        }
        catch
        {
            // Subpart list can be mid-rebuild during staging or docking.
        }
        return null;
    }
```

3. **Simulate the drive in plain C#** (no KSA types, unit-testable): step toward a command at a fixed rate, the
   short way round, or within an arc without crossing the gap behind it:

```csharp
// src/KSArmory/Sim/Turret.cs L243-274
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
            // No wrapping here: elevation is a limited arc, not a circle, so stepping is a
            // plain clamped move. Wrapping it would let the pods take the "short way" through
            // the deck to reach a high angle.
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

4. **Compose poses in the part's frame.** A piece that elevates about an offset trunnion and rides the turret:

```csharp
// src/KSArmory/Sim/TubeGeometry.cs L248-257
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

   Runtime elevation is applied as a rotation away from the elevation the mesh was modelled at, so any pose the
   engine never accepts still looks right.
5. **Write the pose** (position, rotation, and the `*Safe` twins), then reset caches, inside a `try`; a throw
   returns false:

```csharp
// src/KSArmory/Ksa/LauncherPart.cs L845-861
    public static bool TryApplyPose(Part part, DrivePose pose, string what)
    {
        try
        {
            part.Asmb2ParentAsmb = pose.Rotation;
            part.Asmb2ParentAsmbSafe = pose.Rotation;
            part.PositionParentAsmb = pose.Position;
            part.PositionParentAsmbSafe = pose.Position;
            part.ResetCachedPosMatrixValues();
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"{what}: could not write pose ({e.GetType().Name}: {e.Message})");
            return false;
        }
    }
```

6. **Latch failures per channel.** One bit per `DriveChannel` (Turret, Pods, Guns, Radar, Optic, Recoil, Linkage):
   the first refusal latches that channel off and logs once, and the drive keeps integrating so the overlay still
   shows where the system thinks it points:

```csharp
// src/KSArmory/Sim/DriveStatus.cs L23-38
public struct DriveStatus
{
    private int _refused;

    /// <summary>True while the engine is still accepting writes for this channel.</summary>
    public readonly bool Works(DriveChannel channel) => (_refused & Bit(channel)) == 0;

    /// <summary>Latches a channel off. True if this was its first refusal, so callers log once.</summary>
    public bool Refuse(DriveChannel channel)
    {
        if (!Works(channel)) return false;
        _refused |= Bit(channel);
        return true;
    }

    /// <summary>True once any channel has been refused.</summary>
```

## Complete example

- [src/KSArmory/Ksa/LauncherPart.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/LauncherPart.cs)[^ksarmory-launcherpart] - finders and `TryApply*` writers
- [src/KSArmory/Sim/Turret.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Turret.cs)[^ksarmory-turret], [src/KSArmory/Sim/TubeGeometry.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TubeGeometry.cs)[^ksarmory-tubegeometry] - the pure drives and poses

## Variations and gotchas

- **Siblings, not nesting.** Composing in code means each write is relative to the launcher part and a missing or
  refused piece cannot drag its children with it.
- **Descendants.** If you do nest subparts, reset the cached matrices of each descendant after writing the parent;
  the parent's setter does not.
- **Frame choice.** The aim direction should come from the same sample the rest of the frame uses (the craft's
  applied attitude, `Vehicle.Asmb2Ego`).
- **The `*Safe` twins.** `PositionParentAsmbSafe`/`Asmb2ParentAsmbSafe` appear to be snapshots; KSArmory writes
  both plain and Safe properties because it is cheaper than guessing wrong (its own comment).
- **Save compatibility.** Never remove one of these sibling subparts in a later release
  ([Change a shipped part without breaking saves](../evolve-a-shipped-part-without-breaking-saves/overview.md)).

## See also

- [Overview](overview.md), [Article 1](define-and-animate-subparts-1.md), [Article 3](define-and-animate-subparts-3.md)
- [ksarmory/articulated-mount-subparts](../../../../mods/ksarmory/features/articulated-mount-subparts.md)
- [ray-cast-against-vessel-meshes-and-terrain](../../../physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md), [choose-a-render-frame-and-anchor-effects](../../../rendering/overlays/choose-a-render-frame-and-anchor-effects/overview.md)

[^ksarmory-turret]: KSArmory: src/KSArmory/Sim/Turret.cs
[^ksarmory-tubegeometry]: KSArmory: src/KSArmory/Sim/TubeGeometry.cs
[^ksa-parttreerenderdata]: current/decomp/KSA/PartTreeRenderData.cs (decompiled, build 2026.10.10.5554)
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-launcherpart]: KSArmory: src/KSArmory/Ksa/LauncherPart.cs
