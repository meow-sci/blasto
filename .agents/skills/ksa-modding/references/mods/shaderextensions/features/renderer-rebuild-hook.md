---
type: Mod Feature
title: Rebuilding mod GPU resources with the game renderer
description: A Program.RebuildRenderer postfix, a forced renderer rebuild on the first rendered frame and a per-frame extent check keep the post-processing chains, their render targets and the shared time buffer in step with swapchain resizes and graphics-setting changes
mod: shaderextensions
category: rendering
techniques: [harmony-postfix, harmony-transpiler, reflection, vulkan-texture]
hooks: [harmony-postfix KSA.Program.RebuildRenderer, harmony-transpiler KSA.Program.RenderGame]
lifecycle: [frame:prepare, frame:render, frame:post-render]
game_types: [KSA.Program, Core.Renderer, KSA.ScreenshotCapture, KSA.Rendering.RenderTarget]
tags: [rendering, resize, swapchain, harmony, lifecycle, vulkan]
---

Every image view a chain samples belongs to a render target that the game recreates when it rebuilds
its renderer: window resize, present-mode or MSAA changes, high-resolution screenshots. ShaderExtensions
rebuilds its own chains right after the game finishes, and adds two safety nets for cases the postfix
does not cover.[^sx-patches]

## How it works

1. **Postfix on `Program.RebuildRenderer`.** Initializes the time buffer if needed, then rebuilds the
   pre-ImGui chain and the post-ImGui chain, in that order (the post chain may reference pre-chain
   images) ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L227-L236)).
2. **What the game rebuilt.** `RebuildRenderer` waits for the device, resizes the main viewport,
   rebuilds `_offscreenTarget` through `BuildRenderTargets`, and re-creates the composite descriptor on
   the new offscreen image view
   ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5105-L5141)). A chain built before
   that would sample a destroyed view.[^ksa-program]
3. **Each chain rebuild** calls `Device.WaitIdle()`, then `Build`, which disposes the old passes and
   targets. The pre-ImGui handler re-reads the private `_offscreenTarget`; the post-ImGui handler
   recreates its source target only when `Renderer.Extent` changed
   ([ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs#L22-L33),
   [ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderHandler.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderHandler.cs#L20-L49)).[^sx-prehandler] [^sx-posthandler]
4. **Forced first rebuild.** The first time `ImGuiPreRender` runs it calls
   `Program.ScheduleRendererRebuild()`, which sets `RendererRebuildNeeded`. `PostRender` of the same
   frame sees the flag and calls `RebuildRenderer`, so the postfix builds the chains against fully
   initialised game targets
   ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L196-L206),
   [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5248-L5251),
   [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2351-L2358)).
5. **Per-frame extent check.** Every frame `ImGuiPreRender` compares the post-ImGui source target with
   `Renderer.Extent` and rebuilds both chains inline if it is missing or differs
   ([ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L208-L217)).
6. **ImGui shader targets** follow the ImGui viewport size instead: `ViewportRenderer.EnsureSize`
   rebuilds every renderer of a viewport whose size changed, each with its own `WaitIdle`
   ([ShaderExtensions/ImGuiShader/ImGuiRenderers.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiRenderers.cs#L134-L146),
   [ShaderExtensions/ImGuiShader/ImGuiBaseRenderer.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiBaseRenderer.cs#L113-L119)).[^sx-imguirenderers] [^sx-imguibaserenderer]

## Lifecycle and timing

`Program.RebuildRenderer` is called from several places, so the postfix runs in more than one phase:

| Caller | Condition | Lifecycle |
|---|---|---|
| `Program.PrepareFrame` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2148)) | `RendererRebuildNeeded` at frame start | `frame:prepare` |
| `Program.Render` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2443-L2451)) | swapchain acquire failed or rebuild pending; the frame is skipped | `frame:render` |
| `Program.PostRender` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2351-L2358)) | present reported `NeedsResize`, or a rebuild was requested during the frame | `frame:post-render` |
| `ScreenshotCapture` ([current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L234-L240), [current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L262-L267)) | arming or aborting a capture with scale above 1 | `frame:prepare` (from `OnPrepareFrame`) |

