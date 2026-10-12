---
type: How-To
title: Python asset scripts with encodings matched to the shader that reads them
description: Build cloud coverage, detail and flowmap textures offline with scripts whose channel layouts follow the stock cloud shader, check the scripts in and keep large derived textures out of the repository
category: celestials
group: atmospheres-and-rings
topic: define-atmospheres-and-cloud-layers-in-system-xml
approach: 3
difficulty: intermediate
prerequisites: [python, texture-compression-tool]
demonstrated_by:
  - mods/realatmospheres/features/texture-and-phase-asset-scripts
  - mods/realatmospheres/features/cloud-layers-in-system-xml
techniques: [xml-asset]
hooks: []
lifecycle: [startup:asset-load]
game_types: [KSA.VolumetricCloudReference, KSA.TextureReference]
tags: [clouds, textures, offline, python, flowmap, bc3, bc5]
---
Outcome: the coverage mask, detail tile and flowmap textures that [approach 2](define-atmospheres-and-cloud-layers-in-system-xml-2.md) points at, produced reproducibly from source maps. Demonstrated by Real Atmospheres ([Texture and phase asset scripts](../../../../mods/realatmospheres/features/texture-and-phase-asset-scripts.md)). Nothing here runs in the game: at launch the mod only checks that each asset exists before inserting the XML that uses it.

## Background: how the game does it

