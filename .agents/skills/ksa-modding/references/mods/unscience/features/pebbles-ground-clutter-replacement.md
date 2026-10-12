---
type: Mod Feature
title: Per-celestial ground clutter replacement with transactional apply and restore
description: Pebbles swaps the meshes, materials, scale and colliders of chosen clutter types on one celestial by building private native clutter bundles in a Universe.ExecuteNextClothSolvers prefix, keeping the originals for exact restore
mod: unscience
submod: pebbles
category: celestials
techniques: [harmony-prefix, harmony-postfix, reflection, bepu-physics, vulkan-buffer, glb-model]
hooks:
  - harmony-prefix KSA.Universe.ExecuteNextClothSolvers
  - harmony-postfix KSA.GroundClutterRenderer.RebuildFrameResources
  - harmony-prefix KSA.GroundClutterRenderer.Dispose
  - starmap StarMapBeforeGui (via unscience ISubmod.Update; scene changes, requeue, discovery)
  - harmony-prefix KSA.Program.OnDrawUiConsole (HiddenUiFrameHook replays ISubmod.Update while the HUD is hidden)
  - reflection KSA.Celestial.<BodyTemplate>k__BackingField and KSA.Astronomical.bodyTemplate
  - reflection KSA.GroundClutterRenderer._renderPassInfo and _planetClutterMaxBoundingRadius
  - reflection KSA.PlanetRenderer._groundClutterRendererCreated
  - reflection KSA.Universe._vehicleUpdateTask
  - reflection KSA.GroundClutterLodReference.BuildMaterialIndirection (private)
  - reflection Brutal.VulkanApi.Abstractions.StagingPool._submitted and _commandBufferIndex
lifecycle: [frame:dispatch-solvers, frame:draw-ui, event:renderer-rebuild, event:load]
game_types:
  - KSA.Universe
  - KSA.Program
  - KSA.GroundClutterRenderer
  - KSA.PlanetRenderer
  - KSA.GroundClutterPlacementData
  - KSA.ClutterEcotypeRenderData
  - KSA.ClutterEcotypePhysicalData
  - KSA.GroundClutterReference
  - KSA.ClutterEcotypeReference
  - KSA.ClutterObjectTemplate
  - KSA.GroundClutterLodReference
  - KSA.Celestial
  - KSA.Astronomical
  - KSA.CelestialTemplate
  - KSA.PhysicsBubble
  - KSA.BubbleClutterStatics
  - KSA.VehicleUpdateTask
  - KSA.JobSystems
  - KSA.ChuteClothSystem
  - KSA.ChuteEnvironmentSnapshot
  - Brutal.VulkanApi.Abstractions.StagingPool
tags: [ground-clutter, celestial, terrain, transaction, physics, vulkan, reflection, solver-timing]
---

Pebbles lets a player pick a mesh (a stock registry mesh or an imported GLB), set a scale and
optional colliders, choose a planet and one or more of its native clutter types ("ecotypes"), and
press **Apply to planet**. Every variant and all five LODs of the checked types then show the new
mesh, while the game's own placement (distribution textures, biomes, spacing, slope and altitude
masks) still decides where instances appear. **Restore** puts back the exact stock objects, per
type or for the whole planet.[^scope-ground-clutter]

Mechanism in one line: the UI only queues a detached recipe; a Harmony prefix on
`Universe.ExecuteNextClothSolvers` waits for every solver and the GPU, builds a complete private
`GroundClutterReference` graph and fresh native placement/render/physical objects for that one
body, then swaps the three per-body arrays of `GroundClutterRenderer` and the body's template in a
single main-thread step, keeping the originals for restore.[^clutter-controller]

## How it works

