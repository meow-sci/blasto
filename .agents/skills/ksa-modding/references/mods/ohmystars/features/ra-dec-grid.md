---
type: Mod Feature
title: RA/Dec grid
description: Draw a celestial right ascension and declination grid as 1-degree arcs with a clip-space near-frustum cull and overlap-free chart labels on the same star sphere as the constellations
mod: ohmystars
category: rendering
techniques: [imgui-drawlist, sky-projection, label-collision-avoidance]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Camera, Brutal.ImGuiApi.ImGui]
tags: [sky, grid, right-ascension, declination, imgui, labels, culling]
---

An optional celestial coordinate grid: declination parallels every 1, 5 or 10 deg, right
ascension meridians every hour (plus optional 10-minute bands), and chart-style labels ("RA"/"0h",
"Dec"/"+30°") that avoid overprinting. It lives on the same camera-centred star sphere as the
constellation lines and goes through the same RA/Dec to ecliptic conversion, so it sits at the
J2000 equatorial tilt against the game's ecliptic-aligned axes.[^oms-skymarkingsrenderer-cs]

## How it works

1. **Build once per setting change.** `Draw` rebuilds only when the declination spacing or the
   minute-band toggle differs from what was last built ([src/SkyMarkingsRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyMarkingsRenderer.cs#L37-L72)).
2. **Everything is 1 deg arcs.** `Rebuild` emits each declination parallel as 360 segments of
   `SegmentStepDegrees = 1`, and `AddMeridian` emits each meridian from -90 to +90 deg as 180
   segments, all through `StarDirectionConverter.RaDecToDirection`
   ([src/SkyMarkingsRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyMarkingsRenderer.cs#L146-L217), [src/Math.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/Math.cs#L148-L168)).[^oms-math-cs] Parallels are small circles, so on
   a gnomonic (perspective) projection they really are curves and must be subdivided; meridians are
   great circles and would project straight, but are split anyway so that occlusion and culling
   work per degree.
3. **Labels.** RA hour labels sit on the meridians at dec -75, -45, 0, +45, +75 deg, with an "RA"
   prefix line on the equator; minute-band labels ("10m".."50m") only on the equator; Dec labels
   every 45 deg of RA on every labelled parallel (every fifth parallel at 1 deg spacing) with a
   "Dec" prefix on RA 0.
4. **Clip-space pre-cull with a margin.** `IsNearFov` transforms a point with
   `camera.EgoToClip`, rejects `W <= 0` (behind the eye), and accepts `|x|, |y| <= W * 1.15`, which
   is the view frustum inflated by 15% so lines do not pop in at the screen edge while panning
   ([src/SkyMarkingsRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyMarkingsRenderer.cs#L219-L228),
   [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L283-L286)).[^ksa-camera-cs] A segment survives if any of
   start, midpoint or end is near the view, then must pass the body occlusion test on all three.
5. **Label collision rejection.** `DrawLabels` centres each label on its anchor with
   `ImGui.CalcTextSize`, keeps a per-frame list of drawn rectangles and skips any label that would
   overlap one already drawn ([src/SkyMarkingsRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyMarkingsRenderer.cs#L78-L140)). RA labels are added to
   the list first, so they win ties at line crossings and near the poles.
6. **Style.** Fixed colour `(0.75, 0.85, 1.0)` at 0.35 alpha for lines and 0.8 for labels; line
   thickness comes from the "Grid Line Thickness" slider (0.5-5 px).

| Spacing | Parallels x 360 | Meridians x 180 | Segments per frame (before culling) |
|---|---|---|---|
| 10 deg | 17 x 360 = 6,120 | 24 x 180 = 4,320 | 10,440 |
| 1 deg | 179 x 360 = 64,440 | 24 x 180 = 4,320 | 68,760 |
| 1 deg + minute bands | 64,440 | 144 x 180 = 25,920 | 90,360 |

## Lifecycle and timing

`SkyMarkingsRenderer.Draw` is the first layer drawn by `StellariumRenderer.Draw` from
`[StarMapAfterGui]` (`frame:draw-ui`), after the occluder list has been rebuilt for the frame
([src/StellariumRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/StellariumRenderer.cs#L97-L104)). The rebuild happens lazily on the main thread inside
the draw call, never on another thread. Like the other overlays it disappears when the HUD is
hidden.

## Key code

| Path | Role |
|---|---|
| [src/SkyMarkingsRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyMarkingsRenderer.cs#L37-L72) | Lazy rebuild, cull, occlude, draw |
| [src/SkyMarkingsRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyMarkingsRenderer.cs#L146-L217) | Parallels, meridians, label anchors |
| [src/SkyMarkingsRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyMarkingsRenderer.cs#L78-L140) | Centred labels with overlap rejection |
| [src/SkyMarkingsRenderer.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/SkyMarkingsRenderer.cs#L219-L228) | Clip-space near-frustum test |

## Snippets

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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Camera.EgoToClip(double3)` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L283-L286) | Clip-space position for the margin test |
| `Camera.IsPointWithinFov(double3)` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L529-L545) | Label visibility |
| `Camera.EgoToScreen(double3, bool)` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L347-L355) | Line and label positions |
| `ImGui.CalcTextSize` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8092) | Label centring and overlap boxes |

## Pitfalls and version notes

- **J2000 mean equator, not a planet's equator.** The grid is not tied to Earth's in-game spin
  axis, which `Astronomicals.xml` defines separately in the ecliptic frame with `Tilt` 23.441522
  deg and `Azimuth` -0.363633 deg ([current/Content/Core/Astronomicals.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Astronomicals.xml#L532-L537)).[^ksa-content-core-astronomicals-xml]
  The repo's `.vscode/settings.json` contains an auto-approved scratch program that compares
  exactly these two poles ([.vscode/settings.json](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/.vscode/settings.json)), so expect a small offset between the
  grid's celestial pole and Earth's rotation pole.
- **Cost at fine spacing.** At 1 deg with minute bands the CPU projects up to about 90,000
  segments per frame, each with three clip transforms and up to three occlusion tests against
  every body. The 10 deg default is about 10,000.
- **Settings validation.** `LoadSettings` accepts only 1 or 5 for `DecSpacingDegrees` and falls
  back to 10 for anything else ([src/OhMyStarsWindow.cs](https://github.com/nanonestor/OhMyStars/blob/4bb602e3bfe36bcd3670b71b88bf644fe95fa407/src/OhMyStarsWindow.cs#L156-L157)).
- **Overlap rejection is first-come.** The list order, not importance, decides which label
  survives.

## See also

- [Constellation lines and labels on the sky](sky-overlay-lines-and-labels.md)
- [Occlusion by bodies](overlay-occlusion-by-bodies.md)
- [settings.ini persistence](ini-settings-persistence.md)
- [One-degree arcs, clip-space frustum cull and CalcTextSize label rectangles](../../../howto/rendering/overlays/draw-lines-on-the-sky/draw-lines-on-the-sky-2.md) - how-to article demonstrating this feature

[^oms-skymarkingsrenderer-cs]: OhMyStars src/SkyMarkingsRenderer.cs
[^oms-math-cs]: OhMyStars src/Math.cs
[^ksa-camera-cs]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-content-core-astronomicals-xml]: Content/Core/Astronomicals.xml (game content, build 2026.10.10.5554)
