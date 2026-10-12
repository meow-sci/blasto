---
type: How-To
title: Procedural ASCII pixel art to an R8G8B8A8 texture with a staging upload
description: Generate pixels in code, create a SimpleVkTexture, upload through a one-buffer staging pool and create a nearest-neighbour clamp sampler for crisp pixel art
category: rendering
group: textures-and-decals
topic: upload-an-image-as-a-vulkan-texture
approach: 3
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/thug-life-procedural-texture]
techniques: [vulkan-texture]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types:
  - RenderCore.SimpleVkTexture
  - Brutal.TextureApi.TextureLoader
  - KSA.BindlessTextures
  - RenderCore.Systems.BindlessTextureLibrary
tags: [texture, procedural, pixel-art, staging, sampler, nearest]
---

Outcome: a texture with no file behind it. Here a 26 by 5 grid of characters in source code becomes an RGBA texture on the GPU the first time it is needed, with a nearest-neighbour sampler so the blocky look survives any size. The staging-upload steps are shown without any decoder.
Demonstrated by unscience Thug Life ([Upload a procedural pixel-art texture and cut it out with geometry](../../../../mods/unscience/features/thug-life-procedural-texture.md)). Prerequisites: StarMap. The quad that shows it is in [draw a textured quad in the world](../draw-a-textured-quad-in-the-world/overview.md) (approach 1).

## Background: how the game does it

`SimpleVkTexture(string name, IImageAllocator allocator, int width, int height, int depth, VkFormat format, int mipLevels, ...)` [current/decomp/RenderCore/SimpleVkTexture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/SimpleVkTexture.cs#L170) creates the image and view; `VkUtils.UploadBufferToImage` transitions the image for the copy, copies each mip and transitions it to `ShaderReadOnlyOptimal`, the layout a descriptor write expects.
The stock unlit fragment shader already applies `gammaToLinear()` to the sampled colour, so an sRGB format would decode twice: use `R8G8B8A8UNorm`.

## Lifecycle and timing

The upload runs on the main thread inside the ImGui button handler that adds the first entry (`frame:draw-ui`), not at mod load: at `[StarMapAllModsLoaded]` the render targets the pipeline needs do not exist yet. Both uploads use `Submit().Wait()`, so that frame blocks until the GPU finishes the copies; the texture is in
`ShaderReadOnlyOptimal` before any draw records against it. After that nothing is uploaded again. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Define the image** (here characters: `#` black, `W` white, `.` transparent) and turn it into four bytes per texel.

2. **Create the texture** with `TransferDstBit | SampledBit` usage, no mips, then upload through a one-buffer staging pool on the graphics queue: map a staging buffer, copy the bytes, record `UploadBufferToImage`, submit and wait.

   ```csharp
   // thug-life.lib/ThugLifeTextureFactory.cs L33-L67
       Texture = new SimpleVkTexture(
           "thug-life",
           renderer.Allocator,
           ThugLifeTexturePattern.Width,
           ThugLifeTexturePattern.Height,
           depth: 1,
           VkFormat.R8G8B8A8UNorm,
           mipLevels: 1,
           arrayLayers: 1,
           cubeMap: false,
           flags: VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.SampledBit);

       UploadPixels(renderer);
       Sampler = CreateSampler(_device);
   }

   private unsafe void UploadPixels(Renderer renderer)
   {
       byte[] pixels = BuildPixelBytes();

       using var stagingPool = renderer.Allocator.CreateStagingPool(renderer.Graphics, 1);
       var cmd = stagingPool.NextCommandBuffer();
       cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);

       var stagingBuffer = stagingPool.AddStagingBuffer(ByteSize.Of<byte>(pixels.Length));
       using (var mapped = stagingBuffer.Map())
           pixels.AsSpan().CopyTo(mapped.AsSpan());

       Span<int> mipSizes = stackalloc int[1];
       mipSizes[0] = pixels.Length;
       VkBuffer src = stagingBuffer.VkBuffer;
       VkUtils.UploadBufferToImage(cmd, in src, Texture.ImageEx.AllocationInfo, mipSizes);

       cmd.End();
       stagingPool.Submit().Wait();
   ```

3. **Create a pixel-art sampler:** nearest filters and mip mode, clamp to edge, `MaxLod = 0`.

   ```csharp
   // thug-life.lib/ThugLifeTextureFactory.cs L110-L125
   private static unsafe VkSampler CreateSampler(DeviceEx device)
   {
       var info = new VkSamplerCreateInfo
       {
           MagFilter = VkFilter.Nearest,
           MinFilter = VkFilter.Nearest,
           MipmapMode = VkSamplerMipmapMode.Nearest,
           AddressModeU = VkSamplerAddressMode.ClampToEdge,
           AddressModeV = VkSamplerAddressMode.ClampToEdge,
           AddressModeW = VkSamplerAddressMode.ClampToEdge,
           MinLod = 0f,
           MaxLod = 0f,
           BorderColor = VkBorderColor.FloatTransparentBlack,
       };
       return device.CreateSampler(info, null);
   }
   ```

4. **Register it** where it is sampled: a descriptor set for your own pipeline, `ImGuiBackend.Vulkan.AddTexture(sampler, view)` for ImGui, or a bindless slot (approaches 1 and 2).

5. **Destroy** the sampler first, then the texture, only when the sub-mod is disposed and after the device is idle.

## Complete example

| File | Role |
|---|---|
| [thug-life.lib/ThugLifeTexturePattern.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/ThugLifeTexturePattern.cs#L21-L31) | The image as a character grid |
| [thug-life.lib/ThugLifeTextureFactory.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/ThugLifeTextureFactory.cs) | Texture, upload, sampler |

## Variations and gotchas

- **`Submit().Wait()` stalls the frame:** fine for a few hundred bytes from a button click; for anything larger or repeated record into a command buffer you control ([submit your own Vulkan commands](../../vulkan-passes/submit-your-own-vulkan-commands/overview.md)).
- **Transparency is geometry here:** the stock unlit fragment writes alpha 1, so the quad is built from one rectangle per opaque texel (capped at 16,384 texels by `ushort` indices); to use a real alpha texture you need your own fragment shader.
- **Inset UVs** by 0.001 of a texel so nearest sampling always hits that texel.
- The README still describes an older smaller grid; the pattern's own comment and the code are authoritative.

## See also

- [Overview](overview.md)
- [Approach 1: decode and re-point an existing slot](upload-an-image-as-a-vulkan-texture-1.md)
- [Approach 2: library textures in the bindless table](upload-an-image-as-a-vulkan-texture-2.md)
- [Upload a procedural pixel-art texture and cut it out with geometry](../../../../mods/unscience/features/thug-life-procedural-texture.md)
- [Draw a textured quad attached to a part or the camera](../draw-a-textured-quad-in-the-world/overview.md), [Show a PNG or runtime-generated image in an ImGui window](../show-an-image-in-imgui/overview.md)
