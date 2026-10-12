---
type: Game Feature
title: Reserve or grow game GPU buffers for content added at runtime
description: The game sizes its shared mesh buffer and star instance buffer at load, so runtime meshes or more stars need headroom reserved before the buffer exists or the buffer re-created while frames are in flight
category: rendering
group: meshes
topic: grow-game-gpu-buffers-for-runtime-content
demonstrated_by:
  - mods/unscience/features/parts-now-mesh-headroom-reservation
  - mods/ohmystars/features/star-instance-restyle-patch
lifecycle: [startup:starmap-before-main, startup:all-mods-loaded, frame:draw-ui]
game_types:
  - KSA.DeviceMeshInterleaved
  - KSA.ModLibrary
  - KSA.InstancedStarTechnique
  - KSA.InstancedRenderTechnique
  - KSA.Program
tags: [buffers, headroom, bump-allocator, instance-buffer, stars, runtime-loading]
---
KSA allocates several GPU buffers once, from sizes it measures while loading content. Anything a mod adds after that point either overruns the allocation or is silently dropped. Two cases matter in practice:

- **Part and subpart meshes.** All interleaved meshes share one vertex buffer and one index buffer sized from a bump allocator's final cursor. A mod that loads parts while the game runs (a runtime part loader) needs headroom reserved before the buffers are created.
- **Background stars.** The star sprite buffer has a fixed capacity from the star binaries' header counts. A mod that wants more stars, or wants to restyle every star, patches `AddInstance` and re-creates the buffer by reflection.

The two cases are different in kind: the mesh buffer can never be re-allocated (bound meshes hold absolute offsets), so it is grown by inflating the counters before the first bind; the star buffer holds only a CPU-backed copy of its instances, so it can be rebuilt on demand.

## Engine background

- `DeviceMeshInterleaved.Shared` [current/decomp/KSA/DeviceMeshInterleaved.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DeviceMeshInterleaved.cs#L16-L67): public static `RunningVertexBufferSize` and `RunningIndexBufferSize` cursors, a one-shot `Build()` guarded by `IsBuilding` / `IsBuilt`, `VertexAllocation` / `IndexAllocation` as `BufferEx`. Each `new DeviceMeshInterleaved(hostMesh)` bumps the cursors; `Bind()` builds the buffers on first use and copies the mesh to its offset.
- Startup order: the `Program` constructor runs `ModLibrary.LoadAll()` (L942, boot meshes counted), viewports and render targets, then `ModLibrary.Bind(renderer)` (L978) which binds every binder in parallel. `[StarMapAllModsLoaded]` is a postfix on `LoadAll` ([StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L17-L28)).
- `InstancedStarTechnique` [current/decomp/KSA/InstancedStarTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/InstancedStarTechnique.cs#L14-L84) extends `InstancedRenderTechnique<SpriteInstance>`, whose `_maxInstances` and `Instances` are `protected readonly`; `CreateInstanceBuffer` allocates a host-visible mapped vertex buffer. It is created at Program.cs L1049 and filled by `ModLibrary.PopulateStars` at L1053.
- Deferred frees: `KSA.Rendering.RetiredResourceQueue` [current/decomp/KSA.Rendering/RetiredResourceQueue.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RetiredResourceQueue.cs#L8-L63).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: inflate DeviceMeshInterleaved.Shared before the first bind](grow-game-gpu-buffers-for-runtime-content-1.md) | unscience (Parts Now) | Two public static `uint` writes at `StarMapAllModsLoaded`, rewind at the first UI frame | `startup:all-mods-loaded`, `frame:draw-ui` | No Harmony; fixed headroom, restart to change, no free list, depends on startup ordering |
| [2: reflective re-creation of the star instance buffer](grow-game-gpu-buffers-for-runtime-content-2.md) | ohmystars | `harmony-prefix` on `AddInstance` and the constructor, reflection on `readonly` fields and `CreateInstanceBuffer` | `startup:starmap-before-main`, `frame:draw-ui` | Capacity can change at any time; the old buffer should be retired, not disposed, while frames are in flight |

Approach 2 also contains the background-star restyle: a prefix on `InstancedStarTechnique.AddInstance` rewrites each star's size byte and colour, drops adds when the buffer is full, and pairs with the growth step when more stars are wanted.

## See also

- [Draw your own meshes through the engine's mesh renderers](../draw-mod-meshes-with-the-engine-renderers/overview.md)
- [Free mod-owned Vulkan resources without device loss](../../vulkan-passes/free-vulkan-resources-safely/overview.md)
- [Draw lines and labels on the sky](../../overlays/draw-lines-on-the-sky/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
