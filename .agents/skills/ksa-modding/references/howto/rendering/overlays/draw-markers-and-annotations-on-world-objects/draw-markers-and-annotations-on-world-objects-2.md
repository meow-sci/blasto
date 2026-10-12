---
type: How-To
title: Lines, curves, rings and plated labels for measurements
description: Draw measurement lines, 3D construction rings, great-circle arcs and plated labels as projected polylines on the background draw list, throttling expensive picks
category: rendering
group: overlays
topic: draw-markers-and-annotations-on-world-objects
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by:
  - mods/ksa-measuretools/features/overlay-drawlist-rendering
  - mods/ksa-measuretools/features/circle-and-face-angle-tools
techniques: [imgui-drawlist]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnMouseButton]
lifecycle: [frame:draw-ui, frame:input]
game_types: [KSA.Camera, KSA.IViewport, KSA.ImGuiHelper, KSA.Vehicle, KSA.Program]
tags: [measurement, overlay, polyline, labels, imgui-drawlist, map-mode]
---

Outcome: measurement annotations on a craft or in the map: a distance line with a plated label, a protractor arc, a fitted circle on a tank rim, a surface-normal arrow of constant screen length. All drawn as 2D ImGui primitives positioned by projecting 3D points every frame; the mod renders nothing through Vulkan.
Demonstrated by KSA-MeasureTools ([World-space measurement overlay on the ImGui background draw list](../../../../mods/ksa-measuretools/features/overlay-drawlist-rendering.md), [Circle and face-angle tools on part geometry](../../../../mods/ksa-measuretools/features/circle-and-face-angle-tools.md)). Prerequisites: StarMap; NaN culling in approach 3 of [project world positions to screen](../project-world-positions-to-screen/overview.md).

## Background: how the game does it

