---
type: How-To
title: Load a part mod into a running game, one loader state per frame
description: Replay KSA's boot asset pipeline for a single mod folder from a pre-GUI hook, reserving mesh-buffer headroom at AllModsLoaded and writing the mod folder atomically
category: vehicles-and-parts
group: parts
topic: load-and-unload-part-mods-at-runtime
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/unscience/features/parts-now-runtime-part-loading
  - mods/unscience/features/parts-now-mesh-headroom-reservation
  - mods/unscience/features/parts-now-paste-xml-mod-writer
techniques: [reflection, starmap-lifecycle, background-thread]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapBeforeGui]
lifecycle: [startup:all-mods-loaded, frame:draw-ui, thread:background]
game_types:
  - KSA.ModLibrary
  - KSA.AssetBundle
  - KSA.Mod
  - KSA.DeviceMeshInterleaved
  - KSA.PartTemplate
  - KSA.FileReference
  - KSA.ILoader
  - KSA.IBinder
  - KSA.VehicleEditor
tags: [runtime-loading, state-machine, mesh-buffer, headroom, bind, thumbnails]
---
Outcome: a part mod folder (or XML pasted in-game and written to a new folder) becomes usable parts in the vehicle
editor's part browser without restarting the game, with thumbnails, mass and connectors. A load that fails halfway
leaves the game as it was. unscience's Parts Now does this; this article follows its design. Validation and unloading
are articles 3 and 2.

Prerequisites: StarMap, reflection into internals, patience. Demonstrated by
[unscience/parts-now-runtime-part-loading](../../../../mods/unscience/features/parts-now-runtime-part-loading.md), [unscience/parts-now-mesh-headroom-reservation](../../../../mods/unscience/features/parts-now-mesh-headroom-reservation.md) and
[unscience/parts-now-paste-xml-mod-writer](../../../../mods/unscience/features/parts-now-paste-xml-mod-writer.md).

## Background: how the game does it

See the [overview](overview.md). What the replay depends on:

- `Mod.MakeUsing(id, manifestPath)` builds a `Mod` from a `mod.toml` without registering it in
  `ModLibrary.Lookup` [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L103-L111)[^ksa-mod]; `ModLibrary.Find(id)` returns boot-loaded mods.
- `AssetBundle.OnDataLoad(mod)` registers parts, materials, game data and queues `ILoader`s exactly as boot does
  [current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L79-L100)[^ksa-assetbundle]; `FileReference.OnDataLoad` registers a loader only while `mod.Preload` is false
  [current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L39-L58)[^ksa-filereference].
