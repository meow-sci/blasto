---
type: How-To
title: Track a part by object identity and rehome the anchor every frame
description: Store the Part object, validate it against its owning PartTree each frame and search the system and the editor for the same object when the owner changes
category: vehicles-and-parts
group: part-geometry
topic: anchor-to-parts-across-vehicle-changes
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-measuretools/features/part-anchor-rehoming]
techniques: [object-identity-tracking]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.Program.OnMouseButton]
lifecycle: [frame:draw-ui, frame:input]
game_types: [KSA.Part, KSA.PartTree, KSA.Vehicle, KSA.VehicleEditor, KSA.VehicleEditingSpace]
tags: [parts, identity, staging, docking, editor, anchor]
---
Outcome: a stored point on a part keeps following that part while it is decoupled, docked, grabbed in the editor, dropped back or launched, and is removed only when the part itself is destroyed.
MeasureTools stores the `Part` object and the owner it last saw, asks every frame whether the owner still contains it, and if not looks for the same object elsewhere.

Prerequisite: StarMap. Demonstrated by [ksa-measuretools/part-anchor-rehoming](../../../../mods/ksa-measuretools/features/part-anchor-rehoming.md). Turning the stored part-local point into a position is
[article 2 of the geometry topic](../get-part-points-in-vehicle-and-world-frames/get-part-points-in-vehicle-and-world-frames-2.md).

## Background: how the game does it

The game moves `Part` objects instead of copying them (see the [overview](overview.md)): `Split` hands the existing part to the new vehicle, `MergeFrom` re-parents the other vehicle's tree, and the editor hands `EditingSpace.Parts`
to the launched vehicle. That is why object identity is a usable key: if the object is found in a tree, it is the same part. `Part.InstanceId` is the key `PartTree.Find` uses for the lookup, but identity is
decided with `ReferenceEquals` afterwards, because ids are only unique within the running session.

## Lifecycle and timing

MeasureTools prunes at the start of every `[StarMapAfterGui]` call (`frame:draw-ui`), before the window and overlay resolve anything. Every ownership change happens earlier in the same frame on the main thread
([game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)):

| Change | Where the game applies it |
|---|---|
| Staging decouple | `InputEvents.ApplyInputEvents`, right after `Glfw.PollEvents` (`frame:input`) |
| Docking on contact, structural part failure | detected on the vehicle worker, applied by `Universe.ApplyVehicleSolvers` (`frame:apply-solvers`) |
| Editor grab, drop, duplicate | `VehicleEditor.OnMouseButton` inside `Program.OnMouseButton`, and `Editor.OnFrame` in `OnFrameEditor` |
| Editor exit hand-off | `VehicleEditor.Dispose` in `PrepareFrame` once `EditorFlag` is cleared |

A placement click resolves in the `Program.OnMouseButton` prefix, before `ApplyInputEvents`, so a point can land on a vehicle that splits later in the same frame; the next prune re-homes it.

## Steps

1. **Store the part, its owner and a part-local offset.** The factory records the vehicle and the part (the mesh-owning subpart for hull picks, the full part for centre and connector picks):

```csharp
// MeasureTools/Features/Measure/Anchor.cs L248-L260
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
```

2. **Test validity by identity, not by name.** A flight anchor is valid while the stored vehicle is the object the system returns for its id and the full part is the object the vehicle's tree returns for its `InstanceId`.
   Subparts never move between full parts, so checking the full part covers a stored subpart. The editor variant checks `EditingSpace.Parts` and every unattached tree:

