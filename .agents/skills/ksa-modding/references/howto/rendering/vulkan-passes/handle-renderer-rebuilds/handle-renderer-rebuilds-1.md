---
type: How-To
title: Postfix Program.RebuildRenderer plus a first-frame schedule and per-frame extent check
description: Rebuild extent-bound mod targets right after the game rebuilds its renderer, force one rebuild after startup, and add a per-frame extent check as a safety net
category: rendering
group: vulkan-passes
topic: handle-renderer-rebuilds
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/shaderextensions/features/renderer-rebuild-hook]
techniques: [harmony-postfix, vulkan-pipeline]
hooks: [harmony-postfix KSA.Program.RebuildRenderer]
lifecycle: [frame:prepare, frame:render, frame:post-render]
game_types: [KSA.Program, Core.Renderer, KSA.Rendering.RenderTarget, KSA.ScreenshotCapture]
tags: [harmony, rebuild, resize, extent-override, screenshot, swapchain]
---

Outcome: images, views and descriptor sets your mod keeps against the game's render targets are rebuilt at the same point the game rebuilds them, so
nothing samples a destroyed view after a window resize, a present-mode or MSAA change, or a high-resolution screenshot. Demonstrated by
ShaderExtensions' post-processing chains ([Rebuilding mod GPU resources with the game renderer](../../../../mods/shaderextensions/features/renderer-rebuild-hook.md)). Requires StarMap and Harmony.

## Background: how the game does it

`Program.RebuildRenderer(bool skipSwapchain = false)` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5105-L5145) rebuilds the swapchain (unless skipped), calls `Device.WaitIdle()`, resizes the main viewport,
rebuilds `_offscreenTarget` through `BuildRenderTargets`, then every renderer that holds views onto it, and re-creates the composite descriptor on the new colour view. A mod object built
before that would sample a destroyed view.

```csharp
// current/decomp/KSA/Program.cs L5105-L5124
public void RebuildRenderer(bool skipSwapchain = false)
{
	bool flag = _renderer.ConsumeSwapchainCurrent(GameSettings.GetPresentMode());
	if (!skipSwapchain && !flag)
	{
		_renderer.Rebuild(GameSettings.GetPresentMode());
	}
	_renderer.Device.WaitIdle();
	_refractionColorCopyTarget.Dispose();
	((IViewportLifecycle)MainViewport).ApplyResize(new int2(_renderer.Extent.Width, _renderer.Extent.Height), GameSettings.GetSampleCount());
	_mainPass.Pass = _renderer.MainRenderPass;
	ImGuiBackend.Vulkan.UpdateMainRenderPass(_renderer.MainRenderPass);
	_computeCommandBufferPool = _renderer.ComputeCommandBufferPool;
	BuildRenderTargets();
	_transparenciesMsaaResolve.RebuildResources(_offscreenTarget);
	PrePassRenderer.Rebuild();
	UiCoverageMask.RebuildDepthStampPipeline(PrePassRenderer);
	AmbientOcclusionRenderer.Rebuild(PrePassRenderer.OpaquePrePassData.DepthTextureForSampling.Texture, PrePassRenderer.OpaquePrePassData.NormalTextureForSampling.Texture);
	SuperMeshRenderSystem.PassContext.PrePass = PrePassRenderer.OpaquePrePassData.Target;
	SuperMeshRenderSystem.Rebuild(_renderer);
```

`Renderer.ExtentOverride` [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L42) is a nullable extent; `Renderer.Extent` returns it when set [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L96), and `ScreenshotCapture` sets it for a high-resolution
capture [current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L237) and clears it afterwards. Code that sizes targets from `Renderer.Extent` therefore follows the capture size, not the swapchain.

## Lifecycle and timing

`RebuildRenderer` has several callers, so the postfix runs in more than one phase:

| Caller | Condition | Lifecycle |
|---|---|---|
| `Program.PrepareFrame` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2143-L2148) | `RendererRebuildNeeded` at frame start | `frame:prepare` |
| `Program.Render` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2445-L2449) | swapchain acquire failed or a rebuild is pending; the frame is skipped | `frame:render` |
| `Program.PostRender` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2351-L2357) | present reported `NeedsResize`, or a rebuild was requested during the frame | `frame:post-render` |
| `ScreenshotCapture` [current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L239) [current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L266) | arming or aborting a capture with scale above 1 | `frame:prepare` |

