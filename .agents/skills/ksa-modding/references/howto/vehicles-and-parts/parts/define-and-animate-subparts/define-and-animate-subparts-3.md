---
type: How-To
title: Aim optical director heads on roll-nod and mast gimbals
description: Drive a head with a rate-limited shortest-rotation pointing drive, clamp its travel to a legal path, and write a roll collar and head from one aim so both stay flush
category: vehicles-and-parts
group: parts
topic: define-and-animate-subparts
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/optical-director-heads]
techniques: [gimbal-geometry, subpart-transform-write]
hooks: [starmap StarMapAfterGui]
lifecycle: [startup:asset-load, frame:draw-ui, frame:celestials, frame:viewports, frame:render]
game_types: [KSA.Part, KSA.SubPartTemplate, KSA.Vehicle]
tags: [subpart, gimbal, roll-nod, keyhole, camera, pointing]
---
Outcome: a sensor head slews onto targets as a real gimbal would. A mast head has an elevation band over its
mounting face; a roll-nod pod has an outer roll collar and an inner nod, with a keyhole on its centreline.
KSArmory's EO Director, the Pantsir's 1TPP1 director and a LITENING pod are all inert parts whose moving bodies
are subparts, written each simulated step from a drive the mod integrates itself.

Prerequisites: StarMap, and the posing basics from [article 1](define-and-animate-subparts-1.md). Demonstrated by
[ksarmory/optical-director-heads](../../../../mods/ksarmory/features/optical-director-heads.md).

## Background: how the game does it

Nothing here is a KSA feature beyond the subpart transform: a head is a `Part` whose `Asmb2ParentAsmb` and
`PositionParentAsmb` the mod writes [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L756-L781)[^ksa-part]. The engine's contribution to the problem is a
frame-of-reference one:

- Rotation about a subpart's own origin means a head needs its mesh exported recentred on the pivot, and the
  asset puts the offset back in the `Transform`; the pod's roll collar and window share one pivot because they
  turn on one bearing.
- The renderer reads transforms in the same frame's `Render` after the GUI pass
  [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L464-L480)[^ksa-parttreerenderdata].
- The view the head provides (camera windows, sight) is re-solved in the main view's controller pass
  (`frame:viewports`) so the line of sight is computed in the pass that draws it.

## Lifecycle and timing

| Value | Where | What runs |
|---|---|---|
| `frame:draw-ui` | `StarMapAfterGui` postfix | roster, sampling, scan, drive, transform writes |
| `frame:celestials` | Harmony prefix on `Program.OnFrameCelestials` (HUD hidden) | the same step before the render |
| `frame:viewports` (next frame) | the main view's controller | line of sight re-solved |
| `frame:render` | subpart transforms written in the hook | drawn this frame |

Heads update on the unclamped simulated step, since a clamped step would make a rate-limited drive under-advance
on alternate frames and shake the picture. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md).

## Steps

1. **Declare** a base, an optional roll collar and a head as subparts with pivot-recentred meshes, plus a
   profile (part Id, gimbal kind, marker names, head pivot, travel, slew rate).
2. **Find bodies by marker** against `Part.SubParts` (instance Id substring), as in
   [article 2](define-and-animate-subparts-2.md).
3. **Point with a drive that has no singularity looking straight up.** Turn by the shortest rotation onto the
   command, at most `rate * dt`; antiparallel has no axis so take any perpendicular (a half turn, not a NaN):

```csharp
// src/KSArmory/Sim/PointingDrive.cs L52-80
    /// <summary>Turns toward <paramref name="commandPartFrame"/>, by at most the rate allows.</summary>
    public void Update(double dt, double3 commandPartFrame, double rateRadPerSec)
    {
        double3 command = Vec.Unit(commandPartFrame);
        if (!Vec.IsFinite(command) || command.Equals(Vec.Zero)) return;
        if (!(dt > 0.0) || !double.IsFinite(dt)) return;

        double3 current = Vec.Unit(Direction);
        if (!Vec.IsFinite(current) || current.Equals(Vec.Zero)) current = OpticGeometry.RestDirection;

        double dot = Math.Clamp(Vec.Dot(current, command), -1.0, 1.0);
        ErrorRad = Math.Acos(dot);

        double step = Math.Max(0.0, rateRadPerSec) * dt;
        if (ErrorRad <= step || step <= 0.0)
        {
            Direction = ErrorRad <= step ? command : current;
            if (ErrorRad <= step) ErrorRad = 0.0;
            return;
        }

        // Turn about the axis joining the two directions. Antiparallel has no such axis and any
        // perpendicular is equally right, which is a half turn rather than a NaN.
        double3 axis = Vec.Cross(current, command);
        axis = Vec.Len2(axis) < 1e-18 ? Vec.AnyPerpendicular(current) : Vec.Unit(axis);

        Direction = Vec.Unit(doubleQuat.CreateFromAxisAngle(axis, step) * current);
        ErrorRad -= step;
    }
```

4. **Clamp the path as well as the command.** Between two legal directions the shortest arc can pass through
   the mast, so re-clamp what the drive actually reached each step and put the head there. Mast heads clamp
   elevation and keep the bearing; roll-nod heads clamp the nod and keep the roll plane:

