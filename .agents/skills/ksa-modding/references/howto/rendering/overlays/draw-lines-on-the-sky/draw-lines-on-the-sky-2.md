---
type: How-To
title: One-degree arcs, clip-space frustum cull and CalcTextSize label rectangles
description: Draw a labelled RA/Dec coordinate grid from lazily built one-degree arcs, a 15 percent inflated clip-space cull and per-frame label rectangle rejection
category: rendering
group: overlays
topic: draw-lines-on-the-sky
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ohmystars/features/ra-dec-grid]
techniques: [imgui-drawlist]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Camera, KSA.Universe, KSA.GalacticPlane, Brutal.ImGuiApi.ImGui]
tags: [sky, grid, ra-dec, labels, culling, declutter, imgui-drawlist]
---

Outcome: a chart-style labelled coordinate grid on the sky (declination parallels, right-ascension meridians, hour and minute bands) that stays cheap and legible: lazily built arcs, a clip-space cull with a margin, per-segment occlusion and a per-frame list of drawn label rectangles so labels never overprint.
The label declutter is reusable for any labelled screen overlay. Demonstrated by Oh My Stars' RA/Dec grid ([RA/Dec grid](../../../../mods/ohmystars/features/ra-dec-grid.md)). Prerequisites: StarMap; read approach 1 first.

What you should see: pale blue-white parallels and hour meridians (35 percent opacity) tilted about
23.4 degrees against the game's ecliptic-aligned sky, `0h` to `23h` labels on the equator with
`RA` above them, and `+n°` labels along the labelled parallels with `Dec` above them on the 0h
meridian. In Oh My Stars the grid is off until enabled in its window (`showRaDecGrid` defaults to
false).

## Background: how the game does it

The grid lives on the same camera-centred star sphere as the constellation lines and uses the same RA/Dec to ecliptic conversion, so it sits at the J2000 equatorial tilt against the game's ecliptic-aligned axes. The stock galactic grid draws parallels in 2 degree pieces and skips NaN endpoints
[current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1853-L1869). `Camera.EgoToClip(double3)` [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L283) returns the homogeneous clip position, so a frustum test is a comparison against `W`. `ImGui.CalcTextSize` measures labels.

## Lifecycle and timing

