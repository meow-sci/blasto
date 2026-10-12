---
type: How-To
title: Swap clutter bundles in an ExecuteNextClothSolvers prefix
description: Commit a replacement clutter graph at the solver handoff, in a Priority.First prefix on Universe.ExecuteNextClothSolvers, after quiescing every solver and the GPU, and keep the originals for exact restore
category: physics
group: colliders
topic: give-ground-clutter-custom-colliders
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/unscience/features/pebbles-ground-clutter-replacement]
techniques: [harmony-prefix, reflection]
hooks:
  - harmony-prefix KSA.Universe.ExecuteNextClothSolvers
  - harmony-postfix KSA.GroundClutterRenderer.RebuildFrameResources
  - harmony-prefix KSA.GroundClutterRenderer.Dispose
lifecycle: [frame:dispatch-solvers, frame:draw-ui, physics:vehicle-solver]
game_types:
  - KSA.Universe
  - KSA.GroundClutterRenderer
  - KSA.ClutterEcotypePhysicalData
  - KSA.CelestialTemplate
  - KSA.JobSystems
tags: [physics, clutter, colliders, harmony, prefix, quiesce, restore, unscience, pebbles]
---

Outcome: a player picks a mesh and colliders, chooses a planet and clutter types, presses Apply, and every instance of
those types is replaced by the new objects with the game's own placement; Restore puts back the exact stock objects. The
UI only queues a detached recipe; a Harmony prefix on `Universe.ExecuteNextClothSolvers` waits for every solver and the
GPU, builds a complete private graph and swaps the per-body arrays in a single main-thread step. Prerequisites:
StarMap and Harmony. The colliders it builds are those of
[approach 1](give-ground-clutter-custom-colliders-1.md).

## Background: how the game does it

`ExecuteNextClothSolvers` has exactly one caller in build 5554, `Program.PrepareFrame`. A prefix on it therefore
runs:

| Step in `PrepareFrame` | Meaning for the swap |
|---|---|
| `JobSystems.*.Wait()` | All workers from the previous step have finished |
| `Universe.Apply*Solvers` | Bubble clutter statics and displaced bodies hold the last committed step |
| `Universe.GetJobSimStep` | Other mutations have run |
| **`ExecuteNextClothSolvers` prefix** | Nothing has snapshotted clutter for the next step yet |
| `ChuteClothSystem.SnapshotAndKick` (inside the method) | Cloth snapshots up to eight nearby clutter collider instances |
| `ExecuteNextVehicleSolvers` -> `SyncGroundClutter()` | Game drains exclusions and reinstalls statics from the new `PlanetPhysicalData[body]` |

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

So the cloth prefix is the earliest main-thread point after every solver has completed and before both new readers
(the cloth snapshot and the vehicle task). A prefix on `ExecuteNextVehicleSolvers` would be too late for cloth.[^ksa-universe]

## Lifecycle and timing

Everything runs on the main thread. `ConstraintSim.UnlockShapes` (used by the physical constructor and private hulls)
throws while the vehicle update is stepping, which is why the build cannot happen from UI code once
`ExecuteNextVehicleSolvers` has kicked the workers. `Device.WaitIdle()` is needed because old render and physical objects own
buffers that in-flight frames may still read; it stalls the GPU, but only on frames with pending work. New clutter cells are
generated during command recording later in the same frame, so the replaced meshes show up on the first rendered frame
after the commit. See the
[lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Capture the stock graph into a serialisable recipe with a SHA-256 signature per ecotype (object ids, LOD mesh ids and
   primitive counts, material ids). Apply re-captures and refuses to queue if any signature changed since.
2. Queue, do not act. `QueueApply` validates and stores a clone in `_pending[bodyId]`; `QueueRestore` stores `null`.
   Nothing native is touched.
3. Install the hooks once. A `Priority.First` prefix on `ExecuteNextClothSolvers` runs the controller's `Process`, which
   returns at once when nothing is pending. Also patch the renderer's `RebuildFrameResources` (postfix, so the
   retained originals are rebuilt too) and `Dispose` (prefix, restore originals first so native disposal frees native objects):

