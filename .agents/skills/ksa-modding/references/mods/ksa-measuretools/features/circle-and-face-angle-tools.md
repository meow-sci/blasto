---
type: Mod Feature
title: Circle and face-angle tools on part geometry
description: One click on a circular part edge yields a fitted center and rim anchor pair for diameter, radius and circumference; two hull hits yield the live angle between their part-frame normals
mod: ksa-measuretools
category: vehicles-and-parts
techniques: [mesh-analysis, imgui-drawlist]
hooks: [harmony-prefix KSA.Program.OnMouseButton, starmap StarMapAfterGui]
lifecycle: [frame:input, frame:draw-ui]
game_types: [KSA.Part, KSA.Vehicle, KSA.VehicleEditingSpace, KSA.Camera, KSA.MeshReference]
tags: [parts, circle, diameter, face-angle, normals, engine-cant, cad, overlay]
---

Two part-only tools build on the mesh picker. **Circle**: one click on a tank rim or an engine bell
exit reads diameter, radius and circumference from a fitted circle that tracks the part. **Face
angle**: two clicks on part surfaces read the angle between the surface normals (engine cant, fin
alignment), live while the vehicle rotates.[^mt-readme]

## How it works

### Circle

Circle is the only mode that settles in one click but needs two anchors, so it bypasses the pending
flow: the mouse prefix calls `MapPicker.PickCircle` and then `MeasureState.AddCircle(center,
rim)`.[^mt-patch-mouse][^mt-state]

`PickCircle` runs the shared mesh-hit scan without the point-feature scan, fetches the hit subpart's
cached circles, and for each circle computes the ring point nearest the hit: project
`hit - center` onto the circle plane, normalize, scale by the radius. The circle whose ring point
projects within 14 px of the cursor wins. It returns two part anchors on the hit subpart: the fitted
center, carrying the circle's plane normal, and the rim point.[^mt-mappicker]

`Measurement.CircleRadiusMeters` is the live distance between the two resolved anchors, so the
value follows part scale and survives re-homing; diameter and circumference derive from it.[^mt-measurement]
The overlay draws the circle in 3D: from the resolved center, the radius vector (rim minus center)
and the resolved normal it builds a second in-plane axis by a cross product and samples 48 points,
breaking the polyline wherever a sample projects behind the camera. The preview draws the candidate
ring the same way with a live "d 2.50 m" label.[^mt-overlay]

### Face angle

`PickFaceAngle` takes the raw surface hit only; snapping to a node or vertex would move the sample off
the face whose normal is being measured. The anchor stores the hit point and the unit hit normal in the
subpart frame. Two such anchors form the measurement.[^mt-mappicker]

`Anchor.ResolveNormalEcl` rotates the stored normal with `Part.Asmb2VehicleAsmb` and then the
vehicle's `Asmb2Ego` (or the editing space's `Asmb2Ecl`), rotation only.[^mt-anchor]
`Measurement.FaceAngleRadians` is `acos(clamp(dot(a, b)))`, so 0 means parallel faces facing the same
way, and NaN ("undefined") when either normal cannot resolve, which only happens for an editor
anchor while no editor exists.

The overlay draws each normal as an arrow whose screen length stays about 40 px at any zoom: the
world length is `40 / camera.GetObjectDiameterPixels(1.0, distance)`, i.e. 40 px divided by pixels
per meter at that depth.[^mt-overlay][^ksa-camera]

### Where the normal comes from

The hit normal is what `Part.RayCastEgo` returns: the mesh's vertex normal at the first vertex of the
hit triangle, read from `HostMesh.GetVertexSpan<float3>(MeshAttribute.Normal)` through the index
buffer.[^ksa-part] On flat faces with split normals (fins, panels) that is the face normal; on a
smooth-shaded cylinder it is a smoothed vertex normal, so two samples on a tank wall differ by up to
one facet's angle depending on where they land.

## Lifecycle and timing

Both tools pick on the placement click (`Program.OnMouseButton` prefix, `frame:input`) and preview
and draw in `[StarMapAfterGui]` (`frame:draw-ui`). Values are recomputed from the current part and
vehicle rotations every time they are drawn or copied, so a cant angle reads live during a roll.

## Key code

