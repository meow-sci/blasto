---
type: Mod Feature
title: Load, reload and roll back Part mods at runtime with a one-state-per-frame loader
description: Parts Now replays KSA's boot asset pipeline for one mod folder after startup, from parse and register through background file loading, GPU binding, incremental game-data attach and thumbnails, one state per frame from the pre-GUI hook
mod: unscience
submod: parts-now
category: assets
techniques: [starmap-lifecycle, xml-asset, reflection, background-thread, vulkan-buffer]
hooks:
  - starmap StarMapBeforeGui (via unscience ISubmod.Update, one loader state per frame)
  - harmony-prefix KSA.Program.OnDrawUiConsole (unscience HiddenUiFrameHook replays Update while the HUD is hidden)
  - reflection KSA.ModLibrary.AllParts, AllPartGameDataReferences, AllMaterials, AllFiles, AllMeshes (through GameRegistry)
lifecycle: [frame:draw-ui, thread:background]
game_types:
  - KSA.ModLibrary
  - KSA.Mod
  - KSA.AssetBundle
  - KSA.ILoader
  - KSA.IBinder
  - KSA.FileReference
  - KSA.MeshReference
  - KSA.PartTemplate
  - KSA.PartGameDataReference
  - KSA.PartModel
  - KSA.PartModelGlass
  - KSA.PartModelDynamic
  - KSA.SerializedCollection
  - KSA.Loading
  - KSA.VehicleEditor
  - Core.Renderer
tags: [runtime-loading, parts, modlibrary, asset-bundle, hot-reload, state-machine, threading]
---

Parts Now lets a player add, re-read or remove a Part mod while the game is running. New parts show
up in the vehicle editor's part browser with thumbnails, mass and connectors. A changed GLB or
texture is really re-read on reload, and a load that fails half-way leaves the game as it was.[^parts-now-readme]

Mechanism in one line: KSA's startup pipeline (`AssetBundle.OnDataLoad` → `ILoader.Load` →
`IBinder.Bind` → game-data attach → thumbnails) is still callable after boot. Parts Now replays it
for one mod at a time as an explicit state machine. It advances exactly one state per frame from
`ISubmod.Update(dt)` and runs only the file reads on a worker thread.

## How it works

Three entry points start a job (`StartInstall`, `StartLoad`, `StartReload`). `Unload` is
synchronous. All of them refuse with a readable sentence instead of throwing: when a job is
running, when the [reflection self-test](./parts-now-reflection-self-test.md) failed, when
[mesh headroom](./parts-now-mesh-headroom-reservation.md) is not usable, or when the mod was loaded
at boot (`ModLibrary.Find(id)` is not null).[^loader-api]

