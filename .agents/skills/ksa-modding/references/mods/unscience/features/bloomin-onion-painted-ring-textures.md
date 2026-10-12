---
type: Mod Feature
title: Procedural ring band textures uploaded through the game's own TextureReference.Bind
description: Bloomin' Onion rasterizes stripes and ringlet noise into a 2048x1 RGBA8 band plus a control strip, wraps each in a TextureReference subclass whose private TextureAsset is seeded by reflection, and binds it through the game so it gets a bindless handle like any stock texture
mod: unscience
submod: bloomin-onion
category: rendering
techniques: [reflection, vulkan-texture, imgui-drawlist]
hooks:
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent; Apply builds the textures, the panel draws a preview strip)
  - reflection KSA.TextureReference.<TextureAsset>k__BackingField
lifecycle: [frame:draw-ui, frame:prepare, render:atmosphere]
game_types:
  - KSA.TextureReference
  - RenderCore.TextureAsset
  - Brutal.TextureApi.Abstractions.GenericTexture
  - Brutal.VulkanApi.Abstractions.StagingPool
  - KSA.Rendering.Rings.Rendering.PlanetaryRingsRenderData
  - KSA.PlanetaryRingsReference
tags: [textures, bindless, rings, procedural, reflection, vulkan, imgui]
---

A Bloomin' Onion ring does not need a texture file. The player edits a base color, any number of
colored stripes (start, end, softness, color and opacity) and a ringlet-noise amount, sees a live
preview strip, and on Apply the mod paints the band in memory and hands it to the game as an
ordinary texture: the ring renderer, the planet's ring shadow and the atmosphere's ring shadow all
sample it through the same bindless handle a stock PNG would get.[^ring-band-painter]

Mechanism in one line: paint RGBA8 bytes into a `GenericTexture`, wrap it in a `TextureAsset`,
store that in a `TextureReference` subclass through the auto-property backing field, and call the
game's own `TextureReference.Bind` with a private `StagingPool`.

## How it works

**Painting** ([bloomin-onion.lib/RingBandPainter.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/RingBandPainter.cs)):

- The band is 2048 texels wide and one row high (`RingDefinition.PainterWidth`), the same
  single-row layout as Saturn's `Rings.png`, whose XML comment notes these strips gain nothing from
  block compression.[^ksa-astronomicals-xml]
- `Evaluate(definition, x)` starts from the base color and composites each stripe "over" it with
  a smoothstep-feathered coverage, then multiplies opacity (and slightly darkens color) by two
  octaves of seeded value noise for ringlets. The output is straight, not premultiplied, RGBA.
- `PaintControl` derives the control strip from the band's opacity: R ramps from 0 to 1 over 0.1
  above the "mesh coverage threshold" (where the instanced rock field may draw), G is the opacity
  (volumetric thickness blend), B is 0 and A is 255. This matches the channel meaning documented
  for `SaturnRingsControl` in the game XML.[^ksa-astronomicals-xml]
- The panel's preview calls the same `Evaluate` at 256 samples and fills rectangles on the window
  draw list, so the preview is exactly what will be uploaded.[^bloomin-ui-sections]

**Uploading** ([bloomin-onion.lib/PaintedTextureReference.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/PaintedTextureReference.cs)):

1. `GenericTexture.Defaults.RGBA8UNorm(new int2(2048, 1))` allocates a CPU texture; the painted
   bytes are copied into its `Data` span.[^ksa-generic-texture]
2. A `PaintedTextureReference` (a sealed subclass of `TextureReference`) gets `Id`, `Category`,
   `Width`, `Height` and `SetHash()`.
3. `TextureReference.TextureAsset` has a private setter, so the mod writes
   `<TextureAsset>k__BackingField` with `new TextureAsset(texture, id)`, the in-memory constructor
   of `RenderCore.TextureAsset`.[^ksa-texture-reference][^ksa-texture-asset]
4. `Bind(renderer, stagingPool)` runs the stock path: it creates a `SimpleVkTexture` (with mips, no
   size cap for `TextureCategory.Default`), sets `ImageView`, and registers the view with
   `Program.Instance.BindlessTextures.AddTexture`. The staging pool is disposed right away, and
   `StagingPool.Dispose` submits and waits, so the upload is complete when `Create` returns.[^ksa-staging-pool]
