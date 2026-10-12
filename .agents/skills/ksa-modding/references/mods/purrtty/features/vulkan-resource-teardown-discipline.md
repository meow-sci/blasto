---
type: Mod Feature
title: Safe Vulkan teardown and live resize for mod-owned GPU resources
description: Stop recording a resource immediately, free it only after the frames that may reference it have completed plus a device WaitIdle, rebuild render targets in place for live resizes, and never touch the Vulkan device in StarMapUnload because the game has already destroyed it
mod: purrtty
category: rendering
techniques: [vulkan-texture, vulkan-pipeline, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, starmap StarMapUnload]
lifecycle: [frame:draw-ui, frame:render]
game_types: [Core.Renderer, Brutal.VulkanApi.Abstractions.ImageEx, KSA.Program, KSA.ImGuiBackendVulkanImpl]
tags: [vulkan, lifetime, teardown, resize, shutdown, device-lost, in-world]
---

Anything a mod records into the game's command buffer is used by the GPU a frame or two later,
and anything it destroys at the wrong moment is a `VK_ERROR_DEVICE_LOST` or an access violation that
managed code cannot catch. purrTTY's in-world terminals are created, resized and destroyed at
runtime, so they carry a small set of rules that apply to any mod owning Vulkan objects.

## How it works

**Remove: detach now, free later** (`InWorldTerminalManager.Remove` / `ProcessPendingTeardown`):[^inworld-manager]

1. Remove the instance from the list the render postfix iterates, so it is not recorded again.
2. Unregister its name and drop focus.
3. Queue `(instance, TeardownDelayFrames = 2)`; clear the global `Active` flag only when no instance
   remains.
4. At the start of each `OnAfterGui`, count down; at zero call
   `Program.GetRenderer()?.Device.WaitIdle()` and then `instance.Dispose()`.

Freeing immediately is a use-after-free: the scene command buffer recorded that frame still
references the quad's descriptor set and sampled image (gotcha 25).[^gotchas]

**Dispose order inside an instance** (`Teardown`): quad descriptor pool, then the per-frame
renderer (waits on its fences, frees command buffers and pool), then the terminal content and its
kitty textures, then the secondary ImGui backend (disposed with the secondary context current),
the context, and finally the off-screen target.[^inworld-instance]

**Live resize without restarting the shell** (`TrySetGridSize`): commit the new cols/rows, stop
recording the quad while `_pendingResize` is set, wait out the same frame window, then under
`Device.WaitIdle()` rebuild the target in place (`OffscreenRenderTarget.Resize`: new images, views,
render pass, framebuffer, sampler), resize the secondary context and rewrite the quad's descriptor
(`InWorldQuad.RebindTarget`). The secondary ImGui backend is not rebuilt: its pipeline was created
against the first render pass, and a recreated pass with identical formats and sample count is
render-pass compatible. The next frame sees the new extent and resizes the engine grid and PTY, so
the program inside reflows.

**Shutdown: never touch the device.** StarMap raises `[StarMapUnload]` from `ModLoader.Dispose`,
which runs in `StarMapCore.DeInit` after the game's entry point has returned, after `Program`
disposed its renderer.[^starmap-surveyer][^starmap-core] Calling `WaitIdle` or any `Destroy*` then
faults with an `AccessViolationException` that try/catch cannot trap. So the coordinator's
`Dispose` calls `instance.Dispose(freeGpu: false)`, which only closes each shell, and leaves GPU
memory to the OS (gotcha 26).

**`ImageEx` is a struct.** `Brutal.VulkanApi.Abstractions.ImageEx` is a `readonly struct` whose
`Dispose()` calls `_allocator.FreeAllocation(...)`; on `default(ImageEx)` the allocator is null and
`Dispose` throws `NullReferenceException`. `OffscreenRenderTarget` stores allocations as `ImageEx?`
and disposes with `?.Dispose()`, because its constructor calls `Resize()`, which first tears down
the "previous" allocation (gotcha 37).[^offscreen-target][^ksa-imageex]

**Same idea for ImGui textures.** `ImageTextureCache` removes an evicted image's ImGui descriptor
immediately and disposes the `SimpleVkTexture` three frames later.[^texture-cache]

## Lifecycle and timing

| Operation | Thread and phase |
|---|---|
| `Remove`, `TrySetGridSize` | Main thread, `frame:draw-ui` (manager UI or layouts dialog) |
| Deferred free, resize apply | Main thread, start of `InWorldTerminalManager.OnAfterGui` (`frame:draw-ui`), two frames later, after `WaitIdle` |
| Recording stop | Immediate: the `RenderTranslucencyPass` postfix (`frame:render`) iterates the live list and skips instances with a pending resize |
| Shutdown | `[StarMapUnload]`, after the game's own teardown: shells only, no device calls |

