---
type: Mod Feature
title: Cancellable one-shot GPU upload submission with publish-after-fence and retire-after-idle
description: Pebbles uploads CPU-built buffers and textures through a private one-shot command buffer and fence, using a StagingPool only for memory so failed recordings are never auto-submitted, publishing after the fence and freeing after Device.WaitIdle
mod: unscience
submod: pebbles
category: rendering
techniques: [vulkan-buffer, vulkan-texture, starmap-lifecycle, harmony-prefix]
hooks:
  - starmap StarMapBeforeGui (via unscience ISubmod.Update; Workshop preview uploads and draws, Sphinx placements and texture edits)
  - harmony-prefix KSA.Universe.ExecuteNextClothSolvers (GLB texture uploads while a clutter Apply builds its private graph)
  - harmony-prefix KSA.Program.OnDrawUiConsole (HiddenUiFrameHook replays ISubmod.Update while the HUD is hidden)
lifecycle: [frame:draw-ui, frame:dispatch-solvers]
game_types:
  - Core.Renderer
  - Core.KSADeviceContextEx
  - Brutal.VulkanApi.Queue
  - Brutal.VulkanApi.Abstractions.StagingPool
  - Brutal.VulkanApi.Abstractions.QueueExtensions
  - Brutal.VulkanApi.Abstractions.DeviceExtensions
  - RenderCore.VkUtils
  - RenderCore.SimpleVkTexture
  - RenderCore.Systems.BindlessTextureLibrary
  - KSA.Program
  - KSA.PartSelectedRenderer
  - KSA.ImGuiBackendVulkanImpl
tags:
  - vulkan
  - upload
  - staging
  - command-buffer
  - fence
  - synchronization
  - resource-lifetime
  - textures
  - buffers
---

Pebbles and Sphinx create GPU data at moments the game never planned for: a GLB texture when the
player refreshes the Workshop preview, preview vertex buffers, the preview's own draw, a clutter
texture during the solver-handoff commit, a Sphinx static's buffers when it is placed or its UVs
change. None of this belongs in the game's per-frame command buffer. `AssetUploadSubmission` is the
64-line primitive all of them share: a private transient command pool with one primary command
buffer and a fence, plus a game `StagingPool` used only as a staging-memory allocator.[^pebbles-readme]

Mechanism in one line: record the copies (or a draw) into the private buffer, `SubmitAndWait` on the
graphics queue, then publish the new object to the renderer (bindless slot, ImGui descriptor, draw
list entry, swapped buffer), and destroy anything it replaces only after `Device.WaitIdle()`, all on
the main thread at a point where the current frame has not yet referenced it.[^asset-upload-submission]

## How it works

