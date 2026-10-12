---
type: Mod Feature
title: Submit EVA kitten equipment before the part-instance upload
description: A prefix on PartModelRenderer.UpdateRenderData pushes each equipped kitten's part tree through a nonvirtual call to the live patched Vehicle.UpdateRenderData before the instance lists are uploaded, and a Priority.First prefix skips the late duplicate while the avatar keeps its later draw
mod: unscience
submod: iron-man
category: rendering
techniques: [harmony-prefix, dynamic-method]
hooks:
  - harmony-prefix KSA.PartModelRenderer.UpdateRenderData
  - harmony-prefix KSA.Vehicle.UpdateRenderData (Priority.First)
lifecycle: [frame:render]
game_types: [KSA.Program, KSA.Vehicle, KSA.KittenEva, KSA.PartModelRenderer, KSA.PartTree, KSA.KittenRenderable]
tags: [rendering, part-models, instancing, kitten, eva, harmony, dynamic-method, mod-compatibility]
---

Equipment that Iron Man bolts onto an EVA kitten (tanks, engines, RCS) lives in the kitten's
`PartTree`, but the game never draws a kitten's part tree at the moment part instances are
collected. Without this patch the equipment is invisible in flight. Iron Man submits it in the
correct phase, exactly once per view, without disturbing the avatar or other mods' render patches.
This applies to any kitten whose backpack root owns Iron Man nodes, even in EVA mode and after a
reload.[^scope-iron-man]

Mechanism in one line: a prefix on `PartModelRenderer.UpdateRenderData` (the per-viewport upload of
part instances) first calls the base `Vehicle.UpdateRenderData` nonvirtually for each equipped
kitten; a `Priority.First` prefix on `Vehicle.UpdateRenderData` then skips the base body when
`KittenEva` calls it again later.

## How it works

The flight render order in build 5554 has three relevant steps:

1. `UpdateRenderingResources` walks `VehiclesInFrame` per visible viewport and calls
   `vehicle.UpdateRenderData(viewport, frameIndex)` for every vehicle that is **not** a
   `KittenEva`; then it calls `PartModelRenderer.UpdateRenderData`, which writes every part model's
   instance list to the GPU (`WriteInstancesToGpu`).[^ksa-program][^ksa-part-model-renderer]
2. Later, during command recording, `RenderViewport` (secondary views) and `RenderGame` (main view)
   call the virtual `UpdateRenderData` only for `KittenEva` vehicles. `KittenEva.UpdateRenderData`
   calls `base.UpdateRenderData` (the part tree, via `Parts.UpdateRenderData`) and then
   `KittenRenderable.UpdateRenderData`, which animates and draws the avatar into SuperMesh
   buckets.[^ksa-kitten-eva][^ksa-vehicle]
3. `PostRender` calls `ClearFrameData`, which clears every part model's instance lists.

A kitten's own parts are therefore appended after the upload and cleared before the next one. For
the stock backpack this is harmless (its look comes from the character model); for added equipment
it means nothing is drawn.

Iron Man's two prefixes
([iron-man.lib/IronManRenderPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManRenderPatches.cs)):

| Hook | Behaviour |
|---|---|
| prefix `PartModelRenderer.UpdateRenderData(IViewport, int)` | For each `KittenEva` in `Program.VehiclesInFrame` whose root has an Iron Man node: set a `[ThreadStatic] _earlyVehicle` marker, call `_submitParts(kitten, viewport, frameIndex)`, clear the marker |
| prefix `Vehicle.UpdateRenderData` (`Priority.First`) | Return `false` (skip the base body) for an equipped `KittenEva`, unless it is the `_earlyVehicle` being submitted early or the editor is open |

`_submitParts` is a `DynamicMethod` whose IL is `ldarg.0; ldarg.1; ldarg.2; call
Vehicle.UpdateRenderData; ret`. The comment in the code explains the choice of all three options:

- an ordinary C# call `((Vehicle)kitten).UpdateRenderData(...)` dispatches virtually into
  `KittenEva.UpdateRenderData`, which would also animate and draw the avatar a second time;
- a Harmony reverse patch copies the original body and so bypasses other mods' patches on
  `Vehicle.UpdateRenderData` (I Feel Seen replaces the body with its own distance-independent
  render through a prefix);
- an IL `call` (nonvirtual) to the method enters Harmony's patched replacement, so every other
  prefix and transpiler still runs for the early submission.

In the late pass, `KittenEva.UpdateRenderData` still calls `base.UpdateRenderData`; Iron Man's
`Priority.First` prefix returns `false` so the part tree is not appended again, and the avatar draw
that follows in `KittenEva.UpdateRenderData` is untouched. The managed test installs a second
skip-style render prefix before and after Iron Man and asserts that it runs exactly once per frame,
early, in both orders.[^iron-man-flight-tests]

## Lifecycle and timing

