---
type: Mod Feature
title: Per-window ImGui post shader
description: <ImGuiShader> re-renders one tagged ImGui window's draw list offscreen with the backend's own shaders, runs a ShaderEx fragment over its bounding rectangle, and swaps the draw list for a single AddImage of the result before the game's ImGui pass
mod: shaderextensions
category: shaders
techniques:
  - imgui-drawlist
  - vulkan-pipeline
  - vulkan-texture
  - vulkan-buffer
  - reflection
  - glsl-shader
  - harmony-transpiler
hooks:
  - harmony-transpiler KSA.Program.RenderGame
  - reflection KSA.ImGuiBackendVulkanImpl._shaderModuleVert
  - reflection KSA.ImGuiBackendVulkanImpl._shaderModuleFrag
  - reflection KSA.ImGuiBackendVulkanImpl._pipelineLayout
  - reflection KSA.ImGuiBackendVulkanImpl.UpdateTexture
  - reflection KSA.ShaderReference.Shader (private setter)
lifecycle: [frame:draw-ui, frame:render, render:imgui]
game_types:
  - KSA.ImGuiBackendVulkanImpl
  - KSA.ImGuiBackend
  - KSA.ShaderReference
  - RenderCore.RenderTechnique
  - KSA.Rendering.RenderTarget
  - Brutal.ImGuiApi.ImDrawListPtr
  - Brutal.ImGuiApi.ImDrawDataPtr
  - Brutal.ImGuiApi.ImGuiPlatformIOPtr
  - Brutal.ImGuiApi.ImDrawVert
tags: [imgui, shaders, vulkan, offscreen, draw-list, reflection]
---

`<ImGuiShader>` lets a mod run a fragment shader over one of its own ImGui windows: a CRT or glass
look for a HUD, a blur behind a panel, a heat shimmer on a gauge. The window is drawn by ImGui as
usual; ShaderExtensions intercepts its draw list after `ImGui.Render`, renders it into a texture,
runs the shader on that texture, and puts the result back where the window was.[^sx-readme]

```xml
<!-- README.md L102-L107 -->
<Assets>
  <ImGuiShader Id="MyImGuiShader">
    <Vertex Id="ImGuiVertexPost" />
    <Fragment Path="MyImGuiShader.frag" />
  </ImGuiShader>
</Assets>
```

The fragment stage is a `ShaderEx`, so it gets the window image at set 0 binding 0, the time buffer at
binding 1 and any extra bindings from 2. The vertex stage is any shader reference; the README's
`ImGuiVertexPost` provides the `In.Px`, `In.Uv`, `PxRect` and `UvRect` inputs shown in its sample
fragment shader.

## How it works

