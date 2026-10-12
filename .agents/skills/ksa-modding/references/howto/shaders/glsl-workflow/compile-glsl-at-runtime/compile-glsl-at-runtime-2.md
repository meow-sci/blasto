---
type: How-To
title: "Compile GLSL at runtime: a sticker fragment shader compiled with FromString and the game's includes"
description: Compile both stages of a depth-aware decal pass from C# raw strings, point the NUL-terminated debug path next to a stock shader so the game's headers resolve, and reconstruct positions from reverse-Z depth
category: shaders
group: glsl-workflow
topic: compile-glsl-at-runtime
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony, vulkan]
demonstrated_by: [mods/gatos/features/sticker-projected-decals]
techniques: [glsl-shader, vulkan-pipeline, harmony-postfix]
hooks: [harmony-postfix KSA.Rendering.RenderTarget.ResolveAttachments, starmap StarMapBeforeGui]
lifecycle: [frame:draw-ui, frame:render]
game_types: [RenderCore.ShaderModuleUtils, KSA.ShaderReference, KSA.ModLibrary]
tags: [glsl, shaderc, fromstring, include, debug-path, nul-terminated, decal, reverse-z, gatos]
---
gatOS draws projected decals (stickers) as a pass over the resolved scene: a unit cube per sticker whose fragment shader reads the resolved reverse-Z depth, rebuilds each pixel's position and shades a texture from the game's bindless array. Both shaders are C# raw strings compiled with `ShaderModuleUtils.FromString`. The point of this article is how such a string can `#include` the game's own headers
and how it must pass its debug path. Pair it with the injection-point details in [Build a Vulkan pipeline compatible with KSA's scene render target](../../../rendering/vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/overview.md).

## Background: how the game does it

`FromString` hands the debug name to shaderc as the input file name; the game's include callback resolves each include relative to the directory of the requesting file ([current/decomp/Brutal.ShaderCApi/ShaderC.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ShaderCApi/ShaderC.cs#L247-L255)). So a string shader behaves as if it sat in whatever directory its debug name says. The stock `Grid.frag` begins with `#include "Common/Camera.glsl"`
([current/Content/Core/Shaders/Grid.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Grid.frag)), so the directory of the `GridFrag` shader asset is the stock `Shaders` folder, and an include of `Common/Camera.glsl` or `Common/TextureSet.glsl` resolves against it. `Camera.glsl` includes `Global.glsl`, which gives `global.camera.inverseProjection` and `inverseView`.

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| Pipeline and unit cube created lazily when the registry becomes non-empty; compiled with `FromString` | Main thread, at the first recorded pass | `frame:render` |
| Pass recorded | A postfix on `RenderTarget.ResolveAttachments(CommandBuffer, bool)` when the depth flag is true, for the main viewport only | `frame:render` (after the post-gizmo resolve, before the map grid) |
| Anchors and matrices updated | `StarMapBeforeGui`, after this frame's camera was built and before the scene renders | `frame:draw-ui` |

See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence); in build 5554 `RenderGame` resolves colour only after translucency, draws bloom, orbit lines and gizmos, then resolves colour and depth and runs `GridPass` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4904-L4936)).

## Steps

