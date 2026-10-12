---
type: How-To
title: Flip Template.Internal from a lazily installed dedicated Harmony instance
description: Make cabin interiors render outside IVA with a cheat that installs its constructor postfix only while on, tracks the templates it changed, and unpatches itself when switched off
category: rendering
group: meshes
topic: hide-parts-or-subparts-from-rendering
approach: 5
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/gatos/features/scale-and-force-render-cheats]
techniques: [harmony-postfix]
hooks: [harmony-postfix KSA.PartModel..ctor]
lifecycle: [frame:render, frame:draw-ui]
game_types: [KSA.PartModel, KSA.PartModelModule, KSA.PartTreeRenderData, KSA.Program]
tags: [iva, interior, internal-flag, lazy-patch, cheat]
---
Outcome: a switch that makes every interior (IVA) mesh render from the outside camera and in the editor, with zero Harmony patches while the switch is off. Turning it on flips `Template.Internal` on all loaded models and installs one constructor postfix to catch models built later; turning it off restores exactly what it changed and unpatches.

Demonstrated by gatOS' `/sim/debug/always_render_iva` cheat, ported from unscience ([Per-vessel scale and force-render cheats](../../../../mods/gatos/features/scale-and-force-render-cheats.md)). Prerequisites: StarMap (or any main-thread command drain), Harmony. This is the lightweight sibling of [approach 4](hide-parts-or-subparts-from-rendering-4.md): choose it when one owner and a global on/off switch are enough.

## Background: how the game does it

The same gate as approach 4: `PartTreeRenderData.Compose` appends an instance only when `!model.Template.Internal || viewport is IVA` ([current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L1300-L1303)), re-read per batch per frame. The raytraced-IVA branch routes through `PartModel.AddInstance`, whose gate is identical ([current/decomp/KSA/PartModel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModel.cs#L486)). Editor trees render through the same `PartTree.UpdateRenderData` then `Compose` path, so the template flip alone covers vehicle editor previews too. The protected `PartModel(PartModelModule.Template)` constructor ([current/decomp/KSA/PartModel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModel.cs#L435)) adds each model to `PartModel.Instances` ([current/decomp/KSA/PartModel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModel.cs#L409)).

## Lifecycle and timing

- gatOS' commands run in a frame-phase drain on the main thread, which is also where patching and unpatching happen, so no render code is mid-call while patches change (`frame:draw-ui` equivalent: any main-thread point before `Render`).
- The internal flag is re-read per batch per frame by `PartTreeRenderData` in `Program.UpdateRenderingResources` (`frame:render`), so the flip is live without rebuilding render data.
- Models first constructed while the cheat is on (new vessels, new editor parts) go through the constructor postfix, also on the main thread.

See row 22 of [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence).

## Steps

1. **Toggle in two directions.** On: install patches, then flip. Off: restore, then remove patches:

   ```csharp
// gatOS.GameMod/Game/Ksa/Render/IvaForceRender.cs L56-L71
public static void SetEnabled(bool value)
{
    if (_enabled == value)
        return;
    _enabled = value;
    if (value)
    {
        InstallPatches();
        ForceInternalVisible();
    }
    else
    {
        RestoreInternalHidden();
        RemovePatches();
    }
}
```

2. **Install a dedicated Harmony instance with a fixed id** only when enabling, so the default state carries no patches:

   ```csharp
// gatOS.GameMod/Game/Ksa/Render/IvaForceRender.cs L73-L102
private static void InstallPatches()
{
    if (_harmony is not null)
        return;
    _harmony = new Harmony("gatos.iva");

    _ctorOriginal = AccessTools.Constructor(typeof(PartModel), [typeof(PartModelModule.Template)]);
    _ctorPostfix = typeof(IvaForceRender).GetMethod(nameof(CtorPostfix),
        BindingFlags.NonPublic | BindingFlags.Static)!;
    _harmony.Patch(_ctorOriginal, postfix: new HarmonyMethod(_ctorPostfix));

    ModLog.Log.Info("gatOS IVA force-render patches installed.");
}

private static void RemovePatches()
{
    try
    {
        _harmony?.UnpatchAll("gatos.iva");
    }
    catch (Exception ex)
    {
        ModLog.Log.Debug($"gatOS IVA unpatch error: {ex.Message}");
    }

    _harmony = null;
    _ctorOriginal = null;
    _ctorPostfix = null;
    ModLog.Log.Info("gatOS IVA force-render patches removed.");
}
```

3. **Catch part types first seen after enabling** with the constructor postfix, tracking what you changed:

   ```csharp
// gatOS.GameMod/Game/Ksa/Render/IvaForceRender.cs L104-L119
/// <summary>Catch part types built after the toggle was enabled (templates not present at enable time).</summary>
private static void CtorPostfix(PartModel __instance)
{
    try
    {
        if (!_enabled || !__instance.Template.Internal)
            return;
        __instance.Template.Internal = false;
        if (!Mutated.Contains(__instance.Template))
            Mutated.Add(__instance.Template);
    }
    catch (Exception ex)
    {
        ModLog.Log.Debug($"gatOS IVA ctor postfix error: {ex.Message}");
    }
}
```

4. **Flip everything loaded now, and remember it**, then restore exactly that list when switching off:

   ```csharp
// gatOS.GameMod/Game/Ksa/Render/IvaForceRender.cs L121-L140
private static void ForceInternalVisible()
{
    Mutated.Clear();
    foreach (var pm in PartModel.Instances)
        if (pm.Template.Internal)
        {
            Mutated.Add(pm.Template);
            pm.Template.Internal = false;
        }

    ModLog.Log.Info($"gatOS IVA force-render: {Mutated.Count} internal templates made visible.");
}

private static void RestoreInternalHidden()
{
    foreach (var t in Mutated)
        t.Internal = true;
    ModLog.Log.Info($"gatOS IVA force-render: {Mutated.Count} internal templates restored.");
    Mutated.Clear();
}
```

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Render/IvaForceRender.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Render/IvaForceRender.cs) | Whole feature: toggle, lazy patch, flip, restore |

## Variations and gotchas

- **Differences from approach 4.** No owner counting (a single boolean), no editor-only reveal, no shadow-proxy exclusion: `ForceInternalVisible` flips every template whose `Internal` is true, including shadow proxies. Add the `RayTracing != ShadowProxy` test from approach 4 if you use ray-traced IVA.
- **Churn.** The authors rate these internals high-churn: the render-data cache (game revision 5456) made the former editor-only `AddInstance` re-add postfix dead and it was removed. Re-verify on every game update.
- **Global by design.** The shared template flag is global; the tracked-template restore and the unpatch fully revert it.
- **Thread rule.** Only patch and unpatch, and flip templates, from the main thread.

## See also

- [Overview](overview.md)
- [Approach 4: owner-counted requirement](hide-parts-or-subparts-from-rendering-4.md), [approach 1](hide-parts-or-subparts-from-rendering-1.md)
- [Per-vessel scale and force-render cheats](../../../../mods/gatos/features/scale-and-force-render-cheats.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
