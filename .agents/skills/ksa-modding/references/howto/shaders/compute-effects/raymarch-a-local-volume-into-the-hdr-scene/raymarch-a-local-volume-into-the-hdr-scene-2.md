---
type: How-To
title: Light a mod volume with KSA's sky and hide it behind weather clouds
description: Bind the viewport's atmosphere LUT set and the cloud renderer's weather images and shadow buffers to a compute pass, so a mod volume is lit by the real sky and sits correctly behind or in front of KSA's clouds
category: shaders
group: compute-effects
topic: raymarch-a-local-volume-into-the-hdr-scene
approach: 2
difficulty: expert
prerequisites: [starmap, harmony, vulkan]
demonstrated_by: [mods/ksarmory/features/raymarched-mushroom-cloud]
techniques: [reflection, glsl-shader, vulkan-pipeline]
hooks: [harmony-prefix KSA.SunbloomRenderer.Render]
lifecycle: [render:bloom, render:atmosphere]
game_types:
  - KSA.AtmosphereRenderer
  - KSA.Atmosphere.Rendering.CloudRenderer
  - KSA.Atmosphere.Rendering.CloudShadowsRenderer
  - KSA.Atmosphere.Rendering.CloudShadowRenderData
  - KSA.PlanetTransparenciesRenderer
  - KSA.Rendering.ComputePipelineWrapper
  - KSA.Program
  - KSA.GameSettings
tags: [atmosphere, lut, ambient, weather, clouds, shadows, reflection, descriptor-sets, compute, ksarmory]
---
A volume that ignores the sky looks pasted on: its colour is the same at sunrise, at altitude and on a body whose air is not Earth's, and it is drawn in front of every cloud because KSA's weather clouds write no depth. KSArmory's mushroom cloud avoids both by using the game's own data: the same ambient-light lookup the engine lights its clouds and meshes with, the atmosphere's transmittance and aerial-perspective LUTs for haze, the cloud renderer's finished images to know where weather stands in front of each sample, and the weather's shadow buffers so a cloud under a deck is shaded by it. This article is the "light and occlude" half of [A raymarched mushroom cloud, held body-fixed and drawn before bloom](../../../../mods/ksarmory/features/raymarched-mushroom-cloud.md); the march itself is in [the previous article](raymarch-a-local-volume-into-the-hdr-scene-1.md).

## Background: how the game does it

