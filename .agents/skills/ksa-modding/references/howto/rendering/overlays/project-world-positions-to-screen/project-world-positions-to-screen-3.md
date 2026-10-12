---
type: How-To
title: Behind-camera NaN culling with polyline breaking
description: Treat a NaN projection as a culling signal, skip points and labels behind the camera, and break sampled curves into pieces instead of drawing across the screen
category: rendering
group: overlays
topic: project-world-positions-to-screen
approach: 3
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksa-measuretools/features/overlay-drawlist-rendering]
techniques: [imgui-drawlist]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Camera, KSA.IViewport, KSA.Program]
tags: [projection, nan, culling, polyline, imgui-drawlist]
---

Outcome: lines, arcs, rings and labels that stay correct when part of them goes behind the camera: no wild segments stretching across the whole screen, no labels at mirrored positions. Demonstrated by KSA-MeasureTools'
overlay ([World-space measurement overlay on the ImGui background draw list](../../../../mods/ksa-measuretools/features/overlay-drawlist-rendering.md)). Prerequisites: StarMap; the base projection is approach 1.

## Background: how the game does it

`Camera.EclToScreen` and `EgoToScreen` return `(NaN, NaN)` for points behind the camera when `ignoreBehind` is true [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L339-L355). The stock overlays rely on this: the galactic grid in `Universe.DrawGalacticGrid` skips any piece with a
NaN endpoint [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1804-L1870). `ImGuiHelper.GetOverlayDrawList(viewport)` is the main viewport's background list for the main viewport [current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L43-L51).

## Lifecycle and timing

`[StarMapAfterGui]` (`frame:draw-ui`): `ImGui.NewFrame` has run, the camera for this frame was computed in `OnFrameViewports`, and the last applied physics results are what the renderer draws, so projected points match the scene. The draw list is consumed in the ImGui render pass
(`render:imgui`). With the HUD hidden nothing runs. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Define the culling rule once: a projected point is valid when neither coordinate is NaN or infinite.

   ```csharp
   // MeasureTools/Features/Measure/MeasureOverlay.cs L828-L831
   private static bool Valid(float2 s)
   {
       return !float.IsNaN(s.X) && !float.IsNaN(s.Y) && !float.IsInfinity(s.X) && !float.IsInfinity(s.Y);
   }
   ```

2. For single points (dots, labels), skip when invalid. For a measurement whose endpoint is behind the camera, skip the whole line.

3. For sampled curves (3D circles, great-circle arcs, a construction-plane disc), keep the previous valid point and break the polyline at invalid samples:

   ```csharp
   // MeasureTools/Features/Measure/MeasureOverlay.cs L197-L222
   private static void DrawWorldCircle(ImDrawListPtr dl, Camera camera, float2 vpPos, double3 centerEcl, double3 radiusVecEcl, double3 normalEcl, byte4 color, float thickness)
   {
       double3 u = radiusVecEcl;
       double3 w = double3.Cross(normalEcl.NormalizeOrZero(), u);
       if (w.LengthSquared() < 1e-12)
           return;
       const int segments = 48;
       float2 prev = default;
       bool hasPrev = false;
       for (int i = 0; i <= segments; i++)
       {
           double angle = Math.PI * 2.0 * i / segments;
           double3 p = centerEcl + u * Math.Cos(angle) + w * Math.Sin(angle);
           float2 s = vpPos + camera.EclToScreen(p);
           if (Valid(s))
           {
               if (hasPrev)
                   dl.AddLine(in prev, in s, color, thickness);
               prev = s;
               hasPrev = true;
           }
           else
           {
               hasPrev = false;
           }
       }
   ```

4. For surface arcs also drop samples on the far hemisphere of the planet (`dot(dir, camera.PositionEcl - pointEcl) > 0` in the sample loop).

5. Add a background plate to labels so they stay readable over orbit lines and planet discs:

   ```csharp
   // MeasureTools/Features/Measure/MeasureOverlay.cs L774-L791
   private static void Label(ImDrawListPtr dl, float2 pos, ImString text)
   {
       // Background plate so labels stay readable over orbit lines, planet discs
       // and other labels.
       float2 size = ImGui.CalcTextSize(text);
       var pMin = new float2(pos.X - 4f, pos.Y - 2f);
       var pMax = new float2(pos.X + size.X + 4f, pos.Y + size.Y + 2f);
       dl.AddRectFilled(in pMin, in pMax, LabelPlate, 3f);
       dl.AddText(in pos, LabelColor, text);
   }

   // Distance labels without prefix text: ToNearest writes into the stack
   // buffer and the span goes straight into the ImGui arena.
   private static void LabelDistance(ImDrawListPtr dl, float2 pos, double meters)
   {
       Span<char> buffer = stackalloc char[64];
       Label(dl, pos, DistanceReference.ToNearest(meters, buffer));
   }
   ```

6. Wrap the whole draw in a `try`/`catch` that logs each exception type once, so a projection problem can never unwind into the game's UI pass; gate on view mode (Map or Orbit) and on a loaded system.

## Complete example

| File | Role |
|---|---|
| [MeasureTools/Features/Measure/MeasureOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureOverlay.cs) | Draw routine, NaN culling, labels |

## Variations and gotchas

- **Break polylines at NaN samples;** joining across a behind-camera gap draws lines across the whole screen.
- **Add `viewport.Position`** to `EclToScreen` results before drawing into a window-wide list.
- **Labels are allocation-free** in this mod: numbers are formatted into `stackalloc char[64]` buffers and passed as `ImString`.
- **Expensive picks** (mesh raycasts) are throttled by wall-clock time (45 ms) plus an explicit invalidation counter, not by frame count.
- **Draw order on the shared background list** follows submission order; `AfterGui` runs before `Universe.OnDrawUi`, so stock outline boxes added later end up above the lines.
- `ImGuiHelper.GetOverlayDrawList` for a non-main viewport is the current window's list; calling it outside that window draws into whatever window is current.

## See also

- [Overview](overview.md)
- [Approach 1: real points through the viewport camera](project-world-positions-to-screen-1.md)
- [Approach 4: clip-space edge clamp](project-world-positions-to-screen-4.md)
- [World-space measurement overlay on the ImGui background draw list](../../../../mods/ksa-measuretools/features/overlay-drawlist-rendering.md)
- [Draw markers, brackets and measurement annotations on world objects](../draw-markers-and-annotations-on-world-objects/overview.md)
