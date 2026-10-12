---
type: How-To
title: Replace the sub-pixel cull with prefixes installed lazily on a dedicated Harmony instance
description: Mark vessels by id to be force-rendered, install two cull-free prefixes only while at least one vessel is marked, and fall back to the stock cull on any fault
category: rendering
group: meshes
topic: keep-vessels-rendered-at-any-distance
approach: 2
difficulty: intermediate
prerequisites: [starmap, harmony]
demonstrated_by: [mods/gatos/features/scale-and-force-render-cheats]
techniques: [harmony-prefix]
hooks: [harmony-prefix KSA.Vehicle.GetWorldMatrix, harmony-prefix KSA.Vehicle.UpdateRenderData]
lifecycle: [frame:render, frame:draw-ui]
game_types: [KSA.Vehicle, KSA.PartTree, KSA.Camera, KSA.KittenEva]
tags: [cull, render-distance, lazy-patch, vessel, marked-ids]
---
Outcome: a per-vessel "always render" switch that costs nothing when no vessel is marked. The patches are installed on a dedicated Harmony instance when the first vessel is marked and removed when the last mark goes. The prefixes read one volatile immutable set and do a hash lookup per vehicle per frame while installed.

Demonstrated by gatOS' `vessels/by-id/<id>/always_render` control, ported from I Feel Seen ([Per-vessel scale and force-render cheats](../../../../mods/gatos/features/scale-and-force-render-cheats.md)). Prerequisites: StarMap, Harmony. This is the same patch pair as [approach 1](keep-vessels-rendered-at-any-distance-1.md) with a different lifecycle around it.

## Background: how the game does it

The cull is in `Vehicle.GetWorldMatrix` and `Vehicle.UpdateRenderData` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L3694-L3726)); `UpdateRenderData` delegates the test to the public `IsLargeEnoughToRender(Camera)`. For both bodies see approach 1. `UpdateRenderData` is virtual: the patch binds `Vehicle`'s implementation, so a subclass override such as `KittenEva.UpdateRenderData` is reached through its `base` call.

## Lifecycle and timing

- Marks change in a frame-phase command drain on the main thread, which is also where patching and unpatching happen, so no render code is mid-call while patches change.
- The prefixes run when `Program.UpdateRenderingResources` iterates `VehiclesInFrame` and calls `vehicle.UpdateRenderData(viewport, frameIndex)`; `Program.Render` calls it after `OnPreRender`, just before `RenderGame` records commands: `frame:render`, not `frame:pre-render` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4308-L4360)).
- In this build the only caller of `Vehicle.GetWorldMatrix` is `KittenEva.UpdateRenderData`, so that prefix matters only for EVA kittens.
- Both prefixes read only a volatile published set, so they are thread-agnostic.

## Steps

1. **Register marks in a set keyed by the stable vehicle id**, publish an immutable copy for the prefixes, and install on the first mark / remove on the last:

   ```csharp
// gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs L55-L75
internal static CommandResult Set(Vehicle vehicle, bool on)
{
    if (on)
    {
        if (!Marked.Contains(vehicle.Id))
        {
            if (Marked.Count == 0)
                InstallPatches();
            Marked.Add(vehicle.Id);
            Publish();
        }
    }
    else if (Marked.Remove(vehicle.Id))
    {
        Publish();
        if (Marked.Count == 0)
            RemovePatches();
    }

    return CommandResult.Ok;
}
```

