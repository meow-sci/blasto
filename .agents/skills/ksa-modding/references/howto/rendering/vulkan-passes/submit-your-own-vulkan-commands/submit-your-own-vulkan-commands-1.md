---
type: How-To
title: Private one-shot command buffer and fence, with StagingPool used only for memory
description: Record buffer and texture uploads into a command buffer the mod owns, submit it with a fence from the main thread, and publish the result only after the fence
category: rendering
group: vulkan-passes
topic: submit-your-own-vulkan-commands
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/pebbles-asset-upload-submission]
techniques: [vulkan-buffer, starmap-lifecycle]
hooks:
  - starmap StarMapBeforeGui
  - harmony-prefix KSA.Program.OnDrawUiConsole
  - harmony-prefix KSA.Universe.ExecuteNextClothSolvers
lifecycle: [frame:draw-ui, frame:dispatch-solvers]
game_types:
  - RenderCore.StagingPool
  - Brutal.VulkanApi.Queue
  - RenderCore.VkUtils
  - RenderCore.SimpleVkTexture
  - KSA.Program
tags: [vulkan, command-buffer, fence, staging, upload, one-shot]
---

Outcome: your mod can upload a mesh, a texture or a buffer swap to the GPU at a moment the game
never planned for (a player clicks "reload", a file changes on disk) without touching the frame's
own command buffer. The recipe is a small disposable class: a transient command pool with one
primary command buffer, one fence, and a game `StagingPool` that is used only to allocate staging
memory. It is demonstrated by unscience's Pebbles sub-mod
([Cancellable one-shot GPU upload submission with publish-after-fence and retire-after-idle](../../../../mods/unscience/features/pebbles-asset-upload-submission.md)). Prerequisites: a StarMap mod with a
`[StarMapBeforeGui]` method (see [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)).

## Background: how the game does it

