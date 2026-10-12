---
type: Mod Feature
title: Cross-pass image references
description: PostProcessingInputReference and PostProcessingOutputReference child elements bind the input or output image of another pass, picked by RenderPassId and by pre- or post-ImGui stage, as extra sampler2D bindings so a pass can combine images from earlier in the chain
mod: shaderextensions
category: shaders
techniques: [post-processing, xml-asset, vulkan-texture, glsl-shader]
hooks: [harmony-transpiler KSA.Program.RenderGame, harmony-postfix KSA.Program.RebuildRenderer]
lifecycle: [frame:render, render:post-process]
game_types: [KSA.Rendering.RenderImage, KSA.SerializedId, KSA.Program]
tags: [shaders, post-processing, render-graph, descriptor-sets, xml]
---

A plain chain only lets pass *n* see pass *n-1*. Two extra child elements turn it into a small
render graph: a shader can also sample the image that any earlier pass read
(`<PostProcessingInputReference>`) or wrote (`<PostProcessingOutputReference>`), and a post-ImGui
shader can reach back into the pre-ImGui chain. Each reference becomes one more `sampler2D` at the
next free binding (2, 3, ...) of the shader's set.[^sx-inputref] [^sx-outputref]

The only sample in the reference repositories is KSA_AircraftHUD's, written for the older subpass
API ([HudAssests.xml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/HudAssests.xml#L14-L19)):[^hud-assets]

```xml
<!-- HudAssests.xml L14-L19 (KSA_AircraftHUD) -->
	<PostProcessingShader Id="PostProcess" Path="Shaders/PostProcess.frag" RenderPassId="64" SubpassId="0" >
		<PPPushConstantsBufferAsset Id="PPPushConstantsBuffer2" Size="1" />
		<PostProcessingInputReference RenderPassId="62" SubpassId="0" />
		<TextureBinding Path="Textures/stbn_vec3_128x128x64.png" />
		<TextureBinding Path="Textures/DirtMask.png" />
	</PostProcessingShader>
```

ShaderExtensions 1.0.5 skips that shader because it still declares `SubpassId`. A current declaration
drops `SubpassId`, gives every reference an explicit `RenderPassId`, and inside a `<GlobalPostShader>`
adds `PreImgui="true"` to reach an image of the pre-ImGui chain. References are numbered together
with the other binding children in document order: in the sample the input reference would be
binding 2 and the two textures bindings 3 and 4 (the push constant element takes no number).

## How it works

1. **Element registration.** `AssetEx.Init` injects both element names into `XmlBindings` of the two
   post-processing asset types
   ([ShaderExtensions/AssetEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/AssetEx.cs#L19-L22)).[^sx-assetex]
2. **Attributes and the `Specified` pattern.** Both classes have `RenderPassId` and `PreImgui` XML
   attributes plus `[XmlIgnore]` `RenderPassIdSpecified` / `PreImguiSpecified` properties, which
   `XmlSerializer` sets when the attribute is present
   ([ShaderExtensions/PostProcessing/PostProcessingInputReference.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingInputReference.cs#L23-L30)).
3. **Defaults per stage.** After load, the owning asset calls `Initialize(this)`
   ([ShaderExtensions/PostProcessing/PreImgui/PostProcessingShaderAsset.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PreImgui/PostProcessingShaderAsset.cs#L50-L59)):[^sx-preasset]

   | Element | In `<PostProcessingShader>` | In `<GlobalPostShader>` |
   |---|---|---|
   | Input reference | `RenderPassId` required (else `MissingFieldException`); `PreImgui` forced `true` | `RenderPassId` required; `PreImgui` defaults to `false`, may be set `true` |
   | Output reference | `RenderPassId` defaults to the owner's; `PreImgui` forced `true` | `RenderPassId` defaults to the owner's; `PreImgui` defaults to `false` |

4. **Registry.** `PostProcessingInputResolver` keeps two dictionaries keyed by
   `(PreImgui, RenderPassId)`: the image each pass sampled and the image each pass rendered.
   `Clear(preImgui)` only empties one stage's entries, so the pre-ImGui entries survive while the
   post-ImGui chain is built
   ([ShaderExtensions/PostProcessing/PostProcessingInputResolver.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingInputResolver.cs#L8-L62)).[^sx-resolver]
5. **Resolution during `Build`.** For each pass in order, the chain registers the pass's input,
   resolves the shader's references, creates the pass (which writes the descriptors) and only then
   registers the pass's output
   ([ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs#L67-L90)).[^sx-chain]
   The pre-ImGui chain is always rebuilt before the post-ImGui chain
   ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L213-L217),
   [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L234-L235)).[^sx-patches]
6. **Barriers.** Every referenced image is collected once and transitioned to `SampledReadF` at the
   start of `Chain.Render`
   ([ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs#L246-L259)).
7. **Descriptor.** `WriteDescriptors` writes the resolved image view with
   `Program.LinearClampedSampler` in `ShaderReadOnlyOptimal`, and throws if the reference was never
   resolved
   ([ShaderExtensions/PostProcessing/PostProcessingOutputReference.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingOutputReference.cs#L74-L87)).

KSA_AircraftHUD's asset file shows the input form, written against the older subpass API:
`<PostProcessingInputReference RenderPassId="62" SubpassId="0" />`
([HudAssests.xml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/HudAssests.xml#L14-L19)).[^hud-assets]

## Lifecycle and timing

- **Resolved** when a chain is built: `RebuildRenderer` postfix (`frame:prepare` or
  `frame:post-render`, wherever the game rebuilds) or inline in `ImGuiPreRender` on an extent change
  (`frame:render`).
- **Sampled** while `Program.RenderGame` records (`frame:render`), at the pre-ImGui or post-ImGui
  insertion point.

## Key code

| Path | Role |
|---|---|
| [ShaderExtensions/PostProcessing/PostProcessingInputReference.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingInputReference.cs#L14-L80) | Input reference element |
| [ShaderExtensions/PostProcessing/PostProcessingOutputReference.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingOutputReference.cs#L14-L91) | Output reference element |
| [ShaderExtensions/PostProcessing/PostProcessingInputResolver.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingInputResolver.cs#L6-L62) | `(stage, RenderPassId)` registry |
| [ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs#L67-L90) | Registration order during `Build` |

## Snippets

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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `RenderImage.Image`, `RenderImage.ImageView` | [current/decomp/KSA.Rendering/RenderImage.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderImage.cs#L46-L48) | Referenced images and their views |
| `Program.LinearClampedSampler` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L472) | Sampler for each reference |

## Pitfalls and version notes

- **Only backwards.** A reference resolves only against passes already built: lower `RenderPassId`
  in the same stage, or any pre-ImGui pass from a post-ImGui shader. An input reference may also name
  the shader's own `RenderPassId`, because the pass's input is registered before its references are
  resolved; that is the same image as binding 0. Anything else throws
  `InvalidOperationException("No ... post-processing input/output found for RenderPassId N")` out of
  the rebuild.
- **An output reference without `RenderPassId` points at its own pass.** Its output is registered
  only after the pass is created, so resolution fails (or, if an earlier pass shares the id, binds
  that pass's output). Always give output references an explicit id.
- **Equal ids overwrite.** The registry keeps the last pass registered for an id.
- **The pre-ImGui source is reused.** The first pre-ImGui pass's input is the game's offscreen image,
  and the chain copies its final result back into that image
  ([ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs#L38-L46)).
  A post-ImGui shader that references that input therefore sees the processed scene, not the
  original.[^sx-prehandler]
- **Not documented in the README**; the attribute semantics above come from the classes.
- **KSA_AircraftHUD is on the old API** at its pinned ref (`SubpassId`, `RequiresUniqueRenderpass`),
  so ShaderExtensions 1.0.5 skips those shaders with an error.

## See also

- [RenderPassId-ordered fullscreen pass chain](post-processing-pass-chain.md)
- [Pre-ImGui post-processing on the HDR scene image](pre-imgui-post-processing.md)
- [Post-ImGui global post shaders on the swapchain image](post-imgui-global-post-shaders.md)
- [Choose the stage: a post-ImGui chain on the presented image via swapchain copies](../../../howto/shaders/post-processing/choose-pre-or-post-imgui-post-processing/choose-pre-or-post-imgui-post-processing-2.md) - how-to article demonstrating this feature
- [Declare post-processing assets: input and output references resolved while the chain is built](../../../howto/shaders/post-processing/declare-post-processing-shader-assets-in-xml/declare-post-processing-shader-assets-in-xml-3.md) - how-to article demonstrating this feature

[^sx-inputref]: ShaderExtensions/PostProcessing/PostProcessingInputReference.cs
[^sx-outputref]: ShaderExtensions/PostProcessing/PostProcessingOutputReference.cs
[^sx-assetex]: ShaderExtensions/AssetEx.cs
[^sx-preasset]: ShaderExtensions/PostProcessing/PreImgui/PostProcessingShaderAsset.cs
[^sx-resolver]: ShaderExtensions/PostProcessing/PostProcessingInputResolver.cs
[^sx-chain]: ShaderExtensions/PostProcessing/PostProcessingChain.cs
[^sx-patches]: ShaderExtensions/Patches.cs
[^hud-assets]: KSA_AircraftHUD/HudAssests.xml
[^sx-prehandler]: ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs
