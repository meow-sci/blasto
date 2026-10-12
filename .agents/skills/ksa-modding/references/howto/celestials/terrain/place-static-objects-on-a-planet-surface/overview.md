---
type: Game Feature
title: Place a static object on a planet surface at a latitude and longitude, and keep it anchored
description: Stand XML static objects on a planet with landmark references, redraw them when the engine culls them, and anchor mod-drawn models in the body-fixed frame so they follow rotation and restore from saves
category: celestials
group: terrain
topic: place-static-objects-on-a-planet-surface
demonstrated_by:
  - mods/ksastructures/features/office-landmarks-static-objects
  - mods/unscience/features/sphinx-glb-statics-placement
  - mods/unscience/features/sphinx-static-object-render-hooks
  - mods/ksagolf/features/course-grid-and-save-file
  - mods/ksagolf/features/minigolf-course-builder
  - mods/ksagolf/features/mod-mesh-draw-hook
lifecycle: [startup:all-mods-loaded, frame:render, physics:bepu, frame:draw-ui, event:save, event:load]
game_types:
  - KSA.LandmarkReference
  - KSA.LocationReference
  - KSA.CelestialTemplate
  - KSA.StaticObject
  - KSA.StaticObjectTemplate
  - KSA.StaticSubObjectTemplate
  - KSA.ConstraintSim
  - KSA.ModLibrary
  - KSA.Celestial
  - KSA.Camera
  - KSA.Program
  - KSA.Vehicle
  - KSA.IViewport
  - KSA.Universe
tags: [static-object, landmark, launch-pad, latitude-longitude, anchor, ccf]
---
KSA's own buildings are static objects: XML templates with sub-objects (render models plus Bepu box colliders) that a **landmark** in a body template's `Locations` list stands on the planet at a latitude and longitude. Core uses this for its launch pads. A mod has two ways to put something on the ground: add `LandmarkReference` entries in code before the engine resolves static objects (engine-drawn, engine-collided), or draw its own models and anchor them itself in the body-fixed frame (`Ccf`), which then has to survive rotation, floating-origin shifts and save games.

## Engine background

- `LandmarkReference` extends `LocationReference`; `IsLaunchPad` turns on `ShowGroundMarker`, which gates drawing ([current/decomp/KSA/LandmarkReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LandmarkReference.cs)). A `LocationReference` has `Latitude`, `Longitude` (`RadianReference`), `StaticObjectId` and a computed `ForwardCcf` (set by `OnDataLoad`, [current/decomp/KSA/LocationReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LocationReference.cs#L40-L50)).
- Local axes of a landmark: `GetAxesCcf` returns up as `ForwardCcf`, east and north from `UnitZ`; the static object's asset frame is +X up, +Y east, +Z north ([current/decomp/KSA/LocationReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LocationReference.cs#L148-L154)).
- `ModLibrary.AttachGameData` ends with `StaticObject.ResolveAll`, which builds one `StaticObject` per template (render models and a Bepu `BigCompound`) and resolves every location of every celestial template ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1863-L1908), [current/decomp/KSA/StaticObject.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObject.cs#L47-L75)).
- Drawing: `LocationReference.UpdateStaticObjectRenderData` places the object at `GetSurfacePositionEclFromCce(ForwardCcf)` plus `up * GroundOffset`, only when `Camera.IsVisibleTo` passes ([current/decomp/KSA/LocationReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LocationReference.cs#L156-L180)); `IsVisibleTo` samples the terrain with `accurate: false` ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L445-L453)).
- Collision: `ConstraintSim.ResolveLaunchPads` builds the list of launch-pad landmarks that have a collision shape (once per celestial) and `UpdateStaticObjectCollider` gives each craft or kitten the nearest within 300 m (squared distance 90000) as its single static collider ([current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L550-L583), [current/decomp/KSA/ConstraintSim.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConstraintSim.cs#L584-L620)).
- Template fields: `GroundOffset`, `SurfaceHeight`, `FootprintRadius` on `StaticObjectTemplate` ([current/decomp/KSA/StaticObjectTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObjectTemplate.cs#L18-L37)).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: IsLaunchPad landmarks added at StarMapAllModsLoaded, plus a render-data postfix](place-static-objects-on-a-planet-surface-1.md) | ksastructures | `xml-asset` | `startup:all-mods-loaded`, `frame:render`, `physics:bepu` | Engine draws, shades and collides it; one pad collider per craft; culled from inside a dug pit |
| [2: GroundAnchor composed each frame into a camera-relative matrix](place-static-objects-on-a-planet-surface-2.md) | unscience | `glb-model` | `frame:draw-ui`, `frame:render`, `event:save`, `event:load` | Any imported GLB, live editing, saved with the scene; mod owns rendering and colliders |
| [3: course frame anchored at the kitten, saved as latitude, longitude, heading and floor height](place-static-objects-on-a-planet-surface-3.md) | ksagolf | `custom-physics` | `frame:draw-ui`, `frame:render` | Terrain-change-tolerant save format; per-viewport draw through a hook |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Exclude or replace ground clutter](../../../celestials/terrain/exclude-or-replace-ground-clutter/overview.md), [Add terrain decals and dig a site](../../../celestials/terrain/add-terrain-decals-and-dig-a-site/overview.md)
- [Add static colliders to physics bubbles](../../../physics/colliders/add-static-colliders-to-physics-bubbles/overview.md), [Sample the ground under each wheel](../../../physics/aero-and-ground/sample-the-ground-under-each-wheel/overview.md), [Build a moving platform kittens can ride](../../../physics/colliders/build-a-moving-platform-kittens-can-ride/overview.md)
- [Query the terrain height field and test line of sight](../../../celestials/terrain/query-terrain-height-and-line-of-sight/overview.md), [Draw mod meshes with the engine renderers](../../../rendering/meshes/draw-mod-meshes-with-the-engine-renderers/overview.md), [Choose a render frame and anchor effects](../../../rendering/overlays/choose-a-render-frame-and-anchor-effects/overview.md)
- Anchor and pick points on bodies, [Ray cast against vessel meshes and terrain](../../../physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md)
- [Keep grass, rocks and trees off a custom site, and replace or preserve a planet's ground clutter](../exclude-or-replace-ground-clutter/overview.md) - related topic
- [Add terrain decals from code and flatten or dig the ground under a custom site](../add-terrain-decals-and-dig-a-site/overview.md) - related topic
- [Query the terrain height field and test line of sight across terrain](../query-terrain-height-and-line-of-sight/overview.md) - related topic
- [Anchor points to a rotating body and pick bodies and orbit lines under the cursor](../../../flight-and-orbits/map-view/anchor-and-pick-points-on-bodies/overview.md) - related topic

