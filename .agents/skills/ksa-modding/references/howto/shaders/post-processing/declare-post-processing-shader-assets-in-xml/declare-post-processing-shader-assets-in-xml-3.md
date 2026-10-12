---
type: How-To
title: "Declare post-processing assets: input and output references resolved while the chain is built"
description: How PostProcessingInputReference and PostProcessingOutputReference pick an earlier pass's image, which stage each defaults to, and why a reference to a later pass cannot work
category: shaders
group: post-processing
topic: declare-post-processing-shader-assets-in-xml
approach: 3
difficulty: advanced
prerequisites: [starmap, shaderextensions, kittenextensions]
demonstrated_by:
  - mods/shaderextensions/features/cross-pass-image-references
  - mods/shaderextensions/features/post-processing-pass-chain
techniques: [post-processing, xml-asset]
hooks: [xml-asset PostProcessingInputReference, xml-asset PostProcessingOutputReference]
lifecycle: [startup:asset-load, render:post-process]
game_types: [KSA.Rendering.RenderImage, KSA.Program, RenderCore.RenderTechnique]
tags: [post-processing, cross-pass, input-reference, output-reference, render-graph, shaderextensions]
---
A plain chain lets pass *n* see only pass *n-1*. Two child elements turn it into a small render graph: a
shader can sample the image any earlier pass read (`<PostProcessingInputReference>`) or wrote
(`<PostProcessingOutputReference>`), and a shader in the post-ImGui chain can reach back into the
pre-ImGui chain. This article gives the rules the code enforces so you can write references that
resolve and diagnose the ones that do not. The only sample in the reference repositories is
KSA_AircraftHUD's, written for the older API (see the [small-file article](declare-post-processing-shader-assets-in-xml-2.md)).

## Background: how the game does it

Nothing in the base game provides this; it is ShaderExtensions plumbing on top of game types:

- A pass's images are `KSA.Rendering.RenderImage` objects whose `ImageView` is written into a descriptor
  with `Program.LinearClampedSampler`
  ([current/decomp/KSA.Rendering/RenderImage.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderImage.cs)).
