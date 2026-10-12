---
type: How-To
title: Locate attach nodes and part centres with Connector.PositionEgo and PositionVehicleAsmb
description: Project a vehicle's connectors and part bounding-box centres to screen pixels and vehicle assembly coordinates without a render camera
category: vehicles-and-parts
group: part-geometry
topic: get-part-points-in-vehicle-and-world-frames
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-measuretools/features/part-mesh-raycast-picking]
techniques: [screen-space-picking]
hooks: [harmony-prefix KSA.Program.OnMouseButton, starmap StarMapAfterGui]
lifecycle: [frame:input, frame:draw-ui]
game_types:
  - KSA.Part
  - KSA.PartConnectorInstance
  - KSA.Vehicle
  - KSA.EngineController
  - KSA.MeshReference
  - KSA.PartModel
  - KSA.MeshViewModule
tags: [parts, connectors, attach-nodes, picking, screen-space]
---
Outcome: given a vehicle, get every attach node and part centre in the vehicle assembly frame and in screen pixels, so a mod can snap a cursor to them, label them or
draw markers. No render camera object is needed beyond `Camera.GetPositionEgo` and `Camera.EgoToScreen`.

Prerequisite: StarMap (`[StarMapAfterGui]` for the preview) and, optionally, Harmony for a click hook. Demonstrated by
[ksa-measuretools/part-mesh-raycast-picking](../../../../mods/ksa-measuretools/features/part-mesh-raycast-picking.md), which uses exactly this scan for its "nodes and centres" tier.

## Background: how the game does it

Each `Part` has `Connectors` (a `List<Part.Connector>`) and a `BoundingBoxPartAsmb` `(Min, Max)` pair [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L833)[^ksa-part]. A connector keeps `PositionParentAsmb` (relative to its part) and
exposes `PositionVehicleAsmb` and `PositionEgo(in matrixVehicleAsmb2Ego)`:

```csharp
// current/decomp/KSA/Part.cs L256,L264-L267
public double3 PositionVehicleAsmb => Parent.PositionParentAsmb + PositionParentAsmb.Transform(Parent.Asmb2ParentAsmb);
// ...
public double3 PositionEgo(ref readonly double4x4 matrixVehicleAsmb2Ego)
{
    return PositionVehicleAsmb.Transform(matrixVehicleAsmb2Ego);
}
```

Two consequences. First, neither expression applies the parent part's `Scale`: a connector on a scaled part is not moved by the part scale (the part's own `Part.MatrixAsmb2Ego` [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1213-L1224) does include `Scale`).
Second, the formula uses the parent's `PositionParentAsmb` and `Asmb2ParentAsmb`, so for a connector on a full part it is a vehicle-frame position; do not assume the same for
connectors on a subpart. The part centre has no property of its own: the mod derives it from `BoundingBoxPartAsmb`.

## Lifecycle and timing

MeasureTools runs the full scan on the placement click in a Harmony prefix on `Program.OnMouseButton` (`frame:input`) and a throttled preview scan (every 45 ms) in `[StarMapAfterGui]`
(`frame:draw-ui`) [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L181-L310)[^ksa-measuretools-mappicker]. Part transforms are the finished physics step's values at both points, since `Apply*Solvers` ran in `Program.PrepareFrame` earlier in the frame.
See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md). The preview must tolerate a one-frame-old state; the click path has no such issue.

## Steps

1. **Pick the vehicles worth scanning.** Skip vehicles that project smaller than about 24 px (their `MeanRadius` is the bounding-sphere radius about the centre of mass) and compute the
   vehicle matrix from the camera's ego position of the vehicle:

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

2. **Scan connectors and centres in screen space.** For each part project the bounding-box centre through `part.MatrixAsmb2Ego(in matrix)` and every connector through
   `connector.PositionEgo(in matrix)`; keep whichever is nearest the cursor within 16 px. The scan deliberately ignores occlusion and does not require the cursor to be on the mesh:

