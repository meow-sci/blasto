---
type: How-To
title: Render kitten equipment parts before the instance upload with a nonvirtual Vehicle.UpdateRenderData call
description: Submit an EVA kitten's part tree early from a prefix on PartModelRenderer.UpdateRenderData, calling the patched Vehicle.UpdateRenderData nonvirtually and skipping the late duplicate with a Priority.First prefix
category: rendering
group: meshes
topic: hide-parts-or-subparts-from-rendering
approach: 6
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/iron-man-equipment-render-ordering]
techniques: [harmony-prefix, dynamic-method]
hooks:
  - harmony-prefix KSA.PartModelRenderer.UpdateRenderData
  - harmony-prefix KSA.Vehicle.UpdateRenderData (Priority.First)
lifecycle: [frame:render]
game_types: [KSA.Program, KSA.Vehicle, KSA.KittenEva, KSA.PartModelRenderer, KSA.PartTree, KSA.KittenRenderable]
tags: [kitten, equipment, render-order, nonvirtual-call, dynamic-method, part-tree]
---
Outcome: parts bolted onto an EVA kitten (tanks, engines, RCS) are drawn. Without this, a kitten's part tree is submitted too late in the frame and the equipment is invisible. This article is the "right order" end of the topic: not hiding a part but making sure it is drawn at all, once per view.

Demonstrated by unscience's Iron Man ([Submit EVA kitten equipment before the part-instance upload](../../../../mods/unscience/features/iron-man-equipment-render-ordering.md)). Prerequisites: StarMap, Harmony, comfort with `DynamicMethod` and Harmony priorities.

## Background: how the game does it

Three steps in one frame decide whether a kitten's equipment is drawn:

1. `UpdateRenderingResources` walks `VehiclesInFrame` per visible viewport and calls `vehicle.UpdateRenderData(viewport, frameIndex)` for every vehicle that is **not** a `KittenEva`, then calls `PartModelRenderer.UpdateRenderData`, which uploads every part model's instance list:

   ```csharp
// current/decomp/KSA/Program.cs L4347-L4357, L4371-L4374
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
// ...
			Universe.UpdateRenderData(viewport2, frameIndex);
			Profiler.MainThread.End();
			Profiler.MainThread.Begin(Profiler.Tag.UpdateRenderDataPartModels);
			PartModelRenderer.UpdateRenderData(viewport2, frameIndex);
```

2. Later, during command recording, `RenderViewport` (secondary views) and `RenderGame` (main view) call the virtual `UpdateRenderData` for `KittenEva` vehicles only. `KittenEva.UpdateRenderData` calls `base.UpdateRenderData` (the part tree, via `Parts.UpdateRenderData`) and then animates and draws the avatar:

   ```csharp
// current/decomp/KSA/KittenEva.cs L1062-L1066
public override void UpdateRenderData(IViewport viewport, int inFrameIndex)
{
	base.UpdateRenderData(viewport, inFrameIndex);
	float4x4? worldMatrix = GetWorldMatrix(viewport.GetCamera());
	if (worldMatrix.HasValue)
```

3. `PostRender` calls `ClearFrameData`, which clears every part model's instance lists ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2344), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4438-L4446)).

A kitten's own parts are therefore appended after the upload and cleared before the next one: for the stock backpack that is harmless (its look comes from the character model), for added equipment nothing is drawn.

## Lifecycle and timing

| Step | Location (build 2026.10.10.5554) | Lifecycle |
|---|---|---|
| `Program.Render` then `UpdateRenderingResources` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2468), defined L4308 | `frame:render` |
| Early loop skips `KittenEva` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4347-L4357) | `frame:render` |
| `PartModelRenderer.UpdateRenderData` (your prefix runs, then upload) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4374) | `frame:render` |
| Late `KittenEva.UpdateRenderData` (your other prefix skips the duplicate part tree) | `RenderViewport` L4483-L4492, `RenderGame` L4690-L4699 | `frame:render` |
| `ClearFrameData` clears instance lists | `PostRender` L2344 | `frame:post-render` |

All main-thread. The early submission happens per visible viewport, because `PartModelRenderer.UpdateRenderData` is called once per viewport, so equipment appears once in every view, including secondary viewports. See rows 21 and 22 of [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence) and RenderGame row 1a. When the editor is open (`Program.Editor != null`) both prefixes stand down: the editor branch draws `EditingSpace.Parts` directly.

