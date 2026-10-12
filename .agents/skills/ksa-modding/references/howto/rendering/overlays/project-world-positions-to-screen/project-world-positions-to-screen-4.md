---
type: How-To
title: Clip-space edge clamp for off-screen and behind-camera targets
description: Point at a target that is not on screen by testing IsPointWithinFov, then dividing the clip position by w, flipping behind the camera and scaling to the viewport rectangle
category: rendering
group: overlays
topic: project-world-positions-to-screen
approach: 4
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/compendium/features/body-pointer-overlay, mods/ohmystars/features/star-pointer]
techniques: [imgui-drawlist]
hooks: [starmap StarMapAfterGui, starmap StarMapBeforeGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Camera, KSA.Program, Brutal.ImGuiApi.ImGuiViewportPtr]
tags: [projection, clip-space, edge-clamp, pointer, off-screen, imgui-drawlist]
---

Outcome: a marker or a pointer line that keeps working when its target is off screen or even behind the camera: it ends on the screen edge in the target's direction. This is the reusable core of "a pointer to anything not currently visible".
Demonstrated by Compendium's body pointer ([Pointer lines to the selected body](../../../../mods/compendium/features/body-pointer-overlay.md)) and Oh My Stars' star pointer ([Star pointer](../../../../mods/ohmystars/features/star-pointer.md)). Prerequisites: StarMap; base projection in approach 1.

## Background: how the game does it

`Camera.IsPointWithinFov(double3 positionEgo)` [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L529-L545) tests a camera-relative point against the frustum planes (the near-plane normal first, then the four side planes). `Camera.EgoToClip(double3)` [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L283) returns the homogeneous clip position
(`float4`); dividing by `W` gives NDC. For a point behind the camera `W` is negative and the perspective divide mirrors the direction, which is why the sign must be flipped. `EgoToScreen` itself returns NaN for such points.

## Lifecycle and timing

`[StarMapAfterGui]` (`frame:draw-ui`). Camera matrices are final, so the projection matches the rendered image. A target pointer is a UI element: it vanishes with the HUD. Window geometry that the pointer anchors to is captured while the window draws and consumed by the overlay
(Compendium: previous frame, invisible unless the window is being dragged; Oh My Stars: recorded in `[StarMapBeforeGui]` in the same frame, so no lag). See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Resolve the target in ego space: a body or craft with `camera.GetPositionEgo(target)`; a catalog star with `direction * 19.5` (approach 2).

2. If `IsPointWithinFov`, project normally. Otherwise take the clip position, divide x and y by `w`, flip both when `w < 0`, handle the degenerate "exactly behind" case, scale so the larger component is 1, and map that point on the NDC square to the viewport rectangle:

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

3. Compendium does the same for a body target and then draws one or two lines from the nearest window corners; the on-screen branch uses `camera.EgoToScreen(pos) + viewport.Pos`:

   ```csharp
   // src/CdmBodyPointer.cs L169-L207
   // On-screen position of the target, or the point on the screen edge in its direction when off-view.
   private static bool TryGetPointerScreenTarget(Camera camera, ImGuiViewportPtr viewport, double3 positionEgo, out float2 target)
   {
       if (camera.IsPointWithinFov(positionEgo))
       {
           target = camera.EgoToScreen(positionEgo) + viewport.Pos;
           return true;
       }

       float4 clip = camera.EgoToClip(positionEgo);
       if (MathF.Abs(clip.W) < float.Epsilon)
       {
           target = default;
           return false;
       }

       float x = clip.X / clip.W;
       float y = clip.Y / clip.W;
       if (clip.W < 0f)
       {
           x = -x;
           y = -y;
       }
       if (MathF.Abs(x) < float.Epsilon && MathF.Abs(y) < float.Epsilon)
       {
           y = -1f;
       }

       float maximum = MathF.Max(MathF.Abs(x), MathF.Abs(y));
       if (maximum < float.Epsilon)
       {
           target = default;
           return false;
       }

       float2 center = viewport.Pos + viewport.Size * 0.5f;
       target = center + new float2(x / maximum * viewport.Size.X * 0.5f, y / maximum * viewport.Size.Y * 0.5f);
       return true;
   }
   ```

