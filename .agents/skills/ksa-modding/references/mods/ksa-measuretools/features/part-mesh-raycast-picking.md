---
type: Mod Feature
title: Pick hull points, attach nodes and part centers on vehicles under the cursor
description: Gates vehicles by projected size and bounding sphere, raycasts parts with the stock Part.RayCastEgo over each MeshViewModule triangle list, and scans attach nodes and part centers in screen space, optionally reusing the stock hover
mod: ksa-measuretools
category: vehicles-and-parts
techniques: [mesh-raycast, screen-space-picking]
hooks: [harmony-prefix KSA.Program.OnMouseButton, starmap StarMapAfterGui]
lifecycle: [frame:input, frame:draw-ui]
game_types:
  - KSA.Part
  - KSA.Part.Connector
  - KSA.MeshViewModule
  - KSA.MeshReference
  - KSA.Ray
  - KSA.Vehicle
  - KSA.PartTree
  - KSA.ViewportPartPicker
  - KSA.CursorTarget
  - KSA.Camera
  - KSA.VehicleEditor
  - KSA.VehicleEditingSpace
tags: [parts, picking, raycast, mesh, attach-nodes, connectors, vehicles, centimeter-precision]
---

When the camera is close enough that a vehicle covers more than a couple of dozen pixels, clicks snap
to individual parts with centimeter precision: attach nodes and part centers first, then mesh
vertices and edges, and otherwise the exact hull point under the cursor. It works on any vehicle in
view, including two vehicles flying side by side, and in the editor on the craft being built and the
parts in the hand.[^mt-readme]

This concept covers how the mod finds the part and the hit point. The CAD snap tiers on top of it
(edges, midpoints, rim centers, mirror) and the anchor that keeps the point attached are separate
concepts.

## How it works

### Which decomp types expose part geometry

The mod never touches the render side. `PartModel` holds per-viewport GPU draw data (draw command
vectors, descriptor sets) and `PartInstance` is the serialized save/template description of a
part tree; neither is used.[^ksa-partmodel][^ksa-partinstance] Geometry comes from the runtime
`KSA.Part`:

| Member | What it gives |
|---|---|
| `Part.Modules.Get<MeshViewModule>()[0].MeshView` | The `MeshReference` of a (sub)part's visual mesh |
| `MeshReference.PositionCompare` | `PositionsCompare[0]`: the first primitive's positions unrolled through the index buffer (three entries per triangle), as `double3` in the subpart's local frame |
| `MeshReference.HostMesh` | Host-side `MeshAsset` with the normal attribute and index buffer |
| `Part.BoundingBoxPartAsmb` | `(Min, Max)` box in the part frame (degenerate for meshless parts such as an EVA kitten root) |
| `Part.Connectors` (`Part.Connector`) | Attach nodes: `Id`, `Flags`, `PositionParentAsmb`, `Asmb2ParentAsmb`, `PositionVehicleAsmb`, `PositionEgo(in matrix)` |
| `Part.MatrixAsmb2Ego(in matrixVehicleAsmb2Ego)`, `MatrixAsmb2VehicleAsmb`, `FullPart`, `PartParent`, `InstanceId`, `Tree`, `DisplayName` | Transforms and identity |

`MeshViewModule.CreateComponents` builds the module from the part template with
`template.MeshView.Get()`, which returns the shared `ModLibrary` instance, so all parts of one type
share a `MeshReference`.[^ksa-meshviewmodule][^ksa-meshreference]

### The stock raycast it reuses

`Part.RayCastEgo(in matrixVehicleAsmb2Ego, ray, out minDistance, ..., out nearPosAsmb, out
nearNormalAsmb, ..., out closestSubPart, ...)` first tests a bounding sphere around the part, then
calls `RayCastEgoSubPart` on each of the part's `SubParts`. That tests the mesh's
`BoundingSphereRadius`, then runs `Ray.RaycastWatertight(PositionCompare, in MatrixAsmb2Ego, ...)`,
a linear loop over every triangle that returns the nearest `t` and the index of the hit triangle's
first vertex. The hit position is mapped back into the subpart frame with the inverted matrix, and
the "normal" is the mesh's stored vertex normal at that first vertex.[^ksa-part][^ksa-ray] This is
the same call `Vehicle.UpdateHighlight` uses for the stock hover.[^ksa-vehicle]

