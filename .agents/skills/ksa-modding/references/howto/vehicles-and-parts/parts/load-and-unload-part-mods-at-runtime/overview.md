---
type: Game Feature
title: Loading, validating and unloading part mods in a running game
description: How KSA's boot asset pipeline can be replayed for one mod after startup, what it takes to roll back or purge it, and the validation rules that stop the game's silent failures
category: vehicles-and-parts
group: parts
topic: load-and-unload-part-mods-at-runtime
demonstrated_by:
  - mods/unscience/features/parts-now-runtime-part-loading
  - mods/unscience/features/parts-now-mesh-headroom-reservation
  - mods/unscience/features/parts-now-paste-xml-mod-writer
  - mods/unscience/features/parts-now-runtime-unload-purge
  - mods/unscience/features/parts-now-bundle-validation
lifecycle: [startup:all-mods-loaded, frame:draw-ui, thread:background]
game_types:
  - KSA.ModLibrary
  - KSA.AssetBundle
  - KSA.DeviceMeshInterleaved
  - KSA.Mod
  - KSA.PartTemplate
  - KSA.MeshReference
  - KSA.SubPartTemplate
  - KSA.FileReference
  - KSA.SerializedCollection
  - KSA.XmlHelper
  - KSA.ILoader
  - KSA.IBinder
tags: [runtime-loading, mods, unload, validation, gpu, mesh-buffer, registries]
---
KSA loads content once, at startup, and never unloads it: its registries only grow, the shared mesh buffers are
sized to exactly what the boot mods needed, and bad XML fails quietly. unscience's Parts Now submod lets a player
add, re-read or remove a part mod while the game is running. It does it by replaying the boot pipeline for one mod
at a time as a state machine, reserving mesh-buffer headroom in advance, validating the XML first, and unwinding in
a careful order.

## Engine background

**The boot pipeline** (what Parts Now replays), from `Program`: `ModLibrary.LoadEditorTags()`,
`ModLibrary.LoadAll()` (`Mod.LoadAssetBundles` -> `XmlLoader.Load<AssetBundle>` -> `AssetBundle.OnDataLoad` ->
`FileReference.OnDataLoad` registers an `ILoader`; then all loaders run in `Parallel.ForEachAsync`),
`ModLibrary.Bind(renderer)` (every `IBinder` in parallel, each with a staging pool) and
`ModLibrary.AttachGameData()` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L935-L990)[^ksa-program] [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L478-L522)[^ksa-modlibrary]
[current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1849-L1861) [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1863-L1876).

**Registries.** `ModLibrary.AllParts`, `AllMeshes`, `AllFiles`, `AllMaterials`, `AllPartGameDataReferences` and
`AllEditorTagDefinitions` are `internal static` `SerializedCollection<T>`s [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L68-L146);
`Loaders` and `Binders` are public lists [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L156-L158). `Register` returns false for an existing
key, and callers then treat the declaration as a reference: a duplicate id is silently dropped, and a duplicate
file is never read [current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L20-L35)[^ksa-serializedcollection] [current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L67-L73)[^ksa-filereference]. A public
`Deregister(item)` exists but swap-removes (it moves the last element into the freed slot)
[current/decomp/KSA/SerializedCollection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedCollection.cs#L37-L55).

**Silent failures.** `FileReference.Load` catches and logs its own exceptions [current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L67-L110);
`XmlLoader` logs a malformed document and returns `null` [current/decomp/KSA/XmlLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlLoader.cs#L20-L33)[^ksa-xmlloader].

**The shared mesh buffers.** `DeviceMeshInterleaved.Shared` holds two `uint` bump cursors
(`RunningVertexBufferSize`, `RunningIndexBufferSize`); `Build()` allocates both buffers once, sized to the cursors;
meshes created after that take offsets past the end of the allocation [current/decomp/KSA/DeviceMeshInterleaved.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DeviceMeshInterleaved.cs#L15-L60)[^ksa-devicemeshinterleaved].

**Threading.** `FileReference.Load` calls `Loading.Task(...)`, whose frame pump returns immediately when it is not
on the main thread; GPU binders must stay on the game thread [current/decomp/KSA/Loading.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Loading.cs#L93)[^ksa-loading].

## Approaches

| Article | Subject | Technique |
|---|---|---|
| [1: one loader state per frame from the pre-GUI hook](load-and-unload-part-mods-at-runtime-1.md) | the load state machine, mesh headroom, writing the mod folder | `reflection` |
| [2: fail-closed gate and ordered purge](load-and-unload-part-mods-at-runtime-2.md) | unloading, reloading and rollback | `reflection` |
| [3: fifteen validation rules](load-and-unload-part-mods-at-runtime-3.md) | validating XML against the game's silent failure modes | `xml-serializer` |

## See also

- [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) (startup sequence and `StarMapAllModsLoaded`)
- [Define a part with its own game data and GLB model](../define-a-part-with-game-data/overview.md), [Change a shipped part without breaking saves](../evolve-a-shipped-part-without-breaking-saves/overview.md)
- Mod features: [unscience/parts-now-runtime-part-loading](../../../../mods/unscience/features/parts-now-runtime-part-loading.md), [unscience/parts-now-mesh-headroom-reservation](../../../../mods/unscience/features/parts-now-mesh-headroom-reservation.md), [unscience/parts-now-paste-xml-mod-writer](../../../../mods/unscience/features/parts-now-paste-xml-mod-writer.md), [unscience/parts-now-runtime-unload-purge](../../../../mods/unscience/features/parts-now-runtime-unload-purge.md), [unscience/parts-now-bundle-validation](../../../../mods/unscience/features/parts-now-bundle-validation.md)

[^ksa-loading]: current/decomp/KSA/Loading.cs (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: current/decomp/KSA/ModLibrary.cs (decompiled, build 2026.10.10.5554)
[^ksa-program]: current/decomp/KSA/Program.cs (decompiled, build 2026.10.10.5554)
[^ksa-devicemeshinterleaved]: current/decomp/KSA/DeviceMeshInterleaved.cs (decompiled, build 2026.10.10.5554)
[^ksa-xmlloader]: current/decomp/KSA/XmlLoader.cs (decompiled, build 2026.10.10.5554)
[^ksa-serializedcollection]: current/decomp/KSA/SerializedCollection.cs (decompiled, build 2026.10.10.5554)
[^ksa-filereference]: current/decomp/KSA/FileReference.cs (decompiled, build 2026.10.10.5554)
