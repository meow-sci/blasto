---
type: How-To
title: Cursor ray against every vehicle, plus the stock hover picker
description: Cast Cursor.GetEgoRay against every vehicle's parts with Part.RayCastEgo to get the subpart, the hit point and the vertex normal in the subpart's local frame, gated by projected size, with an optional reuse of ViewportPartPicker
category: physics
group: mod-simulation
topic: ray-cast-against-vessel-meshes-and-terrain
approach: 2
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksa-measuretools/features/part-mesh-raycast-picking
  - mods/unscience/features/hot-pursuit-mesh-ray-placement
techniques: [mesh-raycast, imgui-input]
hooks: [harmony-prefix KSA.Program.OnMouseButton, starmap StarMapAfterGui]
lifecycle: [frame:input, frame:draw-ui]
game_types: [KSA.Part, KSA.Vehicle, KSA.Camera, KSA.Cursor, KSA.ViewportPartPicker, KSA.MeshViewModule]
tags: [physics, ray-cast, picking, cursor, mesh, subpart, hover, placement]
---

Outcome: click a vessel and learn exactly which subpart and which point on its hull is under the cursor, in a frame
that survives the next frame. KSA-MeasureTools uses it to snap a ruler to hull points; unscience's Hot Pursuit mod
uses it to mount a camera 15 cm off the surface and keep it there as the vessel moves. Prerequisites: StarMap, and
Harmony if you hook mouse buttons. Both mods use the same call the engine uses for hover highlighting.

## Background: how the game does it

`Cursor.GetEgoRay(viewport)` is `viewport.GetCamera().ScreenToEgoRay(GetPosition(viewport))`, built on demand from
this frame's camera and the desktop cursor position captured in `PrepareImGui`:

```csharp
// current/decomp/KSA/Cursor.cs L27-L30
	public static Ray GetEgoRay(IViewport inViewport)
	{
		return inViewport.GetCamera().ScreenToEgoRay(GetPosition(inViewport));
	}
```

The stock hover loop in `Vehicle.UpdateHighlight` normalises the ray, builds `GetMatrixAsmb2Ego(camera)` and calls
`Part.RayCastEgo` for every part, offering the distance to the viewport's `PartPicker`:

```csharp
// current/decomp/KSA/Vehicle.cs L2903-L2927
	protected virtual void UpdateHighlight(IGameViewport inViewport)
	{
		if (Program.GetCursorMode() != GlfwCursorMode.Normal || inViewport.Mode != CameraMode.Orbit || Program.IsModalOpen() || !CursorTarget.IsHitTestViewport(inViewport))
		{
			if (Highlighted != null)
			{
				Highlighted.Selected = false;
				Highlighted = null;
			}
			return;
		}
		Camera camera = inViewport.GetCamera();
		Ray egoRay = Cursor.GetEgoRay(inViewport);
		egoRay.Direction = egoRay.Direction.NormalizeOrZero();
		double4x4 matrixVehicleAsmb2Ego = GetMatrixAsmb2Ego(camera);
		ReadOnlySpan<Part> parts = Parts.Parts;
		for (int i = 0; i < parts.Length; i++)
		{
			Part part = parts[i];
			if (part.RayCastEgo(in matrixVehicleAsmb2Ego, egoRay, out double minDistance, out double _, out double3 _, out double3 _, out double3 _, out double3 _, out Part closestSubPart, out Part _))
			{
				inViewport.PartPicker.TryOffer(minDistance, closestSubPart?.PartParent);
			}
		}
	}
```

`RayCastEgo` returns, in the closest subpart's local assembly frame, the hit position and the stored vertex normal
(not a face or interpolated normal), plus `closestSubPart`. Terrain and static objects are never tested.

## Lifecycle and timing

| Path | Hook | State |
|---|---|---|
| Click | `Program.OnMouseButton` prefix (`frame:input`) or an armed click read in `StarMapAfterGui` | Part transforms applied from the physics step; full scan |
| Preview | `StarMapAfterGui` (`frame:draw-ui`), throttled | The stock picker may be reused |

Check `ImGui.GetIO().WantCaptureMouse` so a click on any ImGui window never places. The stock picker is reset and
refilled inside `Universe.OnDrawUi`, which runs after the AfterGui hook, so during AfterGui `PartPicker.Part` is the
previous frame's result. Picking never touches simulated state. The
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) has the full order.

