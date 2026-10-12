---
type: Mod Feature
title: GLB PBR materials converted to native clutter maps with private bindless textures
description: Pebbles bakes glTF metallic-roughness materials and common Blender extensions into native diffuse, packed AO-roughness-metal, normal and cutout maps on the CPU, then uploads each as a private SimpleVkTexture with its own bindless slot set by reflection
mod: unscience
submod: pebbles
category: assets
techniques: [glb-model, vulkan-texture, reflection]
hooks:
  - reflection KSA.TextureReference.BindlessHandle (private setter)
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent and RenderFloatingWindows; CPU conversion when a mesh is selected)
  - starmap StarMapBeforeGui (via unscience ISubmod.Update; GPU upload on Workshop preview refresh and Sphinx placement)
  - harmony-prefix KSA.Universe.ExecuteNextClothSolvers (GPU upload during a clutter Apply)
lifecycle: [frame:draw-ui, frame:dispatch-solvers]
game_types:
  - KSA.TextureReference
  - RenderCore.SimpleVkTexture
  - RenderCore.Systems.BindlessTextureLibrary
  - Brutal.TextureApi.TextureLoader
  - KSA.Program
  - Core.Renderer
tags: [glb, gltf, pbr, materials, textures, bindless, srgb, alpha-cutout, reflection]
---

KSA's clutter and static-object shaders do not read glTF materials; they expect a fixed set of
bindless textures with their own conventions (a diffuse map whose alpha means "terrain tint", a
packed AO/roughness/metal map, a tangent-free normal map, a red-channel opacity map). Pebbles
converts each imported glTF material into exactly that set on the CPU, records lossy fallbacks as
user-visible warnings, and uploads the generated images as private GPU textures that never enter
the game's asset library. Sphinx reuses the same conversion and the same textures.[^scope-glb-materials]

## How it works

**Detached conversion (`GlbMaterialReader`).** For each glTF material index (or -1 for the default
material) the reader:

| Input | Output |
|---|---|
| `baseColorTexture` x `baseColorFactor` (or legacy `KHR_materials_pbrSpecularGlossiness` diffuse) | Diffuse: decoded to linear, multiplied by the factor, re-encoded as sRGB, alpha forced to 255 |
| `occlusionTexture` (strength), `metallicRoughnessTexture`, `metallicFactor`, `roughnessFactor` | One packed map: R = AO, G = roughness, B = metallic, nearest-resampled to the larger source size; missing channels use glTF defaults; legacy materials get metallic 0 and roughness `1 - glossinessFactor` |
| `normalTexture` (scale, capped at 100) | Normal map rescaled in X/Y and renormalised |
| `alphaMode` MASK or BLEND, `alphaCutoff`, base alpha x factor | Separate opacity map, 255 or 0 at the cutoff; BLEND becomes a fixed 0.5 cutout |
| `doubleSided` | Material flag |

Every material is marked `SourceColors = true` so the clutter shader shows its true colors (see
[private clutter materials](./pebbles-private-clutter-materials.md)). Detail maps must use the same
UV set and texture transform as the main texture (the native vertex has one UV stream); otherwise
that detail map is skipped with a warning while the main color stays. Generated images are capped
at 256 MiB of CPU pixels per source.[^glb-material-reader][^glb-pixels]

**Compatibility policy.** Known appearance extensions (specular, IOR, clearcoat, sheen, anisotropy,
iridescence, transmission, volume, dispersion, unlit, emissive strength, legacy
spec-gloss) are accepted even when required and reported as omitted effects; emissive glow is
dropped with a warning. `KHR_texture_transform` is honoured. `EXT_texture_webp` and
`KHR_texture_basisu` are accepted only when the texture also names a PNG/JPEG `source`. Clamp and
mirrored wrap modes fall back to repeat with a warning. Anything else is an error that names the
extension.[^glb-compatibility][^glb-texture-mapping]

**Bounded decode.** `GlbPixels.Decode` reads width and height from the PNG `IHDR` or the JPEG `SOFn`
marker first and rejects anything over 4096 pixels per side before calling the game's native stb
decoder (`TextureLoader.LoadFromMemory` with `ForceRgba8`), then checks that the decoded extent and
byte count match the header, copies the pixels and unloads the native image.[^ksa-texture-loader]

