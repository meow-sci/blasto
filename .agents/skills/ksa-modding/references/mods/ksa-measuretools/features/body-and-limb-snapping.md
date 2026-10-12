---
type: Mod Feature
title: Snap to bodies and to the edge of their projected disc in the map view
description: One scan projects every body's center and disc radius, snaps inside a disc to the smallest body, snaps the disc-edge band to the limb point toward the cursor, and gates dot-sized bodies on the game's hover-eligibility flag
mod: ksa-measuretools
category: flight-and-orbits
techniques: [screen-space-picking]
hooks: [harmony-prefix KSA.Program.OnMouseButton, starmap StarMapAfterGui]
lifecycle: [frame:input, frame:draw-ui]
game_types:
  - KSA.Astronomical
  - KSA.Camera
  - KSA.CelestialSystem
  - KSA.IOrbiter
  - KSA.StellarBody
  - KSA.Vehicle
  - KSA.Program
tags: [map-view, snapping, celestials, picking, screen-space, limb]
---

In the map view a click near a planet snaps either to its center or, when the cursor sits on the
edge of its disc, to the point on its surface sphere in that direction ("Earth surface"). A dot-sized
moon or a star snaps at its center. Free placement still works in dense systems because bodies that
the game itself does not mark on screen are not snap targets.[^mt-readme]

## How it works

`MapPicker.PickBody` walks `Universe.CurrentSystem.All` once and computes, for each body:[^mt-mappicker]

- `s = camera.EclToScreen(body.GetPositionEcl())` and the cursor distance `d` (NaN, meaning behind
  the camera, skips the body).
- **Tier 2, center snap:** the nearest projected center within 14 px wins, but only if the body is a
  `StellarBody` or has `Astronomical.CursorHoverEligible` set. Stock sets that flag in
  `IOrbiter.OnDrawUi`, inside the `GameSettings.ShowCelestialNames()` branch, under the same
  predicate that draws the name label and outline box, and only for the viewport that is
  `CursorTarget`'s hit-test viewport, so the snap set equals the set of bodies the player can see
  marked in the hovered view.[^ksa-iorbiter] Every `StellarBody` is admitted unconditionally: a
  `FixedStar` is not an `IOrbiter` and is never flagged, while an `OrbitingStar` is.
- **Tier 1, disc snap:** skipped for vehicles (their `MeanRadius` is a bounding-sphere radius, not a
  surface). The projected radius is `camera.GetObjectDiameterPixels(MeanRadius * 2, distance) * 0.5`;
  bodies under 16 px are treated as dots. The cursor must be within the disc plus a 10 px band.
  Among overlapping discs the smallest projected disc wins (a moon in front of its planet).

If a disc won and the cursor is in the outer 10 px band, `SnapToLimb` computes the limb point;
otherwise the anchor is the body center (`Anchor.AtBody`). If no disc won, the tier-2 center snap
applies.

`SnapToLimb` takes the cursor ray, finds its closest point to the body center
(`t = dot(center - origin, direction)`), and pushes that point radially out to `MeanRadius`. At map
distances that is the visible limb toward the cursor; the exact tangent circle is tilted toward the
camera by roughly radius over distance, which the author accepts as negligible. The result is
`Anchor.AtSurface(body, dir * MeanRadius)`, an offset in the body's CCE frame, so it keeps facing the
direction it was placed toward rather than rotating with the surface (the Surface tool uses
body-fixed lat/lon instead).[^mt-anchor]

The overlay previews the snap with a ring at the cursor, the body's name, and for a limb snap a faint
circle along the whole projected disc edge (`DrawLimbRing`).[^mt-overlay]

### Why not reuse the stock hover

The comment in `PickBody` lists the reasons: stock arbitration (`CursorTarget`, fed by
`Astronomical.UpdateMouseHover`) resolves a single winner across bodies, burn gizmos and orbit
points, knows no limb and no tolerance band, and is computed once per frame at the live cursor, so
a click callback would see the previous frame's target. The mod's own scan is cheap (one projection
per body) and gives disc, limb and center in one pass.

## Lifecycle and timing

| Path | Hook | Notes |
|---|---|---|
| Placement | `Program.OnMouseButton` prefix, inside `Glfw.PollEvents` in `PrepareFrame` (`frame:input`) | `CursorHoverEligible` holds the previous frame's UI pass, as the code comment says |
| Hover preview | `[StarMapAfterGui]` (`frame:draw-ui`), throttled to about 22 picks per second | `CursorHoverEligible` was cleared earlier this frame and not yet set again, so it reads `false`; see the pitfall below |

Both paths only read body positions and the camera.

## Key code

