---
type: How-To
title: In-place rescale using KSA's light pre-pass images
description: Rescale each scene pixel by the share of light that did not come from ambient by reading the light pre-pass irradiance images in a Priority.First compute prefix
category: rendering
group: vulkan-passes
topic: modify-the-hdr-scene-with-a-compute-pass
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksastructures/features/underground-dark-compute-pass]
techniques: [vulkan-pipeline, harmony-prefix, reflection]
hooks:
  - harmony-prefix KSA.SunbloomRenderer.Render
  - reflection KSA.Rendering.Lighting.ClusteredLightSystem._diffuseIrradianceImage
  - reflection KSA.Rendering.Lighting.ClusteredLightSystem._specularIrradianceImage
  - starmap StarMapAllModsLoaded
lifecycle: [startup:all-mods-loaded, frame:render, render:bloom]
game_types:
  - KSA.SunbloomRenderer
  - KSA.Rendering.ComputePipelineWrapper
  - KSA.Rendering.Lighting.ClusteredLightSystem
  - KSA.PrePassRenderer
  - KSA.Rendering.BarrierBatch
  - KSA.GpuTextureSystem
tags: [compute, hdr, light-prepass, irradiance, relighting, reflection, priority-first]
---

Outcome: darken (or otherwise relight) part of the scene in place, using what the game's light pre-pass already computed. KSAStructures
makes an underground garage dark where no lamp reaches: KSA's static-object shader lights every face with the atmosphere's ambient
whatever stands over it, so the mod rescales each pixel by the share of its light that did not come from the ambient. Demonstrated by
[Underground dark compute pass](../../../../mods/ksastructures/features/underground-dark-compute-pass.md). The hook, wrapper and shader-asset mechanics are in approach 1; this article
adds the light images, reflection, a bindless texture set and a hook ordering. Requires StarMap and Harmony.

## Background: how the game does it

