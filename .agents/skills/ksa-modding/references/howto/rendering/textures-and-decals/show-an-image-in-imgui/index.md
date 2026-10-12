# Game Feature

* [Show a PNG or runtime-generated image in an ImGui window](overview.md) - Decode or build pixels, upload them as a Vulkan texture, register with the ImGui Vulkan backend and draw with ImGui.Image or a draw list, then update, evict and release the texture without crashing a frame in flight

# How-To

* [Many changing images with in-place refresh and LRU eviction](show-an-image-in-imgui-2.md) - Upload CPU RGBA images as ImGui textures, refresh same-size images in place, and evict least recently used textures under the shared descriptor pool cap
* [One static PNG decoded to TextureAsset, uploaded once](show-an-image-in-imgui-1.md) - Decode a PNG into a TextureAsset, upload it with a staging pool that submits on dispose, register it with ImGuiBackend.Vulkan.AddTexture and draw it with ImGui.Image
