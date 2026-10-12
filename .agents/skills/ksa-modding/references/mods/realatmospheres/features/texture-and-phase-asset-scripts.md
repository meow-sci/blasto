---
type: Mod Feature
title: Texture and phase asset scripts
description: Python scripts build every shipped asset (cloud masks, flowmaps, cubemaps, modified stock textures and a Mie phase LUT emitted as GLSL) from source maps, game textures and Mie theory
mod: realatmospheres
category: assets
techniques: [code-generation, texture-pipeline]
hooks: []
lifecycle: []
game_types: []
tags: [assets, textures, dds, ktx2, python, numpy, nvtt, flowmap, cloud-mask, mie]
---

The repository does not contain the textures it ships: `assets/` is ignored because the files are large
and several are third-party derivatives whose terms are tracked in `CREDITS.md`
([.gitignore](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/.gitignore#L9-L13), [CREDITS.md](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/CREDITS.md#L1-L40)). What is checked in is the set
of scripts that make them and the list of which outputs ship
([release-files.txt](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/release-files.txt)).[^ra-release-files-txt]

## How it works

| Script | Inputs | Outputs (shipped) | Notes |
|---|---|---|---|
| [mars_dust_mie.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/mars_dust_mie.py#L1-L18) + [mie.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/mie.py) | Feldspar/hematite refractive indices, bimodal log-normal sizes, 680/550/440 nm | `mars_dust_phase.glsl` (level-1 three-lobe HG fit; constants copied into `Payloads.cs`) | Not shipped as a file |
| [make_mars_phase_lut.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_mars_phase_lut.py#L1-L10) | Same Mie model | `MarsDustPhaseLut.glsl`: 361 `vec3` samples plus a lookup function, emitted as GLSL source | Shader code delivered as an asset; deleting it falls back to level 1 |
| [make_mars_cirrus.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_mars_cirrus.py#L1-L40) | The author's SpaceEngine PRO export | `MarsCirrusMaskVolumetric.dds` (BC3), `MarsCirrusMask2D.png` | Coverage in alpha, faint 2D opacity, pre-blurred edges |
| [convert_titan_haze_maps.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/convert_titan_haze_maps.py#L1-L17) | SpaceEngine exports | Titan upper/lower haze masks, Titan cloud masks, Triton cloud masks (volumetric DDS + 2D PNG) | Coverage from luminance or alpha per job |
| [make_giant_clouds.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_giant_clouds.py#L1-L31) | Saturn/Uranus/Neptune equirect maps | `<Body>CloudsMask.dds` (alpha coverage, red cloud type), `GiantCloudDetail.dds` | Bright bands become high cloud types |
| [make_giant_flowmaps.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_giant_flowmaps.py#L1-L12) | Same maps | `<Body>Flowmap.dds` (BC5) | Zonal jets from band brightness, eddies, Neptune's dark spot; same encoding as stock Jupiter's flowmap |
| [make_giant_surfaces.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_giant_surfaces.py#L1-L6) | Same maps | `<Body>Diffuse.ktx2` (2048 per face) | Imports `build_maps.py` from a Proxima Centauri mod's build tools on the author's machine |
| [make_jupiter_mask.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_jupiter_mask.py#L1-L41) | The game's `JupiterMask.dds` | `JupiterCloudsMask.dds` (BC4, 8192 x 4096) | Grey and white swapped, vortex discs left low |
| [make_venus_detail.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_venus_detail.py#L1-L15) | The game's `CloudDetail2048.png` | `VenusCloudDetail.png` | Coverage lifted from 0-1 to 0.7-1 so the deck has no holes |

Patterns worth reusing:

- **Encode for the shader you feed.** The giant masks put coverage in alpha and the cloud type in red
  because that is what `GetCoverageAndCloudType` reads, and the companion detail tile is reduced to the
  two endpoints the blend needs ([make_giant_clouds.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_giant_clouds.py#L6-L31)).
- **Derive from the player's install when you cannot redistribute.** Two assets are modified copies of
  game textures; `CREDITS.md` flags them as "to confirm" and suggests building them at launch from the
  player's install instead ([CREDITS.md](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/CREDITS.md#L43-L48)).
- **Emit data as source.** The phase LUT is a GLSL `const` array spliced into the shader, so no new
  texture binding or descriptor is needed ([make_mars_phase_lut.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_mars_phase_lut.py#L96-L134)).
- **One manifest for both packaging and dev deploy.** `release-files.txt` lists asset names;
  anything else in `assets/` is a source or stale output and never ships
  ([release-files.txt](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/release-files.txt#L1-L3)).

## Lifecycle and timing

None in the game. At launch the mod only checks whether each asset exists before inserting the XML
or shader code that uses it (see [Cloud layers in the system XML](cloud-layers-in-system-xml.md)).

## Key code

| Path | Role |
|---|---|
| [release-files.txt](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/release-files.txt) | Shipped asset list |
| [make_venus_detail.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_venus_detail.py#L22-L30) | The smallest complete example |
| [make_mars_phase_lut.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_mars_phase_lut.py#L81-L134) | Mie phase per channel and GLSL emission |
| [CREDITS.md](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/CREDITS.md) | Source and terms of every shipped file |

## Snippets

```python
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

```python
# make_mars_phase_lut.py L96-L103
entries = ",\n    ".join(
    f"vec3({phase[0][a]:.5g}, {phase[1][a]:.5g}, {phase[2][a]:.5g})" for a in range(N_ANG)
)
glsl = f"""// ---- Martian dust phase (LEVEL 2: exact bhmie Mie LUT, Schneegans et al. 2024) ----
// Exact per-angle P(theta) per RGB for feldspar+hematite dust (bimodal log-normal).
// NO knee compression - the double-exp dust density tames the sunset instead.
// {N_ANG} samples, 0..180 deg. Generated by make_mars_phase_lut.py - do not hand-edit.
#define MARS_DUST_LUT_N {N_ANG}
```

## Game APIs touched

| Item | Source | Used for |
|---|---|---|
| Stock cloud mask and detail tile consumers | [current/Content/Core/Shaders/Clouds/CloudFunctions.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Clouds/CloudFunctions.glsl#L72-L106) | Channel layout the masks are built for |

## Pitfalls and version notes

- **Windows-only tooling paths.** NVIDIA Texture Tools and the game install are hard-coded as
  `C:\Program Files\...` paths, and `make_giant_surfaces.py` looks for a sibling mod's build script in
  the author's documents folder ([make_giant_surfaces.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_giant_surfaces.py#L12-L18)).
- **Stale constant.** `make_giant_flowmaps.py` scales its eddies by a per-planet displacement table
  (6,200 / 18,000 / 9,000 km) whose comment says `ShadowBuilder` sets those values, but `ShadowBuilder`
  now uses 700 km for the volumetric flow and 4,000 km for the 2D map
  ([make_giant_flowmaps.py](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/make_giant_flowmaps.py#L36-L40), [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L207-L219)).
  Regenerating the flowmaps would use the old scaling.
- **Licences travel with the files.** The release is non-commercial because of the Uranus map;
  `package.ps1` copies `CREDITS.md` into the archive for that reason.

## See also

- [Cloud layers in the system XML](cloud-layers-in-system-xml.md)
- [Mie phase sentinel dispatch](mie-phase-sentinel-dispatch.md)
- [Measured-atmosphere code generator](measured-atmosphere-generator.md)
- [Release packaging and deploy scripts](release-packaging-and-deploy.md)
- [Write a modified copy of a stock texture with an offline script](../../../howto/assets/models-and-textures/composite-png-over-stock-texture/composite-png-over-stock-texture-2.md) - how-to article demonstrating this feature
- [Python asset scripts with encodings matched to the shader that reads them](../../../howto/celestials/atmospheres-and-rings/define-atmospheres-and-cloud-layers-in-system-xml/define-atmospheres-and-cloud-layers-in-system-xml-3.md) - how-to article demonstrating this feature
- [Smuggle data into stock shaders: cut the stock MiePhaseFunction and insert a dispatcher on asymmetry sentinels](../../../howto/shaders/stock-shaders/send-extra-data-to-stock-shaders-without-new-uniforms/send-extra-data-to-stock-shaders-without-new-uniforms-4.md) - how-to article demonstrating this feature

[^ra-release-files-txt]: release-files.txt
