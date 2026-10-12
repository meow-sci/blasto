---
type: How-To
title: Edge cue from the camera basis (bearing), not the projection
description: Compute the direction to an off-screen target from the camera's right and up vectors and clamp it to an inset rectangle, which stays correct when the projection is degenerate
category: rendering
group: overlays
topic: project-world-positions-to-screen
approach: 5
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/world-anchored-screen-markers, mods/ksarmory/features/gunner-sight-overlay]
techniques: [imgui-drawlist]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Camera, KSA.IViewport, KSA.Program]
tags: [projection, edge-cue, bearing, camera-basis, imgui-drawlist]
---

Outcome: an arrow or chevron on the screen edge that always points the right way, even when the target is directly behind the camera or near the camera plane, where a projection-based clamp (approach 4) becomes unstable. Demonstrated by KSArmory's world-anchored
markers ([World-anchored screen markers on full-screen ImGui overlay windows](../../../../mods/ksarmory/features/world-anchored-screen-markers.md)) and gunner sight ([Gunner's sight painted over a borrowed view or a camera window](../../../../mods/ksarmory/features/gunner-sight-overlay.md)). Prerequisites: StarMap; base projection in approach 1.

## Background: how the game does it

A projection matrix maps a point behind the camera to the opposite side of the screen, so an arrow built from it points the wrong way. The camera itself exposes its basis in the ecliptic frame: `GetForwardEcl()`, `GetRightEcl()` and `GetUpEcl()`
[current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L176-L190), and `EgoToEcl(Vec.Zero)` is the eye position [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L223-L227). Projecting uses `EclToScreen(posEcl, ignoreBehind: true)` [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L339-L345).

## Lifecycle and timing

`[StarMapAfterGui]` (`frame:draw-ui`) after `OnFrameViewports` built the camera for this frame. KSArmory draws all four overlays from its `OnAfterGui`, after its simulation step and after its panel, so the overlays sit above the scene and below the panel. When `Program.DrawUI` is false none of this is drawn.
See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Compute the vector from the eye to the target in ecliptic, and whether the target is ahead (`dot(toTarget, forward) > 0`).

2. If ahead, project with `EclToScreen(..., ignoreBehind: true)`; if the result is finite and inside `0..width, 0..height`, it is in view; add `viewport.Position` and draw the normal marker. Otherwise take the bearing from the basis:

   ```csharp
   // src/KSArmory/Ksa/KsaWorld.cs L4559-L4595
   public static bool TryProjectOrClamp(double3 pointEcl, out float2 screen, out bool inView)
   {
       screen = default;
       inView = false;
       try
       {
           if (Program.MainViewport is not { } viewport) return false;
           if (viewport.GetCamera() is not { } camera) return false;

           int w = viewport.Width, h = viewport.Height;
           if (w <= 0 || h <= 0) return false;

           double3 toTarget = pointEcl - camera.EgoToEcl(Vec.Zero);
           bool ahead = Vec.Dot(toTarget, camera.GetForwardEcl()) > 0.0;

           if (ahead)
           {
               float2 local = camera.EclToScreen(pointEcl, ignoreBehind: true);
               if (float.IsFinite(local.X) && float.IsFinite(local.Y)
                   && local.X >= 0f && local.Y >= 0f && local.X <= w && local.Y <= h)
               {
                   inView = true;
                   screen = new float2(viewport.Position.X + local.X, viewport.Position.Y + local.Y);
                   return true;
               }
           }

           // Off-screen or behind: bearing from the camera basis, then out to the edge.
           double right = Vec.Dot(toTarget, camera.GetRightEcl());
           double up = Vec.Dot(toTarget, camera.GetUpEcl());
           if (!EdgeCue.TryOffset(right, up, w, h, EdgeMargin, out double dx, out double dy)) return false;

           screen = new float2((float)(viewport.Position.X + w * 0.5 + dx),
                               (float)(viewport.Position.Y + h * 0.5 + dy));
           return true;
       }
       catch
   ```

3. Turn the bearing into an edge offset: the components of the target direction along the camera's right and up are normalised and scaled to the inset screen rectangle (`EdgeCue.TryOffset`). The up component is negated because screen y grows downward.

   ```csharp
   // src/KSArmory/Sim/EdgeCue.cs L18-L36
   public static bool TryOffset(double right, double up, double width, double height, double margin,
                                out double dx, out double dy)
   {
       dx = 0.0;
       dy = 0.0;
       if (!double.IsFinite(right) || !double.IsFinite(up)) return false;
       if (Math.Abs(right) < 1e-9 && Math.Abs(up) < 1e-9) return false;

       double len = Math.Sqrt((right * right) + (up * up));
       double ux = right / len, uy = -up / len;

       double halfW = (width * 0.5) - margin, halfH = (height * 0.5) - margin;
       double scale = Math.Min(Math.Abs(ux) > 1e-9 ? halfW / Math.Abs(ux) : double.MaxValue,
                               Math.Abs(uy) > 1e-9 ? halfH / Math.Abs(uy) : double.MaxValue);

       dx = ux * scale;
       dy = uy * scale;
       return true;
   }
   ```

4. Draw the chevron or arrow at `viewport centre + (dx, dy)` rotated along the bearing; the gunner sight also prints the range in kilometres. A zero-length bearing returns false (nothing to point at).

5. For markers on a craft, prefer a crossed or dimmed mark when a body is between camera and craft (see approach 6), and no bracket at all when the craft subtends more than 0.03 rad.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Sim/EdgeCue.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/EdgeCue.cs) | Game-free bearing-to-edge math (unit-testable) |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4559-L4595) | `TryProjectOrClamp` |
| [src/KSArmory/Ksa/Markers.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Markers.cs) | Overlay window and marker drawing |

## Variations and gotchas

- **Robust near the camera plane.** The bearing is a function of two dot products, so it stays finite where `clip.w` approaches zero.
- **Re-base for other viewports.** For a window other than the main view, `TryProjectEgoOrClamp` re-bases the ego position through ecliptic before projecting and takes the edge bearing from that camera's basis.
- **Fixed-mode viewports** use `BaseCamera`.
- **Hover** is computed from `ImGui.GetMousePos()` against the projected point (18 px radius), since the overlay window ignores input.

## See also

- [Overview](overview.md)
- [Approach 4: clip-space edge clamp](project-world-positions-to-screen-4.md)
- [Approach 1: real points through the viewport camera](project-world-positions-to-screen-1.md)
- [World-anchored screen markers on full-screen ImGui overlay windows](../../../../mods/ksarmory/features/world-anchored-screen-markers.md), [Gunner's sight painted over a borrowed view or a camera window](../../../../mods/ksarmory/features/gunner-sight-overlay.md)
- [Draw markers, brackets and measurement annotations on world objects](../draw-markers-and-annotations-on-world-objects/overview.md)
