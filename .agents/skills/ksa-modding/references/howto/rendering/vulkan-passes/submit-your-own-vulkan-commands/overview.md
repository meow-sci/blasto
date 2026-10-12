---
type: Game Feature
title: "Record and submit your own Vulkan work: offscreen renders and one-shot uploads"
description: One-shot uploads, offscreen renders and ImGui-to-texture need command buffers the game does not own, so pick the pool, fence and queue lock that neither stall nor auto-submit half-recorded work
category: rendering
group: vulkan-passes
topic: submit-your-own-vulkan-commands
demonstrated_by:
  - mods/unscience/features/pebbles-asset-upload-submission
  - mods/purrtty/features/offscreen-imgui-context-render-to-texture
  - mods/unscience/features/parts-now-thumbnail-generation
  - mods/unscience/features/pebbles-workshop-offscreen-preview
lifecycle: [frame:draw-ui, frame:dispatch-solvers, frame:pre-render, render:imgui]
game_types:
  - RenderCore.StagingPool
  - Brutal.VulkanApi.Queue
  - KSA.Program
  - RenderCore.SimpleVkTexture
  - KSA.ImGuiBackendVulkanImpl
  - Core.Renderer
tags: [vulkan, command-buffer, fence, staging, offscreen, upload]
---

Everything the game draws is recorded into one primary command buffer per frame in flight and submitted in
`PostRender`. A mod that needs the GPU to do something outside that command buffer (upload a texture, render
a preview, draw a UI into an image, read a thumbnail back) must record and submit a command buffer of its
own. The decisions are which pool owns the buffer, how completion is observed, and when it is safe to publish
or destroy the result.

## Engine background

- `StagingPool` [current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs#L7-L238) allocates staging buffers and
  command buffers; `Dispose` always calls `Submit()` and `Wait()`, so a pool whose command buffer was
  half-recorded would submit it. Use it as a memory allocator only.
- `VkUtils.StageAndUploadToBuffer` [current/decomp/RenderCore/VkUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/VkUtils.cs#L82-L106) and `SimpleVkTexture.UploadData`
  [current/decomp/RenderCore/SimpleVkTexture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/SimpleVkTexture.cs#L374-L383) record into a command buffer you pass in.
- `Queue.Submit` takes the queue lock [current/decomp/Brutal.VulkanApi/Queue.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi/Queue.cs#L77-L83); the frame is submitted to
  the same `Renderer.Graphics` queue in `PostRender`. `MaxFramesInFlight` is 2 [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L28).
- `ImGuiBackendVulkanImpl.AddTexture` / `RemoveTexture` [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L1119-L1131)
  register an image view as an ImGui texture.
- Frame phases involved: the pre-GUI prefix of `OnDrawUiFrame` (`frame:draw-ui`), `OnPreRender`
  (`frame:pre-render`), and the ImGui pass at step 25 of `RenderGame` (`render:imgui`). See
  [the render sequence of the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: private command buffer and fence](submit-your-own-vulkan-commands-1.md) | unscience | `StagingPool` for memory only, blocking fence wait | `frame:draw-ui` (BeforeGui) | Simple and cancellable; blocks the main thread per submission |
| [2: command pool, fence ring, locked submit](submit-your-own-vulkan-commands-2.md) | purrtty | Re-recorded slot ring, no steady-state wait | `frame:draw-ui` (AfterGui) | Per-frame work without stalls; needs correct sync with the consumer |
| [3: transient pool for readback and thumbnails](submit-your-own-vulkan-commands-3.md) | unscience | Game thumbnail pipeline, fence wait, optional readback | `frame:draw-ui` before `frame:pre-render` | Reuses game framing; shares a camera slice with the hover preview |
| [4: private images, runtime GLSL](submit-your-own-vulkan-commands-4.md) | unscience | Free-standing pipeline, `WaitIdle` before publishing | `frame:draw-ui` + `render:imgui` | Full control of an offscreen scene; drains the GPU on change frames |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Free mod-owned Vulkan resources without device loss](../free-vulkan-resources-safely/overview.md), [Capture rendered frames from KSA](../capture-rendered-frames/overview.md), [Render ImGui UI into a Vulkan texture for use in the 3D world](../../textures-and-decals/render-imgui-into-a-texture/overview.md), [Build a Vulkan pipeline compatible with KSA's scene render target](../build-a-vulkan-pipeline-for-the-scene-target/overview.md)