| Step | Location (build 5554) | Lifecycle |
|---|---|---|
| `Program.Render` -> `UpdateRenderingResources(ResourceFrameIndex)` | `Program.cs` L2443, call at L2468 | `frame:render` |
| Early loop skips `KittenEva` | `Program.cs` L4349-L4357 | `frame:render` |
| `PartModelRenderer.UpdateRenderData` (prefix runs here, then upload) | `Program.cs` L4374; `PartModelRenderer.cs` L807 | `frame:render` |
| Late `KittenEva.UpdateRenderData` (Vehicle prefix skips the part tree) | `RenderViewport` L4483-L4492, `RenderGame` L4690-L4699 | `frame:render` |
| `ClearFrameData` clears instance lists | `PostRender` L2344 -> `ClearFrameData` L4438-L4440 | `frame:post-render` |

All of it is main-thread code: `UpdateRenderingResources` runs inside `Program.Render` just before
`RenderGame`/`RenderEditor` record commands (lifecycle value `frame:render`: the resource-update
step that precedes command recording). The early submission happens per visible viewport, because
`PartModelRenderer.UpdateRenderData` is called once per viewport, so equipment appears once in every
view, including secondary viewports.
Because the submission goes through `Parts.UpdateRenderData`, the cached `PartTreeRenderData` and the
`Compose*` calls are the stock ones, and anything that hooks them (for example the shared
[part render filter](./lib-part-render-filter.md)) applies to the equipment too. The marker is
`[ThreadStatic]` and saved/restored around each call, so a nested or re-entrant render cannot leak
it.

When the editor is open (`Program.Editor != null`) both prefixes stand down: the editor branch of
`UpdateRenderingResources` draws `EditingSpace.Parts` directly, and the avatar is handled by
[Iron Man kitten vehicle editing](./iron-man-kitten-vehicle-editing.md).

## Key code