```csharp
// pebbles.lib/Runtime/ClutterHooks.cs L39-L86
        harmony.Patch(Required(typeof(Universe), nameof(Universe.ExecuteNextClothSolvers)), prefix: Method(nameof(BeforeCloth)));
        harmony.Patch(Required(typeof(GroundClutterRenderer), nameof(GroundClutterRenderer.RebuildFrameResources)), postfix: Method(nameof(RebuildOriginals)));
        harmony.Patch(Required(typeof(GroundClutterRenderer), nameof(GroundClutterRenderer.Dispose)), prefix: Method(nameof(BeforeDispose)));
        var serializeSave = AccessTools.Method(typeof(GroundClutterRenderer), nameof(GroundClutterRenderer.SerializeSave), [typeof(List<ClutterEcotypeSaveData>)])
            ?? throw new MissingMethodException(typeof(GroundClutterRenderer).FullName, nameof(GroundClutterRenderer.SerializeSave));
        harmony.Patch(serializeSave, postfix: Method(nameof(AfterSerializeSave)));
        harmony.Patch(Required(typeof(ClutterEcotypeRenderData), nameof(ClutterEcotypeRenderData.RebuildFrameResources)), prefix: Method(nameof(BeforeRebuild)), finalizer: Method(nameof(AfterRebuild)));
        harmony.Patch(Required(typeof(ShaderReference), nameof(ShaderReference.CompileVariantWithCustomOptions)), prefix: Method(nameof(CompileColor)));
    }
    public static void Remove(Harmony harmony, ClutterController controller)
    {
        if (ReferenceEquals(_controller, controller)) { controller.Dispose(); _controller = null; }
        // Main shares one Harmony owner across all submods. Remove only our methods.
        foreach (var original in Harmony.GetAllPatchedMethods().ToArray())
        {
            var patches = Harmony.GetPatchInfo(original);
            if (patches == null) continue;
            foreach (var patch in patches.Prefixes.Concat(patches.Postfixes)
                .Concat(patches.Transpilers).Concat(patches.Finalizers)
                .Where(p => p.owner == harmony.Id && p.PatchMethod.DeclaringType == typeof(ClutterHooks)))
                harmony.Unpatch(original, patch.PatchMethod);
        }
    }
    private static MethodInfo Required(Type type, string name) => AccessTools.Method(type, name) ?? throw new MissingMethodException(type.FullName, name);
    private static HarmonyMethod Method(string name) => new(Required(typeof(ClutterHooks), name)) { priority = Priority.First };

    private static IEnumerable<CodeInstruction> MaterialCalls(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
    {
        var buffer = AccessTools.PropertyGetter(typeof(GroundClutterRenderer), nameof(GroundClutterRenderer.MaterialBuffer));
        var index = Required(typeof(GroundClutterRenderer), nameof(GroundClutterRenderer.GetMaterialIndex));
        var replaced = 0;
        foreach (var instruction in input)
        {
            if (instruction.Calls(buffer)) { instruction.opcode = OpCodes.Call; instruction.operand = Required(typeof(ClutterHooks), nameof(Buffer)); replaced++; }
            else if (instruction.Calls(index)) { instruction.opcode = OpCodes.Call; instruction.operand = Required(typeof(ClutterHooks), nameof(Index)); replaced++; }
            yield return instruction;
        }
        if (replaced != 1) throw new InvalidOperationException($"Pebbles expected one material binding in {__originalMethod.Name}, found {replaced}. Game build is incompatible.");
    }
    private static BufferEx Buffer(GroundClutterRenderer renderer)
        => ReferenceEquals(_context?.Owner, renderer) ? _context.MaterialBuffer!.Value : renderer.MaterialBuffer;
    private static uint Index(GroundClutterRenderer renderer, GroundClutterMaterialReference material)
        => ReferenceEquals(_context?.Owner, renderer) ? _context.MaterialIndices[material.Hash] : renderer.GetMaterialIndex(material);
    private static void BeforeConstruct(object __instance) => _context?.Constructed.Add(__instance);
    private static void BeforeCloth()
    { try { _controller?.Process(); } catch (Exception ex) { _controller?.Report(ex); } }
    private static void RebuildOriginals(GroundClutterRenderer __instance, DeviceEx __0) => _controller?.RebuildOriginals(__instance, __0);
    private static void BeforeDispose(GroundClutterRenderer __instance) => _controller?.RendererDisposing(__instance);
```