4. Draw into a full-screen, input-less window's draw list (`NoTitleBar | NoResize | NoMove | NoCollapse | NoBringToFrontOnFocus | NoBackground | NoFocusOnAppearing | NoInputs | NoNavFocus | NoSavedSettings`); pick the nearest (and optionally second-nearest) corner of your window by squared distance as the line's start.

   ```csharp
   // src/CdmBodyPointer.cs L134-L166
   Astronomical? target = GetPointerTarget();
   Camera camera = Program.GetMainCamera();
   ImGuiViewportPtr viewport = ImGui.GetMainViewport();
   if (target == null || camera == null || viewport.IsNull())
   {
       return;
   }

   if (!TryGetPointerScreenTarget(camera, viewport, camera.GetPositionEgo(target), out float2 targetScreen))
   {
       return;
   }

   ImGui.SetNextWindowPos(viewport.Pos);
   ImGui.SetNextWindowSize(viewport.Size);
   ImGui.SetNextWindowViewport(viewport.ID);
   ImGuiWindowFlags flags =
       ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
       ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoBackground |
       ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoNavFocus |
       ImGuiWindowFlags.NoSavedSettings;
   if (ImGui.Begin("##CompendiumPointerOverlay", flags))
   {
       ImDrawListPtr drawList = ImGui.GetWindowDrawList();
       ImColor8 lineColor = ToLineColor(pointerRainbow ? GetRainbowPointerColor() : pointerColor);
       GetTwoNearestWindowCorners(pointerWindowPosition, pointerWindowSize, targetScreen, out float2 nearest, out float2 secondNearest);
       drawList.AddLine(nearest, targetScreen, lineColor, 1.5f);
       if (pointerDualLines)
       {
           drawList.AddLine(secondNearest, targetScreen, lineColor, 1.5f);
       }
   }
   ImGui.End();
   ```

5. Optional: a rainbow mode from `ImGui.GetTime()` divided by a cycle length, converted from hue with a six-sector ramp.

## Sub-step: clamp a marker or pointer line to the screen edge

The clamp is the whole trick: after the divide the pair `(x, y)` points from the screen centre toward the target. Scaling it by `1 / max(|x|, |y|)` puts it on the edge of the NDC square, and multiplying by half the viewport size around the centre gives
a pixel position on the viewport rectangle. For a target in front of the camera this lies exactly on the projected direction; for a target behind it is a "turn this way" hint.

## Complete example

| File | Role |
|---|---|
| [src/CdmBodyPointer.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmBodyPointer.cs) | Body pointer with window-corner anchors, rainbow, settings |
| [src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L150-L254) | Star pointer: target, clamp, corners, rainbow |

## Variations and gotchas

- **Window, not the foreground list.** Because the lines live in a normal ImGui window, other windows can draw over them; `GetForegroundDrawList()` would put them above everything.
- **`EgoToScreen` returns NaN behind the camera,** which is why the code calls it only after the field-of-view test.
- **Main viewport only;** the projection uses the main camera.
- **Occlusion is ignored on purpose:** a pointer should show through a planet.
- **No window, no pointer** when the anchoring window is hidden.
- For a more robust cue near the camera plane, derive the bearing from the camera basis instead (approach 5).

## See also

- [Overview](overview.md)
- [Approach 5: edge cue from the camera basis](project-world-positions-to-screen-5.md)
- [Approach 3: NaN culling](project-world-positions-to-screen-3.md)
- [Pointer lines to the selected body](../../../../mods/compendium/features/body-pointer-overlay.md), [Star pointer](../../../../mods/ohmystars/features/star-pointer.md)
- [Draw markers, brackets and measurement annotations on world objects](../draw-markers-and-annotations-on-world-objects/overview.md)