`ClusteredLightSystem` owns two private `RenderImage` fields written by the light pre-pass, `_diffuseIrradianceImage` and
`_specularIrradianceImage` [current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs#L251-L253), bound as storage images for the pre-pass compute
[current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs#L496-L497). `PrePassRenderer.OpaqueHasValidDepth` [current/decomp/KSA/PrePassRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PrePassRenderer.cs#L222) is set by the depth
validator; when it is false the engine itself skips AO and the light compute passes, so the irradiance images were not written this frame. The light passes run for the
main view only. `BarrierBatch.Add(RenderImage, ImageBarrierInfo)` [current/decomp/KSA.Rendering/BarrierBatch.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/BarrierBatch.cs#L78) builds a barrier from the image's tracked state and
records the new state, so the engine's next barrier on that image starts from the right layout. `GpuTextureSystem.Layout` / `DescriptorSet`
[current/decomp/KSA/GpuTextureSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuTextureSystem.cs#L38-L40) expose the bindless texture set.

## Lifecycle and timing

| Step | Where |
|---|---|
| Install the prefix | `startup:all-mods-loaded` (`[StarMapAllModsLoaded]`), with `priority = Priority.First` |
| Light pre-pass writes the images | `render:prepass` (steps 2 to 4 of `RenderGame`) |
| This pass | `render:bloom`, prefix on `SunbloomRenderer.Render`, main view only |

`Priority.First` puts the result in place before other mods' prefixes on the same method run. The prefix returns `true` so the engine's sun bloom still runs. See
[insert a custom Vulkan pass](../insert-a-custom-pass-into-render-game/overview.md) approach 4 and [the render sequence](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. Declare the kernel as a `Shader` asset (in `KSAStructuresOffice.xml`: `<Shader Id="KSAStructuresDarkCompute" Path="Shaders/KSAStructuresDark.comp" />`).

2. Gate every frame: record only for `Program.MainViewport`, only when the effect applies, only when `PrePassRenderer.OpaqueHasValidDepth` is true, and only
   when both light images can be read by reflection (they are private).

3. Build one `ComputePipelineWrapper` per viewport slot. Colour is a storage image, depth a depth sampler, the two light images are read-only views
   in `ShaderReadOnlyOptimal`, and KSA's bindless texture layout is an external set:

   ```csharp
   // src/KSAStructures/Ksa/DarkPass.cs L251-L270
   IRenderImage[] storageTargets = [view.Target];
   IRenderImage[] depthTargets = [depth];
   VkPushConstantRange[] ranges =
   [
       new VkPushConstantRange
       {
           Offset = (ByteSize32)0,
           Size = (ByteSize32)Marshal.SizeOf<Push>(),
           StageFlags = VkShaderStageFlags.ComputeBit,
       },
   ];

   VkDescriptorSetLayout[] external = [Program.Instance.TextureSystem.Layout];
   view.Pipeline = new ComputePipelineWrapper(
       storageTargets, depthTargets, default, default, shader,
       external, ranges, renderer.MaxFramesInFlight, renderer,
       "KSAStructures.Dark", Program.PointClampedSampler, Program.LinearClampedSampler,
       colorSamplerLinearViewsReadOnlyLayout: [irradiance.ImageView, specular.ImageView]);
   view.Irradiance = irradiance.ImageView;
   view.Specular = specular.ImageView;
   ```

4. Match the shader to the wrapper's binding order: set 1 bindings 0 to 3 are colour, depth, irradiance, specular; set 2 is the bindless texture set.

   ```glsl
   // src/KSAStructures/Shaders/KSAStructuresDark.comp L13-L35
   // Set 2 is KSA's bindless textures, for the ambient table. Bindings follow ComputePipelineWrapper's
   // order: the storage image, the depth it samples, then the engine's two light results.

   #define SET_TEXTURE 2
   #define SET_CLOUD_DATA 5

   #include "Content/KSAStructuresCore.glsl"

   layout (local_size_x = 8, local_size_y = 8) in;

   layout (set = 1, binding = 0, Rgba16f) uniform image2D sceneColor;
   layout (set = 1, binding = 1) uniform sampler2D sceneDepth;
   layout (set = 1, binding = 2) uniform sampler2D engineIrradiance;
   layout (set = 1, binding = 3) uniform sampler2D engineSpecular;

   layout (push_constant) uniform PushConstants
   {
       mat4 InvViewProj;   // clip -> camera-relative world
       vec4 Origin;        // the building's origin, camera-relative, and the share of the ambient left
       vec4 Up;            // the building's axes; w is the lamps' brightness, zero for off
       vec4 East;          // w: the share of a lamp's light the rooms return onto every face
       vec4 North;         // w: one if parts are drawn with ambient at all, as the game's settings have it
   } pc;
   ```

5. Record: move each light image to `SampledReadC` through `BarrierBatch`, bind with the texture set as external set, dispatch `ceil(w/8) x ceil(h/8)`, and end with a
   compute-to-compute barrier so another mod dispatching into the same image from the same hook sees this result.

   ```csharp
   // src/KSAStructures/Ksa/DarkPass.cs L200-L221
   using (commandBuffer.TagRegion(GpuTag))
   {
       // Into a layout a compute shader may sample, through KSA's own tracked state, so the
       // engine barriers them back out next frame from wherever this left them.
       Span<VkImageMemoryBarrier2> one = stackalloc VkImageMemoryBarrier2[1];
       BarrierBatch toSample = new(one);
       toSample.Add(irradiance!, ImageBarrierInfo.Presets.SampledReadC);
       toSample.SubmitAndFlush(commandBuffer);
       toSample = new(one);
       toSample.Add(specular!, ImageBarrierInfo.Presets.SampledReadC);
       toSample.SubmitAndFlush(commandBuffer);

       Span<VkDescriptorSet> sets = stackalloc VkDescriptorSet[1];
       sets[0] = Program.Instance.TextureSystem.DescriptorSet;
       view.Pipeline!.BindPipeline(commandBuffer, viewport.ShaderSlot, sets, default, push);
       commandBuffer.Dispatch((view.Width + Group - 1) / Group, (view.Height + Group - 1) / Group, 1);

       // Whatever dispatches into this image next, in this mod or another, reads what this wrote.
       Hazard(commandBuffer);
   }

   Underground.DarkPassRan();
   ```

6. In the shader: skip sky (depth `<= 0`), rebuild the camera-relative position, estimate the ambient the shader used (KSA's own `GetAmbient` from
   `AtmosphereLuts.glsl`), read the lamp light at the pixel from the irradiance image, and scale the rest by
   `share = 1 - (1 - left) * ambient / (ambient + lamps)`, then `imageStore` the pixel back.

## Complete example

| File | Role |
|---|---|
| [src/KSAStructures/Ksa/DarkPass.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/DarkPass.cs) | Hook, gating, wrapper, record |
| [src/KSAStructures/Shaders/KSAStructuresDark.comp](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Shaders/KSAStructuresDark.comp) | The in-place kernel |
| [src/KSAStructures/KSAStructuresOffice.xml](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/KSAStructuresOffice.xml) | Shader asset declaration |

## Variations and gotchas

- **Main view only.** Camera windows keep the daylight; the light pre-pass result is the main view's.
- **The pre-hook barrier only declares read access.** The mod writes the colour image with `imageStore` and adds no write barrier before it.
- **Pipelines are dropped, not destroyed.** `Release()` clears the dictionary; destroy with a wait if you rebuild often.
- **128-byte push block.** The `Push` struct is exactly 128 bytes; more fields need a buffer.
- **Restated layout.** The shader copies layout tables by hand from the mesh generator; it is correct only while they match.
- **Atmosphere off means no pass.** With the Atmosphere graphics setting off the static object takes a different ambient path, so the mod stands down.
- **Reflection can fail.** A renamed private field disables the effect; log once and degrade to "no effect".

## See also

- [Overview](overview.md)
- [Approach 1: depth-reading compute dispatches](modify-the-hdr-scene-with-a-compute-pass-1.md)
- [Approach 3: fireball fill](modify-the-hdr-scene-with-a-compute-pass-3.md)
- [Underground dark compute pass](../../../../mods/ksastructures/features/underground-dark-compute-pass.md)
- [Insert a custom Vulkan pass into KSA's frame](../insert-a-custom-pass-into-render-game/overview.md), [Add dynamic spot and point lights to the world from a mod](../../lights-and-particles/add-dynamic-lights-to-the-world/overview.md)
