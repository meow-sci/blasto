---
type: Mod Feature
title: World-space textured quad pipeline with a runtime-compiled shader
description: Build a dynamic-rendering Vulkan pipeline that draws a textured quad into KSA's scene target, reusing the game's UnlitMeshVert vertex shader, compiling a premultiplied-alpha fragment shader at runtime with the bundled shaderc, depth-testing against reverse-Z without writing depth
mod: purrtty
category: rendering
techniques: [vulkan-pipeline, vulkan-buffer, glsl-shader, vulkan-texture]
hooks: [starmap StarMapAfterGui, harmony-postfix KSA.SuperMeshRenderSystem.RenderTranslucencyPass]
lifecycle: [frame:draw-ui, render:translucency]
game_types:
  - RenderCore.ShaderModuleUtils
  - KSA.ModLibrary
  - KSA.ShaderReference
  - KSA.RenderingPresets
  - Brutal.VulkanApi.Abstractions.Presets
  - KSA.Program
  - KSA.Rendering.RenderTarget
  - KSA.Camera
  - Core.Renderer
tags: [vulkan, pipeline, glsl, shaderc, premultiplied-alpha, reverse-z, in-world, quad]
---

The in-world terminal texture is shown on a flat quad in the game world, blended over the scene so
the planet and the vehicle show through wherever the terminal theme is translucent. purrTTY builds
the pipeline itself with Brutal's Vulkan wrappers, but borrows as much as it can from KSA: the
stock unlit-mesh vertex shader, the scene target's formats and sample count, the dynamic viewport
state, reverse-Z depth presets, and the bundled shaderc compiler for its one custom fragment shader.

## How it works

**Shared, built once** (`SharedQuadResource`, created by the coordinator on the first in-world
terminal):[^shared-quad]

| Piece | How |
|---|---|
| Vertex shader | `ModLibrary.Get<ShaderReference>("UnlitMeshVert")`, the game's `Shaders/Mesh/UnlitMesh.vert` asset (inputs `vec3` pos at 0, `vec2` uv at 1; push constant `mat4 worldViewProjMatrix`; outputs uv at 0). Owned by `ModLibrary`, never destroyed by the mod[^ksa-default-assets][^ksa-unlit-vert] |
| Fragment shader | `RenderCore.ShaderModuleUtils.FromString(device, glslUtf8, FragmentBit, options: null, debugName)`; requires a build reference to `Brutal.ShaderC` (game-provided). Owned by the mod and destroyed with the pipelines[^ksa-shadermodule][^gamemod-csproj] |
| Descriptor set layout | One combined image sampler at binding 0, fragment stage |
| Pipeline layout | That layout plus a vertex-stage push constant of `sizeof(float4x4)` |
| Vertex input | One binding, stride 20 (`float3` pos, `float2` uv) |
| Pipeline target | `VkPipelineRenderingCreateInfo` chained in `Next` (dynamic rendering, no render pass): color format and depth format from `Program.OffscreenTarget.ColorAttachment/DepthAttachment`, `RasterizationSamples = Program.OffscreenTarget.Samples` |
| Fixed state | `renderer.DynamicStateInfo`, `renderer.ViewportState`, `Presets.InputAssembly.TriangleList`, `Presets.Rasterization.Fill.CullNone` (visible from both sides) |
| Blend | Premultiplied alpha built inline: color and alpha `One` / `OneMinusSrcAlpha` |
| Depth | Two pipelines: `RenderingPresets.ReverseZDepthStencil.DepthTestNoWrite` (compare `GreaterOrEqual`) and `NoDepthTest` (billboard "always on top")[^ksa-rendering-presets] |
| Geometry | Unit quad (4 vertices, 6 `ushort` indices, V flipped) in device-local buffers uploaded once through a staging pool |

**Why a custom fragment shader.** The stock `UnlitMesh.frag` gamma-decodes the texel and forces
alpha to 1.[^ksa-unlit-frag] The off-screen terminal texture is premultiplied (KSA's ImGui backend
blends over a transparent clear), so purrTTY's shader un-premultiplies, applies the same
`pow(2.2)` decode, then re-premultiplies. Skipping the un-premultiply makes a 50% background render
at about 21%; straight-alpha blending leaves dark fringes around glyphs (gotcha 28).[^gotchas]
With every opacity at 1 the result equals the old opaque quad.

