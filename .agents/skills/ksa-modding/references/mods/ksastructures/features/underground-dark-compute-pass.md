---
type: Mod Feature
title: Underground dark compute pass
description: A first-priority Harmony prefix on SunbloomRenderer.Render dispatches a mod compute shader over the main view's HDR scene image, scaling the sky's ambient out of every pixel inside the dug rooms before bloom and tonemapping
mod: ksastructures
category: shaders
techniques: [harmony-prefix, vulkan-pipeline, glsl-shader, post-processing, reflection]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-prefix KSA.SunbloomRenderer.Render
  - reflection KSA.Rendering.Lighting.ClusteredLightSystem._diffuseIrradianceImage
  - reflection KSA.Rendering.Lighting.ClusteredLightSystem._specularIrradianceImage
lifecycle: [startup:all-mods-loaded, render:bloom]
game_types:
  - KSA.SunbloomRenderer
  - KSA.Rendering.ComputePipelineWrapper
  - KSA.Rendering.BarrierBatch
  - KSA.Rendering.ImageBarrierInfo
  - KSA.Rendering.RenderImage
  - KSA.Rendering.Lighting.ClusteredLightSystem
  - KSA.PrePassRenderer
  - KSA.GpuTextureSystem
  - KSA.ShaderReference
  - KSA.GameSettings
  - KSA.Program
tags: [compute-shader, vulkan, ambient, bloom, post-processing, barriers, hdr]
---

Shadow casters stop the sun, but not the sky. KSA's static-object shader lights every face with the
atmosphere's ambient whatever stands over it, so a garage under the ground still glows like an
overcast day. This compute pass makes the garage dark where no lamp reaches. It also adds the light
the rooms would bounce back onto their own ceilings and pillars, and the glow of each lamp's fixture.

Mechanism in one line: a `Priority.First` prefix on `SunbloomRenderer.Render(CommandBuffer, IViewport, int)`
builds a `ComputePipelineWrapper` over the main view's colour and depth plus KSA's own light pre-pass
images, and dispatches `KSAStructuresDark.comp`. The shader rescales each pixel in place by the share
of its light that did not come from the ambient.

## How it works

