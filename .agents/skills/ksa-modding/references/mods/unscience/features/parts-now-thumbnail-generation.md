---
type: Mod Feature
title: Render part-browser thumbnails at runtime on KSA's offscreen thumbnail viewport
description: Parts Now gives runtime-loaded parts real editor thumbnails by combining ThumbnailCreator's boot framing with ThumbnailDynamic's per-frame submit, rendering two parts per frame on Program.ThumbnailViewport with a private command pool and one fence wait per part
mod: unscience
submod: parts-now
category: rendering
techniques: [vulkan-texture, vulkan-buffer, starmap-lifecycle, offscreen-viewport]
hooks:
  - starmap StarMapBeforeGui (ISubmod.Update -> RuntimeModLoader Thumbnails state, 2 parts per frame)
  - harmony-prefix KSA.Program.OnDrawUiConsole (unscience HiddenUiFrameHook replays Update while the HUD is hidden)
lifecycle: [frame:draw-ui]
game_types:
  - KSA.Program
  - KSA.PartThumbnailViewport
  - KSA.IViewport
  - KSA.Camera
  - KSA.PartTemplate
  - KSA.Rendering.ThumbnailCreator
  - KSA.Rendering.Thumbnails.ThumbnailRenderer
  - KSA.Rendering.Thumbnails.ThumbnailRenderResources
  - KSA.Rendering.Thumbnails.ThumbnailPart
  - KSA.Rendering.Thumbnails.ThumbnailReference
  - KSA.Rendering.Thumbnails.ThumbnailDynamic
  - KSA.GlobalShaderBindings
  - KSA.VehicleEditor
  - Core.Renderer
tags:
  - thumbnails
  - offscreen-render
  - vulkan
  - command-buffer
  - fence
  - part-browser
  - vehicle-editor
  - camera-ubo
---

A part that Parts Now loads after boot appears in the vehicle editor's part browser with a real
picture, framed and lit the same way KSA frames its own parts. KSA has no runtime API for this: boot
thumbnails are rendered once, for every part at the same time, from the `Program` constructor. Parts Now
rebuilds the same result one part at a time while the game is running.[^thumbnail-generator][^parts-now-readme]

Mechanism in one line: during the loader's `Thumbnails` state, each frame re-asserts the thumbnail
viewport's camera, writes that viewport's camera UBO slice, then for two parts builds the framed
`ThumbnailPart` tree. For each of the two it collects draws, creates a fresh `ThumbnailReference`
image, and records `ThumbnailRenderer.RecordPartRender` into a one-shot command buffer that it
submits to the graphics queue and waits on.

## How it works

KSA renders thumbnails in two places, and Parts Now reuses one piece of each.[^ksa-thumbnail-creator][^ksa-thumbnail-dynamic]

| | `ThumbnailCreator.PreparePartThumbnails` (boot) | `ThumbnailDynamic.Render` (editor hover preview) | Parts Now `PartThumbnailGenerator` |
|---|---|---|---|
| Called from | `Program` constructor (`Program.cs` L1270 → L1340-L1344) | `VehicleEditor.OnPreRender` (L5425-L5430), from `Program.OnPreRender` (L2437) | Loader `Thumbnails` state in `ISubmod.Update` |
| Parts | Every non-sub-part in `ModLibrary.AllParts` | The one hovered template | The job's new top-level parts, 2 per frame |
| Camera setup | `Unfollow()`, origin/identity, `OnFrame`, then `Program.UpdateShaderData` + `UpdateRenderingResources` | `camera.OnFrame(0)`, then the static `ThumbnailDynamic.UpdateGlobalCameraData` | Origin/identity with `Unfollow(changeControl: false)`, `OnFrame(1/60)`, `UpdateGlobalCameraData` only |
| Framing | `AddPart` + `MoveRootPart` + `CollectDraws` | Same, plus a spin rotation | Same as boot |
| Image | `CreateThumbnailImage` (disposes the old one, drops any XML `ModelTransform`) | One persistent image | `CreateThumbnailReference`, keeping the XML `ModelTransform` |
| Submit | All parts in one command buffer, one fence wait | One buffer, async fence retired on a later frame | One buffer and fence per part, waited immediately |

