---
type: How-To
title: Skip the one-pixel cull with prefixes on Vehicle.GetWorldMatrix and UpdateRenderData
description: Keep chosen vessels and EVA kittens drawn at any distance by rebuilding the native result without the pixel test in two skip-original Harmony prefixes resolved by name
category: rendering
group: meshes
topic: keep-vessels-rendered-at-any-distance
approach: 1
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/i-feel-seen-render-distance-override]
techniques: [harmony-prefix]
hooks:
  - harmony-prefix KSA.Vehicle.GetWorldMatrix
  - harmony-prefix KSA.Vehicle.UpdateRenderData
  - starmap StarMapAfterGui
lifecycle: [frame:render, frame:draw-ui]
game_types: [KSA.Vehicle, KSA.KittenEva, KSA.PartTree, KSA.Camera, KSA.IViewport, KSA.Program]
tags: [cull, render-distance, pixel-size, vehicle, kitten, skip-original]
---
Outcome: vessels you choose are always submitted for drawing, however far away or small on screen. The game stops building render data for a vessel whose projected diameter is under one pixel; two prefixes reproduce what the original would have produced had the test passed and skip the original.

Demonstrated by unscience's I Feel Seen ([Bypass the one-pixel render cull for chosen vessels](../../../../mods/unscience/features/i-feel-seen-render-distance-override.md)). Prerequisites: StarMap, Harmony. Note that this removes the cull, it does not enlarge anything: a tracked vessel is still drawn at its true, possibly sub-pixel, size (see [approach 4](keep-vessels-rendered-at-any-distance-4.md) to enlarge it).

## Background: how the game does it

Two methods on `Vehicle` hold the cull ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3694-L3726)):

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

- `UpdateRenderData(IViewport, int)` (virtual) builds `GetMatrixAsmb2Ego(camera)` and calls `Parts.UpdateRenderData(...)` only when `IsLargeEnoughToRender` (pixel diameter of `2 * MeanRadius` at the camera distance, `< 1.0` rejects).
- `GetWorldMatrix(Camera)` returns `null` under the same pixel test. Its only caller in this build is `KittenEva.UpdateRenderData`, which skips drawing the avatar on `null` ([current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L1062-L1078)).

`Program.RefreshVehiclesInFrame` puts every vessel of the current system into `VehiclesInFrame` without a distance test, so the pixel cull inside `UpdateRenderData` is the cull that matters for vessel meshes.

## Lifecycle and timing

| Patched method | Called from | Phase |
|---|---|---|
| `Vehicle.UpdateRenderData` for ordinary vessels | `Program.UpdateRenderingResources`, once per visible viewport ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4347-L4357)) | `frame:render`, before command recording |
| `KittenEva.UpdateRenderData` then `base.UpdateRenderData` and `GetWorldMatrix` | `Program.RenderViewport` L4491 (secondary views) and `Program.RenderGame` L4698 (main view) | `frame:render`, before passes are recorded |
| Tracker edits | tracking UI in `[StarMapAfterGui]` | `frame:draw-ui` |

Everything is on the main thread, so the tracker needs no locking: the UI edits it during the GUI phase and render-data building reads it later in the same frame. The point in the frame only has to be "before render data is consumed". See row 22 of [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence).

## Steps

1. **Keep a set of tracked vessels** (a list of vessel references with a flag, or a set of ids). Reference equality is enough for a small set.

2. **Resolve and patch both targets by name** with prefixes taking `__instance` and the original parameter names. Both are single overloads in this build:

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

3. **Rebuild the native result without the pixel test and return `false`**; untracked vessels return `true` immediately:

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

4. **Unpatch by exact patch method** on unload:

   ```csharp
// i-feel-seen.lib/IFeelSeenPatches.cs L36-L50
public static void Remove(Harmony harmony)
{
    if (_vehicleGetWorldMatrix != null && _getWorldMatrixPrefix != null)
        harmony.Unpatch(_vehicleGetWorldMatrix, _getWorldMatrixPrefix);
    if (_vehicleUpdateRenderData != null && _updateRenderDataPrefix != null)
        harmony.Unpatch(_vehicleUpdateRenderData, _updateRenderDataPrefix);

    _tracker = null;
    _vehicleGetWorldMatrix = null;
    _vehicleUpdateRenderData = null;
    _getWorldMatrixPrefix = null;
    _updateRenderDataPrefix = null;

    Console.WriteLine("i-feel-seen.lib: patches removed");
}
```

5. **Drive the tracker from the UI.** I Feel Seen exposes a filterable vessel combo and a per-vessel "SeeMe" checkbox in `[StarMapAfterGui]` and scene-saves the ids and flags.

## Complete example

| File | Role |
|---|---|
| [i-feel-seen.lib/IFeelSeenPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/i-feel-seen.lib/IFeelSeenPatches.cs) | Patch install, removal and both prefixes |
| [i-feel-seen.lib/VehicleTracker.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/i-feel-seen.lib/VehicleTracker.cs) | Tracked vessels and `SeeMe` flags |
| [i-feel-seen.lib/IFeelSeenSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/i-feel-seen.lib/IFeelSeenSubmod.cs) | Panel: filterable vessel combo, tracked table |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L83) | Supermod install with the shared tracker |

## Variations and gotchas

- **Re-implemented body.** The `UpdateRenderData` prefix re-implements the original; anything KSA adds there later (in this build the physics-debug terrain and clutter overlays) is skipped for tracked vessels until the prefix is updated.
- **Name-based lookup.** A rename or a second overload surfaces as a patch failure at load, not a compile error. Pass the parameter list (as in [approach 2](keep-vessels-rendered-at-any-distance-2.md)) to be exact.
- **Kitten fires through `base`.** For a tracked `KittenEva`, the `UpdateRenderData` prefix fires through the override's `base` call; `GetWorldMatrix` is non-virtual and intercepted directly.
- **Parachute canopies** are drawn by `Vehicle.UpdateParachuteRenderData`, which has no pixel cull and is not patched.
- **Other patches coexist.** A visual-scale transpiler (approach 4) still runs inside `Parts.UpdateRenderData`; its `GetWorldMatrix` postfix still runs after this skipping prefix.
- **README caution.** The repo's `i-feel-seen/README.md` contains illustrative pseudocode with members that do not exist in KSA; use the library code.

## See also

- [Overview](overview.md)
- [Approach 2](keep-vessels-rendered-at-any-distance-2.md), [3](keep-vessels-rendered-at-any-distance-3.md), [4](keep-vessels-rendered-at-any-distance-4.md)
- [Bypass the one-pixel render cull for chosen vessels](../../../../mods/unscience/features/i-feel-seen-render-distance-override.md)
- [Hide part meshes or subparts without removing them](../hide-parts-or-subparts-from-rendering/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
