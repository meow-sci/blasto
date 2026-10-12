---
type: Mod Feature
title: Runtime canopy textures from PNGs, CPU compositing over a BC7 stock texture, and owned GPU assets
description: Free Fallin builds canopy textures on the CPU (PNG decode, a centred decal over the stock BC7 albedo re-opened from KTX2 with an RGBA32 transcode, a 1x1 PBR map), registers them with GpuTextureSystem.TryAddTexture and releases its own entries through the protected AssetMap
mod: unscience
submod: free-fallin
category: assets
techniques: [vulkan-texture, reflection]
hooks:
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent; Apply)
  - reflection KSA.AssetManager.AssetMap (protected)
lifecycle: [frame:draw-ui, event:load]
game_types:
  - KSA.GpuTextureSystem
  - KSA.GpuTextureAssetRef
  - KSA.AssetManager
  - KSA.LoadedAssetRef
  - KSA.TextureReference
  - KSA.PbrMaterialReference
  - KSA.GpuMaterialSystem
  - Brutal.TextureApi.TextureLoader
  - Brutal.TextureApi.Abstractions.GenericTexture
  - RenderCore.TextureAsset
  - KSA.Rendering.RetiredResourceQueue
  - KSA.Program
tags: [textures, ktx2, bc7, png, compositing, gpu-assets, parachutes]
---

Three of Free Fallin's four canopy modes need a texture the game does not ship: **Replace** and
**Full canopy** use an imported PNG as is, and **Center decal** paints a PNG into the middle of the
stock canopy pattern before upload, so the result bends with the cloth like the original
texture.[^ff-readme][^ff-texture-mode] All of it happens once per Apply on the CPU; the GPU only sees
finished textures registered in KSA's own texture system.

Mechanism in one line: produce an RGBA8 `GenericTexture` on the CPU, wrap it in a `TextureAsset`, call
`GpuTextureSystem.TryAddTexture(name, asset)` to get a bindless handle and sampler, and track every
such registration in a small owner object that can later remove exactly those entries from the
protected `AssetMap` and dispose them.[^ff-controller][^ff-gpu-assets]

## How it works

**Source images.** PNGs come from the shared `.unscience/pngs` library ([shared file
library](./lib-shared-file-library.md)).[^png-library]

- *Replace / Full canopy* (`LoadReplacement`): `TextureLoader.LoadFromMemory(bytes, Png,
  TextureAsset.LoadOptions(R8G8B8A8UNorm, Rgba32))`, then copy mip 0 into
  `GenericTexture.Defaults.RGBA8UNorm(extent)` and destroy the decoder's image.
- *Center decal* (`ComposeCenteredDecal`): the stock canopy diffuse is resident as BC7 at runtime,
  which is useless for CPU work, so when its format is not RGBA8 the mod re-opens the source file
  (`TextureReference.ModPath`) with `TextureLoader.LoadFromFile(path, LoadOptions(R8G8B8A8UNorm,
  Rgba32))`, asking libktx to transcode to RGBA32. It copies the rows (honouring `RowPitch`), scales
  the PNG to fit `DecalScale` of the canopy texture with nearest sampling, and alpha-blends it into the
  centre while keeping the stock alpha, so transparent PNG pixels never cut holes. If the transcode
  fails (a native, non-transcodable BC7 file), it falls back to a flat white, tintable base instead of
  failing.[^ff-controller][^ksa-textureloader][^ksa-texturereference]
- *Uniform PBR* (`UploadSolidPbr`): a 1x1 RGBA8 texture holding (AO, roughness, metallic, 255).[^ksa-generictexture]

**Upload.** `Upload` names the asset `free-fallin/<kind>/<revision>`, wraps the CPU texture in a
`TextureAsset` (disposed right after), and calls `TryAddTexture`, which builds a `SimpleVkTexture` on a
staging pool, binds it into the bindless table and registers a `GpuTextureAssetRef` with the clamp
sampler (`repeat: false`). The returned `BindlessHandle` and `SamplerHandle` go into the canopy
`MaterialData`.[^ksa-gputexturesystem]

