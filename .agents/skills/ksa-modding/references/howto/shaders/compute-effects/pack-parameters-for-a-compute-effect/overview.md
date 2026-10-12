---
type: Game Feature
title: Pack parameters and camera data for a compute effect
description: How to fit a compute effect's state into 128 bytes of push constants or a host-visible storage buffer, run several effects through one pipeline, and give the pass a camera pose that survives float precision and map mode
category: shaders
group: compute-effects
topic: pack-parameters-for-a-compute-effect
demonstrated_by:
  - mods/ksarmory/features/raymarched-mushroom-cloud
  - mods/ksarmory/features/high-altitude-burst-sky
  - mods/ksarmory/features/compute-shell-tracers
  - mods/ksarmory/features/shock-front-refraction
  - mods/ksarmory/features/burst-flash-and-view-shake
lifecycle: [frame:draw-ui, frame:celestials, render:bloom, render:post-process]
game_types:
  - KSA.Rendering.ComputePipelineWrapper
  - Brutal.VulkanApi.Abstractions.BufferEx
  - Brutal.VulkanApi.Abstractions.MappedMemory
  - KSA.KsaVmaAllocator
  - Core.KSADeviceContextEx
  - KSA.GlobalShaderBindings
  - KSA.Camera
  - KSA.ViewProjection
  - KSA.IGameViewport
  - KSA.ViewportRegistry
tags: [compute, push-constants, storage-buffer, octahedral, camera-relative, double-precision, ksarmory]
---
A compute effect in a game this size has the same problem every time: the shader needs a handful of numbers per frame (where the effect is, how old it is, which way is up) and the host side must hand them over without a descriptor update per dispatch, without losing precision at solar-system distances, and without overrunning the guaranteed limits. This topic collects four patterns from the KSArmory mod, which records its own compute dispatches into KSA's frame before bloom.

## Engine background

- **What the wrapper binds for you.** `ComputePipelineWrapper` builds a pipeline layout of the game's global set 0 (`GlobalShaderBindings.DescriptorSetLayout`), optionally the bindless texture set, then its own set, then any external sets you pass; its constructor accepts storage images, depth samplers, colour samplers, uniform buffers, dynamic uniform buffers and storage buffers ([current/decomp/KSA.Rendering/ComputePipelineWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ComputePipelineWrapper.cs#L32)). `Utils.WriteBindings` numbers its own set in that order (storage images first, then samplers, uniform buffers, dynamic uniform buffers, storage buffers) ([current/decomp/KSA.Rendering/Utils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/Utils.cs#L296-L320)).
- **Push constants.** `BindPipeline<T>(commandBuffer, viewportIndex, externalSets, externalDynamicOffsets, pushConstant)` binds the pipeline and the global set for the viewport, then calls `PushConstants` with the struct at offset zero ([current/decomp/KSA.Rendering/ComputePipelineWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ComputePipelineWrapper.cs#L210-L213)). The caller declares the `VkPushConstantRange`; Vulkan guarantees 128 bytes on every device and this topic treats that as the budget.
- **Buffers.** `Renderer` (through `KSADeviceContextEx`) exposes a public `Allocator` of the concrete type `KsaVmaAllocator`, which implements `IBufferAllocator`; `CreateBuffer` and `BufferEx.Map` give a persistently mapped, host-coherent storage buffer ([current/decomp/Core/KSADeviceContextEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/KSADeviceContextEx.cs#L67)).
- **Cameras.** `Camera.MVP` and `Camera.VPInv` are row-vector view-projection matrices rebuilt in `Camera.OnFrame` (`frame:viewports`); `Camera.PositionEcl` is the double-precision ecliptic position. `GameViewport.GetCamera()` returns the map camera while the viewport is in map mode, `BaseCamera` always the flight camera ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L297-L300)); `Program.GetMainCamera()` is `MainViewport.GetCamera()` and `Program.GetRenderCamera()` is `RenderedViewport.GetCamera()` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L631-L644)).
- **Where it runs.** KSArmory records from a Harmony prefix on `SunbloomRenderer.Render`, called per rendered viewport just before bloom (Program.cs L4584 for secondary viewports, L4915 for the main one). See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence) and [Paint into or rescale the HDR scene with a compute pass shipped as a Shader asset](../../../rendering/vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [128-byte push constant with octahedral vectors and flag sums](pack-parameters-for-a-compute-effect-1.md) | KSArmory | `vulkan-pipeline` | `render:bloom` | No buffers at all; every float is spoken for and the shader hard-codes what it cannot be told |
| [One pipeline multiplexed by a kind tag with a per-kind cap](pack-parameters-for-a-compute-effect-2.md) | KSArmory | `vulkan-pipeline` | `render:bloom` | Reuses one pipeline and push block for four effects; the kind and the discriminating sign become a contract |
| [Host-visible storage buffer of screen-space segments](pack-parameters-for-a-compute-effect-3.md) | KSArmory | `vulkan-buffer`, `reflection` | `render:bloom` | Thousands of items per dispatch; needs a buffer ring, an allocator reached by reflection and a tile gather |
| [Camera-relative positions subtracted in double, then narrowed](pack-parameters-for-a-compute-effect-4.md) | KSArmory | `vulkan-pipeline` | `render:bloom`, `frame:viewports` | Exact at planetary distances; the camera choice (render, base or main) has to be deliberate |

## See also

- [Raymarched mushroom cloud](../../../../mods/ksarmory/features/raymarched-mushroom-cloud.md), [High-altitude burst sky](../../../../mods/ksarmory/features/high-altitude-burst-sky.md), [Compute shell tracers](../../../../mods/ksarmory/features/compute-shell-tracers.md), [Shock-front refraction](../../../../mods/ksarmory/features/shock-front-refraction.md), [Burst flash and view shake](../../../../mods/ksarmory/features/burst-flash-and-view-shake.md), [Compute pass before bloom](../../../../mods/ksarmory/features/compute-pass-before-bloom.md)
- [Send per-frame values and time to a shader with push constants, written at the right point in the frame](../../post-processing/send-push-constants-to-a-shaderextensions-shader/overview.md), [Reconstruct scene positions from the depth buffer and paint on them](../reconstruct-scene-positions-from-depth-in-a-compute-shader/overview.md), [Raymarch a local volume or analytic sky effect into the HDR scene](../raymarch-a-local-volume-into-the-hdr-scene/overview.md), [Compile-check a mod's GLSL without launching the game](../../glsl-workflow/check-mod-shaders-offline-with-glslang/overview.md)
- [Project world and sky positions to screen pixels and point at off-screen targets](../../../rendering/overlays/project-world-positions-to-screen/overview.md), [Choose a render frame and anchor effects to bodies, parts and terrain](../../../rendering/overlays/choose-a-render-frame-and-anchor-effects/overview.md)
