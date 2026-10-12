---
type: How-To
title: "Feed a shader with uniform buffers: Pack=1 structs with manual std140 padding, one block per effect"
description: Define an effect's parameters as a Pack=1 C# struct, register it by attribute name with no ShaderExtensions reference, and write it from StarMapAfterGui
category: shaders
group: post-processing
topic: feed-uniform-buffers-to-a-shaderextensions-shader
approach: 1
difficulty: intermediate
prerequisites: [starmap, shaderextensions, kittenextensions]
demonstrated_by:
  - mods/mpfx/features/duck-typed-buffer-structs
  - mods/mpfx/features/separable-gaussian-blur
  - mods/mpfx/features/color-grading-math
techniques: [vulkan-buffer, reflection, glsl-shader]
hooks:
  - starmap StarMapAfterGui
  - xml-asset MPFXDefaultBufferAsset
  - harmony-prefix KSA.Program.Main (ShaderExtensions AssetEx.Init)
lifecycle: [startup:starmap-before-main, startup:asset-load, frame:draw-ui, render:post-process]
game_types:
  - KSA.KeyHash
  - Brutal.VulkanApi.Abstractions.BufferEx
  - Brutal.VulkanApi.Abstractions.MappedMemory
  - Brutal.Numerics.float4x4
  - KSA.Program
tags: [uniform-buffer, std140, attributes, duck-typing, mpfx, gaussian-weights, float4x4]
---
This is the whole pipeline of a ShaderExtensions uniform buffer as MPFX builds it: copy four tiny attribute
declarations into your assembly, tag a struct, name the XML element, declare the same layout in GLSL, and
write the struct from a per-frame hook. You need StarMap, KittenExtensions Continued and ShaderExtensions
installed; your project needs **no** reference to `ShaderExtensions.dll`.

## Background: how the game does it

