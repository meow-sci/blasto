---
type: How-To
title: Click a hull point and store it in the hit sub-part's local frame
description: Hot Pursuit raycasts the cursor against every vessel's part meshes with Part.RayCastEgo, stores the hit point and normal in the closest sub-part's local frame with the vehicle id and Part.InstanceId, and re-resolves them every frame so a camera pauses instead of failing when its target is gone
category: camera
group: secondary-viewports
topic: mount-a-camera-on-a-part
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/hot-pursuit-mesh-ray-placement]
techniques: [imgui-drawlist]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:viewports]
game_types: [KSA.Part, KSA.Vehicle, KSA.Camera, KSA.IGameViewport]
tags: [camera, part, raycast, picking, instance-id, hot-pursuit]
---
The player places a camera by clicking a part. The click is turned into a mesh-precise hit on a specific sub-part, stored in that sub-part's own frame, and re-found every frame from stable ids, so the camera stays attached through motion, scaling and articulation and goes dormant (instead of throwing) when its vessel unloads. This article is the placement half of
[mounting a camera on a part](overview.md); the pose is [approach 1](mount-a-camera-on-a-part-1.md). Prerequisites: StarMap.

## Background: how the game does it

- `Cursor.GetEgoRay(viewport)` is the main camera's `ScreenToEgoRay` at the cursor; ego space is camera-relative, so the origin is zero ([current/decomp/KSA/Cursor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Cursor.cs#L27-L30), [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L704-L713)).
- `Part.RayCastEgo(in vehicleMatrix, ray, out minDistance, out maxDistance, out nearPositionAsmb, out nearNormalAsmb, out farPositionAsmb, out farNormalAsmb, out closestSubPart, out farthestSubPart)` is the same mesh-precise test KSA uses for hover highlighting. It returns the closest sub-part and the hit point and vertex normal already in that sub-part's local assembly frame ([current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2534-L2572)).
  Since 5482 it first tests a part-level bounding sphere, then each sub-part's mesh bounding sphere, then a triangle test.
- `Part.InstanceId` is a runtime counter assigned in the `Part` constructor from `Universe.GetNextRunningId()`, so it is stable within a session but not across loads ([current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L574), [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1492)).

## Lifecycle and timing

The click is handled in `[StarMapAfterGui]`'s floating-window pass (`frame:draw-ui`): ImGui IO is valid, and the main camera matrices were built in `frame:viewports` of this frame, so the ray and the vessel matrices come from the pose the player sees. Picking is read-only on vessels, so it does not touch the worker-thread physics. The stored mount is posed each frame in `frame:viewports`
([approach 1](mount-a-camera-on-a-part-1.md)).

## Steps

1. **Arm one click, skip the arming frame, ignore ImGui clicks.** The first armed frame is skipped so the click that pressed the Arm button cannot also become the world click; Escape or a right-click cancels; a left click counts only when ImGui does not want the mouse:

```csharp
// hot-pursuit.lib/HotPursuitSubmod.Placement.cs L30-L52
public void RenderFloatingWindows()
{
    if (!_armed)
        return;

    DrawCursorHint();
    if (_armingThisFrame)
    {
        // The arm button itself is an ImGui click. Consume this frame so that the button
        // cannot also become the world placement click.
        _armingThisFrame = false;
        return;
    }
    if (Program.EditorFlag)
    {
        Disarm("Placement cancelled — cameras are a flight-scene feature.");
        return;
    }
    if (ImGui.IsKeyPressed(ImGuiKey.Escape) || ImGui.IsMouseClicked(ImGuiMouseButton.Right))
    {
        Disarm("Placement cancelled.");
        return;
    }
```

2. **Ray cast every vessel, broad phase first.** A camera-space bounding-sphere test skips far vessels; the narrow phase asks each top-level part and keeps the smallest distance and the closest sub-part, hit position and normal exactly as returned:

