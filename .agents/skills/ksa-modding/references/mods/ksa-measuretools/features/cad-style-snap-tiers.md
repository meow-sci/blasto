---
type: Mod Feature
title: "CAD-style snapping: nodes, rim centers, mirror point, vertices, edges and axial/radial components"
description: Tiered part snapping (nodes and centers, rim centers and the mirror point, then vertices and edge midpoints, then edge slide, then the raw hull hit) plus axial and radial components for rulers between parts of one vehicle
mod: ksa-measuretools
category: vehicles-and-parts
techniques: [screen-space-picking, mesh-analysis]
hooks: [harmony-prefix KSA.Program.OnMouseButton, starmap StarMapAfterGui]
lifecycle: [frame:input, frame:draw-ui]
game_types: [KSA.Part, KSA.Part.Connector, KSA.Vehicle, KSA.VehicleEditingSpace, KSA.Camera]
tags: [cad, snapping, parts, mirror, symmetry, edges, vertices, axial, radial, stack-axis]
---

Beyond attach nodes and hull points, the part picker behaves like a CAD sketcher: it slides along a
tank rim between its vertices, snaps to edge midpoints and fitted rim centers, and offers the exact
opposite point of the previous click across the part's axis for diameter-style measurements. A ruler
between two parts of the same vehicle also reports how much of the distance runs along the stack axis
and how much across it, on the overlay and in the copied text.[^mt-readme]

## How it works

### Tier order

`MapPicker.PickPart` resolves one candidate per tier and returns the first tier that produced
anything, "most intentional first":[^mt-mappicker]

| Tier | Candidates | Screen radius | Toggle |
|---|---|---|---|
| 1. Point features | Attach nodes and part bounding-box centers (all scanned vehicles), fitted rim centers and the mirror point (hit subpart only) | 16 px | "Nodes, centers, rims" |
| 2. Vertices and midpoints | Welded mesh vertices and feature-edge midpoints of the hit subpart | 12 px | "Vertices and edges" |
| 3. Edge slide | Closest point on any feature edge of the hit subpart | 10 px | same |
| 4. Surface | The raw watertight raycast hit, with its normal | always under the cursor | "Parts" master switch |

Tiers 2 to 4 need a mesh hit; tier 1's node and center scan does not, so a node on the silhouette
still snaps. If no tier produced a candidate, picking falls through to bodies, orbit lines and free
placement. The window's three checkboxes (`PartSnapEnabled`, `PartFeatureSnapEnabled`,
`PartVertexSnapEnabled`) are nested under the master "Bodies and orbit lines" snap toggle, and every
setter bumps `StateVersion` so the preview re-picks at once.[^mt-window][^mt-state]

### Rim centers

`AddCircleCenterCandidates` projects each cached circle center of the hit subpart
(`circle.Center.Transform(subPart.MatrixAsmb2Ego(in m))`) and offers it as a point feature that also
carries the circle's plane normal.

### Mirror point across the stack axis

`AddMirrorCandidate` runs only when the previous pending point sits on the same full part as the
current hit. It brings that point into the full part's frame (inverse of `MatrixAsmb2Ego`), reflects
it across the part's axis line (keep the along-axis component, negate the perpendicular one), and
offers the result as "opposite". `GetPartAxis` defines the axis as the line through the two most
opposed stack connectors (connectors without `Part.Connector.Flag.ToSurface` or `FromSurface`) whose
facing vectors, part-frame `+X` rotated by `Connector.Asmb2ParentAsmb`, are within about 25 degrees
of anti-parallel (`dot < -0.9`). Otherwise it falls back to part-frame `X` through the bounding-box
center.[^ksa-part] The `+X` facing convention matches the stock content, where a fuel tank's two
stack connectors sit at `Position X="0.5"` and `X="-0.5"` with the second rotated by pi about
Z,[^ksa-content-fueltank] and the editor's own connector code, which rotates `double3(1, 0, 0)` by a
connector's `Asmb2ParentAsmb` to get its facing direction.[^ksa-vehicleeditor]

