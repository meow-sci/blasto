---
type: How-To
title: Call the base through a DynamicMethod non-virtual call so other mods' patches still run
description: Iron Man emits a DynamicMethod that makes a plain call to the Vehicle base handlers, entering Harmony's patched replacement so every other prefix and transpiler still runs, unlike a reverse-patch copy
category: kittens
group: eva
topic: treat-an-eva-kitten-as-a-rocket
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony, il-emit]
demonstrated_by:
  - mods/unscience/features/iron-man-equipment-render-ordering
  - mods/unscience/features/iron-man-kitten-rocket-flight
techniques: [dynamic-method, harmony-prefix]
hooks:
  - dynamic-method KSA.Vehicle.UpdateRenderData
  - dynamic-method KSA.Vehicle.IsFlightComputerDisabled
  - harmony-prefix KSA.PartModelRenderer.UpdateRenderData
  - harmony-prefix KSA.Vehicle.UpdateRenderData
lifecycle: [frame:render, frame:draw-ui, render:gauges]
game_types: [KSA.KittenEva, KSA.Vehicle, KSA.PartModelRenderer, KSA.GaugeButtonFlightComputer, KSA.Program]
tags: [kitten, harmony, dynamic-method, nonvirtual-call, render, coexistence]
---
Sometimes you need to call a base-class method on a derived instance while leaving every other mod's patches on that base method in effect. A C# cast dispatches virtually to the override; a Harmony reverse patch ([approach 2](treat-an-eva-kitten-as-a-rocket-2.md)) runs a copy of the original body and therefore *bypasses* other mods' prefixes. The third option is a `DynamicMethod` whose IL is a plain `call` (not `callvirt`) to the base method: that enters the method's *live, patched* implementation, so every other prefix, postfix and transpiler still runs. Iron Man uses it twice: to submit a kitten's equipment parts at the right moment in the render, and to give an enabled kitten the stock vessel flight-computer button policy. Prerequisites: Harmony and a little IL.

## Background: how the game does it

**Render order.** `UpdateRenderingResources` walks `VehiclesInFrame` per visible viewport and calls `vehicle.UpdateRenderData` for every vehicle that is **not** a `KittenEva`, then `PartModelRenderer.UpdateRenderData` uploads every part model's instance list to the GPU. Later, during command recording, `RenderViewport` and `RenderGame` call the virtual `UpdateRenderData` only for `KittenEva`: it calls `base.UpdateRenderData` (the part tree, via `Parts.UpdateRenderData`) and then `KittenRenderable.UpdateRenderData`, which animates and draws the avatar. `PostRender` clears every part model's instance lists. A kitten's own parts are therefore appended **after** the upload and cleared before the next: for the stock backpack that is harmless (its look comes from the character model), for added equipment it means nothing is drawn ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4349-L4357), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4483-L4496), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4687-L4703)).