```csharp
// hot-pursuit.lib/HotPursuitPicker.cs L18-L58
internal static bool TryPick(double range, out PickResult result)
{
    result = default;
    var camera = Program.MainViewport.GetCamera();
    var ray = Cursor.GetEgoRay(Program.MainViewport);
    if (!IsFinite(ray.Direction) || ray.Direction.LengthSquared() <= 0)
        return false;

    var best = range;
    var found = false;
    foreach (var vehicle in VehicleProvider.GetAllVehicles(includeDebris: true))
    {
        // Cheap broad phase before walking the part tree. This is deliberately camera-space,
        // matching KSA's own hover path and avoiding an ECL/floating-origin comparison.
        var centre = camera.GetPositionEgo(vehicle);
        if (!IsFinite(centre) || centre.Length() - vehicle.BoundingSphereRadiusBody > best)
            continue;

        var vehicleMatrix = vehicle.GetMatrixAsmb2Ego(camera);
        foreach (var part in vehicle.Parts.Parts)
        {
            // RayCastEgo returns the closest subpart and returns hit position/normal in that
            // closest subpart's local assembly frame. Keep those values exactly as returned.
            if (!part.RayCastEgo(in vehicleMatrix, ray, out var distance, out _,
                    out var position, out var normal, out _, out _, out var closestSubPart, out _)
                || closestSubPart == null || distance < 0 || distance >= best)
                continue;

            var length = normal.Length();
            if (!double.IsFinite(length) || length <= 0)
                continue;

            best = distance;
            result = new PickResult(vehicle, closestSubPart,
                position, normal / length, distance);
            found = true;
        }
    }

    return found;
}
```

3. **Store the mount in the sub-part's frame.** Lift the point 15 cm off the surface along the normal and pick a tangent for the initial up; keep the vehicle id and the sub-part's `InstanceId`, plus a structural path for saves (`InstanceId` is not stable across loads):

```csharp
// hot-pursuit.lib/HotPursuitSubmod.Placement.cs L53-L72
if (!ImGui.GetIO().WantCaptureMouse && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
{
    if (HotPursuitPicker.TryPick(_placementRange, out var pick))
    {
        var tangent = PickTangent(pick.Normal);
        if (tangent.LengthSquared() > 0)
        {
            var entry = new HotPursuitCamera
            {
                VehicleId = pick.Vehicle.Id,
                PartInstanceId = pick.Part.InstanceId,
                Vehicle = pick.Vehicle,
                Part = pick.Part,
                SaveTarget = MeowSci.KsaAbstractions.Persistence.SavedPartReference.Capture(pick.Vehicle, pick.Part),
                MountPoint = pick.Position + pick.Normal * 0.15,
                SurfaceNormal = pick.Normal,
                MountTangent = tangent,
                Visible = true,
            };
            _cameras.Add(entry);
```

4. **Re-resolve every frame, by id.** The vehicle by id (null when missing or when two vessels share an id, on purpose), the part by recursive search of `Parts` and each `SubParts`. When resolution fails the entry is dormant: it unfollows, hides its viewport and the pose patch does nothing:

```csharp
// hot-pursuit.lib/HotPursuitSubmod.cs L255-L266
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

   Resolution is done again inside the pose prefix ([hot-pursuit.lib/HotPursuitSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitSubmod.cs#L238-L242)) because a vessel destroyed during `PrepareFrame` would leave last frame's disposed `Part` reference.

## Complete example

- [hot-pursuit.lib/HotPursuitPicker.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitPicker.cs), [hot-pursuit.lib/HotPursuitSubmod.Placement.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitSubmod.Placement.cs), [hot-pursuit.lib/HotPursuitCamera.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitCamera.cs), [hot-pursuit.lib/HotPursuitSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitSubmod.Saves.cs)

## Variations and gotchas

- **Vessel parts only.** Terrain and static objects are never tested; a picker for cloth, kittens and terrain is a different topic ([Ray cast against part meshes and terrain, from the cursor or any origin](../../../physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md)).
- **The normal is the vertex normal** of the nearest vertex of the hit triangle, not a face normal; on coarse meshes the camera may point slightly off the face.
- **Bounds matter.** A mod that moves sub-part transforms without updating part bounds can make those sub-parts unpickable.
- **Allocation.** `VehicleProvider.GetAllVehicles` builds a list with LINQ on each call and `FindVehicle` calls it, so each camera allocates every frame in `Update` and again in the prefix.
- **A part that leaves its vessel** is no longer found under the stored vehicle id; the camera goes dormant ([Keeping an anchor on a part across staging, docking, editor changes and saves](../../../vehicles-and-parts/part-geometry/anchor-to-parts-across-vehicle-changes/overview.md)).

## See also

- [Topic overview](overview.md); [approach 1](mount-a-camera-on-a-part-1.md), [2](mount-a-camera-on-a-part-2.md)
- [Click-to-mount picking on vessel meshes with stable part anchoring](../../../../mods/unscience/features/hot-pursuit-mesh-ray-placement.md)
- [Pick the vessel, part, world quad or terrain point under the cursor](../../../ui/input/pick-objects-under-the-cursor/overview.md), [Ray cast against part meshes and terrain, from the cursor or any origin](../../../physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/overview.md), [Lease a secondary viewport for a mod camera and show it in a window](../borrow-a-secondary-viewport/overview.md)
