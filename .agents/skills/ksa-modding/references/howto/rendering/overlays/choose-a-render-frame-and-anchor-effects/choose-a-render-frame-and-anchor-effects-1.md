---
type: How-To
title: Two-instant draw anchor against GetPositionEgo
description: Pair the craft's rendered Ego position sampled this frame with Ecl offsets measured against the same-instant platform sample so overlays do not slide hundreds of metres per frame
category: rendering
group: overlays
topic: choose-a-render-frame-and-anchor-effects
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/frame-epoch-draw-anchor]
techniques: [reference-frames]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:apply-solvers, frame:render]
game_types: [KSA.Camera, KSA.Vehicle, KSA.GizmosRenderer, KSA.Program]
tags: [ecl, ego, anchor, epoch, gizmos, reference-frames]
---

Outcome: geometry you measured relative to a craft (rings, lines, a landing marker) stays glued to the craft in the picture instead of sliding by the frame's ecliptic motion (about 500 m per frame near Earth). The recipe is a two-instant `DrawAnchor`:
the craft's `Ego` position as the renderer will draw it this frame, paired with the craft's `Ecl` position from the same instant the overlay geometry was measured against. Every overlay point is `anchor.Ego + (pointEcl - anchor.Ecl)`.
Demonstrated by KSAGolf ([Frame and epoch draw anchor](../../../../mods/ksagolf/features/frame-epoch-draw-anchor.md)). Prerequisites: StarMap.

## Background: how the game does it

Every KSA position near Earth carries about 29.8 km/s of ecliptic (`Ecl`) motion, roughly 500 m per frame at 60 fps, so 1 ms of epoch error is 30 m. `Ego` is camera-relative: `Camera.EclToEgo(posEcl)` subtracts `PositionEcl` and `EgoToEcl` adds it [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L223-L233).
`Camera.GetPositionEgo(IPosition)` [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L236-L250) is how KSA places vehicle meshes: `-PositionCce` for the followed object, the physics position (`KinematicStates.PositionPhys` through the bubble origin) for another vehicle in the same physics bubble, `GetPositionEcl() - PositionEcl` otherwise.
`Vehicle.GetMatrixAsmb2Ego(camera)` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1270) builds on the same call. `Camera.EclToEgo(vehicle.GetPositionEcl())` would use only the analytic orbit position and miss a landed craft by metres.

## Lifecycle and timing

| Moment | What happens |
|---|---|
| `frame:apply-solvers` (engine) | `Universe.ApplyVehicleSolvers` sets the last sim step and writes every celestial and vehicle position for the new epoch |
| `frame:draw-ui` (`[StarMapAfterGui]`) | The mod samples `PositionEcl`, calls `BeginDraw`, and submits gizmos |
| Window | `Program.OnFrame` calls `GizmosRenderer.ResetInstances()` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2260) before `PrepareImGui`; gizmos submitted after that and before `Render` are drawn this frame; anything submitted from `StarMapAfterOnFrame` is cleared before it is drawn |

With the HUD hidden these overlays are simply not drawn, which is acceptable for a development tool. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Invalidate the previous anchor once per frame at the top of your step, so a caller that forgets `BeginDraw` draws nothing visible rather than something in the wrong place.

2. In `BeginDraw(vehicle, vehicleEcl)` take the render camera (`Program.GetRenderCamera() ?? Program.GetMainCamera()`) and store the pair, using `GetPositionEgo` and the Ecl sample from the same step as your geometry:

   ```csharp
   // src/KSAGolf/Ksa/KsaWorld.cs L1110-L1143
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
           // That holds for a landed craft and fails once the camera follows something in
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

3. Convert every overlay point through the anchor:

   ```csharp
   // src/KSAGolf/Sim/DrawAnchor.cs L29-L40
   internal readonly struct DrawAnchor(double3 ego, double3 ecl)
   {
       /// <summary>The platform's position in the render frame, sampled this frame.</summary>
       public double3 Ego { get; } = ego;

       /// <summary>The platform's ecliptic position at the instant the geometry was measured.</summary>
       public double3 Ecl { get; } = ecl;

       public bool IsValid => Vec.IsFinite(Ego) && Vec.IsFinite(Ecl);

       /// <summary>Maps an ecliptic position from the geometry's epoch into the render frame.</summary>
       public double3 ToEgo(double3 posEcl) => Ego + (posEcl - Ecl);
   ```

4. Submit the converted Ego points to `Program.GizmosRenderer.DrawLine` / `DrawSphere` (see [Draw world-space lines and gizmos with the game's own renderers](../draw-world-space-lines-and-gizmos/overview.md)), from the GUI hook so they sit inside the draw window.

5. Measure errors as vectors and divide by 29.8 km/s: the quotient in milliseconds names the cause. A drawn offset is `PositionEcl - anchorEcl` measured after the step against the platform sample from the same frame; never re-project the platform by a `dt`.

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Sim/DrawAnchor.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/DrawAnchor.cs) | The game-free two-instant struct (unit-tested) |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1110-L1143) | `BeginDraw` and the gizmo draw helpers |

## Variations and gotchas

- **Collapsing the two instants** (deriving `Ego` from `Ecl`, or re-reading the platform at draw time) puts the whole overlay a frame of ecliptic motion beside the craft; the mod's tests assert the collapsed form measurably fails.
- **`Ego` belongs to one camera.** A position taken into Ego through one camera and back through another is out by the distance between them.
- **Gizmos from `StarMapAfterOnFrame` never appear;** only the GUI hooks (or a prefix in the window) sit inside the draw window.
- The ball avoids the problem by living in the planet's body-fixed frame (approach 5).

## See also

- [Overview](overview.md)
- [Approach 2: anchor to the engine's own render position](choose-a-render-frame-and-anchor-effects-2.md)
- [Approach 5: body-fixed mesh](choose-a-render-frame-and-anchor-effects-5.md)
- [Frame and epoch draw anchor](../../../../mods/ksagolf/features/frame-epoch-draw-anchor.md)
- [Draw world-space lines and gizmos with the game's own renderers](../draw-world-space-lines-and-gizmos/overview.md), [Project world and sky positions to screen pixels and point at off-screen targets](../project-world-positions-to-screen/overview.md)
