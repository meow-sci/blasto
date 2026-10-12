---
type: Mod Feature
title: World-space textured quad (thug_life)
description: Draw a custom textured quad attached to a vessel part into KSA's offscreen scene pass by postfixing SuperMeshRenderSystem.RenderMainPass with a lazily created Vulkan pipeline built against the engine's own render target
mod: gatos
category: rendering
techniques: [harmony-postfix, vulkan-pipeline, vulkan-buffer, vulkan-texture, dynamic-harmony-install]
hooks:
  - harmony-postfix KSA.SuperMeshRenderSystem.RenderMainPass
  - starmap StarMapBeforeGui
  - starmap StarMapAfterOnFrame
lifecycle: [render:scene-opaque, frame:draw-ui, frame:after-on-frame]
game_types:
  - KSA.SuperMeshRenderSystem
  - KSA.Program
  - KSA.Rendering.RenderTarget
  - KSA.Vehicle
  - KSA.Part
  - KSA.Camera
  - KSA.ModLibrary
  - KSA.ShaderReference
  - RenderCore.RenderTechnique
tags: [vulkan, rendering, harmony, world-space, quad, pipeline, dynamic-rendering]
---

`/sim/debug/thug_life` puts the "thug life" pixel sunglasses on any part of any vessel: a flat quad
that rides the part through flight. It was ported from the unscience mod and is gatOS's first
custom GPU draw. The technique generalizes to any world-space marker, label or decal-like quad: build
a Vulkan pipeline once against KSA's offscreen target, then record a draw per frame inside the
engine's own opaque scene pass.

## How it works

**Injection point** ([gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeRenderPatches.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeRenderPatches.cs#L38-L79)): a
postfix on `SuperMeshRenderSystem.RenderMainPass(IViewport, CommandBuffer)`. `Program.RenderGame`
calls it inside the offscreen pass after planets, generic meshes, part models and statics, and
before gizmos and `_offscreenTarget.EndRendering` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4837-L4848)),
so a postfix can record extra draws into the same begun pass. The postfix binds the
`commandBuffer` parameter by name and returns at once unless the manager is `Active`.