- The chain moves images between layouts with the state-tracked
  `CommandBuffer.PipelineBarrier2(IRenderImage, ImageBarrierInfo)`, so a referenced image is
  transitioned to `SampledReadF` once at the start of `Chain.Render`
  ([ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs#L96-L107)).

## Lifecycle and timing

- **Resolved** when a chain is built: the `Program.RebuildRenderer` postfix (`frame:prepare` or
  `frame:post-render`, wherever the game rebuilds) or inline in the `RenderGame` replacement on an
  extent change (`frame:render`).
- **Sampled** while `Program.RenderGame` records, at the pre-ImGui or post-ImGui insertion point
  (`render:post-process`); see the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).
- XML defaults are applied at `startup:asset-load`, in `OnDataLoad`.

## Steps

1. **Pick the element.** Input reference: the image pass N *sampled* (its input). Output reference: the
   image pass N *rendered*. Each adds one `sampler2D` at the next free binding (2, 3, ...) in document order.

2. **Know the stage defaults.** The owning asset calls `Initialize(this)` on each reference in
   `OnDataLoad` ([ShaderExtensions/PostProcessing/PreImgui/PostProcessingShaderAsset.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PreImgui/PostProcessingShaderAsset.cs#L50-L59)):

   | Element | Inside `<PostProcessingShader>` | Inside `<GlobalPostShader>` |
   |---|---|---|
   | Input reference | `RenderPassId` required (else `MissingFieldException`); `PreImgui` forced `true` | `RenderPassId` required; `PreImgui` defaults to `false`, may be set `true` |
   | Output reference | `RenderPassId` defaults to the owner's; `PreImgui` forced `true` | `RenderPassId` defaults to the owner's; `PreImgui` defaults to `false` |

   ```csharp
   // ShaderExtensions/PostProcessing/PostProcessingInputReference.cs L46-L64
           public void Initialize(PostProcessingShaderAsset parent)
           {
               if (!RenderPassIdSpecified) throw new MissingFieldException($"{nameof(PostProcessingInputReference)} for PostProcessingShaderAsset {parent.Id} must specify a RenderPassId.");

               PreImgui = true; // pre imgui shaders can only use pre imgui inputs
           }

           public void Initialize(GlobalPostShaderAsset parent)
           {
               if (!RenderPassIdSpecified) throw new MissingFieldException($"{nameof(PostProcessingInputReference)} for GlobalPostShaderAsset {parent.Id} must specify a RenderPassId.");

               if (!PreImguiSpecified) PreImgui = false;
           }

           public void Resolve(RenderImage attachment)
           {
               this.attachment = attachment;
               resolved = true;
           }
   ```

   Source: [ShaderExtensions/PostProcessing/PostProcessingInputReference.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingInputReference.cs#L46-L64). Always give output references an
   explicit `RenderPassId`: one without it points at its own pass, whose output is registered only
   after the pass is built.

3. **Order your ids so the referenced pass is built first.** The resolver is a registry keyed by
   `(PreImgui, RenderPassId)`. For each pass in order the chain registers its input, resolves the
   shader's references, builds the pass, and only then registers its output; `Clear(preImgui)` empties one
   stage only, so pre-ImGui entries survive while the post chain is built:

   ```csharp
   // ShaderExtensions/PostProcessing/PostProcessingInputResolver.cs L13-L41
           public static void Clear(bool preImgui)
           {
               foreach (PostProcessingInputKey key in Inputs.Keys.Where(key => key.PreImgui == preImgui).ToArray())
                   Inputs.Remove(key);
               foreach (PostProcessingInputKey key in Outputs.Keys.Where(key => key.PreImgui == preImgui).ToArray())
                   Outputs.Remove(key);
           }

           public static void RegisterInput(bool preImgui, int renderPassId, RenderImage attachment)
           {
               Inputs[new PostProcessingInputKey(preImgui, renderPassId)] = attachment;
           }

           public static void RegisterOutput(bool preImgui, int renderPassId, RenderImage attachment)
           {
               Outputs[new PostProcessingInputKey(preImgui, renderPassId)] = attachment;
           }

           public static void Resolve(PostProcessingInputReference input)
           {
               PostProcessingInputKey key = new(input.PreImgui, input.RenderPassId);
               if (!Inputs.TryGetValue(key, out RenderImage attachment))
               {
                   string stage = input.PreImgui ? "pre-imgui" : "post-imgui";
                   throw new InvalidOperationException($"No {stage} post-processing input found for RenderPassId {input.RenderPassId}.");
               }

               input.Resolve(attachment);
           }
   ```

   Source: [ShaderExtensions/PostProcessing/PostProcessingInputResolver.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingInputResolver.cs#L13-L41). This is why a reference to a
   later pass is impossible: its entry does not exist yet when the earlier pass resolves.

4. **Reach into the pre-ImGui chain from a global post shader.** Adapted usage, with the XML shape taken from the
   classes above (the README does not document it, and no reference mod uses it):

   ```xml
   <!-- adapted: GlobalPostShader reading the image pre-ImGui pass 128 sampled -->
   <GlobalPostShader Id="MyCompositePost" Path="Shaders/CompositePost.frag" RenderPassId="10">
     <PostProcessingInputReference RenderPassId="128" PreImgui="true" />
   </GlobalPostShader>
   ```

   The pre-ImGui chain is always rebuilt before the post-ImGui chain, so its entries exist
   ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L213-L217), [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L234-L235)).

5. **Declare the sampler in GLSL at the right binding.** Count from 2 in document order; the unprocessed
   scene a later pass wants is the input of the first pre-ImGui pass.

## Complete example

| File | Role |
|---|---|
| [ShaderExtensions/PostProcessing/PostProcessingInputReference.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingInputReference.cs) | Input reference element and stage defaults |
| [ShaderExtensions/PostProcessing/PostProcessingOutputReference.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingOutputReference.cs) | Output reference element, descriptor write |
| [ShaderExtensions/PostProcessing/PostProcessingInputResolver.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingInputResolver.cs) | `(stage, RenderPassId)` registry |
| [ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs) | Registration order inside `Build` |

## Variations and gotchas

- **Only backwards.** A reference resolves against passes already built: lower id in the same stage, or any
  pre-ImGui pass from a post-ImGui shader. Anything else throws
  `InvalidOperationException("No ... post-processing input/output found for RenderPassId N")` out of the rebuild.
- **A self-reference works for inputs.** An input reference may name the shader's own id because the pass's
  input is registered first; it is the same image as binding 0.
- **Equal ids overwrite.** The registry keeps the last pass registered for an id.
- **The pre-ImGui source is reused.** The first pre-ImGui pass's input is the game's offscreen image and the
  chain copies its final result back into that image
  ([ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs#L38-L46)). A post-ImGui shader
  that references that input therefore sees the processed scene, not the original.
- **The old subpass syntax.** `SubpassId` on a reference is meaningless now, and a shader that sets it on the
  owner is skipped.

## See also

- Siblings: [paired pre/post assets](declare-post-processing-shader-assets-in-xml-1.md), [small file with chain, references and ImGuiShader](declare-post-processing-shader-assets-in-xml-2.md), [overview](overview.md)
- [Cross-pass image references](../../../../mods/shaderextensions/features/cross-pass-image-references.md), [RenderPassId-ordered fullscreen pass chain](../../../../mods/shaderextensions/features/post-processing-pass-chain.md)
- [Choose between pre-ImGui (HDR scene) and post-ImGui (final frame) post-processing](../choose-pre-or-post-imgui-post-processing/overview.md), [Write a fullscreen post-processing fragment shader for KSA](../write-a-post-processing-fragment-shader/overview.md)
