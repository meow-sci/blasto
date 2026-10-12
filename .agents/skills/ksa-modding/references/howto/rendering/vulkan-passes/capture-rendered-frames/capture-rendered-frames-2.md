---
type: How-To
title: ThumbnailCreator framing plus ThumbnailDynamic per-frame submit, two parts per frame
description: Render runtime part thumbnails with the game's thumbnail viewport framing, a private transient command pool and a fence wait, submitted before OnPreRender
category: rendering
group: vulkan-passes
topic: capture-rendered-frames
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/parts-now-thumbnail-generation]
techniques: [offscreen-viewport, vulkan-buffer, starmap-lifecycle]
hooks: [starmap StarMapBeforeGui]
lifecycle: [frame:draw-ui, frame:pre-render]
game_types:
  - KSA.Rendering.ThumbnailCreator
  - KSA.Rendering.Thumbnails.ThumbnailDynamic
  - KSA.PartThumbnailViewport
  - KSA.Program
tags: [thumbnails, offscreen-viewport, readback, part-browser]
---

Outcome: a part loaded after boot appears in the editor's part browser with a real picture, framed and lit exactly like the game's own thumbnails, and (optionally) a readback tells you whether the picture drew anything. The Vulkan submission recipe (private transient pool,
one-shot buffer, fence wait, readback copy) is in [submit your own Vulkan commands](../submit-your-own-vulkan-commands/overview.md) (approach 3); this article covers the framing, the shared viewport and the part-thumbnail specifics. Demonstrated by unscience Parts Now
([Render part-browser thumbnails at runtime on KSA's offscreen thumbnail viewport](../../../../mods/unscience/features/parts-now-thumbnail-generation.md)). Requires StarMap.

## Background: how the game does it

KSA renders part thumbnails in two places. `ThumbnailCreator.PreparePartThumbnails(Loading)` [current/decomp/KSA.Rendering/ThumbnailCreator.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ThumbnailCreator.cs#L57) renders every part once from the `Program` constructor; `ThumbnailDynamic.Render(double)`
[current/decomp/KSA.Rendering.Thumbnails/ThumbnailDynamic.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Thumbnails/ThumbnailDynamic.cs#L167) renders the editor's hovered template every frame from `VehicleEditor.OnPreRender`. Both use `ThumbnailCreator`'s helpers: `AddPart` [current/decomp/KSA.Rendering/ThumbnailCreator.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ThumbnailCreator.cs#L179), `MoveRootPart` [current/decomp/KSA.Rendering/ThumbnailCreator.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ThumbnailCreator.cs#L192),
`CollectDraws` [current/decomp/KSA.Rendering/ThumbnailCreator.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ThumbnailCreator.cs#L126) and `CreateThumbnailReference` [current/decomp/KSA.Rendering/ThumbnailCreator.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ThumbnailCreator.cs#L153), and both write the viewport's camera uniform slice with
`ThumbnailDynamic.UpdateGlobalCameraData(IViewport, Camera)` [current/decomp/KSA.Rendering.Thumbnails/ThumbnailDynamic.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Thumbnails/ThumbnailDynamic.cs#L272-L288). The viewport is a `PartThumbnailViewport` [current/decomp/KSA/PartThumbnailViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartThumbnailViewport.cs#L6), a fixed-mode viewport with its own `BaseCamera`.

## Lifecycle and timing

| Step | Where |
|---|---|
| Re-assert camera, frame two parts, record, submit, fence wait | `[StarMapBeforeGui]` (`frame:draw-ui`), before `OnPreRender` |
| Hover preview writes the same camera slice | `Program.OnPreRender` -> `Editor.OnPreRender` (`frame:pre-render`), later in the frame |
| Slice copied to rendering viewports | `UpdateRenderingResources` in `Render` (`frame:render`) |

Parts Now writes the slice, submits and waits before the hover preview writes it again, and before `UpdateRenderingResources` overwrites it from the cached data, so each writer finishes its GPU work before the next write. Moving the submit later would interleave with them. See
[the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Per job:** create a `ThumbnailRenderer` (its constructor builds a pipeline layout and graphics pipeline from the part-model layouts), a private transient resettable command pool on `renderer.Graphics.Family`, and a root `ThumbnailPart` parented to the thumbnail viewport's camera.

2. **Per frame:** re-assert the camera, then render up to two parts, each in its own `try`/`catch`:

   ```csharp
   // parts-now.lib/Runtime/PartThumbnailGenerator.cs L177-L195
   // INVARIANT: never move this camera — move the root part. ThumbnailCreator.MoveRootPart
   // positions the ThumbnailPart in front of a camera parked at origin/identity, and
   // ThumbnailDynamic.Render assumes the same. This block only RE-ASSERTS origin/identity; it
   // must never set a non-zero camera transform.
   //
   // Unfollow MUST pass changeControl: false — the default overload nulls
   // Program.ControlledVehicle, which would drop the player's vessel mid-flight.
   try
   {
       camera.Unfollow(changeControl: false);
       camera.LocalPosition = double3.Zero;
       camera.LocalRotation = doubleQuat.Identity;
       camera.LocalScale = double3.One;
       camera.OnFrame(1.0 / 60.0);

       // Writes only this viewport's slice of the global camera UBO. Must happen AFTER
       // OnFrame (which recomputes MVP) and BEFORE the submit below.
       ThumbnailDynamic.UpdateGlobalCameraData(viewport, camera);
   }
   ```

3. **Per part:** skip sub-parts; frame the part with `MoveRootPart(root, template.Thumbnail, camera)` (it reads only `ModelTransform`, so a `<Thumbnail><ModelTransform>` in XML works before any image exists); collect the draws; create the image; and guard against zero draws, because only `RecordPartRender` moves the image out of `Undefined`.

   ```csharp
   // parts-now.lib/Runtime/PartThumbnailGenerator.cs L278-L322
   ThumbnailCreator.MoveRootPart(root, template.Thumbnail, camera);

   ThumbnailRenderResources resources = new ThumbnailRenderResources(
       renderer,
       thumbRenderer.PerInstanceDataDescriptorSetLayout,
       thumbRenderer.PerDrawDataDescriptorSetLayout,
       thumbRenderer.Sampler,
       ThumbnailRenderer.SIZE);

   try
   {
       ThumbnailCreator.CollectDraws(root, resources);

       // Logged for every part: a uniformly transparent (0,0,0,0) thumbnail is almost always
       // a zero draw count, i.e. no <PartModel> on the sub-parts or an unresolved mesh id.
       int drawCount = (int)resources.DrawCommandVector.ElementCount;
       Console.WriteLine($"parts-now: thumbnail '{template.Id}' collected {drawCount} draw(s).");

       if (drawCount == 0)
       {
           // Deliberately BEFORE the image is created. RecordPartRender is what transitions the
           // image out of VK_IMAGE_LAYOUT_UNDEFINED, so an image created for a part with no
           // draws would be sampled by the part browser while still undefined.
           Record(template.Id, false, "no draws collected");
           return;
       }

       // A <Thumbnail> that came from XML carries a ModelTransform but never had
       // CreateImageView called, so its ImageViewEx is default and Dispose() would NRE on a null
       // captured Device. Only dispose a reference that actually owns an image, and carry the
       // declared ModelTransform across so custom framing survives a regeneration.
       // (KSA's own ThumbnailCreator.CreateThumbnailImage drops it — we deliberately do not.)
       TransformReference? declaredTransform = template.Thumbnail?.ModelTransform;
       if (template.Thumbnail is { } previous && !previous.ImageView.IsNull())
       {
           previous.Dispose();
       }

       ThumbnailReference thumbnail =
           ThumbnailCreator.CreateThumbnailReference(renderer, "Thumbnail_" + template.Id);
       thumbnail.ModelTransform = declaredTransform;
       template.Thumbnail = thumbnail;

       resources.UpdateDescriptorSets();
       SubmitOne(renderer, thumbRenderer, template, thumbnail, resources);
   ```

4. **Submit and wait:** allocate a primary buffer from your pool, `RecordPartRender`, an optional readback, `End`, `Graphics.Submit(..., fence)`, `WaitForFence(fence, -1)`, destroy the fence:

   ```csharp
   // parts-now.lib/Runtime/PartThumbnailGenerator.cs L346-L372
   try
   {
       commandBuffer.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
       thumbRenderer.RecordPartRender(commandBuffer, thumbnail, resources, viewport, template.Id);

       bool readbackRecorded = DebugReadback && TryRecordReadback(renderer, commandBuffer, thumbnail, size);

       commandBuffer.End();

       VkFence fence = renderer.Device.CreateFence(new VkFenceCreateInfo(), null);
       VkResult waitResult;
       try
       {
           CommandBuffer submitRef = commandBuffer;
           renderer.Graphics.Submit(
               default,
               default,
               new Span<CommandBuffer>(ref submitRef),
               default,
               fence);

           waitResult = renderer.Device.WaitForFence(fence, -1);
       }
       finally
       {
           renderer.Device.DestroyFence(fence, null);
       }
   ```

5. **Readback (diagnostic).** Add a `SampledReadF -> TransferSrc` transition, `CopyImageToBuffer` into a host-visible buffer and a transition back; after the fence, count non-zero RGBA texels. A thumbnail that drew nothing is exactly the clear colour `(0,0,0,0)`, so 0 percent is a diagnosis.

6. Show the image: the editor part browser draws whatever `template.Thumbnail` holds next time it draws; check `ImageView.IsNull()` first, because an XML-declared thumbnail has no image until its turn.

## Complete example

| File | Role |
|---|---|
| [parts-now.lib/Runtime/PartThumbnailGenerator.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/PartThumbnailGenerator.cs) | Job, per-frame step, per-part submit |
| [parts-now.lib/Runtime/ThumbnailReadback.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/ThumbnailReadback.cs) | Readback and non-zero fraction |

## Variations and gotchas

- **Do not call `Camera.Unfollow()` with its default argument:** `Unfollow(bool changeControl = true)` [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L620) also drops the controlled vehicle. Pass `changeControl: false`.
- **Never move the thumbnail camera;** `MoveRootPart` places the part in front of a camera at origin and identity.
- **`ThumbnailRenderer.SIZE` reads the setting live** while the viewport was sized at boot; both are square, so the generator only warns when they differ.
- **Cost.** Each part is a submit plus blocking wait on the main thread; two per frame bounds the hitch. Each job builds and destroys a pipeline.
- **Hidden HUD.** In the supermod the update is replayed from a prefix on `OnDrawUiConsole`; the standalone host's jobs pause while the HUD is hidden.

## See also

- [Overview](overview.md)
- [Approach 1: transpiler before the final End](capture-rendered-frames-1.md)
- [Render part-browser thumbnails at runtime on KSA's offscreen thumbnail viewport](../../../../mods/unscience/features/parts-now-thumbnail-generation.md)
- [Record and submit your own Vulkan work: offscreen renders and one-shot uploads](../submit-your-own-vulkan-commands/overview.md), [Reserve or grow game GPU buffers for content added at runtime](../../meshes/grow-game-gpu-buffers-for-runtime-content/overview.md)
