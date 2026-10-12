---
type: How-To
title: Feed a compute shader thousands of items through a host-visible storage buffer
description: Allocate a persistently mapped storage buffer through the renderer's allocator, give every view and frame in flight its own slice, fill it on the CPU at record time and read it from a compute shader as a std430 array
category: shaders
group: compute-effects
topic: pack-parameters-for-a-compute-effect
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony, vulkan]
demonstrated_by: [mods/ksarmory/features/compute-shell-tracers]
techniques: [vulkan-buffer, reflection, glsl-shader]
hooks: [harmony-prefix KSA.SunbloomRenderer.Render]
lifecycle: [render:bloom]
game_types:
  - KSA.Rendering.ComputePipelineWrapper
  - Brutal.VulkanApi.Abstractions.BufferEx
  - Brutal.VulkanApi.Abstractions.MappedMemory
  - KSA.KsaVmaAllocator
  - Core.KSADeviceContextEx
  - Core.Renderer
tags: [storage-buffer, host-visible, ring-buffer, std430, reflection, compute, tracers, ksarmory]
---
A 128-byte push block holds one effect; a gun burst has hundreds of tracers. KSArmory draws all of them in one dispatch by writing their screen-space segments into a host-visible storage buffer from the CPU every frame, and reading them in the compute shader as a `std430` array. This article is the buffer half of [Gun tracers added into the scene by one tiled compute dispatch](../../../../mods/ksarmory/features/compute-shell-tracers.md): allocating the buffer when the mod does not reference the game's allocator type, carving it into per-view, per-frame slices, binding it to a `ComputePipelineWrapper` and consuming it in the shader. The projection and gather are in [Reconstruct scene positions from the depth buffer and paint on them](../reconstruct-scene-positions-from-depth-in-a-compute-shader/overview.md).

## Background: how the game does it