**Private GPU texture (`GlbTexture : TextureReference`).** `Upload` creates a `SimpleVkTexture`
(`R8G8B8A8UNorm`, full mip chain), records `UploadData` (one mip level, so the game generates the
rest on the GPU) into an [asset upload submission](./pebbles-asset-upload-submission.md), submits
and waits on its fence, and only then calls `Program.Instance.BindlessTextures.AddTexture(imageView)`
and writes the returned slot into the inherited `BindlessHandle` through its private setter. Shaders
then index it like any game texture. `Release` frees the bindless slot and the image directly; it
never calls `TextureReference.Dispose`, which expects a game-owned `TextureAsset`.[^glb-textures][^ksa-texture-reference][^ksa-simple-vk-texture]

**Lazy upload and ownership (`GlbMaterials`).** CPU recipes are produced as soon as a mesh is
selected; `ResolveTexture` (the GPU upload) runs only on an explicit preview refresh, a clutter
Apply or a Sphinx placement. `Dispose` first waits on the device of every renderer that actually
uploaded a texture, then frees each slot once; a source that was never uploaded never touches the
renderer.[^glb-materials]

**PNG overrides.** `ImportedPngTexture` reuses the same decoder and uploader for Sphinx's shared PNG
catalog: a color map plus, only if any pixel has alpha below 255, a 0.5-cutoff opacity map; files
over 64 MiB are refused.[^imported-png-texture]

## Lifecycle and timing

| Step | Where | Thread / frame point |
|---|---|---|
| CPU conversion (`GetMaterial`) | `PebblesSubmod.SelectMesh` from UI, `ClutterAssets.GlbMaterials` | Main thread, `frame:draw-ui` (StarMapAfterGui) |
| GPU upload (`ResolveTexture` -> `GlbTexture.Upload`) | Workshop preview refresh (`ISubmod.Update`, StarMapBeforeGui), Sphinx queued placement (`Update`), clutter Apply (`ExecuteNextClothSolvers` prefix) | Main thread, outside ImGui draw recording; blocking submit + fence wait |
| Release | Deferred `ReleaseGlbImports` in `Update` after all borrowers retired; unload | Main thread, after `Device.WaitIdle()` |

The upload is kept out of ImGui emission on purpose: a texture must not be destroyed or replaced
after this frame's ImGui list may reference it, and the private command buffer is submitted
immediately rather than joining the frame's command buffer.[^pebbles-submod-ui]

## Key code

| Path | Role |
|---|---|
| [pebbles.lib/Models/GlbMaterialReader.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/GlbMaterialReader.cs) | glTF material to recipe plus generated CPU images |
| [pebbles.lib/Models/GlbPixels.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/GlbPixels.cs) | Pure pixel conversions (diffuse, PBR pack, normal, opacity) |
| [pebbles.lib/Models/GlbCompatibility.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Models/GlbCompatibility.cs) | Extension fallbacks and errors |
| [pebbles.lib/Assets/GlbTextures.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Assets/GlbTextures.cs) | Bounded native decode, private texture and bindless slot |
| [pebbles.lib/Assets/GlbMaterials.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Assets/GlbMaterials.cs) | Lazy upload cache and device-aware disposal |
| [pebbles.lib/Assets/ImportedPngTexture.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Assets/ImportedPngTexture.cs) | PNG color/cutout override built on the same path |
| [pebbles.tests/GlbPixelChecks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.tests/GlbPixelChecks.cs), [pebbles.tests/GlbTextureChecks.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.tests/GlbTextureChecks.cs) | Managed checks with an injected decoder |

## Snippets

Diffuse and PBR packing (linear-space factor multiply, alpha reserved for the clutter convention):

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

Bounding the native decoder with the encoded header:

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

Private texture plus bindless slot, written into `TextureReference.BindlessHandle` by reflection:

