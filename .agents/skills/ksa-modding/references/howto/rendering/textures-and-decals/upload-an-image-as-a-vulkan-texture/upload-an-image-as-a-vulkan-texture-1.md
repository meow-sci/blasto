---
type: How-To
title: Decode with the game's TextureLoader and re-point an existing bindless slot
description: Decode PNG, JPEG, DDS or KTX bytes with the game's loaders into a SimpleVkTexture and write its image view into a bindless slot the game already samples
category: rendering
group: textures-and-decals
topic: upload-an-image-as-a-vulkan-texture
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/clutter-texture-override]
techniques: [vulkan-texture, bindless-slot-swap]
hooks: [starmap StarMapBeforeGui, harmony-prefix KSA.GameViewport.OnFrame, starmap StarMapAfterOnFrame]
lifecycle: [frame:draw-ui, frame:viewports]
game_types:
  - RenderCore.SimpleVkTexture
  - Brutal.TextureApi.TextureLoader
  - KSA.BindlessTextures
  - RenderCore.Systems.BindlessTextureLibrary
tags: [texture, upload, bindless, textureloader, mips, staging]
---

Outcome: image bytes (PNG, JPEG, BMP, HDR, DDS, KTX, KTX2) become a GPU texture with a generated mip chain, and the texture takes over a bindless slot that the game's shaders already sample, so every material using that slot shows your image. Nothing is written to game asset folders and no
Harmony patch is needed. Demonstrated by gatOS' clutter texture override ([Ground-clutter texture override](../../../../mods/gatos/features/clutter-texture-override.md)). Prerequisites: StarMap. The slot swap and retirement are covered in [replace a game texture at runtime](../replace-a-game-texture-at-runtime/overview.md) and [free mod-owned Vulkan resources safely](../../vulkan-passes/free-vulkan-resources-safely/overview.md).

## Background: how the game does it

