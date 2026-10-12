---
type: How-To
title: "Colour-grade a camera feed: a two-dispatch measure and map pass per viewport with a cached pipeline"
description: Give a secondary camera window a TV or thermal look with a compute pass on its HDR scene image before bloom, using a 1x1 log-mean reduction in a first dispatch and a mapping dispatch second
category: shaders
group: screen-effects
topic: colour-grade-the-frame-and-add-a-vignette
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony, vulkan]
demonstrated_by: [mods/ksarmory/features/sensor-look-pass]
techniques: [vulkan-pipeline, glsl-shader, harmony-prefix]
hooks: [harmony-prefix KSA.SunbloomRenderer.Render]
lifecycle: [frame:celestials, frame:draw-ui, render:bloom]
game_types:
  - KSA.SunbloomRenderer
  - KSA.IViewport
  - KSA.Rendering.RenderTarget
  - KSA.Rendering.BarrierBatch
  - KSA.Rendering.ComputePipelineWrapper
  - KSA.ViewportOptionFlags
tags: [colour-grading, thermal, tv, compute, viewport, log-average, tonemap, ksarmory]
---
A mod that adds a camera feed (an optical head, a targeting pod) often wants it to look like a sensor: a monochrome TV channel or a white-hot or black-hot thermal image. KSArmory does it with a compute shader on that viewport's HDR scene image, recorded from a `SunbloomRenderer.Render` prefix, before bloom and the tonemap, so the grey picture is exposed like the colour one. This article is the
recipe, including the log-mean reduction in its own dispatch and the pipeline cache that survives viewport resizes. You need Harmony and a camera window driving one of KSA's secondary viewports.

## Background: how the game does it

