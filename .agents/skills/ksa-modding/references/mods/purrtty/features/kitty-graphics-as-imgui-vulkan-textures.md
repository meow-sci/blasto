---
type: Mod Feature
title: Kitty graphics images as ImGui Vulkan textures
description: Upload images decoded from the kitty graphics protocol into SimpleVkTexture objects, register them with KSA's ImGui Vulkan backend as ImTextureRef, and draw them in grid-aligned z-bands with an LRU cache under the shared descriptor pool
mod: purrtty
category: rendering
techniques: [vulkan-texture, imgui-drawlist, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, render:imgui]
game_types:
  - RenderCore.SimpleVkTexture
  - KSA.ImGuiBackend
  - KSA.ImGuiBackendVulkanImpl
  - Core.Renderer
  - KSA.Program
  - Brutal.ImGuiApi.ImDrawListPtr
  - Brutal.ImGuiApi.ImTextureRef
tags: [vulkan, textures, imgui, kitty-graphics, images, lru, terminal]
---

Terminal programs that speak the kitty graphics protocol (image viewers, plotting tools,
terminal-doom, the gatOS screen stream) can show bitmaps inside a purrTTY terminal. The engine
(libghostty-vt) parses and stores images and placements; purrTTY decodes the pixels once, uploads
them into a Vulkan texture through KSA's own texture helper, registers the texture with KSA's
ImGui Vulkan backend, and draws it with `ImDrawListPtr.AddImage` sized to the cell span the engine
reserved. This is the general recipe for showing any CPU-side image inside an ImGui window in KSA.

## How it works

**Backend (CPU, renderer-neutral).** Each tick, `GhosttyTerminalSurface.PopulateImages` walks the
engine's visible placements with the binding's `KittyPlacementCursor` and emits `ImagePlacement`
records (image id, viewport col/row, cell span, z). When an image id is new, or the FNV-1a hash of
its stored payload changed, it decodes the image to RGBA (`KittyImageDecoder`: raw fast path,
zlib via `ZLibStream`, PNG/JPEG via StbImageSharp, 4096x4096 cap) and emits a `TerminalImage` in
`TerminalFrame.NewImages`. Only ids, geometry and packed RGBA cross to the frontend.[^surface][^decoder]

**Frontend (GPU, render thread).** `TerminalWindow.Render` creates an `ImageTextureCache` lazily and
per frame calls `BeginFrame()`, `Upload(frame.NewImages)`, then
`KittyImageRenderer.Draw(..., belowText: true)` before the text grid and `belowText: false` after
it.[^terminal-window]

| Step | Call | Notes |
|---|---|---|
| Create | `new SimpleVkTexture(name, renderer.Allocator, w, h, depth: 1, VkFormat.R8G8B8A8UNorm, mipLevels: 1, flags: TransferDstBit \| SampledBit)` | `renderer = Program.GetRenderer()` |
| Upload | `renderer.Allocator.CreateStagingPool(renderer.Graphics, 1)`, one-shot command buffer, `texture.UploadData(pool, cmd, rgba, ...)`, `pool.Submit().Wait()` | Synchronous; `SimpleVkTexture` handles layout transitions |
| Register | `ImGuiBackend.Vulkan.AddTexture(renderer.LinearSampler, texture.ImageView)` -> `ImTextureRef` | KSA allocates a descriptor set from its ImGui pool; the set handle is the texture id |
| Draw | `drawList.AddImage(texRef, min, max, null, null, tint)` inside `PushClipRect` of the grid | Size = cell span x fractional cell metrics; tint alpha = foreground opacity |
| Update same size | `UploadPixels` into the existing texture | Video case: no new image, no descriptor churn |
| Evict | `ImGuiBackend.Vulkan.RemoveTexture(texRef)` now; `texture.Dispose()` after 3 frames | LRU over `MaxTextures` (default 32, menu-tunable 4-256) |

## Lifecycle and timing

- Upload, registration and drawing happen in `frame:draw-ui` (`[StarMapAfterGui]` -> controller ->
  `TerminalWindow.Render`), on the main thread. The upload blocks on its own fence
  (`Submit().Wait()`), a few hundred microseconds for a video-sized frame per the code comment, and
  only when an image changed.[^texture-cache]
- The texture is sampled later the same frame when KSA records ImGui draw data (`render:imgui`). A
  same-size re-upload is ordered after earlier sampling by the upload's own pipeline barrier on the
  same queue (comment in `UploadOne`).
- Eviction removes the descriptor at once (no new draw can use it) but defers destroying the image
  3 frames, covering frames still in flight.
- Decoding happens earlier in the same tick, inside `Surface.BuildFrame()` on the main thread; the
  hash and decode are skipped for background tabs (`DecodeKittyImages = false`).

## Key code

