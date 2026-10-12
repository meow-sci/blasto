---
type: Mod Feature
title: Bypass the one-pixel render cull for chosen vessels
description: I Feel Seen keeps selected vessels and EVA kittens drawn at any distance with skip-original Harmony prefixes on Vehicle.GetWorldMatrix and Vehicle.UpdateRenderData, both resolved by name, that rebuild the native result without the pixel-size cull
mod: unscience
submod: i-feel-seen
category: rendering
techniques: [harmony-prefix, reflection]
hooks:
  - harmony-prefix KSA.Vehicle.GetWorldMatrix (resolved by name)
  - harmony-prefix KSA.Vehicle.UpdateRenderData (resolved by name)
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent, tracking UI)
lifecycle: [frame:render, frame:draw-ui]
game_types: [KSA.Vehicle, KSA.KittenEva, KSA.PartTree, KSA.Camera, KSA.IViewport, KSA.Program]
tags: [culling, render, distance, prefix, kitten, harmony]
---

KSA stops building render data for a vessel whose projected diameter is under one pixel. I Feel Seen
lets the player pick vessels (and EVA kittens) that are always submitted for drawing regardless of
distance, with a per-vessel "SeeMe" checkbox to toggle each one.[^i-feel-seen-submod]

Mechanism in one line: two Harmony prefixes check a `VehicleTracker`; for a tracked vessel they
compute what the original method would have produced if the pixel test had passed, write it, and
return `false` so the original (and its cull) does not run.

## How it works

**The native culls** (build 5554).[^ksa-vehicle]

- `Vehicle.UpdateRenderData(IViewport, int)` calls `IsLargeEnoughToRender(camera)` (pixel diameter of
  `2 * MeanRadius` at the camera distance, `< 1.0` rejects) and only then builds
  `GetMatrixAsmb2Ego(camera)` and calls `Parts.UpdateRenderData(...)`, plus physics debug overlays.
- `Vehicle.GetWorldMatrix(Camera)` returns `null` under the same pixel test, otherwise
  `CreateFromQuaternion(Body2Cce) * CreateTranslation(positionEgo)`. Its only caller is
  `KittenEva.UpdateRenderData`, which skips drawing the avatar on `null`.