`SkyMarkingsRenderer.Draw` is the first layer drawn from `[StarMapAfterGui]` (`frame:draw-ui`), after the occluder list is rebuilt for the frame. The lazy rebuild happens on the main thread inside the draw call. Like the other overlays the grid disappears with the HUD. See
[the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Build once per setting change.** Rebuild only when the declination spacing or the minute-band toggle differs from what was last built. Emit each declination parallel as 360 one-degree segments and each meridian from -90 to +90 degrees as 180 segments, all through the RA/Dec conversion. Parallels are small circles, so on a
   gnomonic projection they really are curves and must be subdivided; meridians are great circles but are split anyway so that occlusion and culling work per degree.

   ```csharp
   // src/SkyMarkingsRenderer.cs L146-L170
   private static void Rebuild(int decSpacingDegrees, bool showRaMinuteBands) {
       segments.Clear();
       labels.Clear();
       builtDecSpacingDegrees = decSpacingDegrees;
       builtShowRaMinuteBands = showRaMinuteBands;

       for(double dec = -90.0 + decSpacingDegrees; dec <= 90.0 - decSpacingDegrees + 1e-9; dec += decSpacingDegrees) {
           for(double ra = 0.0; ra < 360.0; ra += SegmentStepDegrees) {
               segments.Add(new Segment(
                   StarDirectionConverter.RaDecToDirection(ra / 15.0, dec),
                   StarDirectionConverter.RaDecToDirection((ra + SegmentStepDegrees) / 15.0, dec)));
           }
       }

       // RA meridians: one per hour; with minute bands on, also at 10/20/30/40/50 minutes past
       // every hour.
       const double hourStepDegrees = 15.0;
       for(double ra = 0.0; ra < 360.0; ra += hourStepDegrees) {
           AddMeridian(ra);
           if(showRaMinuteBands) {
               foreach(double minuteOffsetDegrees in new[] { 2.5, 5.0, 7.5, 10.0, 12.5 }) {
                   AddMeridian(ra + minuteOffsetDegrees);
               }
           }
       }
   ```

2. **Pre-cull in clip space with a margin.** Reject `W <= 0` (behind the eye) and accept `|x|, |y| <= W * 1.15`, the frustum inflated by 15 percent so lines do not pop in at the screen edge while panning. A segment survives if any of start, midpoint or end is near the view, then must pass the body occlusion test on all three.

   ```csharp
   // src/SkyMarkingsRenderer.cs L219-L228
   // Cheap clip-space test: in front of the camera and inside the view frustum inflated by
   // FovMargin, so off-screen segments are rejected before the costlier occluder checks.
   private static bool IsNearFov(Camera camera, double3 position) {
       float4 clip = camera.EgoToClip(position);
       if(clip.W <= 0f)
           return false;

       float limit = clip.W * (1f + FovMargin);
       return MathF.Abs(clip.X) <= limit && MathF.Abs(clip.Y) <= limit;
   }
   ```

3. **Labels:** RA hour labels sit on the meridians at declinations -75, -45, 0, +45, +75 with an "RA" prefix line on the equator; minute-band labels only on the equator; Dec labels every 45 degrees of RA on labelled parallels.

4. **Declutter:** centre each label on its projected anchor with `ImGui.CalcTextSize`, keep a per-frame list of drawn rectangles and skip any label that overlaps one already drawn. Add the more important labels to the list first so they win ties.

   ```csharp
   // src/SkyMarkingsRenderer.cs L78-L92, L116-L140
   private static void DrawLabels(ImDrawListPtr draw_list, Camera camera, double3 center, double radius) {
       ImColor8 labelColor = StellariumRenderer.ToLineColor(gridLineColor, gridLabelOpacity);
       drawnLabelRects.Clear();

       foreach(GridLabel label in labels) {
           double3 position = center + StellariumRenderer.ApplyAlignment(label.Direction) * radius;

           if(!camera.IsPointWithinFov(position))
               continue;
           if(!StellariumRenderer.IsVisibleFromCamera(position))
               continue;

           float2 anchor = StellariumRenderer.EgoToOverlayScreen(camera, position);

           if(label.Prefix != null) {
   // ...
           // near the poles - only the first is drawn. RA labels come first in the list, so an
           // RA number always survives a tie.
           if(OverlapsDrawnLabel(screen, size))
               continue;

           drawnLabelRects.Add(new float4(screen.X, screen.Y, screen.X + size.X, screen.Y + size.Y));

           ImDrawListExtensions.AddText(
               draw_list,
               screen,
               labelColor,
               label.Text);
       }
   }

   private static bool OverlapsDrawnLabel(float2 screen, float2 size) {
       foreach(float4 rect in drawnLabelRects) {
           if(screen.X < rect.Z && screen.X + size.X > rect.X &&
              screen.Y < rect.W && screen.Y + size.Y > rect.Y) {
               return true;
           }
       }

       return false;
   }
   ```

## Complete example

| File | Role |
|---|---|
| [src/SkyMarkingsRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyMarkingsRenderer.cs) | Lazy build, culling, labels, overlap rejection |

| Spacing | Parallels x 360 | Meridians x 180 | Segments per frame before culling |
|---|---|---|---|
| 10 deg | 6,120 | 4,320 | 10,440 |
| 1 deg | 64,440 | 4,320 | 68,760 |
| 1 deg + minute bands | 64,440 | 25,920 | 90,360 |

## Variations and gotchas

- **J2000 mean equator, not a planet's equator:** a small offset against Earth's in-game rotation pole is expected.
- **Cost at fine spacing:** each segment costs three clip transforms and up to three occlusion tests against every body; the 10 degree default is about 10,000 segments.
- **Overlap rejection is first-come:** list order, not importance, decides which label survives.
- **Settings validation:** only 1 or 5 are accepted for the fine declination spacing; anything else falls back to 10.
- **Behind-camera points:** the clip test rejects `W <= 0`, so a segment is only drawn when one of
  its three points is near the view; with 1 degree segments the far endpoint cannot be behind the
  camera plane at normal fields of view, which is why this layer gets away without a NaN check.
- **Same hook, same limits as approach 1:** nothing draws with the HUD hidden or in secondary
  camera windows.

## See also

- [Overview](overview.md)
- [Approach 1: window draw list with ego-space sky directions](draw-lines-on-the-sky-1.md)
- [RA/Dec grid](../../../../mods/ohmystars/features/ra-dec-grid.md)
- [Project world and sky positions to screen pixels and point at off-screen targets](../project-world-positions-to-screen/overview.md)
