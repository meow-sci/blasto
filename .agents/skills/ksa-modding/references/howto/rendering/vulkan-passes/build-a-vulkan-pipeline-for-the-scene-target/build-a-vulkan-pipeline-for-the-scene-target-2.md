---
type: How-To
title: Hand-built dynamic-rendering pipeline reusing UnlitMeshVert with premultiplied alpha
description: Build the pipeline by hand with a chained VkPipelineRenderingCreateInfo, the stock unlit vertex shader, a runtime-compiled premultiplied-alpha fragment shader and depth test against the scene
category: rendering
group: vulkan-passes
topic: build-a-vulkan-pipeline-for-the-scene-target
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/purrtty/features/world-space-quad-pipeline-custom-shader]
techniques: [vulkan-pipeline, glsl-shader]
hooks: [starmap StarMapAfterGui, harmony-postfix KSA.SuperMeshRenderSystem.RenderTranslucencyPass]
lifecycle: [frame:draw-ui, render:translucency]
game_types: [KSA.Program, KSA.Rendering.RenderTarget, RenderCore.ShaderModuleUtils, KSA.ShaderReference]
tags: [vulkan, pipeline, premultiplied-alpha, glsl, shaderc, dynamic-rendering]
---

Outcome: a translucent, depth-tested pipeline compatible with the scene attachments, where you control the blend state
and the fragment shader, while still borrowing the stock unlit vertex shader, the dynamic viewport state and the reverse-Z
depth presets. Demonstrated by purrTTY's in-world terminal quads ([World-space textured quad pipeline with a runtime-compiled shader](../../../../mods/purrtty/features/world-space-quad-pipeline-custom-shader.md)).
Use this when approach 1's presets are not enough, for example when the stock fragment shader's forced alpha 1 or gamma
decode is wrong for your texture. It also shows the runtime-GLSL step. Requires StarMap and a reference to
the game-provided `Brutal.ShaderC`.

## Background: how the game does it

