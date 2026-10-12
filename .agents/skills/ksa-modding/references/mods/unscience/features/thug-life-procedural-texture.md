---
type: Mod Feature
title: Upload a procedural pixel-art texture and cut it out with geometry
description: Thug Life builds its sunglasses image from an ASCII grid into an R8G8B8A8 SimpleVkTexture with a staging upload and a nearest-neighbour sampler, and emits one small quad per opaque texel because the stock unlit shader always writes alpha 1
mod: unscience
submod: thug-life
category: rendering
techniques: [vulkan-texture, vulkan-buffer]
hooks:
  - "starmap StarMapAfterGui (via unscience ISubmod.RenderContent: first Add Sunglasses builds the texture)"
lifecycle: [frame:draw-ui]
game_types:
  - RenderCore.SimpleVkTexture
  - RenderCore.VkUtils
  - Brutal.VulkanApi.Abstractions.StagingPool
  - KSA.Program
tags: [vulkan, texture, sampler, staging-buffer, pixel-art, geometry]
---

The sunglasses image is not a file the mod loads. It is a 26 x 5 grid of characters in source code
(`#` black, `W` white glare, `.` transparent), turned into an RGBA texture on the GPU the first time
the player adds a pair. Transparency does not come from alpha blending: the quad is built from one
small rectangle per non-transparent texel, so the gaps between the lenses simply have no
geometry.[^tl-pattern][^tl-quad] The original PNG ships in the repo only as the design
reference.[^tl-png]

Mechanism in one line: bytes from the ASCII grid -> `SimpleVkTexture(R8G8B8A8UNorm)` -> staging
buffer -> `VkUtils.UploadBufferToImage` -> nearest/clamp `VkSampler`, plus a vertex/index buffer with
one quad per opaque texel.

## How it works

**Texture.** `ThugLifeTextureFactory` creates
`new SimpleVkTexture("thug-life", renderer.Allocator, 26, 5, depth: 1, VkFormat.R8G8B8A8UNorm,
mipLevels: 1, arrayLayers: 1, cubeMap: false, flags: TransferDst | Sampled)`. `BuildPixelBytes`
writes 4 bytes per texel from the pattern. The upload creates a one-buffer staging pool on the
graphics queue, maps a staging buffer, copies the bytes, records
`VkUtils.UploadBufferToImage(cmd, stagingBuffer, Texture.ImageEx.AllocationInfo, mipSizes)` and
submits with `Submit().Wait()`.[^tl-texture] `UploadBufferToImage` transitions the image for the
copy, copies each mip and transitions it to `ShaderReadOnlyOptimal` for fragment-shader reads, which
is the layout the descriptor write expects.[^rc-vkutils][^rc-simplevktexture][^brutal-stagingpool]

**Sampler.** `VkFilter.Nearest` for min and mag, nearest mip mode, `ClampToEdge` on all axes and
`MaxLod = 0`, so the blocky look survives any quad size.

**Format choice.** The class comment explains the UNorm format: `UnlitMesh.frag` already applies
`gammaToLinear()` to the sampled color, so an sRGB format would decode twice. With only pure black
and pure white texels the difference is invisible here, but it matters for any reuse with real
colors.[^ksa-unlit-frag]

**Cut-out geometry.** `UnlitMesh.frag` writes `vec4(sampledColor, 1.0)`, discarding texture alpha,
so alpha blending cannot hide transparent texels.[^ksa-unlit-frag] `ThugLifeQuadRenderer.BuildGeometry`
therefore walks the same pattern and, for every non-`.` texel, emits four vertices in a unit square
centred on the origin (rows flipped so row 0 is the top) and six indices. Each quad's UVs are inset by
0.001 of a texel so nearest sampling always hits that texel. The buffers are device-local and filled
with `VkUtils.StageAndUploadToBuffer` through another staging pool.[^tl-quad]

