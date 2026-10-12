---
type: Mod Feature
title: Surface tool with body-fixed pins, great-circle distance and an occlusion-aware arc
description: Ray-casts the cursor against each celestial's mean-radius sphere, pins hits as body-fixed lat/lon, reports haversine distance, chord and bearing, and draws the great-circle arc while hiding samples on the far hemisphere
mod: ksa-measuretools
category: celestials
techniques: [screen-to-world-ray, imgui-drawlist]
hooks: [harmony-prefix KSA.Program.OnMouseButton, starmap StarMapAfterGui]
lifecycle: [frame:input, frame:draw-ui]
game_types: [KSA.Celestial, KSA.Camera, KSA.Ray, KSA.CelestialSystem, KSA.Astronomical]
tags: [celestials, surface, great-circle, haversine, bearing, lat-lon, map-view, overlay]
---

The Surface tool pins two points to a planet's surface and reads the great-circle distance, the
straight chord, and the initial bearing from the first pin to the second. Pins track the body's
rotation like ground markers, and the arc is drawn over the visible hemisphere only.[^mt-readme]

## How it works

### Picking a pin

`MapPicker.PickSurface` does not use the body snaps. It builds the cursor ray with
`camera.ScreenToEgoRay`, and for every `Celestial` in the system ray-tests a
`BoundingSphere3D(camera.GetPositionEgo(celestial), celestial.MeanRadius)` with `Ray.Raycast(sphere,
out t, out inside)`, keeping the nearest positive hit from outside the sphere. Once the first pin is
down, only that pin's body qualifies, because the great-circle math needs both points on one
sphere.[^mt-mappicker][^ksa-ray]

The hit is lifted to ECL from the ray's own origin (`camera.EgoToEcl(ray.Origin) + direction * t`,
correct for orthographic projection too), converted to CCE with `GetPositionCceFromEcl`, and then to
degrees with `Celestial.GetLatitudeFromCce` and `GetLongitudeFromCce`, which rotate into the
body-fixed CCF frame first.[^ksa-celestial] The anchor is `Anchor.PinOnSurface(body, lat, lon)`,
labelled like "Earth 28.57N 80.65W".[^mt-anchor]

Each frame the pin resolves through `Celestial.GetPositionEclFromLatLon(lat, lon)`, which puts the
point on the mean-radius sphere via the body-fixed to CCE rotation, so pins rotate with the planet.

### The numbers

`Measurement` computes:[^mt-measurement]

- **Great-circle distance**: haversine in the `atan2` form, `R * 2 * atan2(sqrt(a), sqrt(1 - a))`,
  with `R = body.MeanRadius`; stable at all separations.
- **Initial bearing**: forward azimuth `atan2(sin dLon cos lat2, cos lat1 sin lat2 - sin lat1 cos lat2
  cos dLon)`, normalized to 0..360 degrees, 0 = north.
- **Chord**: the straight-line distance between the two resolved ECL points.

The overlay shows the great-circle distance as the headline and "chord 1,234 km  brg 87 deg" as a
second line; clicking the list row copies all three.[^mt-window]

### Drawing the arc

`DrawGreatCircleArc` builds the basis from the two radial unit vectors: the rotation axis is their
normalized cross product and the angle their `acos`. It samples 48 points with Rodrigues' rotation of
the first radial vector about the axis, interpolating the radius linearly between the two pins'
actual distances. A sample is drawn only while its outward direction faces the camera
(`dot(dir, cameraPos - point) > 0`), so the polyline breaks at the limb instead of cutting across the
disc. Coincident or antipodal pins have no unique great circle; nothing is drawn but the dots and label
remain, anchored at the first pin. The label sits at the arc midpoint (`Rotate(ua, axis, angle /
2)`).[^mt-overlay]

## Lifecycle and timing

- Placement in the `Program.OnMouseButton` prefix (`frame:input`): the body's rotation and position
  are post-physics for this step.
- Arc, pins and labels in `[StarMapAfterGui]` (`frame:draw-ui`), resolved with this frame's body
  rotation, so the pins visibly turn with the planet under time warp.
- No game state is written.

## Key code