### Vertices, midpoints and edge slide

`TryPickVertexOrMidpoint` projects every welded vertex and every feature-edge midpoint of the hit
subpart and keeps the nearest within 12 px. `TryPickEdgePoint` projects both ends of each feature
edge, computes the closest point on the 2D segment and its parameter `t`, and maps `t` back onto the
3D segment (`A + (B - A) * t`), so the point slides along a rim.

### Axial and radial components

`Measurement.TryGetAxialRadialMeters` applies when both ruler anchors are flight part anchors on the
same vehicle, or both are editor part anchors. It rotates the ECL delta back into the vehicle's asmb
frame with the inverse of `Vehicle.Asmb2Ego` (or `EditingSpace.Asmb2Ecl` in the editor; ego and ECL
share axes) and reports `|dx|` as axial and `sqrt(dy^2 + dz^2)` as radial. Asmb `X` is the stack axis:
stack connectors sit on part-frame X and `Vehicle.BODY2UPFRAME` (a 180-degree rotation about X) keeps
X as the up axis.[^mt-measurement][^ksa-vehicle] The overlay prints a second label line "ax 12.3 m
rad 0.8 m" under the distance, and the copied text gets ", axial ..., radial ...".[^mt-overlay]

## Lifecycle and timing

Same as the part picker: full evaluation on the placement click in the `Program.OnMouseButton`
prefix (`frame:input`), throttled evaluation for the hover preview in `[StarMapAfterGui]`
(`frame:draw-ui`). Axial and radial values are computed every time a measurement is drawn or copied,
from the current vehicle rotation.

## Key code

| Path | Role |
|---|---|
| [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L328-L380) | `PickPart` tier order |
| [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L462-L572) | Rim centers, mirror point, `GetPartAxis` |
| [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L654-L742) | Vertex, midpoint and edge-slide tiers |
| [MeasureTools/Features/Measure/Measurement.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Measurement.cs#L97-L130) | Axial and radial components |
| [MeasureTools/Features/Measure/MeasureWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureWindow.cs#L223-L267) | Nested snap toggles |

## Snippets

```csharp
// MeasureTools/Features/Measure/MapPicker.cs L503-L510
double4x4 matrixFullPart2Ego = fullPart.MatrixAsmb2Ego(in hit.Matrix);
double4x4.Invert(matrixFullPart2Ego, out double4x4 ego2FullPart);
double3 previousLocal = (previous.ResolveEcl() - camera.PositionEcl).Transform(ego2FullPart);

(double3 axisPoint, double3 axisDirection) = GetPartAxis(fullPart);
double3 offset = previousLocal - axisPoint;
double3 along = axisDirection * double3.Dot(offset, axisDirection);
double3 mirroredLocal = axisPoint + along - (offset - along);
```

```csharp
// MeasureTools/Features/Measure/MapPicker.cs L718-L740
for (int i = 0; i < edges.Length; i++)
{
    float2 a = camera.EgoToScreen(edges[i].A.Transform(matrixAsmb2Ego));
    float2 b = camera.EgoToScreen(edges[i].B.Transform(matrixAsmb2Ego));
    if (float.IsNaN(a.X) || float.IsNaN(a.Y) || float.IsNaN(b.X) || float.IsNaN(b.Y))
        continue;
    float2 ab = b - a;
    float lengthSq = ab.X * ab.X + ab.Y * ab.Y;
    float t = 0f;
    if (lengthSq > 1e-6f)
    {
        float2 toMouse = mouseViewport - a;
        t = Math.Clamp((toMouse.X * ab.X + toMouse.Y * ab.Y) / lengthSq, 0f, 1f);
    }
    var closest = new float2(a.X + ab.X * t, a.Y + ab.Y * t);
    float d = float2.Distance(closest, mouseViewport);
    if (d < bestDist)
    {
        bestDist = d;
        edgePointLocal = edges[i].A + (edges[i].B - edges[i].A) * t;
        found = true;
    }
}
```

```csharp
// MeasureTools/Features/Measure/Measurement.cs L111-L129
doubleQuat asmb2Ecl;
if (a.Kind == AnchorKind.PartPoint && b.Kind == AnchorKind.PartPoint
    && a.Body is Vehicle vehicle && ReferenceEquals(vehicle, b.Body))
{
    asmb2Ecl = vehicle.Asmb2Ego;
}
else if (a.Kind == AnchorKind.EditorPartPoint && b.Kind == AnchorKind.EditorPartPoint
    && Program.Editor is VehicleEditor editor)
{
    asmb2Ecl = editor.EditingSpace.Asmb2Ecl;
}
else
{
    return false;
}
double3 deltaAsmb = (b.ResolveEcl() - a.ResolveEcl()).Transform(doubleQuat.Inverse(asmb2Ecl));
axial = Math.Abs(deltaAsmb.X);
radial = Math.Sqrt(deltaAsmb.Y * deltaAsmb.Y + deltaAsmb.Z * deltaAsmb.Z);
return true;
```

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `Part.Connector.Flags` (`Flag.ToSurface`, `Flag.FromSurface`), `Asmb2ParentAsmb`, `PositionParentAsmb` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L191-L262) | Stack-axis detection |
| `Part.MatrixAsmb2Ego`, `BoundingBoxPartAsmb`, `FullPart` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1213-L1225) | Mirror math in the part frame; axis fallback |
| `Vehicle.Asmb2Ego`, `Vehicle.BODY2UPFRAME` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L271-L275) | Rotating deltas into the vehicle frame |
| `VehicleEditingSpace.Asmb2Ecl` | [current/decomp/KSA/VehicleEditingSpace.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditingSpace.cs) | Editor equivalent |
| `VehicleEditor.TryComputeAttachedMountQuat` | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L3372-L3395) | Stock use of connector `+X` as the facing direction |
| `CoreFuelTankAAssets.xml` connectors | [current/Content/Core/CoreFuelTankAAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/CoreFuelTankAAssets.xml) | Stack connectors on part-frame X |

