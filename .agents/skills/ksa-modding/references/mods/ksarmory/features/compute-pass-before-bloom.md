---
type: Mod Feature
title: A mod compute pass recorded into KSA's frame just before bloom
description: One Harmony prefix on the public SunbloomRenderer.Render hands the mod KSA's command buffer while the HDR scene colour is in a storage layout and depth is sampleable, and the mod dispatches its own Shader-asset compute pipelines there through KSA's ComputePipelineWrapper
mod: ksarmory
category: shaders
techniques:
  - harmony-prefix
  - vulkan-pipeline
  - vulkan-buffer
  - glsl-shader
  - post-processing
  - xml-asset
  - reflection
  - gpu-profiling
hooks:
  - harmony-prefix KSA.SunbloomRenderer.Render
  - reflection Core.Renderer.Allocator
  - reflection KSA.FileReference.DoLoad
lifecycle: [startup:asset-load, startup:all-mods-loaded, render:bloom]
game_types:
  - KSA.SunbloomRenderer
  - KSA.Rendering.ComputePipelineWrapper
  - KSA.ShaderReference
  - KSA.Rendering.BarrierBatch
  - KSA.Rendering.ImageBarrierInfo
  - KSA.Rendering.RenderImage
  - KSA.Program
  - Core.Renderer
  - KSA.GpuProfiler
  - KSA.ProfilerTag
  - KSA.FileReference
tags:
  - vulkan
  - compute
  - post-processing
  - harmony
  - render-pass
  - barriers
  - descriptor-sets
  - push-constants
  - gpu-profiler
  - specialization-constants
---

KSArmory draws its nuclear cloud, the sky a high burst lights, blast-front refraction, ground
rings, shell holes, tank leaks, compute tracers, the fireball's light on nearby hulls, the
whiteout and the view shake as **compute dispatches written straight into KSA's own HDR scene
image**, inside KSA's own command buffer. No renderer is ported, no transpiler matches IL, and no
framework mod is required: KSA compiles the mod's `.comp` files as ordinary `<Shader>` assets,
KSA's `ComputePipelineWrapper` builds the pipelines, and one Harmony prefix on the **public**
`SunbloomRenderer.Render(CommandBuffer, IViewport, int)` supplies the moment to dispatch.[^ksarmory-cloudpasshook]

That moment is chosen with care: KSA has just barriered the scene colour into `General` layout and
the depth into a sampled layout for its own use, everything the scene contains has been drawn, and
bloom and the tonemap composite are still to come, so whatever the mod writes blooms and is graded
like the rest of the picture.[^ksa-program] This file is the framework; the individual effects
plug into it and are documented separately (see the end).

## How it works

### 1. Shaders are KSA assets

`src/KSArmory/KSArmoryParticles.xml` L563-L571 declares nine `<Shader Id="..." Path="Shaders/X.comp"/>`
entries.[^ksarmory-particles-xml] `AssetBundle` maps the `Shader` element to `ShaderReference`;
its `OnDataLoad` registers it as a loader, and `ModLibrary.LoadAll` runs every loader in a
`Parallel.ForEachAsync` (L510-L514), where `ShaderReference.DoLoad` compiles the file with shaderc
(`ShaderModuleUtils.FromFile`, include callback attached) and stores the `VkShaderModule`.[^ksa-assetbundle][^ksa-filereference][^ksa-modlibrary][^ksa-shaderreference]
The mod later asks `ModLibrary.TryGet<ShaderReference>(id, out var shader)`; a GLSL error leaves
`shader.Shader` null (KSA logs the compile error in its own log from `FileReference.Load`), and
`ComputePipelineWrapper.BuildPipeline` then throws `"Compute Shader is null"`, which is where the
mod first sees the fault.[^ksa-cpw]

### 2. Pipelines come from `ComputePipelineWrapper`

The constructor takes image and buffer lists and writes one descriptor set for them, in a fixed
binding order (`Utils.WriteBindings`):[^ksa-cpw][^ksa-utils]

| Set | Contents |
|---|---|
| 0 | `GlobalShaderBindings` for the viewport slot (camera, lighting, global constants), bound automatically |
| 1 | The wrapper's own set: storage images first, then combined image samplers (depth targets with the point-clamp sampler in a depth read-only layout, then linear colour, point colour, read-only views), then uniform buffers, dynamic uniform buffers, storage buffers |
| 2+ | `externalDescriptorSetLayouts`, in the order passed (when `bindTextures` is false) |

