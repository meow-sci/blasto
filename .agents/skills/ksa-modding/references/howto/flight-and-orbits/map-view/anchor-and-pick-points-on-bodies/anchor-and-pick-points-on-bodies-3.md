---
type: How-To
title: Pick a body under the cursor by its projected centre and disc radius, and snap to its limb by the closest point on the cursor ray
description: Project every body's centre and disc radius to screen, let the smallest disc under the cursor win, snap to the limb in an edge band using the cursor ray's closest point to the centre, and gate dot-sized bodies on the game's CursorHoverEligible flag
category: flight-and-orbits
group: map-view
topic: anchor-and-pick-points-on-bodies
approach: 3
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksa-measuretools/features/body-and-limb-snapping]
techniques: [screen-space-picking, harmony-prefix]
hooks: [harmony-prefix KSA.Program.OnMouseButton, starmap StarMapAfterGui]
lifecycle: [frame:input, frame:draw-ui]
game_types:
  - KSA.Astronomical
  - KSA.StellarBody
  - KSA.CelestialSystem
  - KSA.Camera
  - KSA.IOrbiter
  - KSA.Program
  - KSA.Universe
tags: [picking, cursor, body, limb, snapping, screen-space, cursor-hover-eligible, map-view]
---
Outcome: a click near a planet in the map view lands on its centre, or, when the cursor is on the edge of its disc, on the point of its surface sphere in that direction; a dot-sized moon or a star snaps at its centre; and bodies the game is not marking on screen are not snap targets, so free placement still works in a crowded system. KSA-MeasureTools picks this way instead of reusing the stock hover target.[Body and limb snapping](../../../../mods/ksa-measuretools/features/body-and-limb-snapping.md)

Prerequisites: StarMap; Harmony for the click prefix on `Program.OnMouseButton`; an anchor type to store the result ([approach 2](anchor-and-pick-points-on-bodies-2.md)).

## Background: how the game does it

