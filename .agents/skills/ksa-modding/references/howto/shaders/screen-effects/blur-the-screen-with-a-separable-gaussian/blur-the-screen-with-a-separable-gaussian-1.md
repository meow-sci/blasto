---
type: How-To
title: "Blur the screen: CPU-computed normalized weights up to radius 20 in a std140 block"
description: Compute Gaussian weights on the CPU, upload them with the radius in one uniform block and run one shader file as the horizontal and vertical pass of both ShaderExtensions chains
category: shaders
group: screen-effects
topic: blur-the-screen-with-a-separable-gaussian
approach: 1
difficulty: intermediate
prerequisites: [starmap, shaderextensions, kittenextensions]
demonstrated_by: [mods/mpfx/features/separable-gaussian-blur]
techniques: [glsl-shader, vulkan-buffer, post-processing]
hooks: [xml-asset PostProcessingShader, starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, render:post-process]
game_types: [KSA.Program]
tags: [blur, gaussian, weights, std140, uniform-buffer, post-processing, mpfx]
---
MPFX's blur is two passes (`RenderPassId` 142 horizontal, 143 vertical) that sample up to 20 texels each way. The weights are not computed on the GPU: C# recomputes them whenever the shared settings dirty flag is raised and uploads them with the radius in one small uniform block. This article is the full recipe: the weight maths, the std140 packing that makes a 21-float array fit, the shader, and the XML that
chains the passes. You need ShaderExtensions and a uniform struct set up as in [Feed a ShaderExtensions shader from C# with uniform buffers (std140)](../../post-processing/feed-uniform-buffers-to-a-shaderextensions-shader/overview.md).

## Background: how the game does it

Nothing in the base game provides this; it is a ShaderExtensions chain. Pass 143 samples pass 142's render target, and in each chain the same shader file serves the pre-ImGui (HDR scene) and post-ImGui (final frame) sides, selecting its parameters by buffer Id. A uniform block cannot hold a tightly packed float array: under std140 an array element is aligned to 16 bytes, so `float weights[24]` would give each float a 16-byte stride (384 bytes) and no longer match the C# layout. Packing four floats into a `vec4` and indexing with `weights[i >> 2][i & 3]` keeps it compact (112 bytes with the radius).

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| Weights computed and written | `[StarMapAfterGui]`, only on frames where the settings dirty flag is set | `frame:draw-ui` |
| Pass 142 then pass 143 in each chain | `Program.RenderGame` | `render:post-process` |

A slider drag recomputes up to 21 exponentials per side once per changed frame. The pre side blurs the HDR scene before tonemapping; the post side blurs the finished frame including the UI. See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Compute normalized weights on the CPU.** Clamp the radius to [0, 20], use `sigma = radius / 3`, fill `w[i] = exp(-i^2 / 2 sigma^2)`, and normalise by `w[0] + 2 * sum(w[i>0])` so the symmetric kernel sums to 1. Radius 0 gives `w[0] = 1` and `shaderRadius = 0`:

   ```csharp
   // MPFX/MPFX.cs L91-L118
           private static void CalculateGaussianWeights(double radius, Span<double> weights, out int shaderRadius)
           {
               weights.Clear();
               radius = Math.Clamp(radius, 0.0, GaussianBlurMaxRadius);
               shaderRadius = (int)Math.Ceiling(radius);

               if (radius <= 0.0 || shaderRadius == 0)
               {
                   weights[0] = 1.0;
                   return;
               }

               double sigma = radius / 3.0;
               double twoSigmaSquared = 2.0 * sigma * sigma;
               double total = 0.0;

               for (int i = 0; i <= shaderRadius; i++)
               {
                   double weight = Math.Exp(-(i * i) / twoSigmaSquared);
                   weights[i] = weight;
                   total += i == 0 ? weight : weight * 2.0;
               }

               for (int i = 0; i <= shaderRadius; i++)
               {
                   weights[i] /= total;
               }
           }
   ```

   Source: [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L91-L118).

2. **Declare a std140-matched struct and upload.** `int Radius` plus three padding ints (16 bytes) then 24 floats (96 bytes) is 112 bytes, equal to the GLSL block; a disabled side gets radius 0:

   ```csharp
   // MPFX/MPFX.cs L73-L89
           private static unsafe void UpdateGaussianBlurBuffer(string id, bool enabled, double radius)
           {
               Span<MPFXGaussianBlurBuffer> data = MPFXGaussianBlurBuffer.LookupSpan(KeyHash.Make(id));
               ref MPFXGaussianBlurBuffer blur = ref data[0];

               Span<double> weights = stackalloc double[GaussianBlurMaxRadius + 1];
               CalculateGaussianWeights(enabled ? radius : 0.0, weights, out int shaderRadius);
               blur.Radius = shaderRadius;

               fixed (float* destination = blur.Weights)
               {
                   for (int i = 0; i <= GaussianBlurMaxRadius; i++)
                   {
                       destination[i] = (float)weights[i];
                   }
               }
           }
   ```

   Source: [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L73-L89); the struct is [MPFX/Buffers/MPFXGaussianBlurBuffer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers/MPFXGaussianBlurBuffer.cs#L6-L18) (shown in [Feed a ShaderExtensions shader from C# with uniform buffers (std140)](../../post-processing/feed-uniform-buffers-to-a-shaderextensions-shader/overview.md)). It runs for `MPFXGaussianBlurPreBuffer` and `MPFXGaussianBlurPostBuffer` whenever the dirty flag is up.

3. **Write the horizontal shader.** Take `w[0]` at the centre and add `+-i` texels times `w[i]`, clamping UVs half a texel inside the edges. The vertical shader is the same with the offset on y:

   ```glsl
   // MPFX/Shaders/GaussianBlurHorizontal.frag L15-L49
   layout(std140, set = 1, binding = 2) uniform BlurParams {
     int radius;
     vec4 weights[6];
   } blur;

   float getWeight(int index)
   {
     // index >> 2 == index / 4
     // index & 3 == index % 4
     return blur.weights[index >> 2][index & 3];
   }

   void main()
   {
     if (blur.radius <= 0) 
     {
         Out = texture(In, Uv);
         return;
     }

     vec4 c = texture(In, Uv) * getWeight(0);
     vec2 texelSize = 1.0 / vec2(textureSize(In, 0));

     vec2 minUv = 0.5 * texelSize;
     vec2 maxUv = 1.0 - minUv;

     for (int i = 1; i <= blur.radius; i++) {
       vec2 offset = vec2(float(i) * texelSize.x, 0.0);

       c += texture(In, clamp(Uv + offset, minUv, maxUv)) * getWeight(i);
       c += texture(In, clamp(Uv - offset, minUv, maxUv)) * getWeight(i);
     }

     Out = c;
   }
   ```

   Source: [MPFX/Shaders/GaussianBlurHorizontal.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/GaussianBlurHorizontal.frag#L15-L49). The radius-0 early return is the neutral value, so the pass is a copy when the effect is off.

4. **Declare the chain in XML.** The same `.frag` for the pre and post elements, the data selected by buffer Id; the vertical pass references the horizontal pass's buffer without `Size`:

   ```xml
   <!-- MPFX/MPFXAssests.xml L103-L115 -->
   <PostProcessingShader Id="MPFXGaussianBlurShaderHorizontalPre" Path="Shaders/GaussianBlurHorizontal.frag" RenderPassId="142">
       <MPFXGaussianBlurBufferAsset Id="MPFXGaussianBlurPreBuffer" Size="1" />
   </PostProcessingShader>
   <GlobalPostShader Id="MPFXGaussianBlurShaderHorizontalPost" Path="Shaders/GaussianBlurHorizontal.frag" RenderPassId="142">
       <MPFXGaussianBlurBufferAsset Id="MPFXGaussianBlurPostBuffer" Size="1" />
   </GlobalPostShader>

   <PostProcessingShader Id="MPFXGaussianBlurShaderVerticalPre" Path="Shaders/GaussianBlurVertical.frag" RenderPassId="143">
       <MPFXGaussianBlurBufferAsset Id="MPFXGaussianBlurPreBuffer" />
   </PostProcessingShader>
   <GlobalPostShader Id="MPFXGaussianBlurShaderVerticalPost" Path="Shaders/GaussianBlurVertical.frag" RenderPassId="143">
       <MPFXGaussianBlurBufferAsset Id="MPFXGaussianBlurPostBuffer" />
   </GlobalPostShader>
   ```

   Source: [MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml#L103-L115).

## Complete example

| File | Role |
|---|---|
| [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs) | Weight computation and upload |
| [MPFX/Buffers/MPFXGaussianBlurBuffer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers/MPFXGaussianBlurBuffer.cs) | Padded std140 struct |
| [MPFX/Shaders/GaussianBlurHorizontal.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/GaussianBlurHorizontal.frag), [MPFX/Shaders/GaussianBlurVertical.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/GaussianBlurVertical.frag) | The two passes |
| [MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml) | The chain |

## Variations and gotchas

- **Spare slots.** `Weights` holds 24 floats but only 21 are written (`GaussianBlurMaxRadius + 1`); the shader never reads past `radius`.
- **Sigma is tied to the radius** (`radius / 3`), so blur strength and tap count cannot be tuned separately.
- **Cost.** `2 * (2r + 1)` texture reads per pixel for the 2D blur, at full resolution, two extra full-size targets.
- **Dirty flag.** Weights are recomputed on any setting change, not only the radius.
- **Both halves must agree** when sharing a buffer: the vertical pass reads the horizontal pass's block.

## See also

- Sibling: [non-linear taps and a refracted pass](blur-the-screen-with-a-separable-gaussian-2.md); [overview](overview.md)
- [Separable Gaussian blur with CPU-computed weights](../../../../mods/mpfx/features/separable-gaussian-blur.md)
- [Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](../../post-processing/declare-post-processing-shader-assets-in-xml/overview.md), [Feed a ShaderExtensions shader from C# with uniform buffers (std140)](../../post-processing/feed-uniform-buffers-to-a-shaderextensions-shader/overview.md)