## Pitfalls and version notes

- The edge-slide parameter is interpolated in screen space and applied in 3D, which is not
  perspective-correct; on long edges seen at a grazing angle the 3D point drifts slightly from the
  cursor. Rims are short segments, so the error is small.
- The node and center scan has no occlusion test; vertices, midpoints and edges are limited to the
  hit subpart, so they cannot leak through the hull.
- Parts whose stack connectors are not on part-frame X (or that have only one stack connector) mirror
  across part-frame X through the bounding-box center, which may not be their symmetry axis.
- Axial/radial is only defined for two part anchors with one owner; a ruler between two vehicles
  shows plain distance.

## See also

- [Overview](../overview.md)
- [Part picking](./part-mesh-raycast-picking.md)
- [Mesh feature edges and rim circles](./mesh-feature-edges-and-circle-fitting.md)
- [Circle and face-angle tools](./circle-and-face-angle-tools.md)
- [Overlay rendering](./overlay-drawlist-rendering.md)
- [Derive the stack axis from connectors and split vectors into axial and radial parts](../../../howto/vehicles-and-parts/part-geometry/get-part-points-in-vehicle-and-world-frames/get-part-points-in-vehicle-and-world-frames-3.md) - how-to article demonstrating this feature

[^mt-mappicker]: MeasureTools/Features/Measure/MapPicker.cs
[^mt-measurement]: MeasureTools/Features/Measure/Measurement.cs
[^mt-overlay]: MeasureTools/Features/Measure/MeasureOverlay.cs
[^mt-window]: MeasureTools/Features/Measure/MeasureWindow.cs
[^mt-state]: MeasureTools/Features/Measure/MeasureState.cs
[^mt-readme]: KSA-MeasureTools README.md
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditor]: KSA.VehicleEditor (decompiled, build 2026.10.10.5554)
[^ksa-content-fueltank]: Core/CoreFuelTankAAssets.xml (game content, build 2026.10.10.5554)
