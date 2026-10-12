---
type: Game Feature
title: Pick the vessel, part, world quad or terrain point under the cursor
description: How a mod decides what the mouse is over in the 3D view, from screen-space nearest through hull ray casts to terrain marches, and how it acts on the answer
category: ui
group: input
topic: pick-objects-under-the-cursor
demonstrated_by:
  - mods/ksagolf/features/cursor-picking-and-terrain-ray
  - mods/ksacars/features/craft-mover-mouse-pick-place
  - mods/ksagolf/features/craft-mover
  - mods/ksarmory/features/cursor-ray-world-click-tools
  - mods/purrtty/features/ray-pick-focus-for-world-quads
  - mods/gatos/features/sticker-projected-decals
lifecycle: [frame:draw-ui, frame:input, frame:viewports, frame:after-on-frame]
game_types:
  - KSA.Part
  - KSA.Vehicle
  - KSA.Cursor
  - KSA.Ray
  - KSA.Celestial
  - KSA.CelestialSystem
  - KSA.Camera
  - KSA.InputEvents
  - KSA.Program
  - KSA.Universe
  - KSA.IGameViewport
tags: [picking, raycast, vehicle, part, terrain, quad, teleport]
---
Once you have a ray ([cursor to world ray](../cursor-to-world-ray/overview.md)), "what is under the cursor" is a
separate question with a different answer depending on what you need: a whole vessel, a part, a point on a
mod-drawn quad, or a place on the ground. KSA answers only one of these for you (its own part hover outline). Everything
else is built by mods from the same few engine pieces.

## Engine background

| Type | File | Role |
|---|---|---|
| `Part.RayCastEgo` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2534-L2572) | Ray (ego space) against a part's sub-part meshes: sphere broad phase, then `Ray.RaycastWatertight`; returns near/far distance, hit position and normal in the closest sub-part's frame |
| `Vehicle.UpdateHighlight` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2903-L2924) | The engine's own hover pick: `Cursor.GetEgoRay`, `GetMatrixAsmb2Ego`, `RayCastEgo` per part, offered to the viewport's `PartPicker`; orbit mode only |
| `Ray` | [current/decomp/KSA/Ray.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Ray.cs) | `RaycastMollerTrumbore` (triangle, double-sided), `RaycastWatertight` (triangle soup), box and sphere casts |
| `Celestial` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L777-L781) | `GetTerrainHeightFromDirCce(dir, accurate)`, `GetLatitudeFromCce`, `GetLongitudeFromCce`; no CPU ray-vs-terrain routine exists |
| `Vehicle.TeleportToLocation` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L4219-L4233) | Queues a `TeleportInputData` on `InputEvents.TeleportInputBuffer` |
| `InputEvents` | [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L407-L423) | Buffered state changes applied in `PrepareFrame` (`frame:input`), when physics is settled |

**Frame order** (see [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).
Picking code runs in the GUI hook (`frame:draw-ui`) or, to keep working with the HUD hidden, in a frame hook; the mouse
state is valid in either because `PrepareImGui` runs before the `DrawUI` gate. Anything that **changes** a vehicle must go
through a buffer such as `InputEvents` so it lands in the settled window.

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [Screen-space nearest with a per-item reach](pick-objects-under-the-cursor-1.md) | KSAGolf, KSACars | Project every vehicle, pick the nearest within an apparent-size radius | `frame:draw-ui` | Cheap, no mesh test, no occlusion |
| 2 | [Part.RayCastEgo hull test plus terrain march](pick-objects-under-the-cursor-2.md) | KSArmory | Engine hull ray, ground nearer than craft hides it, fire-at-spot | `frame:draw-ui`, `frame:input` | Exact, ignores sub-part-less parts |
| 3 | [Ray hit to texture UV for in-world UI](pick-objects-under-the-cursor-3.md) | purrTTY | Moller-Trumbore on quad triangles, project to UV, UV to cell | `frame:draw-ui` | Mod-drawn geometry only |
| 4 | [Clearance-stepped march with bisection, then TeleportToLocation](pick-objects-under-the-cursor-4.md) | KSAGolf, KSACars | Walk out from the eye, bisect, name lat/lon, queue a teleport | `frame:draw-ui`, `frame:input` | Terrain only, structures excluded |
| 5 | [Ray.RaycastWatertight plus terrain bisection](pick-objects-under-the-cursor-5.md) | gatOS | Part-local or body-fixed anchors from one function | `frame:viewports`, `frame:draw-ui` | One-shot command, anchor survives vessel switches |

## Choosing

- Approximate and cheap, or many small craft: approach 1.
- Which craft or part exactly: approach 2 (or 5 when you need the part-local hit for an anchor).
- Interactive panel in the world: approach 3.
- A point on the ground, or placing a craft there: approach 4.

## See also

- [Turn the mouse cursor into a world ray](../cursor-to-world-ray/overview.md)
- [Share the keyboard and mouse between ImGui, mod tools and the game](../coordinate-imgui-focus-with-game-input/overview.md)
- [Place pieces on a grid with a snapping ghost](../place-pieces-on-a-grid-with-a-ghost/overview.md)
- [Choose a render frame and anchor effects to bodies, parts and terrain](../../../rendering/overlays/choose-a-render-frame-and-anchor-effects/overview.md)
- [Ray cast against part meshes and terrain, from the cursor or any origin](../../../physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md)