- **No per-viewport post-process hook.** `SunbloomRenderer.Render(CommandBuffer, IViewport, int)` is public and called once per rendered viewport, handing over that viewport ([current/decomp/KSA/SunbloomRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SunbloomRenderer.cs#L326)). A prefix on it is the per-viewport seam used before bloom.
- **Secondary viewports.** `RenderGame` loops `ViewportRegistry.Views` into `RenderViewport`; inside the branch for viewports with the `RenderAtmosphere` flag colour is left in `StorageReadFc` and depth in `DepthSampledReadVfc` before the sunbloom call, so a compute pass needs no extra image barriers except for its own private image. The four secondary viewports use `ViewportPresets.SECONDARY_GAME`,
  which includes `RenderAtmosphere`.
- **`RenderTarget.Rebuild(w, h)` rebuilds its colour and depth images in place**: the object is the same, the `VkImageView` a descriptor set names is not, so a cache must compare size as well as the object.

## Lifecycle and timing

| Moment | What happens | Value |
|---|---|---|
| Per frame, in the mod's step (HUD shown) or in the `OnFrameCelestials` prefix (HUD hidden) | `DriveCameraWindows` rebuilds the "wanted" set: viewport object to sensor mode | `frame:draw-ui` / `frame:celestials` |
| Per rendered viewport | The `SunbloomRenderer.Render` prefix calls the cloud pass, then `SensorPass.Record` for that viewport | `render:bloom` |
| Main viewport | The prefix fires too; `SensorPass.Record` returns because the main view is never wanted | `render:bloom` |

See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Register the shader as a `<Shader>` asset** in the mod's XML (`<Shader Id="KSArmorySensorCompute" .../>`) and look it up with `ModLibrary.TryGet<ShaderReference>` when you build the pipeline; if it is missing, warn once and leave the window in colour.

2. **Hook the prefix and record after your other passes:**

   ```csharp
   // src/KSArmory/Ksa/CloudPassHook.cs L100-L110
       private static void BeforeSunbloom(CommandBuffer commandBuffer, IViewport viewport, int frameIndex)
       {
           // Always: at zero the pass skips only the cloud, and the rings, holes and tracers it paints
           // have no other way onto the screen.
           float tint = _tint?.Invoke() ?? 0f;
           CloudPass.Record(commandBuffer, viewport, frameIndex, Math.Max(0f, tint));

           // Last and whatever the cloud pass is set to: a sensor sees the burst too, and its look is
           // the director's setting rather than a display one.
           SensorPass.Record(commandBuffer, viewport);
       }
   ```

   Source: [src/KSArmory/Ksa/CloudPassHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPassHook.cs#L100-L110).

3. **Cache a pipeline and a 1x1 mean image per viewport, keyed by `ShaderSlot`, and rebuild on a size change.** The wrapper numbers storage images first, then samplers: set 1 binding 0 is the scene colour (rgba16f storage), 1 the mean image (r32f), 2 the depth sampler:

   ```csharp
   // src/KSArmory/Ksa/SensorPass.cs L98-L145
       // Rebuilt on a resize. The size is compared as well as the image, because a resize can rebuild
       // the image inside the same object, and a set still naming the old one loses the device.
       private static View? ViewFor(IViewport viewport, IRenderImage colour, IRenderImage depth,
                                    int width, int height)
       {
           if (_views.TryGetValue(viewport.ShaderSlot, out View? view) && ReferenceEquals(view.Target, colour)
               && view.Width == width && view.Height == height)
           {
               return view;
           }

           if (!ModLibrary.TryGet<ShaderReference>(ShaderId, out var shader) || shader is null)
           {
               Warn($"no shader '{ShaderId}'; camera windows stay in colour");
               return null;
           }

           Renderer renderer = Program.GetRenderer();
           RenderImage mean = RenderImage.CreateColorStorage(renderer, "KSArmory Sensor Mean",
                                                             new VkExtent2D(1, 1), VkFormat.R32SFloat);

           IRenderImage[] storageTargets = [colour, mean];
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

           view = new View
           {
               Target = colour,
               Width = width,
               Height = height,
               Mean = mean,
               Pipeline = new ComputePipelineWrapper(
                   storageTargets, depthTargets, default, default, shader,
                   default, ranges, renderer.MaxFramesInFlight, renderer,
                   "KSArmory.Sensor", Program.PointClampedSampler, Program.LinearClampedSampler),
           };

           _views[viewport.ShaderSlot] = view;
           return view;
       }
   ```

   Source: [src/KSArmory/Ksa/SensorPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/SensorPass.cs#L98-L145). Replaced views are not disposed in this checkout (the cloud pass buries its images for 16 frames instead); dispose or bury yours.

4. **Record two dispatches with a hazard between them.** The first measures the frame; the second maps every pixel. Separate dispatches, because the mapping pass rewrites the pixels the measurement reads:

   ```csharp
   // src/KSArmory/Ksa/SensorPass.cs L57-L90
       public static void Record(CommandBuffer commandBuffer, IViewport viewport)
       {
           try
           {
               if (!_wanted.TryGetValue(viewport, out SensorMode mode)) return;
               if (viewport.OffscreenTarget is not { } target) return;
               if (target.ColorImage is not { } colour || target.DepthImage is not { } depth) return;

               int width = (int)target.Extent.Width;
               int height = (int)target.Extent.Height;
               if (width <= 0 || height <= 0) return;

               View? view = ViewFor(viewport, colour, depth, width, height);
               if (view?.Pipeline is null) return;

               Span<VkImageMemoryBarrier2> one = stackalloc VkImageMemoryBarrier2[1];
               BarrierBatch toStorage = new(one);
               toStorage.Add(view.Mean, ImageBarrierInfo.Presets.StorageReadWriteC);
               toStorage.SubmitAndFlush(commandBuffer);

               float seconds = (float)(Environment.TickCount64 % 100_000L) * 0.001f;
               Push push = new() { ModeTime = new float4((float)mode, seconds, 0f, 0f) };

               Hazard(commandBuffer);
               view.Pipeline.BindPipeline(commandBuffer, viewport.ShaderSlot, default, default, push);
               commandBuffer.Dispatch(1, 1, 1);

               Hazard(commandBuffer);
               push.ModeTime.Z = 1f;
               view.Pipeline.BindPipeline(commandBuffer, viewport.ShaderSlot, default, default, push);
               commandBuffer.Dispatch((width + Group - 1) / Group, (height + Group - 1) / Group, 1);

               Hazard(commandBuffer);
           }
   ```

   Source: [src/KSArmory/Ksa/SensorPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/SensorPass.cs#L57-L90). Any exception logs once, clears the wanted set and never throws into the render loop. The last hazard uses destination `AllCommandsBit`, because bloom and the composite follow.

5. **Measure with a log-average in a single invocation**, then map about that mean so the result goes back into scene units:

   ```glsl
   // src/KSArmory/Shaders/KSArmorySensor.comp L29-L43
   // The frame's mean brightness off a sparse grid, by one invocation in its own dispatch: read while
   // the mapping pass writes the image, the cells would already be grey.
   void Measure(ivec2 size)
   {
       float sum = 0.0;
       for (int y = 0; y < 16; y++)
       {
           for (int x = 0; x < 16; x++)
           {
               ivec2 at = ivec2((vec2(x, y) + 0.5) / 16.0 * vec2(size));
               sum += log(max(dot(imageLoad(sceneColor, at).rgb, Luma), 1.0e-4));
           }
       }
       imageStore(meanLevel, ivec2(0), vec4(exp(sum / 256.0)));
   }
   ```

   ```glsl
   // src/KSArmory/Shaders/KSArmorySensor.comp L61-L94
       vec4 c = imageLoad(sceneColor, px);
       float y = max(dot(c.rgb, Luma), 0.0);
       float mid = max(imageLoad(meanLevel, ivec2(0)).r, 1.0e-4);

       vec2 uv = (vec2(px) + 0.5) / vec2(size);
       bool sky = texture(sceneDepth, uv).r <= 0.0;

       // Into 0..1 about the mean, where a curve and an inversion mean something.
       float heat = y;
       if (mode != 1 && sky) heat *= 0.12;
       float t = heat / (heat + mid);

       if (mode == 1)
       {
           t = smoothstep(0.0, 1.0, t);
       }
       else
       {
           // Thermal detail is coarse and contrasty; the hottest things clip.
           t = clamp((t - 0.18) * 1.6, 0.0, 1.0);
           if (mode == 3) t = 1.0 - t;
           // A faint fixed column pattern, as an uncooled detector leaves.
           t += (Hash(vec2(px.x, 0.0)) - 0.5) * 0.02;
       }

       float grain = (Hash(vec2(px) + fract(pc.ModeTime.y * 13.7)) - 0.5) * (mode == 1 ? 0.02 : 0.025);
       vec2 d = uv - 0.5;
       float vignette = 1.0 - dot(d, d) * 0.6;

       t = clamp((t + grain) * vignette, 0.0, 0.999);

       // Back out of 0..1 onto the scene's scale, so the tonemap exposes it as it did the colour.
       float g = mid * t / (1.0 - t);
       imageStore(sceneColor, px, vec4(vec3(g), c.a));
   ```

   Sources: [src/KSArmory/Shaders/KSArmorySensor.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmorySensor.comp#L29-L43), [src/KSArmory/Shaders/KSArmorySensor.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmorySensor.comp#L61-L94). The trick: `y/(y+mid)` and its inverse `mid * t/(1-t)` act on a 0..1 value centred on the frame's own mean and return to scene units. In this look "heat" is scene luminance with the sky (depth 0 in reverse-Z) read cold; KSA has no per-pixel temperature.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/SensorPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/SensorPass.cs) | Wanted set, per-viewport pipeline cache, two dispatches |
| [src/KSArmory/Shaders/KSArmorySensor.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmorySensor.comp) | Log-mean measure, TV/thermal mapping |
| [src/KSArmory/Ksa/CloudPassHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPassHook.cs) | The prefix |

## Variations and gotchas

- **Main view excluded by design;** the wanted set is a frame's state rebuilt in the mod's step. With the HUD hidden, the `OnFrameCelestials` prefix still rebuilds it before the render.
- **Grain is on wall time** (`Environment.TickCount64`), so it keeps moving while the game is paused.
- **Restrict to one secondary viewport** by keying everything on the `IViewport`; do not touch the main view.
- **Planned design differs.** The mod's older planning notes describe a full-screen post-process routed through another mod; the built feature is this per-viewport compute look.

## See also

- Sibling: [grading passes on the HDR scene](colour-grade-the-frame-and-add-a-vignette-1.md); [overview](overview.md)
- [TV and thermal look for a camera window, recorded before bloom](../../../../mods/ksarmory/features/sensor-look-pass.md), [A mod compute pass recorded into KSA's frame just before bloom](../../../../mods/ksarmory/features/compute-pass-before-bloom.md)
- [Paint into or rescale the HDR scene with a compute pass shipped as a Shader asset](../../../rendering/vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/overview.md), [Render ImGui UI into a Vulkan texture for use in the 3D world](../../../rendering/textures-and-decals/render-imgui-into-a-texture/overview.md)
