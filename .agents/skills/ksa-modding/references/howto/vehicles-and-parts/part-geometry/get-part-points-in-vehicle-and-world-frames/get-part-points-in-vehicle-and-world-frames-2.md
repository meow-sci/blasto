---
type: How-To
title: Turn a part-local point into an ECL position through the centre of mass and Asmb2Ego
description: Store a point in a part's frame and resolve it to ECL every frame by applying MatrixAsmb2VehicleAsmb, the centre-of-mass offset and the vehicle's Asmb2Ego about its ECL position
category: vehicles-and-parts
group: part-geometry
topic: get-part-points-in-vehicle-and-world-frames
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksa-measuretools/features/part-anchor-rehoming]
techniques: [frame-relative-anchors]
hooks: [harmony-prefix KSA.Program.OnMouseButton, starmap StarMapAfterGui]
lifecycle: [frame:input, frame:draw-ui]
game_types: [KSA.Part, KSA.Vehicle, KSA.Camera, KSA.VehicleEditor, KSA.VehicleEditingSpace]
tags: [parts, anchors, frames, ecl, center-of-mass]
---
Outcome: store a point in a part's own frame, then turn it into an ECL (world) position at any later moment, following the vehicle's motion and the part's own motion inside the
vehicle, without a `Camera` or any render state. MeasureTools uses this for measurement endpoints; the same maths drives any marker pinned to a part.

Prerequisite: StarMap. Demonstrated by [ksa-measuretools/part-anchor-rehoming](../../../../mods/ksa-measuretools/features/part-anchor-rehoming.md). Keeping the point valid when the part changes vehicle is a separate problem:
[Keep an anchor on a part through staging, docking, editor changes and saves](../anchor-to-parts-across-vehicle-changes/overview.md).

## Background: how the game does it

The game builds the render transform as `Vehicle.GetMatrixAsmb2Ego`: rotate by `Asmb2Ego` and translate to `bodyPositionEgo - CenterOfMassAsmb.Transform(Asmb2Ego)`:

```csharp
// current/decomp/KSA/Vehicle.cs L1276-L1282
public double4x4 GetMatrixAsmb2Ego(double3 bodyPositionEgo)
{
    doubleQuat asmb2Ego = Asmb2Ego;
    double3 position = bodyPositionEgo - CenterOfMassAsmb.Transform(asmb2Ego);
    return double4x4.CreateFromQuaternion(asmb2Ego) * double4x4.CreateTranslation(position);
}

```