**Why a post pass is needed.** In `StaticObject.frag` the screen-space AO is sampled and then
overwritten with `ao = 1.0f`. Ambient comes from the atmosphere's ambient LUT via `GetAmbient` times
the sun colour, and `main()` never samples an emissive texture even though `PerDrawData` declares
`emissiveTextureIndex` ([current/Content/Core/Shaders/Mesh/StaticObject.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/StaticObject.frag#L334-L359),
[current/Content/Core/Shaders/Mesh/StaticObject.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/StaticObject.frag#L50-L58)).[^ksa-staticobject-frag] Nothing
in the static object's own draw knows there is a roof overhead, and nothing can make it glow.

**Gate.** `DarkPass.Record` returns without recording anything unless all of these hold
([src/KSAStructures/Ksa/DarkPass.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/DarkPass.cs#L152-L183)):

- the viewport is `Program.MainViewport`. The light pre-pass images belong to the main view; the
  engine dispatches the light passes only for it ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4721-L4728));
- `Underground.TryDarkened` holds an origin from the lamp postfix within the last 4 frames, and that
  origin is within 600 m of the render camera;
- `PrePassRenderer.OpaqueHasValidDepth` is true. The depth validator sets it, and when it is false the
  engine itself skips AO and the light compute passes, so the irradiance images were not written this
  frame ([current/decomp/KSA/PrePassRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PrePassRenderer.cs#L222),
  [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4716-L4728));
- the Atmosphere graphics setting is on and the body has an atmosphere. That is when the static
  object's push constant selects the `GetAmbient` branch
  ([current/decomp/KSA/StaticObjectRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/StaticObjectRenderer.cs#L355-L357));
- `ClusteredLightSystem._diffuseIrradianceImage` and `._specularIrradianceImage` can be read by
  reflection. Both are B10G11R11 storage images written by the light pre-pass
  ([current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs#L622-L623)).

**Pipeline.** One `ComputePipelineWrapper` per viewport shader slot, rebuilt when the colour target,
its size or either light image view changes ([src/KSAStructures/Ksa/DarkPass.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/DarkPass.cs#L224-L273)):

| Wrapper argument | Value | Ends up as |
|---|---|---|
| `storageRenderTargets` | `[viewport.OffscreenTarget.ColorImage]` | set 1 binding 0, `General` layout |
| `depthSamplerRenderTargets` | `[viewport.OffscreenTarget.DepthImage]` | set 1 binding 1, point-clamped, depth read-only layout |
| `colorSamplerLinearViewsReadOnlyLayout` | `[irradiance.ImageView, specular.ImageView]` | set 1 bindings 2-3, linear-clamped, `ShaderReadOnlyOptimal` |
| `externalDescriptorSetLayouts` | `[Program.Instance.TextureSystem.Layout]` | set 2 (KSA's bindless textures), since `bindTextures` is false |
| `pushConstantRanges` | one compute range of `Marshal.SizeOf<Push>()` = 128 bytes | push block |
| (always) | `GlobalShaderBindings` | set 0 |

The wrapper puts `GlobalShaderBindings` at set 0, its own set at 1 and the external sets after
that. Bindings run storage images, then depth samplers, then linear samplers, then the read-only
views ([current/decomp/KSA.Rendering/ComputePipelineWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ComputePipelineWrapper.cs#L32-L135)). The shader mirrors
this with `#define SET_TEXTURE 2` and set-1 bindings 0-3
([src/KSAStructures/Shaders/KSAStructuresDark.comp](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Shaders/KSAStructuresDark.comp#L13-L35)).

**Recording** ([src/KSAStructures/Ksa/DarkPass.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/DarkPass.cs#L200-L221)):

1. Each light image is moved to `ImageBarrierInfo.Presets.SampledReadC` (`ShaderReadOnlyOptimal`,
   compute read) through `BarrierBatch.Add(RenderImage, preset)`, one single-slot batch each. That
   overload builds the barrier from the image's tracked state and records the new state, so the
   engine's own next barrier on those images starts from the right layout
   ([current/decomp/KSA.Rendering/BarrierBatch.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/BarrierBatch.cs#L78-L86),
   [current/decomp/KSA.Rendering/RenderImage.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderImage.cs#L358-L387)).[^ksa-renderimage]
2. `BindPipeline(commandBuffer, viewport.ShaderSlot, [TextureSystem.DescriptorSet], default, push)`,
   then `Dispatch(ceil(w/8), ceil(h/8), 1)`.
3. A `VkMemoryBarrier2` from compute shader write to compute shader read and write, so another mod
   dispatching into the same image from the same hook sees this result.

**Push constants** ([src/KSAStructures/Ksa/DarkPass.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/DarkPass.cs#L63-L71), filled at L191-L198):

| Field | xyz | w |
|---|---|---|
| `InvViewProj` (mat4) | `camera.VPInv.viewProjection`: clip to camera-relative world | |
| `Origin` | building origin relative to the render camera | share of ambient left (0.1, or 1 with darkening off) |
| `Up` | building up axis | lamp brightness (`LampScale`, 0 when lamps are off) |
| `East` | building east axis | share of lamp light the rooms return (`Fill`, 0.08) |
| `North` | building north axis | 1 if the Reflections setting is on |

**What the shader computes** ([src/KSAStructures/Shaders/KSAStructuresDark.comp](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Shaders/KSAStructuresDark.comp#L142-L288)):

1. Skips sky pixels (depth `<= 0`; the scene depth is cleared to 0,
   [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4792-L4804)), rebuilds the camera-relative position and moves
   it into the building frame. It leaves anything above the ceiling's underside, and anything outside
   the seven pit boxes and the deep shaft and hall, untouched.
2. Reconstructs a normal from neighbouring depths and classifies the pixel as the garage's own shell
   (floor, ceiling, wall by restated layout tables) or something else (a car, a kitten).
3. Estimates the ambient each shader used. It calls KSA's own `GetAmbient(...)` from
   `AtmosphereLuts.glsl` with `AMBIENT_LUT_TEXTURE` and `GLOBAL_CLAMP_SAMPLER`, times the sun colour,
   mirroring `StaticObject.frag`
   ([current/Content/Core/Shaders/Atmosphere/AtmosphereLuts.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/AtmosphereLuts.glsl#L470-L477),
   [current/Content/Core/Shaders/Common/TextureSet.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/TextureSet.glsl#L65-L74)). Shell faces take 0.92 of it;
   other surfaces take a greyed, up-facing-weighted share times the Reflections flag, because the
   part shader's `getPBRLightingAmbient` returns zero when both reflection bits are off
   ([current/Content/Core/Shaders/Common/Lighting.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/Lighting.glsl#L608-L611)).
4. Reads the engine's lamp light at the pixel (`engineIrradiance * (1 - fresnel) / pi`) and keeps
   specular highlights aside. It then scales the rest by
   `share = 1 - (1 - left) * ambient / (ambient + lamps)`. Pixels lit by sun coming down the open
   ramp keep their light, and the shell loses only the ambient its paint took.
5. Adds, for shell faces near any of the 19 garage lamps, a one-bounce "returned" term and a fixture
   glow. Visibility is tested by marching through the restated air and pillar boxes. The deep hall
   gets a constant bounce from its floor. Then `imageStore` writes the pixel back.

## Lifecycle and timing

- **Install:** `[StarMapAllModsLoaded]` -> `DarkPass.Install()`
  ([src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L59)), a prefix with `priority = Priority.First`
  ([src/KSAStructures/Ksa/DarkPass.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/DarkPass.cs#L86-L114)).
- **Where it runs:** main thread, inside `Program.RenderGame`'s command recording (`render:bloom`).
  The main view's translucency, translucent particles, MSAA resolve and underwater pass are done.
  The engine then moves the colour image to `StorageReadFc` (`General` layout, shader read, fragment
  and compute) and the depth to `DepthSampledReadVfc` (`DepthStencilReadOnlyOptimal`) and calls
  `_sunbloomRenderer.Render`, then `_overallBloomRenderer.Render`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4901-L4916),
  [current/decomp/KSA.Rendering/ImageBarrierInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ImageBarrierInfo.cs#L35-L57)).[^ksa-program]
- **What follows:** bloom, the selection outline, a copy back into the MSAA image when MSAA is on
  (`SingleToMultisamplePass`), orbit lines, gizmos, grid, gauges, then the tonemap composite and
  ImGui ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4917-L4950)). The pass therefore edits linear HDR
  colour (`R16G16B16A16SFloat`, [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L223); the main viewport shares
  this target, L1525), and bloom and exposure are applied to its result.
- **Secondary viewports** call `SunbloomRenderer.Render` too, with the same barrier pair, when they
  render atmosphere ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4576-L4585)); the prefix returns at once
  for them.
- **Why this hook:** `SunbloomRenderer.Render` is public, takes the command buffer and viewport, and is
  the first call after the engine has already put colour in a storage layout and depth in a sampled
  one ([current/decomp/KSA/SunbloomRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SunbloomRenderer.cs#L326-L394)). The mod adds no barriers for colour or
  depth of its own.
- **Prefix returns `true`:** the engine's sun bloom always runs afterwards. After 3 faults the pass
  stands down until `DarkPass.Release()` (also called by the shader reload), which drops the
  pipelines and resets the fault count.

## Key code

| Path | Role |
|---|---|
| [src/KSAStructures/Ksa/DarkPass.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/DarkPass.cs#L86-L114) | Prefix install with `Priority.First` and signature pinning |
| [src/KSAStructures/Ksa/DarkPass.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/DarkPass.cs#L152-L222) | `Record`: gates, barriers, bind, dispatch, trailing barrier |
| [src/KSAStructures/Ksa/DarkPass.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/DarkPass.cs#L237-L273) | `Build`: `ComputePipelineWrapper` construction |
| [src/KSAStructures/Ksa/DarkPass.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/DarkPass.cs#L293-L313) | Reflection into the light pre-pass images |
| [src/KSAStructures/Shaders/KSAStructuresDark.comp](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Shaders/KSAStructuresDark.comp) | The compute shader and its restated layout tables |
| [src/KSAStructures/Ksa/OfficeUnderground.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/OfficeUnderground.cs#L34-L36) | `DaylightLeft` (0.1) and the 600 m range |

## Snippets

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

```glsl
// src/KSAStructures/Shaders/KSAStructuresDark.comp L196-L239
    vec4 was = imageLoad(sceneColor, at);

    // The ambient each shader lit this pixel with. StaticObject.frag takes the sky table whole;
    // parts, kittens and fur take it greyed, and by how far the face looks up.
    vec3 toSun = normalize(global.lighting.sunPosition.xyz - world);
    float bottom = global.lighting.planetRadius;
    vec3 sky = GetAmbient(world, global.lighting.planetPosition.xyz, toSun, bottom,
                          bottom + global.lighting.atmosphereHeight, AMBIENT_LUT_TEXTURE, GLOBAL_CLAMP_SAMPLER,
                          float(global.lighting.atmosphereLutLayer)) * global.lighting.sunColor.rgb;
    vec3 ambient = shell ? sky * 0.92
                         : mix(vec3(length(sky)), sky, 0.7) * ((0.5 + 0.5 * rise) * pc.North.w);

    // The engine's lamp light here, and its highlights, which are no share of the ambient.
    float glance = 1.0 - clamp(dot(n, -normalize(world)), 0.0, 1.0);
    float fresnel = 0.04 + 0.96 * glance * glance * glance * glance * glance;
    vec3 lamps = texelFetch(engineIrradiance, at, 0).rgb * ((1.0 - fresnel) / 3.14159265);
    vec3 highlights = min(was.rgb, texelFetch(engineSpecular, at, 0).rgb * fresnel);
// ...
    vec3 colour;
    if (!sunlit)
    {
        vec3 share = 1.0 - (1.0 - left) * ambient / max(ambient + lamps, vec3(1.0e-6));
        colour = (was.rgb - highlights) * share + highlights;
    }
```

```csharp
// current/decomp/KSA/Program.cs L4910-L4916
		inImages = stackalloc VkImageMemoryBarrier2[2];
		BarrierBatch barrierBatch3 = new BarrierBatch(inImages);
		barrierBatch3.Add(_offscreenTarget.ColorImage, ImageBarrierInfo.Presets.StorageReadFc);
		barrierBatch3.Add(_offscreenTarget.DepthImage, ImageBarrierInfo.Presets.DepthSampledReadVfc);
		barrierBatch3.SubmitAndFlush(commandBuffer2);
		_sunbloomRenderer.Render(commandBuffer2, MainViewport, resourceFrameIndex);
		_overallBloomRenderer.Render(commandBuffer2, MainViewport, resourceFrameIndex);
```

## Game APIs touched

| API | Decompiled source | Used for |
|---|---|---|
| `SunbloomRenderer.Render(CommandBuffer, IViewport, int)` | [current/decomp/KSA/SunbloomRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SunbloomRenderer.cs#L326-L394) | Patched (prefix, first) |
| `ComputePipelineWrapper` constructor, `BindPipeline<T>` | [current/decomp/KSA.Rendering/ComputePipelineWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ComputePipelineWrapper.cs#L32-L214) | Descriptor sets, pipeline layout, push constants |
| `BarrierBatch.Add(RenderImage, ImageBarrierInfo)`, `ImageBarrierInfo.Presets.SampledReadC` | [current/decomp/KSA.Rendering/BarrierBatch.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/BarrierBatch.cs#L78-L86) | State-tracked layout change of the light images |
| `ClusteredLightSystem._diffuseIrradianceImage`, `._specularIrradianceImage` | [current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs#L251-L253) | Lamp light per pixel, apart from ambient (private, reflection) |
| `ClusteredLightSystem` light pre-pass barriers | [current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs#L1485-L1503) | Leaves the images in `SampledReadF` for the scene; next frame starts from `StorageWriteC` |
| `PrePassRenderer.OpaqueHasValidDepth` | [current/decomp/KSA/PrePassRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PrePassRenderer.cs#L222) | Whether this frame's light result exists |
| `GpuTextureSystem.Layout`, `.DescriptorSet` | [current/decomp/KSA/GpuTextureSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuTextureSystem.cs#L38-L40) | Bindless set for the ambient LUT |
| `ShaderReference` via `ModLibrary.TryGet` | [current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L21-L36) | The compiled `KSAStructuresDarkCompute` module |
| `GameSettings.ShowReflections` | [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L3183-L3186) | Push-constant switch for non-shell ambient |
| `GetAmbient` (GLSL) | [current/Content/Core/Shaders/Atmosphere/AtmosphereLuts.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/AtmosphereLuts.glsl#L470-L477) | Same ambient lookup the static-object shader uses |

## Pitfalls and version notes

- **Main view only.** Camera windows (the office computer's among them) keep the daylight, because
  the light pre-pass result is the main view's.
- **The pre-sunbloom barrier only declares read access.** The engine moves the colour image to
  `StorageReadFc` (shader *read*) before this hook; the pass writes the image in place with
  `imageStore` and adds no write barrier before it.
- **Pipelines are dropped, not destroyed.** `Release()` clears the dictionary without calling
  `ComputePipelineWrapper.Destroy`, so each rebuild (resize, light-image change, shader reload) leaves
  the old pipeline, layout and descriptor pool alive. That is safe for frames still in flight, but
  the objects are never freed.
- **128-byte push block.** `Push` is exactly 128 bytes, the minimum `maxPushConstantsSize` Vulkan
  guarantees; any further field needs a buffer instead.
- **Restated layout.** The pit, air, pillar and lamp tables and the deep shaft and hall bounds are
  copied by hand from the mesh generator and `OfficeUnderground.cs`; the shader is only right while
  they match.
- **Atmosphere off means no pass.** With the Atmosphere graphics setting off the static object uses
  `getPBRLightingAmbient` instead, and the mod stands down rather than guess
  ([current/Content/Core/Shaders/Mesh/StaticObject.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/StaticObject.frag#L353-L357)).
- **Version:** the mod is built against KSA 2026.10.7.5541; this page was checked against
  2026.10.10.5554. The decompiled diff between the two builds does not touch `SunbloomRenderer`,
  `ComputePipelineWrapper`, `BarrierBatch` or `ClusteredLightSystem`.

## See also

- [Core shader library include](./core-shader-library-include.md): how `KSAStructuresDark.comp` reaches `Global.glsl` and `AtmosphereLuts.glsl`
- [Garage spot lights](./garage-spot-lights.md): the lamps whose light the pass reads back
- [Invisible shadow casters](./invisible-shadow-casters.md): the direct-sun half of the darkness
- [Generated deep ramp](./generated-deep-ramp.md): the deep shaft and hall the shader bounds
- [Dev file bridge and MCP](./dev-file-bridge-and-mcp.md): `office_light` tuning and `reload_shaders`
- [Prefix SunbloomRenderer.Render to dispatch compute on the HDR scene](../../../howto/rendering/vulkan-passes/insert-a-custom-pass-into-render-game/insert-a-custom-pass-into-render-game-4.md) - how-to article demonstrating this feature
- [In-place rescale using KSA's light pre-pass images](../../../howto/rendering/vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/modify-the-hdr-scene-with-a-compute-pass-2.md) - how-to article demonstrating this feature

[^ksa-staticobject-frag]: Core shader Mesh/StaticObject.frag
[^ksa-renderimage]: KSA.Rendering.RenderImage (decompiled)
[^ksa-program]: KSA.Program (decompiled)