- **Global set 0 is already there.** `ComputePipelineWrapper` binds `GlobalShaderBindings.DescriptorSetLayout` at set 0 for every compute pass, so `global.lighting.planetPosition`, `planetRadius`, `atmosphereHeight`, `sunPosition`, `sunColor` and `atmosphereLutLayer` are in a mod shader with no descriptor of its own ([current/Content/Core/Shaders/Common/Global.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/Global.glsl#L13-L71)). The planet block describes the camera's effective nearby celestial and is filled only for atmospheric bodies.
- **The LUTs are per viewport.** `AtmosphereRenderer.GetAtmosphereLutsDescriptorSet(int shaderSlot)` returns the viewport's set (transmittance array, aerial perspective volumes, aerial range, ambient array) and `GetAtmosphereLutsDescriptorSetLayout()` its layout:

  ```csharp
  // current/decomp/KSA/AtmosphereRenderer.cs L275-L283
  public DescriptorSetLayoutEx GetAtmosphereLutsDescriptorSetLayout()
  {
      return _atmosphereLutsDescriptorSetLayout;
  }

  public VkDescriptorSet GetAtmosphereLutsDescriptorSet(int inShaderSlot)
  {
      return (GetResources(inShaderSlot) ?? _fallbackResources)?.LutsDescriptorSet ?? default(VkDescriptorSet);
  }
  ```

  Source: [current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L275-L283)

- **The sky function.** `GetAmbient(worldPosition, planetPosition, directionToSun, bottomRadius, topRadius, ambientLut, lutLayer)` samples the ambient LUT; the game's meshes and clouds call the same function:

  ```glsl
  // current/Content/Core/Shaders/Atmosphere/AtmosphereLuts.glsl L463-L468
  vec3 GetAmbient(vec3 worldPosition, vec3 planetPosition, vec3 directionToSun, float bottomRadius, float topRadius,
      sampler2DArray ambientLut, float lutLayer)
  {
      vec2 multipleScatteringLutCoords = GetAmbientLutCoords(worldPosition, planetPosition, directionToSun, bottomRadius, topRadius);
      return textureLod(ambientLut, vec3(multipleScatteringLutCoords, lutLayer), 0.0).rgb;
  }
  ```

  Source: [current/Content/Core/Shaders/Atmosphere/AtmosphereLuts.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/AtmosphereLuts.glsl#L463-L468)

- **Weather writes no depth.** The weather is marched by the cloud renderer, upscaled into flip and flop images, and composited. The renderer runs right after the atmosphere in `PlanetTransparenciesRenderer`, which is before the sunbloom hook:

  ```csharp
  // current/decomp/KSA/PlanetTransparenciesRenderer.cs L292-L297
  _atmosphereRenderer.RenderWithCompute(value2, inCommandBuffer, inViewport, frameIndex);
  TransitionWriteTargets(inCommandBuffer, offscreenTarget, inAtmosphereOnly);
  if (flag)
  {
      bool volumetricsComposited;
      bool flag6 = _cloudRenderer.RenderWithCompute(value2.AtmosphericBody, inCommandBuffer, inViewport, frameIndex, frameNumber, _atmosphereRenderer.GetAtmosphereLutsDescriptorSet(inViewport.ShaderSlot), _atmosphereRenderer.GetAtmosphereDataSet(), _atmosphereRenderer.GetAtmosphereDataDynamicOffset(inViewport.ShaderSlot, frameIndex, value2.AtmosphericBody), out volumetricsComposited);
  ```

  Source: [current/decomp/KSA/PlanetTransparenciesRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetTransparenciesRenderer.cs#L292-L297)

- **Build 5554 has public getters** for the accumulated images and the flag saying which one is history:

  ```csharp
  // current/decomp/KSA.Atmosphere.Rendering/CloudRenderer.cs L311-L343
  public RenderImage GetUpscaledCloudColorFlipTarget()
  {
      return _upscaledCloudColorFlipTarget;
  }

  public RenderImage GetUpscaledCloudColorFlopTarget()
  {
      return _upscaledCloudColorFlopTarget;
  }

  public RenderImage GetUpscaledDistanceFlipTarget()
  {
      return _upscaledDistanceFlipTarget;
  }

  public RenderImage GetUpscaledDistanceFlopTarget()
  {
      return _upscaledDistanceFlopTarget;
  }

  public RenderImage GetUpscaledAverageDensityFlipTarget()
  {
      return _upscaledAverageDensityFlipTarget;
  }

  public RenderImage GetUpscaledAverageDensityFlopTarget()
  {
      return _upscaledAverageDensityFlopTarget;
  }

  public bool IsUpscaledHistoryInFlip()
  {
      return !writeHistoryToFlip;
  ```

  Source: [current/decomp/KSA.Atmosphere.Rendering/CloudRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Atmosphere.Rendering/CloudRenderer.cs#L311-L343)

  The mod reads the private fields instead (see the gotchas); the public methods make that reflection unnecessary on this build.

## Lifecycle and timing

The pipeline and its descriptor writes are built lazily on the first recorded frame and rebuilt when the weather images or the shadow data object change (the accumulated pair alternates between two sets of images, so two pipelines are held). Binding and dispatch happen in the prefix on `SunbloomRenderer.Render` (`render:bloom`), after `render:atmosphere` has drawn the sky and the weather for this frame. The weather images are therefore this frame's, and the shadow buffers hold this frame's slice. KSA renders weather clouds for the main viewport only, so in a camera window the mod binds stand-ins and tells the shader not to read them. See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Include the game's atmosphere code and pick the descriptor slots.** The shader includes the generated `CoreAtmosphere.glsl` (see [Include KSA's own GLSL library from a mod shader](../../glsl-workflow/include-the-game-shader-library-in-mod-glsl/overview.md)) after defining where the game's bindless textures live and pointing the cloud-data slot at an unused set:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryCloud.comp L8-L19
   // Set 0 is the engine's GLOBAL set and ComputePipelineWrapper already binds it for every compute
   // pass -- so the lighting block and the ambient LUT are in this shader without a descriptor of
   // this mod's own. That is the whole of why the real sky costs no plumbing.
   //
   // SET_TEXTURE is KSA's bindless textures, bound at 2 by CloudPass, which the weather's coverage
   // maps are read out of; the weather's shadow data is in this pass's own set, below. SET_CLOUD_DATA is pointed at a
   // slot this pass does not use, the way Core's own Volumetric.comp does it: the headers declare it,
   // nothing here calls what they declare, and an unused declaration binds nothing.
   #define SET_TEXTURE 2
   #define SET_CLOUD_DATA 5

   #include "Content/CoreAtmosphere.glsl"
   ```

   Source: [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L8-L19)

2. **Declare the LUT set at 3 and the weather images in the pass's own set.** The LUT bindings are the ones the game's own cloud march reads them at. The weather colour (light with transmittance in alpha) and distance images are ordinary samplers in the pass's own set:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryCloud.comp L50-L61
   layout (set = 3, binding = 0) uniform sampler2DArray transmittanceLut;
   layout (set = 3, binding = 1) uniform sampler3D aerialColorRgbTransmittanceRLut;
   layout (set = 3, binding = 2) uniform sampler3D aerialTransmittanceGbLut;
   layout (set = 3, binding = 3) uniform sampler2D aerialRangeLut;
   layout (set = 3, binding = 7) uniform sampler2DArray ambientLut;

   // KSA's own weather clouds as they were drawn this frame, at the cloud renderer's low resolution:
   // their light with transmittance in alpha, and how far each pixel's cloud is from the camera in
   // kilometres. They write no depth, so without these everything drawn here off the depth buffer is
   // drawn in front of every cloud. Stand-ins when clouds are off -- see weatherBound.
   layout (set = 1, binding = 4) uniform sampler2D weatherColor;
   layout (set = 1, binding = 5) uniform sampler2D weatherDistance;
   ```

   Source: [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L50-L61)

3. **Build the pipeline with the weather images as colour samplers, the textures and LUTs as external sets, and the shadow buffers as uniforms.** The builder numbers its own set 1 (storage images, then depth sampler, then colour samplers, then uniform buffers, then dynamic uniform buffers) and externals from set 2. With clouds off the pass's own distance layer stands in, and a flag tells the shader not to read it:

   ```csharp
   // src/KSArmory/Ksa/CloudPass.cs L2177-L2220
   // KSA's weather clouds, so the burst is drawn behind the ones in front of it. With clouds
   // switched off there is nothing to bind and a descriptor cannot be left empty, so the
   // pass's own distance layer stands in and CloudFlags tells the shader not to read it.
   IRenderImage[] weather =
   [
       (IRenderImage?)weatherColour ?? view.LayerDistance,
       (IRenderImage?)weatherDistance ?? view.LayerDistance,
   ];
   VkPushConstantRange[] ranges =
   [
       new VkPushConstantRange
       {
           Offset = (ByteSize32)0,
           Size = (ByteSize32)Marshal.SizeOf<Push>(),
           StageFlags = VkShaderStageFlags.ComputeBit,
       },
   ];

   // KSA's bindless textures at set 2, which the weather's coverage maps are read out of; the
   // builder numbers external sets from 2, and KSA declares this one for compute as well. Then
   // the atmosphere's LUTs at 3, the set Core's own cloud march reads them from; the scalars
   // the aerial-perspective call wants are in the global set's lighting block, so there is no
   // uniform buffer here and nothing of this mod's to keep in step with the engine.
   VkDescriptorSetLayout[] external =
   [
       Program.Instance.TextureSystem.Layout,
       Program.PlanetAtmosphereRenderer.GetAtmosphereLutsDescriptorSetLayout(),
   ];

   // And the weather's shadow data in this pass's own set, after everything above: bindings 6
   // and 7 in the builder's order, the second advanced a slice per frame in flight.
   VkBuffer[] shadowData = [shadowFixed];
   VkBuffer[] shadowPerFrame = [shadowFrame];
   ByteSize[] shadowSlice = [CloudShadowRenderData.DynamicUboStride];

   using Specialization tuned = new();
   _pipeline = new ComputePipelineWrapper(
       storageTargets, depthTargets, weather, default, shader,
       external, ranges, renderer.MaxFramesInFlight, renderer,
       "KSArmory.CloudPass", Program.PointClampedSampler, Program.LinearClampedSampler,
       specializationInfo: tuned.Info,
       uniformBuffers: shadowData,
       uniformDynamicBuffers: shadowPerFrame,
       uniformDynamicBufferRanges: shadowSlice);
   ```

   Source: [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L2177-L2220)

   The shadow data goes in as buffers rather than KSA's own descriptor set because that set's layout is declared for fragment shaders only and read from a compute pass it returned garbage (every float a NaN).

4. **Bind the per-viewport sets and the per-frame offset on every dispatch.**

   ```csharp
   // src/KSArmory/Ksa/CloudPass.cs L2227-L2240
   // Binds the cloud pipeline with the bindless textures, this viewport's atmosphere LUTs, and its
   // own set at this frame's slice of the weather's per-frame shadow buffer -- the pass's only
   // dynamic offset -- which is how KSA's own passes read it.
   private static void BindCloud(CommandBuffer commandBuffer, IViewport viewport, Camera camera, Push push)
   {
       Span<VkDescriptorSet> sets = stackalloc VkDescriptorSet[2];
       sets[0] = Program.Instance.TextureSystem.DescriptorSet;
       sets[1] = Program.PlanetAtmosphereRenderer.GetAtmosphereLutsDescriptorSet(viewport.ShaderSlot);

       Span<ByteSize32> offsets = stackalloc ByteSize32[1];
       offsets[0] = Program.Instance.ResourceFrameIndex * CloudShadowRenderData.DynamicUboStride;

       _pipeline!.BindPipeline(commandBuffer, viewport.ShaderSlot, sets, offsets, push);
   }
   ```

   Source: [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L2227-L2240)

5. **Light each sample with the real sky.** Per march step, call `GetAmbient` at the sample, toward the sun, with the planet from the global block, and scale by the sun colour. The mod's comment states the point: the same call, the same LUT and the same layer the engine lights its weather clouds and meshes with, so the volume is lit correctly at sunrise, at altitude and on a body whose air is not Earth's:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryCloud.comp L2120-L2133
   // toward the sun and toward the horizon, and a single number for it is what makes a volume
   // read as a cut-out.
   //
   // KSA's own sky, not an approximation of one: the same call, the same LUT and the same
   // layer the engine lights its weather clouds and its meshes with, so this cloud is lit by
   // the atmosphere everything else in frame is lit by -- at sunrise, at altitude, and on a
   // body whose air is not Earth's, all of which a hand-written tint gets wrong.
   vec3 camRelative = dir * (t0 + (float(i) + jitter) * dt);
   float atmosRadius = global.lighting.planetRadius + global.lighting.atmosphereHeight;

   vec3 sky = GetAmbient(camRelative, global.lighting.planetPosition.xyz,
                         normalize(global.lighting.sunPosition.xyz - camRelative),
                         global.lighting.planetRadius, atmosRadius, ambientLut,
                         float(global.lighting.atmosphereLutLayer))
   ```

   Source: [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L2120-L2133)

   The aerial-perspective call `GetAerialPerspectiveForObjectInAtmosphere` (same LUT set) hazes a far cloud like terrain at that range.

6. **Find the weather images, by reflection if you must.** Reach the weather renderer through the one private field that owns it, `Program._planetTransparenciesRenderer` (the public `GetCloudRenderer()` is on the type), then prefer the accumulated pair, which holds still while the world is paused, over the jittered low-resolution pair, whose edges crawl:

   ```csharp
   // src/KSArmory/Ksa/KsaWorld.cs L1292-L1316
   if (!_lookedForWeather)
   {
       _lookedForWeather = true;
       _transparencies = typeof(Program)
                         .GetField("_planetTransparenciesRenderer",
                                   BindingFlags.NonPublic | BindingFlags.Instance)
                         ?.GetValue(Program.Instance);

       if (_transparencies is not PlanetTransparenciesRenderer && !_warnedAboutWeather)
       {
           _warnedAboutWeather = true;
           Log.Warn("weather clouds: Program._planetTransparenciesRenderer did not resolve; "
                    + "a burst will be drawn in front of them");
       }
   }

   if (_transparencies is not PlanetTransparenciesRenderer owner) return false;
   if (owner.GetCloudRenderer() is not { } clouds) return false;

   if (TrySettledWeather(clouds, out colour, out distance)) return true;

   colour = clouds.GetLowResolutionCloudColorTarget();
   distance = clouds.GetLowResolutionCloudDistanceTarget();

   return colour is not null && distance is not null;
   ```

   Source: [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L1292-L1316)

   The accumulated images alternate every frame; the image just written is the one the flag no longer names:

   ```csharp
   // src/KSArmory/Ksa/KsaWorld.cs L1357-L1366
       if (_settled.Length != 4 || _writesFlipNext is null) return false;

       bool flipIsNext = (bool)_writesFlipNext.GetValue(clouds)!;
       int written = flipIsNext ? 1 : 0;

       colour = _settled[written]!.GetValue(clouds) as KSA.Rendering.RenderImage;
       distance = _settled[2 + written]!.GetValue(clouds) as KSA.Rendering.RenderImage;

       return colour is not null && distance is not null;
   }
   ```

   Source: [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L1357-L1366)

   If any reflection step fails the mod logs once and falls back to the public low-resolution pair; if the owner is gone the volume is drawn in front of the weather.

7. **Find the weather shadow buffers, by reflection.** `Program.GetCloudShadowsRenderer()` is public; its per-body dictionary and the stand-in object are private, and the buffers inside each data object are private too:

   ```csharp
   // src/KSArmory/Ksa/KsaWorld.cs L1388-L1418
   var renderer = Program.GetCloudShadowsRenderer();
   const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

   _shadowPlanets ??= renderer.GetType().GetField("PlanetToCloudShadowData", Private);
   _shadowNone ??= renderer.GetType().GetField("_noShadowData", Private);

   object? data = null;
   if (GameSettings.ShowClouds()
       && _shadowPlanets?.GetValue(renderer) is System.Collections.IDictionary planets
       && planets.Contains(body.Hash))
   {
       data = planets[body.Hash];
   }

   data ??= _shadowNone?.GetValue(renderer);
   if (data is null) return Unshadowed("the renderer's shadow data did not resolve");

   Type type = data.GetType();
   _shadowStatic ??= type.GetField("_staticShadowDataBuffer", Private);
   _shadowFrame ??= type.GetField("_dynamicShadowDataBuffer", Private);

   if (_shadowStatic?.GetValue(data) is not Brutal.VulkanApi.Abstractions.BufferEx fixedPart
       || _shadowFrame?.GetValue(data) is not Brutal.VulkanApi.Abstractions.BufferEx movingPart)
   {
       return Unshadowed("its buffers did not resolve");
   }

   staticBuffer = fixedPart.VkBuffer;
   frameBuffer = movingPart.VkBuffer;
   source = data;
   return true;
   ```

   Source: [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L1388-L1418)

   The buffers match the game's declarations (`PlanetToCloudShadowData`, `_noShadowData`, `_staticShadowDataBuffer`, `_dynamicShadowDataBuffer`):

   ```csharp
   // current/decomp/KSA.Atmosphere.Rendering/CloudShadowRenderData.cs L20-L30
   private BufferEx _staticShadowDataBuffer;

   private BufferEx _dynamicShadowDataBuffer;

   private MappedMemory _staticShadowDataMemory;

   private MappedMemory _dynamicShadowDataMemory;

   public static readonly ByteSize StaticUboStride = ByteSize.Of<CloudShadowStaticData>().AlignTo(Utils.MinUniformBufferOffsetAlignment);

   public static readonly ByteSize DynamicUboStride = ByteSize.Of<DynamicShadowDataArray>().AlignTo(Utils.MinUniformBufferOffsetAlignment);
   ```

   Source: [current/decomp/KSA.Atmosphere.Rendering/CloudShadowRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Atmosphere.Rendering/CloudShadowRenderData.cs#L20-L30)

   Clouds switched off (`GameSettings.ShowClouds()` false) uses the no-shadow stand-in, which carries no layers.

8. **Use the weather in the march.** `WeatherInFront(t)` dims any sample behind a weather sheet along the ray; the sheets are placed from the two images before the march:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryCloud.comp L514-L527
   // How much of the light from a point t along the ray gets through the weather in front of it.
   float WeatherInFront(float t)
   {
       float through = 1.0;
       for (int i = 0; i < sheetCount; i++)
       {
           through *= sheetFog[i] > 0.0
                          ? mix(sheetClear[i], 1.0, exp(-t / sheetFog[i]))
                          : mix(1.0, sheetClear[i],
                                smoothstep(sheetAt[i] - sheetBand[i], sheetAt[i] + sheetBand[i], t));
       }

       return through;
   }
   ```

   Source: [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L514-L527)

   `WeatherShadow(worldPos, sunDir)` is the lowest-quality path of KSA's own weather shadow: where the sun ray crosses each layer's middle, one read of that layer's coverage (the better paths sharpen the edges with derivatives a compute shader does not have).

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs) | `Build`, `BindCloud`, view and pipeline cache keyed by weather images and shadow data |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs) | `TryWeatherClouds`, `TrySettledWeather`, `TryWeatherShadowBuffers` (L1264-L1441) |
| [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp) | Includes, set 3 LUTs, `WeatherInFront`, `WeatherShadow`, the `GetAmbient` call |

