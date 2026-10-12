---
type: How-To
title: Transactional apply and exact restore of a body's clutter data
description: Swap a body's clutter objects in one main-thread step at the solver handoff, retain the originals for exact restore, and hook GroundClutterRenderer rebuild and dispose so a renderer recreation rebuilds or releases the override
category: celestials
group: atmospheres-and-rings
topic: change-loaded-game-data-and-make-the-renderer-notice
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/pebbles-ground-clutter-replacement]
techniques: [harmony-prefix, harmony-postfix]
hooks:
  - harmony-prefix KSA.Universe.ExecuteNextClothSolvers
  - harmony-postfix KSA.GroundClutterRenderer.RebuildFrameResources
  - harmony-prefix KSA.GroundClutterRenderer.Dispose
lifecycle: [frame:dispatch-solvers, event:renderer-rebuild]
game_types: [KSA.GroundClutterRenderer, KSA.PlanetRenderer, KSA.Universe, KSA.AstronomicalTemplate]
tags: [renderer-rebuild, transaction, restore, ground-clutter, solver-handoff]
---
Outcome: runtime replacement of renderer-owned data that workers also read, with an exact undo and no stale pointers after a renderer recreation. Demonstrated by unscience's Pebbles ([Pebbles ground clutter replacement](../../../../mods/unscience/features/pebbles-ground-clutter-replacement.md)); the clutter specifics are in [exclude or replace ground clutter](../../../celestials/terrain/exclude-or-replace-ground-clutter/overview.md).

## Background: how the game does it

`PlanetRenderer` owns a `GroundClutterRenderer`, re-created or rebuilt by `RebuildGroundClutterResources` depending on `_groundClutterRendererCreated` ([current/decomp/KSA/PlanetRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetRenderer.cs#L1716-L1740)). The clutter renderer's `RebuildFrameResources` iterates only the live per-body arrays, and `Dispose` frees whatever is in them ([current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L1799), [current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L1935)). Physics workers read the same data, so the swap needs a quiet moment.

## Lifecycle and timing

The commit runs in a first-priority prefix on `Universe.ExecuteNextClothSolvers` inside `Program.PrepareFrame` (`frame:dispatch-solvers`): after every solver has finished and the apply calls have run, before the cloth snapshot and the vehicle task read clutter. A queued recipe waits until then. The two renderer hooks run when the game rebuilds the renderer (`event:renderer-rebuild`, from `Program.RebuildRenderer`), which can happen at the top of `PrepareFrame` before the solver waits, so the dispose hook quiesces itself. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Queue only.** The UI stores a detached recipe keyed by body (null means restore); nothing native changes yet.

2. **Install the hooks at startup**, resolved with exact types, priority first:

```csharp
// pebbles.lib/Runtime/ClutterHooks.cs L39-L47
    harmony.Patch(Required(typeof(Universe), nameof(Universe.ExecuteNextClothSolvers)), prefix: Method(nameof(BeforeCloth)));
    harmony.Patch(Required(typeof(GroundClutterRenderer), nameof(GroundClutterRenderer.RebuildFrameResources)), postfix: Method(nameof(RebuildOriginals)));
    harmony.Patch(Required(typeof(GroundClutterRenderer), nameof(GroundClutterRenderer.Dispose)), prefix: Method(nameof(BeforeDispose)));
    var serializeSave = AccessTools.Method(typeof(GroundClutterRenderer), nameof(GroundClutterRenderer.SerializeSave), [typeof(List<ClutterEcotypeSaveData>)])
        ?? throw new MissingMethodException(typeof(GroundClutterRenderer).FullName, nameof(GroundClutterRenderer.SerializeSave));
    harmony.Patch(serializeSave, postfix: Method(nameof(AfterSerializeSave)));
    harmony.Patch(Required(typeof(ClutterEcotypeRenderData), nameof(ClutterEcotypeRenderData.RebuildFrameResources)), prefix: Method(nameof(BeforeRebuild)), finalizer: Method(nameof(AfterRebuild)));
    harmony.Patch(Required(typeof(ShaderReference), nameof(ShaderReference.CompileVariantWithCustomOptions)), prefix: Method(nameof(CompileColor)));
}
```

3. **Commit in the prefix**, guarded so an exception becomes a status line rather than breaking the frame:

```csharp
// pebbles.lib/Runtime/ClutterHooks.cs L83-L86
private static void BeforeCloth()
{ try { _controller?.Process(); } catch (Exception ex) { _controller?.Report(ex); } }
private static void RebuildOriginals(GroundClutterRenderer __instance, DeviceEx __0) => _controller?.RebuildOriginals(__instance, __0);
private static void BeforeDispose(GroundClutterRenderer __instance) => _controller?.RendererDisposing(__instance);
```

```csharp
// pebbles.lib/Runtime/ClutterController.cs L89-L110
internal void Process()
{
    if (_processing || _pending.Count == 0) return;
    _processing = true;
    try
    {
        Quiesce();
        foreach (var request in _pending.ToArray())
        {
            if (request.Value != null && ActiveRenderer() == null) continue;
            _pending.Remove(request.Key);
            try { if (request.Value == null) Restore(request.Key); else Apply(request.Key, request.Value); }
            catch (Exception ex)
            {
                Status = $"{request.Key}: {ex.GetBaseException().Message}";
                Console.WriteLine($"pebbles: {ex}");
                if (_live.TryGetValue(request.Key, out var old)) old.Status = "Previous override retained. " + Status;
            }
        }
    }
    finally { _processing = false; }
}
```

4. **Swap and retain.** Save the original template, arrays and bounds on first apply; install private objects built exactly as the game builds them; write the new template; verify ownership before every later overwrite, so you never clobber state someone else installed.

5. **Keep the override coherent across recreation.** After the game's `RebuildFrameResources`, rebuild the retained original render data; before `Dispose`, put the originals back so native disposal frees native objects, and mark records suspended until a new renderer exists:

```csharp
// pebbles.lib/Runtime/ClutterHooks.cs L85-L86
private static void RebuildOriginals(GroundClutterRenderer __instance, DeviceEx __0) => _controller?.RebuildOriginals(__instance, __0);
private static void BeforeDispose(GroundClutterRenderer __instance) => _controller?.RendererDisposing(__instance);
```

6. **Restore on scene change.** When `Universe.CurrentSystem` is replaced the objects are gone: restore synchronously and drop the records.

## Complete example

| File | Role |
|---|---|
| [pebbles.lib/Runtime/ClutterController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterController.cs) | Queue, process, apply, restore, ownership checks |
| [pebbles.lib/Runtime/ClutterHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterHooks.cs) | The patch set |

## Variations and gotchas

- **Never build native objects from UI code once the workers have been kicked:** the shape registry refuses mutation during a vehicle update.
- **A disabled renderer is not null.** `PlanetRenderer._groundClutterRendererCreated` tells a live renderer from a disposed one.
- **Keep working with the HUD hidden:** replay the submod update from a hook that still runs without the UI.

## See also

- [Overview](overview.md); [approach 1](change-loaded-game-data-and-make-the-renderer-notice-1.md); [approach 3](change-loaded-game-data-and-make-the-renderer-notice-3.md)
- [Pebbles ground clutter replacement](../../../../mods/unscience/features/pebbles-ground-clutter-replacement.md)
- [Exclude or replace ground clutter](../../../celestials/terrain/exclude-or-replace-ground-clutter/overview.md), [Handle renderer rebuilds](../../../rendering/vulkan-passes/handle-renderer-rebuilds/overview.md)
