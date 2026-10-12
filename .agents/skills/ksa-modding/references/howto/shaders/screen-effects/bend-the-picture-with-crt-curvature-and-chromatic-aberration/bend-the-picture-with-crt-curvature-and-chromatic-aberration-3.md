---
type: How-To
title: "Bend the picture: how the host replays a tagged ImGui window offscreen and rewrites it to one image"
description: Follow ShaderExtensions' replay of a tagged ImGui draw list through private-member reflection, an offscreen base pass and a shader pass, and use it to debug a window that does not show
category: shaders
group: screen-effects
topic: bend-the-picture-with-crt-curvature-and-chromatic-aberration
approach: 3
difficulty: advanced
prerequisites: [starmap, shaderextensions, kittenextensions]
demonstrated_by:
  - mods/shaderextensions/features/imgui-window-post-shader
  - mods/shaderextensions/features/imgui-draw-list-marker-callback
  - mods/ksa-aircrafthud/features/imgui-window-hud-shader
techniques: [vulkan-pipeline, reflection, imgui-drawlist]
hooks:
  - harmony-transpiler KSA.Program.RenderGame (ShaderExtensions)
  - reflection KSA.ImGuiBackendVulkanImpl._shaderModuleVert
lifecycle: [frame:draw-ui, frame:render, render:imgui]
game_types:
  - KSA.ImGuiBackendVulkanImpl
  - KSA.ShaderReference
  - Brutal.ImGuiApi.ImDrawListPtr
  - Brutal.ImGuiApi.ImDrawDataPtr
  - Brutal.ImGuiApi.ImDrawCmd
  - Brutal.ImGuiApi.ImGuiPlatformIOPtr
  - KSA.Program
tags: [imgui, draw-list, offscreen, reflection, debugging, imguishader, shaderextensions, marker-callback]
---
You rarely need this code, but when a tagged window does not show, or shows unshaded, you need to know exactly what ShaderExtensions does with it. This article follows the pipeline from the marker callback to the rewritten draw list, names the game's private members it reflects into, lists the failure modes (including the vertex shader you must supply) and gives a debugging checklist. The mod-side recipe is in the
[ImGuiShader article](bend-the-picture-with-crt-curvature-and-chromatic-aberration-1.md).

## Background: how the game does it

The game's ImGui backend (`KSA.ImGuiBackendVulkanImpl`, singleton `ImGuiBackend.Vulkan`) draws all draw lists in `RenderDrawData`. It already handles a user callback command: it turns the pointer back into a delegate, calls it and resets its render state, so an unprocessed marker is a harmless no-op and the window draws normally
([current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L774-L791)). ShaderExtensions reaches into the backend with reflection, not Harmony; the only patch involved is the `RenderGame` transpiler that provides the slot ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L196-L225)).

## Lifecycle and timing

| Step | Where | Value |
|---|---|---|
| Consumer tags the window | Its ImGui code, between `ImGui.Begin` and `ImGui.End` | `frame:draw-ui` |
| Draw data finalised | `ImGui.Render()` in `Program.OnFrame` | `frame:imgui-render` |
| Base pass, post pass, list rewrite | `ImGuiPreRender`, in place of the swapchain `BeginRenderPass`, before the pre-ImGui chain | `frame:render` |
| Substitute quad drawn | `ImGuiBackend.Vulkan.RenderDrawData` inside the swapchain pass | `render:imgui` |

