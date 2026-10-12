---
type: How-To
title: Inflate DeviceMeshInterleaved.Shared before the first bind allocates the buffer
description: Reserve headroom in the game's shared interleaved mesh buffer by adding to its size counters at StarMapAllModsLoaded and rewinding the bump cursor on the first UI frame, so meshes created later get valid ranges
category: rendering
group: meshes
topic: grow-game-gpu-buffers-for-runtime-content
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/parts-now-mesh-headroom-reservation]
techniques: [vulkan-buffer, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapBeforeGui]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.DeviceMeshInterleaved, KSA.MeshReference, KSA.ModLibrary, KSA.Program]
tags: [mesh, shared-buffer, bump-allocator, headroom, runtime-loading, parts-now]
---
Outcome: after this change a mod can create and upload part meshes (`MeshReference` with `Interleaved = true`) at any point in a session, not only during startup. Without it a mesh created after startup gets an offset past the end of the game's one shared vertex buffer and one shared index buffer, and uploading it copies out of range.

Demonstrated by unscience's Parts Now ([Reserve headroom in KSA's shared interleaved mesh buffer before it is allocated](../../../../mods/unscience/features/parts-now-mesh-headroom-reservation.md)). Prerequisites: StarMap. No Harmony patch is needed; the mechanism is two writes to public static fields at exactly the right moments.

## Background: how the game does it

All interleaved part meshes live in two device-local buffers owned by `DeviceMeshInterleaved.Shared`. The class is a bump allocator with two public `uint` cursors, `RunningVertexBufferSize` and `RunningIndexBufferSize`. Creating a `DeviceMeshInterleaved` takes its offset from the cursor with an interlocked loop and advances it by the mesh size; nothing is uploaded yet ([current/decomp/KSA/DeviceMeshInterleaved.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DeviceMeshInterleaved.cs#L156-L198)):

```csharp
// current/decomp/KSA/DeviceMeshInterleaved.cs L156-L162
uint runningVertexBufferSize;
do
{
	runningVertexBufferSize = Shared.RunningVertexBufferSize;
}
while (Interlocked.CompareExchange(ref Shared.RunningVertexBufferSize, (uint)(runningVertexBufferSize + VerticesSize), runningVertexBufferSize) != runningVertexBufferSize);
VerticesOffset = new ByteSize(runningVertexBufferSize);
```

The first `DeviceMeshInterleaved.Bind()` calls `Shared.Build()`, which allocates both buffers once, with `BufferSize` equal to the current cursors, and flips `IsBuilt`:

```csharp
// current/decomp/KSA/DeviceMeshInterleaved.cs L33-L40, L51-L58
		public static void Build()
		{
			if (!Interlocked.CompareExchange(ref IsBuilding, value: true, comparand: false))
			{
				BuildBuffers();
				Interlocked.Exchange(ref IsBuilt, value: true);
			}
		}
// ...
			VertexAllocation = allocator.CreateBuffer(new BufferEx.CreateInfo
			{
				Name = "DeviceMeshInterleaved Shared Vertices Buffer",
				AllocRequiredProperties = VkMemoryPropertyFlags.DeviceLocalBit,
				BufferSize = new ByteSize(RunningVertexBufferSize),
				BufferUsage = (VkBufferUsageFlags.TransferSrcBit | VkBufferUsageFlags.TransferDstBit | VkBufferUsageFlags.VertexBufferBit | vkBufferUsageFlags),
				BufferSharingMode = VkSharingMode.Exclusive
			});
```

Each `Bind()` then copies its mesh to its recorded offset. The buffers therefore end up exactly as large as the boot meshes needed and never grow. `Shared.Rebuild()` only recreates them when the IVA ray-tracing setting no longer matches their usage flags ([current/decomp/KSA/DeviceMeshInterleaved.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DeviceMeshInterleaved.cs#L69-L87)).

Startup order in the `Program` constructor decides when you can intervene: `ModLibrary.LoadAll()` creates every boot mesh (cursors grow), then later `ModLibrary.Bind(renderer)` binds every binder in parallel, and the first mesh bind builds the buffers:

```csharp
// current/decomp/KSA/ModLibrary.cs L1849-L1861
public static void Bind(Renderer renderer)
{
	Task task = Parallel.ForEachAsync(Binders, delegate(IBinder binder, CancellationToken token)
	{
		using StagingPool stagingPool = renderer.Allocator.CreateStagingPool(renderer.Graphics, 1);
		binder.Bind(renderer, stagingPool);
		return ValueTask.CompletedTask;
	});
	while (!task.Wait(1))
	{
		Loading.Current?.OnFrame();
	}
}
```

## Lifecycle and timing

| Moment | What runs | State of `DeviceMeshInterleaved.Shared` |
|---|---|---|
| `ModLibrary.LoadAll` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L942)) | Boot loaders create `DeviceMeshInterleaved` objects | cursors grow to the boot watermark; no buffer |
| `[StarMapAllModsLoaded]` | StarMap's Harmony postfix on `ModLibrary.LoadAll` | **inflate both cursors here** |
| `ModLibrary.Bind` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L978)) | first `DeviceMeshInterleaved.Bind` runs `Build()` | buffers allocated at watermark + headroom |
| first `Program.OnDrawUiFrame` (`[StarMapBeforeGui]`) | | **rewind cursors to the watermark** |
| any later frame | your loader creates and binds meshes | offsets fall inside the headroom |

