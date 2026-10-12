---
type: How-To
title: Depth-reading compute dispatches for rings, holes, tracers and relighting
description: Ship .comp files as Shader assets, build ComputePipelineWrapper pipelines over the HDR scene colour and depth, and dispatch from a prefix on SunbloomRenderer.Render
category: rendering
group: vulkan-passes
topic: modify-the-hdr-scene-with-a-compute-pass
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by:
  - mods/ksarmory/features/compute-pass-before-bloom
  - mods/ksarmory/features/depth-painted-ground-rings
  - mods/ksarmory/features/compute-shell-tracers
  - mods/ksarmory/features/fireball-light-injection
  - mods/ksarmory/features/burst-flash-and-view-shake
techniques: [vulkan-pipeline, harmony-prefix, glsl-shader, xml-asset]
hooks:
  - harmony-prefix KSA.SunbloomRenderer.Render
  - starmap StarMapAfterGui
  - harmony-prefix KSA.Program.OnFrameCelestials
lifecycle: [startup:asset-load, frame:draw-ui, frame:celestials, frame:render, render:bloom]
game_types:
  - KSA.SunbloomRenderer
  - KSA.Rendering.ComputePipelineWrapper
  - KSA.ShaderReference
  - KSA.Rendering.RenderImage
  - KSA.Rendering.BarrierBatch
  - KSA.Rendering.ImageBarrierInfo
tags: [compute, hdr, depth, shader-asset, pipeline-wrapper, push-constants, specialization-constants]
---

