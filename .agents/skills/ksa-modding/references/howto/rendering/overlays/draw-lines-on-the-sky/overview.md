---
type: Game Feature
title: Draw lines and labels on the sky
description: Draw constellation lines, boundary polylines, star names and labelled RA/Dec grids on KSA's sky from catalog data in a click-through ImGui overlay that tracks the camera, with J2000 conversion to the ecliptic frame the game draws in
category: rendering
group: overlays
topic: draw-lines-on-the-sky
demonstrated_by: [mods/ohmystars/features/sky-overlay-lines-and-labels, mods/ohmystars/features/ra-dec-grid]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.Camera, KSA.Universe, KSA.GalacticPlane, KSA.InstancedStarTechnique, Brutal.ImGuiApi.ImGui]
tags: [sky, overlay, constellations, ra-dec, imgui-drawlist, projection]
---

KSA draws its stars as sprites on a sphere of radius 19.5 centred on the camera, and it has no
API for sky annotations. A mod draws lines and labels with ImGui instead: a catalog direction placed
around the camera and projected with the main camera lands exactly on the sprites, with no
parallax, from any position in the system. The work is the coordinate conversion (catalog RA/Dec or
galactic coordinates to the game's ecliptic axes), the projection, behind-camera handling, culling,
occlusion and decluttering.

## Engine background

- Stars: `InstancedStarTechnique.AddInstance` stores `NormalizeOrZero(forward) * distance` [current/decomp/KSA/InstancedStarTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedStarTechnique.cs#L68-L83); `Mod.LoadStarBinaries` passes 19.5 [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L231-L285).
- Frames: `GalacticPlane.EquatorialToEcliptic` / `EclipticToEquatorial` [current/decomp/KSA/GalacticPlane.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GalacticPlane.cs#L43-L55) rotate about X by the J2000 obliquity; `GalacticPlane.GetDirectionEcl(longitude, latitude)` [current/decomp/KSA/GalacticPlane.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GalacticPlane.cs#L77-L87) converts galactic coordinates.
- Projection: ego space is the ecliptic frame with the camera at the origin and the view matrix is a pure rotation (`Camera.OnFrame` [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L487-L497)), so `direction * r` projects to the same pixel for any radius `r`; `Camera.EgoToScreen` returns NaN behind the camera plane and viewport-relative pixels otherwise [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L347-L355). A perspective camera is a gnomonic projection of that sphere, so great circles are straight screen lines and small circles are curves.
- Stock reference: `Universe.DrawGalacticGrid` [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1804-L1870), drawn with `ImGui.GetForegroundDrawList()` from `Universe.OnDrawUi` once per game viewport (terminal command `skygrid`).
- Hook: `[StarMapAfterGui]` (`frame:draw-ui`), after `OnFrameViewports` has finalised the cameras. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: window draw list with ego-space sky directions](draw-lines-on-the-sky-1.md) | ohmystars | Catalog to ecliptic, times 19.5, `EgoToScreen`, straight segments | `startup:all-mods-loaded`, `frame:draw-ui` | Exact great circles with no subdivision; labels and boundaries from the same path; fixed J2000; the mod leaves NaN endpoints and a shared window name for you to fix |
| [2: one-degree arcs, clip-space cull, label rectangles](draw-lines-on-the-sky-2.md) | ohmystars | Lazy arc build, inflated clip test, declutter | `frame:draw-ui` | Correct small circles and legible labels; tens of thousands of segments at fine spacing |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Project world and sky positions to screen pixels and point at off-screen targets](../project-world-positions-to-screen/overview.md), [Draw full-screen click-through overlays and choose the right ImGui draw list](../../../ui/overlays/draw-a-fullscreen-click-through-overlay/overview.md)
- [Keep mod UI and per-frame work running when the HUD is hidden (F2)](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md)
