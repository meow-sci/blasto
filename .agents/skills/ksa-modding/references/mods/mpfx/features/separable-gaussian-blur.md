---
type: Mod Feature
title: Separable Gaussian blur with CPU-computed weights
description: A two-pass horizontal and vertical Gaussian blur where C# computes normalized one-sided weights for a radius up to 20 and packs them into a std140 int-plus-vec4[6] uniform block, one shader file serving both chains through different buffer Ids
mod: mpfx
category: shaders
techniques: [glsl-shader, post-processing, vulkan-buffer]
hooks: [starmap StarMapAfterGui, xml-asset PostProcessingShader / GlobalPostShader (ShaderExtensions)]
lifecycle: [frame:draw-ui, render:post-process]
game_types: [KSA.KeyHash]
tags: [shaders, glsl, blur, gaussian, std140, uniform-buffer, separable-filter]
---

The newest MPFX effect, missing from the README's list, is a separable Gaussian blur on either side of
ImGui. Two passes (`RenderPassId` 142 horizontal, 143 vertical) sample up to 20 texels each way.
The weights are not computed on the GPU. C# recomputes them whenever the shared settings dirty flag is
raised (any setting change, not only the radius) and uploads them with the radius in one small uniform
block.[^mpfx-main] [^mpfx-assets]

## How it works

1. **Weights on the CPU.** `CalculateGaussianWeights(radius, weights, out shaderRadius)` clamps the radius
   to [0, 20]. It sets `shaderRadius = ceil(radius)` and `sigma = radius / 3`, fills
   `w[i] = exp(-i² / 2σ²)` for `i = 0..shaderRadius`, and normalizes by `w[0] + 2 * Σ w[i>0]`, so the
   symmetric kernel sums to 1. A radius of 0 gives `w[0] = 1` and `shaderRadius = 0`
   ([MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L91-L118)).
2. **Upload.** `UpdateGaussianBlurBuffer(id, enabled, radius)` computes into a `stackalloc double[21]`,
   gets a `Span<MPFXGaussianBlurBuffer>` for the Id, sets `Radius`, and copies the weights into the
   `fixed float Weights[24]` through a `fixed` pointer. A disabled side gets radius 0
   ([MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L73-L89)). It runs for both
   `MPFXGaussianBlurPreBuffer` and `MPFXGaussianBlurPostBuffer` whenever the dirty flag is up.
3. **std140 packing by hand.** In GLSL the block is `int radius; vec4 weights[6];`. Under std140 an
   array element is aligned to 16 bytes, so `weights` starts at offset 16. The C# struct is `Pack = 1`
   with `int Radius` plus three `int` padding fields (16 bytes), then `fixed float Weights[24]`
   (96 bytes): 112 bytes on both sides ([MPFX/Buffers/MPFXGaussianBlurBuffer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers/MPFXGaussianBlurBuffer.cs#L6-L18)).
   Declaring `float weights[24]` in GLSL would instead give each float a 16-byte stride. Packing four
   floats per `vec4` and indexing with `weights[i >> 2][i & 3]` avoids that.[^mpfx-blurbuffer]
4. **One file per axis, shared by both chains.** `GaussianBlurHorizontal.frag` and
   `GaussianBlurVertical.frag` differ only in the offset axis. Each takes `w[0]` at the center and adds `±i` texels times `w[i]`, clamping UVs to
   half a texel inside the edges ([MPFX/Shaders/GaussianBlurHorizontal.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/GaussianBlurHorizontal.frag#L27-L49)).
   The XML uses the same `.frag` for the pre (`PostProcessingShader`) and post (`GlobalPostShader`)
   elements, and selects the data by buffer Id. The vertical pass references the horizontal pass's
   buffer by Id without `Size` ([MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml#L103-L115)).[^mpfx-sh-gaussianblurhorizontal]

## Lifecycle and timing

- **`frame:draw-ui`.** Weights are computed and written in `[StarMapAfterGui]`, only on frames where
  `UpdateValues` is set. A slider drag recomputes up to 21 exponentials per side once per changed
  frame.
- **`render:post-process`.** ShaderExtensions runs pass 142, then pass 143, in each chain. The second
  pass samples the first pass's render target, so the 2D blur costs `2 * (2r + 1)` texture reads per
  pixel instead of `(2r + 1)²`.
- The pre side blurs the HDR scene before tonemapping. The post side blurs the finished frame, UI
  included.

## Key code

| Path | Role |
|---|---|
| [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L73-L118) | Weight computation and upload |
| [MPFX/Buffers/MPFXGaussianBlurBuffer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers/MPFXGaussianBlurBuffer.cs) | Padded std140 struct |
| [MPFX/Shaders/GaussianBlurHorizontal.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/GaussianBlurHorizontal.frag), [MPFX/Shaders/GaussianBlurVertical.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/GaussianBlurVertical.frag) | The two passes |

## Snippets

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

```csharp
// MPFX/Buffers/MPFXGaussianBlurBuffer.cs L6-L18
[StructLayout(LayoutKind.Sequential, Pack = 1)]
[SxUniformBuffer("MPFXGaussianBlurBufferAsset")]
public unsafe struct MPFXGaussianBlurBuffer
{
    public int Radius;
    public int Padding0;
    public int Padding1;
    public int Padding2;
    public fixed float Weights[24];

    // lookup delegate fields must be static fields on the buffer element type
    [SxUniformBufferLookup] public static MPFXSpanLookup<MPFXGaussianBlurBuffer> LookupSpan; // gives a Span<T> of length Size
}
```

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

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `KSA.KeyHash.Make` | via the lookup call | Selecting the pre or post blur buffer |

No other game API is involved. The blur is pure ShaderExtensions plumbing plus math.

## Pitfalls and version notes

- `Weights` holds 24 floats but only 21 are ever written (`GaussianBlurMaxRadius + 1`). The shader never
  reads past `radius`, so the spare slots are harmless.
- The pre and post sides share `RenderPassId` 142/143 but live in separate chains, so they do not
  interfere.
- The sigma is tied to the radius (`radius / 3`), so the blur strength and the tap count cannot be
  tuned separately.

## See also

- [Uniform buffer and push constant structs bound by attribute name](duck-typed-buffer-structs.md)
- [Per-frame effect parameter upload](per-frame-parameter-upload.md)
- [Color grading shader math](color-grading-math.md)
- [Declare post-processing assets: paired pre and post passes sharing a RenderPassId and buffer](../../../howto/shaders/post-processing/declare-post-processing-shader-assets-in-xml/declare-post-processing-shader-assets-in-xml-1.md) - how-to article demonstrating this feature
- [Feed a shader with uniform buffers: Pack=1 structs with manual std140 padding, one block per effect](../../../howto/shaders/post-processing/feed-uniform-buffers-to-a-shaderextensions-shader/feed-uniform-buffers-to-a-shaderextensions-shader-1.md) - how-to article demonstrating this feature
- [Blur the screen: CPU-computed normalized weights up to radius 20 in a std140 block](../../../howto/shaders/screen-effects/blur-the-screen-with-a-separable-gaussian/blur-the-screen-with-a-separable-gaussian-1.md) - how-to article demonstrating this feature

[^mpfx-main]: MPFX/MPFX.cs
[^mpfx-assets]: MPFX/MPFXAssests.xml
[^mpfx-blurbuffer]: MPFX/Buffers/MPFXGaussianBlurBuffer.cs
[^mpfx-sh-gaussianblurhorizontal]: MPFX/Shaders/GaussianBlurHorizontal.frag
