---
type: Mod Feature
title: Click-to-mount picking on vessel meshes with stable part anchoring
description: A one-shot click casts Cursor.GetEgoRay against every vessel with Part.RayCastEgo, stores the hit in the hit sub-part's local frame and re-resolves the anchor each frame by vehicle id and Part.InstanceId, going dormant when the target is gone
mod: unscience
submod: hot-pursuit
category: camera
techniques: [imgui-drawlist]
hooks:
  - starmap StarMapAfterGui (via unscience ISubmod.RenderFloatingWindows, armed click)
  - starmap StarMapBeforeGui (via unscience ISubmod.Update, target re-resolution)
  - harmony-prefix KSA.FixedController.OnFrame(IViewport, double) (re-resolution before the pose)
lifecycle: [frame:draw-ui, frame:viewports]
game_types: [KSA.Cursor, KSA.Part, KSA.Vehicle, KSA.Camera, KSA.Program]
tags: [picking, raycast, cursor, parts, anchoring, imgui]
---

To place a Hot Pursuit camera, the player clicks "Arm camera placement" and then clicks a vessel in
the 3D view. The camera appears 15 cm off the clicked surface, looking straight out along the surface
normal. It stays attached to that exact sub-part even if the vessel moves, rotates, articulates or is
scaled, and it pauses (rather than failing) if the vessel is unloaded and comes back later.

Mechanism in one line: the same mesh-precise part raycast KSA uses for hover highlighting
(`Part.RayCastEgo` against a `Cursor.GetEgoRay` ray) returns the closest sub-part plus the hit point and
vertex normal already expressed in that sub-part's local assembly frame; the mod stores those local
vectors with the vehicle id and the sub-part's `Part.InstanceId`, and resolves the live objects again
every frame.[^hp-picker][^ksa-part]

## How it works

Armed one-shot click (`HotPursuitSubmod.Placement`):[^hp-placement]

- `Arm()` sets `_armed` and `_armingThisFrame`. In `RenderFloatingWindows` the first armed frame is
  skipped so the ImGui click that pressed the Arm button cannot also become the world click.
- While armed, a hint is drawn next to the mouse with `ImGui.GetForegroundDrawList().AddText` (black
  shadow, white text). Escape or right-click cancels; entering the editor cancels.
- A left click counts only when `ImGui.GetIO().WantCaptureMouse` is false, so clicks on any ImGui
  window are ignored.

Picking (`HotPursuitPicker.TryPick(range)`):

1. Ray: `Cursor.GetEgoRay(Program.MainViewport)` = main camera `ScreenToEgoRay` at the cursor. Ego
   space is camera-relative, so the origin is zero and the direction is normalized.[^ksa-cursor][^ksa-camera]
2. Candidates: `VehicleProvider.GetAllVehicles(includeDebris: true)` (all `Vehicle` objects in
   `Universe.CurrentSystem.All`).[^vehicle-provider] Broad phase per vessel:
   `camera.GetPositionEgo(vehicle).Length() - vehicle.BoundingSphereRadiusBody > best` skips it.
3. Narrow phase: `vehicle.GetMatrixAsmb2Ego(camera)`, then `part.RayCastEgo(...)` for each top-level part,
   keeping the smallest distance. In 5554 `RayCastEgo` first tests a part bounding sphere
   (`BoundingBoxPartAsmb` extent times the largest `ScaleTotal` component), then calls
   `RayCastEgoSubPart` on each sub-part, which tests the sub-part's `MeshViewModule` mesh bounding sphere
   and then runs a watertight triangle test against the view mesh transformed by `MatrixAsmb2Ego`. The
   hit position is transformed back by the inverse of that matrix, and the "normal" is the stored vertex
   normal of the closest vertex.[^ksa-part]
4. The result keeps `closestSubPart`, the local position and the normalized local normal.

Mount basis: `MountPoint = position + normal * 0.15`; `MountTangent` is the sub-part's local +Y projected onto the surface
plane (local +X if +Y is parallel to the normal), which fixes the camera's initial "up". All three vectors
live in the sub-part's frame, so the per-frame pose only has to push them through the current
`Part.MatrixAsmb2Ego` (see [part-mounted cameras](./hot-pursuit-part-mounted-cameras.md)).