and `Camera.EgoToEcl` is `posEgo + PositionEcl` [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L222-L226)[^ksa-camera]. Because the camera term is a translation, you can reproduce the result about the vehicle's own ECL position:
`vehicle.GetPositionEcl()` (the vehicle's cached ECL position [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L953-L957)[^ksa-vehicle], the point `GetMatrixAsmb2Ego` treats as the centre of mass) plus `(posVehicleAsmb - CenterOfMassAsmb)` rotated by `Asmb2Ego`.
`Part.MatrixAsmb2VehicleAsmb` supplies `posVehicleAsmb` from a part-local offset and includes scale and any live animation of the part [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L740-L754)[^ksa-part].

## Lifecycle and timing

Resolve whenever you need the position; MeasureTools does it every frame in `[StarMapAfterGui]` (`frame:draw-ui`) while drawing overlays and in the click prefix
(`frame:input`). By that point `Universe.ApplyVehicleSolvers` has copied the physics result into the vehicle and part matrices are cached for the frame ([game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).
In the editor `Part.MatrixAsmb2VehicleAsmb` is recomputed on each call, so a dragged part is followed without invalidation.

## Steps

1. **Store the offset in the part frame.** For a mesh hit or vertex you already have a part-local position. For a connector, whose position the game gives in the vehicle frame, invert the part matrix once
   so the stored value round-trips exactly through the resolve below:

```csharp
// MeasureTools/Features/Measure/Anchor.cs L244-L270
// For picks whose position is already known in the part's local asmb frame
// (mesh raycast hits, mesh vertices, the bounding-box center). partLabel is
// the owner-free description; the vehicle id prefix is derived here so Rehome
// can rebuild it for a new owner.
public static Anchor AtPartLocal(Vehicle vehicle, Part part, double3 offsetPartAsmb, string partLabel, double3? normalPartAsmb = null)
{
    return new Anchor
    {
        Kind = AnchorKind.PartPoint,
        Body = vehicle,
        Part = part,
        OffsetPartAsmb = offsetPartAsmb,
        NormalPartAsmb = normalPartAsmb,
        Label = vehicle.Id + " " + partLabel,
        PartLabel = partLabel,
    };
}

// For picks known in the vehicle-asmb frame (connector positions). Inverting
// the part matrix once here round-trips exactly through ResolveEcl, and
// sidesteps Connector.PositionVehicleAsmb applying rotation and translation
// but not the part scale.
public static Anchor AtPartVehicleAsmb(Vehicle vehicle, Part part, double3 posVehicleAsmb, string partLabel)
{
    double4x4.Invert(part.MatrixAsmb2VehicleAsmb, out double4x4 inverse);
    return AtPartLocal(vehicle, part, posVehicleAsmb.Transform(inverse), partLabel);
}
```

2. **Resolve to ECL.** Local offset to vehicle frame via `MatrixAsmb2VehicleAsmb`, then about the vehicle's ECL position (flight) or the editing space (editor):

```csharp
// MeasureTools/Features/Measure/Anchor.cs L105-L127
private double3 ResolvePartPointEcl()
{
    var vehicle = (Vehicle)Body!;
    double3 posVehicleAsmb = OffsetPartAsmb.Transform(Part!.MatrixAsmb2VehicleAsmb);
    return vehicle.GetPositionEcl()
        + (posVehicleAsmb - vehicle.CenterOfMassAsmb).Transform(vehicle.Asmb2Ego);
}

// Editor twin of ResolvePartPointEcl, mirroring VehicleEditingSpace.GetMatrixAsmb2Ego:
// the editing space rotates by Asmb2Ecl about PositionEcl and, unlike a Vehicle,
// has no center-of-mass term.
private double3 ResolveEditorPartPointEcl()
{
    // The overlay's throttled preview cache can hold an editor anchor for a
    // few frames past editor teardown (Prune only clears pending points and
    // settled measurements); NaN routes it into the existing Valid() filters
    // instead of throwing.
    VehicleEditor? editor = Program.Editor;
    if (editor == null)
        return InvalidPosition();
    double3 posVehicleAsmb = OffsetPartAsmb.Transform(Part!.MatrixAsmb2VehicleAsmb);
    return editor.EditingSpace.PositionEcl + posVehicleAsmb.Transform(editor.EditingSpace.Asmb2Ecl);
}
```

   The editor variant mirrors `VehicleEditingSpace.GetMatrixAsmb2Ego`, which has no centre-of-mass term.

3. **Rotate normals, do not translate them.** Surface normals stored in the part frame are rotated by `Part.Asmb2VehicleAsmb` and then `Vehicle.Asmb2Ego` (or `EditingSpace.Asmb2Ecl`).

4. **Get a screen position if you need one.** Subtract `camera.PositionEcl` to get ego and call `camera.EgoToScreen(posEgo)`; the ECL position is still camera-independent until this point.

## Complete example

- [MeasureTools/Features/Measure/Anchor.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Anchor.cs#L100-L153)[^ksa-measuretools-anchor], the factories and both resolvers.

## Variations and gotchas

- **Do not store vehicle-frame or ECL coordinates.** Staging rebases the new vehicle's frame, so a stored vehicle-frame value is wrong immediately; a part-local offset is not.
- **Scale.** The stored offset is in unscaled part space and the matrix applies the scale, so the point follows [runtime scaling](../../vehicles/scale-a-vessel-or-its-parts-at-runtime/overview.md).
- **Centre of mass moves.** The formula uses the current `CenterOfMassAsmb`; when propellant burns it shifts and the ECL result changes consistently with the rendered vehicle.
- **Editor-to-flight.** The editor path has no vehicle object; resolve through `Program.Editor.EditingSpace` and treat a null editor as "NaN, skip".
- **Gimbals.** No game code writes the gimbal into `Asmb2ParentAsmb`; deflection is not followed.

## See also

- [Overview](overview.md); [Article 1](get-part-points-in-vehicle-and-world-frames-1.md)
- [ksa-measuretools/part-anchor-rehoming](../../../../mods/ksa-measuretools/features/part-anchor-rehoming.md)
- [Keep an anchor on a part through staging, docking, editor changes and saves](../anchor-to-parts-across-vehicle-changes/overview.md)

[^ksa-camera]: current/decomp/KSA/Camera.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksa-measuretools-anchor]: KSA-MeasureTools: MeasureTools/Features/Measure/Anchor.cs
