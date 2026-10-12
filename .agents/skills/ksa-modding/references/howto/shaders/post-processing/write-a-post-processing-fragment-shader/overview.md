---
type: Game Feature
title: Write a fullscreen post-processing fragment shader for KSA
description: The contract a ShaderExtensions pass shader is written against, the extra bindings it can declare, and how the mod builds each pass from a RenderTechnique and a RenderTarget
category: shaders
group: post-processing
topic: write-a-post-processing-fragment-shader
demonstrated_by:
  - mods/mpfx/features/effect-shader-contract
  - mods/shaderextensions/features/post-processing-pass-chain
  - mods/ksa-aircrafthud/features/glass-post-process-chain
  - mods/shaderextensions/features/shaderex-extra-bindings
  - mods/ksa-aircrafthud/features/blue-noise-dithering
lifecycle: [startup:asset-load, frame:render, render:post-process]
game_types:
  - KSA.Program
  - KSA.GlobalShaderBindings
  - RenderCore.RenderTechnique
  - KSA.Rendering.RenderTarget
  - KSA.Rendering.ImageBarrierInfo
  - KSA.CommandBufferEx
  - KSA.RenderingPresets
  - KSA.ShaderReference
  - KSA.TextureReference
  - KSA.ModLibrary
  - KSA.FileReference
  - KSA.IBinder
  - KSA.AssetBundle
  - Brutal.VulkanApi.Abstractions.DescriptorPoolEx
  - Brutal.VulkanApi.Abstractions.DescriptorSetLayoutEx
  - KSA.Camera
  - KSA.ViewProjection
  - KSA.IGameViewport
  - KSA.ViewportRegistry
tags: [glsl, post-processing, shaderextensions, fragment-shader, descriptor-set, render-technique]
---
KSA has no post-processing chain of its own. A fullscreen effect is a GLSL fragment shader that the
ShaderExtensions mod turns into one render pass: it compiles the file with the game's own shader
loader, gives it a full-resolution render target, and draws the game's screen-space vertex shader
over it. This topic is about the shader side of that: what the shader may assume, what it can ask
for, and what the host builds for it so you can debug a pass that does not show.

## Engine background

- **No post-process stage in the base game.** `Program.RenderGame` renders the scene into an
  offscreen RGBA16F target, then inside one swapchain render pass runs `RenderFinalComposite`
  (tonemap) and the ImGui draw. A mod pass has to run outside that render pass, which is why
  ShaderExtensions transpiles `RenderGame`; see the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence) (steps 23 to 26) for the exact
  order.[^ksa-program]