Outcome: effects that paint straight into KSA's HDR scene image, with the depth buffer available so they lie on terrain and hulls:
targeting rings of fixed pixel width, bullet holes, shell tracers, a fireball's glow. Everything is compute: no graphics pipeline,
no mesh, no render pass. KSA compiles the mod's `.comp` files because they are declared as `Shader` assets, `ComputePipelineWrapper`
builds the pipelines, and one prefix on a public method supplies the moment to dispatch (see
[insert a custom Vulkan pass](../insert-a-custom-pass-into-render-game/overview.md), approach 4, for the hook itself). Demonstrated by KSArmory
([A mod compute pass recorded into KSA's frame just before bloom](../../../../mods/ksarmory/features/compute-pass-before-bloom.md), [Targeting rings painted on the depth buffer](../../../../mods/ksarmory/features/depth-painted-ground-rings.md), [Gun tracers added into the scene by one tiled compute dispatch](../../../../mods/ksarmory/features/compute-shell-tracers.md),
[Fireball light through LightDebug, filled in where the light pre-pass dropped it](../../../../mods/ksarmory/features/fireball-light-injection.md), [A burst's whiteout and a blast front's view shake, painted into the scene image instead of moving the camera](../../../../mods/ksarmory/features/burst-flash-and-view-shake.md)). Prerequisites: StarMap, Harmony, a content mod.

## Background: how the game does it

`ShaderReference` [current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs) is the asset type for a shader file: `DoLoad` compiles it with shaderc through
`ShaderModuleUtils.FromFile` while recording the includes it resolved [current/decomp/KSA/ShaderReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ShaderReference.cs#L80-L175), and `ModLibrary.LoadAll`
runs every loader. `ComputePipelineWrapper` [current/decomp/KSA.Rendering/ComputePipelineWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ComputePipelineWrapper.cs#L12-L135) builds one compute pipeline plus
one descriptor set from lists of images and buffers, in a fixed order: storage images, then depth samplers, linear colour samplers,
point colour samplers and read-only views, then uniform and storage buffers. `GlobalShaderBindings` is always set 0; the wrapper's
own set follows (set 1 when `bindTextures` is false); `externalDescriptorSetLayouts` come after. `BindPipeline<T>(commandBuffer,
viewportIndex, externalDescriptorSets, externalDynamicOffsets, pushConstant)` binds pipeline and sets and pushes the constants
[current/decomp/KSA.Rendering/ComputePipelineWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ComputePipelineWrapper.cs#L195-L215).

At `SunbloomRenderer.Render` the main view's scene colour is the resolved single-sample HDR image in `General` layout (preset
`StorageReadFc`) and depth is in `DepthSampledReadVfc` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4910-L4916).

## Lifecycle and timing

| Step | Where |
|---|---|
| Shaders compile | `startup:asset-load`, in `ModLibrary.LoadAll`, from the mod's `assets` XML |
| Collect what to draw (rings, rounds, light) | `frame:draw-ui` (`[StarMapAfterGui]`) or `frame:celestials` with the HUD hidden; anchored to the body, not to a camera |
| Resolve against the camera and dispatch | `render:bloom`, in the prefix on `SunbloomRenderer.Render`, with `Program.GetRenderCamera()` (the camera of the viewport being recorded) |
| Result | scene colour before bloom, tonemap and the composite, so it blooms and grades with the scene |

Collecting in the UI pass is deliberate for annotations (rings go with the HUD); effects that are part of the world (tracers) are
collected in KSArmory's per-frame step, which runs from a Harmony prefix on `Program.OnFrameCelestials` (`frame:celestials`, outside
the `DrawUI` guard), so they stay with the HUD hidden. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Declare the shaders as assets.** List the XML in `mod.toml` `assets` and declare each `.comp` with a `Shader` element; KSA
   compiles them from any mod folder.

   ```xml
   <!-- src/KSArmory/KSArmoryParticles.xml L563-L571 -->
   <Shader Id="KSArmoryCloudCompute" Path="Shaders/KSArmoryCloud.comp" />
   <Shader Id="KSArmoryCloudResolveCompute" Path="Shaders/KSArmoryCloudResolve.comp" />
   <Shader Id="KSArmoryShockCompute" Path="Shaders/KSArmoryShock.comp" />
   <Shader Id="KSArmorySensorCompute" Path="Shaders/KSArmorySensor.comp" />
   <Shader Id="KSArmoryFireLightCompute" Path="Shaders/KSArmoryFireLight.comp" />
   <Shader Id="KSArmoryRingCompute" Path="Shaders/KSArmoryRing.comp" />
   <Shader Id="KSArmoryHoleCompute" Path="Shaders/KSArmoryHole.comp" />
   <Shader Id="KSArmoryLeakCompute" Path="Shaders/KSArmoryLeak.comp" />
   <Shader Id="KSArmoryTracerCompute" Path="Shaders/KSArmoryTracer.comp" />
   ```

   Fetch with `ModLibrary.TryGet<ShaderReference>(id, out var shader)`. A GLSL error leaves `shader.Shader` null (KSA logs the compile
   error in its own log) and the wrapper then throws `Compute Shader is null`.

2. **Lay out the shader's bindings in the wrapper's order.** For a ring pass: set 1 binding 0 is the scene colour as
   `rgba16f` storage image, binding 1 the scene depth sampler. One push-constant block, 128 bytes at most (Vulkan's guaranteed
   minimum).

   ```glsl
   // src/KSArmory/Shaders/KSArmoryRing.comp L8-L24
   // Bindings follow ComputePipelineWrapper's order: the storage image, then the depth it samples.

   layout (local_size_x = 8, local_size_y = 8) in;

   layout (set = 1, binding = 0, Rgba16f) uniform image2D sceneColor;
   layout (set = 1, binding = 1) uniform sampler2D sceneDepth;

   layout (push_constant) uniform PushConstants
   {
       mat4 InvViewProj;   // clip -> camera-relative world
       vec4 CentreRadius;  // the ring's centre, camera-relative, and its radius
       vec4 UpInner;       // the local vertical there, and a second ring's radius, zero for none -- for an
                           // ellipse, the vertical and the long axis each packed into two floats
       vec4 Colour;        // the ink, and the brightness it keeps over the darkest ground
       vec4 TileWidth;     // the tile's first pixel, the line's half-width in pixels, and dashes or zero --
                           // or minus an ellipse's short radius, which is what says it is one
   } pc;
   ```

3. **Build the wrapper once per viewport.** Pass the scene colour as a storage target and depth as a depth sampler; pass `Program.PointClampedSampler`
   and `Program.LinearClampedSampler`. Key per-view resources by `viewport.ShaderSlot`, and rebuild when the target image or its size changes,
   because descriptor sets name images.

   ```csharp
   // src/KSArmory/Ksa/CloudPass.cs L1756-L1784
   private static bool BuildRings(View view, IRenderImage depth)
   {
       if (!ModLibrary.TryGet<ShaderReference>(RingShaderId, out var shader) || shader is null)
       {
           Warn($"no shader '{RingShaderId}'; targeting rings will not draw");
           return false;
       }

       Renderer renderer = Program.GetRenderer();

       IRenderImage[] storageTargets = [view.Target];
       IRenderImage[] depthTargets = [depth];
       VkPushConstantRange[] ranges =
       [
           new VkPushConstantRange
           {
               Offset = (ByteSize32)0,
               Size = (ByteSize32)Marshal.SizeOf<RingPush>(),
               StageFlags = VkShaderStageFlags.ComputeBit,
           },
       ];

       view.Rings = new ComputePipelineWrapper(
           storageTargets, depthTargets, default, default, shader,
           default, ranges, renderer.MaxFramesInFlight, renderer,
           "KSArmory.Rings", Program.PointClampedSampler, Program.LinearClampedSampler);

       return true;
   }
   ```

4. **Record the dispatch** inside the prefix: compute the tile covering the effect on screen, fill the push struct (inverse view-projection,
   centre relative to the camera, radius, colour), `BindPipeline`, `Dispatch(tile.GroupsX, tile.GroupsY, 1)`, and put a compute write-to-read memory
   barrier between two dispatches that touch the scene.

   ```csharp
   // src/KSArmory/Ksa/CloudPass.cs L1023-L1056
   if (!GroundRings.TryAt(i, out double3 centreEcl, out double3 up, out double radius,
                          out double inner, out float4 colour, out int dashes,
                          out double3 major, out double minor)) continue;

   double3 centre = centreEcl - camera.PositionEcl;
   if (!Vec.IsFinite(centre)) continue;

   Tile tile = TileFor(camera, centre, Vec.AnyPerpendicular(up), radius, RingTileReach,
                       width, height);
   if (tile.Empty) continue;

   if (marks > 0) Hazard(commandBuffer);

   RingPush push = new()
   {
       InvViewProj = camera.VPInv.viewProjection,
       CentreRadius = new float4((float)centre.X, (float)centre.Y, (float)centre.Z, (float)radius),
       UpInner = new float4((float)up.X, (float)up.Y, (float)up.Z, (float)inner),
       Colour = colour,
       TileWidth = new float4(tile.OriginX, tile.OriginY, RingHalfWidthPixels, dashes),
   };

   // An ellipse: both directions packed into the four floats the up and the inner ring
   // take, and the short radius where the dashes go, negative to say which this is.
   if (minor > 0.0)
   {
       float2 upPacked = OctahedralPack(up);
       float2 majorPacked = OctahedralPack(major);
       push.UpInner = new float4(upPacked.X, upPacked.Y, majorPacked.X, majorPacked.Y);
       push.TileWidth.W = -(float)minor;
   }

   view.Rings!.BindPipeline(commandBuffer, viewport.ShaderSlot, default, default, push);
   commandBuffer.Dispatch(tile.GroupsX, tile.GroupsY, 1);
   ```

5. **Reconstruct world positions from depth in the shader.** Depth is reverse-Z (zero is sky). Unproject with `InvViewProj`, take the ring's
   height above the local up vector, and measure radius in the ground plane, so the ring lies on terrain, a pad or a rock at any size, with a fixed pixel width
   from a tangent-plane footprint step.

6. **Tracers add a storage buffer of segments.** Pass `storageBuffers`, write segments for this view into its own
   slice of a host-visible, coherent, mapped buffer sized `frames in flight x views x capacity`, and dispatch over the bounding box of the
   segments:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryTracer.comp L12-L33
   layout (local_size_x = 16, local_size_y = 16) in;

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

   layout (push_constant) uniform PushConstants
   {
       ivec4 FirstCountOrigin;  // first segment, how many, the dispatch's first pixel
       vec4 Colour;             // the core's colour, and the halo's share of it
       vec4 BallColour;         // a round with no tracer's colour
   } pc;
   ```

7. **Contain faults.** Wrap the record in one `try`; on any exception log once, drop every view and set a failed flag so a broken shader degrades to
   "no effect". Expect a single log line from KSA for a GLSL error and one from the mod when the pass cannot build.

## Sub-step: tune shader constants live with specialization constants

KSArmory declares every look constant in GLSL with a `constant_id` and a default, so a value can change without recompiling:

```glsl
// src/KSArmory/Shaders/KSArmoryCloud.comp L1025, L1028-L1029, L1044-L1045, L1049-L1050
layout (constant_id = 7) const float GlareFloorNits = 1.1;
// ...
layout (constant_id = 9) const float GlareOverexposure = 1.6;
layout (constant_id = 8) const float GlareCoreNits = 3.0;
// ...
layout (constant_id = 10) const float GlareCoreDeg = 6.0;
layout (constant_id = 11) const float GlareWhiteDeg = 20.0;
// ...
layout (constant_id = 12) const float HaloGain = 0.8;
layout (constant_id = 13) const float HaloMost = 0.85;
```

The glare constants use ids 7 to 13. The wrapper takes `VkSpecializationInfo? specializationInfo`; the mod marshals map entries and
data into native memory for the duration of the constructor (the wrapper reads it only while building) and frees it afterwards:

```csharp
// src/KSArmory/Ksa/CloudPass.cs L310-L362
// exists -- the wrapper reads it only while building. Written through Marshal rather than
// pointers, so the mod needs no unsafe code for it.
private sealed class Specialization : IDisposable
{
    private readonly IntPtr _entries;
    private readonly IntPtr _data;

    public VkSpecializationInfo? Info { get; }

    public Specialization()
    {
        (ShaderTunables.Tunable Tunable, double Value)[] set = [.. ShaderTunables.Overridden()];
        if (set.Length == 0) return;

        int entrySize = Marshal.SizeOf<VkSpecializationMapEntry>();
        byte[] entries = new byte[entrySize * set.Length];
        byte[] data = new byte[4 * set.Length];

        for (int k = 0; k < set.Length; k++)
        {
            VkSpecializationMapEntry entry = new()
            {
                ConstantID = set[k].Tunable.ConstantId,
                Offset = (ByteSize32)(4 * k),
                Size = (ByteSize64)4L,
            };
            MemoryMarshal.Write(entries.AsSpan(k * entrySize), in entry);

            if (set[k].Tunable.Integer)
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4 * k), (int)set[k].Value);
            else
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4 * k), (float)set[k].Value);
        }

        _entries = Marshal.AllocHGlobal(entries.Length);
        _data = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(entries, 0, _entries, entries.Length);
        Marshal.Copy(data, 0, _data, data.Length);

        byte[] raw = new byte[Marshal.SizeOf<VkSpecializationInfo>()];
        int count = set.Length;
        ByteSize64 size = (ByteSize64)(long)data.Length;
        IntPtr entriesAt = _entries;
        IntPtr dataAt = _data;
        MemoryMarshal.Write(raw.AsSpan(Offset(nameof(VkSpecializationInfo.MapEntryCount))), in count);
        MemoryMarshal.Write(raw.AsSpan(Offset(nameof(VkSpecializationInfo.MapEntries))), in entriesAt);
        MemoryMarshal.Write(raw.AsSpan(Offset(nameof(VkSpecializationInfo.DataSize))), in size);
        MemoryMarshal.Write(raw.AsSpan(Offset(nameof(VkSpecializationInfo.Data))), in dataAt);

        Info = MemoryMarshal.Read<VkSpecializationInfo>(raw);
    }

    private static int Offset(string field) => (int)Marshal.OffsetOf<VkSpecializationInfo>(field);