`TextureLoader.LoadFromMemory(ReadOnlySpan<byte>, FormatType, params TextureLoadSettings?[])` [current/decomp/Brutal.TextureApi/TextureLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.TextureApi/TextureLoader.cs#L135) decodes by format; `TextureAsset.LoadOptions(VkFormat stbFormat, KtxTranscodeFmt ktxTranscodeFormat)` [current/decomp/RenderCore/TextureAsset.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/TextureAsset.cs#L109) builds the settings, and
`new TextureAsset(ITexture, string filePath)` [current/decomp/RenderCore/TextureAsset.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/TextureAsset.cs#L61) wraps the decoded image (the path must be non-empty; it names the Vulkan image). `SimpleVkTexture(IImageAllocator, StagingPool, TextureAsset, CreateOptions)` [current/decomp/RenderCore/SimpleVkTexture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/SimpleVkTexture.cs#L245) creates the device-local image, view and uploads through the staging pool.
`BindlessTextureLibrary` [current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs) has `AddTexture(VkImageView, layout)`, `SetTexture(int handle, VkImageView, layout)` and `FreeTexture(int handle)`; `Program.Instance.BindlessTextures` is created with 1024 slots [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L830). The set uses `UpdateAfterBind` and `PartiallyBound`, so a slot write between frames is picked up by the next submission.

## Lifecycle and timing

| Step | Where |
|---|---|
| Commands (bind, unbind) update a game-free store | the main-viewport `GameViewport.OnFrame` prefix (`frame:viewports`) |
| Decode, upload, slot write, retire | `[StarMapBeforeGui]` (`frame:draw-ui`) on the main thread, before the scene renders |
| Sampling | later the same frame in `frame:render` |

Uploads block the main thread on their own staging submit; they do not stall the frame's command buffers. With the HUD hidden the tick runs from `[StarMapAfterOnFrame]`, so the slot change first shows in the next frame. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Decode with the game's settings.** `R8G8B8A8UNorm` forces stb to four channels; `Rgba32` is the KTX transcode target. This is the exact settings pair `TextureReference.DoLoad` uses for game assets. Destroy the decoded native buffer explicitly in a `finally` (`ITexture` is not `IDisposable`).

   ```csharp
   // gatOS.GameMod/Game/Ksa/Paint/UserTextureGpu.cs L56-L95
   internal static SimpleVkTexture Upload(
       Core.Renderer renderer, TextureFile file, int maxDimension, bool faithful, string debugNamePrefix)
   {
       var kind = file.Kind switch
       {
           TextureImageKind.Png => TextureLoader.FormatType.Png,
           TextureImageKind.Jpeg => TextureLoader.FormatType.Jpg,
           TextureImageKind.Bmp => TextureLoader.FormatType.Bmp,
           TextureImageKind.Hdr => TextureLoader.FormatType.Hdr,
           TextureImageKind.Dds => TextureLoader.FormatType.Dds,
           TextureImageKind.Ktx => TextureLoader.FormatType.Ktx,
           TextureImageKind.Ktx2 => TextureLoader.FormatType.Ktx2,
           _ => throw new InvalidOperationException($"'{file.Name}' is not a recognised image"),
       };

       // R8G8B8A8UNorm forces stb to 4 channels; Rgba32 is the ktx transcode target. This is the
       // exact settings pair TextureReference.DoLoad uses for the game's own assets.
       var decoded = TextureLoader.LoadFromMemory(file.Bytes, kind,
           TextureAsset.LoadOptions(VkFormat.R8G8B8A8UNorm, Brutal.KtxApi.KtxTranscodeFmt.Rgba32));
       try
       {
           // Inside the guard: MakeFaithful throws by design for any format it cannot rewrite, and
           // the decoded native buffer has to be freed on that path too.
           if (faithful)
               MakeFaithful(decoded, file.Name);

           // FilePath must be non-empty (the ctor throws otherwise) and names the Vulkan image.
           var asset = new TextureAsset(decoded, $"{debugNamePrefix}{file.Name}");
           using var pool = renderer.Allocator.CreateStagingPool(renderer.Graphics, 1);
           var image = new SimpleVkTexture(renderer.Allocator, pool, asset,
               new SimpleVkTexture.CreateOptions(maxDimension,
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

2. **Capture the stock slot first** (its bindless handle and the stock `ImageView`), keeping the first capture across re-binds so restore always returns to stock.

3. **Write the slot:** `Program.Instance.BindlessTextures.SetTexture(handle, image.ImageView)`. Retire the previous override image (do not destroy inline).

   ```csharp
   // gatOS.GameMod/Game/Ksa/Paint/ClutterTextureBridge.cs L219-L244
   private Override Apply(string targetId, TextureFile file, TextureBindMode mode)
   {
       if (Program.GetRenderer() is not { } renderer)
           throw new InvalidOperationException("the renderer is not running yet");
       if (Program.Instance?.BindlessTextures is not { } bindless)
           throw new InvalidOperationException("the bindless texture table is not available yet");
       if (ResolveStock(targetId) is not { } stock)
           throw new InvalidOperationException($"stock texture '{targetId}' is gone");

       var image = UserTextureGpu.Upload(renderer, file, _maxDimension,
           faithful: mode == TextureBindMode.Faithful, "gatos:paint/textures/");

       // Capture the pristine slot BEFORE the first swap. A re-bind over an existing override keeps
       // the original capture, so restore always returns to stock and never to a previous override.
       var stockView = _live.TryGetValue(targetId, out var previous) ? previous.StockView : stock.ImageView;
       var handle = _live.TryGetValue(targetId, out var prior) ? prior.Handle : stock.BindlessHandle;

       bindless.SetTexture(handle, image.ImageView);
       _retire.Retire(previous?.Image);

       var live = new Override(targetId, file.Name, file.Version, handle, stockView, image,
           image.Width, image.Height, image.MipMapCount, UserTextureGpu.VramBytes(image));
       _live[targetId] = live;
       return live;
   }

   ```

4. **Reconcile instead of executing commands:** compare a store revision with the last applied one and return when equal.

5. **Colour fidelity:** clutter diffuse maps are modulation maps, not photographs (alpha 0 means sRGB, alpha 1 linear, RGB mapped with `x*2-1` then adding 1). gatOS' default `faithful` mode rescales RGB so an ordinary sRGB PNG renders as authored; a `raw` token uploads untouched.

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Paint/UserTextureGpu.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/UserTextureGpu.cs) | Decode, upload, retire queue |
| [gatOS.GameMod/Game/Ksa/Paint/ClutterTextureBridge.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/ClutterTextureBridge.cs) | Discovery, slot binding, reconcile |

## Variations and gotchas

- **A slot holds one texture asset:** an override changes every material that uses it.
- **No KSA code writes those slots** in this build besides the mod, so there is no contention.
- **Cap the dimension** (`CreateOptions(maxDimension, ReductionMethod.Downsample, fillMipChain: true)`) and account for VRAM.
- Never destroy a replaced `VkImage` a recorded frame may still sample: retire it by frames in flight.
- In-game validation of this feature is still pending in the repo.

## See also

- [Overview](overview.md)
- [Approach 2: mip-mapped library textures claimed into the bindless table](upload-an-image-as-a-vulkan-texture-2.md)
- [Approach 3: procedural pixel art](upload-an-image-as-a-vulkan-texture-3.md)
- [Ground-clutter texture override](../../../../mods/gatos/features/clutter-texture-override.md)
- [Replace a game texture or model texture with a user PNG at runtime](../replace-a-game-texture-at-runtime/overview.md), [Show a PNG or runtime-generated image in an ImGui window](../show-an-image-in-imgui/overview.md), [Free mod-owned Vulkan resources without device loss](../../vulkan-passes/free-vulkan-resources-safely/overview.md), [Draw a textured quad attached to a part or the camera](../draw-a-textured-quad-in-the-world/overview.md)