1. **Asset.** `ImGuiShaderReference` (`[KxAsset("ImGuiShader")]`) holds a `Vertex` `ShaderReference`
   and a `Fragment` `ShaderEx`, calls `OnDataLoad` on both itself and registers in a static
   `LookupCollection` keyed by `Id`
   ([ShaderExtensions/ImGuiShader/ImGuiShaderReference.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiShaderReference.cs#L8-L33)).[^sx-imguishaderref]
2. **Viewport bookkeeping.** Each frame `ImGuiRenderers.Render` marks every ImGui viewport active,
   resizes its renderers when the viewport size changed, and moves the renderers of vanished viewports
   to an orphan pool for reuse. Renderers are pooled per viewport and shader key, so two windows with
   the same shader in one viewport get two renderer instances
   ([ShaderExtensions/ImGuiShader/ImGuiRenderers.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiRenderers.cs#L28-L83),
   [ShaderExtensions/ImGuiShader/ImGuiRenderers.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiRenderers.cs#L119-L202)).[^sx-imguirenderers]
3. **Texture uploads first.** The backend normally uploads new or changed ImGui textures (font atlas)
   inside `RenderDrawData`, which has not run yet. `ImGuiRenderers` calls the backend's private
   `UpdateTexture` through a delegate for every texture whose status is not OK, so the offscreen draw
   can sample them
   ([ShaderExtensions/ImGuiShader/ImGuiRenderers.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiRenderers.cs#L23-L26),
   [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L905-L925)).[^ksa-imguibackendvulkan]
4. **Base pass: the window, alone.** `ImGuiBaseRenderer` renders into a viewport-sized
   `R8G8B8A8UNorm` `RenderTarget`, cleared each time. It borrows the backend's private
   `_shaderModuleVert`, `_shaderModuleFrag` and `_pipelineLayout`, wraps the modules in
   `ShaderReference` objects by invoking the private `Shader` setter, and builds its own pipeline with
   the ImGui vertex layout (`pos`, `uv`, `col` of `ImDrawVert`) and ImGui's alpha blending. It copies
   the list's vertices and indices into its own host-visible buffers (a ring of one pair per swapchain
   image) and replays the list's commands the way the backend does: scissor from `ClipRect`, bind the
   descriptor set stored in the texture id, `DrawIndexed`. User callbacks are skipped. While drawing,
   it computes the pixel and UV bounding box of every drawn index on the CPU
   ([ShaderExtensions/ImGuiShader/ImGuiBaseRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiBaseRenderer.cs#L21-L111),
   [ShaderExtensions/ImGuiShader/ImGuiBaseRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiBaseRenderer.cs#L150-L261)).[^sx-imguibaserenderer] [^ksa-shaderreference]
5. **Post pass: the custom shader.** `ImGuiPostRenderer` owns a second viewport-sized RGBA8 target,
   registers it as an ImGui texture with `ImGuiBackend.Vulkan.AddTexture(Program.LinearClampedSampler,
   view)`, and creates set 0 from the `ShaderEx` with binding 0 = the base target. Its pipeline layout
   has a 32-byte vertex push constant range for the two `float4` rectangles plus the shader's own
   range. `Render` clears the target, pushes `[pxBounds, uvBounds]`, binds set 0 with zero dynamic
   offsets and draws a 4-vertex strip
   ([ShaderExtensions/ImGuiShader/ImGuiPostRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiPostRenderer.cs#L43-L102),
   [ShaderExtensions/ImGuiShader/ImGuiPostRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiPostRenderer.cs#L146-L188)).[^sx-imguipostrenderer] [^ksa-imguibackend]
6. **Swap the draw list.** The list is then rewritten in place: `ResetForNewFrame`,
   `PushClipRectFullScreen`, `AddImage(ImGuiTexture, pxMin, pxMax, uvMin, uvMax)`, and
   `drawData.TotalVtxCount` / `TotalIdxCount` are corrected so the backend sizes its buffers right. The
   game's `RenderDrawData` later draws that one textured quad at the window's position in z-order
   ([ShaderExtensions/ImGuiShader/ImGuiPostRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiPostRenderer.cs#L190-L197),
   [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L21-L24),
   [current/decomp/Brutal.ImGuiApi/ImDrawDataPtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawDataPtr.cs#L19-L21)).[^brutal-imdrawlistext] [^brutal-imdrawdataptr]

This is reflection, not a Harmony patch: no `Brutal.ImGuiApi` or backend method is patched. The only
patch involved is the [RenderGame transpiler](rendergame-render-pass-transpiler.md), which provides the
slot to run in. The backend class is `KSA.ImGuiBackendVulkanImpl`, in the `KSA` assembly, not in
`Brutal.ImGuiApi`.

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Consumer tags the window | its ImGui code, between `ImGui.Begin` and `ImGui.End` | `frame:draw-ui` |
| Draw data finalized | `ImGui.Render()` in `Program.OnFrame` | `frame:imgui-render` |
| Base pass, post pass, list rewrite | `ImGuiPreRender`, in place of the swapchain `BeginRenderPass`, before the pre-ImGui chain | `frame:render` |
| Substitute quad drawn | `ImGuiBackend.Vulkan.RenderDrawData` inside the swapchain pass | `frame:render`, `render:imgui` |

The rewrite has to happen between `ImGui.Render` and the backend draw, and the offscreen passes have
to be outside the swapchain render pass; the slot before `BeginRenderPass` is the one point that meets
both ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L196-L225)).[^sx-patches]

## Key code

| Path | Role |
|---|---|
| [ShaderExtensions/ImGuiShader/ImGuiShaderReference.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiShaderReference.cs#L8-L33) | `<ImGuiShader>` asset |
| [ShaderExtensions/ImGuiShader/ImGuiRenderers.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiRenderers.cs#L14-L203) | Viewport tracking, texture uploads, dispatch, renderer pools |
| [ShaderExtensions/ImGuiShader/ImGuiBaseRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiBaseRenderer.cs#L19-L330) | Offscreen replay of one draw list with the backend's shaders |
| [ShaderExtensions/ImGuiShader/ImGuiPostRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiPostRenderer.cs#L16-L199) | Custom fragment pass and draw-list replacement |

## Snippets

```csharp
// ShaderExtensions/ImGuiShader/ImGuiBaseRenderer.cs L21-L37, L56-L61
        private static VkShaderModule? VertModule => field ??=
          (VkShaderModule)typeof(ImGuiBackendVulkanImpl).GetField(
            "_shaderModuleVert", BindingFlags.Instance | BindingFlags.NonPublic
          ).GetValue(ImGuiBackend.Vulkan);
        private static VkShaderModule? FragModule => field ??=
          (VkShaderModule)typeof(ImGuiBackendVulkanImpl).GetField(
            "_shaderModuleFrag", BindingFlags.Instance | BindingFlags.NonPublic
          ).GetValue(ImGuiBackend.Vulkan);
        private static VkPipelineLayout? BasePipelineLayout => field ??=
          (VkPipelineLayout)typeof(ImGuiBackendVulkanImpl).GetField(
            "_pipelineLayout", BindingFlags.Instance | BindingFlags.NonPublic
          ).GetValue(ImGuiBackend.Vulkan);

        private static ShaderReference[] BaseShaders => field ??= [
          MakeShader(VkShaderStageFlags.VertexBit, VertModule),
        MakeShader(VkShaderStageFlags.FragmentBit, FragModule)
        ];
// ...
        private static ShaderReference MakeShader(VkShaderStageFlags stage, VkShaderModule? module)
        {
            var shader = new ShaderReference() { Stage = stage };
            typeof(ShaderReference).GetProperty("Shader").GetSetMethod(true).Invoke(shader, [module]);
            return shader;
        }
```

```csharp
// ShaderExtensions/ImGuiShader/ImGuiPostRenderer.cs L173-L197
            Span<float4> pushConsts = [pxBounds, uvBounds];
            commandBuffer.PushConstants(
              PipelineLayout, VkShaderStageFlags.VertexBit, 0,
              MemoryMarshal.Cast<float4, byte>(pushConsts));
            shader.PushConstants(commandBuffer, PipelineLayout);

            Span<Brutal.ByteSize32> dynamicOffsets = stackalloc Brutal.ByteSize32[dynamicOffsetCount];
            dynamicOffsets.Clear();
            commandBuffer.BindDescriptorSets(VkPipelineBindPoint.Graphics, PipelineLayout, 0, [bindingSet], dynamicOffsets);

            commandBuffer.Draw(4, 1, 0, 0);

            renderTarget.EndRendering(
              commandBuffer,
              ImageBarrierInfo.Presets.SampledReadF,
              default);

            // replace draw list with image draw
            var vcount = drawList.VtxBuffer.Count;
            var icount = drawList.IdxBuffer.Count;
            drawList.ResetForNewFrame();
            drawList.PushClipRectFullScreen();
            drawList.AddImage(ImGuiTexture, pxBounds.XY, pxBounds.ZW, uvBounds.XY, uvBounds.ZW);
            drawData.TotalVtxCount -= vcount - drawList.VtxBuffer.Count;
            drawData.TotalIdxCount -= icount - drawList.IdxBuffer.Count;
```

```glsl
// README.md L111-L132
#version 450 core

layout(location = 0) out vec4 outColor;
layout(set=0, binding=0) uniform sampler2D imguiTex; // rendered ImGui Window
layout(std140, set=0, binding=1) uniform ShaderTime {
  uint FrameNumber;
  float DeltaTime;
  float RealTimeSinceStart;
  float TimeSinceStart;
  float TimeWarpSpeed;
} Time;
layout(location = 0) in struct {
  vec2 Px; // screen pixel coord
  vec2 Uv; // screen uv coord
} In;
layout(location = 4) flat in vec4 PxRect; // bounding pixel rect for window
layout(location = 8) flat in vec4 UvRect; // bounding uv rect for window

void main()
{
  outColor = textureLod(imguiTex, In.Uv, 0);
}
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `ImGuiBackendVulkanImpl._shaderModuleVert`, `_shaderModuleFrag`, `_pipelineLayout` (private) | [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L600-L608) | Reusing the stock ImGui shaders and layout |
| `ImGuiBackendVulkanImpl.UpdateTexture(ImTextureDataPtr)` (private), `ProcessTextureUpdates` (public) | [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L905-L925) | Early texture upload |
| `ImGuiBackendVulkanImpl.AddTexture`, `RemoveTexture` | [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L1119-L1131) | Registering the result as an ImGui texture |
| `ImGuiBackendVulkanImpl.RenderDrawData` | [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L689-L828) | The draw loop the base renderer mirrors |
| `ImGuiBackend.Vulkan` | [current/decomp/KSA/ImGuiBackend.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackend.cs#L23) | Backend singleton |
| `ShaderReference.Shader` (private setter) | [current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L33-L34) | Wrapping existing `VkShaderModule`s |
| `RenderTechnique.CreatePipeline`, `Dispose` | [current/decomp/RenderCore/RenderTechnique.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/RenderTechnique.cs#L59-L128) | Pipelines; `Dispose` destroys `PipelineLayout` |
| `ImDrawListExtensions.PushClipRectFullScreen`, `ResetForNewFrame`, `AddImage` | [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L21-L24), [current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L550-L611) | Draw-list replacement |
| `ImDrawDataPtr.TotalVtxCount`, `CmdLists`, `Textures` | [current/decomp/Brutal.ImGuiApi/ImDrawDataPtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawDataPtr.cs#L15-L31) | Count correction, list access, texture list |

## Pitfalls and version notes

- **`ImGuiVertexPost` is not shipped here.** Neither this repository at the pinned ref nor the game's
  `Content` defines a shader with that id (the game's stock fullscreen vertex shader is
  `ScreenspaceVert` in
  [current/Content/Core/DefaultAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/DefaultAssets.xml#L94)). A consumer
  must provide a vertex shader that turns the pushed rectangles into the README's varyings, or ship one
  under that id. KSA_AircraftHUD references `ImGuiVertexPost` too and ships only fragment shaders
  ([HudAssests.xml](https://github.com/uriahgnu/KSA_AircraftHUD/blob/4e4c71719a8355afb1fc608f54d531676bec4ef3/HudAssests.xml#L21-L26)),
  and the ShaderExtensions project has no shader files for `CopyOutput.targets` to put in its release
  folder, so none of the reference repositories defines the id at its pinned ref.[^ksa-defaultassets] [^hud-assets]
- **Not in the editor.** `RenderEditor` is not patched, so tagged windows draw unshaded there.
- **ImGui platform windows are included.** Unlike the post-processing chains, `ImGuiRenderers.Render`
  walks every entry of `ImGui.GetPlatformIO().Viewports`, so when multi-viewports are enabled a tagged
  window dragged out into its own OS window is rewritten too. `PostRender` draws those windows with
  `ImGui.RenderPlatformWindowsDefault` after the main frame is submitted, from the rewritten lists
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2345-L2349)).
  Read from the code, not tested in game.[^ksa-program]
- **Two full-viewport RGBA8 targets per tagged window,** plus a CPU loop over every index of the list
  for the bounds, plus `Device.WaitIdle()` on every viewport resize.
- **Private members.** All four backend members are private and can be renamed by any game update.
  Build 5554 also exposes a public `ProcessTextureUpdates(ImDrawDataPtr)` that would replace the
  `UpdateTexture` reflection.
- **Borrowed pipeline layout.** `ImGuiBaseRenderer` stores the backend's layout in `PipelineLayout`;
  `RenderTechnique.Dispose` destroys `PipelineLayout`, so disposing a base renderer would destroy the
  backend's layout. The code never disposes them.[^rc-rendertechnique]
- **Foreign render state.** `Renderer_RenderState` is pointed at a private struct whose layout differs
  from the backend's; harmless because callbacks are skipped in the replay.
- **Push constant overlap.** A push constant on the fragment `ShaderEx` overlaps the 32-byte vertex
  range at offset 0, see [Mod-defined push constants](push-constants.md).
- **Clipping.** The substitute image uses a full-screen clip rect; anything the shader writes outside
  the computed bounds is cropped by the UV rectangle, not by the window's clip rect.

## See also

- [Tagging an ImGui window with a draw-list marker callback](imgui-draw-list-marker-callback.md)
- [ShaderEx asset with extra descriptor bindings](shaderex-extra-bindings.md)
- [RenderGame transpiler around the swapchain render pass](rendergame-render-pass-transpiler.md)
- [Shared shader time uniform buffer](shared-time-uniform-buffer.md)
- [Bend the picture: how the host replays a tagged ImGui window offscreen and rewrites it to one image](../../../howto/shaders/screen-effects/bend-the-picture-with-crt-curvature-and-chromatic-aberration/bend-the-picture-with-crt-curvature-and-chromatic-aberration-3.md) - how-to article demonstrating this feature

[^sx-readme]: ShaderExtensions/README.md
[^sx-imguishaderref]: ShaderExtensions/ImGuiShader/ImGuiShaderReference.cs
[^sx-imguirenderers]: ShaderExtensions/ImGuiShader/ImGuiRenderers.cs
[^ksa-imguibackendvulkan]: KSA.ImGuiBackendVulkanImpl (decompiled, build 2026.10.10.5554)
[^sx-imguibaserenderer]: ShaderExtensions/ImGuiShader/ImGuiBaseRenderer.cs
[^ksa-shaderreference]: KSA.ShaderReference (decompiled, build 2026.10.10.5554)
[^sx-imguipostrenderer]: ShaderExtensions/ImGuiShader/ImGuiPostRenderer.cs
[^ksa-imguibackend]: KSA.ImGuiBackend (decompiled, build 2026.10.10.5554)
[^brutal-imdrawlistext]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
[^brutal-imdrawdataptr]: Brutal.ImGuiApi.ImDrawDataPtr (decompiled, build 2026.10.10.5554)
[^sx-patches]: ShaderExtensions/Patches.cs
[^ksa-defaultassets]: Content/Core/DefaultAssets.xml (build 2026.10.10.5554)
[^rc-rendertechnique]: RenderCore.RenderTechnique (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^hud-assets]: KSA_AircraftHUD/HudAssests.xml
