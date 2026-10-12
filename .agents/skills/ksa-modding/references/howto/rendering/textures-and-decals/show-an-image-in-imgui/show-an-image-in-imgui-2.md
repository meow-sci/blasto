---
type: How-To
title: Many changing images with in-place refresh and LRU eviction
description: Upload CPU RGBA images as ImGui textures, refresh same-size images in place, and evict least recently used textures under the shared descriptor pool cap
category: rendering
group: textures-and-decals
topic: show-an-image-in-imgui
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/purrtty/features/kitty-graphics-as-imgui-vulkan-textures]
techniques: [vulkan-texture]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, render:imgui]
game_types:
  - RenderCore.SimpleVkTexture
  - Brutal.TextureApi.TextureLoader
  - KSA.ImGuiBackendVulkanImpl
  - RenderCore.TextureAsset
tags: [imgui, image, texture, lru, eviction, video, descriptor-pool]
---

Outcome: a window that shows many images, some of which change every frame (kitty graphics protocol images in a terminal, video frames), without exhausting the game's shared ImGui descriptor pool and without destroying an image a frame in flight still samples. The recipe is a small texture cache:
create and register on first sight, refresh same-size images in place, evict least-recently-used entries under a cap, and remove the ImGui descriptor immediately while deferring the image destruction. Demonstrated by purrTTY ([Kitty graphics images as ImGui Vulkan textures](../../../../mods/purrtty/features/kitty-graphics-as-imgui-vulkan-textures.md)). Prerequisites: StarMap; read approach 1 first.

## Background: how the game does it

