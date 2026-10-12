---
type: How-To
title: Retire by MaxFramesInFlight + 1 frames
description: Replace a texture the game samples through a bindless slot and destroy the old image only after a counter-based retire queue has outlived every frame in flight, with no explicit idle
category: rendering
group: vulkan-passes
topic: free-vulkan-resources-safely
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/clutter-texture-override]
techniques: [deferred-destruction, vulkan-texture]
hooks: [starmap StarMapBeforeGui, starmap StarMapAfterOnFrame, harmony-prefix KSA.GameViewport.OnFrame]
lifecycle: [frame:draw-ui, frame:after-on-frame, frame:viewports]
game_types: [RenderCore.SimpleVkTexture, RenderCore.Systems.BindlessTextureLibrary, Core.Renderer]
tags: [vulkan, deferred-destruction, retire-queue, bindless, frames-in-flight]
---

Outcome: you can swap an image that the game (or your own recorded frames) keeps sampling without waiting for the GPU to idle. The replaced image goes on a counter-based retire queue and is destroyed `MaxFramesInFlight + 1`
ticks later. This is the cheaper alternative to a `WaitIdle` per change and fits any mod that rewrites a bindless slot between frames. Demonstrated by gatOS' clutter texture override
([Ground-clutter texture override](../../../../mods/gatos/features/clutter-texture-override.md)). Requires StarMap.

## Background: how the game does it