| Path | Role |
|---|---|
| [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L96-L144) | `PickSurface` |
| [MeasureTools/Features/Measure/Measurement.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Measurement.cs#L29-L60) | Haversine distance and bearing |
| [MeasureTools/Features/Measure/MeasureOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureOverlay.cs#L246-L385) | Surface measurement, arc, midpoint, Rodrigues rotation |
| [MeasureTools/Features/Measure/Anchor.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Anchor.cs#L232-L242) | `PinOnSurface` |

## Snippets

```csharp
// MeasureTools/Features/Measure/MapPicker.cs L114-L143
foreach (Astronomical astronomical in system.All.AsSpan())
{
    if (astronomical is not Celestial celestial)
        continue;
    if (required != null && celestial != required)
        continue;
    var sphere = new BoundingSphere3D(camera.GetPositionEgo(celestial), celestial.MeanRadius);
    if (!ray.Raycast(sphere, out double t, out bool inside) || inside || !(t > 0.0))
        continue;
    if (t < bestT)
    {
        bestT = t;
        best = celestial;
    }
}
if (best == null)
    return null;
// ...
double3 hitEcl = camera.EgoToEcl(ray.Origin) + ray.Direction * bestT;
double3 hitCce = best.GetPositionCceFromEcl(hitEcl);
double latitude = best.GetLatitudeFromCce(hitCce);
double longitude = best.GetLongitudeFromCce(hitCce);
return Anchor.PinOnSurface(best, latitude, longitude);
```

```csharp
// MeasureTools/Features/Measure/MeasureOverlay.cs L341-L363
float2 prev = default;
bool hasPrev = false;
for (int i = 0; i <= SurfaceArcSegments; i++)
{
    double t = (double)i / SurfaceArcSegments;
    double3 dir = Rotate(ua, axis, angle * t);
    double radius = ra + (rb - ra) * t;
    double3 pointEcl = centerEcl + dir * radius;
    // Visible while the surface normal faces the camera.
    bool facing = double3.Dot(dir, camera.PositionEcl - pointEcl) > 0.0;
    float2 s = vpPos + camera.EclToScreen(pointEcl);
    if (facing && Valid(s))
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

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `Celestial.GetPositionEclFromLatLon`, `GetLatitudeFromCce`, `GetLongitudeFromCce`, `MeanRadius` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L677-L740) | Body-fixed pins on the mean-radius sphere |
| `Astronomical.GetPositionCceFromEcl` | [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L374-L378) | ECL to CCE before lat/lon |
| `Camera.ScreenToEgoRay`, `GetPositionEgo`, `EgoToEcl`, `EclToScreen` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L700-L740) | Cursor ray; drawing |
| `Ray.Raycast(BoundingSphere3D, out t, out inside)` | [current/decomp/KSA/Ray.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Ray.cs) | Ray versus body sphere |

## Pitfalls and version notes

- Pins sit on the mean-radius sphere, not on terrain; on bodies with large relief the pin is drawn
  above or below the visible ground and the distance is a sphere distance.
- The haversine uses `MeanRadius`, so oblate bodies are treated as spheres.
- The ray test rejects hits from inside a sphere, so the tool does not work with the camera below a
  body's mean radius (for example deep in a canyon close up).
- Only celestials are candidates; stars and vehicles are skipped by the type check.

## See also

- [Overview](../overview.md)
- [Live anchors in body, orbit, surface and part frames](./live-body-relative-anchors.md)
- [Cursor unprojection and the construction plane](./cursor-unprojection-and-construction-plane.md)
- [Overlay rendering](./overlay-drawlist-rendering.md)
- [Sphere pick, GetLatitudeFromCce and GetLongitudeFromCce, haversine, chord and bearing](../../../howto/celestials/terrain/click-a-planet-to-get-latitude-and-longitude/click-a-planet-to-get-latitude-and-longitude-1.md) - how-to article demonstrating this feature
- [Draw a great-circle arc over a planet without drawing through it: 48 Rodrigues samples with a back-face test](../../../howto/celestials/terrain/click-a-planet-to-get-latitude-and-longitude/click-a-planet-to-get-latitude-and-longitude-2.md) - how-to article demonstrating this feature

[^mt-mappicker]: MeasureTools/Features/Measure/MapPicker.cs
[^mt-measurement]: MeasureTools/Features/Measure/Measurement.cs
[^mt-anchor]: MeasureTools/Features/Measure/Anchor.cs
[^mt-overlay]: MeasureTools/Features/Measure/MeasureOverlay.cs
[^mt-window]: MeasureTools/Features/Measure/MeasureWindow.cs
[^mt-readme]: KSA-MeasureTools README.md
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-ray]: KSA.Ray (decompiled, build 2026.10.10.5554)
