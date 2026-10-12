---
type: Mod Feature
title: Reserve headroom in KSA's shared interleaved mesh buffer before it is allocated
description: Parts Now inflates DeviceMeshInterleaved.Shared size counters from StarMapAllModsLoaded, before ModLibrary.Bind sizes the one shared vertex and index buffer, then rewinds the bump cursor on the first frame so runtime meshes get valid offsets
mod: unscience
submod: parts-now
category: rendering
techniques: [starmap-lifecycle, vulkan-buffer, toml-config]
hooks:
  - starmap StarMapAllModsLoaded (via unscience ISubmod.Initialize, MeshBudget.Reserve)
  - starmap StarMapBeforeGui (via unscience ISubmod.Update, first frame only, MeshBudget.OnFirstFrame)
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.DeviceMeshInterleaved, KSA.MeshReference, KSA.ModLibrary, KSA.Program]
tags: [mesh, vulkan, vertex-buffer, startup, runtime-loading, toml]
---

KSA draws every part mesh out of one shared, device-local vertex buffer and one index buffer.
They are allocated once during startup, sized to exactly what the boot mods needed, and never grow.
A mesh created after that lands past the end of the allocation and its upload copies out of range.
Parts Now (the runtime part loader) makes room for meshes that only arrive later, so a player can
load new parts without restarting.[^mesh-budget]

Mechanism in one line: at `[StarMapAllModsLoaded]`, after every boot mesh has bumped the size
counters but before the first bind allocates the buffers, add N MiB to both counters. On the first
UI frame, rewind the counters to the boot watermark. The buffers end up `watermark + headroom` bytes
long while the bump cursor sits at `watermark`.

## How it works

`DeviceMeshInterleaved.Shared` is a static bump allocator with two `uint` cursors,
`RunningVertexBufferSize` and `RunningIndexBufferSize`. Each `new DeviceMeshInterleaved(hostMesh)`
takes its offset with an `Interlocked.CompareExchange` loop and advances the cursor. Nothing is
uploaded yet. `Shared.Build()` runs once (guarded by `IsBuilding`) and allocates both `BufferEx`es
with `BufferSize = Running*BufferSize`. `DeviceMeshInterleaved.Bind()` then copies each mesh to its
recorded offset with `vkCmdCopyBuffer`.[^ksa-device-mesh]

[parts-now.lib/Runtime/MeshBudget.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/MeshBudget.cs) splits the trick into small game-thread steps:

| Member | When | What it does |
|---|---|---|
| `Reserve()` | `[StarMapAllModsLoaded]` via `PartsNowSubmod.Initialize()` | Reads the headroom from `parts-now.toml`, records the boot watermark, adds the headroom to both cursors. Warns if `Shared.IsBuilt` is already true. Idempotent |
| `OnFirstFrame()` | First `PartsNowSubmod.Update(dt)` | Warns if `Shared.IsBuilt` is still false, then writes the watermark back into both cursors |
| `IsUsable` | Every load start | `Reserve` succeeded and no step recorded a failure. `RuntimeModLoader.CanStart` refuses to load without it |
| `SnapshotCursors()` / `RestoreCursors()` | `RegisterBundles` / rollback | Lets a load that fails before any bind give its bytes back. Refuses to rewind below the watermark |
| `RecordLeak()` / `MeasureMesh()` | Purge step 6 | Counts the bytes an unload or reload orphans. The allocator has no free list |
| `AllocatedVertexBytes`, `UsedVertexBytes`, `FreeVertexBytes`, `WithinBudget` | Budget check and status UI | Read `VertexAllocation.BufferSize` and the live cursor, so the numbers come from the allocation itself |

Configuration lives in `<mods>/parts-now/parts-now.toml`
([parts-now.lib/Runtime/PartsNowSettings.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/PartsNowSettings.cs)).
The path is built from `ModLibrary.LocalModsFolderPath`. The file is read with Tomlyn on first
access and never throws.

| Key | Default | Range |
|---|---|---|
| `vertexHeadroomMiB` | 48 | clamped to 4..512 |
| `indexHeadroomMiB` | 12 | clamped to 4..512 |
| `hotkey` | `F10` | standalone window toggle, any `ImGuiKey` name |