Stable anchoring (`HotPursuitCamera`, `HotPursuitSubmod.ResolveTarget`):[^hp-camera][^hp-submod]

| Stored | Purpose |
|---|---|
| `VehicleId` (string) | Re-find the vessel with `VehicleProvider.FindVehicle`, which returns null when no vessel or more than one vessel has that id |
| `PartInstanceId` (`Part.InstanceId`, uint) | Recursive search of `vehicle.Parts.Parts` and each `Part.SubParts` |
| `Vehicle`, `Part` | Per-frame cache only, overwritten on every resolve |
| `SaveTarget` (`SavedPartReference`) | Structural tree path used by scene saves, because `InstanceId` is a runtime counter (`Universe.GetNextRunningId()` in the `Part` constructor) and is not stable across loads[^hp-saves] |

When resolution fails the entry is "Dormant": `Update` unfollows the camera and hides its viewport,
and the pose prefix does nothing. When the vessel id and part are live again the camera resumes.

## Lifecycle and timing

| Code | When | State available |
|---|---|---|
| Armed click and `TryPick` | `frame:draw-ui`, from `ISubmod.RenderFloatingWindows` (`StarMapAfterGui`, postfix of `Program.OnDrawUiViewports`) | ImGui IO is valid for this frame. The main camera matrices were built in `frame:viewports` of this frame, so the ray and the vessel matrices are computed from the same main-camera pose the player sees. Read-only on vessels |
| `ResolveTarget` in `Update` | `frame:draw-ui` (`StarMapBeforeGui` or hidden-HUD replay) | Main thread; vessel list is the main-thread copy after `ApplyVehicleSolvers` |
| `ResolveTarget` in the `FixedController.OnFrame` prefix | `frame:viewports`, before `Update` in the same frame | Re-resolved there because a vessel destroyed during `PrepareFrame` would otherwise leave last frame's `Part` reference |

Picking never touches simulated state, so the worker-thread physics step running concurrently is not
affected.

## Key code