`WaitIdle` stalls the CPU until the GPU drains; purrTTY accepts it because it happens only on
destroy and resize.

## Key code

| Path | Role |
|---|---|
| [purrTTY.GameMod/InWorld/InWorldTerminalManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/InWorldTerminalManager.cs#L31-L37) | `TeardownDelayFrames` |
| [purrTTY.GameMod/InWorld/InWorldTerminalManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/InWorldTerminalManager.cs#L166-L186) | `Remove`: detach now |
| [purrTTY.GameMod/InWorld/InWorldTerminalManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/InWorldTerminalManager.cs#L401-L476) | Deferred free and the no-device shutdown |
| [purrTTY.GameMod/InWorld/InWorldTerminalInstance.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/InWorldTerminalInstance.cs#L221-L270) | Live resize |
| [purrTTY.GameMod/InWorld/InWorldTerminalInstance.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/InWorldTerminalInstance.cs#L308-L372) | `Dispose(freeGpu)` and teardown order |
| [purrTTY.GameMod/InWorld/OffscreenRenderTarget.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/OffscreenRenderTarget.cs#L38-L43) | `ImageEx?` holders |
| [purrTTY.GameMod/InWorld/PerFrameRenderer.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/PerFrameRenderer.cs#L268-L299) | Fence drain before freeing command buffers |

## Snippets

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

```csharp
// purrTTY.GameMod/InWorld/InWorldTerminalInstance.cs L264-L270
    private void ApplyResize(int width, int height)
    {
        _renderer.Device.WaitIdle();
        _target!.Resize(width, height);
        _ctx!.Resize(width, height);
        _quad!.RebindTarget();
    }
```

```csharp
// current/decomp/Brutal.VulkanApi.Abstractions/ImageEx.cs L165-L168
	public void Dispose()
	{
		_allocator.FreeAllocation(in AllocationInfo);
	}
```

## Game APIs touched

| API | Where | Purpose |
|---|---|---|
| `Renderer.Device.WaitIdle()` | `Core.Renderer` via `Program.GetRenderer()` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L557-L560)) | Drain before destroy |
| `ImageEx.Dispose()` | [current/decomp/Brutal.VulkanApi.Abstractions/ImageEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/ImageEx.cs#L165-L168) | Frees the VMA allocation; throws on `default` |
| `Program.Dispose` (`Device.WaitIdle`, renderer teardown) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1362-L1372) | Runs before StarMap's unload callbacks |

## Pitfalls and version notes

- Two frames is enough for KSA's frames in flight at this build; if you share objects with the game
  across more frames, size the delay from the swapchain or frame-resource count.
- Do not wrap shutdown device calls in try/catch as a fix; an access violation is not catchable.
  Skip the calls.
- Never dispose a `default` struct wrapper; use nullable holders.
- A render pass recreated with identical formats and samples is compatible with pipelines built on
  the old one; if you change a format during resize, rebuild those pipelines too.
- The shared quad pipeline (`SharedQuadResource`) is not disposed at shutdown either; the coordinator
  just drops the reference.
- KSA 2026.8.5.5168 (rev 5154) deleted `KSA.RenderTarget`/`Framebuffer`; the `ImageEx` struct trap
  appeared when purrTTY took over its own attachment allocation then.

## See also

- [Render-to-texture with a second ImGui context](./offscreen-imgui-context-render-to-texture.md)
- [World-space textured quad pipeline](./world-space-quad-pipeline-custom-shader.md)
- [Draw after the translucency pass](./draw-after-translucency-pass-postfix.md)
- [Kitty graphics as ImGui Vulkan textures](./kitty-graphics-as-imgui-vulkan-textures.md)
- [purrTTY overview](../overview.md)
- [Stop recording now, free two frames later after WaitIdle; nullable images; no device work in unload](../../../howto/rendering/vulkan-passes/free-vulkan-resources-safely/free-vulkan-resources-safely-1.md) - how-to article demonstrating this feature
- [Rebuild targets in place on a live size change](../../../howto/rendering/vulkan-passes/handle-renderer-rebuilds/handle-renderer-rebuilds-2.md) - how-to article demonstrating this feature

[^inworld-manager]: purrTTY.GameMod/InWorld/InWorldTerminalManager.cs
[^gotchas]: purrTTY docs/gotchas.md
[^inworld-instance]: purrTTY.GameMod/InWorld/InWorldTerminalInstance.cs
[^starmap-surveyer]: StarMap/GameSurveyer.cs
[^starmap-core]: StarMap.Core/StarMapCore.cs
[^offscreen-target]: purrTTY.GameMod/InWorld/OffscreenRenderTarget.cs
[^ksa-imageex]: Brutal.VulkanApi.Abstractions.ImageEx (decompiled, build 2026.10.10.5554)
[^texture-cache]: purrTTY.Display/Ghostty/ImageTextureCache.cs