## Variations and gotchas

- **Prefer the public getters on build 5554.** `CloudRenderer` exposes `GetUpscaledCloudColorFlipTarget()`, `...FlopTarget()`, the distance pair and `IsUpscaledHistoryInFlip()`, which would replace the four private fields and `writeHistoryToFlip` the mod reads by name. A rename of any private member makes the mod fall back to the jittered low-resolution pair (edges crawl) or, if the owner is gone, to drawing in front of the weather.
- **Weather shadows are reflection too, and gate the whole pass.** If `TryWeatherShadowBuffers` fails, the pass returns before anything is drawn on that path: cloud, marks, sky and flash, and also the rings, holes, leak streams and tracers.
- **Main view only.** In a camera window the cloud draws with the "no weather" flag and ignores clouds in front of it.
- **Do not bind the game's fragment-only descriptor set to compute.** Its layout is declared for fragment stages; the mod takes the two buffers into its own compute set instead.
- **Shared gates.** The lighting needs a nearby celestial and the planet block; the pass checks `camera.NearbyCelestial` and returns without one.
- **A rebuilt weather renderer invalidates the pipeline.** The renderer is rebuilt when settings change and the images when the window is resized, so the mod asks every frame rather than holding the images.

## See also

- Siblings: [march and resolve](raymarch-a-local-volume-into-the-hdr-scene-1.md), [analytic sky dispatches](raymarch-a-local-volume-into-the-hdr-scene-3.md), [overview](overview.md)
- [A raymarched mushroom cloud, held body-fixed and drawn before bloom](../../../../mods/ksarmory/features/raymarched-mushroom-cloud.md), [A generated include that reaches KSA's own shader library from a mod shader](../../../../mods/ksarmory/features/core-shader-library-include.md), [A mod compute pass recorded into KSA's frame just before bloom](../../../../mods/ksarmory/features/compute-pass-before-bloom.md)
- [Include KSA's own GLSL library from a mod shader](../../glsl-workflow/include-the-game-shader-library-in-mod-glsl/overview.md), [Extend KSA's sky and cloud shaders: per-pixel haze and cloud lighting fixes](../../stock-shaders/extend-stock-sky-and-cloud-shaders/overview.md), [Choose a render frame and anchor effects to bodies, parts and terrain](../../../rendering/overlays/choose-a-render-frame-and-anchor-effects/overview.md)