`ImGuiBackendVulkanImpl.AddTexture` / `RemoveTexture` [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L1119-L1131) allocate and free descriptor sets from the backend's pool under a lock; the pool is created with 1000 sets for the whole game [current/decomp/KSA/ImGuiBackend.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackend.cs#L15). `SimpleVkTexture.UploadData(stagingPool, commandBuffer, rawData, mipSizes)`
[current/decomp/RenderCore/SimpleVkTexture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/SimpleVkTexture.cs#L374) stages pixels and records the copy including layout transitions. The ImGui pass samples the registered image at `render:imgui`, so any frame submitted before a destroy may still read it.

## Lifecycle and timing

- Upload, registration and drawing happen in `frame:draw-ui` (`[StarMapAfterGui]` -> controller -> `TerminalWindow.Render`), on the main thread. The upload blocks on its own fence (`Submit().Wait()`), a few hundred microseconds for a video-sized frame, and only when an image changed.
- The texture is sampled later the same frame when KSA records ImGui draw data (`render:imgui`). A same-size re-upload is ordered after earlier sampling by the upload's own pipeline barrier on the same queue.
- Eviction removes the descriptor at once (no new draw can use it) but defers destroying the image three frames, covering frames in flight. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Create** a `SimpleVkTexture(name, renderer.Allocator, w, h, depth: 1, R8G8B8A8UNorm, mipLevels: 1, flags: TransferDstBit | SampledBit)`, upload, and register with `ImGuiBackend.Vulkan.AddTexture(renderer.LinearSampler, texture.ImageView)`. Release the CPU pixel buffer once both succeeded; on any failure dispose the texture.

   ```csharp
   // purrTTY.Display/Ghostty/ImageTextureCache.cs L154-L183
   var texture = new SimpleVkTexture(
       $"purrtty-kitty-{img.ImageId}",
       renderer.Allocator,
       img.Width,
       img.Height,
       depth: 1,
       VkFormat.R8G8B8A8UNorm,
       mipLevels: 1,
       flags: VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.SampledBit);

   try
   {
       UploadPixels(renderer, texture, img);

       var texRef = ImGuiBackend.Vulkan.AddTexture(renderer.LinearSampler, texture.ImageView);
       // GPU upload and ImGui registration both succeeded — release the CPU buffer now.
       img.ClearPixelData();

       var entry = new Entry
       {
           Texture = texture,
           TexRef = texRef,
           ContentVersion = img.ContentVersion,
           Width = img.Width,
           Height = img.Height,
       };
       var node = _lruOrder.AddLast(img.ImageId);
       _entries[img.ImageId] = (entry, node);
       _estimatedVramBytes += (long)img.Width * img.Height * 4;
   }
   ```

2. **Upload through a one-shot staging pool** and wait:

   ```csharp
   // purrTTY.Display/Ghostty/ImageTextureCache.cs L197-L205
   private static void UploadPixels(Renderer renderer, SimpleVkTexture texture, TerminalImage img)
   {
       using var pool = renderer.Allocator.CreateStagingPool(renderer.Graphics, 1);
       var cmd = pool.NextCommandBuffer();
       cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
       texture.UploadData(pool, cmd, img.Rgba, new[] { img.Rgba.Length });
       cmd.End();
       pool.Submit().Wait();
   }
   ```

3. **Refresh in place when the geometry is unchanged** (the video case: one fixed id re-transmitted per frame): upload into the existing texture, no new `VkImage`, no descriptor churn on the shared pool. Detect changes by content hash or version, not payload length.

4. **Draw** with `ImDrawListPtr.AddImage(texRef, min, max, null, null, tint)` inside a `PushClipRect`, sized to the span the engine reserved; tint alpha carries the foreground opacity.

5. **Evict LRU** when the entry count exceeds the cap (default 32, tunable 4 to 256): `RemoveTexture` now, queue the image for destruction `DeferredDeleteFrames` (3) later, and drain the queue at the start of each frame.

   ```csharp
   // purrTTY.Display/Ghostty/ImageTextureCache.cs L228-L240
   private void Evict(int imageId, (Entry Entry, LinkedListNode<int> Node) bucket)
   {
       _entries.Remove(imageId);
       _lruOrder.Remove(bucket.Node);
       _estimatedVramBytes = Math.Max(0, _estimatedVramBytes - (long)bucket.Entry.Width * bucket.Entry.Height * 4);

       // Release the ImGui descriptor slot immediately (prevents new draw calls from
       // referencing this texture). The Vulkan image itself is deferred: in-flight
       // commands from prior frames may still be sampling it via the old descriptor.
       try { ImGuiBackend.Vulkan.RemoveTexture(bucket.Entry.TexRef); } catch { }

       _pendingDelete.Enqueue((bucket.Entry, _frame + DeferredDeleteFrames));
   }
   ```

## Complete example

| File | Role |
|---|---|
| [purrTTY.Display/Ghostty/ImageTextureCache.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/ImageTextureCache.cs) | The cache: upload, in-place refresh, LRU, deferred delete |

## Variations and gotchas

- **Main thread only:** create, upload, `AddTexture` and `RemoveTexture`.
- **Remove the ImGui texture before disposing the image,** and defer the dispose until frames in flight are done (see [free mod-owned Vulkan resources safely](../../vulkan-passes/free-vulkan-resources-safely/overview.md)).
- **The descriptor pool is shared by the whole game (1000 sets);** one texture per frame or per image without a cap exhausts it.
- **Decode costs** are skipped for background tabs in purrTTY (`DecodeKittyImages = false`).
- Known gaps in purrTTY's implementation: virtual (Unicode placeholder) placements, animation, pixel-exact non-cell sizing and source-rect crop.

## See also

- [Overview](overview.md)
- [Approach 1: one static PNG](show-an-image-in-imgui-1.md)
- [Kitty graphics images as ImGui Vulkan textures](../../../../mods/purrtty/features/kitty-graphics-as-imgui-vulkan-textures.md)
- [Upload an image or procedural pixels as a GPU texture](../upload-an-image-as-a-vulkan-texture/overview.md), [Free mod-owned Vulkan resources without device loss](../../vulkan-passes/free-vulkan-resources-safely/overview.md), [Record and submit your own Vulkan work: offscreen renders and one-shot uploads](../../vulkan-passes/submit-your-own-vulkan-commands/overview.md)
