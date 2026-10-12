---
type: How-To
title: Scale a pre-declared subpart to nothing until it is needed
description: Declare spare subparts in the part XML, park them at 0.001 scale, and rewrite their parent-relative transform from mod code each frame to hide, seat or fly them
category: rendering
group: meshes
topic: hide-parts-or-subparts-from-rendering
approach: 2
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksarmory/features/rounds-as-hidden-launcher-subparts]
techniques: [scale-to-hide, xml-asset, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials]
lifecycle: [startup:asset-load, frame:draw-ui, frame:celestials]
game_types: [KSA.Part, KSA.Vehicle, KSA.PartTemplate, KSA.PartInstance, KSA.PartModel, KSA.PartTreeRenderData]
tags: [subpart, scale, hide, transform, missile, xml]
---
Outcome: a mesh that is part of a vessel (so it shares the part shader, lighting and shadows, and ships in the part XML) but is invisible until your code wants it. KSArmory declares one missile body per launcher tube, parks each at one thousandth scale, and while a round flies it writes that body's position, rotation and scale every frame from the mod's own simulation.

Demonstrated by KSArmory ([Rounds drawn as hidden launcher subparts flown by transform writes](../../../../mods/ksarmory/features/rounds-as-hidden-launcher-subparts.md)). Prerequisites: StarMap; Harmony only for the optional hidden-UI hook. Nothing is created or destroyed at runtime, so saves never see a round; the price is a fixed number of bodies per part.

## Background: how the game does it