### Flight: which vehicles to test

`MapPicker.TryGetMeshHit` normalizes the cursor ray (stock does the same before part raycasts; the
watertight test needs a unit direction for metric distances), then `ScanFlightVehicles` walks the
system's vehicles:[^mt-mappicker]

1. Skip vehicles whose bounding sphere (`Vehicle.MeanRadius`, centered on the center of mass, which
   is what `camera.GetPositionEgo(vehicle)` points at) projects smaller than 24 px.
2. Build `matrixVehicleAsmb2Ego = vehicle.GetMatrixAsmb2Ego(vehiclePosEgo)`.
3. Optionally scan point features (below), attributing the winner to this vehicle.
4. Ray-test a sphere of 1.1 times `MeanRadius`; on a hit, call `part.RayCastEgo` for every part and
   keep the globally nearest hit (`FullPart`, `SubPart`, local position, local normal, distance,
   and the vehicle matrix for later transforms).

### The hover shortcut (preview only)

For the throttled hover preview the mod can reuse the stock part picker. When the viewport is the
hit-test viewport, the camera is in `Orbit` mode, the cursor is normal and no modal is open (the same
conditions `Vehicle.UpdateHighlight` checks), it reads `viewport.PartPicker.Part` and re-raycasts only
that part. If stock reports nothing and no feature scan is requested, it returns immediately; if the
single re-raycast misses (cursor drift at a silhouette, an EVA kitten's bounding-sphere hover
shadowing a vehicle behind it), it falls back to the full scan. Placement clicks always do the full
scan.

### Point features in screen space

`ScanPartFeatures` projects, for every part, the bounding-box center
(`centerLocal.Transform(part.MatrixAsmb2Ego(in m))`) and every connector (`connector.PositionEgo(in
m)`), and keeps the one nearest the cursor within 16 px. It is deliberately not ray-gated, so a node
on the silhouette snaps even when the cursor is just off the mesh, and it ignores occlusion
(features are sparse and the preview shows the winner). The author cites the editor's connector snap
(`VehicleEditor.HandleConnectorConnections`, a screen-space proximity test) as the model. Connector
positions are stored in the vehicle-asmb frame and converted to the part frame once by inverting
`MatrixAsmb2VehicleAsmb`, because `Connector.PositionVehicleAsmb` applies rotation and translation
but not the part scale.[^mt-anchor][^ksa-part]

Labels are built from `part.DisplayName` ("TankA node 'top'", "TankA center", "TankA surface"), and
the overlay draws faint dots on the hovered part's nodes and center so the snappable features are
visible before the click.[^mt-overlay]

### Editor

With `Program.Editor` set, the scan covers `EditingSpace.AllParts` plus every tree in
`UnattachedPartTrees` (parts in the hand) with `EditingSpace.GetMatrixAsmb2Ego(camera)`, and never
the system's vehicles: the original of an edited vehicle is still in the system list and would be
picked twice.[^ksa-vehicleeditor]

## Lifecycle and timing

| Path | Hook | State |
|---|---|---|
| Click | `Program.OnMouseButton` prefix (`frame:input`) | Part transforms just applied from the physics step; full scan |
| Preview | `[StarMapAfterGui]` (`frame:draw-ui`), every 45 ms or on a state change | Shortcut allowed |

The stock picker is reset and refilled for the main viewport inside `CelestialSystem.OnDrawUi`
(`Universe.OnDrawUi`), which `Program.OnDrawUiThreadSafe` calls after the AfterGui hook, so during
AfterGui `PartPicker.Part` is the previous frame's result, not this frame's as the source comment
says.[^ksa-celestialsystem][^ksa-program] The fallback covers only half of the staleness: when the
stale picker names a part the single re-raycast misses, the full scan runs. When the stale picker is
`null` (the cursor has just moved onto a vehicle), `TryGetMeshHit` returns no hit without scanning,
and with the feature scan on it still skips every mesh raycast, so the preview shows no hull, vertex
or edge snap for that pick; the next pick, at most 45 ms later, sees the filled picker. Placement
clicks are unaffected because they never take the shortcut. In the editor the shortcut never
applies: the editor branch returns first, and `OnDrawUiThreadSafe` does not call `Universe.OnDrawUi`
while `EditorFlag` is set, so the main viewport's picker is not refilled there.