The states, in order ([parts-now.lib/Runtime/RuntimeModLoader.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModLoader.cs#L13-L57)):

| State | What it does | Thread |
|---|---|---|
| `Validate` | Parse each document with KSA's serializer and run [rules V1-V15](./parts-now-bundle-validation.md) as one set. A reload purges its previous load here, only after the rules pass | game |
| `WriteModFolder` | Paste flow only: [write the mod folder](./parts-now-paste-xml-mod-writer.md) | game |
| `CreateMod` | `ModLibrary.Find(id) ?? Mod.MakeUsing(id, mod.toml)`. Not registered into `ModLibrary.Lookup`. `Preload` forced to false | game |
| `RegisterBundles` | Take registry marks and snapshot the mesh cursors, then `AssetBundle.OnDataLoad(mod)` per document | game |
| `RunLoaders` | `Task.Run` over the `ILoader`s this job appended, serially. Polled each frame | **worker** |
| `CheckMeshBudget` | Fail before any bind if the loaders overflowed the shared mesh buffer | game |
| `Bind` | 4 binders per frame, each with its own `StagingPool` (submit and fence wait) | game |
| `AttachGameData` | `PartTemplate.ApplyGameData` for this job's game data only, then `ResolveConsumerFeedPoints` | game |
| `WarmModels` | `PartModel.Get` / `PartModelGlass.Get` / `PartModelDynamic.Get` per model component, so a bad `<Mesh Id>` throws here | game |
| `Thumbnails` | [2 parts per frame](./parts-now-thumbnail-generation.md) against the offscreen thumbnail viewport | game |
| `RefreshEditor` | `VehicleEditor.ResetPartDiameterCache()`, record the job in `RuntimeModRegistry`, add the manifest entry for pasted mods | game |

Details that make it correct rather than merely possible:

- **Use the game's own collections, not copies.** `AssetBundle.OnDataLoad(mod)` registers parts,
  materials and game data and queues loaders exactly as boot does. Boot's `ModLibrary.AttachGameData`
  is not called, because `ApplyGameData` is additive (`AddRange` on rockets, masses, wiring) and would
  double every part attached at boot. Parts Now applies only this job's `PartGameDataReference`s.
  `ResolveConsumerFeedPoints` starts with `ConsumerFeeds.Clear()`, so it is safe to re-run.[^ksa-part-template]
- **Bind only what you added.** `ModLibrary.Bind` runs every binder ever registered in parallel.
  The `Bind` state mirrors one iteration of it for the job's own binders.[^ksa-modlibrary]
- **Read deltas at the right moment.** Marks for all seven registries are taken before the first
  `OnDataLoad`. `AllParts`, `AllPartGameDataReferences`, `AllMaterials` and `ModLibrary.Loaders`
  grow during `RegisterBundles`. `AllFiles`, `AllMeshes` and `ModLibrary.Binders` only grow when
  `FileReference.Load` runs on the worker. So the code reads two delta sets at two moments
  (`CaptureRegistrationDeltas`, `CaptureLoadDeltas`), each in a `finally`/before-fault path so a
  half-finished step is still recorded.[^loader-deltas]
- **Verify the silent loaders.** `FileReference.Load` catches and logs its own exceptions. After the
  worker finishes, `VerifyLoadersProduced` checks each post-condition by hand: a file demoted to a
  reference (duplicate id), an atlas with no meshes, a mesh file with no mesh, a texture that never
  queued a binder, a mesh still marked as a reference. A duplicate declared twice inside the same
  mod is tolerated, and the parts that use the losing copy are marked Degraded.[^loader-deltas][^ksa-file-reference]
- **Record everything for undo.** `LoadedModRecord` ([parts-now.lib/Runtime/LoadedModRecord.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/LoadedModRecord.cs))
  holds the new parts, game data, materials, loaders, files, meshes, binders, model-template objects,
  part ids and the mesh-cursor snapshot. It fills in as each state completes, so any failure can be
  unwound.[^loaded-mod-record]
- **Failure splits at the first bind.** `Fail()` calls `RuntimeModUnloader.Rollback` (purge plus
  rewind of the mesh cursors) while `BoundAny` is false, and `Purge` (with leak accounting) once a
  bind has started. A paste-flow folder this job wrote is deleted again so the id stays usable.
  Cancel is honoured only between states and never while the worker runs.[^loader-core][^runtime-unloader]
- **Reload = purge + load.** `SerializedCollection.Register` returns false for a duplicate key, and
  `FileReference.Load` then marks itself a reference and returns before `DoLoad`. A reload without a
  purge would therefore never re-read a changed file. The purge removes every id this mod introduced
  (list and hash dictionary), so the fresh load registers cleanly. It runs at the end of `Validate`,
  so XML that no longer validates leaves the loaded mod untouched.[^ksa-serialized-collection][^loader-states]
- **The part browser needs no refresh.** `VehicleEditor.PartWindow.OnDrawUi` iterates
  `ModLibrary.AllParts.GetList()` every frame. Only the lazily built diameter-filter cache needs
  `ResetPartDiameterCache()`.[^ksa-vehicle-editor][^editor-refresh]

Save integration (supermod only): `PartsNowSubmod` contributes a `parts-now` save participant that
records each runtime mod id and its part ids, and on load warns when a template no longer resolves.
It never loads or unloads anything from a save.[^parts-now-saves]

## Lifecycle and timing

`RuntimeModLoader.Step()` is called once per frame from `PartsNowSubmod.Update(dt)`. In the
supermod that is `[StarMapBeforeGui]`, StarMap's Harmony prefix on `Program.OnDrawUiFrame`
(`Program.cs` L2267, def L3131). While the HUD is hidden, unscience replays `Update` from
`HiddenUiFrameHook`, a prefix on `OnDrawUiConsole` (L2275).[^unscience-mod][^ksa-program] The
standalone host only uses `[StarMapBeforeGui]`, so its jobs pause while the HUD is hidden.[^parts-now-mod]

What that point guarantees, in build 5554:

| Fact | Where | Why it matters |
|---|---|---|
| Main thread, after `PrepareImGui` (L2261) and before `ImGui.Render` (L2286) | `Program.OnFrame` | Log lines and panel state can be touched; nothing is rendering |
| Before `ImGuiWindow.DrawAllStaticWindows` (L3140, inside `OnDrawUiFrame`) | The editor part browser is a static window | A purge that frees thumbnail textures runs before the browser records `ImageButton`s for them this frame |
| Before `OnPreRender` → `_renderer.TryAcquireNextFrame()` (L2420) | `Program.OnPreRender` | `Bind` and `Thumbnails` may submit their own command buffers to `renderer.Graphics` and wait on fences without racing the frame's own submit |
| After `ExecuteNext*Solvers` (L2217-L2221) | `PrepareFrame` | Simulated vessels are on worker threads. The loader never touches vessels; the unload gate only reads them |

The worker step is the only off-thread code. It runs `ILoader.Load()` and nothing else.
`FileReference.Load` calls `Loading.Task(...)`, whose push path ends in `Loading.OnFrame()`, which
renders and submits a whole loading-screen ImGui frame on the main thread. `Loading.OnFrame` returns
immediately when `!Program.IsMainThread()`, so on a worker that path is a no-op.[^ksa-loading][^ksa-file-reference]
Binders, by contrast, must stay on the game thread: each `StagingPool` submits to the graphics
queue, and queue submission must not race the main thread.[^loader-gpu-states]

A new part appears in the browser from the frame its `RegisterBundles` state ran, because the
browser lists `AllParts` live. Its mass and thumbnail arrive a few frames later. Until then
`DrawPartImageButton` sees a null `Thumbnail` and draws a plain button.[^ksa-vehicle-editor]

## Key code

| Path | Role |
|---|---|
| [parts-now.lib/Runtime/RuntimeModLoader.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModLoader.cs#L203-L267) | `Step()` dispatch, cancel, `Fail()` rollback-vs-purge split |
| [parts-now.lib/Runtime/RuntimeModLoaderApi.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModLoaderApi.cs) | `StartInstall` / `StartLoad` / `StartReload` / `Unload` and their refusals |
| [parts-now.lib/Runtime/RuntimeModLoaderStates.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModLoaderStates.cs) | Validate, WriteModFolder, CreateMod, RegisterBundles, RunLoaders, reload purge |
| [parts-now.lib/Runtime/RuntimeModLoaderGpuStates.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModLoaderGpuStates.cs) | CheckMeshBudget, Bind, AttachGameData, WarmModels, Thumbnails, CompleteJob |
| [parts-now.lib/Runtime/RuntimeModLoaderDeltas.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModLoaderDeltas.cs) | Registry marks, the two delta capture points, loader post-condition checks |
| [parts-now.lib/Runtime/RuntimeModLoaderJob.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModLoaderJob.cs) | Per-job state carried between frames |
| [parts-now.lib/Runtime/RuntimeModRegistry.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModRegistry.cs) | Session map of mods this loader owns (the only ones it may reload or unload) |
| [parts-now.lib/Runtime/EditorRefresh.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/EditorRefresh.cs) | The single editor nudge after a load or purge |
| [parts-now.lib/PartsNowSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/PartsNowSubmod.cs#L60-L78) | Calls `Step()` once per frame, then runs any deferred unload |

## Snippets

The per-frame driver in the sub-mod:

```csharp
// parts-now.lib/PartsNowSubmod.cs L60-L78
    public void Update(double dt)
    {
        if (!_firstFrameDone)
        {
            // By the first real UI frame ModLibrary.Bind() has run and the enlarged shared buffers
            // are allocated, so the bump cursors can be rewound to the startup watermark.
            MeshBudget.OnFirstFrame();
            _firstFrameDone = true;
        }

        // Exactly once per frame, and only from here: the loader's Bind and Thumbnails states submit
        // command buffers and block on fences, which is only safe inside Program.OnDrawUiFrame.
        RuntimeModLoader.Step();

        // An unload confirmed in the UI is deferred to here for the same reason a purge is: it frees
        // ImGui textures, and by the time the panels draw, the vehicle editor's part browser has
        // already emitted ImageButton draw commands holding those descriptor sets.
        _modFolderPanel.ProcessPendingActions();
    }
```

The only off-thread step, polled from later frames:

```csharp
// parts-now.lib/Runtime/RuntimeModLoaderStates.cs L243-L291
    private static void StateRunLoaders(LoadJob job)
    {
        if (job.LoaderTask is null)
        {
            job.PendingLoaders.AddRange(job.Record.NewLoaders);
            List<ILoader> loaders = job.PendingLoaders;

// ...
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
// ...
        Transition(LoadJobState.CheckMeshBudget);
    }
```

Binding only this job's binders, a few per frame, on the game thread:

```csharp
// parts-now.lib/Runtime/RuntimeModLoaderGpuStates.cs L80-L94
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
```

The incremental game-data attach that replaces `ModLibrary.AttachGameData`:

```csharp
// parts-now.lib/Runtime/RuntimeModLoaderGpuStates.cs L275-L309
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
// ...
        foreach (PartTemplate template in resolve)
        {
            if (template.IsSubPart)
            {
                continue;
            }

            try
            {
                template.ResolveConsumerFeedPoints();
                resolved++;
            }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `AssetBundle.OnDataLoad(Mod)` | [current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L84-L108) | Registers every asset in a parsed document[^ksa-asset-bundle] |
| `Mod.MakeUsing`, `Mod.Preload`, `Mod.DirectoryPath` | [current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L103-L111) | The `Mod` the bundles register against[^ksa-mod] |
| `ModLibrary.Loaders`, `ModLibrary.Binders`, `ModLibrary.Find`, `ModLibrary.Bind` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L156-L230) | Loader/binder queues, boot-mod test, the per-binder body mirrored by `Bind` |
| `FileReference.OnDataLoad` / `Load` | [current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L39-L77) | Loader queued only when `!mod.Preload`; duplicate id demotes to a reference before `DoLoad` |
| `SerializedCollection<T>.Register` | [current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L20-L35) | Returns false for a duplicate key, the reason reload must purge first |
| `Loading.OnFrame` | [current/decomp/KSA/Loading.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Loading.cs#L91-L96) | Early return off the main thread keeps worker loads silent |
| `PartTemplate.ApplyGameData` / `ResolveConsumerFeedPoints` | [current/decomp/KSA/PartTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTemplate.cs#L265-L420) | Additive merge (applied once per new game data) and idempotent feed resolution |
| `PartModel.Get` (and Glass/Dynamic) | [current/decomp/KSA/PartModel.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModel.cs#L417-L433) | Warms the model caches; scans `Instances` by `Template.Id`[^ksa-part-model] |
| `VehicleEditor.ResetPartDiameterCache`, `PartWindow.OnDrawUi` | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L7215-L7218) | Diameter cache reset; the browser re-reads `AllParts` each frame (L314) |
| `Program.OnFrame`, `OnDrawUiFrame`, `OnPreRender` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2442) | Frame points used in the timing table (`OnPreRender` def L2372, acquire at L2420) |

## Pitfalls and version notes

- Mods loaded at boot can never be reloaded or unloaded: their templates were registered by KSA and
  the loader cannot account for them. The folder scanner and `StartLoad` both refuse them.
- `Mod.MakeUsing` keeps the runtime `Mod` out of `ModLibrary.Lookup`, so `ModLibrary.Find` stays a
  reliable "loaded at boot" test. For a folder that was also enabled at boot, a reload reuses KSA's
  own `Mod`; the scanner checks the Parts Now registry first for that reason.
- While `RunLoaders` is in flight, the worker appends to `AllFiles` and `AllMeshes`.
  `SerializedCollection.Register` takes the collection's lock, but `GetList()` readers do not. Another
  mod that enumerates those lists on the main thread during those frames could see a concurrent
  modification (inferred from the decomp, not observed).
- 5554 has a public `SerializedCollection<T>.Deregister`. Parts Now still removes items through
  reflection; the authors noted that `Deregister`'s swap-remove would reorder the part browser.[^scope-part-editor]
  See [the purge](./parts-now-runtime-unload-purge.md).
- Explosion definitions (`<Explosion>`, `<ExplosionVolume>`, added in KSA 5438) and the startup-only
  libraries are rejected by validation, because the loader does not snapshot or purge their
  registries.
- A part whose XML declares `<Thumbnail>` (to set a `ModelTransform`) has a non-null `Thumbnail`
  with no image between `RegisterBundles` and `Thumbnails`. If the editor part browser is open in
  those frames, `DrawPartImageButton` calls `GetOrCreateImGuiTexture`, which hands the unset image
  view to `ImGuiBackend.Vulkan.AddTexture` (inferred from the decomp, not observed).
- Pasted or reloaded parts must keep their mod folder: the manifest entry makes them load at the
  next launch, and saved vehicles reference their template ids.

## See also

- [Runtime unload and purge](./parts-now-runtime-unload-purge.md)
- [Bundle validation rules V1-V15](./parts-now-bundle-validation.md)
- [Mesh headroom reservation](./parts-now-mesh-headroom-reservation.md)
- [Thumbnail generation](./parts-now-thumbnail-generation.md)
- [Paste XML into a new mod folder](./parts-now-paste-xml-mod-writer.md)
- [Reflection self-test](./parts-now-reflection-self-test.md)
- [Hidden-HUD frame hook](./lib-hidden-ui-frame-hook.md)
- [Scene save coordinator](./lib-scene-save-coordinator.md)
- [Parts Now sub-mod](../submods/parts-now.md)
- [Mod overview](../overview.md)
- [Load a part mod into a running game, one loader state per frame](../../../howto/vehicles-and-parts/parts/load-and-unload-part-mods-at-runtime/load-and-unload-part-mods-at-runtime-1.md) - how-to article demonstrating this feature

[^parts-now-readme]: parts-now.lib/README.md (authors' design notes)
[^loader-api]: parts-now.lib/Runtime/RuntimeModLoaderApi.cs
[^loader-core]: parts-now.lib/Runtime/RuntimeModLoader.cs
[^loader-states]: parts-now.lib/Runtime/RuntimeModLoaderStates.cs
[^loader-gpu-states]: parts-now.lib/Runtime/RuntimeModLoaderGpuStates.cs
[^loader-deltas]: parts-now.lib/Runtime/RuntimeModLoaderDeltas.cs
[^loaded-mod-record]: parts-now.lib/Runtime/LoadedModRecord.cs
[^runtime-unloader]: parts-now.lib/Runtime/RuntimeModUnloader.cs
[^editor-refresh]: parts-now.lib/Runtime/EditorRefresh.cs
[^parts-now-saves]: parts-now.lib/PartsNowSubmod.Saves.cs
[^parts-now-mod]: parts-now/Mod.cs
[^unscience-mod]: unscience/Mod.cs
[^scope-part-editor]: scope/part-editor-and-robotics.md (authors' integration map)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-serialized-collection]: KSA.SerializedCollection (decompiled, build 2026.10.10.5554)
[^ksa-file-reference]: KSA.FileReference (decompiled, build 2026.10.10.5554)
[^ksa-loading]: KSA.Loading (decompiled, build 2026.10.10.5554)
[^ksa-asset-bundle]: KSA.AssetBundle (decompiled, build 2026.10.10.5554)
[^ksa-mod]: KSA.Mod (decompiled, build 2026.10.10.5554)
[^ksa-part-template]: KSA.PartTemplate (decompiled, build 2026.10.10.5554)
[^ksa-part-model]: KSA.PartModel (decompiled, build 2026.10.10.5554)
[^ksa-vehicle-editor]: KSA.VehicleEditor (decompiled, build 2026.10.10.5554)
