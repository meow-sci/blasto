---
type: How-To
title: One static PNG decoded to TextureAsset, uploaded once
description: Decode a PNG into a TextureAsset, upload it with a staging pool that submits on dispose, register it with ImGuiBackend.Vulkan.AddTexture and draw it with ImGui.Image
category: rendering
group: textures-and-decals
topic: show-an-image-in-imgui
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/compendium/features/logo-image-as-imgui-texture]
techniques: [vulkan-texture]
hooks: [starmap StarMapImmediateLoad, starmap StarMapAfterGui]
lifecycle: [startup:immediate-load, frame:draw-ui, render:imgui]
game_types:
  - RenderCore.SimpleVkTexture
  - Brutal.TextureApi.TextureLoader
  - KSA.ImGuiBackendVulkanImpl
  - RenderCore.TextureAsset
tags: [imgui, image, texture, png, addtexture, startup]
---

Outcome: a PNG from your mod folder shows in an ImGui window. Five game APIs make the chain: decode, GPU image, upload, sampler and an ImGui texture handle. Compendium's 20-line `RegisterPhotos` is a minimal working example. Demonstrated by [Logo image as an ImGui texture](../../../../mods/compendium/features/logo-image-as-imgui-texture.md).
Prerequisites: StarMap, a PNG copied next to the mod DLL by the build. For many changing images and eviction see approach 2.

## Background: how the game does it

`TextureAsset(string filePath, params TextureLoadSettings?[] loadInfos)` [current/decomp/RenderCore/TextureAsset.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/TextureAsset.cs#L69) decodes a file (`ForceChannels = 4` forces RGBA). `Program.GetRenderer()` returns the game's `Renderer`; its `Device` is a `DeviceEx` that allocates images and buffers and its `Graphics` is the graphics queue.
`new SimpleVkTexture(IImageAllocator, StagingPool, TextureAsset)` [current/decomp/RenderCore/SimpleVkTexture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/SimpleVkTexture.cs#L131) creates the device-local image and view and records the upload into the pool's command buffer; `StagingPool.Dispose` submits and then waits [current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs#L214-L218), so the upload has finished when a `using` block exits.
`ImGuiBackend.Vulkan.AddTexture(sampler, imageView, layout = ShaderReadOnlyOptimal)` [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L1119-L1126) allocates a combined-image-sampler descriptor set from the backend's pool and returns an `ImTextureRef` whose id is that set; the pool holds `DESCRIPTOR_POOL_SIZE = 1000` sets for the whole game [current/decomp/KSA/ImGuiBackend.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackend.cs#L15).
`ImGui.Image(ImTextureRef, in float2 size, in float2? uv0, in float2? uv1)` [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L6359) records the handle into this frame's draw data.

## Lifecycle and timing

| Step | Where |
|---|---|
| Create image, upload, register | `[StarMapImmediateLoad]` (`startup:immediate-load`): the renderer exists and the ImGui backend is initialised before `ModLibrary.PrepareAll` triggers this hook, and the main thread is between loading-screen frames, so a blocking submit does not interleave with frame recording |
| Draw | `frame:draw-ui`: `ImGui.Image` only records the handle |
| Sample | `render:imgui` (step 25 of the `RenderGame` table in [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence)) |

## Steps

1. Copy the PNG next to the DLL in the csproj and build its path from the assembly location (`Path.Combine(assemblyDir, "Photos", ...)`).

2. Decode, upload, create a sampler and register. The staging pool is disposed at the end of the `using`, which submits and waits:

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

3. Draw: compute a square that fits the pane and call `ImGui.Image(texID, new(size, size), new(0f, 0f), new(1f, 1f))`:

   ```csharp
   // src/CompendiumMain.cs L478-L483


   float paneHeight = ImGui.GetContentRegionAvail().Y;
   float imageSize = Math.Min(sidePaneWidth - 20f, paneHeight);

   ImGui.Image(texID, new(imageSize, imageSize), new(0f, 0f), new(1f, 1f));
   ```

4. Keep the `ImTextureRef` in a static; if you create textures dynamically, also keep the `SimpleVkTexture` and sampler so you can release them (`RemoveTexture` before `Dispose`; see approach 2).

## Complete example

| File | Role |
|---|---|
| [src/CdmLoadPhotos.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CdmLoadPhotos.cs#L15-L34) | `RegisterPhotos`: decode, upload, sampler, register |
| [src/CompendiumMain.cs](https://github.com/meow-sci/Compendium/blob/62cfbe7aafabc58cf71f7c6d618d0c5ece7c504c/src/CompendiumMain.cs#L478-L483) | The draw call |

## Variations and gotchas

- **Never released** in Compendium: the image, sampler and descriptor set live until process exit. Fine for one logo; dynamic textures need `RemoveTexture` and `Dispose`.
- **Folder-name assumption.** The `..\Compendium\Photos` path breaks if the mod folder is renamed.
- **Failure leaves a default handle:** if the file is missing the loader throws inside the `try` and `texID` stays default; guard the draw.
- **Samplers and aspect.** The point sampler aliases a downscaled logo; the game already exposes shared samplers (`Program.LinearClampedSampler`, `Program.PointClampedSampler`) you can reuse instead of creating your own.
- `TextureAsset.Dispose` is empty in this build, so skipping it costs nothing.

## See also

- [Overview](overview.md)
- [Approach 2: many changing images with in-place refresh and LRU eviction](show-an-image-in-imgui-2.md)
- [Logo image as an ImGui texture](../../../../mods/compendium/features/logo-image-as-imgui-texture.md)
- [Upload an image or procedural pixels as a GPU texture](../upload-an-image-as-a-vulkan-texture/overview.md), [Free mod-owned Vulkan resources without device loss](../../vulkan-passes/free-vulkan-resources-safely/overview.md), [Record and submit your own Vulkan work: offscreen renders and one-shot uploads](../../vulkan-passes/submit-your-own-vulkan-commands/overview.md)
