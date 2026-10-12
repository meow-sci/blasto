---
type: Game Feature
title: Place a vessel on a surface at a latitude and longitude or on a structure
description: How a mod sets a vessel down on a body's surface with Vehicle.TeleportToLocation, corrects its bounds-based clearance when the vessel is not nose-up, and lends a structure's surface height so a craft stands on a roof
category: physics
group: kinematics
topic: place-a-vessel-on-a-surface-at-latitude-and-longitude
demonstrated_by:
  - mods/ksagolf/features/craft-mover
  - mods/ksacars/features/craft-mover-mouse-pick-place
  - mods/ksarmory/features/spawn-and-teleport-test-craft
  - mods/unscience/features/iron-man-surface-teleport-correction
  - mods/ksastructures/features/terrain-height-roof-override
  - mods/ksastructures/features/office-furniture-craft
lifecycle: [frame:draw-ui, frame:input, frame:prepare, event:teleport, frame:dispatch-solvers, event:save]
game_types: [KSA.Vehicle, KSA.InputEvents, KSA.Celestial, KSA.LocationReference]
tags: [physics, kinematics, teleport, surface, latitude, longitude, launch-pad, bounds, placement]
---

`Vehicle.TeleportToLocation(celestial, lat, lon)` is the engine's own "put this craft on the ground here": it builds
the initial kinematic state from the craft's bounding box, so the hull arrives resting on the terrain, not with its
origin at the point, and it queues the result for the next frame. It is the safe way to place a vessel from a GUI hook,
because nothing mutates vehicle state until `ApplyInputEvents` runs in the settled window. Its assumptions (rocket
orientation, terrain height under the box corners, launch pad surface height) are the three things mods adjust.

## Engine background

| Piece | Behaviour |
|---|---|
| `Vehicle.TeleportToLocation` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L4219-L4233) | Mass-centred bounds from `MassToGeometryAsmb` and the box half extents, `GetInitialKinematicStateForLocation(celestial, Universe.GetNextSimStep().NextTime, lat, lon, min, max, 0, colour)`, then a `TeleportInputData` into `InputEvents.TeleportInputBuffer` |
| `Vehicle.GetInitialKinematicStateForLocation` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L4135-L4191) | Orients the body with `ComputeBody2Cce(radialUp, tangent)` (body +X radially out), takes the highest `GetTerrainHeightFromDirCcf` under the four corners of the **min-X face**, places the centre of mass at `(centre.X - min.X) + MeanRadius + terrain + launch pad height` with the surface rotation velocity |
| `Vehicle.GetLaunchPadHeightAtDirCcf` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L4193-L4215) | The `GroundOffset + SurfaceHeight` of the first launch-pad landmark in list order whose footprint holds the point |
| `InputEvents.ApplyInputEvents` | Drains `TeleportInputBuffer` early in `PrepareFrame`, after the solver apply and before the dispatch; the vessel is still at its old position for the rest of the frame |

So there are three levers: **when** (always queued, one action at a time), **the bounds** (a non-rocket vessel's body frame is
different, so the min-X face is the wrong face) and **the height** (the terrain and pad numbers the helper reads, which a
structure's roof does not change). See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Trade-off |
|---|---|---|---|
| [1: Queue `TeleportToLocation` once per action](place-a-vessel-on-a-surface-at-latitude-and-longitude-1.md) | KSAGolf, KSACars, KSArmory | starmap-lifecycle | Simplest and safest; not a per-frame call, and the vessel is east-facing and upright |
| [2: Transpile the `GetInitialKinematicStateForLocation` call for head-up bounds](place-a-vessel-on-a-surface-at-latitude-and-longitude-2.md) | unscience (Iron Man) | harmony-transpiler | Right clearance for a kitten; a call-site rewrite that must match exactly once |
| [3: Lend the pad template's `SurfaceHeight` around `TeleportToLocation`](place-a-vessel-on-a-surface-at-latitude-and-longitude-3.md) | KSAStructures | harmony-prefix | Stands a craft on a roof without changing the engine's maths; temporary template state |
| [4: Build one-part craft in code and set them on the ground](place-a-vessel-on-a-surface-at-latitude-and-longitude-4.md) | KSAStructures | harmony-prefix | Spawns a craft and sets it on a floor 62 m up; blocking shape lock and save stripping |

## See also

- [Teleport a vessel by building a new orbit](../teleport-a-vessel-by-building-a-new-orbit/overview.md)
- [Weld one vessel to another](../weld-one-vessel-to-another/overview.md)
- [Ray cast against part meshes and terrain](../../mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md), which picks the destination under the cursor
- The topic override-terrain-height-for-roofs-and-platforms (other batch) covers the terrain-height half
- [Override the terrain height cameras and wheels see](../../aero-and-ground/override-terrain-height-for-roofs-and-platforms/overview.md) - related topic

