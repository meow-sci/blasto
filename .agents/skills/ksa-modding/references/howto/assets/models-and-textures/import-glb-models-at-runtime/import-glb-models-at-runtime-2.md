---
type: How-To
title: Convert glTF materials on the CPU into game maps and upload them as private bindless textures
description: Bake glTF base colour, metallic-roughness, occlusion, normal and alpha into the game's diffuse, packed AO-roughness-metal, normal and opacity images, decode PNG and JPEG with bounded dimensions, and upload them through a private SimpleVkTexture and a reflected bindless handle outside ImGui
category: assets
group: models-and-textures
topic: import-glb-models-at-runtime
approach: 2
difficulty: advanced
prerequisites: [starmap, reflection, vulkan]
demonstrated_by: [mods/unscience/features/pebbles-glb-material-conversion]
techniques: [glb-model, vulkan-texture, reflection]
hooks: [starmap StarMapBeforeGui, reflection KSA.TextureReference.BindlessHandle]
lifecycle: [frame:draw-ui, frame:dispatch-solvers]
game_types:
  - KSA.TextureReference
  - RenderCore.SimpleVkTexture
  - RenderCore.Systems.BindlessTextureLibrary
  - Brutal.TextureApi.TextureLoader
  - KSA.Program
  - Core.Renderer
  - KSA.TexturePowerReference
  - KSA.MeshReference
tags: [glb, materials, pbr, bindless, vulkan-texture, simplevktexture, conversion, stb]
---

An imported GLB brings glTF materials; KSA's clutter and static-object shaders want something else: a fixed set of bindless textures with their own conventions. Pebbles converts each glTF material on the CPU into that set, records lossy fallbacks as user-visible warnings, and uploads the generated images as private GPU
textures that never enter the game's asset library. This article covers the whole material-conversion topic: the channel mapping (R occlusion, G roughness, B metalness), the bounded decoder, the private upload and its ordering rules.

Demonstrated by [GLB PBR materials converted to native clutter maps with private bindless textures](../../../../mods/unscience/features/pebbles-glb-material-conversion.md). The geometry half is [Parse a GLB with a bounded reader into unregistered CPU meshes with path-plus-hash ids](import-glb-models-at-runtime-1.md); texture upload in general: [Upload an image or procedural pixels as a GPU texture](../../../rendering/textures-and-decals/upload-an-image-as-a-vulkan-texture/overview.md).

## Background: how the game does it