- **The vertex shader.** The default is the game's `ScreenspaceVert`
  ([current/Content/Core/DefaultAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/DefaultAssets.xml#L94)), which is
  [current/Content/Core/Shaders/PostProcess/screenspace_quad.vert](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/PostProcess/screenspace_quad.vert). It emits four vertices
  with no vertex buffer, writes `Uv` at location 0 and is drawn as a triangle strip (`Draw(4, 1, 0, 0)`),
  not as a single oversized triangle.
- **Set 0 belongs to the game.** `GlobalShaderBindings` owns the descriptor set layout shared by
  every game pipeline: binding 0 is the per-viewport `Global` block (camera, lighting, celestial
  and vessel data) and binding 1 is `GlobalConstants`, both plain uniform buffers visible to the
  vertex, fragment and compute stages
  ([current/decomp/KSA/GlobalShaderBindings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GlobalShaderBindings.cs#L98-L123)).[^ksa-globalshaderbindings]
  `Program.UpdateRenderingResources` copies the frame's camera, lighting, celestial and vessel
  data into them per viewport slot before `RenderGame` records
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4313-L4325)).
- **A render pass without a render pass.** `RenderTarget` implements `IRenderPassInfo`; its
  `SetupGraphicsPipeline` fills a `VkPipelineRenderingCreateInfo`, so a pipeline built against a
  `RenderTarget` uses dynamic rendering, not a `VkRenderPass`
  ([current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L356-L368)). `RenderCore.RenderTechnique` is the
  base class that compiles shader stages and creates the graphics pipeline
  ([current/decomp/RenderCore/RenderTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/RenderTechnique.cs#L59-L118)).
- **Ordering and layouts.** Passes are ordered by `RenderPassId` and each ends in
  `ImageBarrierInfo.Presets.SampledReadF` so the next one can sample it
  ([current/decomp/KSA.Rendering/ImageBarrierInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ImageBarrierInfo.cs#L33)).

## The contract in one table

| Slot | Declaration | Supplied by |
|---|---|---|
| `layout(location = 0) in vec2 Uv` | vertex output | `ScreenspaceVert` (top-left origin) |
| `layout(location = 0) out vec4` | one colour target | the pass's own `RenderTarget`, in the chain's source format |
| `set = 1, binding = 0` `sampler2D` | previous pass output (or the game scene for the first pass) | `PostProcessingPassRenderer` |
| `set = 1, binding = 1` `ShaderTime` UBO | frame, delta, real and simulation time, warp speed | ShaderExtensions' shared time buffer |
| `set = 1, binding = 2...` | `<TextureBinding>`, buffer and cross-pass reference children in XML order | `ShaderEx` |
| `push_constant` | at most one block, at most 128 bytes in total | the XML push-constant child |
| `set = 0` | the game's `Global` and `GlobalConstants` | `GlobalShaderBindings` |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Minimal identity-for-neutral pass](write-a-post-processing-fragment-shader-1.md) | MPFX | `glsl-shader` | `startup:asset-load`, `render:post-process` | The recommended shape: one file, one parameter block, neutral values return the input. Also covers reading the game's set 0 |
| [Multi-binding pass and the dynamic-rendering port](write-a-post-processing-fragment-shader-2.md) | KSA_AircraftHUD | `glsl-shader` | `startup:asset-load`, `render:post-process` | Shared `#include`, extra samplers, and a worked port from the removed subpass style. The pinned ShaderExtensions rejects the original |
| [One RenderTarget per shader as pipeline pass info](write-a-post-processing-fragment-shader-3.md) | ShaderExtensions | `vulkan-pipeline` | `frame:render` | Background for debugging: how a pass is built from `RenderTechnique` and `RenderTarget`, and the build 5554 dynamic-offset quirk |
| [ShaderReference subclass with its own descriptor set](write-a-post-processing-fragment-shader-4.md) | ShaderExtensions | `vulkan-pipeline` | `startup:asset-load` | How `ShaderEx` counts descriptors, allocates one set and resolves bindings at `Bind` time; for authors of a new asset type |

## See also

- [RenderPassId-ordered fullscreen pass chain](../../../../mods/shaderextensions/features/post-processing-pass-chain.md)
- [ShaderEx asset with extra descriptor bindings](../../../../mods/shaderextensions/features/shaderex-extra-bindings.md)
- Next topics: [Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](../declare-post-processing-shader-assets-in-xml/overview.md), [Choose between pre-ImGui (HDR scene) and post-ImGui (final frame) post-processing](../choose-pre-or-post-imgui-post-processing/overview.md), [Feed a ShaderExtensions shader from C# with uniform buffers (std140)](../feed-uniform-buffers-to-a-shaderextensions-shader/overview.md), [Send per-frame values and time to a shader with push constants, written at the right point in the frame](../send-push-constants-to-a-shaderextensions-shader/overview.md)
- Related, in other categories: [Insert a custom Vulkan pass into KSA's frame](../../../rendering/vulkan-passes/insert-a-custom-pass-into-render-game/overview.md), [Build a Vulkan pipeline compatible with KSA's scene render target](../../../rendering/vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/overview.md), [Recreate mod render targets when KSA rebuilds its renderer](../../../rendering/vulkan-passes/handle-renderer-rebuilds/overview.md), [Refract and distort the rendered scene, and flash or shake it](../../screen-effects/refract-and-distort-the-scene/overview.md), [Add film grain and dither away banding](../../screen-effects/add-film-grain-and-blue-noise-dithering/overview.md), [Pack parameters and camera data for a compute effect](../../compute-effects/pack-parameters-for-a-compute-effect/overview.md), [Reconstruct scene positions from the depth buffer and paint on them](../../compute-effects/reconstruct-scene-positions-from-depth-in-a-compute-shader/overview.md), [Project world and sky positions to screen pixels and point at off-screen targets](../../../rendering/overlays/project-world-positions-to-screen/overview.md), [Choose a render frame and anchor effects to bodies, parts and terrain](../../../rendering/overlays/choose-a-render-frame-and-anchor-effects/overview.md)

[^ksa-globalshaderbindings]: current/decomp/KSA/GlobalShaderBindings.cs (decompiled, build 2026.10.10.5554)
[^ksa-program]: current/decomp/KSA/Program.cs (decompiled, build 2026.10.10.5554)