## Steps

1. Build the ray: `Cursor.GetEgoRay(Program.MainViewport)`, normalised (stock normalises before part casts; the
   watertight test needs a unit direction for metric distances).
2. Broad phase per vessel. Skip vessels whose bounding sphere projects too small (MeasureTools: under 24 px using
   `camera.GetObjectDiameterPixels`) or lies beyond the best hit (Hot Pursuit: `camera.GetPositionEgo(vehicle).Length()
   - vehicle.BoundingSphereRadiusBody > best`). `Vehicle.MeanRadius` is centred on the centre of mass, which is what
   `camera.GetPositionEgo(vehicle)` points at:

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

3. Narrow phase: for each part call `RayCastEgo` with the vehicle matrix and keep the globally nearest hit, with the
   subpart and the local position and normal:

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

   Hot Pursuit's picker is the same loop with its own broad phase, returning a normalised local normal:

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

4. Convert the hit into something stable. Store it in the **subpart's** frame with the vessel id and the subpart's
   `Part.InstanceId`, and resolve the live objects again every frame. Hot Pursuit's mount sits 15 cm off the surface
   along the normal:

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

   `InstanceId` is a runtime counter, not stable across loads: a persisted anchor needs a structural reference
   instead (unscience's `SavedPartReference`).
5. Optional hover shortcut (MeasureTools preview only): when the viewport is the hit-test viewport, the camera is in
   `Orbit` mode, the cursor is normal and no modal is open, reuse `viewport.PartPicker.Part` and re-cast only that
   part; fall back to the full scan when the single re-cast misses. Placement clicks always do the full scan.
6. In the editor, scan `EditingSpace.AllParts` and `UnattachedPartTrees` with `EditingSpace.GetMatrixAsmb2Ego(camera)`
   and never the system's vehicles (the original of an edited vehicle is still in the list and would be picked twice).

## Complete example

| File | Role |
|---|---|
| [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L181-L310) | `TryGetMeshHit`, `ScanFlightVehicles`, hover shortcut |
| [MeasureTools/Features/Measure/MapPicker.cs](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools/blob/aed7f874f1c925c10c733b1dee72bd04b9b1985c/MeasureTools/Features/Measure/MapPicker.cs#L574-L652) | `RaycastPart`, `ScanPartFeatures` |
| [hot-pursuit.lib/HotPursuitPicker.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitPicker.cs#L18-L58) | Ray, broad phase, `RayCastEgo` narrow phase |
| [hot-pursuit.lib/HotPursuitSubmod.Placement.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitSubmod.Placement.cs#L30-L121) | Armed click and mount basis |
| [hot-pursuit.lib/HotPursuitSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/hot-pursuit.lib/HotPursuitSubmod.cs#L238-L266) | Re-resolution by vehicle id and `InstanceId` |

## Variations and gotchas

- `PositionCompare` is only the first glTF primitive; multi-material meshes are hit-tested on primitive 0 only.
- No acceleration structure: gate by projected size, and throttle previews (MeasureTools: 45 ms).
- The returned normal is a vertex normal of the hit triangle; on coarse meshes a camera pointing along it may sit
  slightly off the face.
- Since 5482 `RayCastEgo` rejects on a part-level bounding sphere first, so moving subpart transforms without
  updating part bounds can make them unpickable.
- Duplicate vessel ids make a lookup return null on purpose; an anchor on one of two same-named vessels goes dormant.
- A part that leaves its vessel (staging, breakup) is no longer found under the stored id.
- The hover shortcut cannot work in the editor, and during AfterGui the stock picker is one frame stale.

## See also

- [Overview and comparison](overview.md)
- [Walk the ray over the height field](ray-cast-against-vessel-meshes-and-terrain-4.md), [layered cast](ray-cast-against-vessel-meshes-and-terrain-5.md)
- Mod features: [MeasureTools part mesh raycast picking](../../../../mods/ksa-measuretools/features/part-mesh-raycast-picking.md), [Hot Pursuit mesh ray placement](../../../../mods/unscience/features/hot-pursuit-mesh-ray-placement.md)
- The topic pick-objects-under-the-cursor (other batch) covers screen-space picking