| Path | Role |
|---|---|
| [purrTTY.Display/Ghostty/ImageTextureCache.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/ImageTextureCache.cs#L119-L205) | Create, upload, register, in-place refresh |
| [purrTTY.Display/Ghostty/ImageTextureCache.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/ImageTextureCache.cs#L207-L263) | LRU eviction, deferred delete, dispose |
| [purrTTY.Display/Ghostty/KittyImageRenderer.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/KittyImageRenderer.cs#L17-L71) | Z-band drawing with `AddImage` and clipping |
| [purrTTY.Display/Ghostty/TerminalWindow.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.cs#L453-L477) | Order: upload, below-text images, grid, above-text images |
| [purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs#L1120-L1260) | Placement scan and content-hash re-decode |
| [purrTTY.Terminal/Ghostty/KittyImageDecoder.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Ghostty/KittyImageDecoder.cs) | zlib/PNG/raw decode to RGBA |
| [vendor/Ghostty.Vt/src/KittyGraphics.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/vendor/Ghostty.Vt/src/KittyGraphics.cs) | `KittyPlacementCursor` binding |

## Snippets

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

What `AddTexture` returns: the descriptor-set handle as the ImGui texture id.

```csharp
// current/decomp/KSA/ImGuiBackendVulkanImpl.cs L1119-L1131
	public ImTextureRef AddTexture(VkSampler sampler, VkImageView imageView, VkImageLayout imageLayout = VkImageLayout.ShaderReadOnlyOptimal)
	{
		VkDescriptorSet vkDescriptorSet = AddTextureImpl(sampler, imageView, imageLayout);
		return new ImTextureRef
		{
			_TexID = (ImTextureID)vkDescriptorSet.VkHandle
		};
	}

	public void RemoveTexture(ImTextureRef texture)
	{
		RemoveTextureImpl(new VkDescriptorSet((nint)texture._TexID));
	}
```

## Game APIs touched

| API | Where | Purpose |
|---|---|---|
| `SimpleVkTexture(string, IImageAllocator, int, int, int, VkFormat, int, ...)`, `UploadData`, `ImageView`, `Dispose` | [current/decomp/RenderCore/SimpleVkTexture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/SimpleVkTexture.cs#L170-L209) | KSA's image + view + upload helper[^ksa-simplevktexture] |
| `ImGuiBackend.Vulkan` | [current/decomp/KSA/ImGuiBackend.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackend.cs#L18-L46) | The game's ImGui renderer instance; its descriptor pool is created with `DescriptorPoolSize = 1000`[^ksa-imgui-backend] |
| `ImGuiBackendVulkanImpl.AddTexture` / `RemoveTexture` | [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L1119-L1131) | Descriptor-set registration[^ksa-imgui-vulkan] |
| `IBufferAllocator.CreateStagingPool(Queue, int)` | [current/decomp/Brutal.VulkanApi.Abstractions/StagingPoolExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/StagingPoolExtensions.cs) | One-shot upload command buffers[^ksa-staging-ext] |
| `Program.GetRenderer()`, `Renderer.LinearSampler`, `Renderer.Allocator`, `Renderer.Graphics` | [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs) | Device context, shared sampler, upload queue[^ksa-renderer] |
| `ImDrawListPtr.AddImage(ImTextureRef, ...)`, `PushClipRect` | [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L611) | Drawing the texture in the window[^ksa-drawlist-ext] |

## Pitfalls and version notes

- Create, upload, `AddTexture` and `RemoveTexture` only on the render (main) thread (gotcha 23).[^gotchas]
- Remove the ImGui texture before disposing the image, and defer the dispose until frames in flight
  are done.
- The ImGui descriptor pool is shared by the whole game (1000 sets); a mod that registers one
  texture per frame or per image without a cap will exhaust it. purrTTY caps at 32 by default.
- Re-transmitted video frames keep the same id and size; refreshing texels in place avoids
  per-frame image and descriptor churn. Detect changes by content hash, not payload length (gotcha 33).
- Known gaps from `plans/KITTY_PLAN.md`: virtual (Unicode placeholder) placements, animation, and
  pixel-exact non-cell sizing and source-rect crop are not implemented.[^kitty-plan]
- The pinned native once corrupted memory on zlib payloads (a zig std bug); the vendored native
  carries a patch (gotcha 34), see [the binding](./vendored-libghostty-vt-binding.md).

## See also

- [Terminal grid drawn with ImGui draw lists](./terminal-grid-drawlist-renderer.md)
- [Headless terminal surface and frame snapshot](./headless-terminal-surface-and-frame-snapshot.md)
- [Vendored libghostty-vt binding](./vendored-libghostty-vt-binding.md)
- [Off-screen ImGui context render-to-texture](./offscreen-imgui-context-render-to-texture.md) (images in in-world terminals)
- [Vulkan resource teardown discipline](./vulkan-resource-teardown-discipline.md)
- [purrTTY overview](../overview.md)
- [Many changing images with in-place refresh and LRU eviction](../../../howto/rendering/textures-and-decals/show-an-image-in-imgui/show-an-image-in-imgui-2.md) - how-to article demonstrating this feature
- [Stop recording now, free two frames later after WaitIdle; nullable images; no device work in unload](../../../howto/rendering/vulkan-passes/free-vulkan-resources-safely/free-vulkan-resources-safely-1.md) - how-to article demonstrating this feature

[^surface]: purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs
[^decoder]: purrTTY.Terminal/Ghostty/KittyImageDecoder.cs
[^terminal-window]: purrTTY.Display/Ghostty/TerminalWindow.cs
[^texture-cache]: purrTTY.Display/Ghostty/ImageTextureCache.cs
[^ksa-simplevktexture]: RenderCore.SimpleVkTexture (decompiled, build 2026.10.10.5554)
[^ksa-imgui-backend]: KSA.ImGuiBackend (decompiled, build 2026.10.10.5554)
[^ksa-imgui-vulkan]: KSA.ImGuiBackendVulkanImpl (decompiled, build 2026.10.10.5554)
[^ksa-staging-ext]: Brutal.VulkanApi.Abstractions.StagingPoolExtensions (decompiled, build 2026.10.10.5554)
[^ksa-renderer]: Core.Renderer (decompiled, build 2026.10.10.5554)
[^ksa-drawlist-ext]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
[^gotchas]: purrTTY docs/gotchas.md
[^kitty-plan]: purrTTY plans/KITTY_PLAN.md
