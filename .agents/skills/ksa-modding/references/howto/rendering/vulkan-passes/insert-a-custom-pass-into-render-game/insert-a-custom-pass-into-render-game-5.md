---
type: How-To
title: Transpile Program.RenderGame around the swapchain render pass
description: Rewrite the BeginRenderPass and EndRenderPass call sites in Program.RenderGame so a mod can record dynamic-rendering passes and image copies before and after the swapchain pass
category: rendering
group: vulkan-passes
topic: insert-a-custom-pass-into-render-game
approach: 5
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/shaderextensions/features/rendergame-render-pass-transpiler
  - mods/shaderextensions/features/pre-imgui-post-processing
  - mods/shaderextensions/features/post-imgui-global-post-shaders
techniques: [harmony-transpiler, vulkan-pipeline]
hooks: [starmap StarMapBeforeMain, harmony-transpiler KSA.Program.RenderGame]
lifecycle: [startup:starmap-before-main, frame:render, render:post-process, render:imgui]
game_types: [KSA.Program, KSA.ImGuiBackendVulkanImpl, KSA.Rendering.RenderTarget]
tags: [harmony, transpiler, swapchain, render-pass, post-process, imgui]
---

Outcome: your code runs immediately before the swapchain render pass opens and immediately after it closes. That
matters because dynamic-rendering scopes (`vkCmdBeginRendering`) and image copies are illegal inside a render pass
instance, and the tonemap composite and the ImGui draw both happen inside the single legacy swapchain pass. No method
boundary exists at either point, so ShaderExtensions rewrites the IL of `Program.RenderGame`. Demonstrated by
[RenderGame transpiler around the swapchain render pass](../../../../mods/shaderextensions/features/rendergame-render-pass-transpiler.md) and its two consumers,
[Pre-ImGui post-processing on the HDR scene image](../../../../mods/shaderextensions/features/pre-imgui-post-processing.md) and [Post-ImGui global post shaders on the swapchain image](../../../../mods/shaderextensions/features/post-imgui-global-post-shaders.md).
Requires StarMap and Harmony; this is the most fragile of the five hooks, so use it only when the earlier ones cannot reach
the stage you need.

## Background: how the game does it

Steps 1 to 22 of `RenderGame` use dynamic rendering or compute on offscreen targets. Step 23 begins the swapchain render
pass, step 24 records `RenderFinalComposite` (tonemap), step 25 `ImGuiBackend.Vulkan.RenderDrawData`, and step 26 ends the pass:

```csharp
// current/decomp/KSA/Program.cs L4945-L4954
commandBuffer2.PipelineBarrier2(_offscreenTarget.ColorImage, ImageBarrierInfo.Presets.SampledReadVfc);
_screenshotCapture.OnRenderGameCapture(commandBuffer2, resourceFrameIndex, in _compositePushConstantData);
RenderAaPreparePasses(commandBuffer2, shaderSlot, resourceFrameIndex);
commandBuffer2.BeginRenderPass(in pRenderPassBegin, contents);
RenderFinalComposite(commandBuffer2, shaderSlot, resourceFrameIndex);
ImGuiBackend.Vulkan.RenderDrawData(commandBuffer2);
commandBuffer2.EndRenderPass();
_screenshotCapture.OnRenderGameSwapchainGrab(commandBuffer2, frameResources.ColorImage);
Profiler.Gpu.EndFrame(commandBuffer2);
commandBuffer2.End();
```

There is exactly one `RenderDrawData` call in `RenderGame`; the editor path `RenderEditor` has its own copy
[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5071-L5075). `RenderTarget` scopes use `vkCmdBeginRendering`
[current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L201-L234), which is why a prefix on `RenderFinalComposite` would already be inside
the render pass.

## Lifecycle and timing

| Slot | Runs | Contents in ShaderExtensions |
|---|---|---|
| Install | `startup:starmap-before-main`: `new Harmony("ShaderExtensions").PatchAll()` in `[StarMapBeforeMain]` | Before the game's entry point, so a failed anchor aborts launch |
| Before `BeginRenderPass` (step 23) | `frame:render`, after `RenderAaPreparePasses` and composite-mode screenshot capture | Per-window ImGui shaders, then the pre-ImGui chain |
| After `EndRenderPass` (step 26) | `frame:render`, before the swapchain screenshot grab and `End` | Post-ImGui chain |