- ShaderExtensions' `Program.Main` prefix calls `AssetEx.Init`, which walks every assembly of every
  `AssemblyLoadContext`; the ones in which `ShaderExtensions.SxUniformBufferAttribute` resolves are scanned
  for tagged structs. Attributes are compared by `FullName`, never by identity, which is why each mod can carry its own
  copy ([ShaderExtensions/AssetEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/AssetEx.cs#L17-L47)).
- For `[SxUniformBuffer("X")]` on struct `T`, `UniformBufferEx.AddUniformBufferGeneric<T>` injects the element
  `X` into `ShaderEx.XmlBindings` and `AssetBundle.Assets` and assigns every `[SxUniformBufferLookup]` static delegate
  field a method chosen by the delegate's return type: `BufferEx`, `MappedMemory`, `Span<T>` or `T*`
  ([ShaderExtensions/UniformBufferEx.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/UniformBufferEx.cs#L22-L74)). `AssetEx.Init` then rebuilds the game's XML serializers so
  the new element names parse.
- The buffer itself is created in `ModLibrary.Bind` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L1849-L1861)).

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| Attribute probe, element injection, delegate assignment | ShaderExtensions' `Program.Main` prefix, after StarMap loaded every mod assembly | `startup:starmap-before-main` |
| XML parsed, owners registered | `ModLibrary.LoadAll` | `startup:asset-load` |
| GPU buffer created and mapped | `ModLibrary.Bind` | `startup:asset-load` |
| Your writes | `[StarMapAfterGui]` runs in `Program.OnFrame` before `ImGui.Render` and `Render` | `frame:draw-ui` |
| GPU reads | When the command buffer recorded in `RenderGame` executes | `render:post-process` |

Calling a lookup earlier than `ModLibrary.Bind` fails: the owner exists but its mapped memory is the default
value, so a span lookup throws `ArgumentException` and a pointer lookup returns null. MPFX calls the delegates only
from its per-frame hook, long after load. See the [game loop and lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for the full order.

## Steps

1. **Copy the attribute shims into your assembly, in the `ShaderExtensions` namespace.** All four are needed even
   if you only use push constants, because the probe looks for the uniform-buffer attribute type:

   ```csharp
   // MPFX/Buffers.cs L7-L25
   namespace ShaderExtensions
   {
       [AttributeUsage(AttributeTargets.Struct)]
       internal class SxUniformBufferAttribute(string xmlElement) : Attribute;

       [AttributeUsage(AttributeTargets.Field)]
       internal class SxUniformBufferLookupAttribute() : Attribute;

       [AttributeUsage(AttributeTargets.Struct)]
       internal class SxPushConstantAttribute(string xmlElement) : Attribute;

       [AttributeUsage(AttributeTargets.Field)]
       internal class SxPushConstantLookupAttribute() : Attribute;

       public delegate BufferEx MPFXBufferLookup(KeyHash hash);
       public delegate MappedMemory MPFXMemoryLookup(KeyHash hash);
       public delegate Span<T> MPFXSpanLookup<T>(KeyHash hash) where T : unmanaged;
       public unsafe delegate T* MPFXPtrLookup<T>(KeyHash hash) where T : unmanaged;
   }
   ```

   Source: [MPFX/Buffers.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers.cs#L7-L25). Only the delegate signature matters; prefix the delegate names with your mod name.
   In `mod.toml` declare ShaderExtensions as a StarMap dependency (MPFX does this in its csproj with a
   `StarMapModDependency` item, [MPFX/MPFX.csproj](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.csproj#L78-L80)).

2. **Declare the struct.** `Pack = 1` plus explicit padding keeps C# offsets equal to std140 offsets. The XML
   element name comes from the attribute string, not the struct name:

   ```csharp
   // MPFX/Buffers/MPFXDefaultBuffer.cs L8-L22
       [StructLayout(LayoutKind.Sequential, Pack = 1)]
       [SxUniformBuffer("MPFXDefaultBufferAsset")]
       public struct MPFXDefaultBuffer
       {
           public float a;
           public float b;

           // lookup delegate fields must be static fields on the buffer element type
           // the names and specific types of these are not relevant, as long as the delegate signature matches
           // these are not all required, but you will need at least one to be able to set the uniform data
           [SxUniformBufferLookup] public static MPFXBufferLookup LookupBuffer;
           [SxUniformBufferLookup] public static MPFXMemoryLookup LookupMemory;
           [SxUniformBufferLookup] public static MPFXSpanLookup<MPFXDefaultBuffer> LookupSpan; // gives a Span<T> of length Size
           [SxUniformBufferLookup] public static MPFXPtrLookup<MPFXDefaultBuffer> LookupPtr; // gives T* to first element
       }
   ```

   Source: [MPFX/Buffers/MPFXDefaultBuffer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers/MPFXDefaultBuffer.cs#L8-L22). One lookup is enough; the others are optional.

3. **Declare the element under the shader and the block in GLSL.** Under a `PostProcessingShader` the buffer is the first
   child, so it lands at `set = 1, binding = 2`:

   ```xml
   <!-- MPFX/MPFXAssests.xml L24-L26 -->
   	<PostProcessingShader Id="MPFXBrightnessShaderPre" Path="Shaders/BrightnessPre.frag" RenderPassId="131">
   		<MPFXDefaultBufferAsset Id="MPFXBrightnessBuffer" Size="1" />
   	</PostProcessingShader>
   ```

   ```glsl
   // MPFX/Shaders/BrightnessPre.frag L15-L18
   layout(set = 1, binding = 2) uniform MPFXDefaultBufferAsset {
     float preAmount;
     float postAmount;
   };
   ```

   `Size` is the number of sequential structs in the buffer. Without an `Id` the buffer is created but can never be
   looked up. Source: [MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml#L24-L26), [MPFX/Shaders/BrightnessPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/BrightnessPre.frag#L15-L18).

4. **Write it, guarded.** Look up by `KeyHash.Make(id)`; check the delegate for null in case ShaderExtensions is not
   installed. MPFX recomputes only when a setting changes (`UpdateValues`) and writes "neutral" values for disabled
   effects:

   ```csharp
   // MPFX/MPFX.cs L140-L148
               if (UpdateValues)
               {
                   UpdateValues = false;

                   if (MPFXDefaultBuffer.LookupSpan != null)
                   {
                       Span<MPFXDefaultBuffer> BrightnessData = MPFXDefaultBuffer.LookupSpan(KeyHash.Make("MPFXBrightnessBuffer"));
                       BrightnessData[0].a = CurrentProfile.BrightnessPreImgui ? CurrentProfile.BrightnessFloatPreImgui : 1f;
                       BrightnessData[0].b = CurrentProfile.BrightnessPostImgui ? CurrentProfile.BrightnessFloatPostImgui : 1f;
   ```

   Source: [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L140-L148), inside the `[StarMapAfterGui]` method [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs#L120-L121). Cache the `KeyHash`
   rather than re-making it every frame if you write often.

5. **Pad for std140 when the block is not just scalars.** The blur block holds an `int` and six `vec4`s used as 24 floats.
   The C# struct spells the padding out and uses a fixed array; the shader indexes it as `weights[i >> 2][i & 3]`:

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
   // MPFX/Shaders/GaussianBlurHorizontal.frag L15-L25
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
   ```

   `int` at 0, then `vec4[6]` aligned to 16: offsets 16 to 112, 112 bytes in total, equal to 4 + 12 + 96 on the C# side.
   Sources: [MPFX/Buffers/MPFXGaussianBlurBuffer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers/MPFXGaussianBlurBuffer.cs#L6-L18), [MPFX/Shaders/GaussianBlurHorizontal.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/GaussianBlurHorizontal.frag#L15-L25). The CPU weight
   computation is in [Blur the screen with a two-pass separable Gaussian](../../screen-effects/blur-the-screen-with-a-separable-gaussian/overview.md).

6. **Matrices.** `Brutal.Numerics.float4x4` stores four row vectors; GLSL `mat4` is column-major, so C# row `r` arrives as GLSL
   `preData[r]`. MPFX uses this on purpose: row 0 is lift, row 1 gamma, row 2 gain and element `[3,3]` is an on/off flag
   ([MPFX/Buffers/MPFXMat4Buffer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers/MPFXMat4Buffer.cs#L11-L25), [MPFX/Shaders/ColorBalancePre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/ColorBalancePre.frag#L15-L36)).

## Complete example

| File | Role |
|---|---|
| [MPFX/Buffers.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers.cs) | Attribute and delegate shims |
| [MPFX/Buffers/MPFXDefaultBuffer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers/MPFXDefaultBuffer.cs), [MPFX/Buffers/MPFXMat4Buffer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers/MPFXMat4Buffer.cs), [MPFX/Buffers/MPFXGaussianBlurBuffer.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Buffers/MPFXGaussianBlurBuffer.cs) | Three layouts: two floats, two matrices, int plus padded array |
| [MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml) | Elements under each shader |
| [MPFX/MPFX.cs](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFX.cs) | Per-frame writes (`AfterGui`) |

## Variations and gotchas

- **The element name is global.** Two mods registering the same name for different structs collide; prefix with the mod name.
  `MPFXMat4Buffer` registers `MPFXMat3BufferAsset` (sic), which is easy to misread.
- **References resolve by `Id` only.** A reference element of one struct type may point at a buffer owned by another type, as
  MPFX's colour balance does; the GLSL block must follow the owner's layout.
- **One copy, two frames in flight.** A write can become visible to a frame the GPU is still executing. For values that must
  match the recorded frame exactly use a push constant ([Send per-frame values and time to a shader with push constants, written at the right point in the frame](../send-push-constants-to-a-shaderextensions-shader/overview.md)).
- **Updates stop with the HUD.** `[StarMapAfterGui]` does not fire while the HUD is hidden, so values written only there freeze
  while the passes keep running.
- **Unknown id.** `LookupCollection.Get` returns null, so a typo ends in a `NullReferenceException` on the first write.

## See also

- Siblings: [share one buffer across passes](feed-uniform-buffers-to-a-shaderextensions-shader-2.md), [lookup forms in a HUD mod](feed-uniform-buffers-to-a-shaderextensions-shader-3.md), [overview](overview.md)
- [Uniform buffer and push constant structs bound by attribute name](../../../../mods/mpfx/features/duck-typed-buffer-structs.md), [Separable Gaussian blur with CPU-computed weights](../../../../mods/mpfx/features/separable-gaussian-blur.md), [Color grading shader math](../../../../mods/mpfx/features/color-grading-math.md)
- [Colour-grade the frame and add an aspect-correct vignette](../../screen-effects/colour-grade-the-frame-and-add-a-vignette/overview.md), [Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](../declare-post-processing-shader-assets-in-xml/overview.md)
