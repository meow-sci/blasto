---
type: Game Feature
title: Sample terrain and pad surfaces under each wheel
description: How wheel or ground-contact code gets the true surface under a point, combining the accurate terrain height with the launch-pad and static-object surfaces the height field omits
category: physics
group: aero-and-ground
topic: sample-the-ground-under-each-wheel
demonstrated_by:
  - mods/ksacars/features/terrain-contact-under-wheels
  - mods/ksagolf/features/launch-site-surface-raycast
  - mods/ksacars/features/launch-pad-surface-contact
lifecycle: [frame:dispatch-solvers, frame:draw-ui, startup:all-mods-loaded]
game_types:
  - KSA.LocationReference
  - KSA.LandmarkReference
  - KSA.StaticObject
  - KSA.ConstraintSim
  - KSA.CelestialTemplate
  - KSA.Celestial
tags: [physics, terrain, launch-pad, static-object, wheels, raycast, bepu, height-field]
---

A wheel that springs against the ground needs one number per frame: how high its hub is above whatever the craft would
rest on. KSA does not offer a "what is under this point" call, and the obvious source, the terrain height field, is only
part of the answer. A launch site is laid over the field: a levelled disc of ground, a pad deck a metre or two higher, and
drawn road and grass that the collider does not cover. This topic compares three ways to get a surface height: the field
alone (sampled accurately), the field plus pad colliders copied from the engine's shape registry, and the field plus pad
colliders rebuilt from template data.

## Engine background

| Piece | What it gives a mod |
|---|---|
| `Celestial.GetTerrainHeightFromDirCce` / `GetTerrainHeightFromDirCcf` [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L777-L882) | Height above the mean radius along a direction from the body centre. `accurate` (default `true`) selects bicubic sampling instead of bilinear and evaluates every terrain modifier; the engine's own collision terrain uses the default[^ksa-celestial] |
| `TerrainPatch.GetTerrainRadiusCcf` [current/decomp/KSA/TerrainPatch.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TerrainPatch.cs#L517-L524) | The collision patch is built from `GetTerrainHeightFromDirCcf(dir)` with the default, so an accurate sample matches the colliders[^ksa-terrainpatch] |
| `CelestialTemplate.Locations` (reached through `Celestial.BodyTemplate`), `LandmarkReference.IsLaunchPad`, `LocationReference.GetStaticObject`, `GetAxesCcf` [current/decomp/KSA/LocationReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LocationReference.cs#L117-L160) | Which landmarks are launch pads, their static object, and the up, east and north axes in the body-fixed frame (`GetAxesCcf` sets up to `ForwardCcf`)[^ksa-locationreference] |
| `StaticObject` [current/decomp/KSA/StaticObject.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObject.cs#L12-L36) | `Template` (colliders and sub-objects), `GroundOffset`, `SurfaceHeight`, `FootprintRadius`, `CollisionShape` (a Bepu `TypedIndex` into the shared shape registry) and the private `_models` list of drawn model instances[^ksa-staticobject] |
| `ConstraintSim.ResolveLaunchPads`, `UpdateStaticObjectCollider` [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L550-L628) | The engine's pad pose: on the terrain at the landmark, lifted by `GroundOffset`, with up, east, north as the basis; the nearest pad within 300 m (the squared range 90000) gets a Bepu static in the bubble's simulation[^ksa-constraintsim] |
| `Vehicle.GetLaunchPadHeightAtDirCcf` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L4193-L4214) | Placement by latitude and longitude adds `GroundOffset + SurfaceHeight` for the first pad whose `FootprintRadius` contains the point[^ksa-vehicle] |
| `ConstraintSim.UnlockShapes` [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L111-L133) | Interlock on the shared shape registry: it throws while the vehicle update is stepping, so reads must happen at the frame sync point |

The height field answers the seabed under water and the levelled ground under a pad. In a wheel loop that means two
questions per hub: how high is the terrain, and is there a pad collider above it.

## Where it runs

`Program.PrepareFrame` applies the previous step, polls input, and calls `Universe.ExecuteNextVehicleSolvers`, which calls
`Vehicle.PrepareWorker` for every vehicle on the main thread before queuing the next step.[^ksa-universe] Both wheel
approaches sample from that main-thread window (`frame:dispatch-solvers`): the physics state, the body attitude and the
height field then describe the same instant, and the resulting impulse is integrated by the step about to be dispatched. A
copy of the shape registry has to be taken in a prefix on `ExecuteNextVehicleSolvers` (the simplest moment at which `UnlockShapes()` is
legal on the main thread, because no vehicle step is running yet) and can be queried any time afterwards. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#the-physics-step).

## Approaches

| Article | Mod | Technique | Trade-off |
|---|---|---|---|
| [1: GetTerrainHeightFromDirCce with accurate: true in the PrepareWorker prefix](sample-the-ground-under-each-wheel-1.md) | KSACars | harmony-prefix | Simplest and matches the colliders on terrain; blind to pads and anything else drawn over the field |
| [2: Copy convex children under the shapes lock in an ExecuteNextVehicleSolvers prefix](sample-the-ground-under-each-wheel-2.md) | KSAGolf | main-thread-sync-point | Exact for any collider the engine built (every convex shape type), plus drawn ground triangles; needs the sync-point copy and a reflection read of `_models` |
| [3: Rebuild box and cylinder colliders from template data and drop a ray per wheel](sample-the-ground-under-each-wheel-3.md) | KSACars | harmony-prefix | No registry access and no lock; reimplements the pose and covers only boxes and cylinders |

Combine them: article 1 for the field and article 3 (or 2) for pads, taking the lesser hub height, as KSACars does.

## See also

- [Override the terrain height cameras and wheels see](../override-terrain-height-for-roofs-and-platforms/overview.md): the `Cce` overload a wheel mod reads can be answered differently by another mod
- [Simulate wheels, suspension and tyres without a wheel module](../simulate-wheels-and-suspension-without-a-wheel-module/overview.md): what consumes the hub heights
- [Create and free shapes in KSA's global Bepu shape registry](../../colliders/mutate-the-shared-bepu-shape-registry/overview.md)
- [Add mod-owned static colliders that vessels and kittens collide with](../../colliders/add-static-colliders-to-physics-bubbles/overview.md)
- [Ray cast against part meshes and terrain, from the cursor or any origin](../../mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md)
- The topic query-terrain-height-and-line-of-sight (other batch) covers the height query in general
- [Query the terrain height field and test line of sight across terrain](../../../celestials/terrain/query-terrain-height-and-line-of-sight/overview.md) - related topic

[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-terrainpatch]: KSA.TerrainPatch (decompiled, build 2026.10.10.5554)
[^ksa-locationreference]: KSA.LocationReference (decompiled, build 2026.10.10.5554)
[^ksa-staticobject]: KSA.StaticObject (decompiled, build 2026.10.10.5554)
[^ksa-constraintsim]: KSA.ConstraintSim (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