| Path | Role |
|---|---|
| [iron-man.lib/IronManRenderPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManRenderPatches.cs) | Both prefixes and the nonvirtual dispatch delegate |
| [iron-man.lib/IronManFlightPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManFlightPatches.cs#L40) | Installs and removes the render patches with the flight patches |
| [iron-man.lib/IronManConnectors.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManConnectors.cs#L18) | `IsOwned`, the "has equipment data" test |
| [iron-man-flight.tests/Program.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man-flight.tests/Program.cs#L10-L60) | Coexistence check with another skip prefix in both install orders |

## Snippets

Building the nonvirtual call into the live patched method and installing both prefixes:

```csharp
// iron-man.lib/IronManRenderPatches.cs L18-L38
        var updateVehicle = AccessTools.DeclaredMethod(typeof(Vehicle), nameof(Vehicle.UpdateRenderData))
            ?? throw new MissingMethodException(typeof(Vehicle).FullName, nameof(Vehicle.UpdateRenderData));
        var updateBatch = AccessTools.Method(typeof(PartModelRenderer), nameof(PartModelRenderer.UpdateRenderData))
            ?? throw new MissingMethodException(typeof(PartModelRenderer).FullName, nameof(PartModelRenderer.UpdateRenderData));
        // A nonvirtual call enters the live patched Vehicle method, preserving other mods'
        // prefixes (notably I Feel Seen). A reverse patch would freeze/bypass those patches;
        // an ordinary C# call would dispatch into KittenEva and advance its animation twice.
        var method = new DynamicMethod("IronManSubmitVehicleParts", typeof(void),
            new[] { typeof(Vehicle), typeof(IViewport), typeof(int) }, typeof(IronManRenderPatches), true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, updateVehicle);
        il.Emit(OpCodes.Ret);
        _submitParts = method.CreateDelegate<Action<Vehicle, IViewport, int>>();
        harmony.Patch(updateVehicle, prefix: new HarmonyMethod(typeof(IronManRenderPatches), nameof(VehiclePrefix))
        {
            priority = Priority.First,
        });
        harmony.Patch(updateBatch, prefix: new HarmonyMethod(typeof(IronManRenderPatches), nameof(BeforePartUpload)));
```

Skip the late duplicate; submit early, just before the upload:

```csharp
// iron-man.lib/IronManRenderPatches.cs L50-L75
    private static bool VehiclePrefix(Vehicle __instance) =>
        !_applied || ReferenceEquals(_earlyVehicle, __instance) || Program.Editor != null || __instance is not KittenEva kitten ||
        !HasEquipmentData(kitten);

    private static void BeforePartUpload(IViewport viewport, int frameIndex)
    {
        if (!_applied || Program.Editor != null) return;
        foreach (var vehicle in Program.VehiclesInFrame)
        {
            if (vehicle is not KittenEva kitten || !HasEquipmentData(kitten)) continue;
            var previous = _earlyVehicle;
            try
            {
                // Program omits every KittenEva from this early submission phase. Its later
                // virtual UpdateRenderData also draws the character into SuperMesh buckets;
                // invoking only the Vehicle method here avoids advancing animation twice.
                _earlyVehicle = kitten;
                _submitParts!(kitten, viewport, frameIndex);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"iron-man: equipment render submission failed: {ex.Message}");
            }
            finally { _earlyVehicle = previous; }
        }
    }
```

The game's early loop and upload; kittens are excluded on purpose:

```csharp
// current/decomp/KSA/Program.cs L4347-L4374
			if (Editor == null)
			{
				ReadOnlySpan<Vehicle> vehiclesInFrame = VehiclesInFrame;
				for (int k = 0; k < vehiclesInFrame.Length; k++)
				{
					Vehicle vehicle = vehiclesInFrame[k];
					if (!(vehicle is KittenEva))
					{
						vehicle.UpdateRenderData(viewport2, frameIndex);
					}
				}
			}
// ...
			Universe.UpdateRenderData(viewport2, frameIndex);
			Profiler.MainThread.End();
			Profiler.MainThread.Begin(Profiler.Tag.UpdateRenderDataPartModels);
			PartModelRenderer.UpdateRenderData(viewport2, frameIndex);
```

The late kitten path that both draws the avatar and re-submits the part tree:

```csharp
// current/decomp/KSA/KittenEva.cs L1062-L1066
	public override void UpdateRenderData(IViewport viewport, int inFrameIndex)
	{
		base.UpdateRenderData(viewport, inFrameIndex);
		float4x4? worldMatrix = GetWorldMatrix(viewport.GetCamera());
		if (worldMatrix.HasValue)
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Program.UpdateRenderingResources` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4308) | Early per-viewport vehicle loop and upload (L4338-L4374) |
| `Program.RenderViewport`, `Program.RenderGame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4483-L4492) | Late `KittenEva` render calls (also L4690-L4699) |
| `Program.ClearFrameData` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4438-L4446) | Called from `PostRender` (L2344); clears instance lists |
| `Program.VehiclesInFrame`, `Program.Editor` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4349) | Vehicles to submit; editor gate |
| `PartModelRenderer.UpdateRenderData(IViewport, int)`, `ClearFrameData` | [current/decomp/KSA/PartModelRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelRenderer.cs#L807-L849) | Instance upload (prefix target) and per-frame clear |
| `Vehicle.UpdateRenderData(IViewport, int)` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3713-L3725) | Pixel cull, then `Parts.UpdateRenderData`; prefix target and early call target |
| `KittenEva.UpdateRenderData` | [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L1062-L1078) | Base part render, then avatar |

## Pitfalls and version notes

- Since KSA 5482 instance lists are cleared by `ClearFrameData` rather than by the upload, so a late
  submission is discarded instead of being drawn one frame late. Early submission is the only way the
  equipment renders.[^scope-iron-man]
- `Vehicle.UpdateRenderData` gained an `IsLargeEnoughToRender` pixel cull (KSA 5482). Because the
  early call goes through the real method, Godzilla's and I Feel Seen's patches on it (cull
  redirection, distance override) apply to kitten equipment as well.[^i-feel-seen-patches][^godzilla-visual-scale-patches]
- `Priority.First` matters: the skip decision must happen before another mod's skip-style prefix
  renders the parts itself. The authors' managed test covers both install orders; it does not run
  Vulkan.[^iron-man-flight-tests]
- `AccessTools.DeclaredMethod(typeof(Vehicle), "UpdateRenderData")` relies on there being one
  declared overload. A second overload would make the lookup ambiguous and fail installation.
- Rendering stays enabled for authored equipment after Iron Man mode is switched off, by design: the
  test is "root owns Iron Man nodes", not "kitten is enabled".

## See also

- [Iron Man kitten vehicle editing](./iron-man-kitten-vehicle-editing.md) (editor avatar draw at the bucket clear)
- [Iron Man kitten rocket flight](./iron-man-kitten-rocket-flight.md) (same nonvirtual-call technique for the flight-computer policy)
- [I Feel Seen render distance override](./i-feel-seen-render-distance-override.md)
- [Godzilla visual scaling](./godzilla-visual-scaling.md)
- [Part render filter](./lib-part-render-filter.md)
- [Iron Man sub-mod](../submods/iron-man.md) and [mod overview](../overview.md)
- [Call the base through a DynamicMethod non-virtual call so other mods' patches still run](../../../howto/kittens/eva/treat-an-eva-kitten-as-a-rocket/treat-an-eva-kitten-as-a-rocket-3.md) - how-to article demonstrating this feature
- [Render kitten equipment parts before the instance upload with a nonvirtual Vehicle.UpdateRenderData call](../../../howto/rendering/meshes/hide-parts-or-subparts-from-rendering/hide-parts-or-subparts-from-rendering-6.md) - how-to article demonstrating this feature

[^scope-iron-man]: scope/iron-man.md (authors' hook inventory, build 5482)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-part-model-renderer]: KSA.PartModelRenderer (decompiled, build 2026.10.10.5554)
[^ksa-kitten-eva]: KSA.KittenEva (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^iron-man-flight-tests]: iron-man-flight.tests/Program.cs (render-order and coexistence checks)
[^i-feel-seen-patches]: i-feel-seen.lib/IFeelSeenPatches.cs
[^godzilla-visual-scale-patches]: godzilla.lib/VisualScalePatches.cs
