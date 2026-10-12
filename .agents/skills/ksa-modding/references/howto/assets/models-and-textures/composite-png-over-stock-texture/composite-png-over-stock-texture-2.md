---
type: How-To
title: Write a modified copy of a stock texture with an offline script
description: Read a texture from the player's install with Pillow and numpy, transform it, write a PNG into the mod's assets folder, ship only files named in a release list, and consider deriving it at launch where the original cannot be redistributed
category: assets
group: models-and-textures
topic: composite-png-over-stock-texture
approach: 2
difficulty: intermediate
prerequisites: []
demonstrated_by: [mods/realatmospheres/features/texture-and-phase-asset-scripts]
techniques: [texture-pipeline, xml-asset]
hooks: [offline python make_venus_detail.py]
lifecycle: [startup:asset-load]
game_types:
  - KSA.TextureReference
  - KSA.GpuTextureSystem
  - KSA.GpuTextureAssetRef
  - KSA.AssetManager
  - Brutal.TextureApi.TextureLoader
  - Brutal.TextureApi.Abstractions.GenericTexture
  - RenderCore.TextureAsset
tags: [texture, offline, pillow, numpy, release-list, redistribution, cloud-detail]
---

Sometimes the right change to a stock texture is a data transform, not a decal: a lifted channel, a swapped mask, a re-encoded flowmap. Real Atmospheres does it offline. `make_venus_detail.py` reads the game's own `CloudDetail2048.png` from the player's install, lifts its coverage channels and writes `VenusCloudDetail.png`
to the mod's `assets` folder; a release list names exactly which outputs ship. The result is an ordinary texture that the XML references by absolute path ([Resolve asset ids and file paths correctly across mods](../../asset-xml/resolve-asset-ids-and-mod-relative-paths/overview.md)). This article covers the script, the shipping list and the redistribution caveat.

Demonstrated by [Texture and phase asset scripts](../../../../mods/realatmospheres/features/texture-and-phase-asset-scripts.md). The runtime alternative is [Blend a PNG decal over a transcoded stock texture on the CPU and register it with GpuTextureSystem](composite-png-over-stock-texture-1.md).

## Background: how the game does it