```csharp
// MeasureTools/Features/Measure/MapPicker.cs L613-L652
    for (int i = 0; i < parts.Length; i++)
    {
        Part part = parts[i];
        (double3 min, double3 max) = part.BoundingBoxPartAsmb;
        // Meshless parts (e.g. an EVA kitten's root) have a degenerate box.
        if (min.X <= max.X)
        {
            double3 centerLocal = (min + max) * 0.5;
            float2 s = camera.EgoToScreen(centerLocal.Transform(part.MatrixAsmb2Ego(in matrixVehicleAsmb2Ego)));
            if (!float.IsNaN(s.X) && !float.IsNaN(s.Y))
            {
                float d = float2.Distance(s, mouseViewport);
                if (d < best.ScreenDist)
                {
                    best.Part = part;
                    best.Position = centerLocal;
                    best.IsVehicleAsmb = false;
                    best.Label = part.DisplayName + " center";
                    best.ScreenDist = d;
                }
            }
        }
        for (int j = 0; j < part.Connectors.Count; j++)
        {
            Part.Connector connector = part.Connectors[j];
            float2 s = camera.EgoToScreen(connector.PositionEgo(in matrixVehicleAsmb2Ego));
            if (float.IsNaN(s.X) || float.IsNaN(s.Y))
                continue;
            float d = float2.Distance(s, mouseViewport);
            if (d < best.ScreenDist)
            {
                best.Part = part;
                best.Position = connector.PositionVehicleAsmb;
                best.IsVehicleAsmb = true;
                best.Label = part.DisplayName + " node '" + connector.Id + "'";
                best.ScreenDist = d;
            }
        }
    }
}
```

3. **Keep the point in the frame that is cheapest to resolve.** The scan returns connector positions in the vehicle assembly frame (`IsVehicleAsmb = true`) and centres in the part
   frame. [Article 2](get-part-points-in-vehicle-and-world-frames-2.md) shows how MeasureTools converts either into an ECL position every frame.

## Complete example

Minimal, adapted: log every node of a vehicle with both its vehicle-frame position and its pixel position.

```csharp
// adapted from KSA-MeasureTools MapPicker.ScanFlightVehicles / ScanPartFeatures
static void LogConnectors(Camera camera, Vehicle vehicle)
{
    double3 vehiclePosEgo = camera.GetPositionEgo(vehicle);
    double4x4 matrixVehicleAsmb2Ego = vehicle.GetMatrixAsmb2Ego(vehiclePosEgo);
    foreach (Part part in vehicle.Parts.Parts)
    {
        foreach (Part.Connector connector in part.Connectors)
        {
            double3 asmb = connector.PositionVehicleAsmb;
            float2 px = camera.EgoToScreen(connector.PositionEgo(in matrixVehicleAsmb2Ego));
            Console.WriteLine($"{part.DisplayName} '{connector.Id}' asmb={asmb} px={px}");
        }
    }
}
```

Original: [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L574-L652).

## Variations and gotchas

- **No part scale.** `Connector.PositionVehicleAsmb` ignores the part's `Scale`. To get a position that follows scale, convert once with the inverse of `part.MatrixAsmb2VehicleAsmb`
  into the part frame and re-resolve with the forward matrix (article 2 does this).
- **Occlusion.** A node on the far side of a vehicle can win if it projects closest to the cursor.
- **Meshless parts.** `BoundingBoxPartAsmb` is degenerate (`Min.X > Max.X`) for parts without a mesh, such as an EVA kitten root; guard before using the centre.
- **Editor.** In the editor, part matrices are recomputed on every call and `Program.Editor` is set; scan `EditingSpace.AllParts` with `EditingSpace.GetMatrixAsmb2Ego(camera)` instead of the
  system's vehicles, or the edited vehicle's original is scanned twice.
- **Gimbals.** Engine gimbal deflection is not written to the part transform, so nozzles measure at their neutral position.

## See also

- [Overview](overview.md); [Article 2](get-part-points-in-vehicle-and-world-frames-2.md), [Article 3](get-part-points-in-vehicle-and-world-frames-3.md)
- [ksa-measuretools/part-mesh-raycast-picking](../../../../mods/ksa-measuretools/features/part-mesh-raycast-picking.md)
- [Ray cast against part meshes and terrain](../../../physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md) for hit points on the hull

[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksa-measuretools-mappicker]: KSA-MeasureTools: MeasureTools/Features/Measure/MapPicker.cs