5. A zero `BindlessHandle` is treated as failure.

**Caching and release** ([bloomin-onion.lib/RingTextureFactory.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/RingTextureFactory.cs)):

- Ids are content hashes: `bloomin_onion/band_<fnv1a>` over base color, noise and stripes;
  the control id also mixes in the threshold. Re-applying unchanged paint reuses the upload.
- After every successful rebuild `RingDefinitionController.PruneUnusedAssets` collects the ids of
  all applied painted definitions and calls `PruneExcept`, which calls `Release` (the game's
  `TextureReference.Dispose(Device)`: frees the bindless slot and the `SimpleVkTexture`) on the
  rest.[^ring-definition-controller]
- Painted textures are never registered in `ModLibrary`, so they never appear in asset catalogs;
  `StockRingAssets` also skips any ring whose band is a `PaintedTextureReference`.

**Texture mode guard:** when a player picks an existing texture as the control strip instead,
`RingReferenceBuilder.IsCpuSampleable` accepts it only if its format descriptor is not block
compressed and has 4 bytes per block. The band texture needs no such check because it is only
GPU-sampled.[^ring-reference-builder]

## Lifecycle and timing

| Step | Where | Phase |
|---|---|---|
| Preview strip | Window draw list in `RenderContent` | `frame:draw-ui` |
| Paint and upload | `RingTextureFactory.GetBand` / `GetControl`, from `RingReferenceBuilder.Build` inside Apply | `frame:draw-ui` (button) or `frame:prepare` (save restore at the physics handoff) |
| Release | `PruneExcept` right after `RingRendererRebuilder.Rebuild` returns | Same call as Apply / Remove |
| GPU sampling | Ring pipeline via descriptor-set image views baked in the render-data constructor; planet, atmosphere and distant-sphere shaders via the bindless handle | `render:atmosphere`, `render:scene-opaque` |
| CPU sampling of the control strip | `PlanetaryRingsRenderData.UpdateData`, called from `RenderRingFarSide` while recording | `render:atmosphere` (main thread) |

Why this is safe where it runs:

- The upload uses its own staging pool and waits for it, so it does not depend on the frame's
  command buffers; it runs before `ImGui.Render` and before the frame is acquired.
- Release happens only after a rebuild whose final `Device.WaitIdle()` has drained every frame that
  could reference the old image, and after the new render data was built without it. The planet
  and atmosphere passes read the template's current band every frame, and the distant sphere is
  re-synced before the prune, so nothing keeps the freed bindless index.
- The CPU copy must outlive the GPU texture: `UpdateData` reads
  `ControlTexture.Get().TextureAsset.Texture.Data` every frame to pick the volumetric thickness,
  step size and render distance at the camera's ring radius.[^ksa-rings-render-data]

## Key code

