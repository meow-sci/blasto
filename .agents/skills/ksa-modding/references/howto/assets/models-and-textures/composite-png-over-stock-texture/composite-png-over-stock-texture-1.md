---
type: How-To
title: Blend a PNG decal over a transcoded stock texture on the CPU and register it with GpuTextureSystem
description: Re-open the stock KTX2 source with an RGBA32 transcode because the resident texture is block-compressed, alpha-blend a scaled PNG into the centre while keeping the stock alpha, then upload with TryAddTexture and track ownership so only your own entries are removed
category: assets
group: models-and-textures
topic: composite-png-over-stock-texture
approach: 1
difficulty: advanced
prerequisites: [starmap, reflection, vulkan]
demonstrated_by: [mods/unscience/features/free-fallin-canopy-texture-compositing]
techniques: [vulkan-texture, reflection]
hooks: [starmap StarMapAfterGui, reflection KSA.AssetManager.AssetMap]
lifecycle: [frame:draw-ui, event:load]
game_types:
  - KSA.TextureReference
  - KSA.GpuTextureSystem
  - KSA.GpuTextureAssetRef
  - KSA.AssetManager
  - Brutal.TextureApi.TextureLoader
  - Brutal.TextureApi.Abstractions.GenericTexture
  - RenderCore.TextureAsset
tags: [texture, decal, ktx2, bc7, transcode, gputexturesystem, composite, livery]
---

You want a user's image (a livery, a decal, a logo) on top of a game texture. At run time the stock texture may not be a bitmap: the mod's comment records that stock vessel textures are kept GPU-ready as BC7 on the build it targeted, so there are no pixels to blend with. unscience's Free Fallin does it for parachute canopies in four steps. Re-open the stock texture's source file asking libktx for an RGBA32 transcode, alpha-blend the scaled PNG into the
centre while preserving the stock alpha, hand the result to `GpuTextureSystem.TryAddTexture` and track ownership so that only entries the mod created are removed later. It all happens once per Apply on the CPU; the GPU only sees finished textures.

Demonstrated by [Runtime canopy textures from PNGs, CPU compositing over a BC7 stock texture, and owned GPU assets](../../../../mods/unscience/features/free-fallin-canopy-texture-compositing.md). For an offline alternative see [Write a modified copy of a stock texture with an offline script](composite-png-over-stock-texture-2.md); for texture upload in general [Upload an image or procedural pixels as a GPU texture](../../../rendering/textures-and-decals/upload-an-image-as-a-vulkan-texture/overview.md).

## Background: how the game does it

