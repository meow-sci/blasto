---
type: Mod Feature
title: Shader contract for chain passes
description: Each MPFX fragment shader follows the ShaderExtensions pass contract, with the previous pass as sampler2D at set 1 binding 0, ShaderTime at binding 1, one parameter block at binding 2 and an optional push constant, and returns its input when parameters are neutral
mod: mpfx
category: shaders
techniques: [glsl-shader, post-processing, vulkan-buffer]
hooks: [xml-asset PostProcessingShader / GlobalPostShader (ShaderExtensions)]
lifecycle: [render:post-process]
game_types: [KSA.Program]
tags: [shaders, glsl, post-processing, shaderextensions, uniform-buffer, push-constants, descriptor-sets]
---

The 30 MPFX shaders show the minimum a ShaderExtensions post-process pass needs. Each one is a
standalone `#version 450` fragment shader drawn over a fullscreen quad, which ShaderExtensions supplies
through the game's `ScreenspaceVert`. Each pass reads the previous pass through a `sampler2D` and writes
one `vec4`. Parameters arrive in a uniform block whose layout mirrors a C# struct. Because a
ShaderExtensions pass cannot be switched off, each shader is written so that neutral parameters return
its input unchanged.[^sx-readme] [^mpfx-sh-brightnesspre]

## How it works