The cloud shader decides what each texture channel means. `GetCoverageAndCloudType` ([current/Content/Core/Shaders/Clouds/CloudFunctions.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Clouds/CloudFunctions.glsl#L72-L134)) reads the global map's **alpha** as coverage (red when the map is flagged single-channel), a detail selector, and a detail texture holding two tiles: **RG** is tile A (coverage, type) and **BA** is tile B, blended by the selector; the type comes from the green channel of the blend. A mask must therefore put coverage in alpha and, when the type should be read off the map, the type in red with a detail tile whose two types are the ends of the blend.

## Lifecycle and timing

None in the game; build time only (a Python script run on the author's machine). The outputs load as ordinary texture assets at `startup:asset-load`. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Check the layout in the shader first**: [current/Content/Core/Shaders/Clouds/CloudFunctions.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Clouds/CloudFunctions.glsl#L72-L134). Write down which channel is coverage and which is type.

2. **Write coverage into alpha and the type into red** in a 4096 x 2048 equirect, derived from the planet's own banding so bright bands become high cloud types; compress with BC3 and no mipmaps:

```py
# make_giant_clouds.py L95-L109
# Type 0 is the tall deck and 1 the low one, so invert brightness: the bright
# cloud rides above the dark lanes, and the relief follows what you can see.
ctype = np.clip(TYPE_GAIN * (1.0 - luma)
                + TYPE_NOISE * (periodic_fbm(W, 1.7, seed=91)[:H] - 0.5) * 2.0, 0.0, 1.0)

out = np.empty((H, W, 4), np.uint8)
out[..., 0] = (ctype * 255).astype(np.uint8)   # cloud type -> how high a column builds
out[..., 1] = 255                              # unused by the shader
out[..., 2] = 255                              # unused by the shader
out[..., 3] = np.asarray(a8)                   # coverage
tmp = os.path.join(OUT, "_gcloud_tmp.png")
Image.fromarray(out, "RGBA").save(tmp)
subprocess.run([NVTT, "-f", "bc3", "--no-mips", "-o",
                os.path.join(OUT, out_base + ".dds"), tmp], check=True)
os.remove(tmp)
```

3. **Emit the detail tile as a flat pair** so nothing about the deck's height comes from a texture that tiles: coverage 1 in both tiles, tall type at one end and low at the other:

```py
# make_giant_clouds.py L114-L127
# The tiled detail texture: RG is tile A (coverage, type), BA is tile B. Coverage
# stays 1 in both, so the global mask owns coverage, as Jupiter's own detail does.
# The two types are the ends of the blend the mask's red channel runs between, flat
# so that nothing about the deck's height comes from a texture that tiles.
detail = np.empty((DETAIL_N, DETAIL_N, 4), np.uint8)
detail[..., 0] = 255                                                   # tile A coverage
detail[..., 1] = 0                                                     # tile A type: tall
detail[..., 2] = 255                                                   # tile B coverage
detail[..., 3] = 255                                                   # tile B type: low
tmp = os.path.join(OUT, "_gdetail_tmp.png")
Image.fromarray(detail, "RGBA").save(tmp)
subprocess.run([NVTT, "-f", "bc3", "--no-mips", "-o",
                os.path.join(OUT, "GiantCloudDetail.dds"), tmp], check=True)
os.remove(tmp)
```

4. **Match the flowmap encoding to stock Jupiter's** (BC5; zonal jets from band brightness, eddies and Neptune's dark spot) so the shader decodes it the way it decodes the stock map. The flow speed in the XML is then `peak flow x displacement / loop duration` ([approach 2](define-atmospheres-and-cloud-layers-in-system-xml-2.md)).

5. **Keep derived assets out of git, scripts in.** `assets/` is git-ignored because the files are large and some are third-party derivatives whose terms are tracked in `CREDITS.md`; `release-files.txt` lists which outputs ship.

6. **Fix a hole in a stock tile with a one-liner script.** The Venus detail tile lifts coverage from 0 to 1 to 0.7 to 1 so the deck has no holes:

```py
# make_venus_detail.py L22-L30
SOURCE = r"C:\Program Files\Kitten Space Agency\Content\Core\Textures\Clouds\CloudDetail2048.png"
OUT = os.path.join(HERE, "assets", "VenusCloudDetail.png")
FLOOR = 0.7                    # lowest coverage anywhere: above 0.65 the noise cannot open the base

tile = np.asarray(Image.open(SOURCE).convert("RGBA")).astype(np.float64) / 255.0
out = tile.copy()
for ch in (0, 2):                                        # coverage of tiles A and B
    out[..., ch] = FLOOR + (1.0 - FLOOR) * tile[..., ch]
Image.fromarray(np.round(out * 255.0).astype(np.uint8), "RGBA").save(OUT, optimize=True)
```

## Complete example

| File | Role |
|---|---|
| [make_giant_clouds.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_giant_clouds.py) | Masks and the detail tile |
| [make_giant_flowmaps.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_giant_flowmaps.py) | Flowmaps from the same source maps |
| [make_venus_detail.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_venus_detail.py) | The smallest complete example |
| [release-files.txt](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/release-files.txt) | Shipped asset list |
| [CREDITS.md](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/CREDITS.md) | Source and terms of every shipped file |

## Variations and gotchas

- **Windows-only tool paths.** NVIDIA Texture Tools and the game install are hard-coded; the giant surface script imports a build tool from the author's documents folder.
- **Stale constant.** `make_giant_flowmaps.py` scales eddies by a per-planet table whose comment says `ShadowBuilder` sets those values, but `ShadowBuilder` now uses 700 km volumetric and 4,000 km 2D; regenerating the flowmaps would use the old scaling.
- **Licences travel with the files.** The release is non-commercial because of the Uranus map.
- **Derive from the player's install** when you cannot redistribute a modified game texture (build it at launch instead).

## See also

- [Overview](overview.md); [approach 1](define-atmospheres-and-cloud-layers-in-system-xml-1.md); [approach 2](define-atmospheres-and-cloud-layers-in-system-xml-2.md)
- [Texture and phase asset scripts](../../../../mods/realatmospheres/features/texture-and-phase-asset-scripts.md), [Cloud layers in system XML](../../../../mods/realatmospheres/features/cloud-layers-in-system-xml.md)
- [Composite a PNG over a stock texture](../../../assets/models-and-textures/composite-png-over-stock-texture/overview.md), [Extend stock sky and cloud shaders](../../../shaders/stock-shaders/extend-stock-sky-and-cloud-shaders/overview.md)
