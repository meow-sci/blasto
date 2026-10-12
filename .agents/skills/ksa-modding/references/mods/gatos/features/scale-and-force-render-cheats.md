---
type: Mod Feature
title: Per-vessel scale and force-render cheats
description: Rescale a whole vessel by writing Part.Scale recursively, keep a vessel drawn at any distance by prefix-replacing KSA's sub-pixel cull, and show IVA interiors from outside by flipping the internal-template flag, each with Harmony patches installed only while in use
mod: gatos
category: rendering
techniques: [harmony-prefix, harmony-postfix, reflection, dynamic-harmony-install]
hooks:
  - harmony-prefix KSA.Vehicle.GetWorldMatrix
  - harmony-prefix KSA.Vehicle.UpdateRenderData
  - harmony-postfix KSA.PartModel..ctor
  - reflection KSA.KittenEva._renderable
lifecycle: [frame:viewports, frame:draw-ui, frame:render]
game_types:
  - KSA.Vehicle
  - KSA.Part
  - KSA.PartTree
  - KSA.PartModel
  - KSA.PartModelModule
  - KSA.PartTreeRenderData
  - KSA.Camera
  - KSA.KittenEva
tags: [rendering, culling, scale, iva, harmony, dynamic-patching, cheats]
---

Three small visual cheats ported from the unscience mod (`garrys-torch` scaling, `i-feel-seen`
and its IVA render toggle). They are useful references because each one shows a different way to
change what KSA draws, and each installs its Harmony patch lazily on first use and removes it on
last use, so the default state costs nothing.

## How it works

**`vessels/by-id/<id>/scale`** ([gatOS.GameMod/Game/Ksa/Actuators/ScaleActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/ScaleActuator.cs#L20-L63)):
walks `vehicle.Parts.Parts` and every `Part.SubParts` recursively setting
`part.Scale = new double3(f, f, f)`. The `Part.Scale` setter only calls
`ResetCachedPosMatrixValues()`, so the change is visual and transform-only; colliders and mass are
untouched. EVA kittens render through a character avatar, so for `KittenEva` it reflects
`_renderable` then `_characterAvatar` then `Core.Scale` and writes `f * 0.01` (0.01 is 1:1),
writing the boxed struct back.

**`vessels/by-id/<id>/always_render`** ([gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs)):
KSA stops drawing a vehicle whose projected diameter is under one pixel: `Vehicle.GetWorldMatrix`
returns null and `Vehicle.UpdateRenderData` skips `Parts.UpdateRenderData` when
`IsLargeEnoughToRender` is false ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3694-L3726)). gatOS installs
two prefixes on a dedicated `Harmony("gatos.always_render")` instance when the first vessel is
marked. Each prefix checks a volatile immutable `HashSet<string>` of marked ids; for a marked
vessel it reproduces the original method body without the size test and returns `false` to skip
the original; for everyone else it returns `true`. Marks are pruned when the vessel despawns, and
the patches are removed when the set empties.

**`/sim/debug/always_render_iva`** ([gatOS.GameMod/Game/Ksa/Render/IvaForceRender.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Render/IvaForceRender.cs)):
interior meshes are skipped unless the viewport is in IVA mode; in 5554
`PartTreeRenderData` tests `!model.Template.Internal || viewport.Mode == CameraMode.IVA`
([current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L1265-L1300)). Enabling the cheat sets
`Template.Internal = false` on every template in `PartModel.Instances`, remembers which ones it
changed, and installs a `Harmony("gatos.iva")` postfix on the protected
`PartModel(PartModelModule.Template)` constructor to catch part models created later. Disabling
restores the remembered templates and unpatches.

## Lifecycle and timing

- Commands run in the Frame-phase drain on the main thread, which is also where patching and
  unpatching happen, so no render code is mid-call while patches change.
- The force-render prefixes run when `Program.UpdateRenderingResources` iterates
  `VehiclesInFrame` and calls `vehicle.UpdateRenderData(viewport, frameIndex)`. `Program.Render`
  calls `UpdateRenderingResources` after `OnPreRender`, just before `RenderGame` records commands,
  so this is `frame:render`, not `frame:pre-render`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4308-L4360)). In 5554 the only caller of `Vehicle.GetWorldMatrix` is
  `KittenEva.UpdateRenderData` (its override, reached the same way), so the `GetWorldMatrix` prefix
  only matters for EVA kittens. Both read only a volatile published set, so they are thread-agnostic
  and cost a hash lookup per call while installed.
- The internal flag is re-read per batch per frame by `PartTreeRenderData`, so the flip is live
  without rebuilding render data.