**Ownership** (`CanopyGpuAssets`): `Own<T>(manager, asset, released)` reflects
`AssetManager<T>.AssetMap` (a protected `ConcurrentDictionary<AssetName, T>`) and stores a release
action that removes exactly that key/value pair (via `ICollection<KeyValuePair>.Remove`, so a
replaced entry with the same name is not touched) and disposes the asset. `Release` first calls
`Program.GetRenderer().Device.WaitIdle()`, then runs the actions in reverse. Disposing a
`GpuTextureAssetRef` hands the image and bindless handle to the game's `RetiredResourceQueue`;
disposing a `GpuObjectAssetRef` frees its material record. An Apply that throws half-way disposes its
uncommitted owner (`using var next`), so failed attempts leak nothing; a committed previous owner is
released only after the canopies point at the new material.[^ff-gpu-assets][^ksa-assetmanager][^ksa-gputextureassetref][^ksa-gpuobjectassetref][^ksa-loadedassetref]

## Lifecycle and timing

Everything here runs inside `CanopyMaterialController.Apply`, on the main thread, either from the
**Apply to All Parachutes** button (`RenderContent`, `[StarMapAfterGui]`, `frame:draw-ui`) or from the
save participant when a save is loaded (`event:load`). Decoding, transcoding and compositing are
synchronous CPU work and `TryAddTexture` waits on its own staging submit, so an Apply with a large PNG
causes one visible hitch; the release of the previous set adds a device-wide `WaitIdle`. Nothing runs
per frame: the draw-time part of the feature is only the material index swap described in
[canopy material substitution](./free-fallin-canopy-material-substitution.md).[^ff-controller]

## Key code