The inline rebuild in `ImGuiPreRender` happens in `frame:render`, while the frame's command buffer is
being recorded; `WaitIdle` there waits for earlier frames only, since the current buffer has not been
submitted.[^ksa-screenshotcapture]

## Key code

| Path | Role |
|---|---|
| [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L227-L236) | `RebuildRenderer` postfix |
| [ShaderExtensions/Patches.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/Patches.cs#L196-L217) | First-frame rebuild request and extent check |
| [ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderHandler.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderHandler.cs#L20-L49) | Source target lifetime |
| [ShaderExtensions/ImGuiShader/ImGuiRenderers.cs](https://github.com/AMPW-german/ShaderExtensions/blob/6a2eb66ea85bcaa2d4657e502a4f75e92693ea77/ShaderExtensions/ImGuiShader/ImGuiRenderers.cs#L119-L166) | Viewport size tracking for ImGui shaders |

## Snippets

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

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `Program.RebuildRenderer(bool skipSwapchain)` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5105-L5141) | Postfix target |
| `Program.ScheduleRendererRebuild`, `RendererRebuildNeeded` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L5248-L5251) | Requesting a rebuild |
| `Renderer.Extent` (`ExtentOverride ?? _extent`) | [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L96-L98) | Size the chains follow |
| `ScreenshotCapture` arming | [current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L234-L240) | `ExtentOverride` plus `RebuildRenderer(skipSwapchain: true)` |

## Pitfalls and version notes

- **Stalls.** Every rebuild path calls `Device.WaitIdle()`, and a resize drag can trigger several in a
  row.
- **Extent override.** During a high-resolution capture `Renderer.Extent` returns the override, so the
  chains are sized to the capture, not to the swapchain; see the copy-region note in
  [Post-ImGui global post shaders](post-imgui-global-post-shaders.md).[^core-renderer]
- **No shutdown path.** Nothing disposes the chains, ImGui renderers or the time buffer when the game
  exits; the compatibility plan notes that StarMap's unload runs after `Program.Main` returns, likely
  too late for Vulkan cleanup.[^sx-compatplan] A future `[StarMapUnload]` would also run after
  `StarMapCore.DeInit` has called `Harmony.UnpatchAll()` with no id, which removes every mod's patches
  first, so it cannot rely on any of these hooks still being installed
  ([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35)).[^sm-core]
- **The ImGui path is not tied to the postfix;** `ImGuiRenderers.RebuildAll()` is commented out
  there.
- **One extra full rebuild at startup** from the forced first-frame request.

## See also

- [RenderGame transpiler around the swapchain render pass](rendergame-render-pass-transpiler.md)
- [RenderPassId-ordered fullscreen pass chain](post-processing-pass-chain.md)
- [Shared shader time uniform buffer](shared-time-uniform-buffer.md)
- [Per-window ImGui post shader](imgui-window-post-shader.md)
- [Postfix Program.RebuildRenderer plus a first-frame schedule and per-frame extent check](../../../howto/rendering/vulkan-passes/handle-renderer-rebuilds/handle-renderer-rebuilds-1.md) - how-to article demonstrating this feature

[^sx-patches]: ShaderExtensions/Patches.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^sx-prehandler]: ShaderExtensions/PostProcessing/PreImgui/PostProcessingHandler.cs
[^sx-posthandler]: ShaderExtensions/PostProcessing/PostImgui/GlobalPostShaderHandler.cs
[^sx-imguirenderers]: ShaderExtensions/ImGuiShader/ImGuiRenderers.cs
[^sx-imguibaserenderer]: ShaderExtensions/ImGuiShader/ImGuiBaseRenderer.cs
[^ksa-screenshotcapture]: KSA.ScreenshotCapture (decompiled, build 2026.10.10.5554)
[^core-renderer]: Core.Renderer (decompiled, build 2026.10.10.5554)
[^sx-compatplan]: ShaderExtensions/KSA-2026.9.7-compatibility-plan.md
[^sm-core]: StarMap.Core/StarMapCore.cs