**Pipeline** ([gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeQuadRenderer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeQuadRenderer.cs#L106-L168)):

- Shaders: the stock `UnlitMeshVert`/`UnlitMeshFrag` from `ModLibrary.Get<ShaderReference>(...)`,
  turned into stages with `RenderTechnique.CreateShaderStages`.
- One combined image sampler descriptor and a vertex-stage push constant for the MVP matrix.
- Reverse-Z depth test and write (`RenderingPresets.ReverseZDepthStencil.DepthTestWrite`), no culling,
  alpha blend.
- `Program.OffscreenTarget.SetupGraphicsPipeline(ref info)` stamps the dynamic-rendering formats,
  nulls `RenderPass` and sets the sample count from the target's MSAA state, the same call KSA's
  own mesh renderers make, so the quad follows the engine's anti-aliasing setting.

**Geometry**: one small quad per opaque texel of a built-in pixel pattern, because the stock unlit
fragment shader writes alpha 1.0; transparency is achieved by omitting geometry.

**Per-draw** ([gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeQuadRenderer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeQuadRenderer.cs#L286-L316)): compute the
model matrix in ego (camera-relative) space from `Vehicle.GetMatrixAsmb2Ego(camera)` and the anchor
part's pose, multiply by `camera.MVP.viewProjection`, bind, `Program.SetViewport(cmd)`, push
constants, `DrawIndexed`. A per-entry camera mask chooses main, crew-portrait or secondary viewports
by testing `Program.RenderedViewport`.

**Lazy lifecycle** ([gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeManager.cs#L271-L333)): the first
entry creates the texture, buffers and pipeline from `Program.GetRenderer()` and installs the patch
on its own `Harmony("gatos.thug_life")`; the last removal sets `Active = false`, unpatches, then
disposes GPU objects. Any draw exception latches the feature off with one log line.

## Lifecycle and timing

- `render:scene-opaque`: `Program.RenderGame` runs on the main thread inside `Program.OnFrame`
  (via `Render`), so the postfix, the command drain that adds entries, and the per-frame anchor
  update are all one thread and share state without locks.
- `UpdateThugLife()` runs in `DrivePerFrame` to drop entries whose vessel despawned and re-resolve
  anchor parts by `InstanceId`. `DrivePerFrame` is called from `StarMapBeforeGui` (`frame:draw-ui`),
  before rendering, or from `StarMapAfterOnFrame` on hidden-HUD frames, after that frame has
  rendered; it is not called from the main-viewport prefix, which only samples, ticks schedules,
  drains commands and drives the camera. The per-draw model matrix is computed in the postfix
  itself, so the quad tracks its part either way.
- `RenderMainPass` is also called by `RenderViewport` and `RenderEditor`, so the postfix fires once
  per rendered viewport; the camera mask decides whether to draw.

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeRenderPatches.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeRenderPatches.cs) | postfix install, remove, draw dispatch |
| [gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeQuadRenderer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeQuadRenderer.cs) | descriptor set, pipeline, geometry, per-draw recording |
| [gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeTextureFactory.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeTextureFactory.cs) | texture upload |
| [gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeManager.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeManager.cs) | registry, lazy GPU and patch, camera mask, teardown |
| [.agents/skills/ksa/quad.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/.agents/skills/ksa/quad.md) | the repo's notes on drawing a quad in KSA |

## Snippets

```csharp
// gatOS.GameMod/Game/Ksa/ThugLife/ThugLifeQuadRenderer.cs L131-L168
    private static VkPipeline BuildPipeline(DeviceEx device, Renderer renderer, VkPipelineLayout layout)
    {
        var shaderRefs = new[]
        {
            ModLibrary.Get<ShaderReference>("UnlitMeshVert"),
            ModLibrary.Get<ShaderReference>("UnlitMeshFrag"),
        };
        var stages = RenderTechnique.CreateShaderStages(device, shaderRefs.AsSpan());

        var vertexInput = new VertexInput(1, 2)
            .AddBinding(0, ByteSize.Of<QuadVertex>(), VkVertexInputRate.Vertex)
            .AddAttribute(0, 0, VkFormat.R32G32B32SFloat, ByteSize.Zero)
            .AddAttribute(1, 0, VkFormat.R32G32SFloat, ByteSize.Of<float3>())
            .Check();

        var info = new VkGraphicsPipelineCreateInfo
        {
            Layout = layout,
            StageCount = stages.Count,
            Stages = stages,
            DynamicState = renderer.DynamicStateInfo,
            ViewportState = renderer.ViewportState,
            VertexInputState = vertexInput,
            InputAssemblyState = Presets.InputAssembly.TriangleList,
            RasterizationState = Presets.Rasterization.Fill.CullNone, // double-sided
            DepthStencilState = RenderingPresets.ReverseZDepthStencil.DepthTestWrite, // offscreen pass is reverse-Z
            ColorBlendState = Presets.BlendState.BlendColorAlpha,
        };

        // CRITICAL (quad.md): bind to the OFFSCREEN scene target, NOT the swapchain/main pass — the wrong
        // one passes validation but silently breaks depth. Since rev 5154 the offscreen pass is Vulkan
        // dynamic rendering rather than a VkRenderPass, so the target stamps the pipeline itself: it fills
        // VkPipelineRenderingCreateInfo (color/depth/stencil formats), nulls RenderPass and sets
        // RasterizationSamples from its own MSAA state. This is the identical call KSA's own
        // GenericMeshRenderer/PartModelRenderer make, so the quad follows the engine's sample count.
        Program.OffscreenTarget.SetupGraphicsPipeline(ref info);
        return device.CreateGraphicsPipeline(default(VkPipelineCache), info, null);
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `SuperMeshRenderSystem.RenderMainPass(IViewport, CommandBuffer)` | [current/decomp/KSA/SuperMeshRenderSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SuperMeshRenderSystem.cs#L388-L400) | Harmony postfix target |
| `Program.OffscreenTarget` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L456-L456) | the scene render target |
| `RenderTarget.SetupGraphicsPipeline(ref VkGraphicsPipelineCreateInfo)` | [current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L356-L370) | stamp formats and samples |
| `Program.GetRenderer()`, `SetViewport`, `RenderedViewport` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L490-L557) | device access, viewport state |
| `Program.RenderGame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4646-L4660) | the frame's render recording |
| `UnlitMeshVert` / `UnlitMeshFrag` shader references | [current/Content/Core/DefaultAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/DefaultAssets.xml) | stock shaders reused |

## Pitfalls and version notes

- Bind the pipeline to the offscreen scene target, not the swapchain pass; the wrong target passes
  validation but breaks depth (repo notes).
- Rev 5154 (5168) replaced the offscreen `VkRenderPass` with dynamic rendering and deleted
  `Program.OffScreenPass`; `SetupGraphicsPipeline` is the replacement. A different class named
  `KSA.Rendering.RenderTarget` now exists, so never rebind by name alone.
- In 5482 `RenderMainPass` gained the `IViewport` parameter and in 5554 it is unchanged at
  `SuperMeshRenderSystem.cs` line 388; the postfix binds `commandBuffer` by name so the change was
  transparent.
- Teardown here disposes Vulkan objects right after unpatching without waiting for the GPU; the later
  sticker renderer adds `WaitIdle` and a retire queue, which is the safer pattern.

## See also

- [Projected sticker decals](sticker-projected-decals.md)
- [Per-vessel scale and force-render cheats](scale-and-force-render-cheats.md)
- [Screen stream frame capture](screen-stream-frame-capture.md)
- [Frame hooks with an F2-proof fallback](frame-hooks-and-f2-fallback.md)
- [gatOS overview](../overview.md)
- [A single textured quad via the same hook with its own pipeline setup](../../../howto/rendering/textures-and-decals/draw-a-textured-quad-in-the-world/draw-a-textured-quad-in-the-world-2.md) - how-to article demonstrating this feature
- [Program.OffscreenTarget.SetupGraphicsPipeline, created lazily](../../../howto/rendering/vulkan-passes/build-a-vulkan-pipeline-for-the-scene-target/build-a-vulkan-pipeline-for-the-scene-target-1.md) - how-to article demonstrating this feature
- [Postfix SuperMeshRenderSystem.RenderMainPass to draw inside the opaque scene pass](../../../howto/rendering/vulkan-passes/insert-a-custom-pass-into-render-game/insert-a-custom-pass-into-render-game-2.md) - how-to article demonstrating this feature