`RenderTarget` exposes `ColorAttachment`, `DepthAttachment` and `Samples` [current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L40-L57); when MSAA is
on the attachments already resolve to the MSAA images. The game's own translucent glass pipeline (`PartModelGlass`) uses the same
chained `VkPipelineRenderingCreateInfo`, which is the pattern copied here. `ShaderModuleUtils.FromString(device, shaderCode,
stage, options = null, debugName = default)` [current/decomp/RenderCore/ShaderModuleUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ShaderModuleUtils.cs#L79) compiles GLSL to SPIR-V with the game's bundled
shaderc and default options that install the default include callbacks [current/decomp/RenderCore/ShaderModuleUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ShaderModuleUtils.cs#L16-L21).
A compile failure throws `ShaderCompilationException` with the shaderc log.

## Lifecycle and timing

| Step | Where |
|---|---|
| Create shared pipelines and buffers | `frame:draw-ui`, on the first in-world terminal, because `Program.OffscreenTarget` does not exist at `startup:all-mods-loaded` |
| Record draws | `render:translucency` (postfix of `RenderTranslucencyPass`) on the main thread, see [insert a custom Vulkan pass](../insert-a-custom-pass-into-render-game/overview.md) approach 3 |

## Steps

1. Reuse the stock vertex shader: `ModLibrary.Get<ShaderReference>("UnlitMeshVert")` converts implicitly to a `VkShaderModule`
   owned by `ModLibrary`; never destroy it.

2. Compile your own fragment shader at runtime. Destroy the module yourself when the pipelines are gone. This one
   un-premultiplies the texel, applies the same `pow(2.2)` decode as the stock shader, and re-premultiplies:

   ```csharp
   // purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs L68-L106
   private const string QuadFragGlsl =
       "#version 450\n" +
       "layout(location = 0) in vec2 inUv;\n" +
       "layout(location = 0) out vec4 outColor;\n" +
       "layout(set = 0, binding = 0) uniform sampler2D samplerColorMap;\n" +
       "void main()\n" +
       "{\n" +
       "    vec4 t = texture(samplerColorMap, inUv);\n" +
       "    vec3 straight = (t.a > 0.0) ? (t.rgb / t.a) : vec3(0.0);\n" +
       "    vec3 lin = pow(straight, vec3(2.2));\n" +
       "    outColor = vec4(lin * t.a, t.a);\n" +
       "}\n";

   public unsafe SharedQuadResource(Renderer renderer)
   {
       ArgumentNullException.ThrowIfNull(renderer);
       _renderer = renderer;

       // KSA's stock unlit-mesh VERTEX shader (outputs UV at location 0, takes the mat4
       // MVP push constant). Get<T> throws if the id is missing, which the coordinator
       // catches to skip the feature cleanly. Owned by ModLibrary — we must NOT destroy.
       ShaderReference vertRef = ModLibrary.Get<ShaderReference>("UnlitMeshVert");
       VkShaderModule  vertModule = vertRef;  // implicit operator — ModLibrary-owned, never destroyed

       var device = renderer.Device;

       // Our OWN fragment shader, runtime-compiled via the engine's bundled shaderc.
       // The stock UnlitMesh.frag discards the texture alpha and forces 1.0; ours keeps
       // the off-screen texture's premultiplied alpha so the quad composites the
       // terminal's per-pixel opacity over the 3D scene. We OWN this module and MUST
       // destroy it (see DestroyGpu); a compile failure throws and the coordinator
       // skips the feature cleanly (InWorldTerminalManager.Create catches it).
       _fragModule = ShaderModuleUtils.FromString(
           device,
           System.Text.Encoding.UTF8.GetBytes(QuadFragGlsl),
           VkShaderStageFlags.FragmentBit,
           options: null,
           debugName: "purrTTY-Quad-Frag"u8);
       VkShaderModule fragModule = _fragModule;
   ```

3. Build the pipeline with the dynamic-rendering structure chained into `Next`, the formats and sample count taken from the
   scene target, and a premultiplied-alpha blend state (`One` / `OneMinusSrcAlpha`) built inline because no KSA preset matches:

   ```csharp
   // purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs L178-L221
   var multisample = new VkPipelineMultisampleStateCreateInfo
   {
       RasterizationSamples = offscreen.Samples,
   };

   // Dynamic-rendering pipeline info (mirrors KSA's own PartModelGlass pipeline,
   // which draws translucent geometry in this same post-atmosphere slot): no
   // VkRenderPass handle, just the attachment formats the pipeline must be
   // compatible with.
   var colorFormat = offscreenColor.Format;
   var renderingInfo = stackalloc VkPipelineRenderingCreateInfo[1];
   renderingInfo[0] = new VkPipelineRenderingCreateInfo
   {
       ColorAttachmentCount    = 1,
       ColorAttachmentFormats  = &colorFormat,
       DepthAttachmentFormat   = offscreenDepth.Format,
       StencilAttachmentFormat = VkFormat.Undefined,
       ViewMask                = 0,
   };

   // PREMULTIPLIED-alpha blend. The off-screen terminal texture is produced by KSA's
   // ImGui backend over a fully-transparent clear, so its RGB is already premultiplied
   // by coverage; the custom frag emits linear-premultiplied color + coverage alpha.
   // Composite with Src=One / Dst=OneMinusSrcAlpha so the 3D scene shows through by
   // (1 - alpha) with no dark fringing. No KSA preset matches, so it is built inline.
   // With all opacities at 1 the texture alpha is 1 and this equals the old opaque
   // BlendNone — existing quads are visually unchanged.
   var blendAttachment = new VkPipelineColorBlendAttachmentState
   {
       BlendEnable         = true,
       SrcColorBlendFactor = VkBlendFactor.One,
       DstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
       ColorBlendOp        = VkBlendOp.Add,
       SrcAlphaBlendFactor = VkBlendFactor.One,
       DstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
       AlphaBlendOp        = VkBlendOp.Add,
       ColorWriteMask      = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit
                           | VkColorComponentFlags.BBit | VkColorComponentFlags.ABit,
   };
   var colorBlend = new VkPipelineColorBlendStateCreateInfo
   {
       AttachmentCount = 1,
       Attachments     = &blendAttachment,
   };
   ```

4. Create two depth variants if you need both: `RenderingPresets.ReverseZDepthStencil.DepthTestNoWrite` (compare
   `GreaterOrEqual`) for quads hidden behind opaque geometry, and `NoDepthTest` for a billboard that stays on top.

5. Draw with `Program.SetViewport(cmd)` first, push the MVP, bind the buffers and `DrawIndexed`.

## Complete example

| File | Role |
|---|---|
| [purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs) | Shared pipelines, shaders and unit-quad buffers |
| [purrTTY.GameMod/InWorld/Display/InWorldQuad.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Display/InWorldQuad.cs) | Per-instance descriptor set and draw |

## Variations and gotchas

- **Dynamic rendering only.** A pipeline built for a classic render pass is invalid inside the scene scope.
- **Source formats and samples from the target you draw into.** Do not hard-code them.
- **Reverse-Z.** KSA's presets use `GreaterOrEqual`; generic `Less` presets render nothing.
- **Do not destroy modules you did not compile.** Do destroy the ones you did.
- **Coupling to the stock vertex shader.** If KSA changes `UnlitMesh.vert`'s inputs or push constant the quad breaks silently.
- **Stale sample count.** `SharedQuadResource` is built once; rebuild the pipeline when the target's `Samples` changes
  (see [Recreate mod render targets when KSA rebuilds its renderer](../handle-renderer-rebuilds/overview.md)).
- **Runtime GLSL with game includes.** To `#include "Common/Camera.glsl"` and friends, pass a NUL-terminated debug path inside the
  game's shader directory; the include resolver reads it as a C string. See [include the game shader library in mod GLSL](../../../shaders/glsl-workflow/include-the-game-shader-library-in-mod-glsl/overview.md) and approach 1 of [Project decals onto vessels, terrain and clutter](../../textures-and-decals/project-decals-onto-scene-geometry/overview.md).

## See also

- [Overview](overview.md)
- [Approach 1: SetupGraphicsPipeline helper](build-a-vulkan-pipeline-for-the-scene-target-1.md)
- [Approach 3: free-standing pipeline for private targets](build-a-vulkan-pipeline-for-the-scene-target-3.md)
- [World-space textured quad pipeline with a runtime-compiled shader](../../../../mods/purrtty/features/world-space-quad-pipeline-custom-shader.md)
- [Insert a custom Vulkan pass into KSA's frame](../insert-a-custom-pass-into-render-game/overview.md), [Draw a textured quad attached to a part or the camera](../../textures-and-decals/draw-a-textured-quad-in-the-world/overview.md)
