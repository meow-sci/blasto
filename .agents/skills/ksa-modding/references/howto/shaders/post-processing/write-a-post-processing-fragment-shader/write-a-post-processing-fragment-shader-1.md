---
type: How-To
title: "Write a post-processing fragment shader: the minimal identity-for-neutral pass (MPFX contract)"
description: Write one ShaderExtensions pass as a standalone fragment shader that returns its input at neutral parameters, plus how to read the game's camera and lighting data from set 0
category: shaders
group: post-processing
topic: write-a-post-processing-fragment-shader
approach: 1
difficulty: beginner
prerequisites: [starmap, shaderextensions, kittenextensions]
demonstrated_by: [mods/mpfx/features/effect-shader-contract, mods/shaderextensions/features/post-processing-pass-chain]
techniques: [glsl-shader, xml-asset, post-processing]
hooks:
  - xml-asset PostProcessingShader
  - harmony-transpiler KSA.Program.RenderGame (ShaderExtensions)
  - harmony-prefix KSA.Program.RenderGame (ShaderExtensions time buffer)
lifecycle: [startup:asset-load, frame:render, render:post-process]
game_types:
  - KSA.Program
  - KSA.GlobalShaderBindings
  - KSA.ShaderReference
  - KSA.Camera
  - KSA.IGameViewport
  - KSA.ViewportRegistry
tags: [glsl, post-processing, shaderextensions, mpfx, neutral-identity, global-shader-bindings]
---
This article builds a fullscreen effect as a single GLSL fragment shader that ShaderExtensions turns
into a pass. You end up with a `.frag` file, one XML element, and a shader that is safe to leave
running all the time because it returns its input unchanged when its parameters are neutral.
MPFX, an effects stack of 30 such shaders, is the worked example. It needs StarMap, KittenExtensions
Continued and ShaderExtensions installed (the three mods are normal StarMap dependencies).

## Background: how the game does it

The base game has no post-process chain. ShaderExtensions adds one by transpiling `Program.RenderGame`
and recording a list of fullscreen passes around the swapchain render pass. Each pass is a
`RenderTechnique` whose fragment stage is your file and whose vertex stage is the game's
`ScreenspaceVert`; the topic overview lists the full contract. The points that matter when you write
the shader:

- The input is a `sampler2D` at `set = 1, binding = 0`. It is the previous pass output, or the game
  image for the first pass. There is no `subpassInput` any more.
- Binding 1 of set 1 is always the `ShaderTime` block, even if you do not declare it.
- Extra bindings (your parameter block, textures) start at binding 2 of set 1, in XML document order.
- A pass cannot be switched off while the game runs, so the shader itself must be able to pass the
  image through.[^shaderextensions-readme]

## Lifecycle and timing

| When | What happens | Value |
|---|---|---|
| Game start | The asset XML is read, the `.frag` is compiled by the game's shader loader, bindings are created in `ModLibrary.Bind` | `startup:asset-load` |
| Each frame, in `Program.RenderGame` | ShaderExtensions' prefix refreshes the time buffer, then its transpiled call sites record the pre-ImGui and post-ImGui chains | `frame:render`, `render:post-process` |

Your shader therefore sees whatever the CPU wrote into its parameter block before `RenderGame`
started recording; see the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence) for where the chain sits relative to the composite and ImGui.

## Steps