**Per job** (`Begin`): create a `ThumbnailRenderer` (its constructor builds a pipeline layout and a
graphics pipeline from `PartModelRenderer.ColorData` layouts), a private transient, resettable command
pool on `renderer.Graphics.Family`, and a root `ThumbnailPart` parented to the camera of
`Program.ThumbnailViewport`. It also warns when `ThumbnailRenderer.SIZE` no longer matches the viewport
size.[^ksa-thumbnail-renderer]

**Per frame** (`Step`): re-assert the camera, then render up to `PartsPerFrame = 2` parts, each inside
its own `try/catch` so one bad part (an unresolved mesh, a material missing a channel) is recorded and
the batch continues.

**Per part** (`RenderOne` and `SubmitOne`):

1. Skip sub-parts (`ThumbnailCreator.AddPart` only walks `SubPartInstances`, so a sub-part has nothing
   to draw). Skip a part whose root has no children.
2. `MoveRootPart(root, template.Thumbnail, camera)`. It only reads `ModelTransform` from the
   reference, so a `<Thumbnail><ModelTransform>` declared in XML works before any image exists.
3. New `ThumbnailRenderResources` (per-draw and per-instance vectors plus its own depth image), then
   `CollectDraws`. The draw count is logged for every part. **Zero draws means no image is created**,
   because only `RecordPartRender` moves the image out of `VK_IMAGE_LAYOUT_UNDEFINED`.
4. Dispose the previous thumbnail only if it owns an image (`!ImageView.IsNull()`). An XML-declared
   `<Thumbnail>` never had `CreateImageView` called, and disposing it would throw. Then create the new
   reference, copy the declared `ModelTransform` across, and assign `template.Thumbnail`.
5. `UpdateDescriptorSets`, allocate a primary command buffer, `Begin(OneTimeSubmit)`,
   `RecordPartRender`, optional readback, `End`, `Graphics.Submit(..., fence)`, `WaitForFence(fence, -1)`,
   destroy the fence, free the command buffer. A non-`Success` wait is recorded as the failure reason.

**Results.** `FoldThumbnailResults` marks deliberate skips (sub-parts) as `Skipped` and every other
miss as `Degraded`, with the reason, in the job's `LoadedModRecord`. It never upgrades a part an
earlier state already degraded. The generator is disposed at the end of the state, releasing the root
part, the renderer (pipeline), the readback buffer and the command pool.[^loader-gpu-states]

**Showing the image.** The editor part browser's `DrawPartImageButton` uses whatever
`template.Thumbnail` holds the next time it draws (a plain button while it is null).[^ksa-vehicle-editor]
The Results panel mirrors that call, with one extra guard: `ImageView.IsNull()` sends XML-only
thumbnails to a plain button, so a null image view is never handed to the ImGui backend.[^results-panel]

**Diagnostic readback** ([parts-now.lib/Runtime/ThumbnailReadback.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/ThumbnailReadback.cs)). When
`DebugReadback` is true, the same command buffer gets a `SampledReadF → TransferSrc` transition, a
`CopyImageToBuffer` into a host-visible, host-coherent buffer, and a transition back. After the fence,
the fraction of non-zero RGBA texels is logged. A thumbnail that drew nothing is exactly the clear
colour `(0,0,0,0)`, so 0 % is a diagnosis rather than a guess.[^thumbnail-readback]

## Lifecycle and timing

Where the thumbnail submits sit in `Program.OnFrame`, build 5554:[^ksa-program]

| Line | Call | Relevance |
|---|---|---|
| L2220-L2221 | `ExecuteNextVehicleSolvers` / `ExecuteNextOrbitSolvers` | Physics is running on workers; thumbnails touch no simulated state |
| L2267 → def L3131 | `OnDrawUiFrame`, **StarMap prefix `[StarMapBeforeGui]` → `PartsNowSubmod.Update` → `RuntimeModLoader.Step()`** | Thumbnails are rendered and fence-waited here |
| L3140 (inside `OnDrawUiFrame`) | `ImGuiWindow.DrawAllStaticWindows` | The editor part browser records `ImageButton`s for the new images in the same frame |
| L2275 | `OnDrawUiConsole` | unscience's hidden-HUD replay of `Update` (prefix), when `DrawUI` is false |
| L2323 → L2420 | `OnPreRender` → `_renderer.TryAcquireNextFrame()` | The frame's own command recording has not started yet |
| L2437 | `Editor?.OnPreRender` → `ThumbnailDynamic.Render` | The hover preview writes the same camera UBO slice and submits on the same viewport, after Parts Now has already finished |
| L2324 → L2468 | `Render` → `UpdateRenderingResources` | Copies the cached `_cameraData[slot]` into every viewport that `RendersThisFrame()`, which includes `ViewportType.PartThumbnail` (L4308-L4327) |

