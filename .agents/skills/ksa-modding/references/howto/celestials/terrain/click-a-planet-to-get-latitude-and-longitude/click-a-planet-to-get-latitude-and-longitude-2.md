---
type: How-To
title: "Draw a great-circle arc over a planet without drawing through it: 48 Rodrigues samples with a back-face test"
description: Sample a great circle between two surface points with Rodrigues rotation, interpolate the radius, and draw each segment only while it faces the camera so the line breaks at the limb
category: celestials
group: terrain
topic: click-a-planet-to-get-latitude-and-longitude
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-measuretools/features/surface-great-circle-tool]
techniques: [imgui-drawlist]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Celestial, KSA.Camera]
tags: [great-circle, arc, rodrigues, imgui-drawlist, limb, back-face]
---
Outcome: an arc over a sphere between two surface pins that stops at the limb instead of cutting across the planet's disc, with a label at its midpoint. Demonstrated by KSA-MeasureTools ([Surface great-circle tool](../../../../mods/ksa-measuretools/features/surface-great-circle-tool.md)). Sub-step of [approach 1](click-a-planet-to-get-latitude-and-longitude-1.md): the pins are already resolved to ecliptic points.

## Background: how the game does it

A 2D draw list has no depth buffer, so a line over a planet is drawn on top of it even where it is on the far side. The fix is geometric: sample the great circle in 3D and drop each sample whose outward normal faces away from the camera. Projection comes from `Camera.EclToScreen(posEcl)` ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L340)); for the general projection and clipping rules see [project world positions to screen pixels](../../../rendering/overlays/project-world-positions-to-screen/overview.md).

## Lifecycle and timing

Drawn from `[StarMapAfterGui]` (`frame:draw-ui`) onto the viewport's ImGui draw list, resolved with this frame's body rotation. Nothing is written to the game. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Build the basis** from the two radial unit vectors: the axis is their normalised cross product, the angle their `acos` (clamped), the radii the actual distances of the two points from the body centre:

```csharp
// MeasureTools/Features/Measure/MeasureOverlay.cs L308-L322
private static bool TryGreatCircleBasis(double3 centerEcl, double3 aEcl, double3 bEcl, out GreatCircleBasis basis)
{
    basis = default;
    double3 da = aEcl - centerEcl;
    double3 db = bEcl - centerEcl;
    double ra = da.Length();
    double rb = db.Length();
    if (!(ra > 0.0) || !(rb > 0.0))
        return false;
    double3 ua = da * (1.0 / ra);
    double3 ub = db * (1.0 / rb);
    double angle = Math.Acos(Math.Clamp(double3.Dot(ua, ub), -1.0, 1.0));
    double3 axis = double3.Cross(ua, ub).NormalizeOrZero();
    basis = new GreatCircleBasis(ua, axis, angle, ra, rb);
    return true;
```

2. **Skip degenerate cases.** Coincident points (angle near 0) and antipodal points (axis zero) have no unique great circle: draw nothing but the endpoint dots and the label, anchored at the first pin:

```csharp
// MeasureTools/Features/Measure/MeasureOverlay.cs L325-L340
private static void DrawGreatCircleArc(ImDrawListPtr dl, Camera camera, float2 vpPos, double3 centerEcl, double3 aEcl, double3 bEcl, byte4 color, float thickness)
{
    if (!TryGreatCircleBasis(centerEcl, aEcl, bEcl, out GreatCircleBasis basis))
        return;
    double3 ua = basis.Ua;
    double3 axis = basis.Axis;
    double angle = basis.Angle;
    double ra = basis.Ra;
    double rb = basis.Rb;
    if (angle < 1e-9 || basis.AxisIsZero)
    {
        // Coincident or antipodal points: no unique great circle, draw nothing
        // (the endpoint dots and the label still render).
        return;
    }

```

3. **Sample with Rodrigues' rotation** of the first radial vector about the axis, 48 segments, interpolating the radius linearly between the pins. A sample is drawn only while `dot(dir, cameraPos - point) > 0`; otherwise the polyline breaks:

```csharp
// MeasureTools/Features/Measure/MeasureOverlay.cs L341-L364
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
}
```

```csharp
// MeasureTools/Features/Measure/MeasureOverlay.cs L379-L384
// Rodrigues' rotation of a vector about a unit axis by the given angle.
private static double3 Rotate(double3 v, double3 axis, double angle)
{
    double c = Math.Cos(angle);
    double s = Math.Sin(angle);
    return v * c + double3.Cross(axis, v) * s + axis * (double3.Dot(axis, v) * (1.0 - c));
```

4. **Place the label** at the arc midpoint by rotating by half the angle and averaging the radii; fall back to the first endpoint when the circle is degenerate:

```csharp
// MeasureTools/Features/Measure/MeasureOverlay.cs L367-L377
private static float2 GreatCircleMidScreen(Camera camera, float2 vpPos, double3 centerEcl, double3 aEcl, double3 bEcl)
{
    if (!TryGreatCircleBasis(centerEcl, aEcl, bEcl, out GreatCircleBasis basis))
        return new float2(float.NaN, float.NaN);
    // Coincident or antipodal points: fall back to the first endpoint so the label
    // still has an anchor.
    if (basis.AxisIsZero)
        return vpPos + camera.EclToScreen(aEcl);
    double3 mid = Rotate(basis.Ua, basis.Axis, basis.Angle * 0.5);
    return vpPos + camera.EclToScreen(centerEcl + mid * ((basis.Ra + basis.Rb) * 0.5));
}
```

## Complete example

| File | Role |
|---|---|
| [MeasureTools/Features/Measure/MeasureOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureOverlay.cs#L246-L385) | Surface measurement, arc, midpoint, Rodrigues rotation |

## Variations and gotchas

- **Segment count is a quality choice.** 48 samples keep long arcs smooth; fewer shows the polyline on small bodies.
- **Back-face, not occlusion.** The test hides the far side of the sphere the arc lies on, not parts of the arc hidden behind other bodies or terrain relief.
- **Interpolated radius.** The helper interpolates the two points' actual distances from the body centre. Surface pins are resolved through `GetPositionEclFromLatLon`, so both sit at `MeanRadius` and the arc is a true great circle on the mean sphere; the interpolation matters when the helper is fed other anchor kinds at different radii.
- **Valid screen points only.** `Valid(s)` rejects NaN projections (behind the camera).

## See also

- [Overview](overview.md); [approach 1](click-a-planet-to-get-latitude-and-longitude-1.md)
- [Surface great-circle tool](../../../../mods/ksa-measuretools/features/surface-great-circle-tool.md)
- [Project world positions to screen pixels](../../../rendering/overlays/project-world-positions-to-screen/overview.md), [Draw markers and annotations on world objects](../../../rendering/overlays/draw-markers-and-annotations-on-world-objects/overview.md)
