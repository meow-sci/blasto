---
type: How-To
title: Same technique for a catalog star on the sky sphere
description: Point from a mod window to a catalog star by using its direction times 19.5 as the target, with the camera-behind w less than 0 handling
category: rendering
group: overlays
topic: draw-markers-and-annotations-on-world-objects
approach: 5
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ohmystars/features/star-pointer]
techniques: [imgui-drawlist, sky-projection]
hooks: [starmap StarMapAfterGui, starmap StarMapBeforeGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Camera, KSA.IViewport, KSA.ImGuiHelper, KSA.Vehicle, KSA.Program]
tags: [pointer, overlay, star, sky, edge-clamp, imgui-drawlist]
---

Outcome: a line from your window's nearest corner (or two corners) to a selected star on the sky; when the star is off screen, or even behind the camera, the line ends at the screen edge in the star's direction, so the player can follow it. This is approach 4 with a sky target.
Demonstrated by Oh My Stars ([Star pointer](../../../../mods/ohmystars/features/star-pointer.md)). Prerequisites: StarMap; see [project world positions to screen](../project-world-positions-to-screen/overview.md) (approaches 2 and 4).

## Background: how the game does it

A catalog star sits where KSA's own star sprites sit: `direction * 19.5` in the camera's ego frame [current/decomp/KSA/InstancedStarTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedStarTechnique.cs#L68-L83). The central star is a real body, so its target is `camera.GetPositionEgo(centralStarBody)`. `Camera.EgoToClip` [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L283) supplies the clip position; behind the camera `w` is negative and the perspective divide mirrors the direction.

## Lifecycle and timing

Drawn from `[StarMapAfterGui]` (`frame:draw-ui`). The window geometry it anchors to was recorded by the window code in `[StarMapBeforeGui]` (prefix on `Program.OnDrawUiFrame`) in the same frame, so the line follows a dragged window without lag. The pointer vanishes with the HUD when `Program.DrawUI` is false. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Inputs:** the selected star, the window position and size captured while the window was drawn earlier in the frame, and whether the pointer is on and the window shown.

2. **Target in ego space:** a catalog star is `ApplyAlignment(direction) * 19.5`; the central star (Sol) is `camera.GetPositionEgo(centralStarBody)`.

3. **Resolve on-screen or edge point.** If `IsPointWithinFov`, the target is the projected point. Otherwise divide the clip position by `W`, flip the sign when `W < 0`, treat "exactly behind" (`x = y = 0`) as straight up, and scale so the larger component is 1, then map the NDC square to the viewport rectangle:

   ```csharp
   // src/StellariumRenderer.cs L150-L185
   private static bool TryGetStarPointerTarget(Camera camera, double3 position, out float2 target) {
       if(camera.IsPointWithinFov(position)) {
           target = EgoToOverlayScreen(camera, position);
           return true;
       }

       float4 clip = camera.EgoToClip(position);
       if(System.MathF.Abs(clip.W) < float.Epsilon) {
           target = default;
           return false;
       }

       float x = clip.X / clip.W;
       float y = clip.Y / clip.W;
       if(clip.W < 0f) {
           x = -x;
           y = -y;
       }

       if(System.MathF.Abs(x) < float.Epsilon && System.MathF.Abs(y) < float.Epsilon) {
           y = -1f;
       }

       float maximum = System.MathF.Max(System.MathF.Abs(x), System.MathF.Abs(y));
       ImGuiViewport* viewport = ImGui.GetMainViewport();
       if(viewport == null || maximum < float.Epsilon) {
           target = default;
           return false;
       }

       float2 center = viewport->Pos + viewport->Size * 0.5f;
       target = center + new float2(
           x / maximum * viewport->Size.X * 0.5f,
           y / maximum * viewport->Size.Y * 0.5f);
       return true;
   }
   ```

4. **Anchor and draw:** choose the nearest window corner (or the two nearest) by squared distance and draw into the same overlay draw list as the constellations with a 1.5 px line; optional rainbow hue is `frac(ImGui.GetTime() / 4)` through a six-sector ramp.

   ```csharp
   // src/StellariumRenderer.cs L187-L234
   private static float2 GetNearestWindowCorner(float2 windowPosition, float2 windowSize, float2 target) {
       float2 topLeft = windowPosition;
       float2 topRight = new float2(windowPosition.X + windowSize.X, windowPosition.Y);
       float2 bottomLeft = new float2(windowPosition.X, windowPosition.Y + windowSize.Y);
       float2 bottomRight = windowPosition + windowSize;

       float2 nearest = topLeft;
       float nearestDistanceSquared = DistanceSquared(topLeft, target);
       foreach(float2 corner in new[] { topRight, bottomLeft, bottomRight }) {
           float distanceSquared = DistanceSquared(corner, target);
           if(distanceSquared < nearestDistanceSquared) {
               nearest = corner;
               nearestDistanceSquared = distanceSquared;
           }
       }

       return nearest;
   }

   // The two window corners closest to the pointer target, for the two-lines pointer mode.
   private static void GetTwoNearestWindowCorners(float2 windowPosition, float2 windowSize, float2 target, out float2 nearest, out float2 secondNearest) {
       float2 topLeft = windowPosition;
       float2 topRight = new float2(windowPosition.X + windowSize.X, windowPosition.Y);
       float2 bottomLeft = new float2(windowPosition.X, windowPosition.Y + windowSize.Y);
       float2 bottomRight = windowPosition + windowSize;

       nearest = topLeft;
       secondNearest = topRight;
       float nearestDistanceSquared = DistanceSquared(topLeft, target);
       float secondDistanceSquared = DistanceSquared(topRight, target);
       if(secondDistanceSquared < nearestDistanceSquared) {
           (nearest, secondNearest) = (secondNearest, nearest);
           (nearestDistanceSquared, secondDistanceSquared) = (secondDistanceSquared, nearestDistanceSquared);
       }

       foreach(float2 corner in new[] { bottomLeft, bottomRight }) {
           float distanceSquared = DistanceSquared(corner, target);
           if(distanceSquared < nearestDistanceSquared) {
               secondNearest = nearest;
               secondDistanceSquared = nearestDistanceSquared;
               nearest = corner;
               nearestDistanceSquared = distanceSquared;
           } else if(distanceSquared < secondDistanceSquared) {
               secondNearest = corner;
               secondDistanceSquared = distanceSquared;
           }
       }
   }
   ```

## Complete example

| File | Role |
|---|---|
| [src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L116-L254) | Target, clamp, corners, rainbow |

## Variations and gotchas

- **Edge point is exact in front, approximate behind:** for a target in front of the camera the mapping is affine about the screen centre; behind the camera it is a "turn this way" hint.
- **Occlusion is ignored** on purpose: a pointer should show through a planet.
- **No window, no pointer:** the target getter returns false when the window is hidden.

## See also

- [Overview](overview.md)
- [Approach 4: body target](draw-markers-and-annotations-on-world-objects-4.md)
- [Star pointer](../../../../mods/ohmystars/features/star-pointer.md)
- [Project world and sky positions to screen pixels and point at off-screen targets](../project-world-positions-to-screen/overview.md)