Why this point:

- **Queue ownership.** Thumbnails submit to `renderer.Graphics` and block on a fence. That is only
  safe on the main thread and outside the frame's own submit. The sub-mod's comment on `Update` says
  `Step()` must be called exactly there and exactly once per frame.[^parts-now-submod]
- **Sharing the viewport with the hover preview.** `UpdateGlobalCameraData` writes
  `GlobalShaderBindings.CameraData(viewport.ShaderSlot)` for the current frame index, and
  `RecordPartRender` binds `GlobalShaderBindings.DescriptorSet(viewport.ShaderSlot)` for the same
  index. Parts Now writes, submits and waits before `ThumbnailDynamic.Render` writes the slot again
  later in the frame, and before `UpdateRenderingResources` overwrites it from `_cameraData`. Each
  writer finishes its own GPU work before the next write, so neither sees the other's camera. Moving
  the Parts Now submit to `OnPreRender` or later would interleave with those writes.[^ksa-global-shader-bindings][^ksa-viewport-ex]
- **Ordering inside the job.** `Thumbnails` runs after `Bind` (bindless handles and mesh offsets are
  valid) and after `WarmModels` (a bad `<Mesh Id>` already failed there).
  `ThumbnailRenderResources.AddDraw` reads `Material.*.BindlessHandle` and the shared interleaved
  mesh offsets, and `RecordPartRender` binds `DeviceMeshInterleaved.Shared`'s vertex and index
  buffers, so the runtime meshes must already sit inside the
  [reserved headroom](./parts-now-mesh-headroom-reservation.md).[^ksa-thumbnail-render-resources]
- **Cost.** Each part is a full submit plus a blocking wait on the main thread. Two per frame bounds
  that hitch.
- **HUD hidden.** In the supermod, `HiddenUiFrameHook` replays `Update` from its `OnDrawUiConsole`
  prefix, which is still before `OnPreRender`. The standalone host only uses `[StarMapBeforeGui]`, so
  its jobs pause while the HUD is hidden.

## Key code