1. **Find a real directory under the stock shader tree.** Take it from a shipped shader asset so it follows the install:

   ```csharp
   // gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs L332-L338
       private static string ShaderIncludeDirectory()
       {
           var reference = ModLibrary.Get<ShaderReference>("GridFrag")
                           ?? throw new InvalidOperationException("the 'GridFrag' shader asset is missing");
           return Path.GetDirectoryName(reference.ModPath)
                  ?? throw new InvalidOperationException($"'{reference.ModPath}' has no directory");
       }
   ```

   Source: [gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs#L332-L338).

2. **Compile with a NUL-terminated debug path in that directory.** The file need not exist; the name just has to make relative includes resolve:

   ```csharp
   // gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs L340-L354
       /// <summary>Compiles one GLSL string, turning a shaderc failure into a message with the full log.</summary>
       private static VkShaderModule Compile(DeviceEx device, string source, VkShaderStageFlags stage, string debugPath)
       {
           try
           {
               // The NUL is required: the include resolver reads debugName as a C string.
               return ShaderModuleUtils.FromString(device, Encoding.UTF8.GetBytes(source), stage, null,
                   Encoding.UTF8.GetBytes(debugPath + "\0"));
           }
           catch (Brutal.ShaderCApi.ShaderException ex)
           {
               throw new InvalidOperationException(
                   $"gatOS sticker shader '{Path.GetFileName(debugPath)}' failed to compile: {ex.Message}", ex);
           }
       }
   ```

   Source: [gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs#L340-L354). The caller passes `Path.Combine(directory, "gatos_sticker.vert")` and `"gatos_sticker.frag"` ([gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs#L250-L259)). The shaderc exception carries the full compile log; surface it.

3. **Write the shader preamble in the order the headers need.** `TextureSet.glsl` declares `globalTextures[]` and `samplers[]` at `SET_TEXTURE`, which must be defined first:

   ```glsl
   // gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs L646-L666
       private const string FragmentShader =
           $$"""
             #version 450

             // Must precede the include: TextureSet.glsl declares globalTextures[]/samplers[] at this set.
             #define SET_TEXTURE 2
             #include "Common/TextureSet.glsl"
             #include "Common/Camera.glsl"

             layout(set = 1, binding = 0) uniform sampler2D sceneDepth;

             {{PushBlock}}

             layout(location = 0) out vec4 outColor;

             // The fast 2.2 approximation from Common/Shared.glsl:203, inlined rather than including
             // Shared.glsl, which would pull in four more files for one pow().
             vec3 StickerGammaToLinear(vec3 sRGBValue)
             {
                 return pow(sRGBValue, vec3(2.2));
             }
   ```

   Source: [gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs#L646-L666). A raw-string interpolation (`{{PushBlock}}`) shares the push-constant struct between C# and both shaders.

4. **Reconstruct the position from reverse-Z depth.** Fetch the resolved depth, rebuild NDC, apply the camera's inverse matrices, and take the normal from screen derivatives before any discard:

   ```glsl
   // gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs L668-L693
             void main()
             {
                 // Screen-sized and single-sample after ResolveAttachments: exactly one texel per fragment.
                 ivec2 size = textureSize(sceneDepth, 0);
                 float z = texelFetch(sceneDepth, ivec2(gl_FragCoord.xy), 0).r;

                 // Same convention as Camera.ScreenToEgoNearPlane (KSA/Camera.cs:658-671): ndc = 2*p/size - 1
                 // on BOTH axes, no Y flip -- the projection already carries it (M22 is negated in
                 // ReverseDepthBufferUtils.CreatePerspectiveFieldOfViewReverseZ).
                 vec2 ndc = (gl_FragCoord.xy / vec2(size)) * 2.0 - 1.0;
                 vec4 v = global.camera.inverseProjection * vec4(ndc, z, 1.0);
                 v /= v.w;

                 // The view matrix is rotation-only (KSA/Camera.cs:482-492), so undoing it lands in ego.
                 vec3 pEgo = (global.camera.inverseView * vec4(v.xyz, 1.0)).xyz;

                 // The receiving surface's normal, from the reconstructed position's screen derivatives.
                 // Taken BEFORE any discard: derivatives are only defined in uniform control flow, and a
                 // neighbour that has already been discarded would otherwise make this undefined rather
                 // than merely noisy. (Noisy it still is at a depth discontinuity -- that is the known
                 // one-pixel edge artifact of a projected decal, and the NaN-safe tests below eat it.)
                 vec3 n = normalize(cross(dFdx(pEgo), dFdy(pEgo)));

                 // Reverse-Z: 0 is the far plane AND what untouched background reads as. A decal has
                 // nothing to stick to there.
                 if (z <= 0.0) discard;
   ```

   Source: [gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs#L668-L693). The rest of `main` tests the decal box, orients the normal, samples the sticker with the `SAMPLE_TEXTURE` helper and applies a sun term. A full explanation of depth reconstruction as a compute-pass technique is in [Reconstruct scene positions from the depth buffer and paint on them](../../compute-effects/reconstruct-scene-positions-from-depth-in-a-compute-shader/overview.md).

5. **Destroy the modules as soon as the pipeline holds the code.** Compiled modules are yours (unlike `ModLibrary` modules): the pipeline builder destroys the vertex and fragment modules in a `finally` after `CreateGraphicsPipeline`.

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerDecalRenderer.cs) | Pipeline, cube, descriptor ring, pass, both shaders |
| [gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerRenderPatches.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/Stickers/StickerRenderPatches.cs) | The `ResolveAttachments` postfix filter |

## Variations and gotchas

- **The NUL is required.** The include resolver reads the debug name as a C string; without the terminator includes fail or resolve against garbage.
- **Includes cost.** The shader inlines the 2.2 gamma approximation instead of including `Shared.glsl`, which would pull in four more files for one `pow()`.
- **Derivatives after a discard are undefined** in non-uniform control flow; compute the normal first.
- **Reverse-Z:** depth 0 is the far plane and what empty background reads as.
- **Main viewport only,** and flight scene only: the postfix checks `__instance`, `Program.RenderedViewport` and `Program.EditorFlag`.
- **In-game validation is listed as pending** in the repository.

## See also

- Siblings: [stock vertex shader quad](compile-glsl-at-runtime-1.md), [preview shader](compile-glsl-at-runtime-3.md), [overview](overview.md)
- [Projected sticker decals](../../../../mods/gatos/features/sticker-projected-decals.md)
- [Include KSA's own GLSL library from a mod shader](../include-the-game-shader-library-in-mod-glsl/overview.md), [Build a Vulkan pipeline compatible with KSA's scene render target](../../../rendering/vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/overview.md), [Project decals onto vessels, terrain and clutter](../../../rendering/textures-and-decals/project-decals-onto-scene-geometry/overview.md)