**The prefixes** ([i-feel-seen.lib/IFeelSeenPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/i-feel-seen.lib/IFeelSeenPatches.cs#L52-L72)):

- `GetWorldMatrixPrefix(Vehicle __instance, Camera camera, ref float4x4? __result)`: for a tracked
  vessel, rebuild the same rotation-times-translation matrix from `camera.GetPositionEgo(__instance)`
  and `__instance.Body2Cce`, assign `__result`, return `false`.
- `UpdateRenderDataPrefix(Vehicle __instance, IViewport viewport, int inFrameIndex)`: for a tracked
  vessel, compute `GetMatrixAsmb2Ego(viewport.GetCamera())` and call
  `__instance.Parts.UpdateRenderData(in matrix, __instance.IsEditedVehicle, viewport, inFrameIndex)`,
  return `false`.
- Untracked vessels return `true` immediately and run the original.

**Resolution** ([i-feel-seen.lib/IFeelSeenPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/i-feel-seen.lib/IFeelSeenPatches.cs#L20-L34)). Targets are found with
`AccessTools.Method(typeof(Vehicle), "GetWorldMatrix")` and `"UpdateRenderData"` (no parameter list;
both are single overloads in 5554). The prefixes are found by reflection on the patch class.

**Tracker** ([i-feel-seen.lib/VehicleTracker.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/i-feel-seen.lib/VehicleTracker.cs)). A `List<TrackedVehicle>` of vessel
references and `SeeMe` flags; `IsTracked` is a linear scan by reference. In the supermod the
`VehicleTracker` instance is handed to the patcher before patching (`Patcher.IFeelSeenTracker`).

## Lifecycle and timing

| Patched method | Called from (build 5554) | Phase |
|---|---|---|
| `Vehicle.UpdateRenderData` for ordinary vessels | `Program.UpdateRenderingResources`, once per visible viewport (L4355) | `frame:render` (`Program.Render`, before command recording) |
| `KittenEva.UpdateRenderData` -> `base.UpdateRenderData` and `GetWorldMatrix` | `Program.RenderViewport` (L4491) for secondary viewports and `Program.RenderGame` (L4698) for the main one | `frame:render`, before passes are recorded |
| Tracker edits | Unscience panel in `[StarMapAfterGui]` | `frame:draw-ui` |

Everything runs on the main thread, so the tracker needs no locking: the UI edits it during the GUI phase
and the render-data build reads it later in the same frame. Nothing here touches simulation state, so the
point in the frame only has to be "before render data is consumed".[^ksa-program]

`Program.RefreshVehiclesInFrame` puts every vessel of the current system into `VehiclesInFrame` without
a distance test, so the pixel cull inside `UpdateRenderData` is the cull that matters for vessel
meshes.[^ksa-program]

## Key code

| Path | Role |
|---|---|
| [i-feel-seen.lib/IFeelSeenPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/i-feel-seen.lib/IFeelSeenPatches.cs) | Patch install/remove and both prefixes |
| [i-feel-seen.lib/VehicleTracker.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/i-feel-seen.lib/VehicleTracker.cs) | Tracked vessels and `SeeMe` flags |
| [i-feel-seen.lib/IFeelSeenSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/i-feel-seen.lib/IFeelSeenSubmod.cs) | Panel: filterable vessel combo, tracked table |
| [i-feel-seen.lib/IFeelSeenSubmod.Saves.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/i-feel-seen.lib/IFeelSeenSubmod.Saves.cs) | `i-feel-seen` scene record (ids and flags) |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L83) | Supermod install with the shared tracker |
| [i-feel-seen/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/i-feel-seen/Patcher.cs) | Standalone development host install |
| [i-feel-seen/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/i-feel-seen/README.md) | Repo README (illustrative pseudocode, see pitfalls) |
| [scope/vehicle-physics.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/vehicle-physics.md) | Authors' integration scope (I Feel Seen section) |

## Snippets

Both prefixes:

```csharp
// i-feel-seen.lib/IFeelSeenPatches.cs L52-L72
    private static bool GetWorldMatrixPrefix(Vehicle __instance, Camera camera, ref float4x4? __result)
    {
        if (_tracker == null || !_tracker.IsTracked(__instance))
            return true;

        double3 vector = camera.GetPositionEgo(__instance);
        float4x4 translation = float4x4.CreateTranslation(float3.Pack(in vector));
        float4x4 rotation = float4x4.CreateFromQuaternion(floatQuat.Pack(__instance.Body2Cce));
        __result = rotation * translation;
        return false;
    }

    private static bool UpdateRenderDataPrefix(Vehicle __instance, IViewport viewport, int inFrameIndex)
    {
        if (_tracker == null || !_tracker.IsTracked(__instance))
            return true;

        double4x4 matrixAsmb2Ego = __instance.GetMatrixAsmb2Ego(viewport.GetCamera());
        __instance.Parts.UpdateRenderData(in matrixAsmb2Ego, __instance.IsEditedVehicle, viewport, inFrameIndex);
        return false;
    }
```

Name-based resolution and install:

```csharp
// i-feel-seen.lib/IFeelSeenPatches.cs L20-L34
    public static void Apply(Harmony harmony, VehicleTracker tracker)
    {
        _tracker = tracker;

        _getWorldMatrixPrefix = typeof(IFeelSeenPatches).GetMethod(nameof(GetWorldMatrixPrefix), BindingFlags.NonPublic | BindingFlags.Static)!;
        _updateRenderDataPrefix = typeof(IFeelSeenPatches).GetMethod(nameof(UpdateRenderDataPrefix), BindingFlags.NonPublic | BindingFlags.Static)!;

        _vehicleGetWorldMatrix = AccessTools.Method(typeof(Vehicle), "GetWorldMatrix");
        _vehicleUpdateRenderData = AccessTools.Method(typeof(Vehicle), "UpdateRenderData");

        harmony.Patch(_vehicleGetWorldMatrix, prefix: new HarmonyMethod(_getWorldMatrixPrefix));
        harmony.Patch(_vehicleUpdateRenderData, prefix: new HarmonyMethod(_updateRenderDataPrefix));

        Console.WriteLine("i-feel-seen.lib: patches applied");
    }
```

The game methods being replaced for tracked vessels (build 5554):

```csharp
// current/decomp/KSA/Vehicle.cs L3694-L3726
	public float4x4? GetWorldMatrix(Camera camera)
	{
		double3 vector = camera.GetPositionEgo(this);
		double objectDiameterPixels = camera.GetObjectDiameterPixels(2.0 * MeanRadius, vector.Length());
		if (objectDiameterPixels < 1.0)
		{
			return null;
		}
		float4x4 float4x5 = float4x4.CreateTranslation(float3.Pack(in vector));
		float4x4 float4x6 = float4x4.CreateFromQuaternion(floatQuat.Pack(Body2Cce));
		return float4x6 * float4x5;
	}

	public bool IsLargeEnoughToRender(Camera inCamera)
	{
		double3 positionEgo = inCamera.GetPositionEgo(this);
		return !(inCamera.GetObjectDiameterPixels(2.0 * MeanRadius, positionEgo.Length()) < 1.0);
	}

	public virtual void UpdateRenderData(IViewport viewport, int inFrameIndex)
	{
		Camera camera = viewport.GetCamera();
		if (IsLargeEnoughToRender(camera))
		{
			double4x4 matrixAsmb2Ego = GetMatrixAsmb2Ego(camera);
			Parts.UpdateRenderData(in matrixAsmb2Ego, IsEditedVehicle, viewport, inFrameIndex);
			if (viewport.IsMain() && Program.ShowPhysicsDebug && IsInSameBubbleAs(Program.ControlledVehicle) && _showTerrain)
			{
				SubmitTerrainDebugOverlay(viewport);
				SubmitClutterTerrainDebugOverlay(viewport);
			}
		}
	}
```

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `Vehicle.GetWorldMatrix(Camera)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3694-L3705) | Prefix target (kitten avatar placement) |
| `Vehicle.UpdateRenderData(IViewport, int)` (virtual) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3713-L3726) | Prefix target |
| `Vehicle.IsLargeEnoughToRender(Camera)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3707-L3711) | The cull being bypassed |
| `Vehicle.GetMatrixAsmb2Ego(Camera)`, `Body2Cce`, `IsEditedVehicle`, `Parts` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1270) | Rebuilding the original's work |
| `PartTree.UpdateRenderData(ref readonly double4x4, bool, IViewport, int)` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1158) | Part, dynamic, glass and light render data |
| `KittenEva.UpdateRenderData` | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L1062-L1078) | Override calling `base` then `GetWorldMatrix` |
| `Camera.GetPositionEgo(IPosition)` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L236) | Camera-relative position |
| `IViewport.GetCamera()` | [current/decomp/KSA/IViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IViewport.cs) | Camera of the viewport |
| `Program.UpdateRenderingResources`, `RenderViewport`, `RenderGame`, `RefreshVehiclesInFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4340-L4358) | Call sites and frame placement |

## Pitfalls and version notes

- Both targets are resolved by string, so a rename or a second overload surfaces as a patch failure at
  load (logged by the supermod's per-feature `TryApply`), not a compile error. Both are single overloads
  in 5554.
- The `UpdateRenderData` prefix re-implements the original body. Anything KSA adds there later (in 5554:
  the physics debug terrain and clutter overlays) is skipped for tracked vessels until the prefix is
  updated.
- The override removes the cull; it does not enlarge anything. A tracked vessel far away is still drawn at
  its true, possibly sub-pixel, size. Godzilla's visual scaling can enlarge it; its `GetWorldMatrix`
  postfix still runs after this skipping prefix, and its part-tree prefix still runs inside the
  `Parts.UpdateRenderData` call made here.
- For a tracked `KittenEva`, the `UpdateRenderData` prefix fires through the override's `base` call, after
  the override started; `GetWorldMatrix` is non-virtual and intercepted directly.
- Parachute canopies are drawn by `Vehicle.UpdateParachuteRenderData`, which has no pixel cull and is not
  patched.
- The repo's `i-feel-seen/README.md` contains illustrative pseudocode with members that do not exist in
  KSA (`ComputeWorldMatrix`, `ForceUpdateRenderData`); the authors' own scope document flags it as
  aspirational. Use the library code.[^vehicle-physics]

## See also

- [Godzilla visual scaling](./godzilla-visual-scaling.md) (patches the same two methods)
- [IVA force render](./lib-iva-force-render.md) and [part render filter](./lib-part-render-filter.md)
- [I Feel Seen sub-mod](../submods/i-feel-seen.md) and [mod overview](../overview.md)
- [Skip the one-pixel cull with prefixes on Vehicle.GetWorldMatrix and UpdateRenderData](../../../howto/rendering/meshes/keep-vessels-rendered-at-any-distance/keep-vessels-rendered-at-any-distance-1.md) - how-to article demonstrating this feature

[^i-feel-seen-submod]: i-feel-seen.lib/IFeelSeenSubmod.cs
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^vehicle-physics]: scope/vehicle-physics.md