| Path | Role |
|---|---|
| [parts-now.lib/Runtime/PartThumbnailGenerator.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/PartThumbnailGenerator.cs) | `Begin`, `Step`, `RenderOne`, `SubmitOne`, resource release |
| [parts-now.lib/Runtime/ThumbnailReadback.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/ThumbnailReadback.cs) | Optional image-to-buffer copy and non-zero texel count |
| [parts-now.lib/Runtime/RuntimeModLoaderGpuStates.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModLoaderGpuStates.cs#L376-L433) | `StateThumbnails` driver and `FoldThumbnailResults` |
| [parts-now.lib/Ui/ResultsPanel.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Ui/ResultsPanel.cs#L104-L140) | Per-part thumbnail column with the `ImageView.IsNull()` guard |
| [parts-now.lib/PartsNowSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/PartsNowSubmod.cs#L60-L78) | The single per-frame `Step()` call; `Dispose` abandons a running job |

## Snippets

Re-asserting the shared camera once per frame:

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

Framing, the zero-draw guard and the image swap:

```csharp
// parts-now.lib/Runtime/PartThumbnailGenerator.cs L278-L322
        ThumbnailCreator.MoveRootPart(root, template.Thumbnail, camera);
// ...
            ThumbnailCreator.CollectDraws(root, resources);
// ...
            if (drawCount == 0)
            {
                // Deliberately BEFORE the image is created. RecordPartRender is what transitions the
                // image out of VK_IMAGE_LAYOUT_UNDEFINED, so an image created for a part with no
                // draws would be sampled by the part browser while still undefined.
                Record(template.Id, false, "no draws collected");
                return;
            }
// ...
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

One submit, one fence, waited on the spot:

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
// ...
```

The game helper that writes only the thumbnail viewport's camera slice:

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

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Program.ThumbnailViewport` (created with `ViewportRegistry.CreatePartThumbnailViewport`) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L496) | The offscreen viewport and camera used for every thumbnail (created at L949) |
| `PartThumbnailViewport` | [current/decomp/KSA/PartThumbnailViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartThumbnailViewport.cs#L16-L25) | `ViewportType.PartThumbnail`, `CameraMode.Fixed`, square size from `PartThumbnailSize` |
| `ThumbnailCreator.ResetRootPart`, `AddPart`, `MoveRootPart`, `CollectDraws`, `CreateThumbnailReference` | [current/decomp/KSA.Rendering/ThumbnailCreator.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ThumbnailCreator.cs#L126-L222) | Framing and image creation shared with the boot path (L57-L124) |
| `ThumbnailRenderer` ctor, `SIZE`, `RecordPartRender` | [current/decomp/KSA.Rendering.Thumbnails/ThumbnailRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Thumbnails/ThumbnailRenderer.cs#L32-L187) | Pipeline per job; dynamic-rendering pass that clears to `(0,0,0,0)` and ends in `SampledReadF` |
| `ThumbnailRenderResources` ctor, `AddDraw`, `UpdateDescriptorSets`, `DrawCommandVector` | [current/decomp/KSA.Rendering.Thumbnails/ThumbnailRenderResources.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Thumbnails/ThumbnailRenderResources.cs#L34-L174) | Per-part draw lists and depth image |
| `ThumbnailReference.ImageView`, `ModelTransform`, `GetOrCreateImGuiTexture`, `Dispose` | [current/decomp/KSA.Rendering.Thumbnails/ThumbnailReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Thumbnails/ThumbnailReference.cs#L12-L60) | The image the browser samples; null-view guard[^ksa-thumbnail-reference] |
| `ThumbnailDynamic.UpdateGlobalCameraData`, `Render` | [current/decomp/KSA.Rendering.Thumbnails/ThumbnailDynamic.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Thumbnails/ThumbnailDynamic.cs#L167-L288) | Camera slice writer reused; the other user of the viewport |
| `GlobalShaderBindings.CameraData`, `DescriptorSet`, `NextFrame` | [current/decomp/KSA/GlobalShaderBindings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GlobalShaderBindings.cs#L51-L64) | Per-slot, per-frame-index camera UBO and descriptor set |
| `ViewportEx.RendersThisFrame` | [current/decomp/KSA/ViewportEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportEx.cs#L78-L81) | Why `UpdateRenderingResources` also rewrites the thumbnail slot |
| `Camera.Unfollow(bool changeControl = true)` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L620-L631) | The default nulls `Program.ControlledVehicle`[^ksa-camera] |
| `VehicleEditor.OnPreRender`, `PartWindow.DrawPartImageButton` | [current/decomp/KSA/VehicleEditor.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleEditor.cs#L5425-L5430) | Hover-preview timing; browser button (L459-L484) |
| `Program.OnFrame`, `OnDrawUiFrame`, `OnPreRender`, `UpdateRenderingResources` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2335) | Frame points in the timing table |

## Pitfalls and version notes

- `Camera.Unfollow()` with its default argument sets `Program.ControlledVehicle = null`. KSA's own
  boot path (`PreparePartThumbnails`) calls the default overload; at runtime that would drop the
  player's vessel. Always pass `changeControl: false`.
- Never move the thumbnail camera. `MoveRootPart` places the part in front of a camera parked at
  origin and identity, and `ThumbnailDynamic` assumes the same.
- The class comment describes the viewport as "viewport index 1" with `IsOffscreen` and
  `ShouldRenderGizmos`, and the README says `RecordPartRender` binds `DynamicOffset(viewport.Index)`.
  None of those members exist in 5554. The viewport is a `PartThumbnailViewport`
  (`ViewportType.PartThumbnail`), and the bind is `GlobalShaderBindings.DescriptorSet(viewport.ShaderSlot)`.
  The code passes the viewport object and never computes an index, so only the comments are stale.[^scope-part-editor]
- `ThumbnailRenderer.SIZE` reads `GameSettings.Current.Graphics.PartThumbnailSize` live, while the
  viewport was sized at boot. Both are square, so the generator only warns.
- KSA's own `ThumbnailCreator.Initialize` and `ThumbnailDynamic` pass `Graphics.Index` as the
  command pool's `QueueFamilyIndex`. Parts Now passes `Graphics.Family`, which is the Vulkan meaning
  of that field. The two only differ on a device where the graphics queue is not index 0 of its
  family (observation from the decomp, not tested).
- `DebugReadback` has no UI toggle and nothing in the repository sets it to true. The Results panel
  notes that the planned toggle was dropped. To use it, set the property in code before `Begin`.
- Each job builds and destroys a `ThumbnailRenderer`, and with it a graphics pipeline. That is fine
  for an occasional load but would be wasteful as a per-frame pattern.
- A part declared with `<Thumbnail>` in XML has a non-null `Thumbnail` without an image from
  `RegisterBundles` until its turn in this state. Code that draws thumbnails must check
  `ImageView.IsNull()`, as the Results panel does; see
  [runtime loading](./parts-now-runtime-part-loading.md) for the editor browser's exposure to this.

## See also

- [Runtime part loading state machine](./parts-now-runtime-part-loading.md) (the `Thumbnails` state)
- [Mesh headroom reservation](./parts-now-mesh-headroom-reservation.md) (the shared buffers the thumbnail draws read)
- [Bundle validation rules V1-V15](./parts-now-bundle-validation.md) (V9 keeps `AddDraw` from crashing)
- [Runtime unload and purge](./parts-now-runtime-unload-purge.md) (thumbnail disposal and the hover-preview reset)
- [Hidden-HUD frame hook](./lib-hidden-ui-frame-hook.md)
- [Parts Now sub-mod](../submods/parts-now.md)
- [Mod overview](../overview.md)
- [ThumbnailCreator framing plus ThumbnailDynamic per-frame submit, two parts per frame](../../../howto/rendering/vulkan-passes/capture-rendered-frames/capture-rendered-frames-2.md) - how-to article demonstrating this feature
- [Private transient pool for readback and thumbnails](../../../howto/rendering/vulkan-passes/submit-your-own-vulkan-commands/submit-your-own-vulkan-commands-3.md) - how-to article demonstrating this feature

[^thumbnail-generator]: parts-now.lib/Runtime/PartThumbnailGenerator.cs
[^parts-now-readme]: parts-now.lib/README.md (authors' design notes)
[^ksa-thumbnail-creator]: KSA.Rendering.ThumbnailCreator (decompiled, build 2026.10.10.5554)
[^ksa-thumbnail-dynamic]: KSA.Rendering.Thumbnails.ThumbnailDynamic (decompiled, build 2026.10.10.5554)
[^ksa-thumbnail-renderer]: KSA.Rendering.Thumbnails.ThumbnailRenderer (decompiled, build 2026.10.10.5554)
[^loader-gpu-states]: parts-now.lib/Runtime/RuntimeModLoaderGpuStates.cs
[^ksa-vehicle-editor]: KSA.VehicleEditor (decompiled, build 2026.10.10.5554)
[^results-panel]: parts-now.lib/Ui/ResultsPanel.cs
[^thumbnail-readback]: parts-now.lib/Runtime/ThumbnailReadback.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^parts-now-submod]: parts-now.lib/PartsNowSubmod.cs
[^ksa-global-shader-bindings]: KSA.GlobalShaderBindings (decompiled, build 2026.10.10.5554)
[^ksa-viewport-ex]: KSA.ViewportEx (decompiled, build 2026.10.10.5554)
[^ksa-thumbnail-render-resources]: KSA.Rendering.Thumbnails.ThumbnailRenderResources (decompiled, build 2026.10.10.5554)
[^scope-part-editor]: scope/part-editor-and-robotics.md (authors' integration map)
[^ksa-thumbnail-reference]: KSA.Rendering.Thumbnails.ThumbnailReference (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