- Despawn pruning rides the sampler's vehicle enumeration (`VesselForceRender.Prune`).

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Actuators/ScaleActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/ScaleActuator.cs) | recursive `Part.Scale`, kitten avatar scale by reflection |
| [gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs#L55-L75) | mark/unmark with lazy install and removal |
| [gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs#L128-L159) | dynamic Harmony instance, half-install rollback |
| [gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs#L189-L237) | cull-free prefixes |
| [gatOS.GameMod/Game/Ksa/Render/IvaForceRender.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Render/IvaForceRender.cs#L56-L140) | template flip, ctor postfix, restore |
| [i-feel-seen/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/i-feel-seen/Patcher.cs) | the unscience original of the force-render patches |

## Snippets

```csharp
// gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs L189-L207
    private static bool GetWorldMatrixPrefix(Vehicle __instance, KsaCamera camera, ref float4x4? __result)
    {
        try
        {
            var marked = _published;
            if (marked is null || !marked.Contains(__instance.Id))
                return true;

            var positionEgo = camera.GetPositionEgo(__instance);
            __result = float4x4.CreateFromQuaternion(floatQuat.Pack(__instance.Body2Cce))
                       * float4x4.CreateTranslation(float3.Pack(in positionEgo));
            return false;
        }
        catch (Exception ex)
        {
            LogPrefixFaultOnce(ex);
            return true;
        }
    }
```

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/ScaleActuator.cs L48-L63
    private static void Apply(Vehicle vehicle, double factor)
    {
        foreach (var part in vehicle.Parts.Parts)
            SetPartScaleRecursive(part, factor);

        // KittenEva renders via CharacterAvatar.Core.Scale (Core.Scale 0.01 == 1:1).
        if (vehicle.GetType().Name == "KittenEva")
            TryScaleAvatar(vehicle, factor);
    }

    private static void SetPartScaleRecursive(Part part, double factor)
    {
        part.Scale = new double3(factor, factor, factor);
        foreach (var sub in part.SubParts)
            SetPartScaleRecursive(sub, factor);
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Vehicle.GetWorldMatrix(Camera)`, `UpdateRenderData(IViewport, int)`, `IsLargeEnoughToRender` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3694-L3726) | prefix targets and the cull test |
| `Camera.GetPositionEgo(Vehicle)` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs) | reproduce the world matrix |
| `PartTree.UpdateRenderData(in double4x4, bool, IViewport, int)` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs) | push part render data |
| `Part.Scale` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L819-L830) | visual scale, resets cached matrices |
| `PartModel.Instances`, `PartModel(PartModelModule.Template)` | [current/decomp/KSA/PartModel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModel.cs#L409-L435) | template enumeration, ctor postfix |
| `PartModelModule.Template.Internal` | [current/decomp/KSA/PartModelModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelModule.cs#L39-L39) | interior flag |

## Pitfalls and version notes

- Since 5348 `Part.ScaleTotal` composes multiplicatively through `PartParent`, so recursing into
  subparts gives subparts `factor^2` total scale (gatOS notes). The game's own editor scaling became
  physical at rev 5329 (`IRescale`, clamped 0.5x to 2x); gatOS's cheat deliberately is not.
- Rev 5456 (5482) moved part rendering into cached `PartTreeRenderData`; the IVA cheat's
  editor-only `AddInstance` postfix became dead and was removed. Re-verify these internals on
  every update; gatOS rates them High churn.
- `GetType().Name == "KittenEva"` is a brittle string gate; the avatar hop chain is private.
- When reproducing a method body in a prefix, port every side effect you need (gatOS skips only
  the physics-debug terrain overlay) and fall through to the original on any fault.

## See also

- [Welds that pin one vessel to another](welds-vessel-anchoring.md)
- [World-space textured quad (thug_life)](thug-life-world-space-quad.md)
- [IVA cabin physics in an own Bepu world](iva-cabin-physics-bepu.md)
- [Vehicle and EVA paint](vehicle-and-eva-paint.md)
- [gatOS overview](../overview.md)
- [Reproduce the original body for marked objects and return false](../../../howto/patching/harmony/skip-the-original-method-with-a-prefix/skip-the-original-method-with-a-prefix-1.md) - how-to article demonstrating this feature
- [Lazy install and removal of one patch while it is in use](../../../howto/patching/safety/remove-your-patches-safely-at-runtime-and-at-exit/remove-your-patches-safely-at-runtime-and-at-exit-2.md) - how-to article demonstrating this feature
- [Flip Template.Internal from a lazily installed dedicated Harmony instance](../../../howto/rendering/meshes/hide-parts-or-subparts-from-rendering/hide-parts-or-subparts-from-rendering-5.md) - how-to article demonstrating this feature
- [Replace the sub-pixel cull with prefixes installed lazily on a dedicated Harmony instance](../../../howto/rendering/meshes/keep-vessels-rendered-at-any-distance/keep-vessels-rendered-at-any-distance-2.md) - how-to article demonstrating this feature
- [Scale a vessel with a recursive Part.Scale write and an avatar scalar](../../../howto/vehicles-and-parts/vehicles/scale-a-vessel-or-its-parts-at-runtime/scale-a-vessel-or-its-parts-at-runtime-2.md) - how-to article demonstrating this feature

