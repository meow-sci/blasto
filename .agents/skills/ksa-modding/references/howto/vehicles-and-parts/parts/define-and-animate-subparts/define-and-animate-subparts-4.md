---
type: How-To
title: Hide subparts by scale and fly them as projectiles of their launcher
description: Declare one hidden body per tube, scale it to a millimetre until its round is in the air, then write its position in the launcher part's frame from the simulated round
category: vehicles-and-parts
group: parts
topic: define-and-animate-subparts
approach: 4
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/ksarmory/features/rounds-as-hidden-launcher-subparts
  - mods/ksarmory/features/articulated-mount-subparts
techniques: [scale-to-hide, subpart-transform-write]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials]
lifecycle: [startup:asset-load, frame:draw-ui, frame:celestials, frame:render]
game_types: [KSA.Part, KSA.SubPartTemplate, KSA.PartTree, KSA.PartTreeRenderData, KSA.Vehicle]
tags: [subpart, hide, projectile, scale, launcher, frame-of-reference]
---
Outcome: real missile meshes leave tubes, nose over and fly kilometres to the target, without ever being KSA
vehicles. Each tube owns a round body, a `<SubPart>` of the launcher part declared once per tube. While loaded
the body is seated in the tube; once spent it is shrunk to a millimetre; while its round is in the air the mod
writes its parent-relative position, rotation and scale every rendered frame from the simulated round. Nothing is
created or destroyed at runtime, so a save never sees a round.

Prerequisites: StarMap and [article 2](define-and-animate-subparts-2.md). Demonstrated by
[ksarmory/rounds-as-hidden-launcher-subparts](../../../../mods/ksarmory/features/rounds-as-hidden-launcher-subparts.md) and [ksarmory/articulated-mount-subparts](../../../../mods/ksarmory/features/articulated-mount-subparts.md).

## Background: how the game does it

- Subparts under a `<Part>` become `PartTemplate.SubPartInstances` and one child `Part` each on construction
  [current/decomp/KSA/PartTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTemplate.cs#L19-L20)[^ksa-parttemplate] [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1517-L1525)[^ksa-part].
- There is no draw switch for a subpart; scaling it to a tiny size is the available hide. `Part.Scale` is a
  `double3` whose setter resets cached matrices [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L819-L830).
- `PositionParentAsmb` is measured in the **parent part's** frame, not the vehicle's, and a subpart is drawn
  against the vehicle's physics origin, so a position computed from the platform's analytic orbit position is
  off by metres on a landed craft (the mod's analysis).
- Engine consumes the transforms in `Render` -> `UpdateRenderingResources` -> `Vehicle.UpdateRenderData`
  [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3713-L3726)[^ksa-vehicle], the same frame the hook ran.

## Lifecycle and timing

| Point | Detail |
|---|---|
| asset load | templates and per-tube instances deserialised from the mod's Assets XML, so the bodies exist from the moment a craft is built (`startup:asset-load`) |
| per frame | the round-body sync runs for every system from the mod's step, **outside** the simulated-step gate: placement is drawing, and on a frame that advanced no simulated time the craft can still move under the camera |
| order | after the fire loop, so a round launched this frame is already in the round list |
| hooks | `StarMapAfterGui`, or the `OnFrameCelestials` prefix when the HUD is hidden |

The craft attitude and the round state are the ones applied this frame (`ApplyVehicleSolvers` in `PrepareFrame`),
so body and overlay in the same GUI hook share one epoch. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md).

## Steps

1. **Declare one body per tube** (and a fin-set instance per round) in the launcher `<Part>`, each parked out of
   the way at a tiny scale; templates are one `SubPart` with a `PartModel` and `MeshView` (see
   [Define a part with its own game data and GLB model](../define-a-part-with-game-data/overview.md) article 2).
2. **Find them in declaration order** by marker, so tube N always maps to the same body.
3. **Decide each body's state per frame.** `InFlight`: place at the round. `Loaded`: seat in the tube, scale 1.
   `Spent`: seat first, **then** hide, so its transform is never stale when it next fires:

```csharp
// src/KSArmory/Ksa/LauncherPart.cs L256-275
    public static bool TrySeatMissile(Part? pods, LauncherProfile profile, Part missile, Part? fins,
                                      int tubeIndex, MunitionProfile munition)
    {
        try
        {
            if (!TryGetSeatedPartFrame(pods, profile, tubeIndex, munition.BodyLength, out double3 seated)) return false;
            if (!TryGetTubeAxisPartFrame(pods, profile, tubeIndex, out double3 axis)) return false;

            doubleQuat rotation = FireGeometry.RotationFromNose(axis);

            missile.PositionParentAsmb = seated;
            missile.PositionParentAsmbSafe = seated;
            missile.Asmb2ParentAsmb = rotation;
            missile.Asmb2ParentAsmbSafe = rotation;
            missile.Scale = Shown;
            missile.ResetCachedPosMatrixValues();

            // Stowed: flat against the casing, so the round clears the bore. A hinged set has no
            // stowed state and four blades rather than one, so the caller places those itself —
            // scaling blade zero to nothing here would leave one missing on the rack.
```

```csharp
// src/KSArmory/Ksa/LauncherPart.cs L547-559
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

4. **Place a body at its round.** Position is the launch anchor (the tube mouth in the part frame at launch),
   carried through how far the craft has turned since launch, plus the round's travel since launch rotated from
   ecliptic into the part frame with the craft's *current* attitude. Travel is a difference of two
   platform-relative offsets, so the craft's orbital motion cancels:

```csharp
// src/KSArmory/Sim/TubeGeometry.cs L407-411
    public static double3 BodyPositionPartFrame(double3 anchorPartFrame, double3 travelEcl,
                                                doubleQuat ecl2Asmb, doubleQuat asmb2Part,
                                                doubleQuat sinceLaunchAsmb)
        => CarryAnchor(anchorPartFrame, sinceLaunchAsmb, asmb2Part)
           + asmb2Part * (ecl2Asmb * travelEcl);
```

5. **Write the transform in the parent frame** and show the body:

```csharp
// src/KSArmory/Ksa/LauncherPart.cs L581-621
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

            // asmb2Part is the launcher's own mounting -- nothing on a surface mount and a half turn
            // on a stack one -- and PositionParentAsmb is measured in the parent part's frame.
            //
            // How far the craft has turned since this round left. The anchor is a world point
            // written in the part's frame, so it has to be carried back through that.
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
        catch (Exception e)
        {
            Log.Warn($"round body: could not place ({e.GetType().Name}: {e.Message})");
            return false;
        }
    }