- **Projection.** `Camera.EclToScreen(double3, ignoreBehind = true)` returns viewport pixels, NaN behind the camera; `Camera.GetObjectDiameterPixels(diameter, distance)` gives a sphere's projected size; `Camera.ScreenToEgoRay` and `EgoToEcl` give the cursor ray.[^ksa-camera] [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L340) [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L807-L810)
- **What is marked on screen.** `Astronomical.CursorHoverEligible` is cleared for every body in `Program.OnFrameViewports` and set again in `IOrbiter.OnDrawUi`, inside the `GameSettings.ShowCelestialNames()` branch, under the predicate that draws the name label and outline box:[^ksa-program] [^ksa-iorbiter] [current/decomp/KSA/IOrbiter.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IOrbiter.cs#L222-L245)

```csharp
// current/decomp/KSA/Program.cs L2665-L2674
		CelestialSystem currentSystem = Universe.CurrentSystem;
		if (currentSystem != null)
		{
			ReadOnlySpan<Astronomical> readOnlySpan = currentSystem.All.AsSpan();
			for (int l = 0; l < readOnlySpan.Length; l++)
			{
				Astronomical astronomical = readOnlySpan[l];
				astronomical.CursorHoverEligible = false;
			}
		}
```

- **Every body, star and vehicle** is in `CelestialSystem.All`. `StellarBody` (stars) is never an `IOrbiter`, so a fixed star is never flagged.[^ksa-celestialsystem] [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L66)
- **Stock arbitration** (`CursorTarget`, fed by `Astronomical.UpdateMouseHover`) resolves one winner across bodies, burn gizmos and orbit points at the live cursor once per frame; it knows no limb, and a click callback would read the previous frame's target.[^ksa-astronomical] [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L86)

## Lifecycle and timing

| Path | Hook | When `CursorHoverEligible` was written |
|---|---|---|
| Placement click | Harmony prefix on `Program.OnMouseButton`, inside `Glfw.PollEvents` in `PrepareFrame` (`frame:input`) | The previous frame's UI pass: the values the player saw |
| Hover preview | `[StarMapAfterGui]` (`frame:draw-ui`), throttled | Cleared earlier this frame in `OnFrameViewports`; the main viewport sets it later in `OnDrawUiThreadSafe`, so it reads `false` here |

Both paths only read body positions and the camera. If you need the flag during the draw, read it after `OnDrawUiThreadSafe` (or cache the previous frame's values), not in `[StarMapAfterGui]`. See the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Fix the pixel thresholds.** A centre-snap radius close to the stock hover box, a minimum disc size below which the limb equals the centre, and an edge band:

```csharp
// MeasureTools/Features/Measure/MapPicker.cs L15-L25
    // Point-like body snap radius in screen pixels; the stock hover box is similar
    // (max(0.5% of viewport height, 8 px) plus the projected body radius).
    private const float CenterSnapRadiusPx = 14f;

    // A body counts as a disc (its limb is a snap target) from this projected
    // radius up; below it the disc is dot-sized and the limb equals the center.
    private const float MinLimbDiscPx = 16f;

    // Snap band around the projected disc edge: inside it the limb wins over the
    // center, outside the disc it still catches a slightly-off click.
    private const float LimbSnapTolerancePx = 10f;
```

2. **Scan every body once.** Project the centre (skip NaN, behind the camera); offer a centre snap only for stars and bodies the game flagged as marked; for non-vehicles compute the projected disc radius and keep the smallest disc under the cursor (a moon in front of its planet). Edge band snaps to the limb, the disc interior to the centre:

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

3. **Snap to the limb by the ray's closest point.** Take the cursor ray, find its closest point to the body centre (`t = dot(center - origin, direction)`), and push that point radially out to `MeanRadius`. At map distances that is the visible limb toward the cursor; the exact tangent circle is tilted toward the camera by about radius over distance:

```csharp
// MeasureTools/Features/Measure/MapPicker.cs L838-L851
    private static Anchor? SnapToLimb(Camera camera, float2 mouseViewport, Astronomical body)
    {
        Ray ray = camera.ScreenToEgoRay(mouseViewport);
        double3 origin = camera.EgoToEcl(ray.Origin);
        double3 center = body.GetPositionEcl();
        double t = double3.Dot(center - origin, ray.Direction);
        if (!(t > 0.0))
            return null;
        double3 closest = origin + ray.Direction * t;
        double3 dir = (closest - center).NormalizeOrZero();
        if (dir.X == 0.0 && dir.Y == 0.0 && dir.Z == 0.0)
            return null;
        return Anchor.AtSurface(body, dir * body.MeanRadius);
    }
```

4. **Take the click before the game does.** A Harmony prefix on `Program.OnMouseButton` consumes the left press while the tool is armed, so a placement click does not also focus a body, set a target or create a burn; middle and right drags still move the camera.

## Complete example

| File | Role |
|---|---|
| [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L744-L851) | `PickBody`, `SnapToLimb` |
| [MeasureTools/Patches/Patch_MouseButton.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Patches/Patch_MouseButton.cs) | Click prefix while armed |
| [MeasureTools/Features/Measure/MeasureOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureOverlay.cs#L636-L651) | Limb-ring preview |

## Variations and gotchas

- **The preview reads a cleared flag.** By the decomp frame order, `CursorHoverEligible` is `false` for every non-star body in the main view inside `[StarMapAfterGui]`; the MeasureTools comment says otherwise. Expect the hover preview to miss centre snaps for dot-sized bodies a click would still take.
- **Names off means stars only.** With celestial names hidden the game never sets the flag, so the centre tier offers only stars.
- **Exclude vehicles from the disc tier.** A vehicle's `MeanRadius` is its bounding-sphere radius, not a surface.
- **Limb anchors do not rotate with the planet.** `Anchor.AtSurface` stores a CCE offset; use a lat/lon pin for a ground feature.
- **Use the hovered viewport's camera.** Pixel coordinates are per viewport; a secondary viewport needs its own camera and offset.

## See also

- [Overview](overview.md); [Orbit-line snap](anchor-and-pick-points-on-bodies-4.md); [Immutable anchors](anchor-and-pick-points-on-bodies-2.md)
- [Body and limb snapping (mod feature)](../../../../mods/ksa-measuretools/features/body-and-limb-snapping.md)
- [Draw a predicted trajectory and markers in the map view](../draw-a-predicted-trajectory-and-map-markers/overview.md)

[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-iorbiter]: KSA.IOrbiter (decompiled, build 2026.10.10.5554)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
[^ksa-astronomical]: KSA.Astronomical (decompiled, build 2026.10.10.5554)