**Lifetime.** Both the texture and the geometry are created inside `EnsureGpuResources`, on the first
entry, and destroyed only when the sub-mod is disposed (sampler first, then the texture).[^tl-manager]

## Lifecycle and timing

The upload runs on the main thread inside the ImGui button handler that adds the first entry
(`frame:draw-ui`), not at mod load: at `[StarMapAllModsLoaded]` the offscreen render target the
pipeline needs does not exist yet (see
[the render pass](./thug-life-textured-quad-render-pass.md#lifecycle-and-timing)). Both uploads use
`Submit().Wait()`, so that one frame blocks until the GPU finishes the copies; the texture is in
`ShaderReadOnlyOptimal` before any draw records against it. After that nothing is uploaded again.

## Key code

| Path | Role |
|---|---|
| [thug-life.lib/ThugLifeTexturePattern.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/ThugLifeTexturePattern.cs) | The 26 x 5 ASCII image |
| [thug-life.lib/ThugLifeTextureFactory.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/ThugLifeTextureFactory.cs) | Texture creation, staging upload, sampler, disposal |
| [thug-life.lib/ThugLifeQuadRenderer.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/ThugLifeQuadRenderer.cs#L163-L235) | Per-texel cut-out geometry and buffer upload |
| [thug-life.lib/ThugLifeRenderManager.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/thug-life.lib/ThugLifeRenderManager.cs#L91-L115) | Lazy creation on first entry |

## Snippets

The image:

```csharp
// thug-life.lib/ThugLifeTexturePattern.cs L21-L31
    public const int Width = 26;
    public const int Height = 5;

    public static readonly string[] Rows = new[]
    {
        "##########################",  // row 0 — solid lens top
        "##W#W#######..##W#W#######",  // row 1 — glare row 1
        ".##W#W######..###W#W#####.",  // row 2 — glare row 2 (stepped)
        "..##W#W####....###W#W###..",  // row 3 — glare row 3 (more stepped)
        "...#######......#######...",  // row 4 — stepped bottom
    };
```

Texture creation and staging upload:

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
// ...
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

Pixel-art sampler:

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

One quad per opaque texel:

```csharp
// thug-life.lib/ThugLifeQuadRenderer.cs L175-L206
        for (int row = 0; row < h; row++)
        {
            string rowStr = ThugLifeTexturePattern.Rows[row];
            for (int col = 0; col < w; col++)
            {
                if (rowStr[col] == '.') continue; // transparent — skip

                float x0 = -0.5f + (float)col / w;
                float x1 = -0.5f + (float)(col + 1) / w;
                // Flip Y so pattern row 0 maps to the top of the quad (+Y) and row N-1 to the bottom.
                float y1 = 0.5f - (float)row / h;
                float y0 = 0.5f - (float)(row + 1) / h;

                float u0 = ((float)col + uvInset) / w;
                float u1 = ((float)(col + 1) - uvInset) / w;
                float v0 = ((float)row + uvInset) / h;
                float v1 = ((float)(row + 1) - uvInset) / h;

                ushort baseIdx = (ushort)verts.Count;
                verts.Add(new QuadVertex { Pos = new float3(x0, y0, 0f), Uv = new float2(u0, v1) });
                verts.Add(new QuadVertex { Pos = new float3(x1, y0, 0f), Uv = new float2(u1, v1) });
                verts.Add(new QuadVertex { Pos = new float3(x1, y1, 0f), Uv = new float2(u1, v0) });
                verts.Add(new QuadVertex { Pos = new float3(x0, y1, 0f), Uv = new float2(u0, v0) });

                indices.Add((ushort)(baseIdx + 0));
                indices.Add((ushort)(baseIdx + 1));
                indices.Add((ushort)(baseIdx + 2));
                indices.Add((ushort)(baseIdx + 0));
                indices.Add((ushort)(baseIdx + 2));
                indices.Add((ushort)(baseIdx + 3));
            }
        }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `SimpleVkTexture(string, IImageAllocator, int, int, int, VkFormat, int, int, bool, VkImageUsageFlags, ...)` | [current/decomp/RenderCore/SimpleVkTexture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/SimpleVkTexture.cs#L170) | Image, memory and view in one object; `ImageEx`, `ImageView` |
| `VkUtils.UploadBufferToImage(CommandBuffer, in VkBuffer, in ImageAllocationInfo, in Span<int>, int)` | [current/decomp/RenderCore/VkUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/VkUtils.cs#L190-L262) | Barrier, copy per mip, barrier to `ShaderReadOnlyOptimal` |
| `VkUtils.StageAndUploadToBuffer<T>` | [current/decomp/RenderCore/VkUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/VkUtils.cs#L101) | Vertex and index buffer upload |
| `StagingPool.NextCommandBuffer` / `AddStagingBuffer` / `Submit` | [current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs#L66-L115) | One-shot upload command buffer |
| `UnlitMesh.frag` | [current/Content/Core/Shaders/Mesh/UnlitMesh.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/UnlitMesh.frag) | `gammaToLinear` on the texel, alpha forced to 1 |

## Pitfalls and version notes

- The thug-life README still describes a 15 x 4 texture; the code and the pattern's own doc comment
  are 26 x 5.[^tl-readme]
- `Submit().Wait()` stalls the frame on the GPU. That is fine for a one-time, few-hundred-byte upload
  from a button click; for anything larger or repeated, record into the frame's command buffer
  instead.
- Indices are `ushort`; the geometry approach caps out at 16,384 opaque texels (65,536 vertices).
  The 5438 upgrade only had to update the quad index binding.[^tl-readme]
- To reuse the pipeline with a real alpha texture you would need your own fragment shader that keeps
  alpha (or discards); the stock unlit shader cannot.
- Rev 5543 changed `SimpleVkTexture` error logging for invalid `TextureAsset`s; the raw-pixel
  constructor used here is unaffected.

## See also

- [Textured quad render pass](./thug-life-textured-quad-render-pass.md)
- [Doh runtime GPU materials](./doh-runtime-gpu-materials.md) (other runtime GPU resource creation)
- [Graffiti bindless decal textures](./graffiti-bindless-decal-textures.md)
- [Thug Life sub-mod](../submods/thug-life.md)
- [Mod overview](../overview.md)
- [Opaque pixel quads with stock UnlitMesh shaders inside the offscreen scene pass](../../../howto/rendering/textures-and-decals/draw-a-textured-quad-in-the-world/draw-a-textured-quad-in-the-world-1.md) - how-to article demonstrating this feature
- [Procedural ASCII pixel art to an R8G8B8A8 texture with a staging upload](../../../howto/rendering/textures-and-decals/upload-an-image-as-a-vulkan-texture/upload-an-image-as-a-vulkan-texture-3.md) - how-to article demonstrating this feature

[^tl-pattern]: thug-life.lib/ThugLifeTexturePattern.cs
[^tl-texture]: thug-life.lib/ThugLifeTextureFactory.cs
[^tl-quad]: thug-life.lib/ThugLifeQuadRenderer.cs
[^tl-manager]: thug-life.lib/ThugLifeRenderManager.cs
[^tl-readme]: thug-life/README.md
[^tl-png]: thug-life.lib/img/thuglife.png (reference image, not loaded at runtime)
[^rc-simplevktexture]: RenderCore.SimpleVkTexture (decompiled, build 2026.10.10.5554)
[^rc-vkutils]: RenderCore.VkUtils (decompiled, build 2026.10.10.5554)
[^brutal-stagingpool]: Brutal.VulkanApi.Abstractions.StagingPool (decompiled, build 2026.10.10.5554)
[^ksa-unlit-frag]: Core/Shaders/Mesh/UnlitMesh.frag (game shader, build 2026.10.10.5554)