**Per instance** (`InWorldQuad`): its own one-set descriptor pool and set, written with the
target's color view, `ShaderReadOnlyOptimal` layout and sampler (`RebindTarget`, also used after a
live resize). `RecordDraw` computes a model matrix (see [anchoring](./anchor-quad-to-part-or-camera.md)),
multiplies by `camera.MVP.viewProjection` (part mode, ego space) or `camera.MVP.projection`
(billboard, view space) using row-vector order, then binds pipeline and set, calls
`Program.SetViewport(cmd)`, pushes the MVP, binds the buffers and `DrawIndexed(6)`.[^inworld-quad][^ksa-camera]

## Lifecycle and timing

- Pipelines and buffers are created on the main thread during `frame:draw-ui` when the first
  instance is created (`InWorldTerminalManager.Create`, from a dialog drawn in `StarMapAfterGui`).
  The renderer exists from the `Program` constructor, but `Program.OffscreenTarget`, whose formats
  and sample count the pipeline copies, is created only after `ModLibrary.LoadAll` returns, so it is
  not available yet at `StarMapAllModsLoaded`.[^ksa-program]
- Draw recording happens in `render:translucency`: the Harmony postfix on
  `SuperMeshRenderSystem.RenderTranslucencyPass` opens a dynamic-rendering scope over the scene's
  color and depth attachments and calls `RecordDraw` for each instance (see
  [the postfix](./draw-after-translucency-pass-postfix.md)). That code runs on the main thread inside
  `Program.RenderGame` (and once more per visible secondary viewport, through `RenderViewport`).
  The texture it samples was last re-rendered in a `StarMapAfterGui` call: this frame's if the
  terminal content changed and the HUD is shown, otherwise an earlier one.
- Part-mode quads depth-test against everything opaque already in the depth buffer but write no
  depth, so later translucent passes and other quads are not occluded by them.

## Key code

