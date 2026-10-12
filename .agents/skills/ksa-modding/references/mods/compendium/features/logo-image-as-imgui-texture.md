---
type: Mod Feature
title: Logo image as an ImGui texture
description: At immediate load the mod decodes a PNG with the game's texture loader, uploads it as a SimpleVkTexture through a staging pool that submits and waits on dispose, registers the image view with the game's ImGui Vulkan backend and draws it with ImGui.Image, keeping it for the whole session
mod: compendium
category: rendering
techniques: [vulkan-texture, imgui-window]
hooks: [starmap StarMapImmediateLoad, starmap StarMapAfterGui]
lifecycle: [startup:immediate-load, frame:draw-ui]
game_types:
  - RenderCore.TextureAsset
  - RenderCore.SimpleVkTexture
  - Core.Renderer
  - Core.KSADeviceContextEx
  - Brutal.VulkanApi.Abstractions.DeviceEx
  - Brutal.VulkanApi.Abstractions.StagingPool
  - Brutal.VulkanApi.Abstractions.Presets
  - Brutal.TextureApi.Stb.Loader
  - KSA.ImGuiBackend
  - KSA.ImGuiBackendVulkanImpl
  - KSA.Program
  - Brutal.ImGuiApi.ImGui
tags: [vulkan, texture, imgui, image, png, renderer]
---

The window's start page shows the Compendium logo, scaled to fit the side pane. Getting a PNG on
screen in ImGui takes five game APIs: decode, GPU image, upload, sampler and an ImGui texture handle.
Compendium's 20-line `RegisterPhotos` is a minimal working example of that chain; commented-out
attempts above the draw call show the dead ends (passing a file path as an `ImTextureRef`) on the way
there ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L456-L483)).[^cmp-compendiummain]

## How it works