1. **Capture and author (UI thread, detached).** `ClutterCapture.Capture` turns a body's stock
   graph into a serialisable `PebblesRecipe` (ecotype placement, every object slot, five LODs, mesh
   and material ids, colliders) and a SHA-256 `Signature` per ecotype built from object ids, LOD mesh
   ids/primitive counts and material ids
   ([pebbles.lib/Runtime/ClutterCapture.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterCapture.cs#L12-L67)).
   `ClutterAuthoring.Replace` copies the authored object into every slot of the checked ecotypes,
   keeping each slot's `SourceId` and native LOD distances, forcing `MinScale = MaxScale = 1`, and
   switching collision to `PrimitiveList` (or `None`) with a `SurfaceNormalSmooth` to `SurfaceNormal`
   fallback because native collision rejects smooth orientation
   ([pebbles.lib/Models/ClutterAuthoring.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/ClutterAuthoring.cs#L38-L69)).
   The Apply button re-captures and refuses to queue if any target's signature changed
   ([pebbles.lib/PebblesSubmod.Ui.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/PebblesSubmod.Ui.cs#L81-L90)).
2. **Queue.** `ClutterController.QueueApply` validates and stores a clone in `_pending[bodyId]`;
   `QueueRestore` stores `null`. Nothing native is touched yet.
3. **Commit at the solver handoff.** `ClutterHooks.BeforeCloth` (prefix, `Priority.First`) calls
   `ClutterController.Process`, which returns immediately when nothing is pending. Otherwise it runs
   `Quiesce()` (wait `JobSystems.VehicleSolver` and `ClothSolvers`, `Device.WaitIdle()`, and read
   `VehicleUpdateTask.SyncWindowBubbles`, whose getter throws if the vehicle task is still running)
   and then applies or restores each queued body.[^clutter-hooks]
4. **Build a private bundle.** `ClutterResources.Build` first lets `ClutterGraph.Build` create a new
   `GroundClutterReference` whose ecotypes, object templates, LOD references and material references
   are all fresh objects; stock meshes and textures are only borrowed, while even "keep original"
   colliders are rebuilt as private collider templates (`ClutterColliders.Build` with `stockHull: true`,
   [pebbles.lib/Runtime/ClutterGraph.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterGraph.cs#L73);
   see [authored clutter colliders](./pebbles-clutter-colliders.md)). Mesh
   geometry is copied into private CPU `MeshAsset`s with the transform baked in, LOD material
   indirection is built by invoking the private `GroundClutterLodReference.BuildMaterialIndirection`,
   and `ToParameters()` is called up front to validate texture bindings before any GPU allocation.
   The candidate-slot and repeated-vertex budgets are checked against the native grid formula
   ([pebbles.lib/Runtime/ClutterGraph.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterGraph.cs#L21-L86)).
   `Build` then constructs `GroundClutterPlacementData`, `ClutterEcotypeRenderData` and
   `ClutterEcotypePhysicalData` per ecotype exactly as the game's `PopulatePlanetData` does, but with
   one shared `StagingPool` command buffer and a private material buffer, and passes the physical
   constructor the larger of the visual radius and the collider reach
   ([pebbles.lib/Runtime/ClutterResources.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterResources.cs#L31-L96)).
   Disposing the pool submits and waits, so the GPU data is resident before the swap.
5. **Swap.** `Apply` drains pending clutter hits for that body, remembers and replays removed and
   displaced clutter (see [clutter state persistence](./pebbles-clutter-state-persistence.md)),
   clears that body's bubble clutter statics, saves the original template, three arrays and shadow
   bound on first apply, shallow-clones the `CelestialTemplate` with `MemberwiseClone` and points its
   `GroundClutterReference` at the private graph, writes both `Celestial.<BodyTemplate>k__BackingField`
   and `Astronomical.bodyTemplate`, replaces `PlanetPlacementData`/`PlanetEcotypeRenderData`/
   `PlanetPhysicalData[body.Hash]` and `_planetClutterMaxBoundingRadius[body.Hash]`, and only then
   retires the previous Pebbles bundle.
6. **Restore** reverses the swap with the retained originals and calls
   `InvalidateGeneratedClutter()` on each original physical object so collision cells regenerate.
7. **Ownership checks.** Before overwriting, `CheckOwnership` requires that the body template and
   all three arrays are still the objects Pebbles installed; otherwise it throws rather than clobber
   newer game state.

Failure handling: if `Build` throws, the outer pool's unsubmitted commands are discarded by
resetting the private `StagingPool._commandBufferIndex` to `_submitted` (otherwise
`StagingPool.Dispose` would submit half-recorded work), and `ClutterRetirement` walks the objects a
constructor prefix captured during the build (six native types) to free reachable buffers, mappings,
descriptor pools, pipelines, textures and Bepu shapes once each
([pebbles.lib/Runtime/ClutterRetirement.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterRetirement.cs#L17-L71)).
Retirement errors become persistent `Faults` and block GLB cache release.[^clutter-retirement][^ksa-staging-pool]

Renderer lifecycle hooks keep the swap coherent:

| Hook | Why |
|---|---|
| Postfix `GroundClutterRenderer.RebuildFrameResources(DeviceEx)` | The native rebuild only iterates the live `PlanetEcotypeRenderData` (the Pebbles override), so `RebuildOriginals` also rebuilds the retained original render data, keeping restore valid after a graphics change |
| Prefix `GroundClutterRenderer.Dispose()` | `RendererDisposing` quiesces and restores the original arrays first so native disposal frees native objects, then marks records "suspended"; `Update` requeues them once a new renderer exists |
| `PlanetRenderer._groundClutterRendererCreated` (reflection) | A disabled clutter renderer stays non-null after `Dispose`; this flag tells a live renderer from a dead one |

## Lifecycle and timing

`ExecuteNextClothSolvers` has exactly one caller in build 5554, `Program.PrepareFrame` at
`current/decomp/KSA/Program.cs` L2217.[^ksa-program] The prefix therefore runs:

| Step in `PrepareFrame` | Line | Meaning for Pebbles |
|---|---|---|
| `JobSystems.*.Wait()` | L2151-L2161 | All workers from the previous step have finished |
| `Universe.Apply*Solvers` | L2164-L2170 | Bubble clutter statics and displaced bodies hold the last committed step |
| `Universe.GetJobSimStep` (swapped by `PhysicsFrameHook`) | L2216 | Other unscience mutations have run |
| **`ExecuteNextClothSolvers` prefix (Pebbles commit)** | L2217 | Nothing has snapshotted clutter for the next step yet |
| `ChuteClothSystem.SnapshotAndKick` (inside `ExecuteNextClothSolvers`) | Universe.cs L2114-L2124 | Parachute cloth snapshots up to eight nearby clutter collider instances (`ChuteEnvironmentSnapshot.AppendClutterStatics`) |
| `ExecuteNextVehicleSolvers` -> private `SyncGroundClutter()` | Universe.cs L2126-L2139, L2157-L2201 | Game drains bubble exclusions and calls `PhysicsBubble.SyncGroundClutterStatics`, which reinstalls statics from the **new** `PlanetPhysicalData[body]` |

So the cloth prefix is the earliest main-thread point that is after every solver has completed and
before both new readers: the cloth snapshot and the vehicle task.[^ksa-universe][^ksa-chute-environment-snapshot]
A prefix on `ExecuteNextVehicleSolvers` would be too late for cloth.[^pebbles-source-map] Pebbles
performs its own drain (`DrainExclusions`, a copy of `SyncGroundClutter` limited to bubbles whose
`Parent` is the body) before swapping, because pending hits refer to the old arrays' indices; the
game's `SyncGroundClutter` two lines later then finds nothing stale and rebuilds the cleared statics
from the new physical data in the same frame.[^clutter-controller-grids][^ksa-physics-bubble]

Thread and state notes:

- Everything runs on the main thread. `ConstraintSim.UnlockShapes` (used by the physical
  constructor and private hulls) throws while the vehicle update is stepping, which is why the build
  cannot happen from UI code once `ExecuteNextVehicleSolvers` has kicked the workers.
- `Device.WaitIdle()` is needed because the old render/physical objects own buffers and descriptors
  that in-flight frames may still read; it stalls the GPU, but only on frames with pending work.
- New clutter cells are generated by the renderer during command recording
  (`PlanetRenderer.OnFrameGroundClutter`, `RenderGame` L4735), later in the same frame, so the
  replaced meshes show up on the first rendered frame after the commit.[^ksa-planet-renderer]
- `ISubmod.Update` (StarMapBeforeGui, or the hidden-HUD replay) handles scene changes
  (`Universe.CurrentSystem` replaced: synchronous restore of everything), requeues suspended
  records, refreshes body ids every five seconds and the shared GLB catalog every two seconds.
  That keeps working with the HUD hidden.[^pebbles-submod]
- `PlanetRenderer.RebuildFrameResources` is reached from `Program.RebuildRenderer`, which can run
  at the very top of `PrepareFrame` (L2146) before the solver waits, so `RendererDisposing` calls
  `Quiesce()` itself.
- Save/load does not use the queue: the `pebbles` save participant restores synchronously before
  native deserialisation and reapplies recipes at the host's restore boundary through
  `ApplyForSaveLoad` (quiesce, then `Apply`).

The hooks are installed once at startup by the consolidated patcher (`Patcher.PebblesController`
is set from `Mod.OnFullyLoaded`) and stay in place while idle; `ClutterHooks.Remove` unpatches only
methods declared by `ClutterHooks` on the shared Harmony owner.[^unscience-patcher][^unscience-mod]

## Key code

| Path | Role |
|---|---|
| [pebbles.lib/Runtime/ClutterController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterController.cs) | Queue, `Process`, `Apply`/`Restore`, ownership checks, quiesce, renderer suspend/rebuild |
| [pebbles.lib/Runtime/ClutterHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterHooks.cs#L30-L61) | Installs and selectively removes every Pebbles patch |
| [pebbles.lib/Runtime/ClutterResources.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterResources.cs) | Builds and disposes one body's native bundle |
| [pebbles.lib/Runtime/ClutterGraph.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterGraph.cs) | Private reference graph, budgets, private materials |
| [pebbles.lib/Runtime/ClutterCapture.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterCapture.cs) | Stock graph to recipe, ecotype signatures |
| [pebbles.lib/Runtime/ClutterLiveRecord.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterLiveRecord.cs) | Per-body originals, owned template, resources, grid memory |
| [pebbles.lib/Runtime/ClutterRetirement.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterRetirement.cs) | Best-effort cleanup after a failed native build |
| [pebbles.lib/Models/ClutterAuthoring.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/ClutterAuthoring.cs) | Replace selected ecotypes in a detached recipe |
| [pebbles.lib/PebblesSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/PebblesSubmod.cs#L20-L36) | Per-frame `Update` (scene change, refresh, deferred import release) |
| [unscience/Patcher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Patcher.cs#L100) | `TryApply("pebbles", ...)` on the shared Harmony instance |

## Snippets

Hook installation and the cloth prefix:

```csharp
// pebbles.lib/Runtime/ClutterHooks.cs L39-L86
        harmony.Patch(Required(typeof(Universe), nameof(Universe.ExecuteNextClothSolvers)), prefix: Method(nameof(BeforeCloth)));
        harmony.Patch(Required(typeof(GroundClutterRenderer), nameof(GroundClutterRenderer.RebuildFrameResources)), postfix: Method(nameof(RebuildOriginals)));
        harmony.Patch(Required(typeof(GroundClutterRenderer), nameof(GroundClutterRenderer.Dispose)), prefix: Method(nameof(BeforeDispose)));
// ...
    private static void BeforeCloth()
    { try { _controller?.Process(); } catch (Exception ex) { _controller?.Report(ex); } }
    private static void RebuildOriginals(GroundClutterRenderer __instance, DeviceEx __0) => _controller?.RebuildOriginals(__instance, __0);
    private static void BeforeDispose(GroundClutterRenderer __instance) => _controller?.RendererDisposing(__instance);
```

The commit: snapshot old state, replay into the new bundle, then swap template and arrays:

```csharp
// pebbles.lib/Runtime/ClutterController.cs L131-L155
        var clone = (CelestialTemplate)AccessTools.Method(typeof(object), "MemberwiseClone").Invoke(originalTemplate, null)!;
        clone.GroundClutterReference = next.Graph.Reference;
        try
        {
            DrainExclusions(body, owner);
            var before = owner.PlanetPlacementData[body.Hash];
            var sourceGraph = body.BodyTemplate.GroundClutterReference!;
            // A live override's placement was replayed from this memory, so its displaced records are authoritative.
            RememberGrids(record, sourceGraph, before, authoritative: record.Owner != null);
            ReplayGrids(record, next.Graph.Reference, next.Placement, next.Render, next.Physical);
            ClearStatics(body);
            if (record.Owner == null)
            {
                record.OriginalTemplate = originalTemplate;
                record.OriginalPlacement = before; record.OriginalRender = owner.PlanetEcotypeRenderData[body.Hash]; record.OriginalPhysical = owner.PlanetPhysicalData[body.Hash];
                record.OriginalRadius = Bounds(owner).GetValueOrDefault(body.Hash);
            }
            SetTemplate(body, clone);
            owner.PlanetPlacementData[body.Hash] = next.Placement; owner.PlanetEcotypeRenderData[body.Hash] = next.Render; owner.PlanetPhysicalData[body.Hash] = next.Physical;
            Bounds(owner)[body.Hash] = next.MaximumRadius;
            var outgoing = record.Resources;
            record.Owner = owner; record.Resources = next; record.OwnedTemplate = clone; record.Recipe = RecipeCopy.Clone(recipe); record.Status = "Applied; cells generate as the game updates";
            _live[id] = record; ClutterHooks.Track(next);
            if (outgoing != null) { ClutterHooks.Forget(outgoing); Retire(outgoing, "Replaced resources"); }
            Status = $"Applied to {id}";
```

Template rebinding, the shadow-bound dictionary, and the quiesce helpers:

```csharp
// pebbles.lib/Runtime/ClutterController.cs L256-L270
    private static void SetTemplate(Celestial body, CelestialTemplate template)
    {
        Field(typeof(Celestial), "<BodyTemplate>k__BackingField").SetValue(body, template);
        Field(typeof(Astronomical), "bodyTemplate").SetValue(body, template);
    }
    private static Dictionary<KeyHash, float> Bounds(GroundClutterRenderer renderer) => (Dictionary<KeyHash, float>)Field(typeof(GroundClutterRenderer), "_planetClutterMaxBoundingRadius").GetValue(renderer)!;
    private static PhysicsBubble[] Bubbles() => ((VehicleUpdateTask)Field(typeof(Universe), "_vehicleUpdateTask").GetValue(null)!).SyncWindowBubbles.ToArray();
    private static void Quiesce()
    {
        JobSystems.VehicleSolver.Wait(); JobSystems.ClothSolvers.Wait();
        Program.GetRenderer()?.Device.WaitIdle();
        _ = Bubbles(); // The game asserts the vehicle task is idle.
    }
    private static void ClearStatics(Celestial body)
    { foreach (var bubble in Bubbles()) if (ReferenceEquals(bubble.Parent, body)) bubble.GroundClutterStatics.Clear(bubble.ConstraintSim); }
```

What runs around the prefix in the game (build 5554):

```csharp
// current/decomp/KSA/Universe.cs L2114-L2133
	public static void ExecuteNextClothSolvers(double dtPlayer, SimStep simStep)
	{
		if (CurrentSystem == null)
		{
			ChuteBepuClothState.ClearTerrainTableCache();
		}
		else
		{
			ChuteClothSystem.SnapshotAndKick(dtPlayer, simStep);
		}
	}

	public static void ExecuteNextVehicleSolvers(double dtPlayer, SimStep simStep)
	{
		if (CurrentSystem != null)
		{
			PrepareVehicleWorkers(simStep);
			SyncGroundClutter();
			_vehicleUpdateTask.PrepareFrame(CollectionsMarshal.AsSpan(_orphanVehicles), simStep);
			JobSystems.VehicleSolver.Queue(_vehicleUpdateTask, 1);
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Universe.ExecuteNextClothSolvers(double, SimStep)` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2114) | Prefix target; commit point |
| `Universe.SyncGroundClutter()` (private) | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2157-L2201) | Native exclusion drain that `DrainExclusions` mirrors; reinstalls statics after the swap |
| `Universe._vehicleUpdateTask`, `VehicleUpdateTask.SyncWindowBubbles` | [current/decomp/KSA/VehicleUpdateTask.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateTask.cs#L158-L168) | Bubble list; getter throws while the task runs |
| `GroundClutterRenderer.PlanetPlacementData` / `PlanetEcotypeRenderData` / `PlanetPhysicalData` | [current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L292-L308) | The three per-body arrays swapped |
| `GroundClutterRenderer.PopulatePlanetData()` | [current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L383-L412) | Native construction order Pebbles reproduces |
| `GroundClutterRenderer._renderPassInfo`, `_planetClutterMaxBoundingRadius` (private) | [current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L186-L296) | Render pass for new render data; per-body shadow-culling radius |
| `GroundClutterRenderer.RebuildFrameResources(DeviceEx)`, `Dispose()` | [current/decomp/KSA/GroundClutterRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GroundClutterRenderer.cs#L1799-L1810) | Postfix / prefix targets |
| `PlanetRenderer._groundClutterRendererCreated`, `RebuildGroundClutterResources` | [current/decomp/KSA/PlanetRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetRenderer.cs#L1725-L1743) | Live-renderer test; where rebuild/dispose originate |
| `PhysicsBubble.PopulatePendingExclusions`, `RemoveExcludedClutterInstance`, `GroundClutterStatics`, `SyncGroundClutterStatics` | [current/decomp/KSA/PhysicsBubble.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsBubble.cs#L331-L370) | Drain hits, clear and later reinstall statics |
| `BubbleClutterStatics.Clear(ConstraintSim)` | [current/decomp/KSA/BubbleClutterStatics.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/BubbleClutterStatics.cs#L923) | Removes the body's Bepu clutter statics before the swap |
| `ClutterEcotypePhysicalData.InvalidateGeneratedClutter()` | [current/decomp/KSA/ClutterEcotypePhysicalData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ClutterEcotypePhysicalData.cs#L959) | Forces original collision cells to regenerate on restore |
| `ChuteEnvironmentSnapshot.AppendClutterStatics` | [current/decomp/KSA/ChuteEnvironmentSnapshot.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ChuteEnvironmentSnapshot.cs#L136-L152) | Why the commit must precede the cloth kick |
| `StagingPool._submitted`, `_commandBufferIndex`, `Dispose` | [current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs#L120-L217) | Discard unsubmitted commands after a failed build |
| `Program.PrepareFrame`, `RebuildRenderer` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2140-L2221) | Frame order around the prefix |

## Pitfalls and version notes

- **Mirrored game code.** `DrainExclusions` is a copy of the body of `Universe.SyncGroundClutter`
  (including the KSA 5447+ `Displaced` branch). It matches 5554 line for line; any change to the
  native drain (new exclusion types, new bookkeeping) must be copied by hand.
- **Small-method prefix.** `ExecuteNextClothSolvers` is a ten-line method. The unscience
  `PhysicsFrameHook` comment warns that prefixes on small `Universe` methods can be skipped if the JIT
  inlined them into `PrepareFrame`; Pebbles relies on this prefix anyway. No in-repo evidence shows it
  being skipped, but it is not confirmed in-game either (unverified).
- **Physics gate.** Native clutter collision only exists when `GameSettings.GetGroundClutterCollisions()`
  is on (`PhysicsBubble.SyncGroundClutterStatics` clears statics otherwise); custom colliders are inert
  without it.
- **Incomplete native rollback.** Native constructors can allocate locals before publishing them to a
  field; a failure there leaks GPU memory that `ClutterRetirement` cannot reach. The authors document a
  renderer or game restart as the remedy.[^scope-ground-clutter]
- **Asset drift.** Signatures include LOD mesh ids; KSA 5482 changed Earth tree LOD 5 meshes, so saved
  recipes touching those types fail with "changed since capture; recapture". There is no migration.
- **Compiler-generated field.** `SetTemplate` writes `<BodyTemplate>k__BackingField`; turning
  `Celestial.BodyTemplate` into a computed property would break it silently at the reflection lookup.
- **Budgets.** The 2,000,000 candidate and repeated-vertex budgets in `PebblesRecipe` do not count the
  5447+ displaced-instance render tail (about 2048 extra slots per ecotype), per the authors' notes.
- **Build drift.** The repo's README says it compiles against 5541; the scope docs cite 5482 line
  numbers. Every hook target and private member used here exists unchanged in 5554.

## See also

- [Private clutter material binding and source-color shader variant](./pebbles-private-clutter-materials.md)
- [Removed and displaced clutter state across swaps and saves](./pebbles-clutter-state-persistence.md)
- [Authored clutter colliders](./pebbles-clutter-colliders.md)
- [Self-contained GLB import](./pebbles-glb-import.md) and [GLB material conversion](./pebbles-glb-material-conversion.md)
- [Collider workshop editor](./pebbles-workshop-editor.md)
- [Physics-safe mutation at the PrepareFrame handoff](./lib-physics-frame-hook.md)
- [Consolidated patcher](./unscience-consolidated-patcher.md)
- [Pebbles sub-mod](../submods/pebbles.md) and [mod overview](../overview.md)
- [Transactional apply and exact restore of a body's clutter data](../../../howto/celestials/atmospheres-and-rings/change-loaded-game-data-and-make-the-renderer-notice/change-loaded-game-data-and-make-the-renderer-notice-2.md) - how-to article demonstrating this feature
- [Prefix on ExecuteNextClothSolvers builds private clutter bundles with originals kept for exact restore](../../../howto/celestials/terrain/exclude-or-replace-ground-clutter/exclude-or-replace-ground-clutter-2.md) - how-to article demonstrating this feature
- [Swap clutter bundles in an ExecuteNextClothSolvers prefix](../../../howto/physics/colliders/give-ground-clutter-custom-colliders/give-ground-clutter-custom-colliders-2.md) - how-to article demonstrating this feature

[^scope-ground-clutter]: scope/ground-clutter.md (authors' integration notes)
[^clutter-controller]: pebbles.lib/Runtime/ClutterController.cs
[^clutter-hooks]: pebbles.lib/Runtime/ClutterHooks.cs
[^clutter-retirement]: pebbles.lib/Runtime/ClutterRetirement.cs
[^ksa-staging-pool]: Brutal.VulkanApi.Abstractions.StagingPool (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
[^ksa-chute-environment-snapshot]: KSA.ChuteEnvironmentSnapshot (decompiled, build 2026.10.10.5554)
[^pebbles-source-map]: plans/PEBBLES_SOURCE_MAP.md (authors' native source investigation, build 5402)
[^clutter-controller-grids]: pebbles.lib/Runtime/ClutterController.Grids.cs
[^ksa-physics-bubble]: KSA.PhysicsBubble (decompiled, build 2026.10.10.5554)
[^ksa-planet-renderer]: KSA.PlanetRenderer (decompiled, build 2026.10.10.5554)
[^pebbles-submod]: pebbles.lib/PebblesSubmod.cs
[^unscience-patcher]: unscience/Patcher.cs
[^unscience-mod]: unscience/Mod.cs
