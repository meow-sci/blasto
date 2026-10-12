---
type: How-To
title: Prefix on ExecuteNextClothSolvers builds private clutter bundles with originals kept for exact restore
description: Replace the models of chosen ground-clutter types on one body by queueing a recipe and committing it in a first-priority prefix at the solver handoff, with renderer hooks so a renderer recreation keeps the override coherent
category: celestials
group: terrain
topic: exclude-or-replace-ground-clutter
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony, reflection]
demonstrated_by:
  - mods/unscience/features/pebbles-ground-clutter-replacement
  - mods/unscience/features/pebbles-private-clutter-materials
techniques: [harmony-prefix, harmony-postfix, reflection]
hooks:
  - harmony-prefix KSA.Universe.ExecuteNextClothSolvers
  - harmony-postfix KSA.GroundClutterRenderer.RebuildFrameResources
  - harmony-prefix KSA.GroundClutterRenderer.Dispose
  - starmap StarMapBeforeGui (via unscience ISubmod.Update)
lifecycle: [frame:dispatch-solvers, frame:draw-ui, event:renderer-rebuild, event:load]
game_types:
  - KSA.GroundClutterRenderer
  - KSA.PlanetRenderer
  - KSA.GroundClutterPlacementData
  - KSA.ClutterEcotypeRenderData
  - KSA.ClutterEcotypePhysicalData
  - KSA.GroundClutterReference
  - KSA.ClutterEcotypeReference
  - KSA.GroundClutterLodReference
  - KSA.Universe
  - KSA.PhysicsBubble
  - KSA.BubbleClutterStatics
tags: [ground-clutter, replacement, ecotype, harmony-prefix, solver-handoff, restore]
---
Outcome: chosen clutter types on one body show imported models (own materials, scale, colliders) while the engine's placement still decides where they appear, and a Restore puts back the exact stock objects. Demonstrated by unscience's Pebbles submod ([Pebbles ground clutter replacement](../../../../mods/unscience/features/pebbles-ground-clutter-replacement.md), [Pebbles private clutter materials](../../../../mods/unscience/features/pebbles-private-clutter-materials.md)). Collider authoring and GLB import belong to the physics and assets topics: [give ground clutter custom colliders](../../../physics/colliders/give-ground-clutter-custom-colliders/overview.md).

## Background: how the game does it

Clutter data is built per body into three arrays held by `GroundClutterRenderer`: `PlanetPlacementData`, `PlanetEcotypeRenderData` and `PlanetPhysicalData`, keyed by the body's hash. Stock materials live in one shared table per renderer, sized at startup, so a mod that needs new materials must give a replaced body its own private buffer and redirect only its own constructions to it (Pebbles does that with a transpiler on the four callers of the material getters). Native ecotype constructors are plain public classes, so a mod can construct a complete private bundle exactly as the game's `PopulatePlanetData` does and swap the three arrays plus the body's template in one main-thread step.

## Lifecycle and timing

The native objects are read by the workers, so the swap must happen when no worker runs and before the next step snapshots clutter:

| Step in `Program.PrepareFrame` | Meaning |
|---|---|
| Solver waits, then `Universe.Apply*Solvers` | All previous workers finished; bubble clutter statics hold the last committed step |
| **`ExecuteNextClothSolvers` prefix (commit)** | Earliest main-thread point after every solver and before both new readers |
| `ChuteClothSystem.SnapshotAndKick` inside it | Parachute cloth snapshots nearby clutter colliders |
| `ExecuteNextVehicleSolvers` then private `SyncGroundClutter()` | Rebuilds bubble statics from the **new** physical data |

`Universe.ExecuteNextClothSolvers` has one caller, `Program.PrepareFrame` ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2114-L2124)); a prefix on `ExecuteNextVehicleSolvers` would be too late for cloth. Everything runs on the main thread: `ConstraintSim.UnlockShapes` (used by the physical constructor and private hulls) throws while the vehicle update is stepping, so the build cannot happen from UI code. `Device.WaitIdle()` is needed because the old objects own buffers that in-flight frames may still read. The UI only queues a detached recipe; the replaced meshes show on the first frame after the commit because new cells are generated during command recording. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Queue, do not mutate.** The UI stores a recipe in a pending dictionary keyed by body id (`null` means restore). Nothing native is touched:

   Capture a stock graph into a serialisable recipe with an ecotype signature, and refuse to queue if a target's signature changed since the UI looked.

2. **Install the hooks** with exact types; the private method is looked up by name, and a transpiler throws unless exactly one call is replaced so an incompatible build aborts the patch set:

```csharp
// pebbles.lib/Runtime/ClutterHooks.cs L30-L47
public static void Apply(Harmony harmony, ClutterController controller)
{
    if (_controller != null && !ReferenceEquals(_controller, controller)) throw new InvalidOperationException("Pebbles already owns a runtime bridge.");
    _controller = controller;
    foreach (var type in new[] { typeof(GroundClutterPlacementData), typeof(ClutterEcotypeRenderData), typeof(ClutterEcotypePhysicalData), typeof(ClutterCubeCellGrid), typeof(ClutterViewResources), typeof(SimpleVkMeshAtlas) })
        foreach (var constructor in type.GetConstructors()) harmony.Patch(constructor, prefix: Method(nameof(BeforeConstruct)));
    // Patch callers rather than tiny getters, which the JIT may inline.
    foreach (var name in new[] { "SortMaterialIds", "CreateColorRenderer", "CreateDepthPrePassRenderer", "CreateShadowDepthRenderer" })
        harmony.Patch(Required(typeof(ClutterEcotypeRenderData), name), transpiler: Method(nameof(MaterialCalls)));
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

3. **Commit in the cloth prefix.** `Process` returns at once when nothing is pending; otherwise it quiesces (wait both solver systems, `Device.WaitIdle()`, touch `SyncWindowBubbles`, whose getter asserts the vehicle task is idle) and applies or restores each queued body:

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

```csharp
// pebbles.lib/Runtime/ClutterController.cs L263-L271
    private static void Quiesce()
    {
        JobSystems.VehicleSolver.Wait(); JobSystems.ClothSolvers.Wait();
        Program.GetRenderer()?.Device.WaitIdle();
        _ = Bubbles(); // The game asserts the vehicle task is idle.
    }
    private static void ClearStatics(Celestial body)
    { foreach (var bubble in Bubbles()) if (ReferenceEquals(bubble.Parent, body)) bubble.GroundClutterStatics.Clear(bubble.ConstraintSim); }
}
```

4. **Build a private bundle** with a fresh `GroundClutterReference` graph, private mesh copies, a private material buffer and index table, and the native `GroundClutterPlacementData`, `ClutterEcotypeRenderData` and `ClutterEcotypePhysicalData` per ecotype. Validate texture bindings and budgets before any GPU allocation; dispose the staging pool so the data is resident before the swap.

5. **Swap and keep the originals.** On the first apply save the original template, the three arrays and the shadow bound; clone the `CelestialTemplate` with `MemberwiseClone` pointing at the private graph; write both `Celestial.<BodyTemplate>k__BackingField` and `Astronomical.bodyTemplate` and the three per-body arrays; only then retire the previous override. Before overwriting check the live objects are still the ones you installed.

6. **Keep the swap coherent across renderer recreation.** A postfix on `GroundClutterRenderer.RebuildFrameResources` rebuilds the retained original render data (the native rebuild only iterates the live data); a prefix on `GroundClutterRenderer.Dispose` restores the originals first so native disposal frees native objects, and marks records suspended until a new renderer exists. A disabled clutter renderer stays non-null after `Dispose`, which `PlanetRenderer._groundClutterRendererCreated` (reflection) tells apart. See [handle renderer rebuilds](../../../rendering/vulkan-passes/handle-renderer-rebuilds/overview.md).

7. **Keep working with the HUD hidden.** The submod's `Update` (scene change detection, requeueing suspended records) is replayed from a hidden-UI hook; see [draw UI while the HUD is hidden](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md).

## Complete example

| File | Role |
|---|---|
| [pebbles.lib/Runtime/ClutterController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterController.cs) | Queue, `Process`, `Apply`/`Restore`, ownership checks, quiesce |
| [pebbles.lib/Runtime/ClutterHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterHooks.cs) | Installs and selectively removes every patch |
| [pebbles.lib/Runtime/ClutterResources.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterResources.cs) | Builds and disposes one body's native bundle |
| [pebbles.lib/Runtime/ClutterGraph.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterGraph.cs) | Private reference graph, budgets, private materials |
| [pebbles.lib/Runtime/ClutterRetirement.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterRetirement.cs) | Best-effort cleanup after a failed native build |

## Variations and gotchas

- **Private state.** The mod reflects `StagingPool._commandBufferIndex`/`_submitted`, `PlanetRenderer._groundClutterRendererCreated`, `Universe._vehicleUpdateTask` and the private `GroundClutterLodReference.BuildMaterialIndirection` ([current/decomp/KSA/GroundClutterLodReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterLodReference.cs#L98-L101)); each can break on an update, so the patch set refuses to apply when one is missing.
- **A failed build must clean up.** Unsubmitted staging commands are discarded and reachable native objects retired once each; errors become persistent faults.
- **Collision rules.** Native collision rejects smooth orientation, so the authoring step falls back from `SurfaceNormalSmooth` to `SurfaceNormal`.
- **Frame cost.** Apply stalls the GPU, but only on frames with pending work.

## See also

- [Overview](overview.md); [approach 1](exclude-or-replace-ground-clutter-1.md); [approach 3](exclude-or-replace-ground-clutter-3.md)
- [Pebbles ground clutter replacement](../../../../mods/unscience/features/pebbles-ground-clutter-replacement.md), [Pebbles private clutter materials](../../../../mods/unscience/features/pebbles-private-clutter-materials.md)
- [Change loaded game data and make the renderer notice](../../../celestials/atmospheres-and-rings/change-loaded-game-data-and-make-the-renderer-notice/overview.md), [Mutate vehicles at a physics-safe point](../../../physics/solver/mutate-vehicles-at-a-physics-safe-point/overview.md)