Engine gimbals do not move the hit point: `GimbalController` only reads the part matrix and no gimbal
code writes `Part.Asmb2ParentAsmb`, which matches the README's "nozzles measure at their neutral
position".[^ksa-gimbalcontroller] The runtime writers of that property are keyframe animations
(`KeyframeAnimationModule`), solar trackers (`SolarTracker`), part-tree re-rooting (`PartTree`) and the
editor, and its setter resets the cached part matrices, so anchors on animated parts follow
them.[^ksa-part]

## Key code

| Path | Role |
|---|---|
| [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L181-L310) | `TryGetMeshHit`, `ScanFlightVehicles`, hover shortcut |
| [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L574-L652) | `RaycastPart`, `ScanPartFeatures` |
| [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L37-L55) | Pixel thresholds per tier |
| [MeasureTools/Features/Measure/MeasureOverlay.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureOverlay.cs#L592-L634) | Feature dots on the hovered part |

## Snippets

```csharp
// MeasureTools/Features/Measure/MapPicker.cs L585-L598
private static void RaycastPart(Part part, ref readonly double4x4 matrixVehicleAsmb2Ego, Ray ray, ref PartHit hit)
{
    if (part.RayCastEgo(in matrixVehicleAsmb2Ego, ray, out double minDistance, out _,
            out double3 nearLocal, out double3 nearNormal, out _, out _, out Part? closestSubPart, out _)
        && minDistance > 0.0 && minDistance < hit.Distance && closestSubPart != null)
    {
        hit.FullPart = part;
        hit.SubPart = closestSubPart;
        hit.LocalPos = nearLocal;
        hit.NormalLocal = nearNormal;
        hit.Distance = minDistance;
        hit.Matrix = matrixVehicleAsmb2Ego;
    }
}
```

```csharp
// MeasureTools/Features/Measure/MapPicker.cs L259-L275
foreach (Astronomical astronomical in system.All.AsSpan())
{
    if (astronomical is not Vehicle vehicle)
        continue;
    PartTree? tree = vehicle.Parts;
    if (tree == null || tree.Parts.Length == 0)
        continue;
    double3 vehiclePosEgo = camera.GetPositionEgo(vehicle);
    // Vehicle.MeanRadius is the bounding-sphere radius about the CoM, the
    // same point vehiclePosEgo refers to.
    double radius = vehicle.MeanRadius;
    if (!(radius > 0.0))
        continue;
    if (camera.GetObjectDiameterPixels(radius * 2.0, vehiclePosEgo.Length()) < PartVehicleMinDiameterPx)
        continue;

    double4x4 matrixVehicleAsmb2Ego = vehicle.GetMatrixAsmb2Ego(vehiclePosEgo);
```

```csharp
// current/decomp/KSA/Part.cs L2582-L2605 (inside RayCastEgoSubPart)
Span<MeshViewModule> span = Modules.Get<MeshViewModule>();
if (span.IsEmpty)
{
    return false;
}
MeshReference meshView = span[0].MeshView;
// ...
double3[] positionCompare = meshView.PositionCompare;
double4x4 vertexOffset = MatrixAsmb2Ego(in matrixVehicleAsmb2Ego);
if (!ray.RaycastWatertight(positionCompare, in vertexOffset, out var nearT, out var nearClosestVertexIndex, out var farT, out var farClosestVertexIndex))
{
    return false;
}
minDistance = nearT;
maxDistance = farT;
double4x4.Invert(vertexOffset, out var result);
nearIntersectionPositionAsmb = (ray.Origin + ray.Direction * nearT).Transform(result);
nearIntersectionNormalAsmb = double3.Unpack(in meshView.HostMesh.GetVertexSpan<float3>(MeshAttribute.Normal)[meshView.HostMesh.IndexBuffer.ToSpan<int>()[nearClosestVertexIndex]]);
```

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `Part.RayCastEgo`, `RayCastEgoSubPart`, `SubParts`, `MatrixAsmb2Ego`, `MatrixAsmb2VehicleAsmb`, `BoundingBoxPartAsmb`, `Connectors`, `FullPart`, `DisplayName` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2534-L2609) | Mesh raycast and part transforms |
| `Part.Connector` (`PositionEgo`, `PositionVehicleAsmb`, `Flags`, `Id`) | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L191-L270) | Attach-node positions |
| `MeshViewModule.MeshView` | [current/decomp/KSA/MeshViewModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshViewModule.cs) | Mesh of a subpart |
| `MeshReference.PositionCompare`, `HostMesh`, `BoundingSphereRadius`, `Load` | [current/decomp/KSA/MeshReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshReference.cs#L55-L118) | Index-unrolled triangle positions |
| `Ray.RaycastWatertight` | [current/decomp/KSA/Ray.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Ray.cs#L207-L279) | Watertight ray-triangle loop |
| `Vehicle.GetMatrixAsmb2Ego`, `MeanRadius`, `Parts`, `UpdateHighlight` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2903-L2927) | Vehicle frame; stock hover reference |
| `ViewportPartPicker.Part`, `TryOffer`, `Reset` | [current/decomp/KSA/ViewportPartPicker.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportPartPicker.cs) | Hover shortcut |
| `Camera.GetPositionEgo`, `GetObjectDiameterPixels`, `EgoToScreen` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L236-L245) | Size gate and projections |

