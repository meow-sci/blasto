---
type: How-To
title: Ego-space direction times a sphere radius, then Camera.EgoToScreen plus viewport origin
description: Place sky directions on the same camera-centred sphere the star sprites use, project them with the main camera and add the ImGui main viewport origin
category: rendering
group: overlays
topic: project-world-positions-to-screen
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ohmystars/features/sky-overlay-lines-and-labels]
techniques: [imgui-drawlist, sky-projection]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Camera, KSA.IViewport, KSA.Program]
tags: [projection, sky, ego, star-sphere, imgui-drawlist]
---

Outcome: overlay points for things that have a direction but no meaningful distance (stars, constellation lines, a sky grid) that sit exactly on KSA's star sprites from any camera position, with no parallax. Demonstrated by Oh My Stars
([Constellation lines and labels on the sky](../../../../mods/ohmystars/features/sky-overlay-lines-and-labels.md)). Prerequisites: StarMap. The drawing recipe for whole constellations is in [Draw lines and labels on the sky](../draw-lines-on-the-sky/overview.md).

## Background: how the game does it

KSA places its stars on a sphere centred on the camera: `InstancedStarTechnique.AddInstance` stores `NormalizeOrZero(forward) * distance` [current/decomp/KSA/InstancedStarTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedStarTechnique.cs#L68-L83), and `Mod.LoadStarBinaries` passes `distance: 19.5f` [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L231-L285). Ego space is
ecliptic-aligned with the camera at the origin (`Camera.EclToEgo` only subtracts `PositionEcl`), so a direction `d` in the ecliptic frame corresponds to the ego point `d * 19.5`; because the view part is a pure rotation, any other radius projects to the same pixel, and only the centre (the camera) matters. The view part of the camera matrix for that pass is a pure rotation, which is why the stars have no parallax. The stock galactic grid uses the same projection recipe with a radius of 1000
[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1804-L1870).

## Lifecycle and timing

`[StarMapAfterGui]` (`frame:draw-ui`), after `OnFrameViewports` has rebuilt the camera's view-projection and frustum planes for this frame, so the projection matches the image that will be rendered. The overlay vanishes with the HUD. Only the main viewport and camera are used;
secondary viewports get nothing. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Convert catalog directions to the ecliptic once at load (see [Draw lines and labels on the sky](../draw-lines-on-the-sky/overview.md) for RA/Dec and galactic conversions).

2. Scale to the star sphere and project. `EgoToScreen(double3, ignoreBehind = true)` returns NaN for points behind the camera; add `ImGui.GetMainViewport()->Pos`, because the pixels are framebuffer-relative:

   ```csharp
   // src/StellariumRenderer.cs L56-L63
   public static float2 EgoToOverlayScreen(Camera camera, double3 position) {
       float2 screen = camera.EgoToScreen(position);
       ImGuiViewport* viewport = ImGui.GetMainViewport();

       return viewport == null
           ? screen
           : screen + viewport->Pos;
   }
   ```

3. In the draw routine take the main camera, use `center = double3.Zero` and `radius = 19.5` so every layer places `direction * radius` in the camera's ego frame, and draw into one full-screen window's draw list:

   ```csharp
   // src/StellariumRenderer.cs L79-L114
   public static void Draw() {
       Camera camera = Program.GetMainCamera();
       ImGuiViewport* viewport = ImGui.GetMainViewport();

       if(camera == null || viewport == null)
           return;

       ImDrawListPtr? draw_list = CreateWindow(viewport);
       if(draw_list == null)
           return;

       IParentBody? occluderRoot = GetOccluderRoot(camera.Following);
       if(occluderRoot is null) {
           celestialOccluders.Clear();
       } else {
           CollectCelestialOccluders(camera, occluderRoot);
       }

       // StarsEdit places stars at this fixed distance from the camera, so the overlays must use
       // the same camera-centered sphere to avoid a camera-offset parallax error.
       double3 center = double3.Zero;
       double radius = StarDistance;

       SkyMarkingsRenderer.Draw(draw_list.Value, camera, center, radius);
       SkyCulturesRenderer.Draw(draw_list.Value, camera, center, radius, showAsterisms, showAsterismNames, showStarNames);
       SkyCulturesRenderer.DrawSelectedDropdownStarName(draw_list.Value, camera, center, radius);

       if(showIAUConstellations)
           IAUConstellationsRenderer.Draw(draw_list.Value, camera, center, radius);

       DrawStarPointer(draw_list.Value, camera, radius);

       NavballMarkerRenderer.Draw();

       ImGui.End();
   }
   ```

4. Straight screen segments between two projected endpoints are exact great circles under a perspective projection (a gnomonic projection of the sphere), so lines between catalog stars need no subdivision as long as both endpoints are in front of the camera. Small circles such as declination parallels are not great circles and must be chopped into short pieces.

5. Skip any segment with a NaN endpoint (the stock grid does) instead of passing NaN to `AddLine`.
   Subdivide long segments first (normalised lerp between the two directions, a few degrees per
   piece), otherwise a constellation line with one star behind the camera disappears entirely
   instead of running to the screen edge; see step 9 of
   [Draw lines and labels on the sky, approach 1](../draw-lines-on-the-sky/draw-lines-on-the-sky-1.md).

## Complete example

| File | Role |
|---|---|
| [src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L56-L114) | Projection helper and the sphere-based draw routine |
| [src/SkyCulturesRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyCulturesRenderer.cs#L219-L250) | Asterism segments projected and stroked |

## Variations and gotchas

- **NaN endpoints are not filtered** in Oh My Stars, so `AddLine` receives NaN for segments with an endpoint behind the camera; copy the stock grid's `float.IsNaN` check.
- **Fixed J2000.** There is no precession or epoch handling; the lines are a J2000 sky, which matches the engine's own constants.
- **Window draw list.** The overlay window's stacking follows ImGui window order; `GetBackgroundDrawList()` would put it strictly under every window.
- **Shared window name.** Another mod also begins a window named `HUDFullscreenWindow`; use a unique name such as `##MyModOverlay`.
- Hiding the overlay behind planets is the mod's job (approach 6), because the ImGui pass is composited over the rendered scene.

## See also

- [Overview](overview.md)
- [Approach 1: real points through the viewport camera](project-world-positions-to-screen-1.md)
- [Approach 6: CPU occlusion by bodies](project-world-positions-to-screen-6.md)
- [Constellation lines and labels on the sky](../../../../mods/ohmystars/features/sky-overlay-lines-and-labels.md)
- [Draw lines and labels on the sky](../draw-lines-on-the-sky/overview.md), [Draw full-screen click-through overlays and choose the right ImGui draw list](../../../ui/overlays/draw-a-fullscreen-click-through-overlay/overview.md)
