---
type: How-To
title: "Compile GLSL at runtime: a premultiplied-alpha fragment shader paired with the stock unlit vertex shader"
description: Compile only the fragment stage from an embedded string and reuse the game's UnlitMesh vertex shader for an in-world quad that blends over the scene
category: shaders
group: glsl-workflow
topic: compile-glsl-at-runtime
approach: 1
difficulty: advanced
prerequisites: [starmap, vulkan]
demonstrated_by:
  - mods/purrtty/features/world-space-quad-pipeline-custom-shader
  - mods/unscience/features/pebbles-workshop-offscreen-preview
techniques: [glsl-shader, vulkan-pipeline]
hooks: [harmony-postfix KSA.SuperMeshRenderSystem.RenderTranslucencyPass (purrtty draw)]
lifecycle: [frame:draw-ui, frame:render, render:translucency]
game_types: [RenderCore.ShaderModuleUtils, KSA.ShaderReference, KSA.RenderingPresets, KSA.Program]
tags: [glsl, shaderc, fromstring, premultiplied-alpha, unlit, quad, purrtty, dynamic-rendering]
---
purrTTY draws its terminal texture on a quad in the world. It builds the pipeline itself but borrows what it can: the stock unlit vertex shader, the scene target's formats and sample count, and the dynamic viewport state. The one thing it cannot borrow is a fragment shader that keeps premultiplied alpha, so it compiles its own from a string at the moment the
pipeline is first built. This article is that recipe for a small pipeline with one custom stage. You need a reference to the game's `Brutal.ShaderC` at build time (the call lives in `RenderCore.ShaderModuleUtils`).

## Background: how the game does it

- **The stock vertex shader.** `ModLibrary.Get<ShaderReference>("UnlitMeshVert")` is the game's `Shaders/Mesh/UnlitMesh.vert` asset ([current/Content/Core/DefaultAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/DefaultAssets.xml#L53)): inputs `vec3` position at location 0 and `vec2` UV at 1, a push constant `mat4 worldViewProjMatrix`, output UV at 0 ([current/Content/Core/Shaders/Mesh/UnlitMesh.vert](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/UnlitMesh.vert)).
  The module belongs to `ModLibrary`; never destroy it.
- **Why not the stock fragment shader.** `UnlitMesh.frag` gamma-decodes the texel and forces alpha to 1. The off-screen terminal texture is premultiplied (KSA's ImGui backend blends over a transparent clear), so the custom shader un-premultiplies, applies the same `pow(2.2)` decode and re-premultiplies. Skipping the un-premultiply makes a 50% background render at about 21%;
  straight-alpha blending leaves dark fringes around glyphs.
- **Dynamic rendering.** Since KSA rev 5154 (build 2026.8.5.5168) the scene uses dynamic rendering, so a pipeline drawn into it is created with a `VkPipelineRenderingCreateInfo` chained in `Next` and no render pass; formats and sample count come from `Program.OffscreenTarget`.

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| Compile, pipeline and buffers created | Main thread, when the first in-world terminal is created (from a dialog drawn in `StarMapAfterGui`); `Program.OffscreenTarget` does not exist yet at `StarMapAllModsLoaded` | `frame:draw-ui` |
| Draw recorded | A postfix on `SuperMeshRenderSystem.RenderTranslucencyPass` opens a dynamic-rendering scope over the scene colour and depth | `render:translucency` |

See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence) for the translucency pass.

## Steps

1. **Write the fragment source as a string.** It samples the premultiplied texture, un-premultiplies, linearises and re-premultiplies:

   ```csharp
   // purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs L68-L79
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
   ```

   Source: [purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs#L68-L79). `inUv` at location 0 matches the vertex shader's output.

2. **Get the stock vertex module and compile your own fragment module.** `FromString` takes UTF-8 bytes, the stage, `null` options and a UTF-8 debug name as a span. This sample has no includes, so the name is only for diagnostics (see the next article for includes). The module is yours to destroy:

   ```csharp
   // purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs L80-L106
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

   Source: [purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs#L80-L106).

3. **Match the pipeline to the shaders and the target.** The descriptor layout is one combined image sampler at binding 0 (fragment), the pipeline layout adds a vertex-stage push constant of `sizeof(float4x4)`, vertex input is one binding with stride 20 (`float3` position, `float2` UV), and the targets' formats and samples come from `Program.OffscreenTarget`. The dynamic-rendering info and the premultiplied blend state:

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

   Source: [purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs#L178-L221). Use the reverse-Z depth presets (`RenderingPresets.ReverseZDepthStencil.DepthTestNoWrite` / `NoDepthTest`), not generic `Less` presets; KSA's compare is `GreaterOrEqual`.

4. **Catch compile failures at build time of the pipeline** and skip the feature rather than crash; destroy your fragment module with the pipelines. The full pipeline-building recipe and anchoring are in [Build a Vulkan pipeline compatible with KSA's scene render target](../../../rendering/vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/overview.md) and [Draw a textured quad attached to a part or the camera](../../../rendering/textures-and-decals/draw-a-textured-quad-in-the-world/overview.md).

## Complete example

| File | Role |
|---|---|
| [purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs) | The GLSL, runtime compile, layouts, pipeline variants, quad buffers |
| [purrTTY.GameMod/InWorld/Display/InWorldQuad.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Display/InWorldQuad.cs) | Per-instance descriptor set and draw recording |

## Variations and gotchas

- **Dynamic rendering requires the matching create info.** A pipeline built for a classic render pass is invalid inside a dynamic-rendering scope.
- **Source formats and sample count from the target you draw into;** with MSAA on, the attachments resolve to the MSAA images.
- **Rebuild on a sample-count change.** The pipeline is built once; `Program.RebuildRenderer` recreates the scene target with the current sample count, so a stale `RasterizationSamples` is possible if the player changes anti-aliasing (inferred from the code, not tested).
- **Coupling.** If KSA changes `UnlitMesh.vert`'s inputs or push constant, the quad breaks silently.
- **Do not destroy `ModLibrary` modules; do destroy yours.**

## See also

- Siblings: [decal pass with includes](compile-glsl-at-runtime-2.md), [preview shader](compile-glsl-at-runtime-3.md), [overview](overview.md)
- [World-space textured quad pipeline with a runtime-compiled shader](../../../../mods/purrtty/features/world-space-quad-pipeline-custom-shader.md), [Independent offscreen Vulkan preview rendered before the GUI and shown as an ImGui image](../../../../mods/unscience/features/pebbles-workshop-offscreen-preview.md) (the same compile-at-first-build pattern for a private preview pipeline, with embedded resources: [article 3](compile-glsl-at-runtime-3.md))
- [Draw a textured quad attached to a part or the camera](../../../rendering/textures-and-decals/draw-a-textured-quad-in-the-world/overview.md), [Build a Vulkan pipeline compatible with KSA's scene render target](../../../rendering/vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/overview.md)