`TextureReference.DoLoad` opens the texture with `TextureAsset.LoadOptions(format, transcodeFormat)`, taken from the optional sidecar `.toml` (`format` default `R8G8B8A8UNorm`, `scblockformatfamily` default `Rgba32`; stock sidecars set `max`, `mipMaps` and `scblockformatfamily`), and `Bind` uploads it as a `SimpleVkTexture` with a bindless handle
([current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L84-L108), [current/decomp/KSA/TextureManifest.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureManifest.cs), [current/Content/Core/Textures/Earth_Normal.toml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Textures/Earth_Normal.toml)). `TextureAsset.LoadOptions` builds both the stb (PNG/JPG) and the KTX settings; the KTX one carries the super-compression transcode format
([current/decomp/RenderCore/TextureAsset.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/TextureAsset.cs#L109-L121)). `TextureLoader.LoadFromFile` and `LoadFromMemory` return an `ITexture` with `ImageData`, `RowPitch` and `Extent`.

`GpuTextureSystem.TryAddTexture(name, TextureAsset, repeat)` returns false when the name is taken, otherwise builds a `SimpleVkTexture` on a staging pool, binds it into the bindless table and registers a `GpuTextureAssetRef` with a clamp (or repeat) sampler:

```csharp
// current/decomp/KSA/GpuTextureSystem.cs L100-L116
	public bool TryAddTexture(AssetName name, TextureAsset cpuTexture, bool repeat = false)
	{
		if (AssetMap.ContainsKey(name))
		{
			return false;
		}
		using StagingPool stagingPool = _deviceCtx.CreateStagingPool();
		SimpleVkTexture simpleVkTexture = new SimpleVkTexture(Allocator, stagingPool, cpuTexture);
		int bindlessHandle = BindTexture(simpleVkTexture);
		GpuTextureAssetRef asset = new GpuTextureAssetRef(name, simpleVkTexture, this, bindlessHandle, repeat ? SamplerRepeatHandle : SamplerClampHandle);
		if (!TryAdd(asset))
		{
			simpleVkTexture.Dispose();
			return false;
		}
		return true;
	}
```

The registry that holds those assets is `AssetManager<T>.AssetMap`, a **protected** `ConcurrentDictionary<AssetName, T>` ([current/decomp/KSA/AssetManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetManager.cs#L9-L20)).

## Lifecycle and timing

Everything runs inside the controller's `Apply`, on the main thread, from the Apply button (`[StarMapAfterGui]`, `frame:draw-ui`) or from a save participant when a save loads (`event:load`). Decoding, transcoding and compositing are synchronous CPU work and `TryAddTexture` waits on its own staging submit, so an Apply with a large PNG causes one visible hitch; releasing the
previous set adds a device-wide `WaitIdle`. Nothing runs per frame. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Resolve the stock material's diffuse texture** and keep its source `ModPath`. A resident `BC7` texture cannot be read as pixels:

   ```csharp
   // free-fallin.lib/CanopyMaterialController.cs L195-L219
       private static GenericTexture ComposeCenteredDecal(PbrMaterialReference stock, string path, float scale)
       {
           TextureReference stockDiffuse = stock.DiffuseReference?.Get()
               ?? throw new InvalidOperationException("The stock canopy diffuse texture is unavailable.");
           Brutal.TextureApi.ITexture source = stockDiffuse.TextureAsset.Texture;
           Brutal.TextureApi.ITexture? decodedStock = null;

           // KSA normally keeps vessel textures GPU-ready (BC7 on the 5402 build). CPU compositing
           // needs texels, so reopen the original KTX2 and explicitly ask Basis/KTX to transcode it
           // to RGBA32 instead of trying to interpret the runtime BC7 blocks as pixels.
           if (!IsRgba8(source.Format))
           {
               try
               {
                   decodedStock = TextureLoader.LoadFromFile(stockDiffuse.ModPath,
                       TextureAsset.LoadOptions(VkFormat.R8G8B8A8UNorm, Brutal.KtxApi.KtxTranscodeFmt.Rgba32));
                   source = decodedStock;
               }
               catch (Exception ex)
               {
                   // A native-BC7 KTX2 cannot be transcoded by libktx. The decal feature remains
                   // useful on a flat tintable canopy, so degrade visually instead of rejecting it.
                   Console.WriteLine($"free-fallin: stock canopy RGBA transcode failed; using a flat decal base: {ex.Message}");
               }
           }
       // ...
   ```

2. **Load the PNG and create the output buffer.** Use the stock extent; if the transcode failed, fall back to a flat white (tintable) base rather than failing. Honour the source `RowPitch` when copying rows:

   ```csharp
   // free-fallin.lib/CanopyMaterialController.cs L221-L236
           Brutal.TextureApi.ITexture? decal = null;
           try
           {
               decal = TextureLoader.LoadFromMemory(File.ReadAllBytes(path), TextureLoader.FormatType.Png,
                   TextureAsset.LoadOptions(VkFormat.R8G8B8A8UNorm, Brutal.KtxApi.KtxTranscodeFmt.Rgba32));
               int width = source.Extent.X;
               int height = source.Extent.Y;
               var output = GenericTexture.Defaults.RGBA8UNorm(new int2(width, height));
               if (IsRgba8(source.Format))
                   CopyRows(source, output, width, height);
               else
               {
                   output.Data.Fill(255);
                   Console.WriteLine($"free-fallin: stock canopy is {source.Format}; centered decal will use a flat tintable base");
               }
           // ...

   ```

3. **Blend the decal centred, aspect preserving, nearest sampled, keeping the stock alpha.** A transparent decal pixel must not cut holes in the canopy:

   ```csharp
   // free-fallin.lib/CanopyMaterialController.cs L237-L258
               scale = Math.Clamp(scale, 0.05f, 1f);
               float fit = Math.Min(width * scale / decal.Extent.X, height * scale / decal.Extent.Y);
               int drawWidth = Math.Max(1, (int)MathF.Round(decal.Extent.X * fit));
               int drawHeight = Math.Max(1, (int)MathF.Round(decal.Extent.Y * fit));
               int left = (width - drawWidth) / 2;
               int top = (height - drawHeight) / 2;
               Span<byte> src = decal.ImageData(0, 0, 0);
               Span<byte> dst = output.Data;
               int srcPitch = (int)decal.RowPitch(0);
               int dstPitch = width * 4;
               for (int y = 0; y < drawHeight; y++)
               for (int x = 0; x < drawWidth; x++)
               {
                   int sx = Math.Min(decal.Extent.X - 1, x * decal.Extent.X / drawWidth);
                   int sy = Math.Min(decal.Extent.Y - 1, y * decal.Extent.Y / drawHeight);
                   int si = sy * srcPitch + sx * 4;
                   int di = (top + y) * dstPitch + (left + x) * 4;
                   float alpha = src[si + 3] / 255f;
                   for (int c = 0; c < 3; c++) dst[di + c] = (byte)Math.Clamp((int)MathF.Round(src[si + c] * alpha + dst[di + c] * (1f - alpha)), 0, 255);
                   // Keep the stock alpha: a transparent decal must not cut holes in the canopy.
               }
               return output;
   ```

4. **Upload.** Name it `<mod>/<kind>/<revision>`, wrap the CPU `GenericTexture` in a `TextureAsset` (disposed right after), call `TryAddTexture`, and fetch the registered ref:

   ```csharp
   // free-fallin.lib/CanopyMaterialController.cs L172-L178
       private static GpuTextureAssetRef Upload(GpuTextureSystem textures, GenericTexture cpuTexture, string kind, CanopyGpuAssets owned)
       {
           AssetName name = $"free-fallin/{kind}/{++_revision}";
           using var asset = new TextureAsset(cpuTexture, name.ToString());
           if (!textures.TryAddTexture(name, asset)) throw new InvalidOperationException($"Could not upload the canopy {kind} texture.");
           return owned.Own(textures, textures.GetOrLoad(name));
       }
   ```

5. **Track ownership.** Record a release action for every entry you created, reflecting `AssetMap` and removing exactly that key/value pair (via `ICollection<KeyValuePair>.Remove`, so a replaced entry of the same name is not touched), after waiting for the device:

   ```csharp
   // free-fallin.lib/CanopyGpuAssets.cs L17-L36
       internal T Own<T>(AssetManager<T> manager, T asset, Action? released = null) where T : LoadedAssetRef
       {
           var field = typeof(AssetManager<T>).GetField("AssetMap", BindingFlags.Instance | BindingFlags.NonPublic)
               ?? throw new MissingFieldException(typeof(AssetManager<T>).FullName, "AssetMap");
           var map = (ConcurrentDictionary<AssetName, T>)field.GetValue(manager)!;
           _release.Add(() =>
           {
               ((ICollection<KeyValuePair<AssetName, T>>)map).Remove(new(asset.Id, asset));
               asset.Dispose();
               released?.Invoke();
           });
           return asset;
       }

       internal void Release()
       {
           if (_release.Count == 0) return;
           Program.GetRenderer().Device.WaitIdle();
           for (int i = _release.Count - 1; i >= 0; i--) { _release[i](); _release.RemoveAt(i); }
       }
   ```

6. **Make a failed Apply leak nothing.** Create the new owner in a `using`; commit it only after the consumers point at the new material, and release the previous set last. Where the PNG comes from (the shared image library) is a separate topic.

## Complete example

- [free-fallin.lib/CanopyMaterialController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/CanopyMaterialController.cs), [free-fallin.lib/CanopyGpuAssets.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/CanopyGpuAssets.cs)
- The game side: [current/decomp/KSA/GpuTextureSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuTextureSystem.cs#L100-L116), [current/decomp/KSA/GpuTextureAssetRef.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuTextureAssetRef.cs#L7-L25)

## Variations and gotchas

- **Native BC7 KTX2s cannot be transcoded by libktx.** The mod degrades visually (flat tintable base) instead of rejecting the decal; decide your own fallback.
- **The stock texture can change per game build** (the mod's comment: BC7 on one build). Check `IsRgba8(format)` and branch rather than assuming.
- **Disposing a `GpuTextureAssetRef` hands the image and bindless handle to the game's retired resource queue**; wait for the device first so no in-flight frame uses it.
- **Names must be unique per revision** because `TryAddTexture` refuses an existing name.
- **Destroy decoder images** by type (stb, ktx or gli texture) after copying, to avoid native leaks.
- **One hitch per Apply.** Move it behind a button or a load event, never per frame.

## See also

- [Write a modified copy of a stock texture with an offline script](composite-png-over-stock-texture-2.md), [Upload an image or procedural pixels as a GPU texture](../../../rendering/textures-and-decals/upload-an-image-as-a-vulkan-texture/overview.md), [Replace a game texture or model texture with a user PNG at runtime](../../../rendering/textures-and-decals/replace-a-game-texture-at-runtime/overview.md), [Customise parachutes: swap canopy materials and lift the diameter limits](../../../vehicles-and-parts/modules-and-resources/customize-parachute-materials-and-limits/overview.md),
  [Build searchable lists, two-pane browsers and file pickers in ImGui](../../../ui/windows/build-searchable-lists-and-browsers/overview.md), [Free mod-owned Vulkan resources without device loss](../../../rendering/vulkan-passes/free-vulkan-resources-safely/overview.md)
- [Runtime canopy textures from PNGs, CPU compositing over a BC7 stock texture, and owned GPU assets](../../../../mods/unscience/features/free-fallin-canopy-texture-compositing.md)