```csharp
// pebbles.lib/Assets/GlbTextures.cs L68-L108
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

    internal void Release()
    {
        if (_released) return;
        _released = true;
        if (_ownedHandle != 0) _bindless!.FreeTexture(_ownedHandle);
        _ownedHandle = 0;
        Texture?.Dispose();
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `TextureReference.Texture`, `ImageView` (protected setters), `BindlessHandle` (private setter), `Dispose(Device)` | [current/decomp/KSA/TextureReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/TextureReference.cs#L64-L82) | Base class of the private texture; stock dispose avoided |
| `SimpleVkTexture(string, IImageAllocator, int, int, int, VkFormat, int, ...)`, `UploadData`, `CalculateMaxMipLevels` | [current/decomp/RenderCore/SimpleVkTexture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/SimpleVkTexture.cs#L170-L405) | Image allocation; single-level upload triggers GPU mip generation (L380-L383) |
| `BindlessTextureLibrary.AddTexture`, `FreeTexture` | [current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs#L186-L212) | Shader-visible slot |
| `Program.BindlessTextures` (created with capacity 1024) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L830) | The library the slots come from |
| `TextureLoader.LoadFromMemory`, `Unload`, `FormatType.Png/Jpg` | [current/decomp/Brutal.TextureApi/TextureLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.TextureApi/TextureLoader.cs#L135) | Native PNG/JPEG decode |

## Pitfalls and version notes

- **Bindless capacity.** The game's bindless library is created with 1024 slots
  (`Program.cs` L830); every converted material uses up to four. Many imported multi-material GLBs
  (plus Graffiti/Thug Life textures from other sub-mods) share that pool (capacity exhaustion
  behavior not verified).
- **Reflection on `BindlessHandle`.** The property has a public getter and a private setter; a game
  change to a field or `init` breaks the import at runtime.
- **Clutter diffuse alpha is not transparency.** That is why opacity is baked into a separate
  red-channel map and diffuse alpha is forced to 255.
- **Not real glTF shading.** Transmission renders solid, unlit gets normal lighting, emissive is
  dropped, BLEND is a cutout, vertex colors and authored tangents are ignored (the shader derives
  tangents from screen derivatives). The authors call it a bounded conversion, not parity.
- **WebP/KTX2-only textures** fail with a message naming the missing decoder.
- Managed checks inject a synthetic decoder, so real PNG/JPEG decode, bindless recycling and GPU
  synchronization are only covered by in-game acceptance.[^glb-pixel-checks][^glb-texture-checks]

## See also

- [Bounded GLB import](./pebbles-glb-import.md)
- [Asset upload submission](./pebbles-asset-upload-submission.md)
- [Private clutter material buffer and source-color shader](./pebbles-private-clutter-materials.md)
- [Sphinx UV mapping and PNG texture overrides](./sphinx-uv-texture-overrides.md)
- [Pebbles sub-mod](../submods/pebbles.md) and [mod overview](../overview.md)
- [Convert glTF materials on the CPU into game maps and upload them as private bindless textures](../../../howto/assets/models-and-textures/import-glb-models-at-runtime/import-glb-models-at-runtime-2.md) - how-to article demonstrating this feature

[^scope-glb-materials]: scope/ground-clutter-glb-materials.md (authors' conversion notes)
[^glb-material-reader]: pebbles.lib/Models/GlbMaterialReader.cs
[^glb-pixels]: pebbles.lib/Models/GlbPixels.cs
[^glb-compatibility]: pebbles.lib/Models/GlbCompatibility.cs
[^glb-texture-mapping]: pebbles.lib/Models/GlbTextureMapping.cs
[^ksa-texture-loader]: Brutal.TextureApi.TextureLoader (decompiled, build 2026.10.10.5554)
[^glb-textures]: pebbles.lib/Assets/GlbTextures.cs
[^ksa-texture-reference]: KSA.TextureReference (decompiled, build 2026.10.10.5554)
[^ksa-simple-vk-texture]: RenderCore.SimpleVkTexture (decompiled, build 2026.10.10.5554)
[^glb-materials]: pebbles.lib/Assets/GlbMaterials.cs
[^imported-png-texture]: pebbles.lib/Assets/ImportedPngTexture.cs
[^pebbles-submod-ui]: pebbles.lib/PebblesSubmod.Ui.cs
[^glb-pixel-checks]: pebbles.tests/GlbPixelChecks.cs
[^glb-texture-checks]: pebbles.tests/GlbTextureChecks.cs
