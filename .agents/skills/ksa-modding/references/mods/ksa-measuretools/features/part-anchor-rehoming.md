---
type: Mod Feature
title: Part-local anchors that survive staging, docking and the editor boundary
description: Part anchors keep their point in the part's asmb frame, resolve to ECL with a camera-free mirror of Vehicle.GetMatrixAsmb2Ego, and are re-homed by finding the same Part object in the editor trees or any vehicle
mod: ksa-measuretools
category: vehicles-and-parts
techniques: [frame-relative-anchors, object-identity-tracking]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Part
  - KSA.PartTree
  - KSA.Vehicle
  - KSA.VehicleEditor
  - KSA.VehicleEditingSpace
  - KSA.CelestialSystem
  - KSA.Camera
tags: [parts, anchors, staging, docking, decoupling, editor, transforms, identity]
---

A point on a part stays on that part. Measurements follow the vehicle as it moves and rotates,
follow the part's own motion inside the vehicle, survive staging and docking (watch a separation
distance grow after decoupling), and cross the editor boundary in both directions. Only destroying
the part removes the measurement.[^mt-readme] The technique has two halves: resolve a part-local point
without needing the render camera, and track part ownership by object identity.

## How it works

### Storing and resolving a part point

`Anchor.AtPartLocal(vehicle, part, offsetPartAsmb, label, normal)` stores the point in the part's
local asmb frame. The stored `Part` is the mesh-owning subpart for vertex, edge and surface picks and
the full part for center and connector picks. Connector positions, which the game computes in the
vehicle-asmb frame, are converted once with the inverse of `part.MatrixAsmb2VehicleAsmb`, which
round-trips exactly through the resolve and sidesteps `Connector.PositionVehicleAsmb` ignoring part
scale.[^mt-anchor][^ksa-part]

Resolution mirrors the game's render transform without a camera (the math, as in the snippet
below): the offset goes to the vehicle-asmb frame through `part.MatrixAsmb2VehicleAsmb`, then the
ECL point is `vehicle.GetPositionEcl()` plus `(posVehicleAsmb - vehicle.CenterOfMassAsmb)` rotated by
`vehicle.Asmb2Ego`.

`Vehicle.GetMatrixAsmb2Ego(bodyPositionEgo)` is a rotation by `Asmb2Ego` followed by a translation to
`bodyPositionEgo - CenterOfMassAsmb.Transform(Asmb2Ego)`, and `Camera.EgoToEcl` is a pure
translation, so the same rotation applied about the vehicle's ECL position gives the ECL
point.[^ksa-vehicle][^ksa-camera] Using the part's live `MatrixAsmb2VehicleAsmb` means deploy
animations and trackers that move a part inside its vehicle are followed.

The editor twin goes through `Program.Editor.EditingSpace`: `PositionEcl + posVehicleAsmb.Transform(Asmb2Ecl)`,
mirroring `VehicleEditingSpace.GetMatrixAsmb2Ego`, which has no center-of-mass term.[^ksa-vehicleeditingspace]
Editor anchors store no owner at all and reach the space through `Program.Editor` at resolve time, so
a stale space can never be resolved; with no editor they return NaN and the overlay skips them.

Surface normals (face-angle and circle anchors) are stored in the part frame and rotated with
`Part.Asmb2VehicleAsmb` then `Vehicle.Asmb2Ego` (or `EditingSpace.Asmb2Ecl`), rotation only, the
same way the editor rotates hit normals.

### Validity by identity

`IsValid(system)` for a flight part anchor requires the stored vehicle to still be the object
`system.Get(vehicle.Id)` returns, and the part's `FullPart` to be the object
`vehicle.Parts.Find(fullPart.InstanceId)` returns. Subparts never move between full parts, so checking
the full part covers a stored subpart. Editor anchors are valid while `Program.Editor` exists and the
full part is found in `EditingSpace.Parts` or in any of `UnattachedPartTrees` (parts in the
hand).[^mt-anchor][^ksa-parttree]

### Re-homing

When `Prune` finds an invalid part anchor it calls `Rehome(system)`, which looks for the same `Part`
object in this order and builds a new anchor with the same offset and normal:[^mt-state]

1. With the editor open: `EditingSpace.Parts`, then each unattached tree, giving an
   `EditorPartPoint`.
2. Every `Vehicle` in `system.All` whose `Parts.Find(InstanceId)` returns the object, giving a
   `PartPoint` whose label is rebuilt from the owner-free `PartLabel` with the new vehicle id.