| Slot | Declaration in MPFX | Supplied by |
|---|---|---|
| `layout(location = 0) in vec2 Uv` | every shader | The game's `ScreenspaceVert` (`Shaders/PostProcess/screenspace_quad.vert`, declared in [current/Content/Core/DefaultAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/DefaultAssets.xml#L94)), the default `VertexShaderID`[^ksa-defaultassets] |
| `layout(location = 0) out vec4 Out` | every shader | The pass's own `RenderTarget`, in the chain source format |
| `set = 1, binding = 0` `uniform sampler2D In` | every shader | The previous pass output, or the game image for the first pass ([ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs#L36-L68)) |
| `std140, set = 1, binding = 1` `uniform ShaderTime { uint FrameNumber; float DeltaTime; float RealTimeSinceStart; float TimeSinceStart; float TimeWarpSpeed; } Time` | declared everywhere, read by no MPFX shader | ShaderExtensions' shared time buffer, written in a `Program.RenderGame` prefix |
| `set = 1, binding = 2` parameter block | one per shader | The first uniform-buffer child of the XML element, written as a `UniformBufferDynamic` descriptor ([ShaderExtensions/ShaderBinding.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderBinding.cs#L80)) whose dynamic offset is always 0. GLSL declares it as a plain `uniform` block. Further binding children would take binding 3 onward (`Binding = i + 2` over `ShaderEx.Bindings`, which skips push-constant children, in [ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L40-L49), [ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L156-L185))[^sx-shaderbinding] |
| `layout(push_constant) uniform ...` | film grain only | The single push-constant child of the XML element. It takes no binding number ([ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs#L59-L103)) |
| `set = 0` | not declared | The game's `GlobalShaderBindings.DescriptorSet(0)` (main viewport, current frame). ShaderExtensions also passes one extra dynamic offset meant for it, computed from a reflected `currentFrame` field that build 2026.10.10.5554 names `_currentFrame`, so it is always 0, and set 0 has only plain `UniformBuffer` bindings ([ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs#L89-L135), [current/decomp/KSA/GlobalShaderBindings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GlobalShaderBindings.cs#L33-L59)). MPFX never reads set 0, so this does not affect it[^ksa-globalshaderbindings] |

Rules MPFX follows (all visible in the snippets):

- **The block name does not matter, the layout does.** Blocks are called `MPFXDefaultBufferAsset`,
  `DataBuffer`, `ColorBuffer`, `MPFXColorBufferAsset` or `BlurParams`. Only binding 2 and the member
  layout must match the C# struct (two floats, two `vec4`, two `mat4`, or `int` plus `vec4[6]`).
- **One block often serves both sides.** Where both XML elements point at the same buffer (color
  balance, temperature, hue, brightness, contrast, saturation, overlay and the experimental passes),
  pre shaders read the first member (`preAmount`, `preData`) and post shaders the second
  (`postAmount`, `postData`). Vignette and blur have one buffer per side, so there the members are
  data and color (vignette) or radius and weights (blur). Film grain packs both sides into one `vec4`
  (`data.xy` pre, `data.zw` post) and shares `config`.
- **Neutral means identity.** Each effect turns into a pass-through when its parameters are neutral:
  brightness and contrast multiply by 1, saturation mixes by `1 - 1 = 0`, and hue shift adds 0 radians
  (identity to within about 0.1 percent, because the YIQ constants are rounded).
  The overlay uses alpha 0, and blur radius 0 returns early. Color balance tests `preData[3][3] == 0`,
  and color temperature and vignette test for 0 explicitly ([MPFX/Shaders/ColorBalancePre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/ColorBalancePre.frag#L21-L36)).[^mpfx-sh-colorbalancepre]
- **Pre shaders see HDR, post shaders see display values.** The pre chain samples the
  `R16G16B16A16SFloat` offscreen image before tonemapping
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L223)). That is why the HSV shaders clamp to [0, 1] on the
  pre side only ("HDR is used until imgui"), and why contrast pivots on mid-gray 0.18 rather than 0.5.
  The film grain shaders clamp unconditionally on both sides, which on the pre side caps everything
  after pass 136 at 1 (see
  [Effect chain declared as RenderPassId-ordered post-process assets](effect-chain-assets.md)).[^ksa-program]
- **Alpha is forced to 1** in nearly every shader. `ColorBalancePre.frag` writes alpha 0.25. Since
  `BlendNone` is used and the offscreen alpha is not otherwise documented, that is probably harmless,
  but it is not verified.

## Lifecycle and timing

The shaders run on the GPU inside the command buffer that `Program.RenderGame` records
(`render:post-process`). For each pass, ShaderExtensions begins rendering into the pass target with
`DontCare` load ops, binds the pipeline, and calls `shader.PushConstants`. It then binds
`[GlobalShaderBindings set, pass set]` with dynamic offsets and draws 4 vertices as a triangle strip
([ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingPassRenderer.cs#L98-L140)).[^sx-passrenderer] The data the
shaders see is therefore whatever the CPU wrote before that recording. For MPFX, that is the
`[StarMapAfterGui]` call in the same frame (see
[Per-frame effect parameter upload](per-frame-parameter-upload.md)). The `ShaderTime` block is written in
ShaderExtensions' `RenderGame` prefix, immediately before ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L29-L43)).[^sx-patches]

## Key code

| Path | Role |
|---|---|
| [MPFX/Shaders/BrightnessPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/BrightnessPre.frag) | Smallest complete pass: the contract plus one multiply |
| [MPFX/Shaders/FilmGrainPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/FilmGrainPre.frag#L15-L22) | Uniform block and push-constant block together |
| [MPFX/Shaders/GaussianBlurHorizontal.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/GaussianBlurHorizontal.frag#L15-L25) | Explicit `std140` block with an array packed in `vec4` |
| [ShaderExtensions/ShaderEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ShaderEx.cs) (ShaderExtensions) | Builds the set 1 layout (0 = input, 1 = time, 2+ = XML bindings) and push-constant ranges |

## Snippets

```glsl
// MPFX/Shaders/BrightnessPre.frag L1-L24
#version 450 core

layout(location = 0) out vec4 Out;
layout(location = 0) in vec2 Uv;
layout(set = 1, binding = 0) uniform sampler2D In;

layout(std140, set = 1, binding = 1) uniform ShaderTime {
  uint FrameNumber;
  float DeltaTime;
  float RealTimeSinceStart;
  float TimeSinceStart;
  float TimeWarpSpeed;
} Time;

layout(set = 1, binding = 2) uniform MPFXDefaultBufferAsset {
  float preAmount;
  float postAmount;
};

void main()
{
    vec4 c = texture(In, Uv);
  Out = vec4(c.rgb * preAmount, 1);
}
```

```glsl
// MPFX/Shaders/FilmGrainPre.frag L15-L22
layout(set = 1, binding = 2) uniform MPFXVec4BufferAsset {
  vec4 data; // preGrainSize, preGrainStrength, postGrainSize, postGrainStrength
  vec4 config; // width, height
};

layout(push_constant) uniform MPFXPushConstantsFloatAsset {
  float framenumPre;
};
```

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `Program.ColorFormat` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L223) | Format of the pre chain's input and targets (`R16G16B16A16SFloat`) |
| `ScreenspaceVert` shader asset | [current/Content/Core/DefaultAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/DefaultAssets.xml#L94) | Fullscreen quad vertex shader that outputs `Uv` at location 0 |
| `GlobalShaderBindings` | via ShaderExtensions | Set 0 of every pass pipeline layout |

## Pitfalls and version notes

- Default-layout uniform blocks (no `std140` qualifier) are std140 under Vulkan GLSL. Only the blur
  shader spells it out. Keep C# structs `Pack = 1` and add explicit padding, as the blur buffer does.
- A post shader that reads only `postAmount` still has to declare `preAmount` first. The offsets come
  from the shared struct, not from what the shader uses.
- The ShaderExtensions README says only the main window is processed. Secondary viewports are not.
- `subpassInput`/`subpassLoad` no longer work. Every input is a `sampler2D`.

## See also

- [Effect chain declared as RenderPassId-ordered post-process assets](effect-chain-assets.md)
- [Uniform buffer and push constant structs bound by attribute name](duck-typed-buffer-structs.md)
- [Color grading shader math](color-grading-math.md)
- [Push constants for a shader: per-side clock and strengths in a settings-driven mod](../../../howto/shaders/post-processing/send-push-constants-to-a-shaderextensions-shader/send-push-constants-to-a-shaderextensions-shader-2.md) - how-to article demonstrating this feature
- [Write a post-processing fragment shader: the minimal identity-for-neutral pass (MPFX contract)](../../../howto/shaders/post-processing/write-a-post-processing-fragment-shader/write-a-post-processing-fragment-shader-1.md) - how-to article demonstrating this feature
- [Colour-grade the frame: pre-ImGui grading passes on the HDR scene](../../../howto/shaders/screen-effects/colour-grade-the-frame-and-add-a-vignette/colour-grade-the-frame-and-add-a-vignette-1.md) - how-to article demonstrating this feature

[^sx-readme]: ShaderExtensions README.md
[^mpfx-sh-brightnesspre]: MPFX/Shaders/BrightnessPre.frag
[^mpfx-sh-colorbalancepre]: MPFX/Shaders/ColorBalancePre.frag
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^sx-passrenderer]: ShaderExtensions PostProcessingPassRenderer.cs
[^sx-patches]: ShaderExtensions Patches.cs
[^ksa-defaultassets]: Content/Core/DefaultAssets.xml (build 2026.10.10.5554)
[^sx-shaderbinding]: ShaderExtensions ShaderBinding.cs
[^ksa-globalshaderbindings]: KSA.GlobalShaderBindings (decompiled, build 2026.10.10.5554)