## Pitfalls and version notes

- `PositionCompare` is only the first glTF primitive. Multi-primitive (multi-material) meshes are
  hit-tested and feature-scanned on primitive 0 only, by stock and by the mod alike.
- `RaycastWatertight` loops over every triangle; there is no BVH. That is why the preview is
  throttled and vehicles are gated by projected size and bounding sphere first.
- The returned normal is a vertex normal of the hit triangle, not the geometric face normal; on
  smooth-shaded surfaces it varies with which triangle corner is first.
- `Part.MatrixAsmb2Ego`, `MatrixAsmb2VehicleAsmb` and `Asmb2VehicleAsmb` are cached in flight and
  recomputed every call while `Program.Editor != null`, which is what makes editor drags track live.
- The feature scan ignores occlusion: a node on the far side of a vehicle can win if it projects
  closest to the cursor.

## See also

- [Overview](../overview.md)
- [CAD-style snap tiers](./cad-style-snap-tiers.md)
- [Mesh feature edges and rim circles](./mesh-feature-edges-and-circle-fitting.md)
- [Part-local anchors that survive staging, docking and the editor](./part-anchor-rehoming.md)
- [Editor measuring](./editor-measuring.md)
- [Cursor ray against every vehicle, plus the stock hover picker](../../../howto/physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/ray-cast-against-vessel-meshes-and-terrain-2.md) - how-to article demonstrating this feature
- [Locate attach nodes and part centres with Connector.PositionEgo and PositionVehicleAsmb](../../../howto/vehicles-and-parts/part-geometry/get-part-points-in-vehicle-and-world-frames/get-part-points-in-vehicle-and-world-frames-1.md) - how-to article demonstrating this feature

[^mt-mappicker]: MeasureTools/Features/Measure/MapPicker.cs
[^mt-anchor]: MeasureTools/Features/Measure/Anchor.cs
[^mt-overlay]: MeasureTools/Features/Measure/MeasureOverlay.cs
[^mt-readme]: KSA-MeasureTools README.md
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-meshviewmodule]: KSA.MeshViewModule (decompiled, build 2026.10.10.5554)
[^ksa-meshreference]: KSA.MeshReference (decompiled, build 2026.10.10.5554)
[^ksa-ray]: KSA.Ray (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-partmodel]: KSA.PartModel (decompiled, build 2026.10.10.5554)
[^ksa-partinstance]: KSA.PartInstance (decompiled, build 2026.10.10.5554)
[^ksa-gimbalcontroller]: KSA.GimbalController (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditor]: KSA.VehicleEditor (decompiled, build 2026.10.10.5554)
