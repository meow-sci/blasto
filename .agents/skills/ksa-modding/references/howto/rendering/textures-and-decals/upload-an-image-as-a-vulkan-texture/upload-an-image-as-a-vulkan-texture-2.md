---
type: How-To
title: Mip-mapped library textures claimed into Program.BindlessTextures, hot-swapped on rescan
description: Upload each PNG once into a mod-owned mip-mapped image, claim a new bindless slot with AddTexture so shaders need only a uint index, and hot-swap on file change
category: rendering
group: textures-and-decals
topic: upload-an-image-as-a-vulkan-texture
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/graffiti-bindless-decal-textures]
techniques: [vulkan-texture]
hooks: [starmap StarMapBeforeGui, starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, event:load]
game_types:
  - RenderCore.SimpleVkTexture
  - Brutal.TextureApi.TextureLoader
  - KSA.BindlessTextures
  - RenderCore.Systems.BindlessTextureLibrary
tags: [texture, bindless, hot-swap, mips, png, retire-queue]
---

Outcome: a library of PNGs becomes GPU textures that shaders reference with a single `uint` index. Each file is uploaded once into a mod-owned image with a full mip chain and claimed into the game's bindless table; editing a PNG on disk and rescanning swaps the image under every user of it. Demonstrated by unscience Graffiti's decal textures
([PNG decals as mod-owned slots in KSA's bindless texture table](../../../../mods/unscience/features/graffiti-bindless-decal-textures.md)). Prerequisites: StarMap. Approach 1 re-points an existing stock slot; this one allocates new slots.

## Background: how the game does it

All game textures live in one bindless descriptor array. `Program.Instance.BindlessTextures` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L111) is created with 1024 slots; `AddTexture(VkImageView, layout)` [current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs#L186) claims a free index, `FreeTexture(int)` [current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs#L212) rewrites the slot to the library's built-in
white "empty" texture and returns the index to the free list. Shaders sample through `Common/TextureSet.glsl` with `SAMPLE_TEXTURE(texId, 0, uv)`; sampler slot 0 is the linear-clamped sampler with `MaxLod = 1000`, so mips are used. The decode and upload helpers are the same as in approach 1.

## Lifecycle and timing

| Operation | Called from | Lifecycle |
|---|---|---|
| `Resolve` on placement | `PlaceAtCursor` in `RenderFloatingWindows` (`[StarMapAfterGui]`) | `frame:draw-ui` |
| Rescan and reconcile | the Rescan button, and startup | `frame:draw-ui` |
| Restore / reset | the save participant | `event:load` |
| Retire-queue drain | top of `Update` (`[StarMapBeforeGui]` or the hidden-HUD replay) | `frame:draw-ui`, once per frame |

All on the main thread, outside command recording, so a slot claimed this frame is valid when `RenderGame` records the decal pass later the same frame. The upload blocks on its own staging submit: a one-off cost per image. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Key the cache by file name plus last-write time.** Same write time: reuse the slot. Different: release the old slot (hot swap) and upload again. A decode failure is latched per (name, write time), so a broken file is retried only after it changes.

   ```csharp
   // graffiti.lib/DecalTextures.cs L52-L76
   internal int? Resolve(string name, out DecalTextureState state)
   {
       var path = PngLibrary.FullPath(name);
       if (!File.Exists(path))
       {
           state = DecalTextureState.Missing;
           return null;
       }

       var lastWrite = File.GetLastWriteTimeUtc(path);
       if (_bound.TryGetValue(name, out var bound))
       {
           if (bound.LastWriteUtc == lastWrite)
           {
               state = DecalTextureState.Ready;
               return bound.Handle;
           }
           Release(name); // the file changed on disk — hot-swap to the new bytes
       }

       if (_failed.TryGetValue(name, out var failedWrite) && failedWrite == lastWrite)
       {
           state = DecalTextureState.Failed;
           return null;
       }
   ```

2. **Decode and upload** with the game's loaders, capping the longest edge and filling the mip chain; destroy the decoded CPU image explicitly:

   ```csharp
   // graffiti.lib/DecalTextures.cs L175-L194
   private SimpleVkTexture Upload(Core.Renderer renderer, string name, byte[] bytes)
   {
       var decoded = TextureLoader.LoadFromMemory(bytes, TextureLoader.FormatType.Png,
           TextureAsset.LoadOptions(VkFormat.R8G8B8A8UNorm, Brutal.KtxApi.KtxTranscodeFmt.Rgba32));
       try
       {
           // FilePath must be non-empty (the ctor throws otherwise) and names the Vulkan image.
           var asset = new TextureAsset(decoded, $"graffiti:decals/{name}");
           using var pool = renderer.Allocator.CreateStagingPool(renderer.Graphics, 1);
           var image = new SimpleVkTexture(renderer.Allocator, pool, asset,
               new SimpleVkTexture.CreateOptions(MaxDimension,
                   SimpleVkTexture.CreateOptions.ReductionMethod.Downsample, fillMipChain: true));
           pool.Submit().Wait();
           return image;
       }
       finally
       {
           DestroyDecoded(decoded);
       }
   }
   ```

3. **Claim the slot, and give it back if anything after the claim fails,** so the table never points at destroyed memory:

   ```csharp
   // graffiti.lib/DecalTextures.cs L148-L170
   private Bound Bind(Core.Renderer renderer, string name, string path, DateTime lastWrite)
   {
       if (Program.Instance?.BindlessTextures is not { } bindless)
           throw new InvalidOperationException("the bindless texture table is not available yet");

       var image = Upload(renderer, name, File.ReadAllBytes(path));
       var handle = -1;
       try
       {
           handle = bindless.AddTexture(image.ImageView);
           return new Bound(image, handle, lastWrite);
       }
       catch
       {
           // Anything after AddTexture must give the slot back: retiring the image while a
           // claimed slot still points at it would leave the table sampling destroyed memory.
           if (handle >= 0)
               bindless.FreeTexture(handle);
           Retire(image);
           throw;
       }
   }

   ```

4. **Release in the right order:** `FreeTexture(handle)` first (safe at once, the slot now points at the white texture), then queue the image for `MaxFramesInFlight + 1` ticks. Drain the queue first thing each frame. Teardown waits on `GraphicsAndCompute.WaitIdle()` and destroys immediately.

5. **Pass the index** to the shader as a push constant or buffer value (`texId`); `0xFFFFFFFF` can mean "debug checker".

## Complete example

| File | Role |
|---|---|
| [graffiti.lib/DecalTextures.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/DecalTextures.cs) | The cache: resolve, upload, bind, release, drain |
| [graffiti.lib/GraffitiSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/graffiti.lib/GraffitiSubmod.cs) | Callers: place, rescan, restore |

## Variations and gotchas

- **Capacity.** The table has 1024 slots shared with every game texture (clamped down on devices with fewer update-after-bind images). Graffiti holds one slot per distinct PNG in use, not per decal.
- **Free the slot before the image.**
- **Retire counting** counts `Update` calls and assumes one per frame. KSA ships its own frame-stamped `RetiredResourceQueue` in this build ([current/decomp/KSA.Rendering/RetiredResourceQueue.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RetiredResourceQueue.cs#L26)); graffiti predates it.
- **Rescan is manual:** there is no file watcher.
- **Decode buffers leak** unless the texture's `Destroy()` is called.

## See also

- [Overview](overview.md)
- [Approach 1: decode and re-point an existing slot](upload-an-image-as-a-vulkan-texture-1.md)
- [Approach 3: procedural pixel art](upload-an-image-as-a-vulkan-texture-3.md)
- [PNG decals as mod-owned slots in KSA's bindless texture table](../../../../mods/unscience/features/graffiti-bindless-decal-textures.md)
- [Project decals onto vessels, terrain and clutter](../project-decals-onto-scene-geometry/overview.md), [Free mod-owned Vulkan resources without device loss](../../vulkan-passes/free-vulkan-resources-safely/overview.md)