```

A change of `ShaderTunables.Generation` releases the views, and the next frame rebuilds the pipelines with the new values: a pipeline rebuild of
a few milliseconds, no recompile. The table of ids lives in [src/KSArmory/Sim/ShaderTunables.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ShaderTunables.cs#L34-L71).

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/KSArmoryParticles.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryParticles.xml) | Shader asset declarations |
| [src/KSArmory/Ksa/CloudPassHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPassHook.cs) | The prefix on `SunbloomRenderer.Render` |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs) | Views, wrappers, dispatches, barriers, specialization |
| [src/KSArmory/Ksa/GroundRings.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/GroundRings.cs) | Body-anchored collection of rings |
| [src/KSArmory/Shaders/KSArmoryRing.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryRing.comp) | Ring shader |
| [src/KSArmory/Shaders/KSArmoryTracer.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryTracer.comp) | Tracer shader (tiled gather) |

## Variations and gotchas

- **Write access is not declared to KSA.** The engine's state before the hook only grants shader read; the mod's own compute-to-compute barriers
  order its dispatches, but the scene colour's tracked state still says "read".
- **Tile behind the camera.** A box corner with `clip.w <= 1e-6` makes the dispatch full-screen, which is correct but costs a whole-screen pass.
- **Shader errors show up at run time.** The C# build cannot catch GLSL faults; check the KSA log. KSA's own hot reload has no caller in this build,
  so KSArmory reflects `FileReference.DoLoad` for its `reload_shaders` command.
- **Pipelines are dropped, not destroyed.** `ComputePipelineWrapper.Destroy(Device)` exists; call it after a wait if you rebuild often.
- **Two views in one frame need separate buffer slices;** four per frame in flight is the budget.
- **Raytracing.** The pass reads and paints over the ray-traced result in IVA, but its output never enters the acceleration structure.
- See [check mod shaders offline with glslang](../../../shaders/glsl-workflow/check-mod-shaders-offline-with-glslang/overview.md) for catching GLSL errors before launch and
  [include the game shader library in mod GLSL](../../../shaders/glsl-workflow/include-the-game-shader-library-in-mod-glsl/overview.md) for `#include "Common/..."`.

## See also

- [Overview](overview.md)
- [Approach 2: in-place rescale with light pre-pass images](modify-the-hdr-scene-with-a-compute-pass-2.md)
- [Approach 3: far-fireball fill using the pre-pass irradiance](modify-the-hdr-scene-with-a-compute-pass-3.md)
- [A mod compute pass recorded into KSA's frame just before bloom](../../../../mods/ksarmory/features/compute-pass-before-bloom.md)
- [Insert a custom Vulkan pass into KSA's frame](../insert-a-custom-pass-into-render-game/overview.md), [Add dynamic spot and point lights to the world from a mod](../../lights-and-particles/add-dynamic-lights-to-the-world/overview.md), [Project decals onto vessels, terrain and clutter](../../textures-and-decals/project-decals-onto-scene-geometry/overview.md)