The mod's pipelines, all with a single 8x8 (or 16x8) workgroup compute stage:[^ksarmory-cloudpass]

| Pipeline | Set 1 bindings | Externals |
|---|---|---|
| Cloud (`KSArmoryCloud.comp`) | 0 scene colour, 1 layer colour, 2 layer distance (storage); 3 scene depth; 4-5 KSA's weather cloud colour/distance (linear); 6 weather shadow UBO; 7 weather shadow dynamic UBO | set 2 `TextureSystem.Layout` (bindless), set 3 atmosphere LUTs |
| Resolve | 5 storage images (target, layer pair, history pair) | none |
| Shock | scene colour, scene copy (storage); depth | none |
| Rings | scene colour; depth | none |
| Holes, Leaks, Tracers | scene colour; depth; one host-visible storage buffer | none |
| Fire light | scene colour; depth and pre-pass depth; pre-pass normal and irradiance views | set 2 `TextureSystem.Layout` |

Push constants are one `VkPushConstantRange` per pipeline; the cloud's `Push` struct is exactly
128 bytes (a `float4x4` and four `float4`), Vulkan's guaranteed minimum, which is why directions are
octahedral-packed into two floats.[^ksarmory-cloudpass] `ShaderTunables` overrides become
`VkSpecializationInfo` built with `Marshal` and freed after the constructor returns.

### 3. Recording a dispatch

`BindPipeline<T>(cmd, viewport.ShaderSlot, externalSets, dynamicOffsets, push)` binds the compute
pipeline, binds sets `[global(slot), own, externals...]` with the dynamic offsets, and pushes the
constants; the mod then calls `commandBuffer.Dispatch(groupsX, groupsY, 1)`.[^ksa-cpw]

- Pass `viewport.ShaderSlot`, never the frame index, as the viewport argument: it selects the
  viewport's global set, and a frame index reads another viewport's lighting block.
- The only dynamic offset is `ResourceFrameIndex * CloudShadowRenderData.DynamicUboStride`
  into KSA's weather shadow buffer; offsets are consumed in set order.[^ksarmory-modding-notes]
- Between two dispatches that both read and write the scene, `Hazard` records a
  `VkMemoryBarrier2` (compute write to compute read/write) through KSA's `BarrierBatch`.
- The mod's own images are transitioned with `BarrierBatch.Add(RenderImage, ImageBarrierInfo)`,
  which builds the barrier from the image's tracked state (`RenderImage.CreateBarrier`), so the
  next transition, KSA's or the mod's, starts from where this one left it.[^ksa-barrierbatch][^ksa-renderimage]
- Ground marks, holes, leaks and tracers dispatch only over their screen-space bounding tile
  (origin in the push constants), not the whole screen.

### 4. Per-viewport and per-frame resources

