---
type: How-To
title: Private colour and depth targets with a standalone dynamic-rendering pipeline
description: Build a render-pass-less pipeline for images the mod owns instead of the scene target, compiling GLSL from embedded resources at runtime
category: rendering
group: vulkan-passes
topic: build-a-vulkan-pipeline-for-the-scene-target
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/pebbles-workshop-offscreen-preview]
techniques: [vulkan-pipeline, glsl-shader]
hooks: [starmap StarMapBeforeGui]
lifecycle: [frame:draw-ui]
game_types: [RenderCore.ShaderModuleUtils, RenderCore.SimpleVkMesh, KSA.Program]
tags: [vulkan, pipeline, dynamic-rendering, offscreen, embedded-glsl]
---

Outcome: a pipeline that is not tied to the scene at all. It renders into colour and depth images the mod owns,
with attachment formats the mod chooses, so it never needs to match the game's sample count or follow renderer rebuilds.
This is the contrast to approaches 1 and 2, which must match `Program.OffscreenTarget`. Demonstrated by unscience Pebbles'
Workshop preview ([Independent offscreen Vulkan preview rendered before the GUI and shown as an ImGui image](../../../../mods/unscience/features/pebbles-workshop-offscreen-preview.md)); the recording side is in
[submit your own Vulkan commands](../submit-your-own-vulkan-commands/overview.md) (approach 4). Requires StarMap.

## Background: how the game does it

Dynamic rendering needs no `VkRenderPass`: the pipeline create-info carries a `VkPipelineRenderingCreateInfo` with the
attachment formats, and the command buffer opens a scope with `BeginRendering`. The game's own pipelines are built the same way.
The renderer provides shared dynamic viewport and scissor state (`Renderer.DynamicStateInfo`, `Renderer.ViewportState`) that the
mod reuses; runtime GLSL goes through `ShaderModuleUtils.FromString`
[current/decomp/RenderCore/ShaderModuleUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ShaderModuleUtils.cs#L79).

## Lifecycle and timing

| Step | Where |
|---|---|
| Create the pipeline lazily on the first render | `frame:draw-ui`, in `[StarMapBeforeGui]`, main thread |
| Shader compile | same call; the first render pays the shaderc cost on the main thread |
| Draw | in the mod's own command buffer, submitted and fenced before the frame records anything |

Because the targets belong to the mod, the pipeline lives until `Dispose`; there is no rebuild on `Program.RebuildRenderer`.

## Steps

1. Check struct layouts first so a mismatch fails loudly instead of rendering garbage: the preview compares
   `Marshal.SizeOf` and `OffsetOf` of its vertex (32 bytes) and push-constant (112 bytes) types to the expected values.

2. Embed the GLSL as resources (`<EmbeddedResource Include="Preview/**/*.glsl" />`), compile with
   `ShaderModuleUtils.FromString` using the resource name plus a NUL as the debug name, and destroy the modules right after
   pipeline creation. Chain `VkPipelineRenderingCreateInfo` into `Next`, set `RenderPass = NullHandle`, and choose your formats
   (`R8G8B8A8UNorm` colour and `D32SFloat` depth in the preview):

   ```csharp
   // pebbles.lib/Preview/PreviewPipeline.cs L104-L135
           VkFormat color = ColorFormat;
           var rendering = new VkPipelineRenderingCreateInfo
           {
               ColorAttachmentCount = 1, ColorAttachmentFormats = &color, DepthAttachmentFormat = DepthFormat
           };
           var info = new VkGraphicsPipelineCreateInfo
           {
               Layout = Layout, Next = &rendering, RenderPass = VkRenderPass.NullHandle,
               StageCount = stages.Length, Stages = stages.AsPointer(),
               DynamicState = _renderer.DynamicStateInfo, ViewportState = _renderer.ViewportState,
               VertexInputState = input, InputAssemblyState = Presets.InputAssembly.TriangleList,
               RasterizationState = Presets.Rasterization.Fill.CullNone, DepthStencilState = &depth,
               ColorBlendState = Presets.BlendState.BlendNone, MultisampleState = &samples
           };
           Pipeline = device.CreateGraphicsPipeline(default(VkPipelineCache), info, null);
       }
       finally
       {
           if (!vertex.IsNull()) device.DestroyShaderModule(vertex, null);
           if (!fragment.IsNull()) device.DestroyShaderModule(fragment, null);
       }
   }

   private VkShaderModule Compile(string suffix, VkShaderStageFlags stage)
   {
       var assembly = typeof(PreviewPipeline).Assembly;
       string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix, StringComparison.Ordinal));
       using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Missing preview shader {name}.");
       using var reader = new StreamReader(stream);
       return ShaderModuleUtils.FromString(_renderer.Device, Encoding.UTF8.GetBytes(reader.ReadToEnd()), stage,
           null, Encoding.UTF8.GetBytes(name + "\0"));
   }
   ```

3. Pick the fixed state you need: here triangle lists, no culling (imported meshes may be single-sided), depth test
   `LessOrEqual` with writes, no blending.

4. Draw with `BeginRendering` on your own images in your own command buffer, with viewport and scissor set dynamically.

## Complete example

| File | Role |
|---|---|
| [pebbles.lib/Preview/PreviewPipeline.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/PreviewPipeline.cs) | Layouts, pipeline, runtime GLSL |
| [pebbles.lib/Preview/PreviewTarget.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/PreviewTarget.cs#L55-L102) | The images and the draw |
| [pebbles.lib/Preview/Workshop.frag.glsl](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/Workshop.frag.glsl) | Fragment shader |

## Variations and gotchas

- **Free-standing means standard depth.** Without the scene target you are not forced into reverse-Z; the preview uses
  `LessOrEqual`.
- **First-render hitch.** shaderc compile on the main thread; do it behind a loading state if it matters.
- **Format assumptions.** Colour is non-sRGB, so brightness is not the game's tonemapped output.
- **Layout drift.** Pin vertex and push-constant sizes with a startup check.

## See also

- [Overview](overview.md)
- [Approach 1: SetupGraphicsPipeline helper](build-a-vulkan-pipeline-for-the-scene-target-1.md)
- [Approach 2: hand-built pipeline with premultiplied alpha](build-a-vulkan-pipeline-for-the-scene-target-2.md)
- [Independent offscreen Vulkan preview rendered before the GUI and shown as an ImGui image](../../../../mods/unscience/features/pebbles-workshop-offscreen-preview.md)
- [Record and submit your own Vulkan work: offscreen renders and one-shot uploads](../submit-your-own-vulkan-commands/overview.md)
