---
type: Mod Feature
title: Anchoring a world-space quad to a part or to the camera
description: Place a quad in KSA's camera-relative ego space from a vehicle part's position and rotation plus a user offset, follow the same Part object through decoupling and docking, or pin it in view space in front of the camera as a billboard
mod: purrtty
category: rendering
techniques: [vulkan-pipeline]
hooks: [harmony-postfix KSA.SuperMeshRenderSystem.RenderTranslucencyPass, starmap StarMapAfterGui]
lifecycle: [frame:render, frame:draw-ui]
game_types:
  - KSA.Vehicle
  - KSA.Part
  - KSA.Camera
  - KSA.Universe
  - KSA.Program
  - KSA.CelestialSystem
  - Brutal.Numerics.float4x4
  - Brutal.Numerics.doubleQuat
tags: [ego-space, transforms, parts, vehicles, camera, billboard, decouple, in-world]
---

An in-world terminal is either bolted to the rocket (a screen on a capsule wall, a panel floating
next to an engine) or held in front of the camera like a HUD billboard. The "Part" anchor picks a
vehicle, a part and optionally a sub-part, then applies an offset, a rotation and a size in meters;
the "Screen" anchor picks a distance, screen offset, size and tilt. Placement edits in the Config
window apply live because the quad reads its record every frame.

## How it works

**Part mode** (`InWorldQuad.TryComputePartModel` / `ComputePartTransform`):[^inworld-quad]

1. Resolve the vehicle: `VehicleLookup.Resolve(id)` scans `Universe.CurrentSystem.All` for a
   `Vehicle` with that `Id`; an empty id, or an id that is not found, means
   `Program.ControlledVehicle`.[^vehicle-lookup]
2. Resolve the part: the record's part id (first match among `vehicle.Parts.Parts`), then an optional
   sub-part id among `part.SubParts`; with no part id, or none matching, the first top-level part is used so the panel is visible before a pick.
3. Build the transform in KSA's camera-relative ego space:
   `vehMat = vehicle.GetMatrixAsmb2Ego(camera)`, `partPos = part.PositionEgo(in vehMat)`,
   `partRot = part.Asmb2Ego(vehicle.Asmb2Ego)`, then `partEgo = R(partRot) * T(partPos)`. Position
   and rotation are taken separately instead of the combined `MatrixAsmb2Ego` so the part's own
   scale does not leak into the panel size.[^ksa-vehicle][^ksa-part]
4. `model = Scale(width, height, 1) * userRot(X, Y, Z degrees) * T(userOffset) * partEgo` (row-vector
   order: rightmost is applied last), and the draw uses `model * camera.MVP.viewProjection`.

**Following a part through staging.** Once a specific part id resolves, the quad caches the `Part`
object. On later frames it calls `VehicleLookup.FindContaining(part)`, a reference-identity search
over every vehicle's parts and sub-parts, and computes the transform through whichever vehicle owns
the part now. KSA decoupling and docking move the same `Part` instance into the new vehicle, so the
panel stays on its part (gotcha 31). The cache is dropped when the record's ids change or the part
disappears; the empty-target default never caches, so it keeps following the controlled vehicle.[^gotchas]

**Billboard mode** (`TryComputeBillboardModel`): `model = Scale * userRot * T(offsetX, offsetY,
-distance)` in view space, drawn with `model * camera.MVP.projection` (no view matrix), so it stays
fixed relative to the camera. `BillboardAlwaysOnTop` selects the no-depth-test pipeline.

**Picking space.** For ray picking the billboard model is brought to ego space with
`modelView * camera.VPInv.view`, so both modes intersect rays in the same frame (see
[ray-pick focus](./ray-pick-focus-for-world-quads.md)).

## Lifecycle and timing

- The model is computed when the quad is recorded, inside the `RenderTranslucencyPass` postfix
  (`frame:render`), on the main thread. Vehicle and part state there is the main-thread state after
  this frame's `Apply*Solvers` in `Program.PrepareFrame`, the same state KSA's renderer draws, and
  the main camera was updated in `frame:viewports`, so the panel moves in lockstep with the part.
- Picking computes the same transform during `frame:draw-ui` (`InWorldTerminalManager.OnAfterGui`).
- Only reads happen; no simulation state is written, so it is safe while the next physics step runs
  on worker threads.

## Key code