This is `startup:all-mods-loaded`, then `frame:draw-ui` (see the loading rows of [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#process-start-and-loading-sequence)). StarMap fires the hook from a postfix on `ModLibrary.LoadAll`:

```csharp
// StarMap.Core/Patches/ModLibraryPatches.cs L17-L28
[HarmonyPatch(nameof(ModLibrary.LoadAll))]
[HarmonyPostfix]
public static void AfterLoad()
{
    var modRegistry = StarMapCore.Instance?.Loader.ModRegistry;
    if (modRegistry is not ModRegistry registry) return;

    foreach (var (_, @object, method) in registry.Get<StarMapAllModsLoadedAttribute>())
    {
        method.Invoke(@object, []);
    }
}
```

Why those two points: `[StarMapImmediateLoad]` is too early (no meshes counted yet) and every GUI hook is too late (`Build()` has run). The rewind cannot fire before `Bind` because `OnDrawUiFrame` is only called from `Program.OnFrame`, which never runs during the constructor.

## Steps

1. **Capture the watermark and inflate the counters** at `[StarMapAllModsLoaded]`. Add tripwire logging in case a game update moves the allocation earlier:

   ```csharp
// parts-now.lib/Runtime/MeshBudget.cs L122-L162
public static void Reserve()
{
    if (_reserved)
        return;

    try
    {
        // Tripwire for the StarMap ordering invariant: Build() must not have run yet.
        if (DeviceMeshInterleaved.Shared.IsBuilt)
        {
            Console.WriteLine(
                "parts-now: WARNING — DeviceMeshInterleaved.Shared is already built at reservation "
                + "time; the shared buffers cannot grow and runtime mesh loading will be unsafe.");
        }

        _vertexHeadroomBytes = ToBytes(PartsNowSettings.VertexHeadroomMiB);
        _indexHeadroomBytes = ToBytes(PartsNowSettings.IndexHeadroomMiB);

        _watermarkVertexBytes = DeviceMeshInterleaved.Shared.RunningVertexBufferSize;
        _watermarkIndexBytes = DeviceMeshInterleaved.Shared.RunningIndexBufferSize;

        DeviceMeshInterleaved.Shared.RunningVertexBufferSize = _watermarkVertexBytes + _vertexHeadroomBytes;
        DeviceMeshInterleaved.Shared.RunningIndexBufferSize = _watermarkIndexBytes + _indexHeadroomBytes;

        _reserved = true;
        _armed = true;
        _failureReason = null;

        Console.WriteLine(
            $"parts-now: reserved mesh headroom {_vertexHeadroomBytes / BytesPerMiB} MiB vtx / "
            + $"{_indexHeadroomBytes / BytesPerMiB} MiB idx (startup watermark "
            + $"{_watermarkVertexBytes / BytesPerMiB} / {_watermarkIndexBytes / BytesPerMiB} MiB)");
    }
    catch (Exception ex)
    {
        _reserved = false;
        _armed = false;
        _failureReason = $"mesh headroom reservation failed: {ex.Message}";
        Console.WriteLine($"parts-now: {_failureReason}");
    }
}
```

   The headroom is read from `parts-now.toml` (`vertexHeadroomMiB` default 48, `indexHeadroomMiB` default 12, each clamped to 4..512 MiB) and frozen at this call, so a change only applies at the next launch.

2. **Call it from the entry point.** The standalone host and the unscience supermod both call `Initialize` from their own `[StarMapAllModsLoaded]` method:

   ```csharp
// parts-now/Mod.cs L35-L54
/// <summary>
/// StarMap fires this as a Harmony postfix on <c>ModLibrary.LoadAll()</c>, i.e. BEFORE
/// <c>ModLibrary.Bind()</c> allocates the shared interleaved mesh buffer. That ordering is what
/// makes the mesh-headroom reservation in <see cref="PartsNowSubmod.Initialize"/> possible.
/// </summary>
[StarMapAllModsLoaded]
public void OnFullyLoaded()
{
    try
    {
        Patcher.Patch();
        _submod.Initialize();
        _hotkey = ResolveHotkey();
        _isInitialized = true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"parts-now: Error during initialization: {ex.Message}");
    }
}
```

3. **Rewind the cursors on the first UI frame**, when the enlarged buffers exist. The bump cursor must sit at the watermark so runtime meshes get offsets inside the headroom:

   ```csharp
// parts-now.lib/PartsNowSubmod.cs L60-L68
public void Update(double dt)
{
    if (!_firstFrameDone)
    {
        // By the first real UI frame ModLibrary.Bind() has run and the enlarged shared buffers
        // are allocated, so the bump cursors can be rewound to the startup watermark.
        MeshBudget.OnFirstFrame();
        _firstFrameDone = true;
    }
```

   ```csharp
// parts-now.lib/Runtime/MeshBudget.cs L169-L202
public static void OnFirstFrame()
{
    if (!_armed)
        return;

    try
    {
        // By the first UI frame ModLibrary.Bind() (Program.cs:985) has run and Build() has
        // allocated the enlarged buffers. The loading screen never goes through
        // Program.OnDrawUiFrame, so the StarMap gui hooks cannot have fired before Bind().
        if (!DeviceMeshInterleaved.Shared.IsBuilt)
        {
            Console.WriteLine(
                "parts-now: WARNING — DeviceMeshInterleaved.Shared is not built on the first frame; "
                + "the reserved headroom may not have been allocated.");
        }

        DeviceMeshInterleaved.Shared.RunningVertexBufferSize = _watermarkVertexBytes;
        DeviceMeshInterleaved.Shared.RunningIndexBufferSize = _watermarkIndexBytes;
        _armed = false;

        Console.WriteLine(
            $"parts-now: mesh budget armed — {FreeVertexBytes / BytesPerMiB} MiB vtx / "
            + $"{FreeIndexBytes / BytesPerMiB} MiB idx free of "
            + $"{AllocatedVertexBytes / BytesPerMiB} / {AllocatedIndexBytes / BytesPerMiB} MiB allocated");
    }
    catch (Exception ex)
    {
        _armed = false;
        _reserved = false;
        _failureReason = $"mesh budget rewind failed: {ex.Message}";
        Console.WriteLine($"parts-now: {_failureReason}");
    }
}
```

4. **Guard every load against overflow.** After the loader step has created this load's `DeviceMeshInterleaved` objects (cursors bumped, nothing copied), compare the cursors with the allocation; if either passed it, fail before binding anything, because a bind would `vkCmdCopyBuffer` out of range:

   ```csharp
// parts-now.lib/Runtime/RuntimeModLoaderGpuStates.cs L33-L57
private static void StateCheckMeshBudget(LoadJob job)
{
    MeshBudget.Cursors before = job.Record.CursorsBefore;
    ulong vertexNeeded = SaturatingDelta(MeshBudget.UsedVertexBytes, before.VertexBytes);
    ulong indexNeeded = SaturatingDelta(MeshBudget.UsedIndexBytes, before.IndexBytes);

    LogLine("this load wants " + Mib(vertexNeeded) + " MiB of vertex data and " + Mib(indexNeeded)
        + " MiB of index data.");

    if (MeshBudget.WithinBudget)
    {
        Transition(LoadJobState.Bind);
        return;
    }

    bool vertexOverflowed = MeshBudget.UsedVertexBytes > MeshBudget.AllocatedVertexBytes;
    ulong needed = vertexOverflowed ? vertexNeeded : indexNeeded;
    ulong free = vertexOverflowed
        ? SaturatingDelta(MeshBudget.AllocatedVertexBytes, before.VertexBytes)
        : SaturatingDelta(MeshBudget.AllocatedIndexBytes, before.IndexBytes);
    string setting = vertexOverflowed ? "vertexHeadroomMiB" : "indexHeadroomMiB";

    Fail(job, "Mesh headroom exhausted (needed " + Mib(needed) + " MiB, " + Mib(free)
        + " MiB free). Increase `" + setting + "` in parts-now.toml and restart the game.");
}
```

5. **Bind on the game thread, only the new binders.** The stock `ModLibrary.Bind` binds every binder ever registered and runs them in parallel; Parts Now mirrors its per-binder body for the new ones, a few per frame, each with its own staging pool (`StagingPool.Dispose` submits to the graphics queue and blocks, which must not race the frame's submit):

   ```csharp
// parts-now.lib/Runtime/RuntimeModLoaderGpuStates.cs L70-L107
private static void StateBind(LoadJob job)
{
    LoadedModRecord record = job.Record;
    if (record.NewBinders.Count == 0)
    {
        LogLine("nothing to upload to the GPU.");
        Transition(LoadJobState.AttachGameData);
        return;
    }

    // Set before the first submit, not after: from here on a failure must purge (recording the
    // leak) instead of rolling the shared-buffer cursors back over data already copied in.
    job.BoundAny = true;

    Renderer renderer = Program.GetRenderer();
    int batchEnd = Math.Min(job.BinderIndex + BindersPerFrame, record.NewBinders.Count);

    for (; job.BinderIndex < batchEnd; job.BinderIndex++)
    {
        IBinder binder = record.NewBinders[job.BinderIndex];
        try
        {
            using StagingPool pool = renderer.Allocator.CreateStagingPool(renderer.Graphics, 1);
            binder.Bind(renderer, pool);
        }
        catch (Exception ex)
        {
            // One bad binder degrades its own asset; it must not abort a load whose other
            // meshes and textures uploaded cleanly.
            job.BindFailures++;
            if (binder is SerializedId asset && !string.IsNullOrEmpty(asset.Id))
            {
                job.FailedBinderIds.Add(asset.Id);
            }

            LogLine("could not upload " + Describe(binder) + " — " + ex.Message);
        }
    }
```

6. **Rewind safely on a failed load.** Snapshot the cursors before the load and restore them on rollback, never below the watermark and only while nothing created after the snapshot has been bound (bound meshes hold absolute offsets):

   ```csharp
// parts-now.lib/Runtime/MeshBudget.cs L221-L248
public static bool RestoreCursors(Cursors cursors)
{
    // Never rewind past the startup watermark. A default (0,0) snapshot would otherwise hand the
    // next runtime mesh offset 0, and its vkCmdCopyBuffer would overwrite the whole game's
    // geometry inside the shared buffer.
    if (_reserved && (cursors.VertexBytes < _watermarkVertexBytes
        || cursors.IndexBytes < _watermarkIndexBytes))
    {
        Console.WriteLine(
            $"parts-now: refusing to rewind the mesh allocation cursors to "
            + $"{cursors.VertexBytes}/{cursors.IndexBytes} bytes — that is below the startup "
            + $"watermark {_watermarkVertexBytes}/{_watermarkIndexBytes} and would overwrite the "
            + "game's own geometry.");
        return false;
    }

    try
    {
        DeviceMeshInterleaved.Shared.RunningVertexBufferSize = cursors.VertexBytes;
        DeviceMeshInterleaved.Shared.RunningIndexBufferSize = cursors.IndexBytes;
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"parts-now: failed to restore mesh allocation cursors: {ex.Message}");
        return false;
    }
}
```

## Complete example

| File | Role |
|---|---|
| [parts-now.lib/Runtime/MeshBudget.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/MeshBudget.cs#L122-L248) | `Reserve`, `OnFirstFrame`, cursor snapshot and restore, leak accounting |
| [parts-now.lib/PartsNowSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/PartsNowSubmod.cs#L51-L68) | Calls `Reserve` from `Initialize` and `OnFirstFrame` from the first `Update` |
| [parts-now/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now/Mod.cs#L35-L54) | Standalone host entry point |
| [parts-now.lib/Runtime/PartsNowSettings.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/PartsNowSettings.cs) | `parts-now.toml` load and clamping |
| [parts-now.lib/Runtime/RuntimeModLoaderGpuStates.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModLoaderGpuStates.cs) | Budget check and per-frame binding |

## Variations and gotchas

- **The ordering is the whole feature.** Parts Now's comments cite older line numbers; in build 2026.10.10.5554 `LoadAll` is at Program.cs L942 and `Bind` at L978 and the order still holds. `ModLibrary.Bind` does not call `Build()` itself: the first `DeviceMeshInterleaved.Bind()` inside it does.
- **No free list.** The allocator is a monotonic bump pointer. Unloading or reloading a mesh orphans its bytes until restart, so reload budget is roughly headroom divided by mesh size per launch. Parts Now measures `VerticesSize` and `IndicesSize` before disposing and shows a warning past 50 % of the headroom.
- **Other mods that create interleaved meshes late.** A `DeviceMeshInterleaved` created from another mod's `[StarMapAllModsLoaded]` after yours gets an offset beyond the headroom; the rewind and budget math would count those bytes as free (inferred from the code, not observed).
- **`Shared.Rebuild()`** (called from `RebuildRenderer` when the IVA ray-tracing setting changes) sizes the new buffers from the current cursors, so the unused headroom is gone for that session and the next load fails cleanly at the budget check (inferred, not tested).
- **Vertex layout.** Interleaved meshes use `float3` position, optional `float3` normal and optional `float2` uv0 per vertex, and 32-bit indices (stride 4) in the shared index buffer.
- **Restart to resize.** Neither the buffers nor the cursors can be resized safely once bound meshes hold absolute offsets.

## See also

- [Overview](overview.md)
- [Approach 2: instance buffer re-creation](grow-game-gpu-buffers-for-runtime-content-2.md)
- [Reserve headroom in KSA's shared interleaved mesh buffer before it is allocated](../../../../mods/unscience/features/parts-now-mesh-headroom-reservation.md)
- [Draw your own meshes through the engine's mesh renderers](../draw-mod-meshes-with-the-engine-renderers/overview.md), [Free Vulkan resources safely](../../vulkan-passes/free-vulkan-resources-safely/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