The cloud detail tile is a plain PNG in Core (`Content/Core/Textures/Clouds/CloudDetail2048.png`), whose RG channels hold coverage and type of tile A, BA of tile B; the shader fills where `coverage + shape - 1 > 0` and noise can only open the cloud where that is below 0.65
(the mod's analysis of `GetCoverageAndCloudType` in `Core/Shaders/Clouds/CloudFunctions.glsl`). Since it is a PNG, Pillow reads it directly; vessel textures in KTX2 would need a transcode ([Blend a PNG decal over a transcoded stock texture on the CPU and register it with GpuTextureSystem](composite-png-over-stock-texture-1.md)). The stock Venus layer references it by `Id` and the mod swaps it for its own
file in the shadow copy of the system XML ([Redirect the game to patched copies of its XML and coordinate edits between mods](../../xml-patching/redirect-game-xml-loading/overview.md), [Define atmospheres, scattering and cloud layers for a body in system XML](../../../celestials/atmospheres-and-rings/define-atmospheres-and-cloud-layers-in-system-xml/overview.md)).

## Lifecycle and timing

Nothing runs in the game. The script runs at development time; at launch the mod only checks whether each asset exists before inserting the XML that uses it. The texture then loads as an ordinary file at `startup:asset-load`. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Read the stock file from the install, as 0 to 1 floats, and transform it.** The whole transform is a linear lift of two channels; cloud types in the other channels are untouched:

   ```python
   # make_venus_detail.py L22-L32
   SOURCE = r"C:\Program Files\Kitten Space Agency\Content\Core\Textures\Clouds\CloudDetail2048.png"
   OUT = os.path.join(HERE, "assets", "VenusCloudDetail.png")
   FLOOR = 0.7                    # lowest coverage anywhere: above 0.65 the noise cannot open the base

   tile = np.asarray(Image.open(SOURCE).convert("RGBA")).astype(np.float64) / 255.0
   out = tile.copy()
   for ch in (0, 2):                                        # coverage of tiles A and B
       out[..., ch] = FLOOR + (1.0 - FLOOR) * tile[..., ch]
   Image.fromarray(np.round(out * 255.0).astype(np.uint8), "RGBA").save(OUT, optimize=True)

   b = out[..., 2]
   ```

2. **Write into the mod's `assets/` folder** (ignored by git: the files are large and several are third-party derivatives). Print what changed so a rerun is checkable; the script's docstring records the shader analysis that justifies the numbers.

3. **List what ships.** One manifest read by both the packager and the local deploy script; anything in `assets/` not listed is a converter source or stale output and never ships:

   ```text
   # release-files.txt L1-L10
   # Assets that ship with Real Atmospheres (filenames inside assets/).
   # Read by BOTH package.ps1 (player zip) and deploy.ps1 (local install), so they always match.
   # Anything in assets/ that is NOT listed here is a converter source or a stale output and never ships.

   # Mars: cirrus layer + exact dust phase LUT (delete the LUT to fall back to the embedded HG fit)
   MarsCirrusMaskVolumetric.dds
   MarsCirrusMask2D.png
   MarsDustPhaseLut.glsl

   # Venus: the game's Earth cumulus detail tile with its coverage lifted, so the deck has no holes (make_venus_detail.py)
   ```

4. **Declare the file in XML by absolute path, gated on existence**, so deleting the PNG reverts just that feature (see [Edit shadow copies with anchor-verified text patches and generated XML between markers](../../xml-patching/redirect-game-xml-loading/redirect-game-xml-loading-3.md)).

5. **Decide redistribution before you ship.** A modified copy of game content may not be redistributable. Real Atmospheres' credits file flags `VenusCloudDetail.png` as "a modified copy of Kitten Space Agency content: confirm the game's terms allow redistributing it, or build it at launch from the player's install instead":

   ```text
   # CREDITS.md L47-L47
   | `VenusCloudDetail.png` | The game's own `Content/Core/Textures/Clouds/CloudDetail2048.png` with its coverage channels lifted by `make_venus_detail.py` | A modified copy of Kitten Space Agency content: confirm the game's terms allow redistributing it, or build it at launch from the player's install instead |
   ```

   Building it at launch means running the same transform in C# from the player's `Content/Core` at `StarMapBeforeMain`, before the XML is written.

## Complete example

- [make_venus_detail.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_venus_detail.py) (the smallest complete example), [release-files.txt](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/release-files.txt), [CREDITS.md](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/CREDITS.md)
- More converters in the same repo: [make_jupiter_mask.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_jupiter_mask.py) (swaps channels of the game's `JupiterMask.dds`), [make_giant_flowmaps.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_giant_flowmaps.py)

## Variations and gotchas

- **The source path is machine specific.** The script hard-codes a Windows install path; read it from an environment variable in your own tooling.
- **Encode for the shader you feed.** The giant masks put coverage in alpha and cloud type in red because that is what the shader reads.
- **Emit data as source where you can.** The Mars phase table is a GLSL `const` array spliced into the shader, so no new texture binding is needed.
- **DDS and KTX2 outputs** need their own encoders; PNG avoids that (PNG is accepted in every material slot).

## See also

- [Blend a PNG decal over a transcoded stock texture on the CPU and register it with GpuTextureSystem](composite-png-over-stock-texture-1.md), [Replace a game texture or model texture with a user PNG at runtime](../../../rendering/textures-and-decals/replace-a-game-texture-at-runtime/overview.md), [Define atmospheres, scattering and cloud layers for a body in system XML](../../../celestials/atmospheres-and-rings/define-atmospheres-and-cloud-layers-in-system-xml/overview.md),
  [Declare glTF meshes and PBR materials in asset XML and author their texture sets](../declare-gltf-and-pbr-material-assets/overview.md), [Redirect the game to patched copies of its XML and coordinate edits between mods](../../xml-patching/redirect-game-xml-loading/overview.md)
- [Texture and phase asset scripts](../../../../mods/realatmospheres/features/texture-and-phase-asset-scripts.md)