- Boot's `ModLibrary.AttachGameData` cannot be called again: `ApplyGameData` is additive (`AddRange` on rockets,
  masses and wiring) and would double every part attached at boot [current/decomp/KSA/PartTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTemplate.cs#L265-L351)[^ksa-parttemplate].
  Parts Now applies only the new `PartGameDataReference`s and then calls `ResolveConsumerFeedPoints()`, which
  starts with `ConsumerFeeds.Clear()` and is safe to re-run.
- `ModLibrary.Bind` runs every binder ever registered, in parallel, each with a staging pool whose dispose
  submits and waits on a fence [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1849-L1861)[^ksa-modlibrary]. The runtime path mirrors one iteration for the
  job's own binders only.
- The shared mesh buffers never grow (`DeviceMeshInterleaved.Shared`), so room for later meshes must exist before
  `Build()` runs [current/decomp/KSA/DeviceMeshInterleaved.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DeviceMeshInterleaved.cs#L15-L60)[^ksa-devicemeshinterleaved].
- The part browser iterates `ModLibrary.AllParts.GetList()` every frame; only the lazily built diameter-filter
  cache needs resetting.

## Lifecycle and timing

| Point | Hook | Why this one |
|---|---|---|
| mesh headroom reserved | `StarMapAllModsLoaded` (postfix on `ModLibrary.LoadAll`) | the only mod hook between the last boot mesh creation and the buffer allocation; earlier is too soon, any GUI hook too late |
| cursors rewound | first `Update` after `Bind` | `Bind()` has run and `Build()` allocated `watermark + headroom` |
| one state per frame | `StarMapBeforeGui` (prefix on `Program.OnDrawUiFrame`) | main thread before `ImGui.Render` and before the frame acquires its swapchain image, so `Bind` and thumbnail states may submit their own command buffers and wait on fences |
| file reads | `Task.Run` worker | the only off-thread state; touches only `ILoader.Load()` |

While the HUD is hidden `OnDrawUiFrame` is skipped; unscience replays `Update` from a hidden-HUD hook (see
[game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md)). A new part appears in the browser the frame `RegisterBundles` ran; mass
and thumbnail arrive a few frames later.

## Steps

1. **Reserve mesh headroom at `StarMapAllModsLoaded`.** Record the boot watermark of both bump cursors, add the
   headroom, and warn if the buffer is already built:

```csharp
// parts-now.lib/Runtime/MeshBudget.cs L122-162
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

2. **Rewind on the first frame.** The buffers end up `watermark + headroom` long while the cursors sit at the
   watermark, so runtime meshes take offsets inside the headroom:

```csharp
// parts-now.lib/Runtime/MeshBudget.cs L169-202
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

3. **Drive a state machine one state per call**, exactly once per frame, from your pre-GUI hook:

```csharp
// parts-now.lib/Runtime/RuntimeModLoader.cs L203-250
    public static void Step()
    {
        LoadJob? job = _job;
        if (job is null || !IsBusy)
        {
            return;
        }

        // Cancellation is honoured only at a state boundary, never mid-Vulkan and never while the
        // loader worker is still running (it would keep registering into ModLibrary behind us).
        if (_cancelRequested && CanHonourCancel(job))
        {
            _cancelRequested = false;
            Fail(job, "cancelled by the user during the " + _state + " step.");
            return;
        }

        try
        {
            switch (_state)
            {
                case LoadJobState.Validate:
                    StateValidate(job);
                    break;
                case LoadJobState.WriteModFolder:
                    StateWriteModFolder(job);
                    break;
                case LoadJobState.CreateMod:
                    StateCreateMod(job);
                    break;
                case LoadJobState.RegisterBundles:
                    StateRegisterBundles(job);
                    break;
                case LoadJobState.RunLoaders:
                    StateRunLoaders(job);
                    break;
                case LoadJobState.CheckMeshBudget:
                    StateCheckMeshBudget(job);
                    break;
                case LoadJobState.Bind:
                    StateBind(job);
                    break;
                case LoadJobState.AttachGameData:
                    StateAttachGameData(job);
                    break;
                case LoadJobState.WarmModels:
                    StateWarmModels(job);
                    break;
```

4. **`CreateMod`**: reuse or build the `Mod`, force `Preload` off, require a directory:

```csharp
// parts-now.lib/Runtime/RuntimeModLoaderStates.cs L145-181
    private static void StateCreateMod(LoadJob job)
    {
        string tomlPath = Path.Combine(job.ModDirectory, ModLibrary.MOD_TOML);

        Mod mod;
        try
        {
            mod = ModLibrary.Find(job.ModId) ?? Mod.MakeUsing(job.ModId, tomlPath);
        }
        catch (Exception ex)
        {
            Fail(job, "could not build a Mod from " + tomlPath + ": " + ex.Message);
            return;
        }

        if (mod.Preload)
        {
            LogLine("WARNING — " + ModLibrary.MOD_TOML + " asked for preload; forcing it off, because "
                + "FileReference.OnDataLoad only registers loaders while Preload is false.");
            mod.Preload = false;
        }

        if (string.IsNullOrEmpty(mod.DirectoryPath))
        {
            Fail(job, "the Mod for '" + job.ModId + "' has no DirectoryPath, so no asset path can resolve.");
            return;
        }

        job.Mod = mod;
        job.Record.Mod = mod;
        LogLine("mod '" + mod.Id + "' ready at " + mod.DirectoryPath);
        Transition(LoadJobState.RegisterBundles);
    }

    /// <summary>
    /// Runs <c>AssetBundle.OnDataLoad</c> for each parsed bundle, which is what registers templates,
    /// materials and loaders. Cheap and free of file I/O.
```

5. **`RegisterBundles`**: take registry marks first, run `AssetBundle.OnDataLoad`, and capture deltas in a
   `finally` so a half-finished step is still recorded for rollback:

```csharp
// parts-now.lib/Runtime/RuntimeModLoaderStates.cs L183-226
    private static void StateRegisterBundles(LoadJob job)
    {
        Mod? mod = job.Mod;
        if (mod is null)
        {
            Fail(job, "RegisterBundles reached with no Mod.");
            return;
        }

        TakeMarks(job);

        string? failure = null;
        try
        {
            foreach (ParsedBundle parsed in job.Bundles)
            {
                parsed.Bundle.OnDataLoad(mod);
            }
        }
        catch (Exception ex)
        {
            // Whatever the earlier bundles registered is already live, so the deltas below still have
            // to be captured — otherwise the rollback would leave it behind.
            failure = "registering the bundles threw " + ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            CaptureRegistrationDeltas(job);
            CollectPartMetadata(job);
        }

        LoadedModRecord record = job.Record;
        LogLine("registered " + record.NewParts.Count + " part(s), " + record.NewGameData.Count
            + " game-data entr(ies), " + record.NewMaterials.Count + " material(s), "
            + record.NewLoaders.Count + " loader(s).");

        if (failure is not null)
        {
            Fail(job, failure);
            return;
        }

        Transition(LoadJobState.RunLoaders);
    }
```

6. **`RunLoaders`**: run the queued `ILoader.Load()` calls on a worker; poll completion each frame; verify the
   silent loaders produced what they should:

```csharp
// parts-now.lib/Runtime/RuntimeModLoaderStates.cs L243-291
    private static void StateRunLoaders(LoadJob job)
    {
        if (job.LoaderTask is null)
        {
            job.PendingLoaders.AddRange(job.Record.NewLoaders);
            List<ILoader> loaders = job.PendingLoaders;

            LogLine("loading " + loaders.Count + " file(s) on a background thread.");
            job.LoaderTask = Task.Run(() =>
            {
                foreach (ILoader loader in loaders)
                {
                    loader.Load();
                }
            });

            return;
        }

        if (!job.LoaderTask.IsCompleted)
        {
            return;
        }

        // Capture first: a faulted task may still have registered files, meshes and binders before it
        // threw, and the rollback has to know about them.
        CaptureLoadDeltas(job);

        if (job.LoaderTask.IsFaulted)
        {
            Exception? error = job.LoaderTask.Exception?.GetBaseException();
            Fail(job, "the loader step threw " + (error?.GetType().Name ?? "an exception") + ": "
                + (error?.Message ?? "unknown error"));
            return;
        }

        LoadedModRecord record = job.Record;
        LogLine("loaded " + record.NewFiles.Count + " file(s), producing " + record.NewMeshes.Count
            + " mesh(es) and " + record.NewBinders.Count + " binder(s).");

        string? problem = VerifyLoadersProduced(job);
        if (problem is not null)
        {
            Fail(job, problem);
            return;
        }

        Transition(LoadJobState.CheckMeshBudget);
    }
```

7. **`CheckMeshBudget`, then `Bind`** four binders per frame, each with its own staging pool; set `BoundAny` before
   the first submit so a later failure purges rather than rewinds the cursors:

```csharp
// parts-now.lib/Runtime/RuntimeModLoaderGpuStates.cs L70-100
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
```

8. **`AttachGameData`** only for this job's game data, then resolve consumer feeds, then warm the models (so a bad
   `<Mesh Id>` throws here, not at the first draw), render thumbnails two per frame, reset the editor's diameter
   cache:

```csharp
// parts-now.lib/Runtime/RuntimeModLoaderGpuStates.cs L264-304
    private static void StateAttachGameData(LoadJob job)
    {
        LoadedModRecord record = job.Record;
        HashSet<PartTemplate> resolve = new HashSet<PartTemplate>(ReferenceEqualityComparer.Instance);
        int attached = 0;

        foreach (PartTemplate template in record.NewParts)
        {
            resolve.Add(template);
        }

        foreach (PartGameDataReference gameData in record.NewGameData)
        {
            PartTemplate? target = GameRegistry.AllParts.Find(gameData.Hash);
            if (target is null)
            {
                LogLine("WARNING — PartGameData '" + gameData.Id + "' matches no Part.");
                continue;
            }

            try
            {
                target.ApplyGameData(gameData);
                resolve.Add(target);
                attached++;
            }
            catch (Exception ex)
            {
                LogLine("could not apply PartGameData '" + gameData.Id + "' — " + ex.Message);
                record.SetResult(target.Id, PartLoadStatus.Degraded, "game data failed to apply: " + ex.Message);
            }
        }

        int resolved = 0;
        foreach (PartTemplate template in resolve)
        {
            if (template.IsSubPart)
            {
                continue;
            }

```

9. **Writing the mod folder from inside the game** (the paste flow): validate the id against every place KSA could
   know it, write XML first and `mod.toml` last, each file via a `.tmp` sibling with UTF-8 no BOM and `\n` endings,
   and delete the folder again if the job fails:

```csharp
// parts-now.lib/Io/ModFolderWriter.cs L329-342
    private static void WriteAtomic(string destination, string content, List<string> temporaryFiles)
    {
        string temp = destination + ".tmp";
        temporaryFiles.Add(temp);

        File.WriteAllText(temp, NormaliseLineEndings(content), Utf8NoBom);

        // overwrite:false is what the plan asks for and is what a fresh mod folder always hits; a
        // reused folder (or a merged mod.toml) has the destination already present, and there
        // overwrite:true is the only way the move can succeed.
        File.Move(temp, destination, overwrite: File.Exists(destination));

        temporaryFiles.Remove(temp);
    }
```

10. On success, add a `ModEntry` to `ModLibrary.Manifest` (`New = false`) and save it so the mod also loads at the
    next launch.

## Complete example

- [parts-now.lib/Runtime/RuntimeModLoader.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModLoader.cs)[^unscience-runtimemodloader], [parts-now.lib/Runtime/RuntimeModLoaderStates.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModLoaderStates.cs)[^unscience-runtimemodloaderstates],
  [parts-now.lib/Runtime/RuntimeModLoaderGpuStates.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModLoaderGpuStates.cs)[^unscience-runtimemodloadergpustates]
- [parts-now.lib/Runtime/MeshBudget.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/MeshBudget.cs)[^unscience-meshbudget] - headroom
- [parts-now.lib/Io/ModFolderWriter.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Io/ModFolderWriter.cs)[^unscience-modfolderwriter] and [parts-now.lib/Io/ModIdValidator.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Io/ModIdValidator.cs)[^unscience-modidvalidator] - the paste flow
- [parts-now.lib/PartsNowSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/PartsNowSubmod.cs#L59-L77)[^unscience-partsnowsubmod] - the hooks

## Variations and gotchas

- **Marks and two delta moments.** `AllParts`, game data, materials and `ModLibrary.Loaders` grow during
  `RegisterBundles`; `AllFiles`, `AllMeshes` and `Binders` grow only when `FileReference.Load` runs on the worker.
  Capture each set at its own moment.
- **A reload needs a purge first.** `Register` returns false for a duplicate key and `FileReference.Load` then
  marks itself a reference and skips `DoLoad`, so a changed file would never be re-read.
- **Failure splits at the first bind.** Before it: purge and rewind the mesh cursors. After it: purge and count the
  orphaned bytes as a leak (the allocator has no free list).
- **Configuration is frozen at reserve time.** Headroom (`vertexHeadroomMiB` 48, `indexHeadroomMiB` 12 by default)
  applies at the next launch.
- **Do not call from a worker.** `Loading.OnFrame` is a no-op off the main thread, but binders and everything
  else must stay on the game thread.
- **Internals.** `AllParts` and friends are internal; Parts Now reads them by reflection and runs a self-test at
  startup so a rename disables loading instead of crashing.

## See also

- [Overview](overview.md), [Article 2](load-and-unload-part-mods-at-runtime-2.md), [Article 3](load-and-unload-part-mods-at-runtime-3.md)
- [unscience/parts-now-runtime-part-loading](../../../../mods/unscience/features/parts-now-runtime-part-loading.md)
- [Define a part with its own game data and GLB model](../define-a-part-with-game-data/overview.md)

[^ksa-modlibrary]: current/decomp/KSA/ModLibrary.cs (decompiled, build 2026.10.10.5554)
[^unscience-runtimemodloadergpustates]: unscience: parts-now.lib/Runtime/RuntimeModLoaderGpuStates.cs
[^unscience-modidvalidator]: unscience: parts-now.lib/Io/ModIdValidator.cs
[^unscience-partsnowsubmod]: unscience: parts-now.lib/PartsNowSubmod.cs
[^ksa-parttemplate]: current/decomp/KSA/PartTemplate.cs (decompiled, build 2026.10.10.5554)
[^ksa-mod]: current/decomp/KSA/Mod.cs (decompiled, build 2026.10.10.5554)
[^ksa-devicemeshinterleaved]: current/decomp/KSA/DeviceMeshInterleaved.cs (decompiled, build 2026.10.10.5554)
[^unscience-modfolderwriter]: unscience: parts-now.lib/Io/ModFolderWriter.cs
[^unscience-runtimemodloaderstates]: unscience: parts-now.lib/Runtime/RuntimeModLoaderStates.cs
[^ksa-assetbundle]: current/decomp/KSA/AssetBundle.cs (decompiled, build 2026.10.10.5554)
[^unscience-runtimemodloader]: unscience: parts-now.lib/Runtime/RuntimeModLoader.cs
[^unscience-meshbudget]: unscience: parts-now.lib/Runtime/MeshBudget.cs
[^ksa-filereference]: current/decomp/KSA/FileReference.cs (decompiled, build 2026.10.10.5554)
