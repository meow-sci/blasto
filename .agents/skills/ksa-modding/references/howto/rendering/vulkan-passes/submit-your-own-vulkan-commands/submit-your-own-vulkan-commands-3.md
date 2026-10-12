---
type: How-To
title: Private transient pool for readback and thumbnails
description: Render the game's part-thumbnail pipeline one part at a time from a private command pool, wait on a fence and read the image back, all before OnPreRender
category: rendering
group: vulkan-passes
topic: submit-your-own-vulkan-commands
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/parts-now-thumbnail-generation]
techniques: [offscreen-viewport, vulkan-buffer, starmap-lifecycle]
hooks: [starmap StarMapBeforeGui, harmony-prefix KSA.Program.OnDrawUiConsole]
lifecycle: [frame:draw-ui, frame:pre-render]
game_types:
  - KSA.Rendering.ThumbnailCreator
  - KSA.Rendering.Thumbnails.ThumbnailDynamic
  - KSA.PartThumbnailViewport
  - KSA.Program
tags: [vulkan, thumbnails, readback, fence, offscreen-viewport]
---

Outcome: a part that a mod loads after boot gets a real picture in the editor's part browser,
framed and lit like the game's own thumbnails. The Vulkan recipe is the same as approach 1 (private
transient pool, one-shot command buffer, fence) with two additions: it reuses the game's thumbnail
viewport and camera uniform slice, and it can record an image-to-buffer copy for a readback. Demonstrated
by unscience's Parts Now ([Render part-browser thumbnails at runtime on KSA's offscreen thumbnail viewport](../../../../mods/unscience/features/parts-now-thumbnail-generation.md)). The thumbnail-framing
side is also covered in [Capture rendered frames](../capture-rendered-frames/overview.md) (approach 2).

## Background: how the game does it

KSA renders thumbnails in two places and exposes no runtime API for a single new part: boot thumbnails are
produced once for every part from the `Program` constructor by `ThumbnailCreator.PreparePartThumbnails`
[current/decomp/KSA.Rendering/ThumbnailCreator.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ThumbnailCreator.cs#L57), and the editor hover preview calls
`ThumbnailDynamic.Render` from `VehicleEditor.OnPreRender`. Both write the thumbnail viewport's
camera uniform slice with `ThumbnailDynamic.UpdateGlobalCameraData(IViewport, Camera)`:

```csharp
// current/decomp/KSA.Rendering.Thumbnails/ThumbnailDynamic.cs L272-L288
public static void UpdateGlobalCameraData(IViewport inViewport, Camera inCamera)
{
	int2 framebufferSize = inCamera.FramebufferSize;
	var (num3, num4) = (int2)(ref framebufferSize);
	float4x4.Invert(inCamera.MVP.view, out var result);
	float4x4.Invert(inCamera.MVP.projection, out var result2);
	ref UboCameraData reference = ref GlobalShaderBindings.CameraData(inViewport.ShaderSlot);
	reference.View = inCamera.MVP.view;
	reference.Projection = inCamera.MVP.projection;
	reference.ViewProjection = inCamera.MVP.viewProjection;
	reference.InverseView = result;
	reference.InverseProjection = result2;
	reference.ScreenWidth = num3;
	reference.ScreenHeight = num4;
	reference.AspectRatio = (float)num3 / (float)num4;
	reference.CameraPosition = new float4(float3.Pack(inCamera.PositionEcl), 1f);
}
```

The viewport is a `PartThumbnailViewport` [current/decomp/KSA/PartThumbnailViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartThumbnailViewport.cs#L6). Parts Now writes the slice,
submits and waits before the hover preview writes it again later in the frame, so the two never see each
other's camera.

## Lifecycle and timing

| Step | Hook | Frame point |
|---|---|---|
| Re-assert the camera, render two parts, fence-wait | `[StarMapBeforeGui]` (and a replay from a prefix on `Program.OnDrawUiConsole` while the HUD is hidden) | `frame:draw-ui`, before `OnPreRender` |
| Hover preview writes the same slice | `Program.OnPreRender` -> `Editor.OnPreRender` | `frame:pre-render`, afterwards |
| Slice copied into viewports that render this frame | `Program.UpdateRenderingResources` inside `Render` | `frame:render` |

The submit must stay where its fence wait is legal: main thread, outside the frame's own recording.
Moving it to `OnPreRender` or later would interleave with the other writers of the camera slice. See
[the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) (`frame:pre-render` and `frame:render` rows).

## Steps

1. Create a private transient, resettable command pool on `renderer.Graphics.Family` once per job.

   ```csharp
   // parts-now.lib/Runtime/PartThumbnailGenerator.cs L132-L137
   VkCommandPoolCreateInfo poolInfo = new VkCommandPoolCreateInfo
   {
       QueueFamilyIndex = renderer.Graphics.Family,
       Flags = VkCommandPoolCreateFlags.TransientBit | VkCommandPoolCreateFlags.ResetCommandBufferBit,
   };
   _commandPool = renderer.Device.CreateCommandPool(in poolInfo, null);
   ```

2. Each frame, re-assert the thumbnail camera before rendering: origin and identity, then
   `OnFrame`, then `ThumbnailDynamic.UpdateGlobalCameraData`.

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

3. Render at most two parts per frame, each in its own `try/catch`. Per part: frame it
   (`MoveRootPart`), collect the draws, create the image, and skip the part if there are zero draws (only
   `RecordPartRender` moves the image out of `Undefined`).

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

4. Allocate a primary buffer from your pool, record the game's `RecordPartRender` (plus an optional
   readback), submit with a fresh fence and wait on the spot:

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

5. Optional readback: add a `SampledReadF -> TransferSrc` transition, `CopyImageToBuffer` into a host-visible
   buffer and a transition back before `End`; after the fence, map the buffer. Parts Now logs the fraction
   of non-zero texels, because a thumbnail that drew nothing is exactly the clear colour.

## Complete example

| File | Role |
|---|---|
| [parts-now.lib/Runtime/PartThumbnailGenerator.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/PartThumbnailGenerator.cs) | Job, per-frame step, per-part submit |
| [parts-now.lib/Runtime/ThumbnailReadback.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/ThumbnailReadback.cs) | The diagnostic readback |

## Variations and gotchas

- `Camera.Unfollow()` with its default argument also clears the controlled vehicle
  [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L620); pass `changeControl: false` or the player loses their craft.
- Never move the thumbnail camera; `MoveRootPart` places the part in front of a camera at origin and identity.
- Each part is a full submit plus a blocking wait on the main thread; two per frame bounds the hitch.
- The game's own thumbnail code passes `Graphics.Index` as the pool's `QueueFamilyIndex`; Parts Now passes
  `Graphics.Family`.
- A thumbnail declared in XML has no image view until its turn; check `ImageView.IsNull()` before handing it
  to the ImGui backend.

## See also

- [Overview](overview.md)
- [Approach 1: one-shot command buffer and fence](submit-your-own-vulkan-commands-1.md)
- [Approach 2: fence ring](submit-your-own-vulkan-commands-2.md)
- [Approach 4: private images, runtime GLSL, fenced submit](submit-your-own-vulkan-commands-4.md)
- [Render part-browser thumbnails at runtime on KSA's offscreen thumbnail viewport](../../../../mods/unscience/features/parts-now-thumbnail-generation.md)
- [Capture rendered frames from KSA](../capture-rendered-frames/overview.md)
