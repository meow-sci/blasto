---
type: How-To
title: Secondary ImGui context sharing the game's atlas, second backend, fence-ring submission
description: Create a second ImGui context that shares the main font atlas, a second Vulkan ImGui backend on a mod-owned render pass, and render a whole UI into an image every changed frame
category: rendering
group: textures-and-decals
topic: render-imgui-into-a-texture
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/purrtty/features/offscreen-imgui-context-render-to-texture]
techniques: [imgui-window, vulkan-pipeline]
hooks: [starmap StarMapAfterGui, starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types:
  - KSA.ImGuiBackendVulkanImpl
  - Brutal.ImGuiApi.ImGui
  - RenderCore.SimpleVkTexture
  - KSA.Rendering.RenderTarget
tags: [imgui, render-to-texture, secondary-context, backend, fence-ring, offscreen]
---

Outcome: a whole ImGui UI (a terminal, a dashboard) is drawn into a Vulkan image the mod owns, instead of the screen, so it can be shown in the scene or in another window. The pieces: a second ImGui context that shares the game's font atlas, a second `ImGuiBackendVulkanImpl` built on the mod's own classic render pass, and a command pool with a fence ring
(the submit pattern is described in [submit your own Vulkan commands](../../vulkan-passes/submit-your-own-vulkan-commands/overview.md), approach 2). Demonstrated by purrTTY's in-world terminals ([Render-to-texture with a second ImGui context and Vulkan backend](../../../../mods/purrtty/features/offscreen-imgui-context-render-to-texture.md)). Prerequisites: StarMap.

## Background: how the game does it

`ImGuiBackendVulkanImpl` [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs) is constructed from a `CreateInfo` (device, graphics queue, `RenderPass`, `SubPass`, `MinImageCount`, `ImageCount`, `SampleCount`, `DescriptorPoolSize` of at least 256) and installs itself into the **currently bound** ImGui context's IO; it still takes a `VkRenderPass`, so a classic pass is created for the mod's target even though the scene uses
dynamic rendering. Its texture ids are raw `VkDescriptorSet` handles, so main-pool sets bind fine in a secondary pass. The game's own backend is `ImGuiBackend.Vulkan` [current/decomp/KSA/ImGuiBackend.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackend.cs#L23) with a 1000-set pool.

## Lifecycle and timing

| Step | When |
|---|---|
| Coordinator `Initialize` (no GPU) | `startup:all-mods-loaded` |
| Instance creation (GPU allocation, font measurement) | `frame:draw-ui`, from a dialog inside `[StarMapAfterGui]`, because it needs an active ImGui frame for the cell measurement and `Program.OffscreenTarget` (created after `StarMapAllModsLoaded`) for the shared quad pipeline |
| Build, record and submit the off-screen UI | `frame:draw-ui`, `OnAfterGui`, between the game's `ImGui.NewFrame` and `ImGui.Render` for the main context; only in frames where the HUD is drawn and the content changed |
| Sampling the texture | later the same frame in `frame:render` |

The secondary context is switched in and out while the main context is mid-frame; every switch restores the previous one, and the secondary context is never destroyed while current (KSA's GLFW backend dereferences `ImGui.GetIO()` every frame). The off-screen submit precedes the game's frame submit on the same queue; no semaphore or barrier links them. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Create the target:** colour image `R8G8B8A8UNorm` (not sRGB: the quad shader gamma-decodes itself), depth image, two views, a classic render pass (colour clear/store with `FinalLayout = ShaderReadOnlyOptimal`), a framebuffer and a linear clamped sampler. Texture size is derived from the requested grid and clamped to 256..4096 px.

2. **Create the secondary context** sharing the main atlas (a second font upload and invalid font handles are avoided), no `.ini`, keyboard navigation on. Always restore the previous context in `finally`.

   ```csharp
   // purrTTY.GameMod/InWorld/OffscreenImGuiContext.cs L53-L88
       // ImGui allocate a fresh one). We share to avoid a second font upload and
       // to keep ImFontPtr handles valid across both contexts.
       var prevCtx = ImGui.GetCurrentContext();
       try
       {
           ImFontAtlasPtr sharedAtlas = default;
           if (!prevCtx.IsNull())
           {
               sharedAtlas = ImGui.GetIO().Fonts;
           }

           Native = ImGui.CreateContext(sharedAtlas);
           ImGui.SetCurrentContext(Native);

           var io = ImGui.GetIO();
           io.DisplaySize     = new float2(width, height);
           io.DeltaTime       = 1f / 60f;
           io.IniFilename     = default;                 // null = no .ini persistence
           io.MouseDrawCursor = false;
           // Keep keyboard nav available in the secondary context even before
           // real focus signals are wired up (Phase 8).
           io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;

           Size = new int2(width, height);
       }
       finally
       {
           // Always restore the prior context, even if CreateContext or any IO
           // mutation throws.
           ImGui.SetCurrentContext(prevCtx);
       }
   }

   /// <summary>
   ///     Runs <paramref name="action"/> with this context current and restores
   ///     whatever context was current on entry — even if <paramref name="action"/>
   ```

3. **Create the second backend while the secondary context is current,** because its constructor installs itself into the current context's IO and asserts that no backend is installed yet. Single-sample, `MinImageCount = ImageCount = 2`, `DescriptorPoolSize = 256`.

   ```csharp
   // purrTTY.GameMod/InWorld/OffscreenImGuiBackend.cs L40-L62
   public OffscreenImGuiBackend(Renderer renderer, VkRenderPass renderPass,
                                int minImageCount, int imageCount,
                                int descriptorPoolSize = 256)
   {
       ArgumentNullException.ThrowIfNull(renderer);

       // The off-screen terminal target is single-sample (no MSAA) — ImGui text
       // needs none. (The world-space quad pipeline, by contrast, matches the
       // scene pass's MSAA in a later phase.) The backend asserts
       // MinImageCount >= 2, ImageCount >= MinImageCount, DescriptorPoolSize > 0.
       Impl = new ImGuiBackendVulkanImpl(new ImGuiBackendVulkanImpl.CreateInfo
       {
           Device             = renderer.Device,
           GraphicsQueue      = renderer.Graphics,
           RenderPass         = renderPass,
           SubPass            = 0,
           MinImageCount      = minImageCount,
           ImageCount         = imageCount,
           SampleCount        = VkSampleCountFlags._1Bit,
           DescriptorPoolSize = descriptorPoolSize,
           // MinAllocationSize: leave default; KSA's backend handles a zero default.
       });
   }
   ```

4. **Each frame**, wait for the slot's fence, run `NewFrame` / your UI / `Render` / `GetDrawData` inside `ctx.With(...)`, record `BeginRenderPass` (clear to transparent), `backend.RenderDrawData(drawData, cmd)` with the secondary context current, `EndRenderPass`, and submit on `renderer.Graphics` (the queue lock makes it safe beside the game's submits). Null `drawData.Textures` during the secondary render so it does not create or destroy atlas textures it does not own.

   ```csharp
   // purrTTY.GameMod/InWorld/PerFrameRenderer.cs L139-L192
   public void Frame(double dt)
   {
       var device = _renderer.Device;
       var cmd    = _cmds[_currentIndex];
       var fence  = _fences[_currentIndex];

       // 1. Wait for the previous use of THIS slot to finish on the GPU before we
       //    reuse its command buffer / fence. Initially signaled, so the first
       //    frame returns immediately.
       Span<VkFence> fenceSpan = stackalloc VkFence[1] { fence };
       device.WaitForFences(fenceSpan, true, unchecked((nint)(-1L)));
       device.ResetFences(fenceSpan);

       // 2. Build the secondary-context UI and bake the draw lists. Every ImGui
       //    call here MUST happen with the secondary context current.
       ImDrawDataPtr drawData = default;
       _ctx.With(() =>
       {
           var io = ImGui.GetIO();
           io.DeltaTime   = (float)dt;
           io.DisplaySize = new float2(_target.Extent.Width, _target.Extent.Height);

           ImGui.NewFrame();
           try
           {
               BuildUi?.Invoke();
           }
           finally
           {
               // ImGui requires a matching Render() for every NewFrame(); skipping
               // it on a BuildUi exception would leave the context in a bad state.
               ImGui.Render();
           }
           drawData = ImGui.GetDrawData();
       });

       // 3. Reset & re-record the slot's command buffer.
       cmd.Reset(VkCommandBufferResetFlags.None);
       cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);

       RecordRenderPass(cmd, drawData);

       cmd.End();

       // 4. Submit on the same graphics queue the main render uses. Submit is
       //    internally locked, so this is safe vs. KSA's own submissions.
       Span<CommandBuffer>        cmds     = stackalloc CommandBuffer[1] { cmd };
       Span<VkSemaphore>          none     = default;
       Span<VkPipelineStageFlags> noStages = default;
       _renderer.Graphics.Submit(none, noStages, cmds, none, fence);

       // 5. Advance the ring.
       _currentIndex = (_currentIndex + 1) % _cmds.Length;
   }
   ```

5. **Skip when unchanged:** tick the content every frame (the PTY inbox must drain) but re-record and submit only on a new frame generation, new images, a focus change, a forced redraw, or at least 1 s since the last render.

## Complete example

| File | Role |
|---|---|
| [purrTTY.GameMod/InWorld/OffscreenRenderTarget.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/OffscreenRenderTarget.cs) | Images, views, render pass, framebuffer, sampler |
| [purrTTY.GameMod/InWorld/OffscreenImGuiContext.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/OffscreenImGuiContext.cs) | Secondary context and safe switching |
| [purrTTY.GameMod/InWorld/OffscreenImGuiBackend.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/OffscreenImGuiBackend.cs) | Second backend |
| [purrTTY.GameMod/InWorld/PerFrameRenderer.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/PerFrameRenderer.cs) | Command ring and per-frame render |

## Variations and gotchas

- **Never let the secondary backend manage atlas textures.** If the main backend has not uploaded a newly added glyph page yet, the secondary render throws `ImException`; the frame is skipped and the next one succeeds.
- **Missing barrier.** The class relies on the pass's final layout and submission order; add a subpass-to-external dependency for spec correctness.
- **HUD hidden:** `[StarMapAfterGui]` does not run, so in-world textures stop updating while the quads keep drawing the last texture.
- **Cost:** each changed frame is an extra render pass and submit plus about 4 MB of colour at 1024 x 1024; bound the instance count.
- **`ImageEx` is a readonly struct** whose `Dispose` throws on `default`; hold allocations as `ImageEx?`.
- Since the scene moved to dynamic rendering purrTTY owns a classic pass because the ImGui backend still takes a `VkRenderPass`.

## See also

- [Overview](overview.md)
- [Approach 2: re-point a monitor material's bindless slot](render-imgui-into-a-texture-2.md)
- [Approach 3: draw the texture as a world-space quad](render-imgui-into-a-texture-3.md)
- [Render-to-texture with a second ImGui context and Vulkan backend](../../../../mods/purrtty/features/offscreen-imgui-context-render-to-texture.md)
- [Record and submit your own Vulkan work: offscreen renders and one-shot uploads](../../vulkan-passes/submit-your-own-vulkan-commands/overview.md), [Draw a textured quad attached to a part or the camera](../draw-a-textured-quad-in-the-world/overview.md), [Free mod-owned Vulkan resources without device loss](../../vulkan-passes/free-vulkan-resources-safely/overview.md)