`StagingPool` [current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs#L7-L238) hands out staging buffers
(`AddStagingBuffer`) and primary command buffers (`NextCommandBuffer`) from its own command pool.
`Submit` sends every command buffer handed out since the last submit
(`_commandBufferIndex - _submitted`), and `Dispose` always runs `Submit()` and `Wait()`
first.[^ksa-stagingpool-cs] That is convenient for the game's own
one-shot loaders and dangerous for a mod: if an exception interrupts recording, disposing the pool
submits a half-recorded command buffer.

```csharp
// current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs L214-L218
public void Dispose()
{
	Submit();
	Wait();
	_device.DestroyCommandPool(_commandPool, null);
```

The helpers that fill a buffer or image take the command buffer as an argument.
`VkUtils.StageAndUploadToBuffer(stagingPool, targetBuffer, targetOffset, src, commandBuffer = default)`
[current/decomp/RenderCore/VkUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/VkUtils.cs#L82-L106) and `SimpleVkTexture.UploadData(stagingPool, commandBuffer, rawData, mipSizes, explicitLayer)`
[current/decomp/RenderCore/SimpleVkTexture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/SimpleVkTexture.cs#L374-L383) record their copies into the buffer you pass; only
when none is passed do they call `NextCommandBuffer` on the pool. Passing your own buffer is what keeps
the pool's auto-submit out of the picture.

`Queue.Submit` takes the queue's lock [current/decomp/Brutal.VulkanApi/Queue.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi/Queue.cs#L77-L83), and the game's frames
are submitted to the same `Renderer.Graphics` queue in `PostRender`, so a mod submission from the main
thread is safe beside it. `Renderer.MaxFramesInFlight` is 2 [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L28).

## Lifecycle and timing

| Step | Where | Why |
|---|---|---|
| Record, submit, wait on the fence | `[StarMapBeforeGui]` (`frame:draw-ui`, the prefix of `Program.OnDrawUiFrame`) | Main thread, outside the frame's own command recording (`Program.Render` runs later); the current frame has not referenced the new object yet |
| Publish (bindless slot, descriptor, swapped buffer) | immediately after `SubmitAndWait` returns | The fence guarantees the data is complete before any reader sees it |
| Destroy what was replaced | after `Device.WaitIdle()` | Readers of the old object are the game's frame command buffers, up to two of which can still be executing after `PostRender` submitted them |
| Same primitive, clutter path | Pebbles' Harmony prefix on `Universe.ExecuteNextClothSolvers` (`frame:dispatch-solvers`), where a pending clutter `Apply` uploads its GLB textures | Still on the main thread inside `PrepareFrame`, long before `Render` records this frame; the upload is complete before the clutter graph that samples it is published |

The unscience submod also replays its update from a prefix on `Program.OnDrawUiConsole` so uploads still
run while the HUD is hidden (`frame:draw-ui` is skipped entirely when `DrawUI` is false). See the
"Skipped entirely when the HUD is hidden" row of the draw-ui phase in
[the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Create `AssetUploadSubmission` (one per upload batch). The constructor builds the staging pool,
   a `Transient` command pool on the graphics queue family, one primary command buffer and an
   unsignaled fence, then begins the buffer with `OneTimeSubmitBit`. Any failure disposes what was created
   and rethrows.

   ```csharp
   // pebbles.lib/Assets/AssetUploadSubmission.cs L8-L43
   /// <summary>
   /// A cancellable private command buffer. StagingPool owns upload memory only: unlike its native
   /// command list, this buffer is never automatically submitted from Dispose after a recording error.
   /// </summary>
   public sealed class AssetUploadSubmission : IDisposable
   {
       private readonly Renderer _renderer;
       private VkCommandPool _pool;
       private VkFence _fence;
       private StagingPool? _staging;
       private bool _submitted, _completed;
       public CommandBuffer Command { get; private set; }
       public StagingPool Staging => _staging ?? throw new ObjectDisposedException(nameof(AssetUploadSubmission));

       public AssetUploadSubmission(Renderer renderer)
       {
           _renderer = renderer;
           try
           {
               _staging = renderer.Allocator.CreateStagingPool(renderer.Graphics, 1);
               var info = new VkCommandPoolCreateInfo
               {
                   QueueFamilyIndex = renderer.Graphics.Index,
                   Flags = VkCommandPoolCreateFlags.TransientBit
               };
               _pool = renderer.Device.CreateCommandPool(in info, null);
               Command = renderer.Device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo
               {
                   CommandPool = _pool, Level = VkCommandBufferLevel.Primary
               });
               var fenceInfo = new VkFenceCreateInfo();
               _fence = renderer.Device.CreateFence(in fenceInfo, null);
               Command.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
           }
           catch { Dispose(); throw; }
       }
   ```

2. Record the work with the game's helpers, always passing `submission.Command`:
   `VkUtils.StageAndUploadToBuffer(submission.Staging, buffer, offset, data, submission.Command)` for
   buffers, `SimpleVkTexture.UploadData(submission.Staging, submission.Command, pixels, mipSizes)` for
   images. Add your own barriers (the consumer below uses a transfer-to-vertex-input buffer barrier).

3. Submit and wait. `Queue.Submit` (the `QueueExtensions` overload with no semaphores) carries the fence;
   `WaitForFence(fence, -1)` blocks until the GPU is done, and anything other than `Success` throws.
   `Dispose` waits for the whole device only if a submission was made whose completion was never observed.

   ```csharp
   // pebbles.lib/Assets/AssetUploadSubmission.cs L45-L63
   public void SubmitAndWait()
   {
       Command.End();
       CommandBuffer command = Command;
       _submitted = true;
       _renderer.Graphics.Submit(default(Span<VkSemaphore>), default(Span<VkPipelineStageFlags>),
           new Span<CommandBuffer>(ref command), default(Span<VkSemaphore>), _fence);
       var result = _renderer.Device.WaitForFence(_fence, -1);
       if (result != VkResult.Success) throw new InvalidOperationException($"Asset upload completion failed: {result}.");
       _completed = true;
   }

   public void Dispose()
   {
       if (_submitted && !_completed) _renderer.Device.WaitIdle();
       if (!_pool.IsNull()) { _renderer.Device.DestroyCommandPool(_pool, null); _pool = default; }
       _staging?.Dispose(); _staging = null;
       if (!_fence.IsNull()) { _renderer.Device.DestroyFence(_fence, null); _fence = default; }
   }
   ```

4. Publish only after step 3, and retire the old object only after `Device.WaitIdle()`. This consumer
   rebuilds vertex buffers for a new UV mapping, uploads them in one submission, waits for idle, then swaps
   and frees the old buffers in `finally`:

   ```csharp
   // sphinx.lib/StaticModelResources.Textures.cs L15-L53
   public void UpdateTextureMapping(TextureMapping mapping)
   {
       if (_disposed || !ReferenceEquals(Owner, Program.GetRenderer()))
           throw new InvalidOperationException("The renderer changed; remove and re-place this static.");
       mapping.Validate();
       var replacements = new List<BufferEx>();
       try
       {
           using (var upload = new AssetUploadSubmission(Owner))
           {
               foreach (var draw in _draws)
               {
                   var vertices = mapping.Apply(draw.OriginalVertices);
                   var buffer = Buffer("Sphinx remapped vertices", ByteSize.Of<StaticVertex>(vertices.Length), VkBufferUsageFlags.VertexBufferBit);
                   replacements.Add(buffer);
                   VkUtils.StageAndUploadToBuffer(upload.Staging, buffer.VkBuffer, buffer.BindOffset, vertices.AsSpan(), upload.Command);
                   var barrier = KSA.Rendering.Utils.CreateBarrier(buffer.VkBuffer, VkAccessFlags.TransferWriteBit,
                       VkAccessFlags.VertexAttributeReadBit, VK.WHOLE_SIZE, ByteSize.Zero);
                   upload.Command.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.VertexInputBit,
                       VkDependencyFlags.None, default(ReadOnlySpan<VkMemoryBarrier>),
                       new ReadOnlySpan<VkBufferMemoryBarrier>(ref barrier), default(ReadOnlySpan<VkImageMemoryBarrier>));
               }
               upload.SubmitAndWait();
           }
           // Keep every old buffer alive until all viewports have finished using it.
           // Validation/upload failure above leaves the entire visible mesh unchanged.
           Owner.Device.WaitIdle();
           for (int i = 0; i < _draws.Count; i++)
           {
               var previous = _draws[i].Vertices;
               _draws[i].Vertices = replacements[i];
               replacements[i] = previous!.Value;
           }
       }
       finally
       {
           foreach (var buffer in replacements) buffer.Dispose();
       }
   }
   ```

5. Expect no log output on success. On failure the exception message is `Asset upload completion
   failed: <VkResult>.` or the original recording exception; nothing is half-submitted because the pool
   never owned the command buffer.

## Complete example

| File | Role |
|---|---|
| [pebbles.lib/Assets/AssetUploadSubmission.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Assets/AssetUploadSubmission.cs) | The 64-line primitive |
| [pebbles.lib/Assets/GlbTextures.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Assets/GlbTextures.cs#L68-L108) | Texture upload and bindless publication after the fence |
| [sphinx.lib/StaticModelResources.Textures.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/StaticModelResources.Textures.cs#L15-L53) | Buffer swap with idle wait |

## Variations and gotchas

- **Blocking.** Every submission stalls the main thread until the GPU finishes, and each retirement adds
  a full `WaitIdle`. Fine for user-driven edits; do not stream per frame.
- **Never call `NextCommandBuffer`.** Passing the command buffer matters: `StageAndUploadToBuffer` without
  it would re-enable the auto-submit-on-dispose behaviour this class exists to avoid.
- **Do not replace something already drawn from `[StarMapAfterGui]`.** Submitting is legal there, but
  retiring an object the current ImGui list references is not (see [free mod-owned Vulkan resources safely](../free-vulkan-resources-safely/overview.md)).
- **Queue family vs index.** The mod passes `renderer.Graphics.Index` as the pool's `QueueFamilyIndex`, as the game's
  own `PartSelectedRenderer` does [current/decomp/KSA/PartSelectedRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartSelectedRenderer.cs#L173-L176); `Queue.Family` is the Vulkan meaning of that field.
  They coincide when the graphics family is family 0.
- A fence wait of `-1` waits forever on a lost device.

## See also

- [Overview](overview.md)
- [Approach 2: command pool, fence ring and locked submit](submit-your-own-vulkan-commands-2.md)
- [Approach 3: transient pool for readback and thumbnails](submit-your-own-vulkan-commands-3.md)
- [Approach 4: private images, runtime GLSL, fenced submit](submit-your-own-vulkan-commands-4.md)
- [Cancellable one-shot GPU upload submission with publish-after-fence and retire-after-idle](../../../../mods/unscience/features/pebbles-asset-upload-submission.md)
- [Free mod-owned Vulkan resources without device loss](../free-vulkan-resources-safely/overview.md), [Capture rendered frames from KSA](../capture-rendered-frames/overview.md), [Render ImGui UI into a Vulkan texture for use in the 3D world](../../textures-and-decals/render-imgui-into-a-texture/overview.md), [Build a Vulkan pipeline compatible with KSA's scene render target](../build-a-vulkan-pipeline-for-the-scene-target/overview.md)

[^ksa-stagingpool-cs]: Brutal.VulkanApi.Abstractions.StagingPool (decompiled, build 2026.10.10.5554)