2. **Install with an exact signature on a dedicated Harmony id**, rolling back a half-installed pair:

   ```csharp
// gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs L128-L176
private static void InstallPatches()
{
    if (_harmony is not null)
        return;

    var getWorldMatrix = AccessTools.Method(typeof(Vehicle), nameof(Vehicle.GetWorldMatrix),
        [typeof(KsaCamera)]);
    var updateRenderData = AccessTools.Method(typeof(Vehicle), nameof(Vehicle.UpdateRenderData),
        [typeof(IViewport), typeof(int)]);
    if (getWorldMatrix is null || updateRenderData is null)
        throw new MissingMethodException(
            "Vehicle.GetWorldMatrix/UpdateRenderData not found in this build");

    var harmony = new Harmony("gatos.always_render");
    try
    {
        harmony.Patch(getWorldMatrix, prefix: new HarmonyMethod(
            typeof(VesselForceRender).GetMethod(nameof(GetWorldMatrixPrefix),
                BindingFlags.NonPublic | BindingFlags.Static)!));
        harmony.Patch(updateRenderData, prefix: new HarmonyMethod(
            typeof(VesselForceRender).GetMethod(nameof(UpdateRenderDataPrefix),
                BindingFlags.NonPublic | BindingFlags.Static)!));
    }
    catch
    {
        harmony.UnpatchAll("gatos.always_render"); // don't leak a half-installed pair
        throw;
    }

    _harmony = harmony;
    ModLog.Log.Info("gatOS vessel force-render patches installed.");
}

private static void RemovePatches()
{
    if (_harmony is null)
        return;
    try
    {
        _harmony.UnpatchAll("gatos.always_render");
    }
    catch (Exception ex)
    {
        ModLog.Log.Debug($"gatOS force-render unpatch error: {ex.Message}");
    }

    _harmony = null;
    ModLog.Log.Info("gatOS vessel force-render patches removed.");
}
```

3. **Write the two prefixes.** Each checks the published set; for a marked vessel it reproduces the original body minus the size test and returns `false`; otherwise `true`. Any exception logs once and falls through to the stock cull:

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
// gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs L224-L242
private static bool UpdateRenderDataPrefix(Vehicle __instance, IViewport viewport, int inFrameIndex)
{
    try
    {
        var marked = _published;
        if (marked is null || !marked.Contains(__instance.Id))
            return true;

        var matrixAsmb2Ego = __instance.GetMatrixAsmb2Ego(viewport.GetCamera());
        __instance.Parts.UpdateRenderData(in matrixAsmb2Ego, __instance.IsEditedVehicle,
            viewport, inFrameIndex);
        return false;
    }
    catch (Exception ex)
    {
        LogPrefixFaultOnce(ex);
        return true;
    }
}
```

4. **Prune marks for despawned vessels** and tear down at unload so the patches do not outlive their use:

   ```csharp
// gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs L83-L110
internal static void Prune(IReadOnlyList<VesselSnapshot> live)
{
    if (Marked.Count == 0)
        return;

    var removed = Marked.RemoveWhere(id =>
    {
        foreach (var vessel in live)
            if (vessel.Id == id)
                return false;
        return true;
    });
    if (removed == 0)
        return;

    Publish();
    ModLog.Log.Info($"gatOS force-render: {removed} despawned vessel mark(s) pruned.");
    if (Marked.Count == 0)
        RemovePatches();
}

/// <summary>Unload teardown: drops every mark and removes the patches. Game thread only.</summary>
internal static void Teardown()
{
    Marked.Clear();
    Publish();
    RemovePatches();
}
```

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Render/VesselForceRender.cs) | Registry, lazy install and removal, both prefixes, prune and teardown |

## Variations and gotchas

- **Port every side effect you need.** When reproducing a method body in a prefix, port what matters (gatOS skips only the debug-only physics terrain overlay) and fall through to the original on any fault.
- **Marked ids, not references.** Marks key on the stable vehicle id, so a marked vessel survives a scene rebuild; a despawned vessel's mark is pruned at sampler cadence.
- **Kittens.** The `UpdateRenderData` patch binds `Vehicle`'s implementation, and `KittenEva` draws its avatar through its own `KittenRenderable` path, so the avatar is not force-rendered by that prefix. It is covered by the `GetWorldMatrix` prefix: the override asks for the world matrix and now gets one instead of `null`.
- **A sibling cheat on the same page.** gatOS also scales parts by setting `Part.Scale` recursively over `vehicle.Parts.Parts` and every `SubParts`. The setter only resets cached matrices, so it is visual-only (colliders and mass are untouched); recursing into subparts compounds the factor because `Part.ScaleTotal` composes through `PartParent`. See the feature page for the kitten-avatar branch.

## See also

- [Overview](overview.md)
- [Approach 1](keep-vessels-rendered-at-any-distance-1.md), [approach 3](keep-vessels-rendered-at-any-distance-3.md), [approach 4](keep-vessels-rendered-at-any-distance-4.md)
- [Per-vessel scale and force-render cheats](../../../../mods/gatos/features/scale-and-force-render-cheats.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