| Path | Role |
|---|---|
| [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L744-L851) | `PickBody` and `SnapToLimb` |
| [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L14-L25) | Pixel constants for center, disc and band |
| [MeasureTools/Features/Measure/MeasureOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureOverlay.cs#L636-L651) | Limb ring preview |

## Snippets

```csharp
// MeasureTools/Features/Measure/MapPicker.cs L785-L831
foreach (Astronomical astronomical in system.All.AsSpan())
{
    float2 s = camera.EclToScreen(astronomical.GetPositionEcl());
    if (float.IsNaN(s.X) || float.IsNaN(s.Y))
        continue;
    float d = float2.Distance(s, mouseViewport);
    bool visibleMarker = astronomical is StellarBody || astronomical.CursorHoverEligible;
    if (visibleMarker && d < nearestDist)
    {
        nearestDist = d;
        nearest = astronomical;
    }
    // ...
    if (astronomical is Vehicle)
        continue;

    double distance = (astronomical.GetPositionEcl() - camera.PositionEcl).Length();
    if (!(distance > astronomical.MeanRadius))
        continue;
    float radiusPx = (float)(camera.GetObjectDiameterPixels(astronomical.MeanRadius * 2.0, distance) * 0.5);
    if (radiusPx < MinLimbDiscPx || d > radiusPx + LimbSnapTolerancePx)
        continue;
    if (radiusPx < discRadiusPx)
    {
        disc = astronomical;
        discRadiusPx = radiusPx;
        discCenterDist = d;
    }
}

if (disc != null)
{
    // Edge band snaps to the limb, the disc interior to the center.
    if (discCenterDist >= discRadiusPx - LimbSnapTolerancePx)
    {
        Anchor? limb = SnapToLimb(camera, mouseViewport, disc);
        if (limb != null)
            return limb;
    }
    return Anchor.AtBody(disc);
}
return nearest != null ? Anchor.AtBody(nearest) : null;
```

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `Astronomical.CursorHoverEligible`, `GetPositionEcl`, `MeanRadius` | [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs) | Visibility gate, positions, disc size |
| `IOrbiter.OnDrawUi` (sets `CursorHoverEligible`) | [current/decomp/KSA/IOrbiter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IOrbiter.cs#L222-L245) | Where the flag is written, next to the name label |
| `Program.OnFrameViewports` (clears the flag every frame) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2664-L2674) | Reset point that decides how fresh the flag is |
| `Camera.EclToScreen`, `GetObjectDiameterPixels`, `ScreenToEgoRay`, `EgoToEcl` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L807-L811) | Projection and pixel radius of a sphere |
| `CelestialSystem.All` | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs) | Every body, star and vehicle in the system |

## Pitfalls and version notes

- Freshness of `CursorHoverEligible` on the preview path: by the decomp frame order,
  `Program.OnFrameViewports` sets the flag to `false` for every body before `[StarMapAfterGui]`
  runs, and the main viewport's `Universe.OnDrawUi` (which sets it again through
  `IOrbiter.OnDrawUi`) runs later in `OnDrawUiThreadSafe`.[^ksa-program] So inside AfterGui the
  flag reads `false` for every non-star body in the main view (the only exception is a hovered
  secondary viewport, whose world UI in `OnDrawUiViewports` sets the flags for its own camera just
  before AfterGui), while the click callback at the next `PollEvents` sees the values the previous
  frame's UI pass left. The source comment states the preview reads the
  current frame; the decomp says otherwise. Expected effect (not checked in game): the hover preview
  does not show the center snap for dot-sized planets and moons that a click would still snap to.
  Discs of 16 px and more are unaffected.
- With celestial names turned off, the game never sets the flag, so tier 2 only offers stars, which
  matches what the view marks. The same holds in the vehicle editor: `OnDrawUiThreadSafe` calls
  `Universe.OnDrawUi(MainViewport)` only while `EditorFlag` is false.
- Limb anchors are stored as a fixed CCE direction; they do not rotate with the planet.
- Vehicles are excluded from the disc tier on purpose; part-level picking or the center snap reaches
  them instead.

## See also

- [Overview](../overview.md)
- [Orbit-line snapping](./orbit-line-snapping.md)
- [Cursor unprojection and the construction plane](./cursor-unprojection-and-construction-plane.md)
- [Live anchors in body, orbit, surface and part frames](./live-body-relative-anchors.md)
- [Pick a body under the cursor by its projected centre and disc radius, and snap to its limb by the closest point on the cursor ray](../../../howto/flight-and-orbits/map-view/anchor-and-pick-points-on-bodies/anchor-and-pick-points-on-bodies-3.md) - how-to article demonstrating this feature

[^mt-mappicker]: MeasureTools/Features/Measure/MapPicker.cs
[^mt-anchor]: MeasureTools/Features/Measure/Anchor.cs
[^mt-overlay]: MeasureTools/Features/Measure/MeasureOverlay.cs
[^mt-readme]: KSA-MeasureTools README.md
[^ksa-iorbiter]: KSA.IOrbiter (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