**Construction** ([pebbles.lib/Assets/AssetUploadSubmission.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Assets/AssetUploadSubmission.cs#L22-L43)).

1. `renderer.Allocator.CreateStagingPool(renderer.Graphics, 1)`: a game pool that owns staging
   buffers (and, unused here, its own command pool with one pre-allocated buffer).
2. A `VkCommandPool` with `TransientBit`, one primary `CommandBuffer`, and an unsignaled `VkFence`.
3. `Command.Begin(OneTimeSubmitBit)`. Any failure disposes what was created and rethrows.

**Recording.** Callers pass `submission.Staging` and `submission.Command` to the game's own helpers:
`VkUtils.StageAndUploadToBuffer(staging, buffer, offset, data, command)` maps a new staging buffer
from the pool and records a `CopyBuffer` into the command buffer it is given (it only calls
`stagingPool.NextCommandBuffer()` when none is passed); `SimpleVkTexture.UploadData(staging, command,
data, mipSizes)` stages the pixels, records `UploadBufferToImage` and, for a single supplied level,
`GenerateMipmaps`. Callers add their own buffer barriers.[^ksa-vk-utils][^ksa-simple-vk-texture]

**Why the pool never owns the command buffer.** `StagingPool.Dispose()` always calls `Submit()` and
`Wait()`. `Submit` sends every command buffer handed out by `NextCommandBuffer` that has not been
submitted yet (`_commandBufferIndex - _submitted`). A pool whose buffer was half-recorded when an
exception hit would therefore submit garbage on dispose. Because `AssetUploadSubmission` never calls
`NextCommandBuffer`, `_commandBufferIndex` stays 0, the pool's dispose submits nothing, and a failed
recording is simply thrown away with the private command pool. The clutter commit, which must hand
native constructors a pool command buffer, instead cancels by resetting the private
`_commandBufferIndex` by reflection (see
[ground clutter replacement](./pebbles-ground-clutter-replacement.md)); this class needs no
reflection.[^ksa-staging-pool]

**Submit and wait** ([pebbles.lib/Assets/AssetUploadSubmission.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Assets/AssetUploadSubmission.cs#L45-L63)).
`End`, `Queue.Submit` (the `QueueExtensions` overload with no semaphores, one command buffer and the
fence; the underlying `Queue.Submit` takes the queue's lock), then `Device.WaitForFence(fence, -1)`;
anything but `VK_SUCCESS` throws. `Dispose` waits for the whole device only if a submission was made
but its completion was never observed, then destroys the command pool (freeing the buffer), disposes
the staging pool (freeing staging buffers) and destroys the fence.[^ksa-queue-extensions][^ksa-queue][^ksa-device-extensions]

**Publish after the fence, retire after idle.** Every consumer follows the same two rules:

| Consumer | Records | Published only after `SubmitAndWait` | Replaced or freed |
|---|---|---|---|
| `GlbTexture.Upload` | `SimpleVkTexture.UploadData`, one mip, GPU mip chain | `BindlessTextures.AddTexture` and the reflected `BindlessHandle` | `GlbMaterials.Dispose` waits on each owning renderer's device, then frees slot and image |
| `ImportedPngTexture` (Sphinx PNG overrides) | One `GlbTexture.Upload` for color, a second for opacity only if some pixel has alpha below 255 | Same | `WaitIdle`, then `Release` |
| `PreviewScene` | Vertex and index buffers, transfer-to-vertex-input barriers | The scene becomes drawable | On failure `WaitIdle` then dispose; normally replaced after the next preview render |
| `PreviewTarget.Render` | Layout transitions and the preview draw itself | `ImGuiBackend.Vulkan.AddTexture` on first use | Old target disposed after the new one rendered |
| Sphinx `StaticModelResources` | Vertex, index and material buffers per primitive | The entry's `Model` is assigned | `Dispose` calls `WaitIdle` first |
| Sphinx `UpdateTextureMapping` | Remapped vertex buffers | `SubmitAndWait`, then `WaitIdle`, then swap | Previous buffers disposed in `finally` |

Sources: [pebbles.lib/Assets/GlbTextures.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Assets/GlbTextures.cs#L68-L108),
[pebbles.lib/Assets/GlbMaterials.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Assets/GlbMaterials.cs#L40-L52),
[pebbles.lib/Assets/ImportedPngTexture.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Assets/ImportedPngTexture.cs),
[pebbles.lib/Preview/PreviewScene.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/PreviewScene.cs#L31-L86),
[pebbles.lib/Preview/PreviewTarget.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/PreviewTarget.cs#L55-L102),
[sphinx.lib/StaticModelResources.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/StaticModelResources.cs#L95-L123) and
[sphinx.lib/StaticModelResources.Textures.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/StaticModelResources.Textures.cs#L15-L53).[^glb-textures][^glb-materials][^imported-png-texture][^preview-scene][^preview-target][^sphinx-static-model-resources][^sphinx-static-model-textures]

The fence makes the new object complete before anything can read it; `WaitIdle` (rather than this
submission's fence) is needed for retirement because the readers of the old object are the game's
own frame command buffers, up to `MaxFramesInFlight` (2) of which can still be executing after
`PostRender` submitted them on the same `Renderer.Graphics` queue.[^core-renderer][^ksa-program]

## Lifecycle and timing

| Caller | Hook | Frame point (build 5554) |
|---|---|---|
| Workshop `Refresh` (GLB texture uploads) and `Render` (scene, target, draw) | `ISubmod.Update` from unscience `[StarMapBeforeGui]`, prefix of `Program.OnDrawUiFrame` (`Program.cs` L2267), or the hidden-HUD replay in `OnDrawUiConsole` (L2275) | `frame:draw-ui`, before any GUI code this frame |
| Clutter Apply: `ClutterGraph.Material` -> `ClutterAssets.ResolveTexture` -> `GlbImportLibrary.ResolveTexture` -> `GlbMaterials.ResolveTexture` -> `GlbTexture.Upload` | `Universe.ExecuteNextClothSolvers` prefix (L2217), after `Quiesce()` (solvers waited, `Device.WaitIdle()`) | `frame:dispatch-solvers` |
| Sphinx placement and texture edits | UI only enqueues an action; `SphinxSubmod.Update` runs the queue (BeforeGui) | `frame:draw-ui`, before GUI code |
| Frame submission it must not race | `PostRender` -> `Renderer.TrySubmitFrame` on `Renderer.Graphics` (L2343) | `frame:post-render` |

All callers are on the main thread and outside the game's command recording (which happens later in
`Program.Render`), so a blocking submit is legal there. The choice of the pre-GUI phase is about
publication, not submission: at `OnDrawUiFrame` the current frame's ImGui list and render passes do not
exist yet, so a texture or descriptor swapped in now is the one the whole frame will see, and nothing
recorded this frame can refer to the object being retired. The [GLB material conversion](./pebbles-glb-material-conversion.md)
feature documents the same rule from the texture side ("a texture must not be destroyed or replaced
after this frame's ImGui list may reference it").[^workshop-preview][^sphinx-submod][^clutter-graph]

## Key code

| Path | Role |
|---|---|
| [pebbles.lib/Assets/AssetUploadSubmission.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Assets/AssetUploadSubmission.cs) | The primitive |
| [pebbles.lib/Assets/GlbTextures.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Assets/GlbTextures.cs#L68-L108) | Texture upload, bindless publication after the fence |
| [pebbles.lib/Assets/GlbMaterials.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Assets/GlbMaterials.cs) | Lazy upload cache; device-aware release |
| [pebbles.lib/Preview/PreviewScene.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/PreviewScene.cs#L31-L86) | Batched buffer uploads with barriers |
| [pebbles.lib/Preview/PreviewTarget.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/PreviewTarget.cs#L55-L102) | Same class used for an offscreen draw |
| [sphinx.lib/StaticModelResources.Textures.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/StaticModelResources.Textures.cs) | Build, wait, idle, swap, free |
| [pebbles.lib/README.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/README.md#L86-L92) | Authors' note on the shared rendering utility |

## Snippets

Construction: a staging pool for memory only, and a private command buffer and fence:

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

Submit, wait, and a dispose that never frees in-flight memory:

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

Swapping live buffers at a safe point (Sphinx consumer):

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

What the game's pool does on dispose (the behavior being avoided):

```csharp
// current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs L214-L218
	public void Dispose()
	{
		Submit();
		Wait();
		_device.DestroyCommandPool(_commandPool, null);
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `StagingPool.AddStagingBuffer`, `NextCommandBuffer`, `Submit`, `Wait`, `Dispose`, `_commandBufferIndex`, `_submitted` | [current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/StagingPool.cs#L35-L238) | Staging memory; auto-submit on dispose |
| `Queue.Family`, `Queue.Index`, `Queue.Submit` (locked) | [current/decomp/Brutal.VulkanApi/Queue.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi/Queue.cs#L10-L83) | Queue identity and thread-safe submit |
| `QueueExtensions.Submit(Queue, waits, stages, buffers, signals, fence)` | [current/decomp/Brutal.VulkanApi.Abstractions/QueueExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/QueueExtensions.cs#L7-L33) | Single `VkSubmitInfo` with a fence |
| `DeviceExtensions.WaitForFence` | [current/decomp/Brutal.VulkanApi.Abstractions/DeviceExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/DeviceExtensions.cs#L291-L294) | Blocking completion |
| `VkUtils.StageAndUploadToBuffer` | [current/decomp/RenderCore/VkUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/VkUtils.cs#L82-L106) | Buffer copy into the given command buffer |
| `SimpleVkTexture.UploadData` | [current/decomp/RenderCore/SimpleVkTexture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/SimpleVkTexture.cs#L374-L383) | Image upload and GPU mip generation |
| `KSADeviceContextEx.Graphics`, `Allocator`, `Device` | [current/decomp/Core/KSADeviceContextEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/KSADeviceContextEx.cs#L63-L69) | Graphics queue is `GetQueue(firstQueueFamilyWithFlag, 0)` (L391) |
| `Renderer.TrySubmitFrame`, `MaxFramesInFlight` | [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L500-L516) | Frames use the same `Graphics` queue |
| `PartSelectedRenderer` command pool creation | [current/decomp/KSA/PartSelectedRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartSelectedRenderer.cs#L173-L176) | The game's own `QueueFamilyIndex = renderer.Graphics.Index` pattern |

## Pitfalls and version notes

- **Queue family vs queue index.** The command pool is created with `QueueFamilyIndex =
  renderer.Graphics.Index`. `Queue.Index` is the queue's index within its family (always 0 for
  `Graphics`); the family is `Queue.Family` (`firstQueueFamilyWithFlag`). They coincide only when the
  graphics family is family 0, which is the common case. The game's `PartSelectedRenderer` uses the
  same expression, so behavior matches stock; a GPU whose graphics family is not 0 would get a pool for
  the wrong family (not observed; unverified on such hardware).
- **Blocking.** Every submission stalls the main thread until the GPU finishes it, and retirements add
  a full `Device.WaitIdle()`. Fine for user-driven edits; not for per-frame streaming.
- **Never call it from AfterGui UI code to replace something already drawn.** Submitting is legal
  there, but publishing or retiring an object the current ImGui list already references is not; keep
  uploads and swaps in `Update`/BeforeGui or at the solver handoff.
- **Passing the command buffer matters.** `VkUtils.StageAndUploadToBuffer` without the `commandBuffer`
  argument would call `NextCommandBuffer` on the pool, re-enabling the auto-submit-on-dispose behavior
  this class exists to avoid.
- **`WaitForFence` with timeout -1** waits forever on a lost device; the result is checked, but a hang
  is not recoverable.
- The authors renamed this class from a private `PreviewSubmission` when Sphinx started sharing
  it.[^pebbles-readme]

## See also

- [GLB material conversion](./pebbles-glb-material-conversion.md) (bindless texture upload built on this)
- [Workshop offscreen 3D preview](./pebbles-workshop-offscreen-preview.md) (uploads and the draw)
- [Per-celestial ground clutter replacement](./pebbles-ground-clutter-replacement.md) (the reflection-based cancel of a native pool)
- [Sphinx UV mapping and PNG texture overrides](./sphinx-uv-texture-overrides.md) and [Sphinx GLB statics](./sphinx-glb-statics-placement.md)
- [Pebbles sub-mod](../submods/pebbles.md) and [mod overview](../overview.md)
- [Publish new GPU resources after a fence, retire old ones after WaitIdle](../../../howto/rendering/vulkan-passes/free-vulkan-resources-safely/free-vulkan-resources-safely-3.md) - how-to article demonstrating this feature
- [Private one-shot command buffer and fence, with StagingPool used only for memory](../../../howto/rendering/vulkan-passes/submit-your-own-vulkan-commands/submit-your-own-vulkan-commands-1.md) - how-to article demonstrating this feature

[^pebbles-readme]: pebbles.lib/README.md
[^asset-upload-submission]: pebbles.lib/Assets/AssetUploadSubmission.cs
[^ksa-vk-utils]: RenderCore.VkUtils (decompiled, build 2026.10.10.5554)
[^ksa-simple-vk-texture]: RenderCore.SimpleVkTexture (decompiled, build 2026.10.10.5554)
[^ksa-staging-pool]: Brutal.VulkanApi.Abstractions.StagingPool (decompiled, build 2026.10.10.5554)
[^ksa-queue-extensions]: Brutal.VulkanApi.Abstractions.QueueExtensions (decompiled, build 2026.10.10.5554)
[^ksa-queue]: Brutal.VulkanApi.Queue (decompiled, build 2026.10.10.5554)
[^ksa-device-extensions]: Brutal.VulkanApi.Abstractions.DeviceExtensions (decompiled, build 2026.10.10.5554)
[^glb-textures]: pebbles.lib/Assets/GlbTextures.cs
[^glb-materials]: pebbles.lib/Assets/GlbMaterials.cs
[^imported-png-texture]: pebbles.lib/Assets/ImportedPngTexture.cs
[^preview-scene]: pebbles.lib/Preview/PreviewScene.cs
[^preview-target]: pebbles.lib/Preview/PreviewTarget.cs
[^sphinx-static-model-resources]: sphinx.lib/StaticModelResources.cs
[^sphinx-static-model-textures]: sphinx.lib/StaticModelResources.Textures.cs
[^core-renderer]: Core.Renderer (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^workshop-preview]: pebbles.lib/Preview/WorkshopPreview.cs
[^sphinx-submod]: sphinx.lib/SphinxSubmod.cs
[^clutter-graph]: pebbles.lib/Runtime/ClutterGraph.cs