`ImGuiHelper.GetOverlayDrawList(viewport)` [current/decomp/KSA/ImGuiHelper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiHelper.cs#L43-L51) returns `ImGui.GetBackgroundDrawList(ImGui.GetMainViewport())` for the main viewport (the list the stock body labels and orbit overlays use), and the current window's list otherwise. Background-list content sits under every ImGui window, so the overlay never covers the tool window or stock panels.
`Camera.GetObjectDiameterPixels(diameter, distance)` gives pixels per metre at a depth, used to keep arrows a constant screen size.

## Lifecycle and timing

Picks happen on the placement click (`Program.OnMouseButton` prefix, `frame:input`); preview and drawing happen in `[StarMapAfterGui]` (`frame:draw-ui`). By then `ImGui.NewFrame` has run, the cursor holds this frame's position, the frame's camera was computed in `OnFrameViewports`, and the last applied physics results are what the renderer draws.
`MainViewport.Position` is last frame's at this hook (it is set at the start of `OnDrawUiThreadSafe`). The draw list is consumed in `render:imgui`. With the HUD hidden the overlay disappears; a mod that must keep drawing would hook `OnDrawUiConsole`. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Pick the list.** The main viewport's background list; restrict the tool to the main viewport, found by `IsMain()` over `ViewportRegistry.GameViews`. Draw only while the window is open, the view is Map or Orbit, and a system is loaded; wrap the whole draw in a `try`/`catch` that logs each exception type once.

2. **Project every point** with `vpPos + camera.EclToScreen(posEcl)`, treat NaN or infinity as culled, and break sampled curves at invalid samples:

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

3. **Constant-size arrows:** a normal arrow whose world length is `40 / pixelsPerMetre`, so it reads about 40 px at any zoom:

   ```csharp
   // MeasureTools/Features/Measure/MeasureOverlay.cs L227-L244
   private static void DrawNormalArrow(ImDrawListPtr dl, Camera camera, float2 vpPos, double3 originEcl, double3? normalEcl, byte4 color, float thickness)
   {
       if (normalEcl == null)
           return;
       double distance = (originEcl - camera.PositionEcl).Length();
       if (!(distance > 0.0))
           return;
       double pxPerMeter = camera.GetObjectDiameterPixels(1.0, distance);
       if (!(pxPerMeter > 1e-9))
           return;
       double3 tipEcl = originEcl + normalEcl.Value * (40.0 / pxPerMeter);
       float2 a = vpPos + camera.EclToScreen(originEcl);
       float2 b = vpPos + camera.EclToScreen(tipEcl);
       if (!Valid(a) || !Valid(b))
           return;
       dl.AddLine(in a, in b, color, thickness);
       dl.AddCircleFilled(in b, 3f, color);
   }
   ```

4. **Plated labels** with a dark rounded rectangle behind the text, positioned at the segment midpoint offset 16 px along the perpendicular that points up the screen; stack labels at one anchor by `GetTextLineHeightWithSpacing() + 6`:

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

5. **Format without allocating:** numbers go into `stackalloc char[64]` buffers and are passed as `ImString`.

6. **Throttle the hover preview** by wall-clock time (about 45 ms) plus an explicit state-version counter and modifier-key check, never by frame count; the placement click always picks fresh.

   ```csharp
   // MeasureTools/Features/Measure/MeasureOverlay.cs L403-L436
   float2 mouseViewport = Cursor.GetPosition(viewport);
   // A cursor outside the viewport cannot place a point, and a cursor that
   // left the OS window reports far outside it; either way picking over
   // that position is wasted work.
   if (mouseViewport.X < 0f || mouseViewport.Y < 0f
       || mouseViewport.X > viewport.Size.X || mouseViewport.Y > viewport.Size.Y)
   {
       Reset();
       return;
   }
   // Ctrl previews (and places) a free point on the ecliptic plane even where
   // snapping would win; a modifier change re-picks immediately so the preview
   // flips with the key.
   bool eclipticFree = ImGui.GetIO().KeyCtrl;
   long now = Stopwatch.GetTimestamp();
   if (Stopwatch.GetElapsedTime(_previewLastPickTimestamp, now).TotalMilliseconds >= PreviewPickIntervalMs
       || _previewStateVersion != MeasureState.StateVersion
       || _previewEclipticFree != eclipticFree)
   {
       if (MeasureState.Mode == MeasureMode.Circle)
       {
           MapPicker.PickCircle(viewport, mouseViewport, out Anchor? circleCenter, out Anchor? circleRim, reuseStockHover: true);
           _previewCache = circleRim;
           _previewCacheSecondary = circleCenter;
       }
       else
       {
           _previewCache = MapPicker.Pick(viewport, mouseViewport, eclipticFree, reuseStockHover: true);
           _previewCacheSecondary = null;
       }
       _previewLastPickTimestamp = now;
       _previewStateVersion = MeasureState.StateVersion;
       _previewEclipticFree = eclipticFree;
   }
   ```

7. **Circle tool:** the fitted circle is drawn in 3D from the resolved centre, the radius vector (rim minus centre) and the plane normal by building a second in-plane axis with a cross product and sampling 48 points, breaking the polyline wherever a sample is behind the camera.

## Complete example

| File | Role |
|---|---|
| [MeasureTools/Features/Measure/MeasureOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureOverlay.cs) | All drawing |
| [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L400-L432) | Circle pick |

## Variations and gotchas

- `EclToScreen` is viewport-local: add `viewport.Position`.
- **Shared background list:** draw order follows submission order, and `AfterGui` runs before `Universe.OnDrawUi`, so stock outline boxes added later end up above the lines.
- **`GetOverlayDrawList` for a non-main viewport** is the current window's list; calling it outside that window draws into whatever window is current.
- Face-angle normals are vertex normals of the hit triangle, not exact face normals on curved surfaces.

## See also

- [Overview](overview.md)
- [Approach 1: constant-pixel-size brackets](draw-markers-and-annotations-on-world-objects-1.md)
- [World-space measurement overlay on the ImGui background draw list](../../../../mods/ksa-measuretools/features/overlay-drawlist-rendering.md), [Circle and face-angle tools on part geometry](../../../../mods/ksa-measuretools/features/circle-and-face-angle-tools.md)
- [Project world and sky positions to screen pixels and point at off-screen targets](../project-world-positions-to-screen/overview.md)