`render:post-process` in this bundle means the composite at step 24 plus whatever a mod inserts here; the base game has no
generic post-process chain. See the notes for render mods in [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. Patch `Program.RenderGame` with a transpiler. Find `ImGuiBackendVulkanImpl.RenderDrawData`, walk backwards to the
   nearest `BeginRenderPass`, remove it, and insert a call to your method that takes the same three stack values
   (command buffer, `in VkRenderPassBeginInfo`, `VkSubpassContents`). Then, from the end, find the last `EndRenderPass`, step past
   it and insert your after-pass call.

   ```csharp
   // ShaderExtensions/Patches.cs L151-L193
   [HarmonyPatch(typeof(Program), "RenderGame"), HarmonyTranspiler, HarmonyDebug]
   internal static IEnumerable<CodeInstruction> Program_RenderGame_Transpile(
     IEnumerable<CodeInstruction> instructions)
   {
       var matcher = new CodeMatcher(instructions);

       matcher.MatchStartForward(
         CodeMatch.Calls(() => default(ImGuiBackendVulkanImpl).RenderDrawData(default)));
       matcher.ThrowIfInvalid("could not find ImGuiBackendVulkanImpl.RenderDrawData call in Program.RenderGame");

       matcher.MatchStartBackwards(
         CodeMatch.Calls(() => default(CommandBuffer).BeginRenderPass(default, default)));
       matcher.ThrowIfInvalid("could not find CommandBuffer.BeginRenderPass call in Program.RenderGame");

       matcher.RemoveInstruction();
       matcher.InsertAndAdvance(CodeInstruction.Call(() => ImGuiPreRender(default, default, default)));

       matcher.End();

       MethodInfo EndRenderPassMethod =
           AccessTools.Method(
               typeof(Brutal.VulkanApi.VkDeviceExtensions),
               "EndRenderPass"
           );

       matcher.MatchStartBackwards(
           new CodeMatch(
               instr => instr.opcode == OpCodes.Call &&
                        instr.operand is MethodInfo mi &&
                        mi.Name == "EndRenderPass"
           )
       );
       matcher.ThrowIfInvalid("EndRenderPass call not found");

       matcher.Advance(1);
       matcher.InsertAndAdvance(
           new CodeInstruction(OpCodes.Ldloc_1), // commandBuffer2
           new CodeInstruction(OpCodes.Ldloc_0), // frameResources
           new CodeInstruction(OpCodes.Ldc_I4_0),
           new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GlobalPostShaderHandler), nameof(GlobalPostShaderHandler.RenderNow)))
       );

       return matcher.Instructions();
   ```

2. In your before-method do your recording, then call `BeginRenderPass` yourself with the arguments you received.

   ```csharp
   // ShaderExtensions/Patches.cs L196-L225
   private static bool rebuilt = false;
   private static unsafe void ImGuiPreRender(
     CommandBuffer commandBuffer, in VkRenderPassBeginInfo beginInfo, VkSubpassContents contents)
   {
       Renderer renderer = Program.GetRenderer();

       if (!rebuilt)
       {
           Program.ScheduleRendererRebuild();
           rebuilt = true;
       }

       bool needsRebuild =
           GlobalPostShaderHandler.SourceTarget == null ||
           GlobalPostShaderHandler.SourceTarget.Extent.Width != renderer.Extent.Width ||
           GlobalPostShaderHandler.SourceTarget.Extent.Height != renderer.Extent.Height;

       if (needsRebuild)
       {
           PostProcessingHandler.Rebuild();
           GlobalPostShaderHandler.Rebuild();
       }

       ImGuiRenderers.Render(renderer, commandBuffer);

       // Post process the game image before the UI pass composites it onto the swapchain.
       PostProcessingHandler.RenderNow(commandBuffer);

       commandBuffer.BeginRenderPass(in beginInfo, contents);
   }
   ```

3. Apply it early. ShaderExtensions calls `PatchAll()` from `[StarMapBeforeMain]`, so the transpiler is in place before
   the first frame is recorded and an anchor failure stops the launch instead of surfacing mid-game. Harmony can also
   patch `RenderGame` later (for example at `[StarMapAllModsLoaded]`), but frames recorded before that run unpatched.

4. Verify at startup: if an anchor is missing, `ThrowIfInvalid` throws during `PatchAll` and the launch aborts rather than running the
   game unpatched. A shifted local index (the hard-coded `ldloc.0` / `ldloc.1`) is not detected at all.

## Complete example

| File | Role |
|---|---|
| [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L151-L225) | Transpiler and `ImGuiPreRender` |
| [ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs#L14-L46) | Pre-ImGui consumer: chain on the offscreen colour image |
| [ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderHandler.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderHandler.cs#L51-L71) | Post-ImGui consumer: copy swapchain image in, run, copy back |

## Variations and gotchas

- **Hard-coded locals.** `ldloc.0` and `ldloc.1` are right only while the compiler keeps `FrameResources` and `CommandBuffer` as the first two locals;
  capture the operands from the matched instructions instead.
- **"Last `EndRenderPass`" anchor.** Any new `EndRenderPass` added later to `RenderGame` moves the insertion point.
- **Only one mod can use this anchor.** After the transpiler runs, `RenderGame` has no `BeginRenderPass`, so a second transpiler that searches for it throws.
- **CMAA2.** With CMAA2 selected, step 22 has already composited the offscreen image into CMAA2's LDR target and step 24 only blits that target, so edits to the offscreen
  colour made at step 23 are not shown. With MSAA (the default) or no anti-aliasing they are.
- **Layout after copy-back.** The pre-ImGui chain leaves the offscreen image in `TRANSFER_DST_OPTIMAL`; add a `SampledReadF` barrier after the copy to be spec-correct.
- **Editor path unpatched.** `RenderEditor` has its own four calls.
- **Platform windows.** ImGui multi-viewport windows are rendered after submit and are not post-processed.

## See also

- [Overview](overview.md)
- [Approach 1: ResolveAttachments postfix](insert-a-custom-pass-into-render-game-1.md)
- [Approach 4: SunbloomRenderer.Render prefix](insert-a-custom-pass-into-render-game-4.md)
- [RenderGame transpiler around the swapchain render pass](../../../../mods/shaderextensions/features/rendergame-render-pass-transpiler.md), [Pre-ImGui post-processing on the HDR scene image](../../../../mods/shaderextensions/features/pre-imgui-post-processing.md), [Post-ImGui global post shaders on the swapchain image](../../../../mods/shaderextensions/features/post-imgui-global-post-shaders.md)
- [Capture rendered frames from KSA](../capture-rendered-frames/overview.md)