1. **Path.** From the assembly's folder it goes up one level and back into
   `Compendium/Photos/compendium-logo-1.png`, which assumes the mod folder is named `Compendium`
   ([src/CdmLoadPhotos.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmLoadPhotos.cs#L19-L20)).[^cmp-cdmloadphotos] The csproj copies `Photos/` next to the DLL
   ([src/Compendium.csproj](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Compendium.csproj#L54-L58)).[^cmp-compendium-csproj]
2. **Decode.** `new TextureAsset(path, new Brutal.TextureApi.Stb.Loader.LoadSettings { ForceChannels = 4 })`
   loads the file through the game's texture loader, forcing RGBA
   ([current/decomp/RenderCore/TextureAsset.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/TextureAsset.cs#L69-L75), [current/decomp/Brutal.TextureApi.Stb/Loader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.TextureApi.Stb/Loader.cs#L10-L21)).[^ksa-rendercore-textureasset]
   [^ksa-brutal-textureapi-stb-loader]
3. **GPU image and upload.** `Program.GetRenderer()` returns the game's `Renderer`, whose `Device` is a
   `DeviceEx` that implements both the image and buffer allocator interfaces, and whose `Graphics` is the
   graphics queue ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L557-L560), [current/decomp/Core/KSADeviceContextEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/KSADeviceContextEx.cs#L63-L69),
   [current/decomp/Brutal.VulkanApi.Abstractions/DeviceEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/DeviceEx.cs#L8)).[^ksa-program] [^ksa-core-ksadevicecontextex]
   [^ksa-brutal-vulkanapi-abstractions-deviceex] Inside
   `using (var stagingPool = renderer.Device.CreateStagingPool(renderer.Graphics, 1))` it constructs
   `new SimpleVkTexture(renderer.Device, stagingPool, texture)`, which creates a device-local image (allocated
   with the maximum mip count for its size) and an image view and records the upload into the pool's command buffer
   ([current/decomp/Brutal.VulkanApi.Abstractions/StagingPoolExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/StagingPoolExtensions.cs#L5), [current/decomp/RenderCore/SimpleVkTexture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/SimpleVkTexture.cs#L131-L168)).[^ksa-rendercore-simplevktexture]
   Leaving the `using` block disposes the pool, and `StagingPool.Dispose` submits and then waits, so the
   upload has finished when the block exits ([current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs#L214-L218)).[^ksa-brutal-vulkanapi-abstractions-stagingpool]
4. **Sampler.** `renderer.Device.CreateSampler(Presets.Sampler.SamplerPointClamped, null)` creates a new
   nearest-neighbor, clamped sampler ([current/decomp/Brutal.VulkanApi.Abstractions/Presets.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/Presets.cs#L319-L369)).[^ksa-brutal-vulkanapi-abstractions-presets]
5. **ImGui handle.** `ImGuiBackend.Vulkan.AddTexture(sampler, vkTex.ImageView)` allocates a
   combined-image-sampler descriptor set from the backend's pool and returns an `ImTextureRef` whose id
   is that descriptor set ([current/decomp/KSA/ImGuiBackend.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackend.cs#L23), [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L1119-L1150)).[^ksa-imguibackend]
   [^ksa-imguibackendvulkanimpl] The default image layout argument is `ShaderReadOnlyOptimal`.
6. **Draw.** The start page computes a square that fits the pane and calls
   `ImGui.Image(texID, new(size, size), new(0f, 0f), new(1f, 1f))`
   ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L480-L483), [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L6359)).[^ksa-brutal-imguiapi-imgui]

## Lifecycle and timing

- **Create at `startup:immediate-load`.** `RegisterPhotos` is the last call in `[StarMapImmediateLoad]`
  ([src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L1256-L1257)). In the `Program` constructor the Vulkan renderer is created
  and the ImGui backend is initialized before `ModLibrary.PrepareAll` triggers that hook
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L794), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L852), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899)). The main thread
  is between loading-screen frames, not inside one, so the blocking submit and wait on the graphics
  queue does not interleave with frame recording.
- **Draw at `frame:draw-ui`.** `ImGui.Image` only records the handle into this frame's draw data; the
  ImGui render pass samples the image later in the frame.
- **Never released.** There is no `RemoveTexture` and no dispose of the `SimpleVkTexture` or the sampler;
  they live until the process exits. The backend offers `ImGuiBackend.Vulkan.RemoveTexture(ImTextureRef)`
  and `SimpleVkTexture.Dispose` for mods that create textures dynamically
  ([current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L1128-L1131), [current/decomp/RenderCore/SimpleVkTexture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/SimpleVkTexture.cs#L350-L354)).

## Key code

| Path | Role |
|---|---|
| [src/CdmLoadPhotos.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmLoadPhotos.cs#L15-L34) | Decode, upload, sampler, ImGui registration |
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L456-L483) | Earlier attempts (commented) and the `ImGui.Image` call |
| [src/Compendium.csproj](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/Compendium.csproj#L54-L58) | Ship `Photos/` with the mod |

## Snippets

```csharp
// src/CdmLoadPhotos.cs L15-L34
private static ImTextureRef texID;

 public static void RegisterPhotos ()
 {
             var assemblyPath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
             var photoPath = Path.Combine(assemblyPath, "..", "Compendium", "Photos", "compendium-logo-1.png");
             var texture = new TextureAsset(photoPath, new Brutal.TextureApi.Stb.Loader.LoadSettings { ForceChannels = 4 });
             var renderer = KSA.Program.GetRenderer();
             SimpleVkTexture vkTex;

             using (var stagingPool = renderer.Device.CreateStagingPool(renderer.Graphics, 1))
             { 
                 vkTex = new SimpleVkTexture(renderer.Device, stagingPool, texture); 
                 }

             var sampler = renderer.Device.CreateSampler(Presets.Sampler.SamplerPointClamped, null);

             texID = ImGuiBackend.Vulkan.AddTexture(sampler, vkTex.ImageView);

 }
```

```csharp
// src/CompendiumMain.cs L463-L483
// var texture = new TextureAsset(
// "Content/Compendium/Photos/lol.jpg",
// new(new StbTexture.LoadSettings { ForceChannels = 4 }));
// var renderer = KSA.Program.GetRenderer();
// SimpleVkTexture vkTex;
// using (var stagingPool = renderer.Device.CreateStagingPool(renderer.Graphics, 1))
// {
// vkTex = new SimpleVkTexture(renderer.Device, stagingPool, texture);
// }
// var sampler = renderer.Device.CreateSampler(Presets.Sampler.SamplerPointClamped, null);
// texID = ImGuiBackend.Vulkan.AddTexture(sampler, vkTex.ImageView);

// Gets the current pane height and sets the image size to fit within the side pane while maintaining aspect ratio

// Checks if the image texture was loaded successfully


float paneHeight = ImGui.GetContentRegionAvail().Y;
float imageSize = Math.Min(sidePaneWidth - 20f, paneHeight);

ImGui.Image(texID, new(imageSize, imageSize), new(0f, 0f), new(1f, 1f));
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `TextureAsset(string, params TextureLoadSettings?[])` | [current/decomp/RenderCore/TextureAsset.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/TextureAsset.cs#L69-L75) | Decode a PNG |
| `Brutal.TextureApi.Stb.Loader.LoadSettings.ForceChannels` | [current/decomp/Brutal.TextureApi.Stb/Loader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.TextureApi.Stb/Loader.cs#L10-L21) | Force RGBA |
| `Program.GetRenderer`, `Renderer.Device`, `Renderer.Graphics` | [current/decomp/Core/KSADeviceContextEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/KSADeviceContextEx.cs#L63-L69) | Device and queue |
| `IBufferAllocator.CreateStagingPool`, `StagingPool.Dispose` | [current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs#L214-L218) | Upload and wait |
| `SimpleVkTexture(IImageAllocator, StagingPool, TextureAsset)`, `ImageView`, `Dispose` | [current/decomp/RenderCore/SimpleVkTexture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/SimpleVkTexture.cs#L131-L168) | GPU image |
| `Presets.Sampler.SamplerPointClamped`, `CreateSampler` | [current/decomp/Brutal.VulkanApi.Abstractions/Presets.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/Presets.cs#L369) | Sampler |
| `ImGuiBackend.Vulkan.AddTexture`, `RemoveTexture` | [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L1119-L1131) | ImGui texture handle |
| `ImGui.Image` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L6359) | Draw |

## Pitfalls and version notes

- **Folder-name assumption.** The `..\Compendium\Photos` path breaks if the mod folder is renamed;
  `Path.Combine(dllDir, "Photos", ...)` would not.
- **Failure leaves a default handle.** If the file is missing, the loader throws inside
  `OnImmediateLoad`'s `try`; `RegisterPhotos` is the last call there, so nothing else is skipped, but
  `texID` stays at its default value and the start page still passes it to `ImGui.Image` (what the
  backend does with a null descriptor set was not verified).
- **Point sampling and aspect.** The logo is a 2000 x 1966 grayscale-plus-alpha PNG, expanded to RGBA,
  downscaled with nearest-neighbor filtering (aliasing) and drawn into a square box (slightly
  stretched). The game already exposes shared samplers such as
  `Program.LinearClampedSampler` and `Program.PointClampedSampler` that a mod can reuse instead of
  creating its own ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L468-L472)).
- **`TextureAsset.Dispose` is empty in build 2026.10.10.5554**, so skipping it costs nothing here; the
  GPU image, view, sampler and descriptor set are the resources that are kept.

## See also

- [Celestial compendium window](celestial-info-window.md)
- [Custom fonts in the ImGui atlas](custom-fonts-in-imgui.md)
- [Kitty graphics images as ImGui Vulkan textures (purrtty)](../../purrtty/features/kitty-graphics-as-imgui-vulkan-textures.md)
- [One static PNG decoded to TextureAsset, uploaded once](../../../howto/rendering/textures-and-decals/show-an-image-in-imgui/show-an-image-in-imgui-1.md) - how-to article demonstrating this feature

[^cmp-compendiummain]: Compendium: src/CompendiumMain.cs
[^cmp-cdmloadphotos]: Compendium: src/CdmLoadPhotos.cs
[^cmp-compendium-csproj]: Compendium: src/Compendium.csproj
[^ksa-rendercore-textureasset]: RenderCore.TextureAsset (decompiled, build 2026.10.10.5554)
[^ksa-brutal-textureapi-stb-loader]: Brutal.TextureApi.Stb.Loader (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-core-ksadevicecontextex]: Core.KSADeviceContextEx (decompiled, build 2026.10.10.5554)
[^ksa-brutal-vulkanapi-abstractions-deviceex]: Brutal.VulkanApi.Abstractions.DeviceEx (decompiled, build 2026.10.10.5554)
[^ksa-rendercore-simplevktexture]: RenderCore.SimpleVkTexture (decompiled, build 2026.10.10.5554)
[^ksa-brutal-vulkanapi-abstractions-stagingpool]: Brutal.VulkanApi.Abstractions.StagingPool (decompiled, build 2026.10.10.5554)
[^ksa-brutal-vulkanapi-abstractions-presets]: Brutal.VulkanApi.Abstractions.Presets (decompiled, build 2026.10.10.5554)
[^ksa-imguibackend]: KSA.ImGuiBackend (decompiled, build 2026.10.10.5554)
[^ksa-imguibackendvulkanimpl]: KSA.ImGuiBackendVulkanImpl (decompiled, build 2026.10.10.5554)
[^ksa-brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
