---
type: How-To
title: Stop recording now, free two frames later after WaitIdle; nullable images; no device work in unload
description: Detach a Vulkan resource from every render path immediately, queue it for a two-frame countdown, call Device.WaitIdle before freeing, and never touch the device from StarMapUnload
category: rendering
group: vulkan-passes
topic: free-vulkan-resources-safely
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/purrtty/features/vulkan-resource-teardown-discipline
  - mods/purrtty/features/kitty-graphics-as-imgui-vulkan-textures
techniques: [vulkan-texture, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, starmap StarMapUnload]
lifecycle: [frame:draw-ui, frame:render, frame:prepare]
game_types: [RenderCore.SimpleVkTexture, KSA.ImGuiBackendVulkanImpl, KSA.Program, Core.Renderer]
tags: [vulkan, teardown, wait-idle, frames-in-flight, shutdown, imgui-texture]
---

Outcome: a mod that creates and destroys Vulkan objects at runtime (an offscreen image, an ImGui texture, a quad's descriptor set) never frees something the GPU is still reading,
and never calls the dead device at game exit. The failure modes are `VK_ERROR_DEVICE_LOST` or an access violation that managed `try`/`catch` cannot trap. Demonstrated by purrTTY
([Safe Vulkan teardown and live resize for mod-owned GPU resources](../../../../mods/purrtty/features/vulkan-resource-teardown-discipline.md), [Kitty graphics images as ImGui Vulkan textures](../../../../mods/purrtty/features/kitty-graphics-as-imgui-vulkan-textures.md)). Requires StarMap.

## Background: how the game does it

- The renderer keeps `MaxFramesInFlight = 2` [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L28): a command buffer recorded this frame is submitted in `PostRender` and may still be executing during the next frame.
  A mod that records a draw referencing an image therefore keeps that image alive for up to two frames after it stops recording it.
- `ImGuiBackendVulkanImpl.AddTexture(sampler, imageView, layout)` allocates a descriptor set from the backend's shared pool and returns its handle as the texture id;
  `RemoveTexture` returns the set to the pool [current/decomp/KSA/ImGuiBackendVulkanImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendVulkanImpl.cs#L1119-L1131).
- `Program.Dispose` destroys the device: textures, bindless library, ImGui backend, then `_renderer.Dispose()` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1470-L1484). StarMap raises `[StarMapUnload]` after the
  game's entry point has returned (see the shutdown row in [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)), so the device is already gone.
- `ImageEx` is a readonly struct whose `Dispose()` calls `_allocator.FreeAllocation(...)` [current/decomp/Brutal.VulkanApi.Abstractions/ImageEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/ImageEx.cs#L165-L168); on `default(ImageEx)` the allocator is null and it throws.

## Lifecycle and timing

| Operation | Thread and phase |
|---|---|
| Detach (remove from the list the draw hook iterates, `RemoveTexture`) | immediately, main thread, `frame:draw-ui` |
| Free the GPU object | start of a later `[StarMapAfterGui]` pass, two frames after detaching, after `Device.WaitIdle()` |
| Recording stops | immediate: the draw hook (`frame:render`) no longer sees the instance |
| Shutdown | `[StarMapUnload]`: close shells and files only, no device calls |

## Steps

1. **Detach now.** Remove the instance from the list the render hook iterates, unregister its name, drop focus, and queue it with a countdown. Clear the global "active" flag only when no instance remains.

   ```csharp
   // purrTTY.GameMod/InWorld/InWorldTerminalManager.cs L166-L185
   public void Remove(InWorldTerminalInstance instance)
   {
       if (!_instances.Remove(instance))
       {
           return; // already detached
       }

       if (ReferenceEquals(_focused, instance))
       {
           _focused = null;
       }

       instance.HasFocus = false;
       instance.UnregisterNow();
       _pendingTeardown.Add((instance, TeardownDelayFrames));

       if (_instances.Count == 0)
       {
           Active = false;
       }
   ```

2. **Free later.** At the start of each `OnAfterGui`, decrement; at zero call `Device.WaitIdle()` and then dispose. Freeing immediately is a use-after-free: the scene command buffer recorded that frame still
   references the quad's descriptor set and sampled image.

   ```csharp
   // purrTTY.GameMod/InWorld/InWorldTerminalManager.cs L401-L422
   private void ProcessPendingTeardown()
   {
       if (_pendingTeardown.Count == 0)
       {
           return;
       }

       for (int i = _pendingTeardown.Count - 1; i >= 0; i--)
       {
           var (instance, framesLeft) = _pendingTeardown[i];
           framesLeft--;
           if (framesLeft > 0)
           {
               _pendingTeardown[i] = (instance, framesLeft);
               continue;
           }

           WaitDeviceIdle();
           instance.Dispose();
           _pendingTeardown.RemoveAt(i);
       }
   }
   ```

3. **Dispose in dependency order.** Descriptor pool and quad (they reference the image) before the per-frame renderer (waits on its fences, frees command buffers and pool), then content and textures, then the secondary ImGui backend
   (with its context current), then the context, finally the target.

4. **Hold images as nullable.** `ImageEx` and similar wrappers throw on `default`; keep them as `ImageEx?` and dispose with `?.Dispose()`.

5. **ImGui textures: remove first, destroy later.** Evicting a texture removes its descriptor immediately (no new draw can use it) and defers disposing the image by a few frames:

   ```csharp
   // purrTTY.Display/Ghostty/ImageTextureCache.cs L228-L240
   private void Evict(int imageId, (Entry Entry, LinkedListNode<int> Node) bucket)
   {
       _entries.Remove(imageId);
       _lruOrder.Remove(bucket.Node);
       _estimatedVramBytes = Math.Max(0, _estimatedVramBytes - (long)bucket.Entry.Width * bucket.Entry.Height * 4);

       // Release the ImGui descriptor slot immediately (prevents new draw calls from
       // referencing this texture). The Vulkan image itself is deferred: in-flight
       // commands from prior frames may still be sampling it via the old descriptor.
       try { ImGuiBackend.Vulkan.RemoveTexture(bucket.Entry.TexRef); } catch { }

       _pendingDelete.Enqueue((bucket.Entry, _frame + DeferredDeleteFrames));
   }
   ```

6. **Shutdown: never touch the device.** The coordinator's `Dispose` closes shells (device-free) and skips every free; the process exit reclaims VRAM.

   ```csharp
   // purrTTY.GameMod/InWorld/InWorldTerminalManager.cs L439-L475
   public void Dispose()
   {
       if (_disposed)
       {
           return;
       }

       _disposed = true;

       Active = false;
       _focused = null;
       Instance = null;

       // The mod only unloads at game shutdown, by which point the game has already
       // destroyed the Vulkan device — calling WaitIdle / Destroy* on it faults with
       // an AccessViolationException (a corrupted-state exception that managed
       // try/catch cannot trap). So we do NOT free GPU resources here: the process is
       // exiting and the driver/OS reclaims the VRAM. We still close each shell
       // session (device-free) to avoid orphaned child processes. The mid-session
       // Close/resize path (ProcessPendingTeardown) keeps its WaitIdle — the device
       // is alive there.
       for (int i = 0; i < _instances.Count; i++)
       {
           _instances[i].Dispose(freeGpu: false);
       }

       _instances.Clear();

       for (int i = 0; i < _pendingTeardown.Count; i++)
       {
           _pendingTeardown[i].Instance.Dispose(freeGpu: false);
       }

       _pendingTeardown.Clear();

       // Drop the shared quad without touching the dead device.
       _sharedQuad = null;
   ```

## Complete example

| File | Role |
|---|---|
| [purrTTY.GameMod/InWorld/InWorldTerminalManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/InWorldTerminalManager.cs) | `Remove`, `ProcessPendingTeardown`, shutdown `Dispose` |
| [purrTTY.GameMod/InWorld/InWorldTerminalInstance.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/InWorldTerminalInstance.cs#L299-L335) | `Dispose(freeGpu)` and `Teardown` order |
| [purrTTY.Display/Ghostty/ImageTextureCache.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/ImageTextureCache.cs) | LRU cache of ImGui textures with deferred delete |
| [purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L220-L235) | The `[StarMapUnload]` entry |

## Variations and gotchas

- **Two frames is enough** for the frames in flight at this build; size the delay from the swapchain or frame-resource count if you share objects with the game across more frames.
- **Do not wrap shutdown device calls in `try`/`catch`.** An access violation is not catchable; skip the calls.
- **The ImGui descriptor pool is shared by the whole game.** A mod that registers one texture per frame or per image without a cap exhausts it; purrTTY caps at 32 textures by default.
- **Create, upload, `AddTexture`, `RemoveTexture` on the main thread only.**
- **`WaitIdle` stalls** until the GPU drains; accept it for destroy and resize, not per frame.

## See also

- [Overview](overview.md)
- [Approach 2: retire by MaxFramesInFlight + 1 frames](free-vulkan-resources-safely-2.md)
- [Approach 3: publish after a fence, retire after WaitIdle](free-vulkan-resources-safely-3.md)
- [Safe Vulkan teardown and live resize for mod-owned GPU resources](../../../../mods/purrtty/features/vulkan-resource-teardown-discipline.md), [Kitty graphics images as ImGui Vulkan textures](../../../../mods/purrtty/features/kitty-graphics-as-imgui-vulkan-textures.md)
- [Recreate mod render targets when KSA rebuilds its renderer](../handle-renderer-rebuilds/overview.md), [Show a PNG or runtime-generated image in an ImGui window](../../textures-and-decals/show-an-image-in-imgui/overview.md), [Record and submit your own Vulkan work: offscreen renders and one-shot uploads](../submit-your-own-vulkan-commands/overview.md)