| Path | Role |
|---|---|
| [purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs#L61-L106) | The GLSL and its runtime compile, the borrowed vertex shader |
| [purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs#L108-L247) | Layouts, dynamic-rendering pipeline info, blend, two depth variants |
| [purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs#L249-L286) | Unit quad buffers via staging upload |
| [purrTTY.GameMod/InWorld/Display/InWorldQuad.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/Display/InWorldQuad.cs#L66-L172) | Per-instance descriptor set and draw recording |
| [purrTTY.Display/Ghostty/InWorldTerminalRenderer.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/InWorldTerminalRenderer.cs#L237-L257) | Background painted at `BackgroundOpacity` into the texture |
| [purrTTY.GameMod/purrTTY.GameMod.csproj](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/purrTTY.GameMod.csproj#L182-L188) | `Brutal.ShaderC` reference for runtime compile |

## Snippets

```csharp
// purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs L68-L106
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

## Game APIs touched

| API | Where | Purpose |
|---|---|---|
| `ShaderModuleUtils.FromString(Device, ReadOnlySpan<byte>, VkShaderStageFlags, CompileOptions?, ReadOnlySpan<byte>)` | [current/decomp/RenderCore/ShaderModuleUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ShaderModuleUtils.cs#L79) | GLSL to `VkShaderModule` at runtime |
| `ModLibrary.Get<ShaderReference>("UnlitMeshVert")` | [current/Content/Core/DefaultAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/DefaultAssets.xml#L53) | Stock vertex shader asset id |
| `RenderingPresets.ReverseZDepthStencil.DepthTestNoWrite` / `NoDepthTest` | [current/decomp/KSA/RenderingPresets.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RenderingPresets.cs#L32-L63) | Reverse-Z depth states |
| `Program.OffscreenTarget` (`ColorAttachment`, `DepthAttachment`, `Samples`) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L456) | Formats and MSAA the pipeline must match |
| `Program.SetViewport(CommandBuffer)`, `Program.GetMainCamera()` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4448) | Dynamic viewport and camera |
| `Camera.MVP` (`viewProjection`, `projection`) | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L63-L65) | Matrices for the push constant |

## Pitfalls and version notes

- Inside a dynamic-rendering scope a pipeline must be created with `VkPipelineRenderingCreateInfo`;
  one built for a classic render pass is invalid there. Since KSA rev 5154 (build 2026.8.5.5168) the
  scene uses dynamic rendering.
- Source formats and sample count from the target you draw into; the main scene is multisampled
  when MSAA is on, and `ColorAttachment`/`DepthAttachment` already resolve to the MSAA images.
- KSA uses reverse-Z depth (`GreaterOrEqual` compare in its presets); do not use generic `Less` presets.
- Do not destroy shader modules you got from `ModLibrary`; do destroy the ones you compiled.
- If KSA changes `UnlitMesh.vert`'s inputs or push constant, the quad breaks silently; the
  `upgrade-ksa` skill lists this coupling.
- A source comment in `InWorldQuad.TryComputeModel` says part mode "always depth-writes"; the pipeline
  it uses is `DepthTestNoWrite` (the comment predates the translucency change).
- `SharedQuadResource` is built once per coordinator and never rebuilt. `Program.RebuildRenderer`
  re-creates the scene target with the current `GameSettings.GetSampleCount()` and rebuilds KSA's
  own pipelines, so changing the anti-aliasing sample count while an in-world terminal exists leaves
  the quad pipeline with a stale `RasterizationSamples` (inferred from the code, not tested). A mod
  copying this should rebuild its pipeline when the target's `Samples` changes.[^ksa-program]

## See also

- [Render-to-texture with a second ImGui context](./offscreen-imgui-context-render-to-texture.md)
- [Draw after the translucency pass](./draw-after-translucency-pass-postfix.md)
- [Anchor the quad to a part or the camera](./anchor-quad-to-part-or-camera.md)
- [Vulkan resource teardown discipline](./vulkan-resource-teardown-discipline.md)
- [purrTTY overview](../overview.md)
- [Premultiplied-alpha translucent quad after the translucency pass](../../../howto/rendering/textures-and-decals/draw-a-textured-quad-in-the-world/draw-a-textured-quad-in-the-world-3.md) - how-to article demonstrating this feature
- [ImGui rendered to a texture and drawn as a world-space quad](../../../howto/rendering/textures-and-decals/render-imgui-into-a-texture/render-imgui-into-a-texture-3.md) - how-to article demonstrating this feature
- [Hand-built dynamic-rendering pipeline reusing UnlitMeshVert with premultiplied alpha](../../../howto/rendering/vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/build-a-vulkan-pipeline-for-the-scene-target-2.md) - how-to article demonstrating this feature
- [Compile GLSL at runtime: a premultiplied-alpha fragment shader paired with the stock unlit vertex shader](../../../howto/shaders/glsl-workflow/compile-glsl-at-runtime/compile-glsl-at-runtime-1.md) - how-to article demonstrating this feature

[^shared-quad]: purrTTY.GameMod/InWorld/Display/SharedQuadResource.cs
[^ksa-default-assets]: KSA Content/Core/DefaultAssets.xml (build 2026.10.10.5554)
[^ksa-unlit-vert]: KSA Content/Core/Shaders/Mesh/UnlitMesh.vert (build 2026.10.10.5554)
[^ksa-shadermodule]: RenderCore.ShaderModuleUtils (decompiled, build 2026.10.10.5554)
[^gamemod-csproj]: purrTTY.GameMod/purrTTY.GameMod.csproj
[^ksa-rendering-presets]: KSA.RenderingPresets (decompiled, build 2026.10.10.5554)
[^ksa-unlit-frag]: KSA Content/Core/Shaders/Mesh/UnlitMesh.frag (build 2026.10.10.5554)
[^gotchas]: purrTTY docs/gotchas.md
[^inworld-quad]: purrTTY.GameMod/InWorld/Display/InWorldQuad.cs
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