A game `TextureReference` loads a file, becomes a `TextureAsset`, and is bound at `ModLibrary.Bind` time: `SimpleVkTexture` from the asset, then `Program.Instance.BindlessTextures.AddTexture(ImageView)` returns the slot stored in `BindlessHandle` (private setter)
([current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L125-L156)). Shaders then index that handle. For a runtime image there is no `TextureAsset`, so Pebbles builds a `SimpleVkTexture` directly and writes the handle itself. The bindless library exposes `AddTexture(VkImageView, VkImageLayout)` and `FreeTexture(int)`
([current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs#L186-L215)). The game's native decoder is `TextureLoader.LoadFromMemory` with stb's `ForceRgba8` settings ([current/decomp/Brutal.TextureApi.Stb/Loader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.TextureApi.Stb/Loader.cs#L16-L20)).

## Lifecycle and timing

| Step | Where | Thread / frame point |
|---|---|---|
| CPU conversion (`GetMaterial`) | When a mesh is selected from UI | Main thread, `frame:draw-ui` (`[StarMapAfterGui]`) |
| GPU upload (`GlbTexture.Upload`) | Preview refresh and queued placements in `Update` (`[StarMapBeforeGui]`); clutter Apply in the `ExecuteNextClothSolvers` prefix | Main thread, outside ImGui draw recording; blocking submit and fence wait |
| Release | Deferred until all borrowers have retired, after `Device.WaitIdle()` | Main thread |

The upload stays out of ImGui emission on purpose: a texture must not be created, destroyed or replaced after this frame's ImGui list may reference it, and the private command buffer is submitted immediately rather than joining the frame's. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Convert on the CPU into the game's conventions.** For each glTF material index (or -1 for the default):

   | glTF input | Output |
   |---|---|
   | `baseColorTexture` x `baseColorFactor` | Diffuse: decoded to linear, multiplied by the factor, re-encoded as sRGB, alpha forced to 255 (for Pebbles' clutter shader, diffuse alpha means terrain tint) |
   | `occlusionTexture` (strength), `metallicRoughnessTexture`, factors | One packed map: R = AO, G = roughness, B = metallic; missing channels use glTF defaults |
   | `normalTexture` (scale) | Normal map rescaled in X/Y and renormalised |
   | `alphaMode` MASK/BLEND, cutoff | A separate opacity map; BLEND becomes a fixed 0.5 cutout |

   The core of the diffuse and packed maps:

   ```csharp
   // pebbles.lib/Models/GlbPixels.cs L8-L35
       internal static GlbPixels Diffuse(GlbPixels? image, float[] factor)
       {
           var output = Blank(image?.Width ?? 1, image?.Height ?? 1);
           for (int i = 0; i < output.Data.Length; i += 4)
           {
               for (int c = 0; c < 3; c++)
               {
                   float source = image == null ? 1 : Linear(image.Data[i + c] / 255f);
                   output.Data[i + c] = Byte(Srgb(source * factor[c]));
               }
               output.Data[i + 3] = 255; // Clutter diffuse alpha is terrain tint/gamma, not transparency.
           }
           return output;
       }

       internal static GlbPixels Pbr(GlbPixels? mr, GlbPixels? ao, float metallic, float roughness, float strength)
       {
           var output = Blank(Math.Max(mr?.Width ?? 1, ao?.Width ?? 1), Math.Max(mr?.Height ?? 1, ao?.Height ?? 1));
           for (int y = 0; y < output.Height; y++) for (int x = 0; x < output.Width; x++)
           {
               int i = (y * output.Width + x) * 4;
               output.Data[i] = Byte(1 + (Sample(ao, x, y, output, 0) - 1) * strength);
               output.Data[i + 1] = Byte(Sample(mr, x, y, output, 1) * roughness);
               output.Data[i + 2] = Byte(Sample(mr, x, y, output, 2) * metallic);
               output.Data[i + 3] = 255;
           }
           return output;
       }
   ```

2. **Decode with bounds first.** Read width and height from the PNG `IHDR` or the JPEG `SOFn` marker, reject anything over 4096 pixels per side, then call the game's native stb decoder and check that the decoded extent and byte count match the header:

   ```csharp
   // pebbles.lib/Assets/GlbTextures.cs L15-L31
       internal static GlbPixels Decode(byte[] encoded, string mime)
       {
           // Bound native decoder allocation from the encoded header before invoking stb.
           var (width, height) = Dimensions(encoded, mime);
           if (width < 1 || height < 1 || width > MaxDimension || height > MaxDimension)
               throw new InvalidOperationException("GLB image dimensions must be between 1 and 4096 pixels.");
           var format = mime == "image/png" ? TextureLoader.FormatType.Png : TextureLoader.FormatType.Jpg;
           var texture = TextureLoader.LoadFromMemory(encoded, format, Brutal.TextureApi.Stb.Loader.LoadSettings.ForceRgba8);
           try
           {
               int bytes = checked(width * height * 4);
               if (texture.Extent.X != width || texture.Extent.Y != height || texture.Data.Length != bytes)
                   throw new InvalidOperationException("Decoded GLB image dimensions/format differ from its header.");
               return new(width, height, texture.Data.ToArray());
           }
           finally { TextureLoader.Unload(texture); }
       }
   ```

3. **Upload as a private texture.** Create a `SimpleVkTexture` (`R8G8B8A8UNorm`, full mip chain), record `UploadData` for one mip level so the game generates the rest on the GPU, submit and wait on a fence, then add the image view to the bindless library and write the slot into the inherited `BindlessHandle` through its private setter:

   ```csharp
   // pebbles.lib/Assets/GlbTextures.cs L68-L99
   /// <summary>Owns one private image and bindless slot; never registers in ModLibrary.</summary>
   internal sealed class GlbTexture : TextureReference
   {
       private static readonly PropertyInfo HandleProperty = typeof(TextureReference).GetProperty(nameof(BindlessHandle), BindingFlags.Public | BindingFlags.Instance)
           ?? throw new MissingMemberException(typeof(TextureReference).FullName, nameof(BindlessHandle));
       internal Core.Renderer Owner { get; private set; } = null!;
       private RenderCore.Systems.BindlessTextureLibrary? _bindless;
       private int _ownedHandle;
       private bool _released;

       internal static GlbTexture Upload(string id, GlbPixels pixels)
       {
           var renderer = Program.GetRenderer();
           var result = new GlbTexture { Owner = renderer, Id = id, Width = pixels.Width, Height = pixels.Height };
           result.SetHash();
           try
           {
               result.Texture = new SimpleVkTexture(id, renderer.Allocator, pixels.Width, pixels.Height, 1,
                   VkFormat.R8G8B8A8UNorm, SimpleVkTexture.CalculateMaxMipLevels(pixels.Width, pixels.Height));
               result.ImageView = result.Texture.ImageView;
               using (var submission = new AssetUploadSubmission(renderer))
               {
                   result.Texture.UploadData(submission.Staging, submission.Command, pixels.Data.AsSpan(), [pixels.Data.Length]);
                   submission.SubmitAndWait();
               }
               result._bindless = Program.Instance.BindlessTextures;
               result._ownedHandle = result._bindless.AddTexture(result.ImageView);
               HandleProperty.SetValue(result, result._ownedHandle);
               return result;
           }
           catch { result.Release(); throw; }
       }
   // ...
   ```

4. **Release by hand.** Free the slot and the image directly; never call `TextureReference.Dispose`, which expects a game-owned `TextureAsset`:

   ```csharp
   // pebbles.lib/Assets/GlbTextures.cs L101-L108
       internal void Release()
       {
           if (_released) return;
           _released = true;
           if (_ownedHandle != 0) _bindless!.FreeTexture(_ownedHandle);
           _ownedHandle = 0;
           Texture?.Dispose();
       }
   ```

5. **Upload lazily, and own the device wait.** CPU recipes are produced when a mesh is selected; `ResolveTexture` (the GPU upload) runs only on an explicit preview refresh, a clutter Apply or a placement. `Dispose` first waits on the device of every renderer that uploaded a texture, then frees each slot once:

   ```csharp
   // pebbles.lib/Assets/GlbMaterials.cs L22-L52
       /// <summary>Call only from explicit import/preview/apply processing outside GUI rendering.</summary>
       public TextureReference ResolveTexture(string id)
       {
           if (_textures.TryGetValue(id, out var existing)) return existing;
           if (!_reader.Pixels.TryGetValue(id, out var pixels))
           {
               var prefix = _sourceKey + "/material/";
               if (!id.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException("Texture belongs to another GLB source.");
               var remainder = id[prefix.Length..]; var slash = remainder.IndexOf('/');
               if (slash < 1 || !int.TryParse(remainder[..slash], out var index)) throw new InvalidOperationException("Invalid GLB texture identity.");
               GetMaterial(index);
               if (!_reader.Pixels.TryGetValue(id, out pixels)) throw new InvalidOperationException("GLB texture channel does not exist.");
           }
           var texture = GlbTexture.Upload(id, pixels);
           _textures.Add(id, texture);
           return texture;
       }

       /// <summary>
       /// Owner must first retire all live graphs/previews. Wait for each actual owning device
       /// before recycling descriptors/images. CPU-only sources never query or touch the renderer.
       /// </summary>
       public void Dispose()
       {
           // Wait before mutating any cache state, so a failed wait preserves every owned handle.
           // Remember the renderer that allocated each image rather than looking up a replacement.
           foreach (var renderer in _textures.Values.Select(t => t.Owner).Distinct()) renderer.Device.WaitIdle();
           foreach (var texture in _textures.Values) texture.Release();
           _textures.Clear();
           _reader = new(_document, _sourceKey, GlbPixels.Decode);
       }
   ```

6. **Warn instead of failing.** Known appearance extensions (specular, IOR, clearcoat, sheen, anisotropy, iridescence, transmission, volume, dispersion, unlit) are accepted and reported as omitted effects; emissive glow is dropped with a warning; clamp and mirrored wrap fall back to repeat. Anything else is an error that names the extension
   ([pebbles.lib/Models/GlbCompatibility.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/GlbCompatibility.cs)).

## Complete example

- [pebbles.lib/Models/GlbMaterialReader.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/GlbMaterialReader.cs), [pebbles.lib/Models/GlbPixels.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/GlbPixels.cs), [pebbles.lib/Assets/GlbTextures.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Assets/GlbTextures.cs), [pebbles.lib/Assets/GlbMaterials.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Assets/GlbMaterials.cs)
- [pebbles.lib/Assets/AssetUploadSubmission.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Assets/AssetUploadSubmission.cs) (the private command buffer and fence)

## Variations and gotchas

- **Conventions belong to the consumer.** The diffuse-alpha-as-tint rule is Pebbles' clutter shader; a part material needs real PBR channels with all of Diffuse, Normal and AoRoughMetal ([Declare glTF meshes and PBR materials in asset XML and author their texture sets](../declare-gltf-and-pbr-material-assets/overview.md)).
- **Never touch Vulkan from the wrong moment.** Do not create or destroy images while the frame's ImGui list may reference them; use a window you control (before the GUI, or at solver dispatch).
- **Free in the right order.** Retire all borrowers first, then wait for the device, then free the bindless slot and the image ([Free mod-owned Vulkan resources without device loss](../../../rendering/vulkan-passes/free-vulkan-resources-safely/overview.md)).
- **Detail maps** must share the main texture's UV set and transform (one UV stream); otherwise the detail map is skipped with a warning.
- **Generated images are capped** (256 MiB of CPU pixels per source).

## See also

- [Parse a GLB with a bounded reader into unregistered CPU meshes with path-plus-hash ids](import-glb-models-at-runtime-1.md), [Enumerate the game's loaded meshes, textures and glTFs for pickers and id checks](import-glb-models-at-runtime-3.md), [Upload an image or procedural pixels as a GPU texture](../../../rendering/textures-and-decals/upload-an-image-as-a-vulkan-texture/overview.md), [Free mod-owned Vulkan resources without device loss](../../../rendering/vulkan-passes/free-vulkan-resources-safely/overview.md),
  [Reserve or grow game GPU buffers for content added at runtime](../../../rendering/meshes/grow-game-gpu-buffers-for-runtime-content/overview.md)
- [GLB PBR materials converted to native clutter maps with private bindless textures](../../../../mods/unscience/features/pebbles-glb-material-conversion.md)