```csharp
// MeasureTools/Features/Measure/Anchor.cs L161-L203
public bool IsValid(CelestialSystem system)
{
    if (Kind == AnchorKind.EditorPartPoint || Kind == AnchorKind.EditorFreePoint)
        return IsValidEditor();
    Astronomical? anchorBody = Kind == AnchorKind.OrbitPoint ? OrbitParent as Astronomical : Body;
    if (anchorBody == null)
        return false;
    if (!ReferenceEquals(system.Get(anchorBody.Id), anchorBody))
        return false;
    if (Kind != AnchorKind.PartPoint)
        return true;
    // A part anchor turns stale when its part leaves the stored vehicle's
    // tree (decoupled, docked away, grabbed in the editor); Rehome then finds
    // the new owner. Subparts never migrate between full parts, so the full
    // part's presence covers a stored subpart too.
    Part? fullPart = Part?.FullPart;
    return fullPart != null && Body is Vehicle vehicle
        && ReferenceEquals(vehicle.Parts?.Find(fullPart.InstanceId), fullPart);
}

// Editor anchors turn stale when the editor closes or the part leaves the
// editor trees; part anchors are then re-homed (to the original or launched
// vehicle), while editor free points drop for good. A part being carried in
// the hand lives in an unattached tree, still a valid home.
private bool IsValidEditor()
{
    VehicleEditor? editor = Program.Editor;
    if (editor == null)
        return false;
    if (Kind == AnchorKind.EditorFreePoint)
        return true;
    Part? fullPart = Part?.FullPart;
    if (fullPart == null)
        return false;
    if (ReferenceEquals(editor.EditingSpace.Parts?.Find(fullPart.InstanceId), fullPart))
        return true;
    foreach (PartTree tree in editor.UnattachedPartTrees)
    {
        if (ReferenceEquals(tree.Find(fullPart.InstanceId), fullPart))
            return true;
    }
    return false;
}
```

3. **Re-home when invalid.** Look for the same `Part` object: editor trees first (if an editor exists), then every `Vehicle` in `system.All`; build a new anchor with the same offset and normal for the new owner.
   Nothing found means the part is gone:

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

4. **Prune every frame, before anything resolves an anchor.** Replace invalid anchors with their re-homed twin and drop the measurement only if re-homing fails:

```csharp
// MeasureTools/Features/Measure/MeasureState.cs L284-L303
private static bool RehomeAnchors(Anchor[] anchors, CelestialSystem system)
{
    for (int i = 0; i < anchors.Length; i++)
    {
        if (anchors[i].IsValid(system))
            continue;
        Anchor? rehomed = anchors[i].Rehome(system);
        if (rehomed == null)
        {
            if (DebugConfig.Measure)
                DefaultCategory.Log.Debug($"[MeasureTools] Anchor '{anchors[i].Label}' could not be re-homed (part or body gone).");
            return false;
        }
        if (DebugConfig.Measure)
            DefaultCategory.Log.Debug($"[MeasureTools] Anchor '{anchors[i].Label}' re-homed as '{rehomed.Label}'.");
        anchors[i] = rehomed;
        StateVersion++;
    }
    return true;
}
```

## Complete example

- [MeasureTools/Features/Measure/Anchor.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/Anchor.cs#L100-L327)[^ksa-measuretools-anchor] - resolvers, validity and the factories.
- [MeasureTools/Features/Measure/MeasureState.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MeasureState.cs#L216-L303)[^ksa-measuretools-measurestate] - `Prune` and `RehomeAnchors`.

## Variations and gotchas

- **Never store coordinates in the vehicle or ECL frame.** Staging rebases the new vehicle's frame; a part-local offset transfers unchanged.
- **Keep the check cheap.** `PartTree.Find` is a linear scan; with many anchors on very large craft that is one scan per anchor per frame.
- **Editor anchors survive.** An anchor placed in flight on a vehicle you then open in the editor stays a flight anchor until its part leaves that tree. Whether it drifts after an edit moves the centre of mass was not checked.
- **Duplicates.** `ReferenceEquals` makes an identically named replacement not silently take over; use [article 3](anchor-to-parts-across-vehicle-changes-3.md) when identity must outlive the process.
- **Gimbal deflection** is not tracked; no game code writes it into `Asmb2ParentAsmb`.

## See also

- [Overview](overview.md); [Article 2](anchor-to-parts-across-vehicle-changes-2.md); [Article 4](anchor-to-parts-across-vehicle-changes-4.md)
- [ksa-measuretools/part-anchor-rehoming](../../../../mods/ksa-measuretools/features/part-anchor-rehoming.md)
- [Weld one vessel to another](../../../physics/kinematics/weld-one-vessel-to-another/overview.md)

[^ksa-measuretools-anchor]: KSA-MeasureTools: MeasureTools/Features/Measure/Anchor.cs
[^ksa-measuretools-measurestate]: KSA-MeasureTools: MeasureTools/Features/Measure/MeasureState.cs
