# Game Feature

* [Composite an image over a stock texture](overview.md) - Two ways to put a user image on top of a game texture, a CPU blend at run time over a re-transcoded stock source and an offline script that writes a modified copy to ship

# How-To

* [Blend a PNG decal over a transcoded stock texture on the CPU and register it with GpuTextureSystem](composite-png-over-stock-texture-1.md) - Re-open the stock KTX2 source with an RGBA32 transcode because the resident texture is block-compressed, alpha-blend a scaled PNG into the centre while keeping the stock alpha, then upload with TryAddTexture and track ownership so only your own entries are removed
* [Write a modified copy of a stock texture with an offline script](composite-png-over-stock-texture-2.md) - Read a texture from the player's install with Pillow and numpy, transform it, write a PNG into the mod's assets folder, ship only files named in a release list, and consider deriving it at launch where the original cannot be redistributed
