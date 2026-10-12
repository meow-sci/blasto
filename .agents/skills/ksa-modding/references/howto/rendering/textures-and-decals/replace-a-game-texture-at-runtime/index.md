# Game Feature

* [Replace a game texture or model texture with a user PNG at runtime](overview.md) - Swap what an existing material samples by re-pointing the bindless slot the game already uses or by rebuilding a mod-owned mesh resource with a PNG override and live UV remap, and give mod-built render objects a private material buffer

# How-To

* [BindlessTextureLibrary.SetTexture on the stock slot with deferred destruction](replace-a-game-texture-at-runtime-1.md) - Re-point the bindless slot a stock material already samples at a mod-owned image, restore it on unbind, and retire the replaced image after frames in flight
* [PNG override rebuilds the mod mesh's private resource; UV scale and offset re-uploads the vertex buffer](replace-a-game-texture-at-runtime-2.md) - Swap the diffuse texture of a mod-owned static mesh by building a complete replacement resource first, and remap UVs by re-uploading vertex buffers computed from the original vertices
* [Thread-static construction context scopes transpiler and prefix redirection](replace-a-game-texture-at-runtime-3.md) - Give mod-built render objects a private material buffer by redirecting the game's own material lookups only while the mod constructs or rebuilds them, with a shader variant for source colours
