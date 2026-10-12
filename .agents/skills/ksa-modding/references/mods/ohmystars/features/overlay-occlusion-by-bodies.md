---
type: Mod Feature
title: Occlusion by bodies
description: Hide sky-overlay points behind planets, moons, the star and vehicles with a per-frame CPU ray-sphere test against every body and body-orbiting vehicle of the camera's current system
mod: ohmystars
category: rendering
techniques: [imgui-drawlist, cpu-occlusion]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Camera, KSA.IParentBody, KSA.IOrbiter, KSA.IRadius, KSA.Vehicle, KSA.StellarBody]
tags: [sky, occlusion, ray-sphere, camera, celestials, imgui]
---

ImGui geometry has no depth buffer, so a constellation line drawn after the 3D scene would show
straight through a planet, a moon, the Sun or the player's own spacecraft. Oh My Stars hides
overlay points that are behind any celestial body or vehicle in the current system with a cheap
CPU ray-sphere test, rebuilt every frame from the camera's view of the system.[^oms-stellariumrenderer-cs]

## How it works

1. **Find the system root.** `GetOccluderRoot(camera.Following)` returns the followed object if it
   is an `IParentBody`, or its `Parent` if it is an `IOrbiter`, else `null` (the list is then
   cleared and nothing occludes). `GetRootBody` climbs `IOrbiter.Parent` until it reaches a body
   that is not an orbiter, which is the system's star: `StellarBody` implements `IParentBody` but
   not `IOrbiter` ([src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L262-L286),
   [current/decomp/KSA/StellarBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StellarBody.cs#L11)).[^ksa-stellarbody-cs]
2. **Collect spheres.** `CollectCelestialOccludersRecursive` adds the body with
   `IRadius.MeanRadius`, then walks `IParentBody.Children`: child bodies recurse, and child
   `Vehicle`s are added with `Vehicle.BoundingSphereRadiusBody`
   ([src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L318-L344),
   [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L27), [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L527-L535)).[^ksa-iparentbody-cs] [^ksa-vehicle-cs]
3. **Ego centres.** Each centre is `camera.GetPositionEgo(object)`. That call returns
   `-PositionCce` for the followed object and, for a vehicle in the same physics bubble as a
   followed vehicle, composes the two bubble-relative positions, so nearby craft are placed
   precisely rather than through large ecliptic coordinates
   ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L235-L250)).[^ksa-camera-cs]
4. **Skip enclosing spheres.** `AddOccluder` keeps a sphere only if the camera is outside it
   (`centerDistance > radius`), so a camera inside a vehicle's bounding sphere, or below a body's
   mean radius, does not black out the whole sky.
5. **Test a direction, not a point.** `IsVisibleFromCamera(position)` normalises the overlay point
   to a direction `d` and, for each sphere with ego centre `c` and radius `r`, computes
   `t = d . c`; spheres with `t <= 0` are behind the camera, otherwise the point is hidden when
   `|c|^2 - t^2 < r^2` ([src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L288-L316)). The overlay sphere has
   radius 19.5 while real bodies are kilometres away, so the code deliberately treats the sky
   point as infinitely far: any sphere crossing the ray hides it.
6. **Per-layer use.** Lines are drawn only if the start, midpoint and end are all visible; labels
   test their single point. A line that crosses a planet's limb disappears whole rather than
   being clipped at the edge. Grid segments are 1 deg long, so the grid hugs limbs more closely
   than long asterism segments do.

## Lifecycle and timing

- Runs inside `StellariumRenderer.Draw` from `[StarMapAfterGui]` (`frame:draw-ui`), once per frame,
  before any layer draws ([src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L90-L95)).
- Positions come from main-thread state. By `frame:draw-ui`, `Program.PrepareFrame` has already run
  `Universe.ApplyVehicleSolvers` for this frame and dispatched the next step to the workers, so
  `Vehicle` and body positions are stable until next frame's apply
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2220)).[^ksa-program-cs] The camera's position was
  updated in `frame:viewports` earlier in the same frame.

## Key code

| Path | Role |
|---|---|
| [src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L262-L286) | Root body from the camera's followed object |
| [src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L318-L344) | Recursive sphere collection, enclosing-sphere rejection |
| [src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L288-L316) | Ray-sphere visibility test |

## Snippets

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

```csharp
// src/StellariumRenderer.cs L323-L344
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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Camera.Following` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L158) | Which object the camera orbits |
| `Camera.GetPositionEgo(IPosition)` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L235-L250) | Camera-relative centre of each occluder |
| `IOrbiter.Parent` | [current/decomp/KSA/IOrbiter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IOrbiter.cs#L18) | Walk up to the system root |
| `IParentBody.Children` | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L27) | Bodies and vehicles orbiting a body |
| `IRadius.MeanRadius` | [current/decomp/KSA/IRadius.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IRadius.cs#L5) | Body sphere radius |
| `Vehicle.BoundingSphereRadiusBody` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L527) | Vehicle sphere radius |

## Pitfalls and version notes

- **Mean-radius sphere, not terrain.** Mountains above the mean radius do not hide lines near the
  horizon, and a camera below the mean radius (deep valley, ocean floor) drops the planet as an
  occluder entirely.
- **No atmosphere or daylight logic.** Lines stay fully visible against a bright daytime sky;
  only solid spheres hide them.
- **Whole-segment rejection.** Testing three points means a long segment can vanish while most of
  it is still in open sky; subdividing long segments would fix this at the cost of more tests.
- **Cost.** Every frame the list holds every body in the system plus every vehicle that is a
  direct child of a body, and each drawn segment runs up to three tests against all of them. With
  the RA/Dec grid at 1 deg spacing this is tens of thousands of segments per frame.
- **Only `IParentBody.Children` vehicles.** Vehicles reachable some other way are not
  occluders.

## See also

- [Constellation lines and labels on the sky](sky-overlay-lines-and-labels.md)
- [RA/Dec grid](ra-dec-grid.md)
- [Star pointer](star-pointer.md)
- [Per-frame ego-space sphere list and ray test per overlay point](../../../howto/rendering/overlays/project-world-positions-to-screen/project-world-positions-to-screen-6.md) - how-to article demonstrating this feature

[^oms-stellariumrenderer-cs]: OhMyStars src/StellariumRenderer.cs
[^ksa-stellarbody-cs]: KSA.StellarBody (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody-cs]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-vehicle-cs]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-camera-cs]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-program-cs]: KSA.Program (decompiled, build 2026.10.10.5554)