| Resource | Lifetime |
|---|---|
| `View` (layer colour/distance, two history images, a scene copy, the effect pipelines) | One per `viewport.ShaderSlot`; rebuilt when the target image or its size changes, because descriptor sets name images |
| Replaced images | Put in a graveyard and disposed 16 recorded frames later, after any frame in flight could still read them |
| Cloud pipelines | Up to two per view, keyed by the weather images and shadow-data object they were built against (KSA's accumulated weather pair alternates) |
| Hole, tracer and leak buffers | Created once with `allocator.CreateBuffer` (host-visible, coherent, mapped), sized `frames in flight x views x capacity`, sliced by `frameIndex % frames` and a per-frame view counter |
| Images it creates | `RenderImage.CreateColorStorage` (storage usage only), so history is read with `imageLoad` and filtered by hand, never sampled |

The allocator is read as `typeof(Renderer).GetProperty("Allocator")?.GetValue(renderer) is
IBufferAllocator`. In build 5554 `Allocator` is a public `KsaVmaAllocator` property on
`KSADeviceContextEx`, the renderer's base class, and `KsaVmaAllocator` implements
`IBufferAllocator`; the code does not say why it reflects rather than calling it.[^ksa-devicecontext]

### 5. The gate, the fault fence and the cost meter

- `CloudPassHook.Install(() => _config.ShaderPass)` (from `OnFullyLoaded`); the prefix calls
  `CloudPass.Record(cmd, viewport, frameIndex, max(0, tint))`, then `SensorPass.Record`.[^ksarmory-mod]
- `Config.ShaderPass` defaults to 1. At 0 `Record` still paints rings, holes, leaks and tracers
  and skips every burst visual (cloud, marks, sky, shock, shake, flash). The slider is shown only in
  developer builds; the scenario runner sets 0 for "noshader" control runs.[^ksarmory-config][^ksarmory-uidebug]
- `Record` is one `try`: any exception logs once, drops every view (`Release`) and sets
  `BuildFailed`, so a broken shader degrades to "no effect" instead of crashing the render loop.
- Tuning: a change to `ShaderTunables.Generation` releases everything and the next frame rebuilds
  with new specialization constants (a pipeline rebuild, no recompile).[^ksarmory-shadertunables]
- Reload: the file-bridge command `reload_shaders` invokes the internal `FileReference.DoLoad` by
  reflection on each of the mod's `ShaderReference`s, logs the old and new module handles, then
  calls `CloudPass.Release()`.[^ksarmory-bridge]
- Cost: every stage is wrapped in `commandBuffer.TagRegion(ProfilerTag)`; `CloudPassCost.Sample`
  (once per frame from the simulation step) reads `Profiler.Gpu.Frames[LastCompleteFrameIndex]`,
  sums samples named `KSArmory Cloud` and `KSArmory Cloud: <stage>`, and sums root samples
  (`ParentIndex == ushort.MaxValue`) for a whole-frame baseline. `Begin` turns
  `Profiler.Gpu.IsActive` on.[^ksarmory-cloudpasscost][^ksa-gpuprofiler][^ksa-gpuregion]

## Lifecycle and timing

The prefix runs inside `Program.Render` (L2324 of `OnFrame`) while `RenderGame` (L2473) records the
frame's single command buffer; nothing is submitted until `PostRender`. `SunbloomRenderer.Render`
has three call sites in build 5554:[^ksa-program]

| Call site | Viewport | When |
|---|---|---|
| `RenderViewport` L4584 | Each visible secondary viewport with `RenderAtmosphere` (the four camera windows have it) | First, from the loop at L4672-L4682 |
| `RenderGame` L4915 | `MainViewport` | After everything below |
| `RenderEditor` L5061 | `MainViewport` in the vehicle editor | Editor frames; the prefix does not check for this |

Order of the main viewport's passes (lifecycle value `render:bloom`):

| Lines | Pass | Phase |
|---|---|---|
| L4713 | `PrePassRenderer.Render` | prepass |
| L4716-L4720 | ambient occlusion | ambient-occlusion |
| L4721-L4768 | light shadow and compute passes, planet mesh, sun shadow, cascaded shadows | shadows, planet |
| L4775-L4778 | `RaytracingRenderer.RunRaytracePass` (IVA with ray tracing only) | raytrace |
| L4784 | `PrePassRenderer.CopyDepthImageToSrc` | |
| L4792-L4848 | stars, sun, part models, SuperMesh, `GizmosRenderer.Render` (L4842), RT rasterizer composite (L4845) | scene-opaque |
| L4850-L4851 | particle GPU update, opaque particles | particles |
| L4866 | ocean | ocean |
| L4875 / L4880 | atmosphere, weather clouds, volumetric trails | atmosphere |
| L4891 | volumetric exhaust | exhaust |
| L4893-L4903 | MSAA transparencies, glass, RT translucent, SuperMesh translucency, translucent particles | translucency |
| L4904, L4908 | colour resolve (no depth), underwater | |
| L4910-L4914 | colour to `StorageReadFc`, depth to `DepthSampledReadVfc` | |
| **L4915** | **`SunbloomRenderer.Render`: the prefix runs first** | bloom |
| L4916-L4925 | overall bloom, selection outline, colour back to attachment (or `SingleToMultisamplePass`) | bloom |
| L4929, L4931, L4935, L4939 | orbit lines (`DrawUI`), `GizmoPass`, grid, gauges (`DrawUI`) | orbit-lines, gizmos, grid, gauges |
| L4946-L4950 | screenshot capture, AA prepare, final composite (tonemap), ImGui | aa, imgui |

State at the prefix: the command buffer is recording and no dynamic rendering scope is open
(`EndRendering` at L4848; `SunbloomRenderer.Render` opens its own at L372), so compute dispatches
and pipeline barriers are legal. The scene colour is the resolved, single-sample
`R16G16B16A16SFloat` HDR image in `General` layout; depth is in `DepthStencilReadOnlyOptimal`.
The secondary path (L4578-L4583) and the editor path (L5056-L5060) apply the same two presets
before their call.[^ksa-program][^ksa-imagebarrierinfo]

### Raytracing interplay

Build 5554 has hardware ray tracing, but only for the cockpit view: `IsRaytracingActive` is
`GameSettings.Current.Graphics.IVARayTracing && _renderer.RaytracingPipelinesSupported` (L826), and
every RT call in `RenderGame` is further gated on `MainViewport.Mode == CameraMode.IVA`.[^ksa-program]

- All three RT steps run **before** the hook: `RunRaytracePass` (L4777) traces reflection and
  direct/indirect lighting images from the pre-pass depth and normals,[^ksa-raytracing] the
  opaque RT rasterizer composites them into the scene in the scene-opaque pass (L4845), and RT
  translucency writes its colour at L4897.
- So in IVA with RT on, the mod's pass reads (and paints over) the ray-traced result. Its own
  output is screen-space and after the fact: a cloud, flash or decal never enters the acceleration
  structure, so it is absent from RT reflections and casts no RT light or shadow.
- Secondary viewports never ray trace, and `RenderEditor` never calls `RunRaytracePass`.

### Why here

The mod's notes contrast this with the published alternative, an IL transpiler over
`Program.RenderGame` that matches a render-pass call and indexes locals, which a recompile can
break silently. A prefix on a public method with all three arguments has nothing to match, and
`PinTheSignature` puts the method in the mod's metadata so a signature change fails the build.[^ksarmory-cloudpasshook]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/CloudPassHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPassHook.cs) | Installs the prefix; `BeforeSunbloom` calls the cloud and sensor passes |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L236-L303) | `Release`, graveyard, per-viewport `View` |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L377-L795) | `Record`: the frame's dispatch order and fault fence |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L2143-L2278) | `Hazard`, `Build`, `BindCloud`, `BuildResolve` |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L1323-L1348) | A per-frame storage buffer from the reflected allocator |
| [src/KSArmory/Ksa/CloudPassCost.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPassCost.cs) | GPU cost from KSA's profiler |
| [src/KSArmory/KSArmoryParticles.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryParticles.xml#L558-L571) | The nine `<Shader>` assets |
| [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L1-L71) | Set and binding declarations matching the wrapper's order |
| [src/KSArmory/Sim/ShaderTunables.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ShaderTunables.cs) | Specialization constant ids and live overrides |
| [src/KSArmory/Ksa/Bridge.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Bridge.cs#L1093-L1172) | `tune`, `cost` and `reload_shaders` commands |

## Snippets

```csharp
// src/KSArmory/Ksa/CloudPassHook.cs L55-L110
public static void Install(Func<float> tint)
{
    _tint = tint;

    if (Installed) return;

    try
    {
        MethodInfo? target = typeof(SunbloomRenderer).GetMethod(
            nameof(SunbloomRenderer.Render), [typeof(CommandBuffer), typeof(IViewport), typeof(int)]);
        // ...
        MethodInfo prefix = typeof(CloudPassHook).GetMethod(
            nameof(BeforeSunbloom), BindingFlags.NonPublic | BindingFlags.Static)!;

        _harmony = new Harmony(HarmonyId);
        _harmony.Patch(target, prefix: new HarmonyMethod(prefix));

        Installed = true;
        // ...
}
// ...
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

```csharp
// src/KSArmory/Ksa/CloudPass.cs L2161-L2225
private static bool Build(IRenderImage colour, IRenderImage depth, View view,
                          RenderImage? weatherColour, RenderImage? weatherDistance,
                          VkBuffer shadowFixed, VkBuffer shadowFrame)
{
    if (!ModLibrary.TryGet<ShaderReference>(ShaderId, out var shader) || shader is null)
    {
        // ...
    }

    Renderer renderer = Program.GetRenderer();

    IRenderImage[] storageTargets = [colour, view.LayerColour, view.LayerDistance];
    IRenderImage[] depthTargets = [depth];
    // ...
    VkDescriptorSetLayout[] external =
    [
        Program.Instance.TextureSystem.Layout,
        Program.PlanetAtmosphereRenderer.GetAtmosphereLutsDescriptorSetLayout(),
    ];
    // ...
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
    // ...
}
```

```csharp
// src/KSArmory/Ksa/CloudPass.cs L2143-L2240
// One compute-write to compute-read barrier, so a dispatch sees what the one before it wrote.
private static void Hazard(CommandBuffer commandBuffer)
{
    Span<VkMemoryBarrier2> one = stackalloc VkMemoryBarrier2[1];
    BarrierBatch batch = new(one, default, default);

    VkMemoryBarrier2 barrier = new()
    {
        SrcStageMask = VkPipelineStageFlags2.ComputeShaderBit,
        SrcAccessMask = VkAccessFlags2.ShaderWriteBit,
        DstStageMask = VkPipelineStageFlags2.ComputeShaderBit,
        DstAccessMask = VkAccessFlags2.ShaderReadBit | VkAccessFlags2.ShaderWriteBit,
    };

    batch.Add(in barrier);
    batch.SubmitAndFlush(commandBuffer);
}
// ...
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

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `SunbloomRenderer.Render(CommandBuffer, IViewport, int)` | [current/decomp/KSA/SunbloomRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SunbloomRenderer.cs#L326-L394) | Prefix target: command buffer, viewport, frame index |
| `ComputePipelineWrapper` ctor, `BindPipeline<T>`, `BuildPipeline` | [current/decomp/KSA.Rendering/ComputePipelineWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ComputePipelineWrapper.cs#L32-L269) | Descriptor set, layout, pipeline, binding and push constants |
| `Utils.WriteBindings` | [current/decomp/KSA.Rendering/Utils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/Utils.cs#L296-L354) | The binding order the GLSL must follow |
| `ShaderReference.Shader`, `DoLoad` | [current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L85-L181) | The compiled module; recompiled by the reload command |
| `ModLibrary.TryGet<T>`, `LoadAll` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L478-L522) | Finding the shader; where it is compiled |
| `BarrierBatch`, `ImageBarrierInfo.Presets` | [current/decomp/KSA.Rendering/BarrierBatch.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/BarrierBatch.cs#L24-L87) | Hazard and layout barriers through tracked image state |
| `RenderImage.CreateColorStorage`, `CreateBarrier` | [current/decomp/KSA.Rendering/RenderImage.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderImage.cs#L118-L134) | Layer, history and scene-copy images |
| `KSADeviceContextEx.Allocator` (reflected via `Renderer`) | [current/decomp/Core/KSADeviceContextEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/KSADeviceContextEx.cs#L67) | Host-visible storage buffers |
| `Program.GetRenderer`, `PointClampedSampler`, `LinearClampedSampler`, `PlanetAtmosphereRenderer`, `TextureSystem`, `ResourceFrameIndex` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs) | Public members the pass is built from |
| `Profiler.Gpu` (`GpuProfiler.Frames`, `LastCompleteFrameIndex`, `IsActive`), `CommandBuffer.TagRegion` | [current/decomp/KSA/GpuProfiler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuProfiler.cs#L73-L83) | Timestamped regions and reading them back |
| `FileReference.DoLoad` (internal) | [current/decomp/KSA/FileReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FileReference.cs#L166) | Shader reload by reflection |

## Pitfalls and version notes

- **Write access is not declared to KSA.** The engine's preset before the call is `StorageReadFc`
  (`General`, `ShaderRead` only); the mod writes the image anyway and its `Hazard` barriers are
  compute-to-compute. The scene colour's tracked state therefore still says "read", and KSA's next
  transition is built from that source mask. It works in play; whether a Vulkan synchronization
  validation layer flags it was not checked here.[^ksa-imagebarrierinfo]
- **It also runs in camera windows and the editor.** The prefix fires for every call site above;
  `Record` keys its resources by `ShaderSlot`, uses the main view's weather only for the main
  view, and runs fire light only there. The editor call is not filtered.
- **Pipelines are dropped, not destroyed.** `Release` and view rebuilds set references to null;
  `ComputePipelineWrapper.Destroy(Device)` exists but is never called, so each rebuild (tuning,
  reload, window resize) leaves a pipeline, layout and descriptor pool behind.[^ksa-cpw]
- **A shader error is only visible at run time.** KSA compiles at load, so the C# build cannot
  catch GLSL faults; `CloudPassCost.Report` distinguishes "pass FAILED TO BUILD" from "pass off".
- **KSA's own hot reload does not help.** `ShaderReloader.EnableHotReloading` has no caller in
  build 5554, which is why the mod reflects `DoLoad` itself.[^ksa-shaderreloader]
- **The UI tooltip is stale.** `UiDebug` says the pass "does not dispatch" at zero and that "the
  spike tints the far field"; the code at zero still dispatches rings, holes, leaks and tracers.[^ksarmory-uidebug]
- **The whole burst path needs a nearby body and KSA's weather shadow buffers.** If
  `TryWeatherShadowBuffers` fails (private fields renamed), `Record` returns before marks, sky,
  clouds, tracers and the flash.
- **Push constants are at the 128-byte floor.** Adding a field means packing something else.
- **Line numbers in the mod's docs** cite an older corpus (`Program.cs:2096` and similar); in
  build 5554 the call is at L4915 and the secondary call at L4584.

## See also

- [The generated Core shader include](./core-shader-library-include.md)
- [Burst flash and view shake](./burst-flash-and-view-shake.md)
- [Raymarched mushroom cloud](./raymarched-mushroom-cloud.md)
- [High-altitude burst sky](./high-altitude-burst-sky.md)
- [Shock-front refraction](./shock-front-refraction.md)
- [Depth-painted ground rings](./depth-painted-ground-rings.md)
- [Bullet-hole decals](./bullet-hole-decals.md)
- [Compute shell tracers](./compute-shell-tracers.md)
- [Fireball light injection](./fireball-light-injection.md)
- [Sensor look pass](./sensor-look-pass.md)
- [API surface tracking](./api-surface-tracking.md)
- [File command bridge and MCP](./file-command-bridge-and-mcp.md)
- [Prefix SunbloomRenderer.Render to dispatch compute on the HDR scene](../../../howto/rendering/vulkan-passes/insert-a-custom-pass-into-render-game/insert-a-custom-pass-into-render-game-4.md) - how-to article demonstrating this feature
- [Depth-reading compute dispatches for rings, holes, tracers and relighting](../../../howto/rendering/vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/modify-the-hdr-scene-with-a-compute-pass-1.md) - how-to article demonstrating this feature

[^ksarmory-cloudpasshook]: src/KSArmory/Ksa/CloudPassHook.cs
[^ksarmory-cloudpass]: src/KSArmory/Ksa/CloudPass.cs
[^ksarmory-cloudpasscost]: src/KSArmory/Ksa/CloudPassCost.cs
[^ksarmory-particles-xml]: src/KSArmory/KSArmoryParticles.xml
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-config]: src/KSArmory/Sim/Config.cs
[^ksarmory-uidebug]: src/KSArmory/Ksa/Ui/UiDebug.cs
[^ksarmory-shadertunables]: src/KSArmory/Sim/ShaderTunables.cs
[^ksarmory-bridge]: src/KSArmory/Ksa/Bridge.cs
[^ksarmory-modding-notes]: docs/KSA-MODDING-NOTES.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-cpw]: KSA.Rendering.ComputePipelineWrapper (decompiled, build 2026.10.10.5554)
[^ksa-utils]: KSA.Rendering.Utils (decompiled, build 2026.10.10.5554)
[^ksa-shaderreference]: KSA.ShaderReference (decompiled, build 2026.10.10.5554)
[^ksa-filereference]: KSA.FileReference (decompiled, build 2026.10.10.5554)
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-assetbundle]: KSA.AssetBundle (decompiled, build 2026.10.10.5554)
[^ksa-barrierbatch]: KSA.Rendering.BarrierBatch (decompiled, build 2026.10.10.5554)
[^ksa-imagebarrierinfo]: KSA.Rendering.ImageBarrierInfo (decompiled, build 2026.10.10.5554)
[^ksa-renderimage]: KSA.Rendering.RenderImage (decompiled, build 2026.10.10.5554)
[^ksa-devicecontext]: Core.KSADeviceContextEx (decompiled, build 2026.10.10.5554)
[^ksa-gpuprofiler]: KSA.GpuProfiler (decompiled, build 2026.10.10.5554)
[^ksa-gpuregion]: KSA.GpuRegionExtensions (decompiled, build 2026.10.10.5554)
[^ksa-shaderreloader]: KSA.AssetReloader.ShaderReloader (decompiled, build 2026.10.10.5554)
[^ksa-raytracing]: KSA.Rendering.Raytracing.RaytracingRenderer (decompiled, build 2026.10.10.5554)