The values are frozen inside `Reserve()`, so a change saved from the Settings UI only applies at the
next launch. The UI says so next to the sliders.[^parts-now-settings]

Downstream guards:

- `CheckMeshBudget` runs after the loader worker has created this load's `DeviceMeshInterleaved`
  objects (cursors bumped, nothing copied yet). If either cursor is now past its allocation, the
  job fails with "Mesh headroom exhausted (needed X MiB, Y MiB free). Increase `vertexHeadroomMiB`
  ...". The rollback then rewinds the cursors to the job's snapshot.[^loader-gpu-states][^runtime-unloader]
- After the first bind, a failure purges instead of rolling back. A bound mesh has already copied
  data to an absolute offset, so handing that range out again would corrupt it.
- Unload and reload measure each mesh's `VerticesSize`/`IndicesSize` before disposing it and add
  them to the leak counter.[^purge-steps] The status strip shows "Orphaned by unload / reload" and
  turns it into a warning once leaks pass 50 % of the headroom on either buffer.[^status-panel]

## Lifecycle and timing

Everything happens inside the `Program` constructor, before the first frame, except the rewind.
Order in build 5554:[^ksa-program][^ksa-modlibrary][^ksa-mesh-reference]

| Step | Where | State of the shared buffer |
|---|---|---|
| Boot loaders run | `ModLibrary.LoadAll` (`Program.cs` L942) runs every `ILoader.Load` through `Parallel.ForEachAsync`; `MeshReference.Load` creates `new DeviceMeshInterleaved(...)` for interleaved meshes | Cursors grow to the boot watermark; no buffer exists |
| `[StarMapAllModsLoaded]` | StarMap's Harmony postfix on `ModLibrary.LoadAll` invokes every mod's attribute method[^starmap-modlibrary-patches] | **`Reserve()` runs here**: watermark captured, cursors inflated |
| More startup | `AssignDefaults`, viewports, render targets, `OnBoot` commands, `JobSystems.Initialize` (L943-L974) | Only `MeshAtlasFileReference.DoLoad` (a loader) creates interleaved meshes, and no loader runs here |
| Bind | `ModLibrary.Bind(_renderer)` (L978): every `IBinder.Bind` in parallel; the first `DeviceMeshInterleaved.Bind()` calls `Shared.Build()` | Buffers allocated at `watermark + headroom` |
| First frame | `Program.OnFrame` → `OnDrawUiFrame` (L2267), whose StarMap prefix is `[StarMapBeforeGui]` → `ISubmod.Update` | **`OnFirstFrame()`** rewinds the cursors to the watermark |
| Any later frame | `RuntimeModLoader` worker → `MeshReference.Load` | Runtime meshes take offsets inside the headroom |

Why these points:

- `[StarMapAllModsLoaded]` is the only mod hook between the last boot mesh creation and the
  allocation. `[StarMapImmediateLoad]` is too early (no meshes counted yet) and every GUI hook is
  too late (`Build()` has run). `OnDrawUiFrame` is only called from `Program.OnFrame`, which never
  runs during the constructor, so the rewind cannot fire before `Bind`.
- In the supermod, unscience's single `[StarMapAllModsLoaded]` method calls `Initialize()` on every
  sub-mod in list order, so Parts Now's reservation still lands at that point.[^unscience-mod]
  The standalone host calls `PartsNowSubmod.Initialize()` from its own `[StarMapAllModsLoaded]`.[^parts-now-mod]
- Writing the cursors is only safe on the game thread while no mesh is being created. At reserve
  time no loader is running. At the rewind no Parts Now job can exist yet. Later reads and writes
  happen only from `Update(dt)` between jobs, or in states where the worker has finished.

## Key code