1. **Create the shader file** next to the mod, for example `Shaders/BrightnessPre.frag`. This is
   MPFX's smallest complete pass, verbatim:

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

   Link: [MPFX/Shaders/BrightnessPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/BrightnessPre.frag#L1-L24). The block name does not matter; only the binding
   number and the member layout must match what the C# side writes.

2. **Make neutral parameters an identity.** A pass cannot be removed, so write every effect so
   that the "off" value returns the input. MPFX multiplies by 1, adds 0 or mixes by 0. A larger
   effect branches on a flag member, as colour balance does with `preData[3][3]`:

   ```glsl
   // MPFX/Shaders/ColorBalancePre.frag L21-L36
   void main()
   {
     vec3 c = texture(In, Uv).rgb;
     if (preData[3][3] == 0)
       Out = vec4(c, 1);
     else
     {
       c = (c + preData[0].rgb); // lift
       c = max(c, 0.0); // avoid negatives
       c = pow(c, 1.0 / preData[1].rgb); // gamma
       c *= preData[2].rgb; // gain
       c = min(c, 1.0);

       Out = vec4(c, 0.25);
     }
   }
   ```

   Source: [MPFX/Shaders/ColorBalancePre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/ColorBalancePre.frag#L21-L36). Pre-ImGui passes see HDR values above 1.0,
   so decide per operation whether to clamp (this one does).

3. **Declare the pass in the mod's asset XML** and list the XML under `assets` in `mod.toml`. One
   element per pass; `RenderPassId` orders the chain and the child element creates the parameter block
   at binding 2:

   ```xml
   <!-- MPFX/MPFXAssests.xml L24-L29 -->
   <PostProcessingShader Id="MPFXBrightnessShaderPre" Path="Shaders/BrightnessPre.frag" RenderPassId="131">
   	<MPFXDefaultBufferAsset Id="MPFXBrightnessBuffer" Size="1" />
   </PostProcessingShader>
   <GlobalPostShader Id="MPFXBrightnessShaderPost" Path="Shaders/BrightnessPost.frag" RenderPassId="131">
   	<MPFXDefaultBufferAsset Id="MPFXBrightnessBuffer" />
   </GlobalPostShader>
   ```

   Source: [MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml#L24-L29). `PostProcessingShader` runs before ImGui on the HDR scene,
   `GlobalPostShader` after ImGui on the finished frame; the XML details are in
   [Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](../declare-post-processing-shader-assets-in-xml/overview.md) and the choice between them in
   [Choose between pre-ImGui (HDR scene) and post-ImGui (final frame) post-processing](../choose-pre-or-post-imgui-post-processing/overview.md).

4. **Feed the block from C#.** The element name (`MPFXDefaultBufferAsset`) is created by a struct
   attribute in your assembly, and the values are written every frame; that is
   [Feed a ShaderExtensions shader from C# with uniform buffers (std140)](../feed-uniform-buffers-to-a-shaderextensions-shader/overview.md).

5. **Run and check the log.** A shader that still declares the removed `SubpassId` or
   `RequiresUniqueRenderpass` is skipped with an error that starts
   `[ShaderExtensions] Skipping post processing shader '<Id>'` and tells you to remove the attribute
   ([ShaderExtensions/PostProcessing/PostProcessingChain.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostProcessingChain.cs#L264-L283)). A GLSL error is reported by
   the game's shader compiler at load.

## Reading the game's camera and lighting data (set 0)

Every pass pipeline layout is `[GlobalShaderBindings.DescriptorSetLayout, <your set 1>]`, and the pass
binds the game's own set 0 for the main viewport. A fragment shader can therefore read the same
`Global` block (camera matrices, `aspectRatio`, `screenWidth`, sun and planet lighting, vessel data)
that game shaders read. The block is declared in
[current/Content/Core/Shaders/Common/Global.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/Global.glsl#L153-L160):

```glsl
// current/Content/Core/Shaders/Common/Global.glsl L153-L160
// Globals from: GlobalShaderBindings.cs
layout(set = SET_GLOBAL, binding = 0) uniform Global
{
    Camera camera;
    GlobalLighting lighting;
    Celestial celestial;
    Vessel vessel;
} global;
```

None of the reference mods' post-processing shaders reads it (MPFX never declares set 0; ImGui shaders use their own set 0), so treat this as a
supported-by-construction capability rather than a copied recipe: include `Global.glsl` the way
[Include KSA's own GLSL library from a mod shader](../../glsl-workflow/include-the-game-shader-library-in-mod-glsl/overview.md) describes and read `global.camera.*`:

```glsl
// adapted from current/Content/Core/Shaders/Common/Global.glsl (not used by MPFX)
#version 450 core
// #include the game's Common/Global.glsl via a generated include file
layout(location = 0) out vec4 Out;
layout(location = 0) in vec2 Uv;
layout(set = 1, binding = 0) uniform sampler2D In;

void main()
{
    // aspect-correct distance from the screen centre
    vec2 p = (Uv - 0.5) * vec2(global.camera.aspectRatio, 1.0);
    Out = texture(In, Uv) * (1.0 - 0.25 * dot(p, p));
}
```

Two cautions: set 0 is always the main viewport's slot (`DescriptorSet(0)`), and the data is the
frame's uploaded values, not the previous frame's. For camera work that needs C# values instead, see
[Pack parameters and camera data for a compute effect](../../compute-effects/pack-parameters-for-a-compute-effect/overview.md) and [Project world and sky positions to screen pixels and point at off-screen targets](../../../rendering/overlays/project-world-positions-to-screen/overview.md).

## Complete example

| File | Role |
|---|---|
| [MPFX/MPFXAssests.xml](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/MPFXAssests.xml) | The whole chain as XML; no render code in C# |
| [MPFX/Shaders/BrightnessPre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/BrightnessPre.frag) | Smallest pass |
| [MPFX/Shaders/ColorBalancePre.frag](https://github.com/AMPW-german/MPFX/blob/fbdeeebca9bd2e0f0001348b6903af3a652cecd0/MPFX/Shaders/ColorBalancePre.frag) | Pass with a neutral-value branch |

## Variations and gotchas

- **Pass cost never goes away.** Every supported shader records a draw every frame; neutral values
  only make the output identical. See [Send per-frame values and time to a shader with push constants, written at the right point in the frame](../send-push-constants-to-a-shaderextensions-shader/overview.md) for the
  enabled-flag pattern.
- **Alpha.** Nearly every MPFX pass writes alpha 1; `ColorBalancePre.frag` writes 0.25 in its active
  branch. The offscreen alpha is not otherwise used by the composite as far as the reviewed code shows.
- **Equal `RenderPassId`s** keep load order in practice (LINQ `OrderBy` is stable) but the README does
  not promise it. Use unique ids.
- **Only the main window is processed.** Secondary viewports and the vehicle editor (`RenderEditor`)
  are not patched.
- **Block layout.** Plain `uniform` blocks are std140 under Vulkan GLSL; a `vec4` array or `mat4` needs
  matching padding in the C# struct (covered in the uniform buffer topic).

## See also

- Siblings: [multi-binding pass and port](write-a-post-processing-fragment-shader-2.md), [RenderTarget per shader](write-a-post-processing-fragment-shader-3.md), [descriptor set per asset](write-a-post-processing-fragment-shader-4.md)
- Overview: [overview](overview.md)
- [Shader contract for chain passes](../../../../mods/mpfx/features/effect-shader-contract.md), [RenderPassId-ordered fullscreen pass chain](../../../../mods/shaderextensions/features/post-processing-pass-chain.md)
- [Declare ShaderEx, PostProcessingShader, GlobalPostShader and ImGuiShader assets](../declare-post-processing-shader-assets-in-xml/overview.md), [Feed a ShaderExtensions shader from C# with uniform buffers (std140)](../feed-uniform-buffers-to-a-shaderextensions-shader/overview.md), [Insert a custom Vulkan pass into KSA's frame](../../../rendering/vulkan-passes/insert-a-custom-pass-into-render-game/overview.md)

[^shaderextensions-readme]: ShaderExtensions/README.md