| Path | Role |
|---|---|
| [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L382-L454) | `PickCircle`, `PickFaceAngle`, `NormalOrNull` |
| [MeasureTools/Features/Measure/Measurement.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Measurement.cs#L62-L95) | Circle values, face angle |
| [MeasureTools/Features/Measure/Anchor.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Anchor.cs#L134-L153) | `ResolveNormalEcl` |
| [MeasureTools/Features/Measure/MeasureOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureOverlay.cs#L156-L244) | 3D circle and normal-arrow drawing |
| [MeasureTools/Features/Measure/MeasureMode.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureMode.cs) | The five modes |

## Snippets

```csharp
// MeasureTools/Features/Measure/MapPicker.cs L400-L432
double4x4 matrixAsmb2Ego = hit.SubPart.MatrixAsmb2Ego(in hit.Matrix);

int bestIndex = -1;
double3 bestRimLocal = default;
float bestDist = CircleSnapRadiusPx;
for (int i = 0; i < features.Circles.Length; i++)
{
    MeshFeatureCache.CircleFeature circle = features.Circles[i];
    // The ring point nearest the hit, in the subpart frame.
    double3 d = hit.LocalPos - circle.Center;
    double3 planar = d - circle.Normal * double3.Dot(d, circle.Normal);
    double3 dir = planar.NormalizeOrZero();
    if (dir.X == 0.0 && dir.Y == 0.0 && dir.Z == 0.0)
        continue;
    double3 rimLocal = circle.Center + dir * circle.Radius;
    float2 s = camera.EgoToScreen(rimLocal.Transform(matrixAsmb2Ego));
    if (float.IsNaN(s.X) || float.IsNaN(s.Y))
        continue;
    float dist = float2.Distance(s, mouseViewport);
    if (dist < bestDist)
    {
        bestDist = dist;
        bestIndex = i;
        bestRimLocal = rimLocal;
    }
}
if (bestIndex < 0)
    return false;
MeshFeatureCache.CircleFeature best = features.Circles[bestIndex];
string partName = hit.FullPart.DisplayName;
center = MakePartAnchor(hitVehicle, hit.SubPart, best.Center, partName + " rim center", best.Normal);
rim = MakePartAnchor(hitVehicle, hit.SubPart, bestRimLocal, partName + " rim");
return true;
```

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

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `Part.RayCastEgo` (near position and normal), `RayCastEgoSubPart` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2574-L2609) | Hit point and vertex normal |
| `Part.MatrixAsmb2Ego`, `Asmb2VehicleAsmb`, `DisplayName` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L724-L738) | Projecting circles; rotating normals |
| `Camera.GetObjectDiameterPixels`, `EclToScreen`, `EgoToScreen`, `PositionEcl` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L807-L811) | Constant-screen-length arrows; projection |
| `VehicleEditor.TryComputeAttachedMountQuat` | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L3372-L3425) | Stock precedent for rotating a part-frame hit normal by `Asmb2VehicleAsmb` (L3422) |

## Pitfalls and version notes

- Circle needs a cached circle on the hit subpart; a rim whose loop is not a clean closed chain of
  feature edges (or lives in a second mesh primitive) gives no candidate and the click is consumed
  without a result (logged in DEBUG builds).
- Face-angle normals are vertex normals of the hit triangle, not exact face normals on curved
  surfaces.
- Normals are rotated without part scale; a non-uniformly scaled part would need the inverse
  transpose for exact normals.
- Settled measurements rarely show "undefined": `Prune` runs before the window and overlay in the
  same AfterGui call, so an anchor whose owner changed earlier in the frame is already re-homed when
  it is drawn. The throttled preview cache is not pruned and can hold an editor anchor for up to one
  pick interval after the editor closes; that anchor resolves to NaN, so the overlay's validity check
  skips the preview instead of labelling it.

## See also

- [Overview](../overview.md)
- [Mesh feature edges and rim circles](./mesh-feature-edges-and-circle-fitting.md)
- [Part picking](./part-mesh-raycast-picking.md)
- [Overlay rendering](./overlay-drawlist-rendering.md)
- [Surface great-circle tool](./surface-great-circle-tool.md)
- [Lines, curves, rings and plated labels for measurements](../../../howto/rendering/overlays/draw-markers-and-annotations-on-world-objects/draw-markers-and-annotations-on-world-objects-2.md) - how-to article demonstrating this feature
- [Analyse a part mesh: MeshView positions, weld, dihedral feature edges and Kasa circle fit](../../../howto/vehicles-and-parts/part-geometry/get-part-points-in-vehicle-and-world-frames/get-part-points-in-vehicle-and-world-frames-4.md) - how-to article demonstrating this feature

[^mt-mappicker]: MeasureTools/Features/Measure/MapPicker.cs
[^mt-measurement]: MeasureTools/Features/Measure/Measurement.cs
[^mt-anchor]: MeasureTools/Features/Measure/Anchor.cs
[^mt-overlay]: MeasureTools/Features/Measure/MeasureOverlay.cs
[^mt-state]: MeasureTools/Features/Measure/MeasureState.cs
[^mt-patch-mouse]: MeasureTools/Patches/Patch_MouseButton.cs
[^mt-readme]: KSA-MeasureTools README.md
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
