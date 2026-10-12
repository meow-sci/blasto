---
type: How-To
title: Publish new GPU resources after a fence, retire old ones after WaitIdle
description: Swap buffers and images while frames are in flight by publishing only after the upload fence and destroying the old object only after Device.WaitIdle in the pre-GUI phase
category: rendering
group: vulkan-passes
topic: free-vulkan-resources-safely
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by:
  - mods/unscience/features/pebbles-asset-upload-submission
  - mods/unscience/features/sphinx-uv-texture-overrides
  - mods/unscience/features/pebbles-workshop-offscreen-preview
techniques: [vulkan-buffer, vulkan-texture, starmap-lifecycle]
hooks: [starmap StarMapBeforeGui]
lifecycle: [frame:draw-ui, frame:render, frame:dispatch-solvers]
game_types: [RenderCore.SimpleVkTexture, KSA.ImGuiBackendVulkanImpl, KSA.Program, Core.Renderer]
tags: [vulkan, wait-idle, fence, swap, publish, retire, imgui-texture]
---

Outcome: replace a live buffer or image (a static mesh's vertex buffers after a UV edit, a preview image the ImGui window displays) with a guarantee that nobody reads the old one when it is destroyed and nobody reads the new one before it is
complete. The rules are two: publish only after the fence of the upload that fills the new object, and retire the old object only after `Device.WaitIdle()` while no unsubmitted work in the current frame refers to it. Demonstrated by unscience
([Cancellable one-shot GPU upload submission with publish-after-fence and retire-after-idle](../../../../mods/unscience/features/pebbles-asset-upload-submission.md), [Live per-static UV scale and offset with shared-PNG texture overrides](../../../../mods/unscience/features/sphinx-uv-texture-overrides.md), [Independent offscreen Vulkan preview rendered before the GUI and shown as an ImGui image](../../../../mods/unscience/features/pebbles-workshop-offscreen-preview.md)). Requires StarMap.

## Background: how the game does it

`Queue.Submit` locks the queue [current/decomp/Brutal.VulkanApi/Queue.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi/Queue.cs#L77-L83) and frames are submitted on `Renderer.Graphics` in `PostRender`; with `MaxFramesInFlight = 2` [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L28) up to two submitted frames can still be executing. A fence on the mod's own
upload covers only the producer; the readers of the old object are the game's frame command buffers, which the mod's fence knows nothing about. `Device.WaitIdle()` drains all queues, including those readers. The ImGui backend
reads a registered image at `render:imgui` (the last step of `RenderGame`) [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4948-L4951).

## Lifecycle and timing

| Step | Where |
|---|---|
| Build the new object, submit the upload, wait on its fence | `StarMapBeforeGui` (`frame:draw-ui`), prefix of `Program.OnDrawUiFrame`; or at the solver handoff for clutter |
| `Device.WaitIdle()` | same call, once the new object is complete |
| Swap handles, dispose the old object | same call, immediately after the wait |
| Draw / sample the new object | later the same frame (`frame:render`, `render:imgui`) |

At the start of `OnDrawUiFrame` the current frame has recorded nothing yet, so after `WaitIdle` no reader of the old object exists. Doing the same work from `StarMapAfterGui` would risk retiring an image this frame's ImGui list already
references. See the draw-ui rows of [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Fill the new object through a private fenced submission** and keep the old one untouched ([submit your own Vulkan commands](../submit-your-own-vulkan-commands/overview.md), approach 1).

2. **Swap in one place after idle.** Either the whole mesh changes or none of it does: the replacements are disposed in `finally`, which after a successful swap holds the old buffers and after a failure the new ones.

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

3. **For an ImGui-referenced image, replace in the pre-GUI phase.** Render the next target first, then dispose the old one (after `RemoveTexture` of its descriptor), so a failed refresh never leaves the window without its previous image.

   ```csharp
   // pebbles.lib/Preview/WorkshopPreview.cs L56-L94
   /// <summary>
   /// Call in BeforeGui, before this frame can record an ImGui reference to Texture.
   /// Changed previews conservatively wait for prior GPU/UI readers. The preview submission
   /// finishes before publishing its image. Unchanged previews do no GPU work and never wait.
   /// </summary>
   public void Render(WorkshopView view, int width, int height)
   {
       ObjectDisposedException.ThrowIf(_disposed, this);
       if (_failedRefresh || (_pending == null && _scene == null)) return;
       var matrix = WorkshopMath.ViewProjection(view, width, height);
       // Keep the logical panel aspect for overlay/picking, even if GPU resolution is capped.
       width = Math.Clamp(width, 64, 1536); height = Math.Clamp(height, 64, 1536);
       var eye = view.Eye;
       if (!Finite(eye) || !Finite(matrix)) { Status = "Preview camera contains invalid values."; _ready = false; return; }
       if (_ready && _pending == null && _target?.Width == width && _target.Height == height &&
           matrix == _lastMatrix && eye == _lastEye) return;

       PreviewScene? nextScene = null;
       PreviewTarget? nextTarget = null;
       try
       {
           _renderer ??= Program.GetRenderer();
           // Retires submitted ImGui consumers too, unlike a fence for the preview producer alone.
           // BeforeGui scheduling is essential: there must be no unsubmitted current-frame UI use.
           _renderer.Device.WaitIdle();
           _pipeline ??= new PreviewPipeline(_renderer);
           if (_pending != null) nextScene = new PreviewScene(_renderer, _pipeline, _pending);
           var drawScene = nextScene ?? _scene!;
           bool resize = _target == null || _target.Width != width || _target.Height != height;
           if (resize) nextTarget = new PreviewTarget(_renderer, width, height);
           var drawTarget = nextTarget ?? _target!;
           drawTarget.Render(_pipeline, drawScene, matrix, eye);
           if (nextScene != null) { _scene?.Dispose(); _scene = nextScene; nextScene = null; }
           if (nextTarget != null) { _target?.Dispose(); _target = nextTarget; nextTarget = null; }
           _pending = null;
           _lastMatrix = matrix; _lastEye = eye;
           _ready = true;
           Status = "Studio preview · terrain lighting and placement are evaluated on Apply.";
       }
   ```

4. **Order dependents.** The preview borrows textures from a shared cache; the owner retires the preview (with `WaitIdle`) before releasing the cache.

   ```csharp
   // pebbles.lib/PebblesSubmod.cs L20-L29
   public void Update(double dt)
   {
       _controller.Update();
       _workshop.Update();
       if (_releaseImports && !_controller.NeedsHooks && _controller.Faults.Count == 0)
       {
           _releaseImports = false; // A failed native release is reported, never retried every frame.
           _workshop.Release(); _workshop.Update();
           _assets.ReleaseGlbImports(); _glbOptions = [];
       }
   ```

5. Dispose the upload helper itself: it waits for the device only if a submission was made whose completion was never observed.

## Complete example

| File | Role |
|---|---|
| [pebbles.lib/Assets/AssetUploadSubmission.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Assets/AssetUploadSubmission.cs) | Fenced private submission |
| [sphinx.lib/StaticModelResources.Textures.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/sphinx.lib/StaticModelResources.Textures.cs) | Buffer swap after idle |
| [pebbles.lib/Preview/WorkshopPreview.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/WorkshopPreview.cs) | Replace-then-dispose of scene and target |
| [pebbles.lib/Preview/PreviewTarget.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pebbles.lib/Preview/PreviewTarget.cs#L55-L102) | `RemoveTexture` before destroying views and images |

## Variations and gotchas

- **Blocking.** A `WaitIdle` per edit means a drag issues one per frame; large models hitch continuously. Approach 2 avoids the drain at the cost of delayed destruction.
- **Owner check.** After a renderer rebuild the owner check fails; the entry must be removed and placed again (see [Recreate mod render targets when KSA rebuilds its renderer](../handle-renderer-rebuilds/overview.md)).
- **Never retire from `[StarMapAfterGui]`** an object this frame's ImGui list may reference.
- **Uploads wait for the solvers** in the unscience queue drain, so a texture edit also waits for the in-flight physics step even though it never touches physics.
- **At exit** the same calls act on a destroyed device; see approach 1.

## See also

- [Overview](overview.md)
- [Approach 1: stop recording, free later after WaitIdle](free-vulkan-resources-safely-1.md)
- [Approach 2: retire by MaxFramesInFlight + 1 frames](free-vulkan-resources-safely-2.md)
- [Cancellable one-shot GPU upload submission with publish-after-fence and retire-after-idle](../../../../mods/unscience/features/pebbles-asset-upload-submission.md), [Live per-static UV scale and offset with shared-PNG texture overrides](../../../../mods/unscience/features/sphinx-uv-texture-overrides.md), [Independent offscreen Vulkan preview rendered before the GUI and shown as an ImGui image](../../../../mods/unscience/features/pebbles-workshop-offscreen-preview.md)
- [Record and submit your own Vulkan work: offscreen renders and one-shot uploads](../submit-your-own-vulkan-commands/overview.md), [Show a PNG or runtime-generated image in an ImGui window](../../textures-and-decals/show-an-image-in-imgui/overview.md)
