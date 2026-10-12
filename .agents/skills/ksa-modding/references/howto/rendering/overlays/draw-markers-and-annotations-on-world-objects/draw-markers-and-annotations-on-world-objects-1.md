---
type: How-To
title: Constant-pixel-size brackets, cues and bars projected from craft and contact positions
description: Draw brackets, lock cues and health bars at a constant pixel size over craft and contacts using one projection pattern with edge arrows and occlusion marks
category: rendering
group: overlays
topic: draw-markers-and-annotations-on-world-objects
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/world-anchored-screen-markers]
techniques: [imgui-drawlist]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Camera, KSA.IViewport, KSA.ImGuiHelper, KSA.Vehicle, KSA.Program]
tags: [markers, brackets, overlay, imgui-drawlist, edge-arrow, occlusion]
---

Outcome: a bracket on a craft, a lock cue on a contact, a chase HUD and part health bars "on the glass" over the 3D world, all at a constant pixel size so a distant target is never a sub-pixel dot and terrain never hides the mark. Four overlays share one projection pattern: project a world position through the
viewport's `Camera`, then draw lines, triangles, rects and text on the draw list of a transparent, input-less ImGui window that covers the whole main viewport. Demonstrated by KSArmory ([World-anchored screen markers on full-screen ImGui overlay windows](../../../../mods/ksarmory/features/world-anchored-screen-markers.md)). Prerequisites: StarMap; projection in
[project world positions to screen](../project-world-positions-to-screen/overview.md).

## Background: how the game does it

`Camera` projects (`EclToScreen`, `EgoToScreen`) [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L339-L355), exposes its basis (`GetForwardEcl/RightEcl/UpEcl`) [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L176-L190) and the followed-object-aware `GetPositionEgo` [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L236-L250). Viewports come from `ViewportRegistry.GameViews`; the main one is found by reference
against `Program.MainViewport`, not by index 0. ImGui windows do not nest, which shapes how clickable labels are built.

## Lifecycle and timing

All four are drawn from `[StarMapAfterGui]` (`frame:draw-ui`), after the mod's own step and panel, each guarded by "in flight" and a config switch. Camera matrices are final (`OnFrameViewports`), ImGui calls are legal between `PrepareImGui` and `ImGui.Render`, and world state is settled after
`ApplyVehicleSolvers`. When `Program.DrawUI` is false (F2, or `ScreenshotCapture` for the duration of a capture) `OnDrawUiViewports` is not called, so none of these overlays draw. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Open the overlay window** sized to `ImGui.GetMainViewport()` with a transparent background and `NoInputs` so clicks fall through to the game; hover is computed from `ImGui.GetMousePos()` against the projected point (an 18 px radius for markers). Draw with `ImGui.GetWindowDrawList()` (the window list, not the background or foreground list).

   ```csharp
   // src/KSArmory/Ksa/Markers.cs L95-L118
   ImGuiViewportPtr main = ImGui.GetMainViewport();
   ImGui.SetNextWindowPos(main.Pos, ImGuiCond.Always);
   ImGui.SetNextWindowSize(main.Size, ImGuiCond.Always);
   ImGui.SetNextWindowBgAlpha(0f);

   // NoInputs: the overlay covers the screen, so anything else would swallow every click in
   // the game. Hovering is worked out from the pointer position instead.
   const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration
                                  | ImGuiWindowFlags.NoInputs
                                  | ImGuiWindowFlags.NoNav
                                  | ImGuiWindowFlags.NoFocusOnAppearing
                                  | ImGuiWindowFlags.NoBringToFrontOnFocus
                                  | ImGuiWindowFlags.NoSavedSettings
                                  | ImGuiWindowFlags.NoBackground;

   if (!ImGui.Begin("##KSArmoryMarkers", flags))
   {
       ImGui.End();
       return;
   }

   ImDrawListPtr draw = ImGui.GetWindowDrawList();
   float2 cursor = ImGui.GetMousePos();
   double3 eye = KsaWorld.CameraPositionEcl();
   ```

2. **Project each target** with an on-screen-or-clamp helper; project the **drawn** position (`Camera.GetPositionEgo(vehicle)`), not the simulated one, because drawing the raw position makes the bracket jump whenever warp changes. Skip craft that subtend more than 0.03 rad (already plainly visible).

3. **Choose the glyph:** a four-corner bracket when in view, crossed and dimmed when `KsaWorld.IsOccluded` finds any body between camera and craft, an edge arrow when off screen so a site can be located from a craft that cannot see it; labels fade over the last 2 s of a 10 s timer.

   ```csharp
   // src/KSArmory/Ksa/Markers.cs L137-L158

   if (!KsaWorld.TryProjectOrClamp(atEcl, out float2 at, out bool inView)) continue;

   bool isActive = ReferenceEquals(craft, active);
   bool blocked = KsaWorld.IsOccluded(eye, atEcl, out _);
   ImColor8 colour = (isActive, blocked) switch
   {
       (true, false) => Active,
       (true, true) => HiddenActive,
       (false, false) => Idle,
       (false, true) => Hidden,
   };

   // In view gets the bracket; out of view gets an arrow at the edge pointing at it, so
   // a site can be located from a craft that cannot see it.
   if (inView) DrawBracket(draw, at, colour, blocked);
   else DrawEdgeArrow(draw, at, colour);

   float dx = cursor.X - at.X;
   float dy = cursor.Y - at.Y;
   if (dx * dx + dy * dy <= HoverRadius * HoverRadius) KeepUpFor(craft, ReachSeconds);

   ```

4. **Make labels clickable** as separate small windows (`AlwaysAutoResize | NoMove | NoDecoration ...`) placed beside the bracket and clamped on screen. Collect them while the overlay window is open and submit them after its `End()`, since windows do not nest; hovering the label extends its timer so the pointer can reach the button.

5. **Lock cue math** is game-free and tested: a phase (None, Acquiring, Locked, Held, ClearToFire), acquisition = dwell / lock time clamped, a standoff that closes linearly from 1.6 to 1.0, and a reticle built from centre, size and standoff as line strokes.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/Markers.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Markers.cs) | System markers: window, brackets, edge arrows, labels |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4559-L4595) | Projection with edge fallback |
| [src/KSArmory/Sim/EdgeCue.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/EdgeCue.cs#L18-L36) | Bearing-to-edge math |

## Variations and gotchas

- **Do not use a full-screen window without `NoInputs`:** it would swallow every click in the game.
- **The overlays are UI:** they vanish with F2 and in screenshots, unlike gizmo-based world overlays, which the mod uses for diagnostics ([Draw world-space lines and gizmos with the game's own renderers](../draw-world-space-lines-and-gizmos/overview.md)).
- **Ego is per camera:** a position sampled against the main camera is wrong for another viewport's camera; re-base through ecliptic.
- **Static label state holds `Vehicle` references;** call a `Forget()` on save load so destroyed craft are not kept alive.
- **Z-order:** the mod submits its panel first and overlays after, relying on this to keep overlays above the scene and below the panel; ImGui's own z-ordering was not verified.

## See also

- [Overview](overview.md)
- [Approach 2: measurement lines, rings and plated labels](draw-markers-and-annotations-on-world-objects-2.md)
- [World-anchored screen markers on full-screen ImGui overlay windows](../../../../mods/ksarmory/features/world-anchored-screen-markers.md)
- [Project world and sky positions to screen pixels and point at off-screen targets](../project-world-positions-to-screen/overview.md), [Draw full-screen click-through overlays and choose the right ImGui draw list](../../../ui/overlays/draw-a-fullscreen-click-through-overlay/overview.md)
