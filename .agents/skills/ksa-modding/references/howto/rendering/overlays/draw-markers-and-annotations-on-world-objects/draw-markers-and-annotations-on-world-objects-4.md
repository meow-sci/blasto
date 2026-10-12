---
type: How-To
title: Previous-frame window rectangle plus edge-clamped projection (body target)
description: Draw a pointer line from a mod window to a selected body, anchored to the window rectangle captured the previous frame and clamped to the screen edge when the body is off screen
category: rendering
group: overlays
topic: draw-markers-and-annotations-on-world-objects
approach: 4
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/compendium/features/body-pointer-overlay]
techniques: [imgui-drawlist]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Camera, KSA.IViewport, KSA.ImGuiHelper, KSA.Vehicle, KSA.Program]
tags: [pointer, overlay, window-rect, edge-clamp, imgui-drawlist]
---

Outcome: a thin line runs from your mod's window to the body currently shown in it, wherever that body is on screen; when the body is behind the camera or outside the view the line points to the screen edge in its direction. Options: one or two lines (from the nearest corners), a fixed colour or a rainbow.
Demonstrated by Compendium ([Pointer lines to the selected body](../../../../mods/compendium/features/body-pointer-overlay.md)). Prerequisites: StarMap. The clip-space clamp itself is explained in [project world positions to screen](../project-world-positions-to-screen/overview.md) (approach 4).

## Background: how the game does it

`Program.GetMainCamera()` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L631) gives the main viewport's camera and `camera.GetPositionEgo(target)` the camera-relative position. `Camera.IsPointWithinFov` [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L529) and `EgoToClip` [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L283) provide the on-screen test and the clip position.
ImGui gives the window rectangle with `GetWindowPos` / `GetWindowSize` only while that window is being drawn.

## Lifecycle and timing

Everything runs in `[StarMapAfterGui]` (`frame:draw-ui`), after `OnFrameViewports`, so the projection matches the image rendered this frame; body positions were refreshed at the frame's solver apply. The window rectangle is from the previous frame: `CaptureWindowRectForPointer` stores position and size and a visible flag while the window draws, and the
draw hook calls the pointer first thing, before the window itself, then clears the flag, so the pointer only draws if the window was drawn last frame. One frame of lag is invisible unless the window is being dragged. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Capture the window rectangle inside the window draw (`GetWindowPos`/`GetWindowSize`) and set a visible flag.

2. Resolve the target once per selection and cache it.

3. Project: `IsPointWithinFov` then `EgoToScreen(pos) + viewport.Pos`; otherwise the clip-space edge clamp:

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

4. Draw into a full-screen input-less overlay window (`##CompendiumPointerOverlay`) with `AddLine` from the nearest (and optionally second-nearest) window corner:

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

5. Save the options (`settings.ini`) on each change.

## Complete example

| File | Role |
|---|---|
| [src/CdmBodyPointer.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmBodyPointer.cs) | Pointer logic, options, rainbow |
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L57-L61) | Window rectangle capture hook |

## Variations and gotchas

- **A window, not the foreground list:** other windows can draw over the lines; `GetForegroundDrawList()` would put them above everything.
- **`EgoToScreen` returns NaN behind the camera,** which is why it is called only after the field-of-view test.
- **Main viewport only.**

## See also

- [Overview](overview.md)
- [Approach 5: same technique for a catalog star](draw-markers-and-annotations-on-world-objects-5.md)
- [Pointer lines to the selected body](../../../../mods/compendium/features/body-pointer-overlay.md)
- [Project world and sky positions to screen pixels and point at off-screen targets](../project-world-positions-to-screen/overview.md)