3. Nothing found: the part is gone, and the pending list or measurement is dropped.

This works because the game moves `Part` objects between owners instead of copying them. The author
lists `Vehicle.CreateVehicle`, `Vehicle.MergeFrom`, `VehicleEditor.Build` and `VehicleEditor.Dispose`
as checked; in the decomp `Vehicle.Split` passes the existing part to `CreateVehicle(..., Part root,
...)`, `VehicleEditingSpace(Vehicle)` wraps the vehicle's own `Parts` tree, and `VehicleEditor.Dispose`
hands `EditingSpace.Parts` to the launched vehicle (`ExistingVehicle.Parts = EditingSpace.Parts`, or
`CreateVehicle` with the editing tree's root for a new craft).[^ksa-vehicle][^ksa-vehicleeditor]
Because the offset is part-local and the launch rebase only rewrites part transforms within the
vehicle, the stored offset transfers unchanged.

## Lifecycle and timing

`Prune` runs at the start of every `[StarMapAfterGui]` call (`frame:draw-ui`), before the window and
overlay resolve anything. The same hook runs while the vehicle editor is open; it is not part of
`VehicleEditor.OnDrawUi`, which runs later in `OnDrawUiThreadSafe`. Every ownership change happens
earlier in the frame, on the main thread, so it is seen on the same frame:[^ksa-program]

| Change | Where the game applies it |
|---|---|
| Staging decouple | `InputEvents.ApplyInputEvents` (`Decoupler.Decouple` -> `Vehicle.Split`), right after `Glfw.PollEvents` (`frame:input`) [current/decomp/KSA/InputEvents.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InputEvents.cs#L457-L466) [current/decomp/KSA/Decoupler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Decoupler.cs#L163-L167) |
| Docking on contact, part failures | Detected on the vehicle worker, applied by `Universe.ApplyVehicleSolvers` -> `VehicleUpdateTask.ApplyResultsToMainThread` -> `PhysicsBubble.ApplyRenderEventsToVehicles` (`frame:apply-solvers`) [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L1922-L1952) [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L919-L955) |
| Editor grab, drop, duplicate | `VehicleEditor.OnMouseButton` inside `Program.OnMouseButton`, and `Editor.OnFrame` in `OnFrameEditor` |
| Editor exit hand-off | `VehicleEditor.Dispose` in `PrepareFrame` once `EditorFlag` is cleared |

A placement click resolves in the `Program.OnMouseButton` prefix, before `ApplyInputEvents`, so a
point can land on a vehicle that splits later in the same frame; the next `Prune` re-homes it. Because
`Part.MatrixAsmb2VehicleAsmb` and `Asmb2VehicleAsmb` are recomputed on every call while
`Program.Editor != null` (and cached in flight), editor anchors follow a dragged part without any
extra invalidation.[^ksa-part]

## Key code

| Path | Role |
|---|---|
| [MeasureTools/Features/Measure/Anchor.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Anchor.cs#L100-L153) | Part and editor resolution, normals |
| [MeasureTools/Features/Measure/Anchor.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Anchor.cs#L155-L203) | `IsValid`, `IsValidEditor` |
| [MeasureTools/Features/Measure/Anchor.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Anchor.cs#L244-L327) | Factories and `Rehome` |
| [MeasureTools/Features/Measure/MeasureState.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureState.cs#L216-L303) | `Prune` and `RehomeAnchors` |

## Snippets

```csharp
// MeasureTools/Features/Measure/Anchor.cs L105-L127
private double3 ResolvePartPointEcl()
{
    var vehicle = (Vehicle)Body!;
    double3 posVehicleAsmb = OffsetPartAsmb.Transform(Part!.MatrixAsmb2VehicleAsmb);
    return vehicle.GetPositionEcl()
        + (posVehicleAsmb - vehicle.CenterOfMassAsmb).Transform(vehicle.Asmb2Ego);
}
// ...
private double3 ResolveEditorPartPointEcl()
{
    // ...
    VehicleEditor? editor = Program.Editor;
    if (editor == null)
        return InvalidPosition();
    double3 posVehicleAsmb = OffsetPartAsmb.Transform(Part!.MatrixAsmb2VehicleAsmb);
    return editor.EditingSpace.PositionEcl + posVehicleAsmb.Transform(editor.EditingSpace.Asmb2Ecl);
}
```

```csharp
// MeasureTools/Features/Measure/Anchor.cs L302-L327
public Anchor? Rehome(CelestialSystem system)
{
    if (Kind != AnchorKind.PartPoint && Kind != AnchorKind.EditorPartPoint)
        return null;
    Part? fullPart = Part?.FullPart;
    if (fullPart == null)
        return null;
    VehicleEditor? editor = Program.Editor;
    if (editor != null)
    {
        if (ReferenceEquals(editor.EditingSpace.Parts?.Find(fullPart.InstanceId), fullPart))
            return AtEditorPartLocal(Part!, OffsetPartAsmb, PartLabel, NormalPartAsmb);
        foreach (PartTree tree in editor.UnattachedPartTrees)
        {
            if (ReferenceEquals(tree.Find(fullPart.InstanceId), fullPart))
                return AtEditorPartLocal(Part!, OffsetPartAsmb, PartLabel, NormalPartAsmb);
        }
    }
    foreach (Astronomical astronomical in system.All.AsSpan())
    {
        if (astronomical is Vehicle vehicle
            && ReferenceEquals(vehicle.Parts?.Find(fullPart.InstanceId), fullPart))
            return AtPartLocal(vehicle, Part!, OffsetPartAsmb, PartLabel, NormalPartAsmb);
    }
    return null;
}
```

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `Vehicle.GetMatrixAsmb2Ego`, `GetPositionEcl`, `CenterOfMassAsmb`, `Asmb2Ego`, `Parts`, `Split`, `CreateVehicle`, `MergeFrom` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1270-L1281) | Transform being mirrored; ownership transitions |
| `Part.MatrixAsmb2VehicleAsmb`, `Asmb2VehicleAsmb`, `FullPart`, `InstanceId` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L724-L754) | Part-in-vehicle transform (uncached in the editor) |
| `PartTree.Find(ulong)` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1462-L1474) | Linear search by instance id |
| `VehicleEditor.EditingSpace`, `UnattachedPartTrees`, `Build`, `Dispose` | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L2256-L2312) | Editor trees and the hand-off at exit |
| `VehicleEditingSpace.PositionEcl`, `Asmb2Ecl`, `Parts`, `GetMatrixAsmb2Ego`, ctor from a `Vehicle` | [current/decomp/KSA/VehicleEditingSpace.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditingSpace.cs#L91-L114) | Editor frame (no CoM term) |
| `Camera.EgoToEcl` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L222-L233) | Why a camera-free mirror is exact |
| `Program.Editor` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L227) | Editor presence |

## Pitfalls and version notes

- Track parts by object identity plus `InstanceId` lookup, not by name or index; ids alone can
  collide across saves and the same part object moves between trees.
- Do not store a part point in vehicle-asmb or ECL coordinates; staging rebases the new vehicle's
  frame and the stored value would be wrong immediately.
- An anchor placed in flight on the vehicle you then open in the editor stays a `PartPoint` (the
  editing space shares the vehicle's tree and the original vehicle stays in the system) until its part
  leaves that tree, so it keeps resolving through the vehicle's center of mass and attitude rather
  than through the editing space. Whether that drifts when an edit changes the vehicle's center of mass
  was not checked (unverified).
- Engine gimbal deflection is not tracked; no gimbal code writes part transforms in this build.
- `PartTree.Find` is linear; with many measurements on very large craft the per-frame validity checks
  cost a scan per anchor.

## See also

- [Overview](../overview.md)
- [Live anchors in body, orbit, surface and part frames](./live-body-relative-anchors.md)
- [Part picking](./part-mesh-raycast-picking.md)
- [Editor measuring](./editor-measuring.md)
- [Game loop and lifecycle reference](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Track a part by object identity and rehome the anchor every frame](../../../howto/vehicles-and-parts/part-geometry/anchor-to-parts-across-vehicle-changes/anchor-to-parts-across-vehicle-changes-1.md) - how-to article demonstrating this feature
- [Turn a part-local point into an ECL position through the centre of mass and Asmb2Ego](../../../howto/vehicles-and-parts/part-geometry/get-part-points-in-vehicle-and-world-frames/get-part-points-in-vehicle-and-world-frames-2.md) - how-to article demonstrating this feature

[^mt-anchor]: MeasureTools/Features/Measure/Anchor.cs
[^mt-state]: MeasureTools/Features/Measure/MeasureState.cs
[^mt-readme]: KSA-MeasureTools README.md
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-parttree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditor]: KSA.VehicleEditor (decompiled, build 2026.10.10.5554)
[^ksa-vehicleeditingspace]: KSA.VehicleEditingSpace (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
