---
type: How-To
title: Per-frame ego-space sphere list and ray test per overlay point
description: Hide overlay points behind planets and vessels with a CPU ray-sphere test against a list of bodies and craft rebuilt each frame from the camera's view
category: rendering
group: overlays
topic: project-world-positions-to-screen
approach: 6
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ohmystars/features/overlay-occlusion-by-bodies]
techniques: [cpu-occlusion, imgui-drawlist]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Camera, KSA.IOrbiter, KSA.IParentBody, KSA.StellarBody, KSA.Vehicle]
tags: [projection, occlusion, ray-sphere, imgui-drawlist, sky]
---

Outcome: overlay geometry drawn with ImGui, which has no depth buffer, disappears behind planets, moons, the Sun and your own spacecraft instead of showing straight through them. The technique is a per-frame list of occluder spheres in ego space
and a ray-sphere test per overlay point. Demonstrated by Oh My Stars ([Occlusion by bodies](../../../../mods/ohmystars/features/overlay-occlusion-by-bodies.md)). Prerequisites: StarMap; a projection routine (approach 2).

## Background: how the game does it

The relevant game types are small. `IOrbiter.Parent` [current/decomp/KSA/IOrbiter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IOrbiter.cs#L18) climbs to the parent body; `IParentBody.Children` [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L27) lists child orbiters (bodies and vehicles); `IRadius.MeanRadius` [current/decomp/KSA/IRadius.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IRadius.cs#L5) is a body's mean radius, and `Vehicle.BoundingSphereRadiusBody`
[current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L527) a craft's bounding sphere; `StellarBody` implements `IParentBody` but not `IOrbiter`, so the climb ends at the star. `Camera.GetPositionEgo(IPosition)` [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L236-L250) returns the camera-relative position, composed precisely for craft in the same physics bubble.
`Camera.Following` [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L158) is the followed object, the natural root for the search.

## Lifecycle and timing

Runs once per frame before any layer draws, in `[StarMapAfterGui]` (`frame:draw-ui`). By then `PrepareFrame` has applied the previous physics step and dispatched the next, so body and vehicle positions are stable main-thread copies until the next frame's apply, and the camera position was updated in
`frame:viewports`. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Find the system root.** The followed object if it is an `IParentBody`, or its `Parent` if it is an `IOrbiter`; climb `Parent` until a body that is not an orbiter (the star). If there is no root, clear the list so nothing occludes.

2. **Collect spheres.** Add the body at `MeanRadius`, recurse into child bodies and add child vehicles at their bounding sphere. Use ego centres and skip a sphere the camera is inside, so a camera inside a vehicle's bounding sphere or below a body's mean radius does not black out the sky.

   ```csharp
   // src/StellariumRenderer.cs L318-L344
   private static void CollectCelestialOccluders(Camera camera, IParentBody parentBody) {
       celestialOccluders.Clear();
       CollectCelestialOccludersRecursive(camera, GetRootBody(parentBody));
   }

   private static void CollectCelestialOccludersRecursive(Camera camera, IParentBody body) {
       AddOccluder(camera, body, body.MeanRadius);

       foreach(IOrbiter child in body.Children) {
           if(child is IParentBody childBody) {
               CollectCelestialOccludersRecursive(camera, childBody);
           }
           else if(child is Vehicle vehicle) {
               AddOccluder(camera, vehicle, vehicle.BoundingSphereRadiusBody);
           }
       }
   }

   private static void AddOccluder(Camera camera, IPosition objectPosition, double radius) {
       double3 center = camera.GetPositionEgo(objectPosition);
       double centerDistance = VectorMath.Length(center);

       // A camera inside its current body or vehicle must not treat that enclosing sphere as an occluder.
       if(radius > 0d && centerDistance > radius) {
           celestialOccluders.Add(new CelestialOccluder(center, radius));
       }
   }
   ```

3. **Test a direction, not a point.** Normalise the overlay point to a direction `d`; for each sphere with ego centre `c` and radius `r`, compute `t = d . c`; spheres with `t <= 0` are behind; otherwise the point is hidden when `|c|^2 - t^2 < r^2`. The overlay sphere has radius 19.5 while real bodies are kilometres away, so the code treats the sky point as infinitely far.

   ```csharp
   // src/StellariumRenderer.cs L288-L316
   public static bool IsVisibleFromCamera(double3 position) {
       double distance = VectorMath.Length(position);
       if(distance <= 0d)
           return false;

       double3 direction = position / distance;
       foreach(CelestialOccluder occluder in celestialOccluders) {
           double projection =
               direction.X * occluder.Center.X +
               direction.Y * occluder.Center.Y +
               direction.Z * occluder.Center.Z;

           // The rendered star sphere is camera-relative, but it represents an infinitely distant
           // background. Any occluder in front of the camera along this direction hides it.
           if(projection <= 0d)
               continue;

           double centerDistanceSquared =
               occluder.Center.X * occluder.Center.X +
               occluder.Center.Y * occluder.Center.Y +
               occluder.Center.Z * occluder.Center.Z;
           double closestDistanceSquared = centerDistanceSquared - projection * projection;

           if(closestDistanceSquared < occluder.Radius * occluder.Radius)
               return false;
       }

       return true;
   }
   ```

4. **Use per layer.** Lines are drawn only if start, midpoint and end are all visible; labels test their single point. A line that crosses a planet's limb disappears whole rather than being clipped.

## Complete example

| File | Role |
|---|---|
| [src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L262-L344) | Root search, sphere collection, visibility test |

## Variations and gotchas

- **Mean-radius sphere, not terrain.** Mountains do not hide lines near the horizon; a camera below the mean radius drops the planet as an occluder.
- **No atmosphere or daylight logic.** Lines stay fully visible against a bright daytime sky.
- **Whole-segment rejection.** Testing three points can make a long segment vanish while most of it is in open sky; subdividing would fix this at the cost of more tests.
- **Cost.** Each drawn segment runs up to three tests against every sphere; with a 1 degree grid that is tens of thousands of segments per frame.
- **Only `IParentBody.Children` vehicles** are occluders.
- For world points (not sky directions) use the actual distance in the test, and consider only spheres closer than the point; see the pointer in approach 4, which deliberately ignores occlusion.

## See also

- [Overview](overview.md)
- [Approach 2: sky directions on the star sphere](project-world-positions-to-screen-2.md)
- [Approach 4: clip-space edge clamp](project-world-positions-to-screen-4.md)
- [Occlusion by bodies](../../../../mods/ohmystars/features/overlay-occlusion-by-bodies.md)
- [Draw lines and labels on the sky](../draw-lines-on-the-sky/overview.md)
