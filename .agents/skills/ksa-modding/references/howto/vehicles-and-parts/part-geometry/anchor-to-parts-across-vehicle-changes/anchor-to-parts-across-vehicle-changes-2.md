---
type: How-To
title: Re-resolve a part every frame by vehicle id and Part.InstanceId, dormant when gone
description: Store a vehicle id and a Part.InstanceId, find both again every frame including subparts, and pause the feature instead of failing when either is missing
category: vehicles-and-parts
group: part-geometry
topic: anchor-to-parts-across-vehicle-changes
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/hot-pursuit-mesh-ray-placement]
techniques: [imgui-drawlist, id-lookup]
hooks: [starmap StarMapAfterGui, harmony-prefix KSA.FixedController.OnFrame]
lifecycle: [frame:draw-ui, frame:viewports]
game_types: [KSA.Part, KSA.Vehicle, KSA.PartTree, KSA.Universe]
tags: [parts, identity, instance-id, camera, dormant]
---
Outcome: a thing attached to a part (Hot Pursuit's part-mounted camera) follows that exact subpart through vessel motion, articulation and scaling, and pauses instead of failing when the vessel is unloaded
or the part leaves it, resuming when both are live again. It stores two plain values and re-resolves live objects every frame.

Prerequisite: StarMap. Demonstrated by [unscience/hot-pursuit-mesh-ray-placement](../../../../mods/unscience/features/hot-pursuit-mesh-ray-placement.md), which also shows how the part and the part-local point were picked.

## Background: how the game does it

`Vehicle.Id` is unique per vehicle in the system but only by convention; `Part.InstanceId` is a per-session counter ([overview](overview.md)). A `PartTree` can be searched by `InstanceId` only for its full parts; subparts live in
`Part.SubParts` of a full part, so the lookup has to recurse. Neither value survives a save and load; for that see [article 3](anchor-to-parts-across-vehicle-changes-3.md). The point to follow is stored in the subpart's local frame
(see the geometry topic) so the per-frame pose only multiplies it by the part's current matrix.

## Lifecycle and timing

`ResolveTarget` runs in the submod's `Update` (`frame:draw-ui`, `StarMapBeforeGui` or a hidden-HUD replay) and again in a `FixedController.OnFrame` prefix (`frame:viewports`), because a vessel destroyed during
`PrepareFrame` would otherwise leave last frame's `Part` reference in use. Both are on the main thread after `Universe.ApplyVehicleSolvers`; the picker is read-only and does not touch simulated state
([game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).

## Steps

1. **Keep identity separate from live references.** Only `VehicleId` and `PartInstanceId` are identity; `Vehicle` and `Part` are overwritten on every resolve:

```csharp
// hot-pursuit.lib/HotPursuitCamera.cs L15-L19
// Stable identity. Live references are only a per-frame convenience.
public string VehicleId { get; init; } = "";
public uint PartInstanceId { get; init; }
public Vehicle? Vehicle;
public Part? Part;
```

   Capture them at the pick (`VehicleId = pick.Vehicle.Id`, `PartInstanceId = pick.Part.InstanceId`) together with the mount vectors in the subpart's frame [hot-pursuit.lib/HotPursuitSubmod.Placement.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitSubmod.Placement.cs#L53-L73)[^unscience-hotpursuitsubmod-placement].

2. **Look the vehicle up unambiguously.** A duplicate id returns null on purpose, so an anchor on one of two same-named vessels goes dormant rather than jumping to the wrong one. Debris is searched too,
   so an id held from before a part failure still resolves:

```csharp
// ksa-abstractions.lib/VehicleProvider.cs L22-L40
public static List<Vehicle> GetAllVehicles(bool includeDebris = false) =>
    Universe.CurrentSystem?.All.UnsafeAsList()
        .OfType<Vehicle>()
        .Where(v => includeDebris || !v.IsDebris)
        .ToList() ?? new List<Vehicle>();

/// <summary>Finds a vehicle by id, or null if none or more than one matches. Debris is searched too, so an id
/// held from before a part failure still resolves.</summary>
public static Vehicle? FindVehicle(string vehicleId)
{
    Vehicle? match = null;
    foreach (var vehicle in GetAllVehicles(includeDebris: true))
    {
        if (vehicle.Id != vehicleId) continue;
        if (match != null) return null; // Ambiguous identities must never retarget saved edits.
        match = vehicle;
    }
    return match;
}
```

3. **Find the part by `InstanceId`, recursing into subparts, every frame:**

```csharp
// hot-pursuit.lib/HotPursuitSubmod.cs L238-L266
private static void ResolveTarget(HotPursuitCamera entry)
{
    entry.Vehicle = VehicleProvider.FindVehicle(entry.VehicleId);
    entry.Part = entry.Vehicle == null ? null : FindPart(entry.Vehicle, entry.PartInstanceId);
}

private static Part? FindPart(Vehicle vehicle, uint instanceId)
{
    foreach (var part in vehicle.Parts.Parts)
    {
        var found = FindPartRecursive(part, instanceId);
        if (found != null)
            return found;
    }
    return null;
}

private static Part? FindPartRecursive(Part part, uint instanceId)
{
    if (part.InstanceId == instanceId)
        return part;
    foreach (var child in part.SubParts)
    {
        var found = FindPartRecursive(child, instanceId);
        if (found != null)
            return found;
    }
    return null;
}
```

4. **Go dormant, do not fail.** When `Vehicle` or `Part` is null the entry is unresolved: `Update` unfollows the camera and hides its viewport, and the pose prefix does nothing. The same code resumes when both resolve again:

```csharp
// hot-pursuit.lib/HotPursuitSubmod.cs L33-L59
public void Update(double dt)
{
    foreach (var entry in _cameras)
    {
        ResolveTarget(entry);
        SyncLease(entry);
        if (entry.Viewport == null)
            continue;

        var camera = entry.Viewport.BaseCamera;
        if (!entry.IsResolved)
        {
            if (camera.Following != null)
                camera.Unfollow(changeControl: false);
            entry.Viewport.SetVisible(false);
            continue;
        }

        if (camera.Following != entry.Vehicle)
        {
            camera.SetFollow(entry.Vehicle!, tidalLocking: true,
                changeControl: false, alert: false);
        }
        camera.SetFieldOfView(entry.FieldOfView);
        entry.Viewport.SetVisible(entry.Visible);
    }
}
```

## Complete example

- [hot-pursuit.lib/HotPursuitSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitSubmod.cs#L33-L59)[^unscience-hotpursuitsubmod] and [hot-pursuit.lib/HotPursuitSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitSubmod.cs#L238-L266): update loop and resolver.
- [hot-pursuit.lib/HotPursuitCamera.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitCamera.cs#L8-L58)[^unscience-hotpursuitcamera] - stable identity versus per-frame references, and the dormant status text.

## Variations and gotchas

- **Staging moves the part to a new vessel with a new id**, so a camera on a stage that separates goes dormant (the part is no longer found under the stored vehicle id). That is the trade-off against [article 1](anchor-to-parts-across-vehicle-changes-1.md), which follows the part.
- **Allocation.** `GetAllVehicles` builds a new LINQ list on every call and `FindVehicle` calls it, so each camera allocates every frame.
- **Duplicate names** make the anchor dormant by design.
- **Subparts.** Search recursively; a flat scan of `vehicle.Parts.Parts` finds only full parts.
- **Drawing.** The only ImGui drawing here is the placement hint next to the mouse while armed (`ImGui.GetForegroundDrawList().AddText`); the camera itself is posed by a viewport prefix from the stored part-local vectors.

## See also

- [Overview](overview.md); [Article 1](anchor-to-parts-across-vehicle-changes-1.md); [Article 3](anchor-to-parts-across-vehicle-changes-3.md)
- [unscience/hot-pursuit-mesh-ray-placement](../../../../mods/unscience/features/hot-pursuit-mesh-ray-placement.md)
- [Ray cast against part meshes and terrain](../../../physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md)

[^unscience-hotpursuitsubmod-placement]: unscience: hot-pursuit.lib/HotPursuitSubmod.Placement.cs
[^unscience-hotpursuitsubmod]: unscience: hot-pursuit.lib/HotPursuitSubmod.cs
[^unscience-hotpursuitcamera]: unscience: hot-pursuit.lib/HotPursuitCamera.cs
