---
type: How-To
title: Anchor to the engine's own render position and add travel since launch
description: Place gizmos and projectile subparts relative to where the engine draws the launcher, using Ego as a pure translation of Ecl and travel measured as a difference of platform-relative offsets
category: rendering
group: overlays
topic: choose-a-render-frame-and-anchor-effects
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/ksarmory/features/gizmo-world-overlays
  - mods/ksarmory/features/rounds-as-hidden-launcher-subparts
techniques: [ego-anchor, reference-frames]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnFrameCelestials]
lifecycle: [frame:draw-ui, frame:celestials, frame:render]
game_types: [KSA.Camera, KSA.Vehicle, KSA.GizmosRenderer, KSA.PartModel, KSA.Part]
tags: [ego, anchor, gizmos, subparts, launcher, reference-frames]
---

Outcome: things that move relative to a craft (radar cones, trails, a missile that leaves a tube) are drawn exactly where the engine draws the craft this frame. Gizmos use an Ego anchor reported by `Camera.GetPositionEgo` plus Ecl offsets captured at the
same instant as the geometry; a projectile body rendered as a subpart of the launcher is placed in the launcher's part frame from the platform-relative travel since launch, so the craft's 29.8 km/s of ecliptic motion cancels. Demonstrated by KSArmory
([World-anchored overlays through KSA's gizmo renderer](../../../../mods/ksarmory/features/gizmo-world-overlays.md), [Rounds drawn as hidden launcher subparts flown by transform writes](../../../../mods/ksarmory/features/rounds-as-hidden-launcher-subparts.md)). Prerequisites: StarMap. Approach 1 explains the anchor pair in isolation.

## Background: how the game does it

`GizmosRenderer.DrawSphere(double3 positionEgo, float radiusMeters, float4 color)` [current/decomp/KSA/GizmosRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GizmosRenderer.cs#L181) and `DrawLine(double3 startEgo, double3 endEgo, float4 color)` [current/decomp/KSA/GizmosRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GizmosRenderer.cs#L217) take camera-relative Ego positions;
`ResetInstances()` [current/decomp/KSA/GizmosRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GizmosRenderer.cs#L471) zeroes the counts once per frame. The renderer draws them inside the opaque scene scope right after `SuperMeshRenderSystem.RenderMainPass` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4837-L4848), so they are depth-tested against terrain and craft.
`Camera.GetPositionEgo` is where the engine draws the craft [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L236-L250). For subparts, `Part` stores `PositionParentAsmb` / `Asmb2ParentAsmb` and a cached matrix; writing them and calling `ResetCachedPosMatrixValues()` makes the engine repack the transform in `PartTreeRenderData`.

## Lifecycle and timing

| Point | Detail |
|---|---|
| Overlay submission | `[StarMapAfterGui]` (`frame:draw-ui`) between `ResetInstances` (L2260) and `Render`; the step is advanced first in the same hook so anchor, offsets and drawn craft share one epoch |
| Hidden HUD | the step also runs from a prefix on `OnFrameCelestials` (`frame:celestials`), but KSArmory draws its overlays only from the GUI hook, so they vanish with F2 and in screenshots |
| Body placement | written every rendered frame, outside the simulated-step gate, because placement is drawing: on a frame that advanced no simulated time the craft can still move under the camera |
| Engine consumes | `Render` -> `UpdateRenderingResources` -> `vehicle.UpdateRenderData` -> `PartTree.UpdateRenderData` repacks transforms flagged dirty by the writes (`frame:render`) |

See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Build the anchor** from the render camera and the platform: `DrawAnchor(camera.GetPositionEgo(anchor), anchorEcl)`; refuse (draw nothing) when the craft is dead or the anchor is not finite.

   ```csharp
   // src/KSArmory/Ksa/KsaWorld.cs L3438-L3470
   public static bool BeginDraw(Vehicle anchor, double3 anchorEcl)
   {
       _anchored = false;
       if (!IsAlive(anchor)) return false;

       try
       {
           // The camera the frame will actually be rendered with, not the main viewport's.
           Camera camera = Program.GetRenderCamera() ?? Program.GetMainCamera();
           if (camera is null) return false;

           // See DrawAnchor for why these are sampled at different instants, and why
           // collapsing them into one puts the whole overlay beside the craft.
           //
           // GetPositionEgo, not EclToEgo. Its branching on what the camera follows is the
           // engine answering correctly per case — exact for the followed craft, physics-based
           // for others in its bubble — and it is the same call KSA renders vehicles with.
           // EclToEgo instead measures against camera.PositionEcl, which only agrees with the
           // rendered scene when the followed craft's analytic and physics positions coincide.
           // That holds for a landed launcher and fails once the camera follows something in
           // flight, which is when anything drawn to a vehicle stops lining up.
           _anchor = new DrawAnchor(camera.GetPositionEgo(anchor), anchorEcl);

           if (!_anchor.IsValid) return false;

           _anchored = true;
           return true;
       }
       catch
       {
           return false;
       }
   }
   ```

2. **Draw through the anchor.** `DrawLineEcl` / `DrawSphereEcl` convert through the anchor and call `Program.GizmosRenderer`; `DrawLineEgo` passes an Ego position straight through (rounds are stored as offsets from their platform, so they draw off `AnchorEgo + round.OffsetFromPlatform` with no absolute arithmetic).

   ```csharp
   // src/KSArmory/Ksa/KsaWorld.cs L3605-L3620
   public static void DrawSphereEgo(double3 positionEgo, float radiusMetres, float4 colour)
   {
       Program.GizmosRenderer?.DrawSphere(positionEgo, radiusMetres, colour);
   }

   public static void DrawLineEgo(double3 startEgo, double3 endEgo, float4 colour)
   {
       Program.GizmosRenderer?.DrawLine(startEgo, endEgo, colour);
   }

   public static void DrawSphereEcl(double3 positionEcl, float radiusMetres, float4 colour)
   {
       if (Program.GizmosRenderer is null) return;
       if (!TryEclToEgo(positionEcl, out double3 ego)) return;
       Program.GizmosRenderer.DrawSphere(ego, radiusMetres, colour);
   }
   ```

3. **Choose Ecl or the rendered sample deliberately.** Use Ecl at `Universe.GetElapsedTime()` for simulation; use the rendered Ego sample for anything the player sees next to the craft.

4. **Place a projectile body on its launcher subpart.** Declare one `<SubPart>` per tube in the launcher's `<Part>` XML (parked at scale 0.001), find the bodies by marker, then each frame write the parent-relative position and rotation from the simulated round. The position is the launch anchor carried through how far the craft has turned since launch, plus the round's travel since launch rotated from ecliptic into the part frame with the craft's current attitude:

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

5. Seat a loaded body half a body length behind the tube mouth, and hide a spent body at scale `1e-3` (never zero: a zero-scaled matrix is singular) only after seating it.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L3438-L3470) | `BeginDraw` and the anchored draw helpers |
| [src/KSArmory/Ksa/Visuals.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Visuals.cs#L104-L134) | Per-system overlay drawing |
| [src/KSArmory/Ksa/LauncherPart.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/LauncherPart.cs) | Hidden launcher subparts, placement |
| [src/KSArmory/KSArmoryAssets.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryAssets.xml#L789-L801) | One body subpart per tube in XML |

## Variations and gotchas

- **Travel is a difference of two platform-relative offsets** (`OffsetFromPlatform - LaunchOffset`), so the orbital motion cancels; the anchor is relative to the physics origin the subpart is drawn against, not the analytic orbit position.
- **Ego positions are narrowed to `float`** in the gizmo renderer; draw short shapes near the camera.
- **Gizmo capacity is silent:** extras beyond the per-frame arrays are dropped without a log.
- **Main view only.** Secondary camera windows run `GizmoPass`, not `GizmosRenderer.Render`.
- **Bodies are culled with the launcher:** a round body is a subpart, so a shell kilometres downrange vanishes when the launcher shrinks below a pixel.
- **Fixed capacity:** twelve bodies means at most twelve rounds of that launcher in the air.

## See also

- [Overview](overview.md)
- [Approach 1: two-instant draw anchor](choose-a-render-frame-and-anchor-effects-1.md)
- [Approach 3: explosion presets with a body-frame origin](choose-a-render-frame-and-anchor-effects-3.md)
- [World-anchored overlays through KSA's gizmo renderer](../../../../mods/ksarmory/features/gizmo-world-overlays.md), [Rounds drawn as hidden launcher subparts flown by transform writes](../../../../mods/ksarmory/features/rounds-as-hidden-launcher-subparts.md)
- [Draw world-space lines and gizmos with the game's own renderers](../draw-world-space-lines-and-gizmos/overview.md), [Draw your own meshes through the engine's mesh renderers](../../meshes/draw-mod-meshes-with-the-engine-renderers/overview.md)
