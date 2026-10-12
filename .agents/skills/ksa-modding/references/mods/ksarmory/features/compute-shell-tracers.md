---
type: Mod Feature
title: Gun tracers added into the scene by one tiled compute dispatch
description: Gun rounds are projected on the CPU into screen-space segments in a host-visible storage buffer when the pass records, and one compute dispatch gathers them per 16x16 tile and adds depth-tested tracer streaks before bloom, with gizmo lines as the fallback
mod: ksarmory
category: shaders
techniques:
  - harmony-prefix
  - glsl-shader
  - vulkan-pipeline
  - vulkan-buffer
  - reflection
  - post-processing
  - tiled-light-culling
  - host-visible-ring-buffer
hooks:
  - starmap StarMapAfterGui
  - harmony-prefix KSA.Program.OnFrameCelestials
  - harmony-prefix KSA.SunbloomRenderer.Render
  - reflection Core.KSADeviceContextEx.Allocator
lifecycle: [frame:draw-ui, frame:celestials, render:bloom]
game_types:
  - KSA.SunbloomRenderer
  - KSA.Camera
  - KSA.Universe
  - KSA.Program
  - KSA.KsaVmaAllocator
  - Core.Renderer
  - Core.KSADeviceContextEx
  - Brutal.VulkanApi.Abstractions.IBufferAllocator
  - Brutal.VulkanApi.Abstractions.BufferEx
  - Brutal.VulkanApi.Abstractions.MappedMemory
  - KSA.Rendering.ComputePipelineWrapper
  - KSA.GizmosRenderer
tags: [tracers, gunnery, compute-shader, storage-buffer, tiled-gather, motion-blur, bloom, depth-test]
---

KSArmory's guns fire up to thousands of mod-simulated shells (a CIWS burst is 150 in the air), and
none of them is a vehicle or a particle, so the engine has nothing to draw them with. The mod draws
every gun round itself: a tracer as a hot orange core with a halo, added into the HDR scene before
bloom so it blooms like any other light; a round with no tracer as a faint grey streak of its own
calibre, only by daylight. All of them go in one compute dispatch over the patch of screen they
cover, depth-tested against the scene. When the pass is not painting (its shader failed, or the
hook is missing) the old gizmo lines come back.[^ksarmory-tracer-comp][^ksarmory-shelltracers]

## How it works

**1. Collect after the step.** `StepSimulation` (tail) calls `ShellTracers.BeginFrame()` then
`ShellTracers.Collect(system)` for every crewed weapon and every loose one (a destroyed launcher's
rounds keep their tracers). Only systems with rounds are kept; nothing is projected yet.[^ksarmory-mod]

**2. Resolve and project when the pass records** (`ShellTracers.Build`, called from
`CloudPass.Tracers` with the render camera of the viewport being drawn):

| Step | Detail |
|---|---|
| Stale guard | Nothing if the collection is older than 0.5 s (wall clock) |
| Filter | `Slug` rounds whose tube marks a gun (`RoundLabel.IsGunRound`), `Flying`, not drawn as a body (`ShellDrawnAsBody`) |
| Two passes | Glowing tracers first, then plain rounds, so if the 4096-per-view cap is hit it is faint rounds that are dropped |
| Glowing | `slug.Tracer && Age < Munition.TracerBurnSeconds` (3 s default); `Tracer` set at firing by `TracerLook.IsTracer(fedThrough, barrel, TracerEvery)`, one in `TracerEvery` per barrel, staggered by barrel[^ksarmory-weaponsystem][^ksarmory-munitionprofile] |
| Brightness | `TracerLook.Burn`: fades in over the first 25 m out of the muzzle, out over the last 0.3 s |
| Position | `system.TryRoundEffectEcl(round)`: the same call that places a round's body, plume and chase camera, so the ecliptic motion cancels and the tracer sits on the round |
| Length | `TracerLook.LengthMetres(speed, simSpeed, flown)`: speed x 40 ms x simulation speed (0 paused), clamped 0.3 to 60 m and never longer than the round has flown |
| Project | Differenced against `camera.PositionEcl` in double, `Camera.EgoToClipDouble` for head and tail, `TracerLook.TryClipToFront` cuts at `w = 0.05` so a streak beside the eye keeps its visible half |
| Width | Pixels per metre at the head from a 1 m sideways offset projected through the same camera; tracer core `0.12 m` wide, min 1.5 px with brightness scaled by what it lacks; plain round its calibre, min 1 px, brightness floor 50% |
| Encode | `Segment { Head = (x, y, ndcZ, level), Tail = (x, y, ndcZ, halfWidth) }`; level is `24 x burn x share` for a tracer and **negative** `BallBrightness x share x burn` for a plain round; bounding box grown by `3 x core + 2` px |