`<SubPart>` elements under a `<Part>` deserialize into `PartTemplate.SubPartInstances` ([current/decomp/KSA/PartTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTemplate.cs#L20-L21)), each an `InstanceOf` a shared subpart template with a `<Transform>`. The `Part` constructor builds one child `Part` per instance and applies its transform ([current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1490-L1524)). Every subpart is a `Part` whose parent-relative `PositionParentAsmb`, `Asmb2ParentAsmb` and `Scale` are public and settable; each setter calls `Part.ResetCachedPosMatrixValues`, which invalidates the tree's render transforms:

```csharp
// current/decomp/KSA/Part.cs L1241-L1249
public void ResetCachedPosMatrixValues()
{
	Tree?.RenderData.InvalidateTransforms();
	_matrixAsmb = UncachedMatrix;
	_positionVehicleAsmb = UncachedPosition;
	_matrixAsmb2Parent = UncachedMatrix;
	_asmb2VehicleAsmb = UncachedQuat;
	_matrixAsmb2VehicleAsmb = UncachedMatrix;
}
```

There is no "is drawn" switch on a subpart, so scale is the switch. A scale of exactly zero gives a singular matrix; one thousandth is invisible at any sensible range.

## Lifecycle and timing

| Point | Detail |
|---|---|
| Asset load | Templates and per-tube instances come from `KSArmoryAssets.xml` (listed in `mod.toml` `assets`), so the bodies exist on the part from the moment a craft is built (`startup:asset-load`) |
| Per frame | `SyncRoundBodies` for every system, called from `StepOnce`: the `[StarMapAfterGui]` postfix on `OnDrawUiViewports` (`frame:draw-ui`) or, with the HUD hidden, a prefix on `Program.OnFrameCelestials` (`frame:celestials`) |
| Engine consumes | `Render` then `UpdateRenderingResources` then `vehicle.UpdateRenderData` then `PartTree.UpdateRenderData` then `PartTreeRenderData.EnsureBuilt`, which repacks transforms flagged dirty by the writes |

Why placement is written outside the simulated-step gate: placement is drawing, and on a frame that advanced no simulated time the craft can still move under the camera, so a body not rewritten would lag it. `Program.OnFrame` only calls `OnDrawUiFrame` and `OnDrawUiViewports` when `DrawUI` is true, so a mod that must pose every frame needs a second hook; KSArmory's `PreRenderHook` is a prefix on `OnFrameCelestials`, the last phase before `OnPreRender` that is not behind that guard ([src/KSArmory/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/PreRenderHook.cs#L1-L40)). It is a prefix on purpose: that method resolves the camera-nearby body and updates planet shader data, so a step taken after it prepares the planet against a camera the mod is about to move. See rows 12, 13 and 20 of [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence).

## Steps

1. **Declare a template and one parked instance per body** in your asset XML. The template needs a `PartModel` and a mesh; each instance is parked below the vehicle at 0.001 scale:

   ```xml
<!-- src/KSArmory/KSArmoryAssets.xml L259-L267 -->
<SubPart Id="KSArmory_Missile_SubPart">
  <PartModel Id="KSArmory_Subpart_Missile_Model">
    <Mesh Id="KSArmory_Subpart_Missile" />
    <Material Id="KSArmory_Material" />
  </PartModel>
  <MeshView>
    <Mesh Id="KSArmory_Subpart_Missile_VM" />
  </MeshView>
</SubPart>
```

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

   A subpart `InstanceOf` template must exist even if nothing else instances it; otherwise the game throws `PartTemplate is null for '...'` at load.

2. **Find the bodies once, by instance Id.** The Id the `<Part>` gives each instance, not the template Id, is what `Part.SubParts[i].Id` holds. KSArmory collects every subpart whose Id contains a marker, in declaration order, so tube N always maps to the same body:

   ```csharp
// src/KSArmory/Ksa/LauncherPart.cs L158-L178
public static void FindMissiles(Part launcher, MunitionProfile munition, List<Part> into)
{
    into.Clear();
    try
    {
        ReadOnlySpan<Part> subParts = launcher.SubParts;
        for (int i = 0; i < subParts.Length; i++)
        {
            if (subParts[i] is { } sub && sub.Id is { } id
                && id.Contains(munition.BodyMarker ?? MissileMarkerFallback,
                               StringComparison.OrdinalIgnoreCase))
            {
                into.Add(sub);
            }
        }
    }
    catch
    {
        into.Clear();
    }
}
```

3. **Hide by scale.** Skip the write when already hidden to avoid a needless transform rebuild:

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

   The constants are `Hidden = (1e-3, 1e-3, 1e-3)` and `Shown = (1, 1, 1)`. Seat a body (place it in its tube) before hiding it, so its transform is never stale when it next fires.

4. **Place a body at its round.** `PositionParentAsmb` is measured in the parent part's frame, not the vehicle's. Two rotations take ecliptic quantities into it: the vehicle's attitude (`platform.Asmb2Ego`), then the launcher part's own mounting (`launcher.Asmb2VehicleAsmb`):

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
```

   The position uses the round's travel since launch (a difference of two platform-relative offsets, so the craft's orbital velocity cancels) carried through how far the craft has turned since:

   ```csharp
// src/KSArmory/Sim/TubeGeometry.cs L407-L411
public static double3 BodyPositionPartFrame(double3 anchorPartFrame, double3 travelEcl,
                                            doubleQuat ecl2Asmb, doubleQuat asmb2Part,
                                            doubleQuat sinceLaunchAsmb)
    => CarryAnchor(anchorPartFrame, sinceLaunchAsmb, asmb2Part)
       + asmb2Part * (ecl2Asmb * travelEcl);
```

5. **Call it every rendered frame**, from the GUI hook and the hidden-UI fallback with a once-per-frame latch, as in [approach 3](hide-parts-or-subparts-from-rendering-3.md), because a transform written after the render is drawn a frame late.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/KSArmoryAssets.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryAssets.xml#L254-L287) | Round and fin templates |
| [src/KSArmory/KSArmoryAssets.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryAssets.xml#L789-L938) | Per-tube parked instances |
| [src/KSArmory/Ksa/LauncherPart.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/LauncherPart.cs#L158-L178) | Find bodies by marker |
| [src/KSArmory/Ksa/LauncherPart.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/LauncherPart.cs#L547-L621) | Hide and place |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L1914-L2129) | `SyncRoundBodies`: per-round placement, fins, seat or hide pass |
| [src/KSArmory/Ksa/PreRenderHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/PreRenderHook.cs) | Fallback prefix for frames with no UI |

## Variations and gotchas

- **Never zero scale.** Hidden is `1e-3`: a zero-scaled matrix is singular.
- **Bodies are culled with the launcher.** `Vehicle.UpdateRenderData` skips every part of a craft that projects under a pixel, so a shell kilometres downrange vanishes when the launcher shrinks; see [round body draw hooks in approach 3 of draw-your-own-meshes](../draw-mod-meshes-with-the-engine-renderers/draw-mod-meshes-with-the-engine-renderers-3.md) and [keep vessels rendered at any distance](../keep-vessels-rendered-at-any-distance/overview.md).
- **Fixed capacity.** Twelve bodies means at most twelve rounds in the air from that launcher; belt-fed guns avoid the limit by drawing shells as part-model instances instead.
- **Body count versus tube count** come from two files (the art declares bodies, the profile declares tubes). Loop over the minimum, and warn once, because an out-of-range index thrown from a frame hook repeatedly trips fault limits.
- **A refused write** should fall back to something visible (KSArmory falls back to tracers) rather than leave the round invisible.
- **Saves.** Subparts still exist, are still in the save and still cost a draw while hidden. A shipped part's subpart list is append-only: saves pair subparts positionally, so removing one breaks existing saves.
- **`ResetCachedPosMatrixValues` is already in the setters** in this build (Part.cs L766, L780, L829); calling it explicitly after a write is redundant but harmless.

## See also

- [Overview](overview.md)
- [Approach 1](hide-parts-or-subparts-from-rendering-1.md), [approach 3](hide-parts-or-subparts-from-rendering-3.md)
- [Rounds drawn as hidden launcher subparts flown by transform writes](../../../../mods/ksarmory/features/rounds-as-hidden-launcher-subparts.md)
- [Defining articulated subparts and animating or hiding them from code](../../../vehicles-and-parts/parts/define-and-animate-subparts/overview.md)
- [Draw your own meshes through the engine's mesh renderers](../draw-mod-meshes-with-the-engine-renderers/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