| Path | Role |
|---|---|
| [purrTTY.GameMod/InWorld/Display/InWorldQuad.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Display/InWorldQuad.cs#L287-L364) | Part resolution, identity follow, ego transform |
| [purrTTY.GameMod/InWorld/Display/InWorldQuad.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Display/InWorldQuad.cs#L366-L391) | Billboard model in view space |
| [purrTTY.GameMod/InWorld/Display/InWorldQuad.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Display/InWorldQuad.cs#L134-L146) | MVP choice per mode |
| [purrTTY.GameMod/InWorld/VehicleLookup.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/VehicleLookup.cs) | Vehicle by id, part by identity |
| [purrTTY.GameMod/InWorld/Settings/InWorldTerminalRecord.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Settings/InWorldTerminalRecord.cs) | Placement fields and defaults |
| [purrTTY.GameMod/InWorld/UI/InWorldManagerUI.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/UI/InWorldManagerUI.cs) | Vehicle, part and sub-part pickers and placement sliders |

## Snippets

```csharp
// purrTTY.GameMod/InWorld/Display/InWorldQuad.cs L337-L364
    private bool ComputePartTransform(Camera camera, Vehicle vehicle, Part part, out float4x4 model)
    {
        // Pull the part's ego-space rotation + position separately rather than its
        // combined MatrixAsmb2Ego: the combined matrix bakes in the part's own scale,
        // which we exclude so Width/Height are the sole size source. The transform flows
        // through whichever vehicle currently owns the part.
        double4x4 vehMat    = vehicle.GetMatrixAsmb2Ego(camera);
        double3   partPos   = part.PositionEgo(in vehMat);
        doubleQuat partRotD = part.Asmb2Ego(vehicle.Asmb2Ego);

        float4x4 partRotMat   = float4x4.CreateFromQuaternion(floatQuat.Pack(in partRotD));
        float4x4 partTransMat = float4x4.CreateTranslation(float3.Pack(in partPos));
        // Row-vector convention (mvp = model * viewProjection): rotate first, then
        // translate — places verts at the part's origin in ego space.
        float4x4 partEgo = partRotMat * partTransMat;

        const float deg2rad = MathF.PI / 180f;
        float4x4 userRot = float4x4.CreateRotationX(_settings.PartRotationX * deg2rad)
                         * float4x4.CreateRotationY(_settings.PartRotationY * deg2rad)
                         * float4x4.CreateRotationZ(_settings.PartRotationZ * deg2rad);
        float4x4 userTrans = float4x4.CreateTranslation(
            new float3(_settings.PartOffsetX, _settings.PartOffsetY, _settings.PartOffsetZ));
        float4x4 scaleMat = float4x4.CreateScale(_settings.PartWidthMeters, _settings.PartHeightMeters, 1f);

        // v_local → scale → userRot → userTrans → partEgo → ego
        model = scaleMat * userRot * userTrans * partEgo;
        return true;
    }
```

```csharp
// current/decomp/KSA/Vehicle.cs L1269-L1281
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public double4x4 GetMatrixAsmb2Ego(Camera camera)
	{
		return GetMatrixAsmb2Ego(camera.GetPositionEgo(this));
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public double4x4 GetMatrixAsmb2Ego(double3 bodyPositionEgo)
	{
		doubleQuat asmb2Ego = Asmb2Ego;
		double3 position = bodyPositionEgo - CenterOfMassAsmb.Transform(asmb2Ego);
		return double4x4.CreateFromQuaternion(asmb2Ego) * double4x4.CreateTranslation(position);
	}
```

## Game APIs touched

| API | Where | Purpose |
|---|---|---|
| `Vehicle.GetMatrixAsmb2Ego(Camera)`, `Vehicle.Asmb2Ego` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1269-L1281) | Vehicle assembly frame to ego space |
| `Part.PositionEgo(ref readonly double4x4)`, `Part.Asmb2Ego(doubleQuat)`, `Part.SubParts`, `Part.Id` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1203-L1211) | Part position and rotation in ego space |
| `Camera.MVP` (`viewProjection`, `projection`), `Camera.VPInv.view` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L63-L65) | Projection per mode, view-to-ego for picking |
| `Universe.CurrentSystem`, `Program.ControlledVehicle`, `Program.GetMainCamera()` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L114) | Vehicle enumeration and defaults[^ksa-universe] |

## Pitfalls and version notes

- Ego space is camera-relative (floating origin). Compute transforms every frame from the current
  camera; never cache ego positions.
- The matrices use row vectors (`model * viewProjection`); composing in column-vector order mirrors
  or misplaces the quad.
- An unknown vehicle id silently falls back to the controlled vehicle.
- Part identity is per session. Saved layouts persist ids, and only `Vehicle.Id` is reliably stable
  across restarts; part ids resolve to the first match (gotcha 29).
- Billboard forward direction is `-distance` along view Z; the code comment flags the sign as the
  likely first-run surprise.
- `docs/KSA_CAMERA_CODE_ANALYSIS.md` in the repo is general camera background from an earlier
  camera-control experiment; purrTTY's current code only reads the main camera.[^camera-analysis]

## See also

- [World-space textured quad pipeline](./world-space-quad-pipeline-custom-shader.md)
- [Ray-pick focus for world quads](./ray-pick-focus-for-world-quads.md)
- [Draw after the translucency pass](./draw-after-translucency-pass-postfix.md)
- [Saved terminal layouts](./saved-terminal-layouts.md)
- [purrTTY overview](../overview.md)
- [Premultiplied-alpha translucent quad after the translucency pass](../../../howto/rendering/textures-and-decals/draw-a-textured-quad-in-the-world/draw-a-textured-quad-in-the-world-3.md) - how-to article demonstrating this feature

[^inworld-quad]: purrTTY.GameMod/InWorld/Display/InWorldQuad.cs
[^vehicle-lookup]: purrTTY.GameMod/InWorld/VehicleLookup.cs
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^gotchas]: purrTTY docs/gotchas.md
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^camera-analysis]: purrTTY docs/KSA_CAMERA_CODE_ANALYSIS.md