The rewrite must happen between `ImGui.Render` and the backend draw, and the offscreen passes must be outside the swapchain render pass; the slot before `BeginRenderPass` meets both. See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Scan for tagged lists.** `ImGuiRenderers.Render` walks every ImGui viewport (including platform windows) and every draw list; `FindCustomRenderer` iterates the list's commands, using `_CallbacksDataBuf.Count` as a budget so it stops once all callback payloads have been seen:

   ```csharp
   // ShaderExtensions/ImGuiShader/ImGuiRenderers.cs L91-L117
           private static unsafe uint? FindCustomRenderer(ImDrawListPtr drawList)
           {
               var remData = drawList._CallbacksDataBuf.Count;

               for (var i = 0; i < drawList.CmdBuffer.Count && remData > 0; i++)
               {
                   if (IsCustomRenderer(ref drawList.CmdBuffer.Data[i], out uint index))
                       return index;
                   remData -= drawList.CmdBuffer.Data[i].UserCallbackDataSize;
               }
               return null;
           }

           private static unsafe bool IsCustomRenderer(ref ImDrawCmd cmd, out uint index)
           {
               index = 0xFFFFFFFF;
               if (cmd.UserCallback == IntPtr.Zero)
                   return false;
               if (cmd.UserCallbackDataSize < MinMarkerSize)
                   return false;

               var data = *(uint2*)cmd.UserCallbackData;
               if (data[0] != MarkerHash)
                   return false;
               index = data[1];
               return true;
           }
   ```

   Source: [ShaderExtensions/ImGuiShader/ImGuiRenderers.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiRenderers.cs#L91-L117). A callback with fewer than 8 bytes of payload, or a different marker, is ignored.

2. **Upload pending ImGui textures early.** The backend normally uploads new or changed textures (the font atlas) inside `RenderDrawData`, which has not run yet, so `ImGuiRenderers` calls the backend's private `UpdateTexture` through a delegate for every texture whose status is not OK ([ShaderExtensions/ImGuiShader/ImGuiRenderers.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiRenderers.cs#L23-L26)).

3. **Replay the window alone with the stock ImGui shaders.** `ImGuiBaseRenderer` borrows the backend's private shader modules and pipeline layout by reflection, wraps them in `ShaderReference` objects via the private `Shader` setter and builds its own pipeline with the ImGui vertex layout and alpha blending into a cleared viewport-sized RGBA8 target:

   ```csharp
   // ShaderExtensions/ImGuiShader/ImGuiBaseRenderer.cs L21-L37
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
   ```

   Source: [ShaderExtensions/ImGuiShader/ImGuiBaseRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiBaseRenderer.cs#L21-L37). It copies the list's vertices and indices into host-visible buffers and replays the commands the way the backend does (scissor from `ClipRect`, descriptor set from the texture id, `DrawIndexed`); user callbacks are skipped. It computes the pixel and UV bounds of every drawn index on the CPU ([ShaderExtensions/ImGuiShader/ImGuiBaseRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiBaseRenderer.cs#L150-L261)).

4. **Run your fragment shader over the bounds, register the result, rewrite the list.** The second renderer pushes `[pxBounds, uvBounds]` for the vertex stage, binds set 0 (binding 0 = the base target) and draws a 4-vertex strip; then the draw list is reset and replaced by a single textured quad, and the draw data's totals are corrected so the backend sizes its buffers right:

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

   Source: [ShaderExtensions/ImGuiShader/ImGuiPostRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiPostRenderer.cs#L173-L197). The result target is registered as an ImGui texture with `ImGuiBackend.Vulkan.AddTexture(Program.LinearClampedSampler, view)`.

## Debugging a window that does not show

| Symptom | Likely cause |
|---|---|
| Window draws plain, no shader | The tag was not in that window's draw list (call between `Begin` and `End`), or ShaderExtensions is missing or in the editor (`RenderEditor` is not patched) |
| Window vanishes or the asset fails | Possibly the `ImGuiVertexPost` vertex shader does not exist in your install (see below; the consequence was not observed here) or the fragment shader failed to compile; check the log |
| Window shrinks to its symbols | Bounds follow the drawn vertices: add a nearly transparent full-window rectangle |
| Child windows unshaded | Only the tagged window's draw list is processed; child windows are separate lists drawn on top |
| Shader sees the wrong data | Extra bindings are set 0 from binding 2; set and push-constant ranges overlap (see below) |

## Complete example

[ShaderExtensions/ImGuiShader/ImGuiRenderers.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiRenderers.cs), [ShaderExtensions/ImGuiShader/ImGuiBaseRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiBaseRenderer.cs) and [ShaderExtensions/ImGuiShader/ImGuiPostRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiPostRenderer.cs) contain the replay; the consumer helper is in [README.md](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/README.md#L135-L169).

## Variations and gotchas

- **`ImGuiVertexPost` is not shipped.** Neither ShaderExtensions at the pinned ref nor the game's `Content` defines a shader with that id (the stock fullscreen vertex shader is `ScreenspaceVert`), and KSA_AircraftHUD references it too and ships only fragment shaders; a consumer must supply a vertex shader that turns the pushed rectangles into the README's varyings
  (`In.Px`, `In.Uv`, `PxRect`, `UvRect`), or ship one under that id.
- **Push-constant overlap.** The post renderer puts a 32-byte vertex range at offset 0, and a fragment push constant is also laid out at offset 0; Vulkan requires a push covering shared bytes to include all those ranges' stages, so validation layers will flag it and the fragment bytes can clobber the window rectangle. Prefer a uniform buffer for ImGui shader parameters.
- **Platform windows are included.** Unlike the post-processing chains, `ImGuiRenderers.Render` walks `ImGui.GetPlatformIO().Viewports`, so a tagged window dragged into its own OS window is rewritten too.
- **Private members.** `_shaderModuleVert`, `_shaderModuleFrag`, `_pipelineLayout`, `UpdateTexture` and the `Shader` setter are reached by name; a game update that renames them breaks the mod. The userdata is copied by ImGui, so a stack variable is fine, and keep delegates alive across the frame.

## See also

- Siblings: [ImGuiShader window](bend-the-picture-with-crt-curvature-and-chromatic-aberration-1.md), [glass composite](bend-the-picture-with-crt-curvature-and-chromatic-aberration-2.md), [overview](overview.md)
- [Per-window ImGui post shader](../../../../mods/shaderextensions/features/imgui-window-post-shader.md), [Tagging an ImGui window with a draw-list marker callback](../../../../mods/shaderextensions/features/imgui-draw-list-marker-callback.md), [RenderGame transpiler around the swapchain render pass](../../../../mods/shaderextensions/features/rendergame-render-pass-transpiler.md), [Curved, chromatic HUD window via an ImGuiShader](../../../../mods/ksa-aircrafthud/features/imgui-window-hud-shader.md)
- [Render ImGui UI into a Vulkan texture for use in the 3D world](../../../rendering/textures-and-decals/render-imgui-into-a-texture/overview.md), [Show a PNG or runtime-generated image in an ImGui window](../../../rendering/textures-and-decals/show-an-image-in-imgui/overview.md)