`Program.ScheduleRendererRebuild()` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5248-L5251) sets `RendererRebuildNeeded`, which the next `PrepareFrame` or `PostRender` acts on. See
[the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for the frame phases.

## Steps

1. Postfix `Program.RebuildRenderer`. Rebuild your targets in dependency order (a later chain may reference an earlier chain's images). Each rebuild calls `Device.WaitIdle()` first, then
   disposes the old objects and builds new ones.

   ```csharp
   // ShaderExtensions/Patches.cs L227-L236
   [HarmonyPatch(typeof(Program), nameof(Program.RebuildRenderer)), HarmonyPostfix]
   internal static void Program_RebuildRenderer_Postfix()
   {
       //ImGuiRenderers.RebuildAll();
       if (!SharedTimeBuffer.IsInitialized)
           SharedTimeBuffer.Initialize(Program.GetRenderer());

       PostProcessingHandler.Rebuild();
       GlobalPostShaderHandler.Rebuild();
   }
   ```

2. Size from `Renderer.Extent`, and recreate your source target only when the extent actually changed:

   ```csharp
   // ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderHandler.cs L20-L49
   public static void Rebuild()
   {
       Renderer renderer = Program.GetRenderer();
       renderer.Device.WaitIdle();

       EnsureSourceTarget(renderer);

       Chain.Build(
           "ShaderExtensions_PostImgui",
           PostProcessingChain.FilterSupported(GlobalPostShaderAsset.AllShaders),
           SourceTarget.ColorImage);
   }

   private static void EnsureSourceTarget(Renderer renderer)
   {
       if (SourceTarget is not null &&
           SourceTarget.Extent.Width == renderer.Extent.Width &&
           SourceTarget.Extent.Height == renderer.Extent.Height)
       {
           return;
       }

       SourceTarget?.Dispose();
       SourceTarget = new RenderTarget(
           renderer,
           "ShaderExtensions_PostImguiSource",
           renderer.Extent,
           renderer.ColorFormat,
           VkFormat.Undefined);
   }
   ```

3. Force one rebuild after startup so your objects are built against fully initialised game targets: on your first render call, `Program.ScheduleRendererRebuild()`. The same frame's `PostRender`
   sees the flag and calls `RebuildRenderer`, which runs your postfix.

4. Add a per-frame safety net: where your code runs each frame (ShaderExtensions does it in the method it substitutes for `BeginRenderPass`), compare your source target's extent with
   `Renderer.Extent` and rebuild inline if it is missing or differs. An inline rebuild in `frame:render` waits only for earlier frames; the current command buffer is not yet submitted.

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

5. If a target follows the ImGui viewport size instead (ShaderExtensions' per-window shaders), rebuild it where the size is observed, not from the postfix.

## Complete example

| File | Role |
|---|---|
| [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L196-L236) | Postfix, forced first rebuild, per-frame check |
| [ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderHandler.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderHandler.cs) | Source target and chain rebuild |
| [ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs) | Pre-ImGui chain that re-reads the game's offscreen target |

## Variations and gotchas

- **Stalls.** Every rebuild path calls `Device.WaitIdle()`, and a resize drag can trigger several in a row.
- **Honour the extent override.** During a high-resolution capture `Renderer.Extent` returns the override, so chains are sized to the capture.
- **No shutdown path.** Nothing disposes the chains at game exit. `[StarMapUnload]` runs after the renderer is gone; do not call device functions there
  ([free mod-owned Vulkan resources safely](../free-vulkan-resources-safely/overview.md)).
- **One extra rebuild at startup** from the forced request.
- **Pipelines built against the scene target** keep their sample count; rebuild them too when the MSAA setting changes (see
  [build a Vulkan pipeline for the scene target](../build-a-vulkan-pipeline-for-the-scene-target/overview.md)).

## See also

- [Overview](overview.md)
- [Approach 2: rebuild in place on a live size change](handle-renderer-rebuilds-2.md)
- [Approach 3: re-create the private material pipeline](handle-renderer-rebuilds-3.md)
- [Rebuilding mod GPU resources with the game renderer](../../../../mods/shaderextensions/features/renderer-rebuild-hook.md)
- [Capture rendered frames from KSA](../capture-rendered-frames/overview.md)