```csharp
// src/KSArmory/Sim/OpticGeometry.cs L315-340
    public static double3 ClampToTravel(OpticProfile profile, MountFrame mount, double3 aimPartFrame)
    {
        if (profile.Gimbal == GimbalKind.RollNod)
        {
            return ClampOffBoresight(profile, mount, aimPartFrame);
        }

        double3 aim = Vec.Unit(aimPartFrame);
        if (Vec.Len2(aim) < 0.5) return aimPartFrame;

        double3 normal = mount.Normal;

        double elevation = ElevationRad(mount, aim);
        double min = float.DegreesToRadians(profile.MinElevationDeg);
        double max = float.DegreesToRadians(profile.MaxElevationDeg);

        double wanted = Math.Clamp(elevation, Math.Min(min, max), Math.Max(min, max));
        if (Math.Abs(wanted - elevation) < 1e-9) return aim;

        // The bearing, as a unit vector in the mounting plane. Zero length means the command was
        // along the normal itself, which has no bearing to keep.
        double3 across = Vec.RejectFrom(aim, normal);
        if (Vec.Len2(across) < 1e-12) return aim;

        return Vec.Unit(Vec.Unit(across) * Math.Cos(wanted) + normal * Math.Sin(wanted));
    }
```

5. **Write both bodies from one aim.** The head's pose is `mount.ToPart(HeadPivot)` with a rotation carrying the
   rest direction onto the aim; the roll collar is a rotation about the mount's centreline by the roll the aim
   implies. Deriving them from different instants would show the window standing off its aperture:

```csharp
// src/KSArmory/Sim/OpticGeometry.cs L67-68, L81-84
    public static DrivePose Pose(OpticProfile profile, MountFrame mount, double3 aimPartFrame)
        => new(mount.ToPart(profile.HeadPivot), Rotation(profile, mount, aimPartFrame));
// ...
    public static DrivePose RollPose(OpticProfile profile, MountFrame mount, double3 aimPartFrame)
        => new(mount.ToPart(profile.HeadPivot),
               doubleQuat.CreateFromAxisAngle(mount.Forward, RollAngleRad(mount, aimPartFrame))
               * mount.Rotation);
```

6. **Apply with a refusal latch** per body: a refused head freezes and must stop claiming to be on target;
   a refused collar is only cosmetic:

```csharp
// src/KSArmory/Ksa/OpticParts.cs L123-125, L134-136, L138-155
    public static bool TryApplyAim(Part head, OpticProfile profile, MountFrame mount,
                                   double3 aimPartFrame)
        => TryApplyPose(head, OpticGeometry.Pose(profile, mount, aimPartFrame), "head");
// ...
    public static bool TryApplyRoll(Part roll, OpticProfile profile, MountFrame mount,
                                    double3 aimPartFrame)
        => TryApplyPose(roll, OpticGeometry.RollPose(profile, mount, aimPartFrame), "roll gimbal");
// ...
    private static bool TryApplyPose(Part body, DrivePose pose, string what)
    {
        try
        {
            body.Asmb2ParentAsmb = pose.Rotation;
            body.PositionParentAsmb = pose.Position;

            // Part caches its matrices; without this the new value is stored and ignored.
            body.ResetCachedPosMatrixValues();

            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"optic {what} transform: {e.Message}");
            return false;
        }
    }
```

7. **Choose the roll reference by gimbal kind.** For a mast it is the mount normal; for roll-nod it is minus the
   mount's forward, so the nose is a pure roll followed by a pure nod and the picture is derotated:

```csharp
// src/KSArmory/Sim/OpticGeometry.cs L120-121
    public static double3 RollReference(OpticProfile profile, MountFrame mount)
        => profile.Gimbal == GimbalKind.RollNod ? -mount.Forward : mount.Normal;
```

## Complete example

- [src/KSArmory/Sim/OpticGeometry.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/OpticGeometry.cs)[^ksarmory-opticgeometry] and [src/KSArmory/Sim/PointingDrive.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/PointingDrive.cs)[^ksarmory-pointingdrive]
- [src/KSArmory/Ksa/OpticParts.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/OpticParts.cs)[^ksarmory-opticparts] - finders, writers, view solve

## Variations and gotchas

- **Keyhole.** On a roll-nod pod's centreline the roll angle is undefined; keep commanded aims out of the
  keyhole cone and make the roll function not produce a NaN when something else puts it there.
- **Keep a head per (craft, ordinal),** not per `Part` reference, because staging and docking rebuild part trees.
- **Precedence.** Mouse aim, hand sliders, designation, tracking, rest, in fixed order, so the UI can say why the
  head is not on the contact.
- **Updates.** The only game dependency is the subpart transform; the geometry is pure C# with unit tests.

## See also

- [Overview](overview.md), [Article 2](define-and-animate-subparts-2.md), [Article 4](define-and-animate-subparts-4.md)
- [ksarmory/optical-director-heads](../../../../mods/ksarmory/features/optical-director-heads.md)
- [ray-cast-against-vessel-meshes-and-terrain](../../../physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md), [choose-a-render-frame-and-anchor-effects](../../../rendering/overlays/choose-a-render-frame-and-anchor-effects/overview.md)

[^ksarmory-opticgeometry]: KSArmory: src/KSArmory/Sim/OpticGeometry.cs
[^ksa-parttreerenderdata]: current/decomp/KSA/PartTreeRenderData.cs (decompiled, build 2026.10.10.5554)
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-opticparts]: KSArmory: src/KSArmory/Ksa/OpticParts.cs
[^ksarmory-pointingdrive]: KSArmory: src/KSArmory/Sim/PointingDrive.cs