`BallBrightness` is `Config.BallRoundBrightness` (1.5) times `TracerLook.Daylight(sunElevation)`,
full at 10 deg sun, zero at -6 deg (civil twilight), from `KsaWorld.SunElevationDeg`.[^ksarmory-tracerlook][^ksarmory-ksaworld]

**3. Buffer** (`CloudPass.BuildTracerBuffer`, once per process):[^ksarmory-cloudpass]

- The allocator is read with `typeof(Renderer).GetProperty("Allocator")` and used as
  `IBufferAllocator`, because its concrete type (`KsaVmaAllocator`) also implements an interface from
  `Brutal.Vulkan.Vma`, which the mod does not reference.[^ksa-ksadevicecontextex][^ksa-ksavmaallocator]
- `allocator.CreateBuffer(new BufferEx.CreateInfo { StorageBufferBit, HostVisible | HostCoherent,
  size = MaxFramesInFlight x 4 views x 4096 x 32 bytes })`, mapped once (`buffer.Map()`) for the
  process.[^ksa-bufferexextensions][^ksa-mappedmemory]
- Each record writes its own slice: `slot = (frameIndex mod framesInFlight) x 4 + viewInFrame`, so two
  viewports in one frame, or the frame the GPU is still reading, never share a run.

**4. Dispatch.** `CloudPass.Tracers` calls `ShellTracers.MarkPainted()` (whether or not anything is
drawn), builds the segments into its slice, clamps the box to the screen, and pushes
`TracerPush { First, Count, OriginX, OriginY, Colour = (1.0, 0.42, 0.12, halo 0.12), BallColour =
(0.9, 0.85, 0.75) }` (48 bytes). A hazard barrier, then
`Dispatch(ceil(w/16), ceil(h/16), 1)` over the box only.