- **The allocator.** `KSADeviceContextEx` declares `public KsaVmaAllocator Allocator { get; set; }` and `Renderer` derives from it ([current/decomp/Core/KSADeviceContextEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/KSADeviceContextEx.cs#L67)). `KsaVmaAllocator` implements `IBufferAllocator` (declared in `Brutal.VulkanApi.Abstractions`), and `BufferExExtensions.CreateBuffer(this IBufferAllocator, BufferEx.CreateInfo)` makes the buffer ([current/decomp/Brutal.VulkanApi.Abstractions/BufferExExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/BufferExExtensions.cs#L5)).
- **Mapping.** `BufferEx.Map()` returns a `MappedMemory` whose `AsSpan<T>()` is a `Span<T>` over the whole allocation ([current/decomp/Brutal.VulkanApi.Abstractions/MappedMemory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/MappedMemory.cs#L101)). A host-visible, host-coherent allocation needs no flush.
- **Frames in flight.** `Renderer.MaxFramesInFlight` is how many frames the GPU may still be reading while the CPU records the next; a buffer the GPU reads must not be written in place for the frame still in flight.
- **Binding.** `ComputePipelineWrapper`'s `storageBuffers:` parameter takes `VkBuffer[]` and puts them last in its own set, after storage images and samplers ([current/decomp/KSA.Rendering/Utils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/Utils.cs#L296-L320)). The descriptor is written once at construction, so the whole buffer is bound; slices are chosen by an offset in the push block, not by the descriptor.

## Lifecycle and timing

Rounds are stepped and collected after the simulation step (`frame:draw-ui`, or `frame:celestials` with the HUD hidden), but segments are projected and written when the pass records, in the prefix on `SunbloomRenderer.Render` (`render:bloom`), against the camera of the viewport being drawn. The buffer is created once, lazily, on the first recorded frame and kept for the process; it names no image, so a rebuilt view reuses it. See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Decide the layout.** Two `float4` per segment (head and tail, each x, y, depth, and one extra), 32 bytes, as a `[StructLayout(LayoutKind.Sequential, Pack = 4)]` struct on the C# side and a `struct Segment { vec4 Head; vec4 Tail; }` in a `readonly buffer` on the GLSL side. All members are `vec4` so `std430` and the C# layout agree with no padding to think about:

   ```csharp
   // src/KSArmory/Ksa/ShellTracers.cs L26-L31
       [StructLayout(LayoutKind.Sequential, Pack = 4)]
       public struct Segment
       {
           public float4 Head;    // x, y, device depth, brightness; negative is a round with no tracer
           public float4 Tail;    // x, y, device depth, the core's half-width in pixels
       }
   ```

   ```glsl
   // src/KSArmory/Shaders/KSArmoryTracer.comp L14-L26
   layout (set = 1, binding = 0, Rgba16f) uniform image2D sceneColor;
   layout (set = 1, binding = 1) uniform sampler2D sceneDepth;

   struct Segment
   {
       vec4 Head;  // x, y in pixels, device depth, brightness; negative is a round with no tracer
       vec4 Tail;  // x, y in pixels, device depth, the core's half-width in pixels
   };

   layout (std430, set = 1, binding = 2) readonly buffer Segments
   {
       Segment segments[];
   };
   ```

   Sources: [src/KSArmory/Ksa/ShellTracers.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ShellTracers.cs#L26-L31), [src/KSArmory/Shaders/KSArmoryTracer.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryTracer.comp#L14-L26). Binding 2 follows from the wrapper's order: one storage image, one depth sampler, then the storage buffer.

2. **Reach the allocator without referencing its type.** The concrete allocator type also implements an interface from `Brutal.Vulkan.Vma`, which the mod does not reference, so the mod reads the property by reflection and uses it only through `IBufferAllocator`. Create the buffer sized for every frame in flight, every view in a frame and every item:

   ```csharp
   // src/KSArmory/Ksa/CloudPass.cs L1665-L1689
       private static bool BuildTracerBuffer()
       {
           Renderer renderer = Program.GetRenderer();
           _tracerFrames = Math.Max(renderer.MaxFramesInFlight, 1);

           // Read by reflection and used through the interface, because the allocator's own type
           // implements one from Brutal.Vulkan.Vma, which this mod does not reference.
           if (typeof(Renderer).GetProperty("Allocator")?.GetValue(renderer) is not IBufferAllocator allocator)
           {
               Warn("no buffer allocator on the renderer; shells are drawn as lines");
               return false;
           }

           BufferEx buffer = allocator.CreateBuffer(new BufferEx.CreateInfo
           {
               Name = "KSArmory tracers",
               BufferUsage = VkBufferUsageFlags.StorageBufferBit,
               BufferSize = ByteSize.Of<ShellTracers.Segment>(_tracerFrames * TracerViewsPerFrame * ShellTracers.MostPerView),
               AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
           });

           _tracerMemory = buffer.Map();
           _tracerBuffer = buffer;
           return true;
       }
   ```

   Source: [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L1665-L1689). With 4096 segments per view and four views per frame, the default `MaxFramesInFlight` of 2 makes this 2 x 4 x 4096 x 32 bytes = 1 MiB, mapped once for the process. If the allocator is missing the mod falls back (to gizmo lines); decide your fallback now.

3. **Hand the buffer to the pipeline.** Build the `ComputePipelineWrapper` with the buffer's `VkBuffer` in `storageBuffers:` and a push range sized for your small header:

   ```csharp
   // src/KSArmory/Ksa/CloudPass.cs L1701-L1718
           IRenderImage[] storageTargets = [view.Target];
           IRenderImage[] depthTargets = [depth];
           VkBuffer[] buffers = [_tracerBuffer!.Value.VkBuffer];
           VkPushConstantRange[] ranges =
           [
               new VkPushConstantRange
               {
                   Offset = (ByteSize32)0,
                   Size = (ByteSize32)Marshal.SizeOf<TracerPush>(),
                   StageFlags = VkShaderStageFlags.ComputeBit,
               },
           ];

           view.Tracers = new ComputePipelineWrapper(
               storageTargets, depthTargets, default, default, shader,
               default, ranges, renderer.MaxFramesInFlight, renderer,
               "KSArmory.Tracers", Program.PointClampedSampler, Program.LinearClampedSampler,
               storageBuffers: buffers);
   ```

   Source: [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L1701-L1718).

4. **Pick a slice per view per frame, fill it, push where it starts.** The slot is `(frameIndex mod framesInFlight) x viewsPerFrame + viewInFrame`, so two viewports in one frame, or the frame the GPU is still reading, never share a run. The push block carries the slice start, the count and the dispatch origin; the segments are written straight into the mapped span:

   ```csharp
   // src/KSArmory/Ksa/CloudPass.cs L1615-L1658
           ShellTracers.MarkPainted();

           if (frameIndex != _tracerFrame)
           {
               _tracerFrame = frameIndex;
               _tracerView = 0;
           }

           int slot = ((((frameIndex % _tracerFrames) + _tracerFrames) % _tracerFrames) * TracerViewsPerFrame)
                      + (_tracerView++ % TracerViewsPerFrame);
           int first = slot * ShellTracers.MostPerView;

           Span<ShellTracers.Segment> all = _tracerMemory.AsSpan<ShellTracers.Segment>();
           Span<ShellTracers.Segment> mine = all.Slice(first, ShellTracers.MostPerView);

           int count = ShellTracers.Build(camera, width, height, mine,
                                          out int minX, out int minY, out int maxX, out int maxY);
           if (count == 0) return false;
           // ...
           TracerPush push = new()
           {
               First = first,
               Count = count,
               OriginX = minX,
               OriginY = minY,
               Colour = TracerColour,
               BallColour = BallColour,
           };

           using (commandBuffer.TagRegion(GpuTag))
           using (commandBuffer.TagRegion(TracersTag))
           {
               Hazard(commandBuffer);

               view.Tracers!.BindPipeline(commandBuffer, viewport.ShaderSlot, default, default, push);
               commandBuffer.Dispatch((maxX - minX + TracerGroup - 1) / TracerGroup,
                                      (maxY - minY + TracerGroup - 1) / TracerGroup, 1);
           }
   ```

   Source: [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L1615-L1658). `frameIndex` here is the value `SunbloomRenderer.Render` receives. Past four views in one frame the slots wrap and could overwrite each other; the mod's budget is four.

5. **Read the slice in the shader.** The push block says where this view's run starts and how many segments it holds; each workgroup strides over them. The tile gather that makes this cheap is in [Reconstruct scene positions from the depth buffer and paint on them](../reconstruct-scene-positions-from-depth-in-a-compute-shader/overview.md); the header is:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryTracer.comp L28-L33
   layout (push_constant) uniform PushConstants
   {
       ivec4 FirstCountOrigin;  // first segment, how many, the dispatch's first pixel
       vec4 Colour;             // the core's colour, and the halo's share of it
       vec4 BallColour;         // a round with no tracer's colour
   } pc;
   ```

   Source: [src/KSArmory/Shaders/KSArmoryTracer.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryTracer.comp#L28-L33). Use `pc.FirstCountOrigin.x + i` to index `segments`.

6. **Order the write against the GPU read.** A host write needs no barrier because the memory is coherent and the slice is not in use by a frame in flight; what needs a barrier is the shader's storage-image write against the previous dispatch's, which the mod's `Hazard` does before each dispatch.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs) | `BuildTracerBuffer`, `BuildTracers`, `Tracers` (L1577-L1721) |
| [src/KSArmory/Ksa/ShellTracers.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ShellTracers.cs) | `Segment`, `MostPerView`, `Build` (projection into the slice) |
| [src/KSArmory/Shaders/KSArmoryTracer.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryTracer.comp) | Bindings, push header, tiled gather, additive streaks |

## Variations and gotchas

- **The same recipe serves the bullet-hole shader.** `BuildHoleBuffer` is the same allocation with an 80-byte `HoleData` and 400 entries per slice ([src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L1323-L1348)); see [Reconstruct scene positions from the depth buffer and paint on them](../reconstruct-scene-positions-from-depth-in-a-compute-shader/overview.md).
- **Reflection on `Allocator`.** The property is public in build 5554, but the mod reads it by reflection because of the unreferenced interface; if the property is renamed, the pass logs a warning and falls back rather than failing.
- **Do not size by `MaxFramesInFlight` only.** Multiple viewports record in one frame; give each its own run, or the second view overwrites the first before the GPU reads it.
- **CPU cost scales with what is collected, GPU cost with what is on screen.** `Build` walks every collected round per recorded view even though the dispatch covers only the bounding box of what is visible.
- **No upload fence needed, no growth possible.** The buffer is fixed size; items past the cap are dropped (the mod drops the faint ones last).
- **Never write beyond your slice.** The span is the whole allocation; a bug here corrupts another view's data without any validation error.

## See also

- Siblings: [128-byte push block](pack-parameters-for-a-compute-effect-1.md), [one pipeline, many kinds](pack-parameters-for-a-compute-effect-2.md), [camera-relative positions](pack-parameters-for-a-compute-effect-4.md), [overview](overview.md)
- [Gun tracers added into the scene by one tiled compute dispatch](../../../../mods/ksarmory/features/compute-shell-tracers.md), [Shell holes held in the struck sub-part's frame and painted from depth](../../../../mods/ksarmory/features/bullet-hole-decals.md), [A mod compute pass recorded into KSA's frame just before bloom](../../../../mods/ksarmory/features/compute-pass-before-bloom.md)
- [Reconstruct scene positions from the depth buffer and paint on them](../reconstruct-scene-positions-from-depth-in-a-compute-shader/overview.md)
