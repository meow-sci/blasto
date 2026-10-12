# Game Feature

* [Upload an image or procedural pixels as a GPU texture](overview.md) - Load PNG or KTX bytes or generate pixels in code into a SimpleVkTexture with mip levels and a sampler and expose it to shaders through a bindless slot

# How-To

* [Decode with the game's TextureLoader and re-point an existing bindless slot](upload-an-image-as-a-vulkan-texture-1.md) - Decode PNG, JPEG, DDS or KTX bytes with the game's loaders into a SimpleVkTexture and write its image view into a bindless slot the game already samples
* [Mip-mapped library textures claimed into Program.BindlessTextures, hot-swapped on rescan](upload-an-image-as-a-vulkan-texture-2.md) - Upload each PNG once into a mod-owned mip-mapped image, claim a new bindless slot with AddTexture so shaders need only a uint index, and hot-swap on file change
* [Procedural ASCII pixel art to an R8G8B8A8 texture with a staging upload](upload-an-image-as-a-vulkan-texture-3.md) - Generate pixels in code, create a SimpleVkTexture, upload through a one-buffer staging pool and create a nearest-neighbour clamp sampler for crisp pixel art