4. Quiesce, then build and swap. Wait on the vehicle and cloth solvers, wait the device idle, and touch the vehicle task's
   `SyncWindowBubbles` (its getter throws if the task is still running):

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

5. Build a private bundle: a fresh `GroundClutterReference` whose ecotypes, objects, LODs and materials are all new
   objects (stock meshes and textures are only borrowed, "keep original" colliders are rebuilt privately), then
   `GroundClutterPlacementData`, `ClutterEcotypeRenderData` and `ClutterEcotypePhysicalData` per ecotype exactly as
   `PopulatePlanetData` does, with one shared staging pool whose disposal submits and waits.
6. Swap in one step. Drain pending hits for that body (a copy of `SyncGroundClutter`'s drain), replay removed and displaced
   clutter, clear that body's bubble clutter statics, save the original template, three arrays and shadow bound on first
   apply, clone the `CelestialTemplate` with `MemberwiseClone` and point its `GroundClutterReference` at the private graph,
   write both `Celestial.<BodyTemplate>k__BackingField` and `Astronomical.bodyTemplate`, replace the three per-body arrays
   and the bounding radius, and only then retire the previous bundle:

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

7. Check ownership before overwriting: the body template and all three arrays must still be the objects you installed,
   otherwise throw rather than clobber newer game state. Restore reverses the swap and calls
   `InvalidateGeneratedClutter()` on each original so collision cells regenerate.

## Complete example

| File | Role |
|---|---|
| [pebbles.lib/Runtime/ClutterHooks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterHooks.cs#L39-L86) | Hook installation and the cloth prefix |
| [pebbles.lib/Runtime/ClutterController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterController.cs#L131-L270) | Commit, template rebinding, quiesce helpers |
| [pebbles.lib/Runtime/ClutterGraph.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterGraph.cs#L21-L86) | Private reference graph |
| [pebbles.lib/Runtime/ClutterResources.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterResources.cs#L31-L96) | Native placement, render and physical objects |
| [pebbles.lib/Runtime/ClutterRetirement.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Runtime/ClutterRetirement.cs#L17-L71) | Freeing what a failed build allocated |

## Variations and gotchas

- `DrainExclusions` is a copy of the body of `Universe.SyncGroundClutter`; any change to the native drain must be copied by hand.
- `ExecuteNextClothSolvers` is a ten-line method; the `PhysicsFrameHook` comment warns prefixes on small `Universe`
  methods can be skipped if the JIT inlined them into `PrepareFrame`. No in-repo evidence shows it being skipped, but
  it is not confirmed in game (unverified).
- Native clutter collision only exists when ground clutter collisions are on; custom colliders are inert without it.
- Native constructors can allocate locals before publishing them; a failure there leaks GPU memory that retirement cannot reach.
- Signatures include LOD mesh ids; asset changes (KSA 5482 changed Earth tree LOD 5) make saved recipes fail with "changed since capture".
- `SetTemplate` writes the compiler-generated `<BodyTemplate>k__BackingField`; making `BodyTemplate` a computed property would break it silently.
- Hooks stay in place while idle and unpatch only methods declared by their own class on the shared Harmony owner.

## See also

- [Overview and comparison](overview.md)
- [Subclass ColliderTemplate](give-ground-clutter-custom-colliders-1.md)
- Mod feature: [Pebbles ground clutter replacement](../../../../mods/unscience/features/pebbles-ground-clutter-replacement.md)
- [Mutate vehicles at a physics-safe point](../../solver/mutate-vehicles-at-a-physics-safe-point/overview.md) for the neighbouring windows

[^ksa-universe]: KSA.Universe (decompiled, build 2026.10.10.5554)
