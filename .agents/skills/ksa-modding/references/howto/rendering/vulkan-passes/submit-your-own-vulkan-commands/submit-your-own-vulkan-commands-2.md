---
type: How-To
title: Mod command pool, fence ring and locked queue submit
description: Re-record a small offscreen render every changed frame from a mod-owned command pool with a two-slot fence ring, submitted under the engine's queue lock
category: rendering
group: vulkan-passes
topic: submit-your-own-vulkan-commands
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/purrtty/features/offscreen-imgui-context-render-to-texture]
techniques: [vulkan-pipeline, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:render]
game_types: [Core.Renderer, Brutal.VulkanApi.Queue, KSA.ImGuiBackendVulkanImpl]
tags: [vulkan, command-pool, fence-ring, frames-in-flight, queue-submit, offscreen]
---

Outcome: a mod-owned command buffer that is re-recorded and submitted every frame (or every changed
frame) without ever blocking the main thread in the steady state. The slot ring (one command buffer and
one fence per frame in flight) is the pattern for per-frame offscreen work such as a UI rendered to a
texture. It is demonstrated by purrTTY's in-world terminal
([Render-to-texture with a second ImGui context and Vulkan backend](../../../../mods/purrtty/features/offscreen-imgui-context-render-to-texture.md)). Compare approach 1, which blocks
on a fence for rare one-shot work.

## Background: how the game does it

The game keeps `Renderer.MaxFramesInFlight = 2` [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L28) and a per-frame array of sync
objects and command buffers [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L542-L543); a frame is submitted in `PostRender` to
`Renderer.Graphics`. `Queue.Submit` takes the queue's lock [current/decomp/Brutal.VulkanApi/Queue.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi/Queue.cs#L77-L83), so
any thread-safe-by-lock submit from the main thread cannot interleave with another submit mid-call.
Nothing orders a mod's submit against the game's frame submission except submission order on the
same queue; a mod that needs the game's output as input must wait for the previous frame itself.

## Lifecycle and timing

| Step | Hook | Frame point |
|---|---|---|
| Create the pool, command buffers, signalled fences | the first time the feature is used, inside an ImGui frame | `frame:draw-ui` (purrTTY needs an active ImGui frame to measure fonts) |
| Wait for slot fence, record, submit | `[StarMapAfterGui]` (postfix of `Program.OnDrawUiViewports`) | `frame:draw-ui`, after the next physics step was dispatched |
| GPU sampling of the result | the frame's own command buffer | `frame:render`, later the same frame (submitted in `frame:post-render`) |

The submit precedes the game's frame submit on the same queue, so in practice the result is ready by the time
the scene samples it. No semaphore or pipeline barrier links the two submissions, which is why the
render-pass dependency in the gotchas below matters. See the draw-ui rows in
[the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Create a transient pool whose `QueueFamilyIndex` is the graphics queue's **family**
   (`renderer.Graphics.Family`), allocate `framesInFlight` primary command buffers in one call, and
   create every fence **signalled** so the first wait returns at once.

   ```csharp
   // purrTTY.GameMod/InWorld/PerFrameRenderer.cs L83-L117
   // Transient: the pool's buffers are short-lived (re-recorded each frame).
   // ResetCommandBufferBit: we reset individual buffers via CommandBuffer.Reset()
   // rather than resetting the whole pool.
   //
   // QueueFamilyIndex must be the graphics queue's FAMILY index (not its index
   // within the family) — command buffers from this pool are submitted to
   // renderer.Graphics, so the pool must belong to that queue's family.
   var poolCi = new VkCommandPoolCreateInfo
   {
       Flags            = VkCommandPoolCreateFlags.TransientBit |
                          VkCommandPoolCreateFlags.ResetCommandBufferBit,
       QueueFamilyIndex = renderer.Graphics.Family,
   };
   _pool = renderer.Device.CreateCommandPool(in poolCi, null);

   _cmds   = new CommandBuffer[framesInFlight];
   _fences = new VkFence[framesInFlight];

   try
   {
       // Allocate all primary command buffers up front in a single call.
       var allocCi = new VkCommandBufferAllocateInfo
       {
           CommandPool = _pool,
           Level       = VkCommandBufferLevel.Primary,
       };
       renderer.Device.AllocateCommandBuffers(allocCi, _cmds);

       // Fences are created SIGNALED so the very first WaitForFences in Frame()
       // returns immediately — no special "first frame" branch.
       for (int i = 0; i < framesInFlight; i++)
       {
           _fences[i] = renderer.Device.CreateFence(VkFenceCreateFlags.SignaledBit, null);
       }
   }
   ```

2. Each frame: take the ring slot, wait for its fence (the submit from two renders ago), reset the fence,
   reset and begin the command buffer, record, end, and submit with the fence. Use
   `OneTimeSubmitBit` because the buffer is re-recorded every time.

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

3. Skip work when nothing changed. purrTTY re-records only when the terminal reports a new frame
   generation, new images, a focus change or at least one second since the last render; the quad keeps
   sampling the previous texture otherwise.

4. Tear down in order: wait for the device (or for every slot fence), destroy fences, then the command
   pool (which frees its buffers). Details in [free mod-owned Vulkan resources safely](../free-vulkan-resources-safely/overview.md).

5. Expect nothing in the log in the steady state. If the result never appears, look for an exception
   from the recording step (the secondary ImGui backend throws `ImException` when a glyph page it uses has
   not been uploaded yet; the frame is skipped and the next one succeeds).

## Complete example

| File | Role |
|---|---|
| [purrTTY.GameMod/InWorld/PerFrameRenderer.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/PerFrameRenderer.cs) | Pool, ring, per-frame record and submit |
| [purrTTY.GameMod/InWorld/OffscreenImGuiBackend.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/OffscreenImGuiBackend.cs#L40-L62) | The second ImGui Vulkan backend created for the target's render pass |
| [Render ImGui into a texture](../../textures-and-decals/render-imgui-into-a-texture/overview.md) | The full topic this ring belongs to |

## Variations and gotchas

- **Ring size equals frames in flight.** Two matches KSA's `MaxFramesInFlight`; one slot would
  serialise CPU and GPU, more only costs memory.
- **Missing barrier.** The class relies on the render pass's `FinalLayout = ShaderReadOnlyOptimal` and
  submission order; Vulkan's implicit end-of-render-pass dependency does not by itself make colour writes
  visible to a later fragment-shader read, so add a subpass-to-external dependency (colour-attachment write
  to fragment-shader read) when you copy this. purrTTY works in practice without it.
- **Family, not index.** Pass `Graphics.Family` as the pool's `QueueFamilyIndex`.
- **Hidden HUD.** `[StarMapAfterGui]` does not run while the HUD is hidden, so the ring stops advancing and
  consumers keep sampling the last image.
- **Cost.** Each changed frame is one extra render pass and queue submit.

## See also

- [Overview](overview.md)
- [Approach 1: one-shot command buffer and fence](submit-your-own-vulkan-commands-1.md)
- [Approach 3: transient pool for readback and thumbnails](submit-your-own-vulkan-commands-3.md)
- [Approach 4: private images, runtime GLSL, fenced submit](submit-your-own-vulkan-commands-4.md)
- [Render-to-texture with a second ImGui context and Vulkan backend](../../../../mods/purrtty/features/offscreen-imgui-context-render-to-texture.md)
- [Render ImGui UI into a Vulkan texture for use in the 3D world](../../textures-and-decals/render-imgui-into-a-texture/overview.md), [Free mod-owned Vulkan resources without device loss](../free-vulkan-resources-safely/overview.md)