| Path | Role |
|---|---|
| [hot-pursuit.lib/HotPursuitPicker.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitPicker.cs#L18-L58) | Ray, broad phase, `RayCastEgo` narrow phase |
| [hot-pursuit.lib/HotPursuitSubmod.Placement.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitSubmod.Placement.cs#L30-L121) | Armed click, mount basis, cursor hint |
| [hot-pursuit.lib/HotPursuitSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitSubmod.cs#L238-L266) | Re-resolution by vehicle id and `InstanceId` |
| [hot-pursuit.lib/HotPursuitCamera.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitCamera.cs#L8-L58) | Stable identity versus per-frame references |
| [ksa-abstractions.lib/VehicleProvider.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/VehicleProvider.cs#L22-L41) | Vessel enumeration and unambiguous lookup |

## Snippets

The picker:

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
// ...
            var centre = camera.GetPositionEgo(vehicle);
            if (!IsFinite(centre) || centre.Length() - vehicle.BoundingSphereRadiusBody > best)
                continue;

            var vehicleMatrix = vehicle.GetMatrixAsmb2Ego(camera);
            foreach (var part in vehicle.Parts.Parts)
            {
// ...
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

Turning the hit into a mount stored in the sub-part's frame:

```csharp
// hot-pursuit.lib/HotPursuitSubmod.Placement.cs L53-L73
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
                    TryOpenViewport(entry);
```

KSA's sub-part raycast returns the hit in the sub-part's local frame:

```csharp
// current/decomp/KSA/Part.cs L2596-L2608
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
		farIntersectionPositionAsmb = (ray.Origin + ray.Direction * farT).Transform(result);
		farIntersectionNormalAsmb = double3.Unpack(in meshView.HostMesh.GetVertexSpan<float3>(MeshAttribute.Normal)[meshView.HostMesh.IndexBuffer.ToSpan<int>()[farClosestVertexIndex]]);
		return true;
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Cursor.GetEgoRay(IViewport)` | [current/decomp/KSA/Cursor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Cursor.cs#L27-L30) | Cursor ray in the viewport camera's ego space |
| `Camera.ScreenToEgoRay`, `Camera.GetPositionEgo` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L704-L724) | Normalized perspective ray; vessel centre for the broad phase (`GetPositionEgo` L236) |
| `Part.RayCastEgo`, `Part.RayCastEgoSubPart` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L2534-L2609) | Bounding spheres, then watertight mesh test; local hit and vertex normal |
| `Part.InstanceId`, `Part.SubParts`, `Part.MatrixAsmb2Ego` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L574) | Runtime identity (L574, set at L1492), sub-part walk (L1127), frame chain (L1213) |
| `Vehicle.GetMatrixAsmb2Ego(Camera)`, `Vehicle.BoundingSphereRadiusBody` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1270-L1281) | Vessel matrix; broad-phase radius (L527) |
| `Vehicle.UpdateHighlight` (stock hover picking) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L2903-L2925) | Reference: the same ray and `RayCastEgo` loop |

## Pitfalls and version notes

- Vessel parts only: terrain and static objects are never tested, by design. Graffiti's picker adds
  cloth, EVA kitten and terrain cases on the same ray.
- The returned normal is the vertex normal of the nearest vertex of the hit triangle, not an
  interpolated or face normal; on coarse meshes the camera may point slightly off the face.
- Since 5482 `RayCastEgo` rejects rays that miss a part-level bounding sphere first. A mod that moves
  sub-part transforms without updating part bounds can make those sub-parts unpickable.[^scope-camera]
- `VehicleProvider.GetAllVehicles` builds a new list with LINQ on every call, and `FindVehicle` calls it,
  so each camera allocates every frame in `Update` and again in the prefix.
- Duplicate vessel ids make `FindVehicle` return null on purpose, so an anchor on one of two
  same-named vessels goes dormant rather than jumping to the wrong one.
- If a part leaves its vessel (staging, structural failure) it is no longer found under the stored
  vehicle id and the camera goes dormant.

## See also

- [Part-mounted cameras (viewport lease and pose)](./hot-pursuit-part-mounted-cameras.md)
- [Graffiti decal placement picking](./graffiti-decal-placement-picking.md) (same ray, more target kinds)
- [Saved part identity](./lib-save-part-identity.md)
- [Game state providers](./lib-game-state-providers.md)
- [Sub-mod: Hot Pursuit](../submods/hot-pursuit.md)
- [Mod overview](../overview.md)
- [Skip the stock FixedController.OnFrame for the viewports you own with a Harmony prefix](../../../howto/camera/controllers/write-the-camera-pose-in-phase-with-the-frame/write-the-camera-pose-in-phase-with-the-frame-3.md) - how-to article demonstrating this feature
- [Click a hull point and store it in the hit sub-part's local frame](../../../howto/camera/secondary-viewports/mount-a-camera-on-a-part/mount-a-camera-on-a-part-3.md) - how-to article demonstrating this feature
- [Cursor ray against every vehicle, plus the stock hover picker](../../../howto/physics/mod-simulation/ray-cast-against-vessel-meshes-and-terrain/ray-cast-against-vessel-meshes-and-terrain-2.md) - how-to article demonstrating this feature
- [Re-resolve a part every frame by vehicle id and Part.InstanceId, dormant when gone](../../../howto/vehicles-and-parts/part-geometry/anchor-to-parts-across-vehicle-changes/anchor-to-parts-across-vehicle-changes-2.md) - how-to article demonstrating this feature

[^hp-picker]: hot-pursuit.lib/HotPursuitPicker.cs
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^hp-placement]: hot-pursuit.lib/HotPursuitSubmod.Placement.cs
[^ksa-cursor]: KSA.Cursor (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^vehicle-provider]: ksa-abstractions.lib/VehicleProvider.cs
[^hp-camera]: hot-pursuit.lib/HotPursuitCamera.cs
[^hp-submod]: hot-pursuit.lib/HotPursuitSubmod.cs
[^hp-saves]: hot-pursuit.lib/HotPursuitSubmod.Saves.cs
[^scope-camera]: scope/camera.md (authors' camera integration analysis)