| Path | Role |
|---|---|
| [parts-now.lib/Runtime/MeshBudget.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/MeshBudget.cs#L122-L202) | `Reserve`, `OnFirstFrame`, cursor snapshot/restore, leak accounting |
| [parts-now.lib/PartsNowSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/PartsNowSubmod.cs#L51-L68) | Calls `Reserve` from `Initialize` and `OnFirstFrame` from the first `Update` |
| [parts-now/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now/Mod.cs#L35-L53) | Standalone host: `[StarMapAllModsLoaded]` → `Initialize` |
| [parts-now.lib/Runtime/PartsNowSettings.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/PartsNowSettings.cs) | `parts-now.toml` load/save, clamping |
| [parts-now.lib/Runtime/RuntimeModLoaderGpuStates.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModLoaderGpuStates.cs#L33-L57) | `CheckMeshBudget` state, fails before any bind |
| [parts-now.lib/Runtime/RuntimeModUnloader.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModUnloader.cs#L169-L200) | `Rollback` rewinds the cursors without recording a leak |
| [parts-now.lib/Runtime/RuntimeModPurgeSteps.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModPurgeSteps.cs#L197-L241) | Purge step 6 measures and records orphaned bytes |
| [parts-now.lib/Ui/StatusPanel.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Ui/StatusPanel.cs) | Budget bars and the leak warning |

## Snippets

Reservation at `[StarMapAllModsLoaded]`:

```csharp
// parts-now.lib/Runtime/MeshBudget.cs L122-L148
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
```

The rewind on the first UI frame, and the guard that protects boot geometry on rollback:

```csharp
// parts-now.lib/Runtime/MeshBudget.cs L169-L227
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
// ...
    public static bool RestoreCursors(Cursors cursors)
    {
        // Never rewind past the startup watermark. A default (0,0) snapshot would otherwise hand the
        // next runtime mesh offset 0, and its vkCmdCopyBuffer would overwrite the whole game's
        // geometry inside the shared buffer.
        if (_reserved && (cursors.VertexBytes < _watermarkVertexBytes
            || cursors.IndexBytes < _watermarkIndexBytes))
```

The game side: the bump allocation in the constructor, and the one-shot build triggered by the first bind:

```csharp
// current/decomp/KSA/DeviceMeshInterleaved.cs L156-L198
		uint runningVertexBufferSize;
		do
		{
			runningVertexBufferSize = Shared.RunningVertexBufferSize;
		}
		while (Interlocked.CompareExchange(ref Shared.RunningVertexBufferSize, (uint)(runningVertexBufferSize + VerticesSize), runningVertexBufferSize) != runningVertexBufferSize);
		VerticesOffset = new ByteSize(runningVertexBufferSize);
// ...
	public void Bind()
	{
		Renderer renderer = Program.GetRenderer();
		Shared.Build();
		while (!Interlocked.CompareExchange(ref Shared.IsBuilt, value: false, comparand: false))
		{
		}
```

StarMap's hook that makes the window exist:

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

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `DeviceMeshInterleaved.Shared.RunningVertexBufferSize` / `RunningIndexBufferSize` | [current/decomp/KSA/DeviceMeshInterleaved.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DeviceMeshInterleaved.cs#L25-L27) | Public static `uint` cursors that are inflated, rewound and snapshotted |
| `DeviceMeshInterleaved.Shared.IsBuilt` | [current/decomp/KSA/DeviceMeshInterleaved.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DeviceMeshInterleaved.cs#L31) | Ordering tripwire at reserve and rewind |
| `DeviceMeshInterleaved.Shared.Build` / `BuildBuffers` | [current/decomp/KSA/DeviceMeshInterleaved.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DeviceMeshInterleaved.cs#L33-L67) | One-shot allocation sized from the cursors |
| `DeviceMeshInterleaved.Shared.VertexAllocation` / `IndexAllocation` (`BufferEx.BufferSize`) | [current/decomp/KSA/DeviceMeshInterleaved.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DeviceMeshInterleaved.cs#L19-L21) | Authoritative allocated size for the budget |
| `DeviceMeshInterleaved..ctor(MeshAsset)`, `VerticesSize`, `IndicesSize` | [current/decomp/KSA/DeviceMeshInterleaved.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DeviceMeshInterleaved.cs#L131-L190) | Where offsets are taken; sizes measured for leak accounting |
| `MeshAtlasFileReference.DoLoad` | [current/decomp/KSA/MeshAtlasFileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshAtlasFileReference.cs#L26-L50) | The only creator of interleaved meshes (`Interleaved = true`)[^ksa-mesh-atlas] |
| `MeshReference.Load` / `DeviceMeshesInterleaved` | [current/decomp/KSA/MeshReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MeshReference.cs#L122-L133) | Creates the interleaved meshes and registers the binder |
| `ModLibrary.LoadAll` / `ModLibrary.Bind` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L478-L522) | Loader phase (cursors grow) and bind phase (buffers allocated, L1849) |
| `Program` constructor startup sequence | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L942-L978) | `LoadAll` at L942, `Bind` at L978 |
| `ModLibrary.LocalModsFolderPath` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L178) | Location of `parts-now.toml` |

## Pitfalls and version notes

- The ordering is the whole feature. The code comments cite `Program.cs:956`/`:985` from an older
  build. In 5554 `LoadAll` is at L942 and `Bind` at L978, and the order still holds. Note that
  `ModLibrary.Bind` does not call `Shared.Build()` itself: the first `DeviceMeshInterleaved.Bind()`
  inside it does.
- Both tripwires only log. If a KSA update moves the allocation before `LoadAll` returns, `Reserve`
  prints a warning but still marks itself usable. The first runtime load would then fail at
  `CheckMeshBudget` (free space would be zero), not corrupt memory.
- Another mod that creates a `DeviceMeshInterleaved` from its own `[StarMapAllModsLoaded]` after
  Parts Now has reserved gets an offset beyond the headroom. The rewind and the budget math would
  then count those bytes as free, so a large runtime load could overwrite that mesh (inferred from
  the code, not observed; no unscience sub-mod does this, and rocky-mcrock-face builds
  non-interleaved meshes).
- `Shared.Rebuild()` (called from `Program.RebuildRenderer`, L5165) re-creates the buffers only when
  the IVA ray-tracing setting no longer matches the buffer usage flags. When it does, `BuildBuffers`
  sizes the new buffers from the current cursor, so the unused headroom is gone for the rest of the
  session (inferred from the decomp, not tested). The next load then fails cleanly at
  `CheckMeshBudget`. The README already lists ray tracing as untested.
- The leak counter is honest, not a fix. Each reload of a mesh-heavy mod spends its mesh size again.
  Reload budget is roughly headroom divided by mesh size per launch.
- Changing the headroom needs a restart. Neither the buffers nor the cursors can be resized safely
  once bound meshes hold absolute offsets.[^parts-now-readme]

## See also

- [Runtime part loading state machine](./parts-now-runtime-part-loading.md) (where `CheckMeshBudget`, bind and rollback sit)
- [Runtime unload and purge](./parts-now-runtime-unload-purge.md) (leak accounting on unload)
- [Reflection self-test](./parts-now-reflection-self-test.md) (the other startup check behind `CanLoad`)
- [Unscience supermod host](./unscience-supermod-host.md)
- [Parts Now sub-mod](../submods/parts-now.md)
- [Mod overview](../overview.md)
- [Inflate DeviceMeshInterleaved.Shared before the first bind allocates the buffer](../../../howto/rendering/meshes/grow-game-gpu-buffers-for-runtime-content/grow-game-gpu-buffers-for-runtime-content-1.md) - how-to article demonstrating this feature
- [Load a part mod into a running game, one loader state per frame](../../../howto/vehicles-and-parts/parts/load-and-unload-part-mods-at-runtime/load-and-unload-part-mods-at-runtime-1.md) - how-to article demonstrating this feature

[^mesh-budget]: parts-now.lib/Runtime/MeshBudget.cs
[^parts-now-settings]: parts-now.lib/Runtime/PartsNowSettings.cs
[^loader-gpu-states]: parts-now.lib/Runtime/RuntimeModLoaderGpuStates.cs
[^runtime-unloader]: parts-now.lib/Runtime/RuntimeModUnloader.cs
[^purge-steps]: parts-now.lib/Runtime/RuntimeModPurgeSteps.cs
[^status-panel]: parts-now.lib/Ui/StatusPanel.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-mesh-reference]: KSA.MeshReference (decompiled, build 2026.10.10.5554)
[^ksa-device-mesh]: KSA.DeviceMeshInterleaved (decompiled, build 2026.10.10.5554)
[^ksa-mesh-atlas]: KSA.MeshAtlasFileReference (decompiled, build 2026.10.10.5554)
[^starmap-modlibrary-patches]: StarMap.Core/Patches/ModLibraryPatches.cs
[^unscience-mod]: unscience/Mod.cs
[^parts-now-mod]: parts-now/Mod.cs
[^parts-now-readme]: parts-now.lib/README.md (authors' design notes)