**5. Shader** (`KSArmoryTracer.comp`, `local_size 16x16`; set 1: 0 = scene colour storage,
1 = depth sampler, 2 = `std430` segment buffer, the wrapper's binding order):[^ksa-computepipelinewrapper]

1. Tiled gather: the 256 invocations stride over all `Count` segments, test each segment's
   halo-padded bounding box against the 16x16 tile and append its index to a `shared` list
   (`atomicAdd`, at most 256 per tile). `barrier()`.
2. Each pixel walks only its tile's list: distance to the segment, reject beyond `5 x halfWidth + 1.5`.
3. Depth test: NDC depth interpolated along the segment (affine in screen space); reverse-Z, so
   skip when `z < sceneZ x 0.9995` (a little slack so a tracer grazing the ground is not dashed).
4. Tracer: `level x mix(1, 0.25, t) x (core + 0.12 x gaussian halo)`; plain round: `-level x core`.
5. `scene.rgb += Colour x light + BallColour x ball`. Purely additive, so order with other additive
   effects does not matter.

**Fallback.** `Visuals.DrawShellStream`, called every UI pass, returns at once while
`ShellTracers.Painting` (the pass reached the tracers within 0.5 s). Otherwise it draws a 14 m
gizmo line behind each shell along `VelocityLocal`.[^ksarmory-visuals]

## Lifecycle and timing

| Moment | Where (build 5554) | What happens |
|---|---|---|
| `frame:draw-ui` / `frame:celestials` | `StepSimulation` tail (KSArmoryMod.cs L873-L918), from `StepOnce` in the AfterGui postfix (Program.cs L2268) or, HUD hidden, the `OnFrameCelestials` prefix (L2320) | Rounds stepped, systems collected |
| `frame:draw-ui` | `OnAfterGui`, `Visuals.DrawShellStream` (KSArmoryMod.cs L526-L531) | Gizmo fallback only if the pass is not painting |
| `render:bloom` | Prefix on `SunbloomRenderer.Render`; main view at Program.cs L4915 just before `_overallBloomRenderer.Render` (L4916), camera windows via `RenderViewport` L4584 | `CloudPass.Tracers` -> `ShellTracers.Build` -> dispatch[^ksa-program] |

- **Collected after the step, not in the UI pass**, so tracers stay up with the HUD hidden: they
  are the rounds, not an annotation. Rings and sights, by contrast, go with the HUD.
- **Projected at record time**, against `Program.GetRenderCamera()` (the camera of the viewport
  being recorded), so each camera window gets its own projection and the main view's matrices are
  the ones the scene was just drawn with.
- **Order inside `CloudPass.Record`:** with the shader pass at zero, after rings, holes and leak
  streams; with no cloud standing, after the sky dispatches and before the whiteout; with clouds,
  after the clouds and shock fronts (a cloud composites over what is already there and would hide a
  tracer in front of it) and before the whiteout.
- **Before bloom**, so the 24-unit core blooms and the tonemap exposes it with the scene.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/ShellTracers.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ShellTracers.cs) | Collection, two-pass projection into segments, painting flag |
| [src/KSArmory/Sim/TracerLook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TracerLook.cs) | Exposure length, burn, core and ball width, daylight, near-plane clip, tracer cadence |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L1577-L1721) | `TracerPush`, per-view buffer slice, dispatch, buffer and pipeline build |
| [src/KSArmory/Shaders/KSArmoryTracer.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryTracer.comp) | Shared-memory tile gather, depth-tested additive streaks |
| [src/KSArmory/Ksa/Visuals.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Visuals.cs#L95-L134) | `DrawShellStream` gizmo fallback |
| [src/KSArmory/Ksa/WeaponSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/WeaponSystem.cs#L106-L124) | `TryRoundEffectEcl`, the shared round placement |

## Snippets

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

```glsl
// src/KSArmory/Shaders/KSArmoryTracer.comp L48-L78
void main()
{
    ivec2 size = imageSize(sceneColor);
    ivec2 origin = pc.FirstCountOrigin.zw;
    ivec2 tileMin = origin + ivec2(gl_WorkGroupID.xy) * 16;
    vec2 lo = vec2(tileMin);
    vec2 hi = lo + vec2(16.0);

    if (gl_LocalInvocationIndex == 0u) tileCount = 0;
    barrier();

    int first = pc.FirstCountOrigin.x;
    int count = pc.FirstCountOrigin.y;
    for (int i = int(gl_LocalInvocationIndex); i < count; i += 256)
    {
        Segment s = segments[first + i];
        float reach = s.Tail.w * HaloWidths + 2.0;
        vec2 sLo = min(s.Head.xy, s.Tail.xy) - vec2(reach);
        vec2 sHi = max(s.Head.xy, s.Tail.xy) + vec2(reach);
        if (any(greaterThan(sLo, hi)) || any(lessThan(sHi, lo))) continue;

        int slot = atomicAdd(tileCount, 1);
        if (slot < MostPerTile) tileList[slot] = i;
    }
    barrier();

    ivec2 at = tileMin + ivec2(gl_LocalInvocationID.xy);
    if (at.x >= size.x || at.y >= size.y) return;

    int n = min(tileCount, MostPerTile);
    if (n == 0) return;
```

```csharp
// src/KSArmory/Sim/TracerLook.cs L91-L96
    public static double LengthMetres(double speed, double worldPerPlayerSecond, double flownMetres)
    {
        double exposed = Math.Max(speed, 0.0) * PersistenceSeconds * Math.Max(worldPerPlayerSecond, 0.0);
        double length = Math.Clamp(exposed, ShortestMetres, LongestMetres);
        return Math.Min(length, Math.Max(flownMetres, 0.0));
    }
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `KSADeviceContextEx.Allocator` (base of `Renderer`) | [current/decomp/Core/KSADeviceContextEx.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/KSADeviceContextEx.cs#L67) | `KsaVmaAllocator`, read by reflection, used as `IBufferAllocator` ([current/decomp/KSA/KsaVmaAllocator.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KsaVmaAllocator.cs#L12) L12) |
| `Renderer.MaxFramesInFlight` | [current/decomp/Core/Renderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Core/Renderer.cs#L28) | Buffer slices per frame in flight[^ksa-renderer] |
| `IBufferAllocator.CreateBuffer` (extension), `BufferEx.CreateInfo`, `Map`, `MappedMemory.AsSpan<T>` | [current/decomp/Brutal.VulkanApi.Abstractions/BufferExExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/BufferExExtensions.cs#L5) | Persistent host-coherent storage buffer, written as a `Span<Segment>` ([current/decomp/Brutal.VulkanApi.Abstractions/MappedMemory.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.VulkanApi.Abstractions/MappedMemory.cs#L101) L101) |
| `ComputePipelineWrapper` ctor (`storageBuffers:`) | [current/decomp/KSA.Rendering/ComputePipelineWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ComputePipelineWrapper.cs#L32) | Storage buffer bound after the images and samplers |
| `Camera.EgoToClipDouble`, `PositionEcl`, `NearbyCelestial` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L277) | Double-precision projection of head, tail and width probe[^ksa-camera] |
| `Universe.SimulationSpeed`, `IsPaused` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L122) | Streak length scales with world speed; zero paused (L1799)[^ksa-universe] |
| `Program.GetRenderCamera`, `RenderGame`, `RenderViewport` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L641) | Per-viewport camera inside the prefix |

## Pitfalls and version notes

- **Paired placement or nothing.** A tracer resolved from `round.PositionEcl` instead of
  `TryRoundEffectEcl` sits a step of the planet's ~30 km/s away from the round's body and plume.
- **Two views in one frame need separate slices** of a host-coherent buffer; four per frame in
  flight is the budget, after which views wrap and could overwrite one another.
- **The painting flag is time-based.** `Painting` is a 0.5 s wall-clock window after the pass last
  reached `Tracers`. With the shader pass on but `CloudPass.Record` returning early (no
  `NearbyCelestial`, or the weather shadow buffers unreadable, CloudPass.cs L427-L429), tracers are
  not reached, and after 0.5 s the gizmo lines take over.
- **The pass costs what is on screen, the CPU costs what is in the air.** `Build` walks every
  collected round per recorded view; only the GPU side is bounded by the tile box.
- **`MostPerTile = 256`.** Past that many streaks through one tile the rest are dropped there; it is
  already saturated.
- `docs/BLOCKED-ON-KSA.md` describes the tracers as the one shader effect the prefix made possible;
  it agrees with the code.

## See also

- [Compute pass before bloom](./compute-pass-before-bloom.md)
- [Mod-simulated rounds](./mod-simulated-rounds.md)
- [Gizmo world overlays](./gizmo-world-overlays.md)
- [Round body draw hooks](./round-body-draw-hooks.md)
- [Holed tank leaks](./holed-tank-leaks.md)
- [Bullet-hole decals](./bullet-hole-decals.md)
- [Sensor look pass](./sensor-look-pass.md)
- [Depth-reading compute dispatches for rings, holes, tracers and relighting](../../../howto/rendering/vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/modify-the-hdr-scene-with-a-compute-pass-1.md) - how-to article demonstrating this feature
- [Feed a compute shader thousands of items through a host-visible storage buffer](../../../howto/shaders/compute-effects/pack-parameters-for-a-compute-effect/pack-parameters-for-a-compute-effect-3.md) - how-to article demonstrating this feature
- [Draw thousands of depth-tested streaks: project on the CPU, gather per tile, add before bloom](../../../howto/shaders/compute-effects/reconstruct-scene-positions-from-depth-in-a-compute-shader/reconstruct-scene-positions-from-depth-in-a-compute-shader-3.md) - how-to article demonstrating this feature

[^ksarmory-shelltracers]: src/KSArmory/Ksa/ShellTracers.cs
[^ksarmory-tracer-comp]: src/KSArmory/Shaders/KSArmoryTracer.comp
[^ksarmory-tracerlook]: src/KSArmory/Sim/TracerLook.cs
[^ksarmory-cloudpass]: src/KSArmory/Ksa/CloudPass.cs
[^ksarmory-visuals]: src/KSArmory/Ksa/Visuals.cs
[^ksarmory-weaponsystem]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksarmory-munitionprofile]: src/KSArmory/Sim/MunitionProfile.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksa-program]: KSA.Program (decompiled)
[^ksa-camera]: KSA.Camera (decompiled)
[^ksa-universe]: KSA.Universe (decompiled)
[^ksa-ksadevicecontextex]: Core.KSADeviceContextEx (decompiled)
[^ksa-renderer]: Core.Renderer (decompiled)
[^ksa-ksavmaallocator]: KSA.KsaVmaAllocator (decompiled)
[^ksa-bufferexextensions]: Brutal.VulkanApi.Abstractions.BufferExExtensions (decompiled)
[^ksa-mappedmemory]: Brutal.VulkanApi.Abstractions.MappedMemory (decompiled)
[^ksa-computepipelinewrapper]: KSA.Rendering.ComputePipelineWrapper (decompiled)
