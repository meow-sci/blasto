# Game Feature

* [Add film grain and dither away banding](overview.md) - Two ways to add noise to the final image in KSA, procedural simplex grain blended soft-light with a push-constant clock, and a spatiotemporal blue-noise texture indexed by pixel and time slice

# How-To

* [Dither with blue noise: a 128x128x64 STBN texture stacked in a 128x8192 PNG, slice picked from time](add-film-grain-and-blue-noise-dithering-2.md) - Index a stacked spatiotemporal blue-noise texture by integer pixel coordinates and a time-selected slice with texel-centre UVs, and apply it multiplicatively as fine, even grain
* [Film grain: Ashima/Gustavson noise with soft-light blending on a push-constant clock](add-film-grain-and-blue-noise-dithering-1.md) - Generate grain with a textureless simplex and periodic Perlin noise seeded by a push-constant time, blend it soft-light and fade it in bright areas, with size, strength and clock set per side