| Path | Role |
|---|---|
| [free-fallin.lib/CanopyMaterialController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/CanopyMaterialController.cs#L123-L146) | `ResolveAlbedo`: stock, replacement or composited albedo |
| [free-fallin.lib/CanopyMaterialController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/CanopyMaterialController.cs#L163-L289) | Uniform PBR, upload, PNG load, KTX2 re-open, composite, decode cleanup |
| [free-fallin.lib/CanopyGpuAssets.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/CanopyGpuAssets.cs) | Owned registrations and release |

## Snippets

Re-opening the BC7 stock texture's KTX2 source with an RGBA32 transcode:

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
```

Centred, aspect-preserving alpha composite that keeps the stock alpha:

```csharp
// free-fallin.lib/CanopyMaterialController.cs L237-L257
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
```

Registering a CPU texture with the game:

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

Removing exactly the owned entries from a game asset manager:

```csharp
// free-fallin.lib/CanopyGpuAssets.cs L17-L37
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
    public void Dispose() { if (!Committed) Release(); }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `GpuTextureSystem.TryAddTexture(AssetName, TextureAsset, bool repeat = false)` | [current/decomp/KSA/GpuTextureSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuTextureSystem.cs#L100-L115) | Upload, bind, register (clamp sampler unless `repeat`) |
| `GpuTextureSystem.SamplerRepeatHandle`, `SamplerClampHandle`, `DefaultBlackTexture` | [current/decomp/KSA/GpuTextureSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuTextureSystem.cs#L24-L34) | Sampler and fallback handles |
| `GpuTextureAssetRef.Destroy` | [current/decomp/KSA/GpuTextureAssetRef.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuTextureAssetRef.cs#L26-L29) | Retires the image and handle through `RetiredResourceQueue` |
| `GpuObjectAssetRef.Destroy` | [current/decomp/KSA/GpuObjectAssetRef.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuObjectAssetRef.cs#L18-L23) | Frees the material record slot |
| `AssetManager<T>.AssetMap` (protected), `GetOrLoad` | [current/decomp/KSA/AssetManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetManager.cs#L9-L60) | Registration map removed from by reflection |
| `LoadedAssetRef.Dispose` | [current/decomp/KSA/LoadedAssetRef.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LoadedAssetRef.cs#L17-L29) | Idempotent dispose calling `Destroy` |
| `TextureLoader.LoadFromFile` / `LoadFromMemory` | [current/decomp/Brutal.TextureApi/TextureLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.TextureApi/TextureLoader.cs#L111-L150) | KTX2 re-open with transcode; PNG decode |
| `GenericTexture.Defaults.RGBA8UNorm(int2)` | [current/decomp/Brutal.TextureApi.Abstractions/GenericTexture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.TextureApi.Abstractions/GenericTexture.cs#L76) | CPU RGBA8 image |
| `TextureReference.ModPath`, `TextureAsset` | [current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L84-L104) | Source path of the stock texture and its load options |

## Pitfalls and version notes

- **One sampler per material.** `MaterialSet.glsl`
  ([current/Content/Core/Shaders/Common/MaterialSet.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/MaterialSet.glsl#L28-L41)) samples albedo, normal, PBR and emissive maps with
  the material's single `samplerIndex`. Custom PNG modes use the clamp sampler that `TryAddTexture`
  assigns, so the stock normal and PBR maps are clamped too in those modes, while Stock mode uses the
  repeat sampler.[^ksa-materialset-glsl]
- **BC7 at runtime.** The comment and README describe BC7 on 5402; whether a given install can be
  transcoded depends on how the KTX2 was authored, hence the flat-base fallback.
- **Reflection on `AssetMap`** is required because asset managers expose no public remove; removing the
  exact pair keeps a same-named newer registration safe.
- The release path waits for the whole device to go idle. It is fine once per Apply, not per frame.

## See also

- [Canopy material substitution](./free-fallin-canopy-material-substitution.md)
- [Full-canopy projection shader](./free-fallin-canopy-projection-shader.md)
- [Bindless decal textures](./graffiti-bindless-decal-textures.md) (graffiti's own upload path for the same PNG library)
- [Free Fallin sub-mod](../submods/free-fallin.md) and the [mod overview](../overview.md)
- [Blend a PNG decal over a transcoded stock texture on the CPU and register it with GpuTextureSystem](../../../howto/assets/models-and-textures/composite-png-over-stock-texture/composite-png-over-stock-texture-1.md) - how-to article demonstrating this feature

[^ff-controller]: free-fallin.lib/CanopyMaterialController.cs
[^ff-gpu-assets]: free-fallin.lib/CanopyGpuAssets.cs
[^ff-texture-mode]: free-fallin.lib/CanopyTextureMode.cs
[^ff-readme]: free-fallin/README.md
[^png-library]: ksa-abstractions.lib/PngLibrary.cs
[^ksa-gputexturesystem]: KSA.GpuTextureSystem (decompiled, build 2026.10.10.5554)
[^ksa-gputextureassetref]: KSA.GpuTextureAssetRef (decompiled, build 2026.10.10.5554)
[^ksa-assetmanager]: KSA.AssetManager (decompiled, build 2026.10.10.5554)
[^ksa-loadedassetref]: KSA.LoadedAssetRef (decompiled, build 2026.10.10.5554)
[^ksa-gpuobjectassetref]: KSA.GpuObjectAssetRef (decompiled, build 2026.10.10.5554)
[^ksa-textureloader]: Brutal.TextureApi.TextureLoader (decompiled, build 2026.10.10.5554)
[^ksa-generictexture]: Brutal.TextureApi.Abstractions.GenericTexture (decompiled, build 2026.10.10.5554)
[^ksa-texturereference]: KSA.TextureReference (decompiled, build 2026.10.10.5554)
[^ksa-materialset-glsl]: Common/MaterialSet.glsl (game shader header, build 2026.10.10.5554)