## Steps

1. **Build a nonvirtual call into the live patched `Vehicle.UpdateRenderData`** with a `DynamicMethod` whose IL is `ldarg.0; ldarg.1; ldarg.2; call; ret`. The choice among three options matters: an ordinary C# call dispatches virtually into `KittenEva.UpdateRenderData` and would draw the avatar twice; a Harmony reverse patch copies the original body and so bypasses other mods' patches on `Vehicle.UpdateRenderData` (for example a distance override); an IL `call` enters Harmony's patched replacement, so every other prefix and transpiler still runs for the early submission:

   ```csharp
// iron-man.lib/IronManRenderPatches.cs L15-L40
internal static void Apply(Harmony harmony)
{
    if (_applied) return;
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
    _applied = true;
}
```

2. **Prefix the batch upload** (`PartModelRenderer.UpdateRenderData`) and submit each equipped kitten's parts early, marking it so the other prefix lets this one call through. The `[ThreadStatic]` marker is saved and restored so a nested or re-entrant render cannot leak it:

   ```csharp
// iron-man.lib/IronManRenderPatches.cs L54-L75
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

3. **Prefix `Vehicle.UpdateRenderData` with `Priority.First`** and return `false` (skip the body) for an equipped `KittenEva` unless it is the early vehicle or the editor is open. The late `KittenEva.UpdateRenderData` still runs its own avatar draw after `base.UpdateRenderData` returns:

   ```csharp
// iron-man.lib/IronManRenderPatches.cs L50-L52
private static bool VehiclePrefix(Vehicle __instance) =>
    !_applied || ReferenceEquals(_earlyVehicle, __instance) || Program.Editor != null || __instance is not KittenEva kitten ||
    !HasEquipmentData(kitten);
```

   `Priority.First` matters: the skip decision must happen before another mod's skip-style prefix renders the parts itself. The authors' managed test installs a second skip-style render prefix before and after Iron Man and asserts that it runs exactly once per frame, early, in both orders ([iron-man-flight.tests/Program.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man-flight.tests/Program.cs#L10-L60)).

4. **Decide per kitten whether the kitten "owns equipment"** with a stable test (here, the backpack root owns Iron Man nodes), not a mode flag, so authored equipment keeps rendering after the mode is switched off or the save is reloaded.

## Complete example

| File | Role |
|---|---|
| [iron-man.lib/IronManRenderPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManRenderPatches.cs) | Both prefixes and the nonvirtual dispatch delegate |
| [iron-man.lib/IronManFlightPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManFlightPatches.cs#L40) | Installs and removes the render patches with the flight patches |
| [iron-man.lib/IronManConnectors.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManConnectors.cs#L18) | `IsOwned`, the "has equipment data" test |
| [iron-man-flight.tests/Program.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man-flight.tests/Program.cs#L10-L60) | Coexistence check with another skip prefix |

## Variations and gotchas

- **Why instance lists are the constraint.** Since the render-data cache the instance lists are cleared by `ClearFrameData` rather than by the upload, so a late submission is discarded instead of being drawn one frame late. Early submission is the only way the equipment renders.
- **Pixel cull applies.** `Vehicle.UpdateRenderData` has an `IsLargeEnoughToRender` pixel cull; because the early call goes through the real method, other mods' patches on it (distance overrides, cull redirection; see [keep vessels rendered at any distance](../keep-vessels-rendered-at-any-distance/overview.md)) apply to kitten equipment too.
- **`AccessTools.DeclaredMethod(typeof(Vehicle), "UpdateRenderData")`** relies on a single declared overload; a second would make the lookup ambiguous and fail installation.
- **Compare with approach 1.** The part render filter's patches still run during this early submission, so a hide predicate also applies to the equipment; the filter cannot hide the kitten avatar itself (a `KittenRenderable`, not part models).
- **Related kitten work.** The kittens category covers avatar visuals; this article is only the part-tree ordering.

## See also

- [Overview](overview.md)
- [Approach 1: hide models by compaction](hide-parts-or-subparts-from-rendering-1.md)
- [Submit EVA kitten equipment before the part-instance upload](../../../../mods/unscience/features/iron-man-equipment-render-ordering.md)
- [Keep a vessel or kitten rendered at any distance](../keep-vessels-rendered-at-any-distance/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
