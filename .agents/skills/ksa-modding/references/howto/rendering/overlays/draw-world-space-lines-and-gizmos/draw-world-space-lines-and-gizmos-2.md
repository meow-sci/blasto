---
type: How-To
title: Gizmos next to a moving craft using a frame-epoch anchor
description: Submit gizmos between GizmosRenderer.ResetInstances and the render, anchored with the craft's rendered Ego position of this frame so overlays do not slide
category: rendering
group: overlays
topic: draw-world-space-lines-and-gizmos
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/frame-epoch-draw-anchor]
techniques: [gizmo-drawing, reference-frames]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, render:scene-opaque]
game_types: [KSA.GizmosRenderer, KSA.Program, KSA.Camera, KSA.Vehicle]
tags: [gizmos, anchor, epoch, ecl, ego, craft-mover]
---

Outcome: rings, lines and a landing marker for a craft being moved (KSAGolf's craft mover) that stay on the craft frame after frame. Gizmos are submitted from the GUI hook between `GizmosRenderer.ResetInstances` and the render, and anchored with the craft's rendered `Ego` position sampled this frame plus ecliptic offsets measured against the same-instant
platform sample. This article is the gizmo-side summary; the anchor theory is in [choose a render frame and anchor effects](../choose-a-render-frame-and-anchor-effects/overview.md) (approach 1). Demonstrated by KSAGolf ([Frame and epoch draw anchor](../../../../mods/ksagolf/features/frame-epoch-draw-anchor.md)). Prerequisites: StarMap.

## Background: how the game does it

The gizmo API and its place in the frame are described in approach 1: `GizmosRenderer.DrawSphere/DrawLine` take Ego, `ResetInstances` runs at L2260 and `Render` at L4842 inside the opaque offscreen scope [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4837-L4848). A torus is not a primitive: KSAGolf draws it as a ring of overlapping spheres, optionally draped
onto the ground. Every KSA position near Earth moves about 500 m per frame in the ecliptic, so the overlay must be built from samples of one instant. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Lifecycle and timing

| Moment | What happens |
|---|---|
| `frame:apply-solvers` | `Universe.ApplyVehicleSolvers` writes every celestial and vehicle position for the new epoch |
| `frame:draw-ui` (`StarMapAfterGui`) | `CraftMover.Draw` samples `PositionEcl`, calls `BeginDraw`, submits gizmos |
| `render:scene-opaque` | `GizmosRenderer.Render` draws them |

Gizmo overlays must be submitted from the GUI hooks; with the HUD hidden they are simply not drawn, which is acceptable for a development tool.

## Steps

1. Call `BeginFrame()` once per frame in the step to invalidate the previous anchor, so a caller that forgets `BeginDraw` draws nothing.

2. `BeginDraw(vehicle, vehicleEcl)` stores `new DrawAnchor(camera.GetPositionEgo(vehicle), vehicleEcl)` from `Program.GetRenderCamera() ?? Program.GetMainCamera()`:

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

3. `DrawLineEcl` and `DrawTorusEcl` convert every point with `anchor.Ego + (pointEcl - anchor.Ecl)` and submit to `Program.GizmosRenderer.DrawLine` / `DrawSphere`:

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

4. Never re-project the platform by a `dt`; measure a drawn offset as `PositionEcl - anchorEcl` after the step against the platform sample from the same frame.

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Sim/DrawAnchor.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/DrawAnchor.cs) | Two-instant anchor |
| [src/KSAGolf/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KsaWorld.cs#L1110-L1143) | `BeginDraw`, draw helpers |

## Variations and gotchas

- **Gizmos submitted from `StarMapAfterOnFrame` are cleared before they are drawn.**
- **`Vehicle.GetPositionEcl` is the analytic orbit position;** a landed craft's physics position differs by metres, so build anything drawn against a vehicle from `GetPositionEgo`.
- The mod's docs cite line numbers from an older build; behaviour was re-checked on this one, but numbers differ.

## See also

- [Overview](overview.md)
- [Approach 1: spheres and lines from the StarMap GUI postfix](draw-world-space-lines-and-gizmos-1.md)
- [Frame and epoch draw anchor](../../../../mods/ksagolf/features/frame-epoch-draw-anchor.md)
- [Choose a render frame and anchor effects to bodies, parts and terrain](../choose-a-render-frame-and-anchor-effects/overview.md)