| Path | Role |
|---|---|
| [bloomin-onion.lib/RingBandPainter.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/RingBandPainter.cs) | Band and control rasterization, content-hash ids |
| [bloomin-onion.lib/PaintedTextureReference.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/PaintedTextureReference.cs) | `TextureReference` subclass, reflection seed, bind and release |
| [bloomin-onion.lib/RingTextureFactory.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/RingTextureFactory.cs) | Cache by id, prune after rebuild |
| [bloomin-onion.lib/RingDefinitionController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/RingDefinitionController.cs#L206-L227) | Which ids stay alive after a rebuild |
| [bloomin-onion.lib/RingReferenceBuilder.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/RingReferenceBuilder.cs#L114-L139) | Painted versus picked band; `IsCpuSampleable` at L220-L233 |
| [bloomin-onion.lib/BloominOnionSubmod.UiSections.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/BloominOnionSubmod.UiSections.cs#L115-L133) | Live preview strip |
| [bloomin-onion.lib/BloominOnionSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/BloominOnionSubmod.cs#L92-L102) | Control-strip candidate list filtered with `IsCpuSampleable` |

## Snippets

Create, seed and bind an in-memory texture through the game's own path:

```csharp
// bloomin-onion.lib/PaintedTextureReference.cs L46-L73
        try
        {
            // The CPU copy must outlive the GPU texture: the ring renderer samples the control
            // strip through TextureAsset.Texture.Data every frame.
            var texture = GenericTexture.Defaults.RGBA8UNorm(new int2(width, height));
            rgba.AsSpan().CopyTo(texture.Data);

            var reference = new PaintedTextureReference
            {
                Id = id,
                Category = TextureCategory.Default,
                Width = width,
                Height = height,
            };
            reference.SetHash();
            TextureAssetField.SetValue(reference, new TextureAsset(texture, id));

            var renderer = Program.GetRenderer();
            // StagingPool disposal submits and waits, so the upload is complete on return.
            using (var stagingPool = renderer.Allocator.CreateStagingPool(renderer.GraphicsAndCompute, 1))
                reference.Bind(renderer, stagingPool);

            if (reference.BindlessHandle == 0)
            {
                error = $"texture '{id}' did not receive a bindless handle";
                return null;
            }
            return reference;
```

The control strip derived from band opacity:

```csharp
// bloomin-onion.lib/RingBandPainter.cs L32-L46
    public static byte[] PaintControl(RingDefinition definition)
    {
        var pixels = new byte[Width * 4];
        float threshold = (float)Math.Clamp(definition.MeshCoverageThreshold, 0.0, 1.0);
        for (int x = 0; x < Width; x++)
        {
            float alpha = Evaluate(definition, x).W;
            float meshes = threshold >= 1f ? 0f : Math.Clamp((alpha - threshold) / 0.1f, 0f, 1f);
            pixels[x * 4 + 0] = ToByte(meshes);
            pixels[x * 4 + 1] = ToByte(alpha);
            pixels[x * 4 + 2] = 0;
            pixels[x * 4 + 3] = 255;
        }
        return pixels;
    }
```

Why the control strip must stay uncompressed and resident on the CPU (game code, build 5554):

```csharp
// current/decomp/KSA.Rendering.Rings.Rendering/PlanetaryRingsRenderData.cs L99-L115
	private float4 SampleRingTexture(float coord, TextureAsset textureAsset)
	{
		int num = DerivedTexturePropertiesExtensions.get_Width(textureAsset.Texture);
		Span<byte> data = textureAsset.Texture.Data;
		VkFormat fmt = textureAsset.Texture.Format.ToVkFormat();
		float num2 = coord * (float)num - 0.5f;
		int num3 = (int)Math.Floor(num2);
		float num4 = num2 - (float)num3;
		int num5 = Math.Clamp(num3, 0, num - 1);
		int num6 = Math.Clamp(num3 + 1, 0, num - 1);
		ByteSize byteSize = TextureAsset.BytesPerTexel(fmt);
		int num7 = (int)(num5 * byteSize);
		int num8 = (int)(num6 * byteSize);
		float4 float5 = new float4((int)data[num7], (int)data[num7 + 1], (int)data[num7 + 2], (int)data[num7 + 3]) / 255f;
		float4 float6 = new float4((int)data[num8], (int)data[num8 + 1], (int)data[num8 + 2], (int)data[num8 + 3]) / 255f;
		return float5 + num4 * (float6 - float5);
	}
```

Cache by content id, release only what no applied ring uses:

```csharp
// bloomin-onion.lib/RingTextureFactory.cs L33-L64
    public int PruneExcept(IReadOnlySet<string> keepIds)
    {
        int pruned = 0;
        foreach (var id in new List<string>(_textures.Keys))
        {
            if (keepIds.Contains(id)) continue;
            _textures[id].Release();
            _textures.Remove(id);
            pruned++;
        }
        if (pruned > 0) Console.WriteLine($"bloomin-onion: freed {pruned} unused painted texture(s)");
        return pruned;
    }
    // ...
    private TextureReference? GetOrCreate(string id, Func<byte[]> paint, out string? error)
    {
        error = null;
        if (_textures.TryGetValue(id, out var cached)) return cached;

        var created = PaintedTextureReference.Create(id, paint(), RingBandPainter.Width, 1, out error);
        if (created == null) return null;
        _textures[id] = created;
        Console.WriteLine($"bloomin-onion: painted texture '{id}' ({RingBandPainter.Width}x1)");
        return created;
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `TextureReference.TextureAsset` (private setter) | [current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L61) | Seeded through `<TextureAsset>k__BackingField` |
| `TextureReference.Bind(Renderer, StagingPool)` (virtual) | [current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L125-L156) | `SimpleVkTexture` upload and `BindlessTextures.AddTexture` |
| `TextureReference.Dispose(Device)` | [current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L77-L82) | Frees the bindless slot and GPU image |
| `TextureReference.BindlessHandle`, `ImageView`, `Get()` | [current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L67-L70) | What ring, planet and atmosphere renderers read |
| `TextureAsset(ITexture, string)` | [current/decomp/RenderCore/TextureAsset.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/TextureAsset.cs#L61-L67) | Wraps an in-memory texture (not owned; `Dispose` is empty in 5554) |
| `GenericTexture.Defaults.RGBA8UNorm(int2)` | [current/decomp/Brutal.TextureApi.Abstractions/GenericTexture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.TextureApi.Abstractions/GenericTexture.cs#L76-L81) | CPU-side RGBA8 buffer |
| `StagingPool.Dispose()` | [current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs#L214-L218) | Submit and wait: the upload is synchronous |
| `PlanetaryRingsRenderData.UpdateData` / `SampleRingTexture` | [current/decomp/KSA.Rendering.Rings.Rendering/PlanetaryRingsRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Rings.Rendering/PlanetaryRingsRenderData.cs#L72-L115) | Per-frame CPU read of the control strip |
| Saturn `<Rings>` XML | [current/Content/Core/Astronomicals.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Astronomicals.xml#L4261-L4278) | Reference layout and control-channel meaning |

## Pitfalls and version notes

- `SampleRingTexture` indexes `Data` with `BytesPerTexel(fmt)` but always reads four bytes per
  texel; a block-compressed or narrower control texture reads the wrong memory. Painted strips are
  RGBA8 by construction, picked ones go through `IsCpuSampleable`.
- The backing-field name is a compiler convention. If `TextureAsset` stops being an auto-property,
  `PaintedTextureReference.IsSupported` turns false and the builder refuses painted bands with a
  clear message instead of crashing.
- Never release a painted texture while any frame in flight could sample it. The mod frees them only
  after `RebuildRenderer`'s final `WaitIdle`; freeing them from an arbitrary UI callback would leave
  a dangling bindless index in the planet and atmosphere UBOs.
- `TextureAsset.Dispose` is a no-op in 5554 and the in-memory constructor does not own the
  texture, so each released strip's 8 KB CPU buffer is left to the garbage collector.
- The band is GPU-sampled with a linear clamped sampler and also drives both ring shadows via its
  alpha; an opaque base color therefore casts a solid shadow band.

## See also

- [Runtime planetary rings](./bloomin-onion-runtime-planetary-rings.md) (where the textures are used and when the rebuild runs)
- [Rocky McRock Face ring asset swap](./rocky-mcrock-face-ring-asset-swap.md) (picked band textures on stock rings)
- [Graffiti bindless decal textures](./graffiti-bindless-decal-textures.md) (another runtime bindless texture path)
- [Bloomin' Onion sub-mod](../submods/bloomin-onion.md) and [mod overview](../overview.md)
- [Build the ring reference tree, assign it to BodyTemplate.RingsReference, dispose and recreate the rings renderer](../../../howto/celestials/atmospheres-and-rings/add-or-restyle-planetary-rings/add-or-restyle-planetary-rings-1.md) - how-to article demonstrating this feature

[^ring-band-painter]: bloomin-onion.lib/RingBandPainter.cs
[^ksa-astronomicals-xml]: Content/Core/Astronomicals.xml (Saturn ring definition, build 2026.10.10.5554)
[^bloomin-ui-sections]: bloomin-onion.lib/BloominOnionSubmod.UiSections.cs
[^ksa-generic-texture]: Brutal.TextureApi.Abstractions.GenericTexture (decompiled, build 2026.10.10.5554)
[^ksa-texture-reference]: KSA.TextureReference (decompiled, build 2026.10.10.5554)
[^ksa-texture-asset]: RenderCore.TextureAsset (decompiled, build 2026.10.10.5554)
[^ksa-staging-pool]: Brutal.VulkanApi.Abstractions.StagingPool (decompiled, build 2026.10.10.5554)
[^ring-definition-controller]: bloomin-onion.lib/RingDefinitionController.cs
[^ring-reference-builder]: bloomin-onion.lib/RingReferenceBuilder.cs
[^ksa-rings-render-data]: KSA.Rendering.Rings.Rendering.PlanetaryRingsRenderData (decompiled, build 2026.10.10.5554)
