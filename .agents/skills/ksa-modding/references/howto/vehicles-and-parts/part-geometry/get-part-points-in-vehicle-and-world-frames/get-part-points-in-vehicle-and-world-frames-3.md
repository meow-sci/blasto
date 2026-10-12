---
type: How-To
title: Derive the stack axis from connectors and split vectors into axial and radial parts
description: Find a part's stack axis from its opposed connectors, mirror points across it and split an ECL delta into components along and across the vehicle's X axis
category: vehicles-and-parts
group: part-geometry
topic: get-part-points-in-vehicle-and-world-frames
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-measuretools/features/cad-style-snap-tiers]
techniques: [screen-space-picking]
hooks: [harmony-prefix KSA.Program.OnMouseButton, starmap StarMapAfterGui]
lifecycle: [frame:input, frame:draw-ui]
game_types: [KSA.Part, KSA.PartConnectorInstance, KSA.Vehicle, KSA.VehicleEditor]
tags: [parts, connectors, axis, symmetry, measurement]
---
Outcome: find a part's symmetry axis from its stack connectors, mirror a point across it, and split a vector between two points on a vehicle into the part along the stack
("axial") and across it ("radial"). MeasureTools uses these for a diameter-style "opposite point" and for the second label line on a ruler.

Prerequisite: StarMap. Builds on [article 1](get-part-points-in-vehicle-and-world-frames-1.md) (connectors) and [article 2](get-part-points-in-vehicle-and-world-frames-2.md) (ECL resolve).
Demonstrated by [ksa-measuretools/cad-style-snap-tiers](../../../../mods/ksa-measuretools/features/cad-style-snap-tiers.md).

## Background: how the game does it

- **Connector facing.** Stock content puts a fuel tank's two stack connectors at `Position X="0.5"` and `X="-0.5"`, the second with `Rotation Z="3.1416"`
  [current/Content/Core/CoreFuelTankAAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/CoreFuelTankAAssets.xml#L346-L356)[^ksa-content-corefueltankaassets], i.e. they face part-frame `+X` rotated by the connector's `Asmb2ParentAsmb`. The editor uses the same convention: it rotates
  `new double3(1.0, 0.0, 0.0)` by `connector.Asmb2ParentAsmb` to get a connector direction (negated for surface connectors at that call site) [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L4987)[^ksa-vehicleeditor].
- **Surface connectors** carry `Flag.ToSurface` or `Flag.FromSurface` and are not stack connectors [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L191-L200)[^ksa-part].
- **Which axis is "up".** Stack connectors sit on part-frame X, so MeasureTools treats assembly X as the stack axis; `Vehicle.BODY2UPFRAME` is a 180 degree rotation about X, which leaves X unchanged
  [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L271)[^ksa-vehicle].

## Lifecycle and timing

Computed on demand: the picker evaluates the mirror candidate during the click (`frame:input`, `Program.OnMouseButton` prefix) and the hover preview (`frame:draw-ui`,
`[StarMapAfterGui]`); the axial/radial split is computed every time a measurement is drawn or copied from the current `Asmb2Ego`. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Find the axis.** Among non-surface connectors, find the pair whose facing vectors are most opposed (`dot < -0.9`, about 25 degrees from anti-parallel); the axis runs through them.
   Fall back to part-frame X through the bounding-box centre:

```csharp
// MeasureTools/Features/Measure/MapPicker.cs L534-L572
private static (double3 Point, double3 Direction) GetPartAxis(Part part)
{
    Part.Connector? bestA = null;
    Part.Connector? bestB = null;
    // A connector pair only counts as the axis when facing within about 25
    // degrees of anti-parallel; anything looser falls through to the
    // bounding-box fallback below.
    const double minOpposingConnectorDot = -0.9;
    double bestDot = minOpposingConnectorDot;
    for (int i = 0; i < part.Connectors.Count; i++)
    {
        Part.Connector a = part.Connectors[i];
        if ((a.Flags & (Part.Connector.Flag.ToSurface | Part.Connector.Flag.FromSurface)) != 0)
            continue;
        double3 facingA = new double3(1.0, 0.0, 0.0).Transform(a.Asmb2ParentAsmb);
        for (int j = i + 1; j < part.Connectors.Count; j++)
        {
            Part.Connector b = part.Connectors[j];
            if ((b.Flags & (Part.Connector.Flag.ToSurface | Part.Connector.Flag.FromSurface)) != 0)
                continue;
            double dot = double3.Dot(facingA, new double3(1.0, 0.0, 0.0).Transform(b.Asmb2ParentAsmb));
            if (dot < bestDot)
            {
                bestDot = dot;
                bestA = a;
                bestB = b;
            }
        }
    }
    if (bestA != null && bestB != null)
    {
        double3 direction = (bestA.PositionParentAsmb - bestB.PositionParentAsmb).NormalizeOrZero();
        if (direction.X != 0.0 || direction.Y != 0.0 || direction.Z != 0.0)
            return (bestB.PositionParentAsmb, direction);
    }
    (double3 min, double3 max) = part.BoundingBoxPartAsmb;
    double3 center = min.X <= max.X ? (min + max) * 0.5 : double3.Zero;
    return (center, new double3(1.0, 0.0, 0.0));
}
```

2. **Mirror a previous point.** Bring the previous point into the part frame with the inverse of `MatrixAsmb2Ego`, keep the along-axis component and negate the perpendicular one:

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

3. **Split a vector into axial and radial.** Rotate the ECL delta back into the vehicle frame with the inverse of `Vehicle.Asmb2Ego` and use X as axial:

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

   In the editor, `EditingSpace.Asmb2Ecl` plays the role of `Asmb2Ego`. The split is only defined when both points belong to parts of the same vehicle (or both are editor parts).

## Complete example

- [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L462-L572)[^ksa-measuretools-mappicker] - rim centres, the mirror candidate and `GetPartAxis`.
- [MeasureTools/Features/Measure/Measurement.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Measurement.cs#L97-L130)[^ksa-measuretools-measurement] - `TryGetAxialRadialMeters`.

## Variations and gotchas

- **Symmetry is a guess for odd parts.** A part with one stack connector, or stack connectors not on part-frame X, mirrors across part-frame X through the bounding-box centre, which may not be its axis.
- **Different vehicles.** A ruler between two vehicles has no shared frame; report plain distance.
- **Surface connectors.** Always filter on both surface flags, or a radial strut attach point is treated as the stack end.
- **Rotating deltas.** Rotate the delta with the inverse quaternion; rotating positions (not deltas) drags the vehicle origin into the result.

## See also

- [Overview](overview.md); [Article 4](get-part-points-in-vehicle-and-world-frames-4.md) for rim centres
- [ksa-measuretools/cad-style-snap-tiers](../../../../mods/ksa-measuretools/features/cad-style-snap-tiers.md)

[^ksa-content-corefueltankaassets]: current/Content/Core/CoreFuelTankAAssets.xml (game content, build 2026.10.10.5554)
[^ksa-vehicleeditor]: current/decomp/KSA/VehicleEditor.cs (decompiled, build 2026.10.10.5554)
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksa-measuretools-mappicker]: KSA-MeasureTools: MeasureTools/Features/Measure/MapPicker.cs
[^ksa-measuretools-measurement]: KSA-MeasureTools: MeasureTools/Features/Measure/Measurement.cs