**Flight-computer buttons.** `GaugeButtonFlightComputer.IsDisabled` and `PackData` call the virtual `IsFlightComputerDisabled<T>`; `KittenEva` overrides it to disable every non-kitten action ([current/decomp/KSA/GaugeButtonFlightComputer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeButtonFlightComputer.cs#L150-L165), [current/decomp/KSA/KittenEva.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KittenEva.cs#L281-L300)).

A direct call `((Vehicle)kitten).UpdateRenderData(...)` would dispatch into `KittenEva.UpdateRenderData`, which would also animate and draw the avatar a second time (advancing its animation twice).

## Lifecycle and timing

| Step | Location | Lifecycle |
|---|---|---|
| `UpdateRenderingResources` | per visible viewport inside `Program.Render` | `frame:render` |
| `PartModelRenderer.UpdateRenderData` (prefix runs here, then the upload) | called once per viewport | `frame:render` |
| Late `KittenEva.UpdateRenderData` (the `Vehicle` prefix skips the part tree) | `RenderViewport`, `RenderGame` | `frame:render` |
| Button policy, click side (`IsDisabled`) | `GaugeCanvas.OnDrawUi` from `OnDrawUiThreadSafe` | `frame:draw-ui` |
| Button policy, GPU state (`PackData`, refilled every frame) | `CanvasRenderer.Render` -> `GaugeBundle.UpdateInstanceBuffer`, during command recording | `frame:render`, `render:gauges` |

All of it is main thread. The early submission happens once per viewport, so equipment appears once in every view including secondary viewports. When the editor is open both prefixes stand down ([game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence)).

## Steps

1. **Emit the non-virtual call and capture it as a delegate.** `ldarg.0; ldarg.1; ldarg.2; call Vehicle.UpdateRenderData; ret`. The comment records why all three alternatives except this one are wrong:

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

2. **Submit early, just before the part upload.** A prefix on `PartModelRenderer.UpdateRenderData` walks the kittens in frame that have equipment, marks the one being submitted in a `[ThreadStatic]` field (restored afterwards, so a re-entrant render cannot leak it) and calls the delegate:

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

3. **Skip the late duplicate with a `Priority.First` prefix.** In the late pass `KittenEva.UpdateRenderData` still calls `base.UpdateRenderData`; the `VehiclePrefix` above returns `false` for an equipped kitten (unless it is the marked early one or the editor is open), so the part tree is not appended again and the avatar draw that follows is untouched. `Priority.First` puts the skip ahead of other mods' prefixes; the fixture's count of one call per frame for a second skip-style prefix shows it does not run a second time in the late pass.

4. **Use the same trick for the flight-computer button policy.** The base policy is the generic `Vehicle.IsFlightComputerDisabled<Enum>`; a `DynamicMethod` emits a `call` to the closed generic base so the stock vessel policy (target, burn and engine restrictions) runs on the enabled kitten, and a transpiler substitutes the single `callvirt` in each of `IsDisabled` and `PackData` with a static adapter that picks the policy:

```csharp
// iron-man.lib/IronManFlightComputerPatches.cs L22-L38
            // Calling through a Vehicle reference still dispatches to KittenEva's override,
            // which disables every ordinary action. Call the native base nonvirtually.
            // Generic Harmony entry-point patches are not guaranteed to affect this call.
            // Patching the generic override itself would depend on JIT generic-code sharing.
            var method = new DynamicMethod("IronManVesselFlightComputerPolicy", typeof(bool),
                new[] { typeof(Vehicle), typeof(Enum) }, typeof(IronManFlightComputerPatches), true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, BasePolicyMethod());
            il.Emit(OpCodes.Ret);
            _basePolicy = method.CreateDelegate<Func<Vehicle, Enum, bool>>();

            Patch(harmony, typeof(GaugeCanvas), nameof(GaugeCanvas.IsContextVisible), nameof(ReplaceEvaContext));
            Patch(harmony, typeof(GaugeButtonFlightComputer), nameof(GaugeButtonFlightComputer.IsDisabled), nameof(ReplaceButtonPolicy));
            Patch(harmony, typeof(GaugeButtonFlightComputer), nameof(GaugeButtonFlightComputer.PackData), nameof(ReplaceButtonPolicy));
            _applied = true;
```

   (`BasePolicyMethod` is `Vehicle.IsFlightComputerDisabled` made generic over `Enum`; see the transpilers in [approach 1](treat-an-eva-kitten-as-a-rocket-1.md), step 8.)

5. **Prove coexistence with a test.** The managed fixture installs a second skip-style render prefix (modelling I Feel Seen, which renders parts itself and skips the stock body) once before and once after Iron Man's patches and asserts that it runs exactly once per frame, early, in both orders:

```csharp
// iron-man-flight.tests/Program.cs L51-L59
            int renders=a.BaseRenders; int avatar=a.AvatarRenders;
            int compatibilityCalls = _compatibilityCalls;
            PartModelRenderer.Pending=0; PartModelRenderer.Uploaded=0;
            PartModelRenderer.UpdateRenderData(viewport,0);
            Require(a.BaseRenders==renders+1 && PartModelRenderer.Uploaded==1,"parts uploaded early");
            a.UpdateRenderData(viewport,0);
            Require(a.BaseRenders==renders+1 && a.AvatarRenders==avatar+1 && PartModelRenderer.Pending==0,"late avatar without duplicate parts");
            Require(_compatibilityCalls == compatibilityCalls + 1,
                "other render prefix runs once early, regardless of installation order");
```

6. **Check the result.** An equipped kitten shows its tanks and engines in every view; with another mod's render prefix installed on `Vehicle.UpdateRenderData` its logic still runs for the kitten's parts.

## Complete example

- [iron-man.lib/IronManRenderPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManRenderPatches.cs#L9-L92) - the early submission and the late skip.
- [iron-man.lib/IronManFlightComputerPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man.lib/IronManFlightComputerPatches.cs#L12-L125) - the button policy and base-policy `DynamicMethod`.
- [iron-man-flight.tests/Program.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/iron-man-flight.tests/Program.cs#L6-L76) - the coexistence test.
- Feature pages: [Iron Man equipment render ordering](../../../../mods/unscience/features/iron-man-equipment-render-ordering.md), [Iron Man kitten rocket flight](../../../../mods/unscience/features/iron-man-kitten-rocket-flight.md).

## Variations and gotchas

- **`DynamicMethod` with `skipVisibility`.** Pass `true` as the last constructor argument so the IL can call methods the owner type cannot see; the dynamic method is bound to the owner type for visibility purposes only.
- **Generic methods.** Close the generic method over the concrete type (`MakeGenericMethod(typeof(Enum))`) for the `call` operand; patching the generic override itself depends on JIT generic-code sharing, which is why the mod avoids it.
- **Priority.** `Priority.First` on the skip prefix decides who sees the late duplicate call; document the ordering and keep a test like the one above.
- **`[ThreadStatic]` marker.** The early-vehicle marker is restored in a `finally` so a nested render cannot leak it.
- **Editor open.** Both prefixes stand down when `Program.Editor != null`; the editor path draws the parts and the avatar itself ([editor topic](../open-the-vehicle-editor-on-a-live-eva-kitten/overview.md)).
- **Reverse patch is still right when nothing else patches the base** and simplicity matters ([approach 2](treat-an-eva-kitten-as-a-rocket-2.md)).

## See also

- [Fly an EVA kitten as an ordinary rocket vessel](overview.md), [approach 1](treat-an-eva-kitten-as-a-rocket-1.md), [approach 2](treat-an-eva-kitten-as-a-rocket-2.md)
- [Coexist with other mods patching the same method](../../../patching/harmony/coexist-with-other-mods-patching-the-same-method/overview.md), [Write a Harmony transpiler](../../../patching/transpilers/write-a-harmony-transpiler/overview.md)
- [Hide parts or subparts from rendering](../../../rendering/meshes/hide-parts-or-subparts-from-rendering/overview.md)