```

6. **Carry the attitude instead of deriving it.** Start at the release attitude and turn toward the local airflow
   by at most a rate times dynamic-pressure authority; in vacuum nothing turns, paused nothing turns, and the turn
   axis is square to the nose so no roll is added. Fins scale or hinge from stowed to deployed.
7. **Guard one-body-per-round.** The magazine refuses a tube whose previous round is still flying, because two
   rounds sharing a body would flip it between their positions.

## Complete example

- [src/KSArmory/Ksa/LauncherPart.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/LauncherPart.cs#L256-L288)[^ksarmory-launcherpart] - seating
- [src/KSArmory/Ksa/LauncherPart.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/LauncherPart.cs#L547-L621) - hiding and placing
- [src/KSArmory/Sim/TubeGeometry.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TubeGeometry.cs#L400-L440)[^ksarmory-tubegeometry] - launcher-relative placement maths

## Variations and gotchas

- **Fixed number of bodies per launcher.** The price of never creating anything at runtime; a gun's shells are
  not subparts at all but instances of a mesh template submitted to the draw (`DrawShellBodies`).
- **Anchor is a world point written down in the part frame.** Left in the part frame it rides the craft, and
  rolling the launcher would swing every round in flight about the craft's centre.
- **Hide cheaply.** Skip the cache reset when the body is already hidden (`if (missile.Scale.Equals(Hidden))
  return;`), since the writer runs every frame for every spent tube.
- **Fixed subpart list = save-safe.** The bodies are part of the template; see
  [Change a shipped part without breaking saves](../evolve-a-shipped-part-without-breaking-saves/overview.md).
- **Scaling is a cosmetic hide.** The shrunken subpart stays in the part tree; for removing a part or subpart
  from rendering properly see [hide-parts-or-subparts-from-rendering](../../../rendering/meshes/hide-parts-or-subparts-from-rendering/overview.md).

## See also

- [Overview](overview.md), [Article 2](define-and-animate-subparts-2.md), [Article 3](define-and-animate-subparts-3.md)
- [ksarmory/rounds-as-hidden-launcher-subparts](../../../../mods/ksarmory/features/rounds-as-hidden-launcher-subparts.md)
- [hide-parts-or-subparts-from-rendering](../../../rendering/meshes/hide-parts-or-subparts-from-rendering/overview.md), [choose-a-render-frame-and-anchor-effects](../../../rendering/overlays/choose-a-render-frame-and-anchor-effects/overview.md)

[^ksarmory-tubegeometry]: KSArmory: src/KSArmory/Sim/TubeGeometry.cs
[^ksa-parttemplate]: current/decomp/KSA/PartTemplate.cs (decompiled, build 2026.10.10.5554)
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksarmory-launcherpart]: KSArmory: src/KSArmory/Ksa/LauncherPart.cs