`BindlessTextureLibrary` is created with `UpdateAfterBind` and `PartiallyBound` binding flags [current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Systems/BindlessTextureLibrary.cs#L22), so a slot write between frames is picked up by the next submission.
Frames already recorded still hold the old descriptor contents they were recorded against, so the replaced image must outlive them. The renderer exposes `MaxFramesInFlight` (2) [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L28).

The game also ships the same idea as a public static class. `RetiredResourceQueue.Retire(IDisposable resource)` [current/decomp/KSA.Rendering/RetiredResourceQueue.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RetiredResourceQueue.cs#L26-L33) stamps the entry with `Renderer.FrameCount` and
`RetiredResourceQueue.CheckQueued()` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2314) destroys it once `FrameCount` has advanced past `MaxFramesInFlight` (or every frame fence is signalled) [current/decomp/KSA.Rendering/RetiredResourceQueue.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RetiredResourceQueue.cs#L108-L127); `RetireHandle(IHandleManager<int>, int)` frees a bindless handle the same way.
A mod can call it instead of keeping its own counter. The gatOS code below predates it and keeps its own tick-counted queue.

## Lifecycle and timing

| Step | Where |
|---|---|
| `bind` / `unbind` commands update a game-free store | the main-viewport `GameViewport.OnFrame` prefix (`frame:viewports`) |
| Tick: upload, rewrite the slot, retire the old image, drain | `StarMapBeforeGui` (`frame:draw-ui`) before the scene renders; with the HUD hidden, `StarMapAfterOnFrame`, after the frame was recorded, so the change first shows next frame |
| Destruction | `MaxFramesInFlight + 1` ticks after retiring |

All Vulkan work happens on the main thread in the tick. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Keep a retire queue owned by the consumer (game thread only). `Retire` never destroys inline: the slot referencing the image was just rewritten and frames already recorded may still sample it. The wait is
   `MaxFramesInFlight + 1`, falling back to a constant if the renderer is unavailable:

   ```csharp
   // gatOS.GameMod/Game/Ksa/Paint/UserTextureGpu.cs L155-L200
   ///     stops being referenced corrupts the device: the descriptor was only just rewritten, and
   ///     frames already recorded may still sample the old image. Each retired image therefore waits
   ///     out <c>MaxFramesInFlight + 1</c> ticks. This is the single real hazard in the feature.
   /// </summary>
   /// <remarks>Game thread only; each consumer owns its own instance.</remarks>
   internal sealed class RetireQueue
   {
       private readonly List<Retired> _retiring = [];

       private sealed record Retired(SimpleVkTexture Image, int TicksRemaining)
       {
           internal int TicksRemaining { get; set; } = TicksRemaining;
       }

       /// <summary>How many images are still waiting out their frames (the <c>retiring</c> status).</summary>
       internal int Count => _retiring.Count;

       /// <summary>
       ///     Queues one image for destruction. Never destroy inline: the slot referencing it was
       ///     only just rewritten, and frames already recorded may still sample it. Null is ignored,
       ///     so callers can pass "the image this replaced" without a branch.
       /// </summary>
       internal void Retire(SimpleVkTexture? image)
       {
           if (image is null)
               return;
           var frames = Program.GetRenderer()?.MaxFramesInFlight ?? 3;
           _retiring.Add(new Retired(image, frames + 1));
       }

       /// <summary>One tick: ages the queue and destroys whatever has outlived every frame in flight.</summary>
       internal void Drain()
       {
           if (_retiring.Count == 0)
               return;
           for (var i = _retiring.Count - 1; i >= 0; i--)
           {
               var retired = _retiring[i];
               if (--retired.TicksRemaining > 0)
                   continue;
               _retiring.RemoveAt(i);
               Dispose(retired.Image, "gatOS user texture disposal failed");
           }
       }

       /// <summary>
   ```

2. Bind the new image: capture the stock `ImageView` and the slot's bindless handle (keeping the first capture across re-binds), then call `Program.Instance.BindlessTextures.SetTexture(handle, ourImageView)`.

   ```csharp
   // gatOS.GameMod/Game/Ksa/Paint/ClutterTextureBridge.cs L219-L244
   private Override Apply(string targetId, TextureFile file, TextureBindMode mode)
   {
       if (Program.GetRenderer() is not { } renderer)
           throw new InvalidOperationException("the renderer is not running yet");
       if (Program.Instance?.BindlessTextures is not { } bindless)
           throw new InvalidOperationException("the bindless texture table is not available yet");
       if (ResolveStock(targetId) is not { } stock)
           throw new InvalidOperationException($"stock texture '{targetId}' is gone");

       var image = UserTextureGpu.Upload(renderer, file, _maxDimension,
           faithful: mode == TextureBindMode.Faithful, "gatos:paint/textures/");

       // Capture the pristine slot BEFORE the first swap. A re-bind over an existing override keeps
       // the original capture, so restore always returns to stock and never to a previous override.
       var stockView = _live.TryGetValue(targetId, out var previous) ? previous.StockView : stock.ImageView;
       var handle = _live.TryGetValue(targetId, out var prior) ? prior.Handle : stock.BindlessHandle;

       bindless.SetTexture(handle, image.ImageView);
       _retire.Retire(previous?.Image);

       var live = new Override(targetId, file.Name, file.Version, handle, stockView, image,
           image.Width, image.Height, image.MipMapCount, UserTextureGpu.VramBytes(image));
       _live[targetId] = live;
       return live;
   }

   ```

3. When replacing or unbinding, `Retire(oldImage)` (null is ignored, so callers can pass "the image this replaced" without a branch). Call `Drain()` once per tick.

4. Teardown: restore every slot to the stock view, wait for the graphics-and-compute queue to go idle, then `DrainAll()`, which destroys everything immediately and is legal only once the device is idle.

5. Reconcile instead of executing commands: compare a store revision counter with the last applied revision and return immediately when equal, so idle cost is one integer compare per frame.

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Paint/UserTextureGpu.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/UserTextureGpu.cs) | Decode, upload, `RetireQueue` |
| [gatOS.GameMod/Game/Ksa/Paint/ClutterTextureBridge.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Paint/ClutterTextureBridge.cs) | Slot binding and reconcile tick |

The upload and slot swap themselves are in [Upload an image or procedural pixels as a GPU texture](../../textures-and-decals/upload-an-image-as-a-vulkan-texture/overview.md) and [Replace a game texture or model texture with a user PNG at runtime](../../textures-and-decals/replace-a-game-texture-at-runtime/overview.md).

## Variations and gotchas

- **Never destroy a `VkImage` a recorded frame may still sample;** defer by frames in flight.
- **A slot holds one texture asset,** so an override changes every material that uses it.
- **Hidden HUD.** The tick then runs after the frame was recorded, so the slot change first shows in the next frame.
- **One consumer per queue.** Each consumer owns its own instance (game thread only).
- **No `WaitIdle` per change is the point;** the cost is a few frames of extra VRAM per replaced image.

## See also

- [Overview](overview.md)
- [Approach 1: stop recording, free later after WaitIdle](free-vulkan-resources-safely-1.md)
- [Approach 3: publish after a fence, retire after WaitIdle](free-vulkan-resources-safely-3.md)
- [Ground-clutter texture override](../../../../mods/gatos/features/clutter-texture-override.md)
- [Replace a game texture or model texture with a user PNG at runtime](../../textures-and-decals/replace-a-game-texture-at-runtime/overview.md), [Upload an image or procedural pixels as a GPU texture](../../textures-and-decals/upload-an-image-as-a-vulkan-texture/overview.md)
