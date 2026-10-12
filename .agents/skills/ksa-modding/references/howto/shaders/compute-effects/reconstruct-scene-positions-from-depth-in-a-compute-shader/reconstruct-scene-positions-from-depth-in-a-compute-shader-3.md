---
type: How-To
title: "Draw thousands of depth-tested streaks: project on the CPU, gather per tile, add before bloom"
description: Project line segments to screen pixels with camera-relative doubles, gather the ones reaching each 16x16 tile in shared memory, depth-test them against the scene in NDC and add their light to the HDR image before bloom
category: shaders
group: compute-effects
topic: reconstruct-scene-positions-from-depth-in-a-compute-shader
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony, vulkan]
demonstrated_by: [mods/ksarmory/features/compute-shell-tracers]
techniques: [glsl-shader, vulkan-buffer, harmony-prefix]
hooks: [harmony-prefix KSA.SunbloomRenderer.Render]
lifecycle: [frame:draw-ui, frame:celestials, render:bloom]
game_types:
  - KSA.SunbloomRenderer
  - KSA.Camera
  - KSA.ViewProjection
  - Brutal.VulkanApi.Abstractions.BufferEx
  - KSA.Rendering.ComputePipelineWrapper
  - KSA.GizmosRenderer
  - RenderCore.ReverseDepthBufferUtils
tags: [tracers, streaks, tile-gather, shared-memory, additive, bloom, depth-test, gizmos, compute, ksarmory]
---
A CIWS burst in KSArmory puts 150 shells in the air and none of them is a vehicle or a particle, so the engine has nothing to draw them with. The mod projects every round to a screen-space segment on the CPU, uploads the segments, and draws the lot with one compute dispatch: a hot core with a halo for tracers, a faint grey streak for plain rounds, depth-tested against the scene and added into the HDR image before bloom so tracers bloom like any other light. This article is the second half of [Gun tracers added into the scene by one tiled compute dispatch](../../../../mods/ksarmory/features/compute-shell-tracers.md): the tile gather and the evaluation. The buffer is in [the storage-buffer approach](../pack-parameters-for-a-compute-effect/pack-parameters-for-a-compute-effect-3.md). This approach differs from the other two in this topic in one way: it does not reconstruct a world point per pixel. It projects forward to pixels and compares NDC depth, which is cheaper when the things drawn are few thin lines.

## Background: how the game does it

The game offers gizmo lines for debug-style overlays, but they are drawn as geometry with no bloom contribution and no per-pixel width control. What the game does give is the same state the other compute effects use: the viewport's HDR colour image as a storage image and the depth image as a sampler, just before `SunbloomRenderer.Render` and bloom ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4911-L4917)). Depth is reverse-Z (larger is nearer, zero is sky). `Camera.EgoToClipDouble` projects a camera-relative point in double precision, giving clip coordinates whose `z / w` is device depth in the same convention as the depth image.

## Lifecycle and timing

| Moment | What happens |
|---|---|
| `frame:draw-ui` or `frame:celestials`, tail of the mod's simulation step | `ShellTracers.BeginFrame()` then `Collect(system)` for each weapon system with rounds; nothing is projected yet |
| `render:bloom`, prefix on `SunbloomRenderer.Render` (Program.cs L4915 main view, L4584 camera windows) | `CloudPass.Tracers` calls `ShellTracers.Build` with that viewport's camera, uploads the segments, dispatches |
| `frame:draw-ui` (UI pass) | `Visuals.DrawShellStream` draws gizmo lines only if the pass is not painting |

Rounds are collected after the step rather than in the UI pass so tracers stay up with the HUD hidden: they are the rounds, not an annotation. They are projected at record time against the render camera, so each camera window gets its own projection. Order inside the pass: after rings, holes and leak streams; with clouds, after the clouds (a cloud composites over what is already there and would hide a tracer in front of it) and before the whiteout. See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Project head and tail in double, then clip to the front of the camera.** Difference against the camera position in double, project both ends with `EgoToClipDouble`, and cut the segment at a small `w` so a streak beside the eye keeps its visible half instead of folding through infinity:

   ```csharp
   // src/KSArmory/Ksa/ShellTracers.cs L133-L144
                   // Differenced against the camera in double and only then projected.
                   double3 head = headEcl - cameraEcl;
                   double3 tail = head - (along * length);
                   if (!Vec.IsFinite(head)) continue;

                   double4 h = camera.EgoToClipDouble(head);
                   double4 t = camera.EgoToClipDouble(tail);
                   if (!TracerLook.TryClipToFront(ref h, ref t, NearestW))
                   {
                       behind++;
                       continue;
                   }
   ```

   Source: [src/KSArmory/Ksa/ShellTracers.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ShellTracers.cs#L133-L144). The position comes from `system.TryRoundEffectEcl(round, ...)`, the same call that places the round's body and plume, so the planet's motion cancels and the tracer sits on the round. The tail is one exposure behind: speed times 40 ms times the simulation speed, clamped to 0.3 to 60 m and never longer than the round has flown ([src/KSArmory/Sim/TracerLook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TracerLook.cs#L91-L96)).

2. **Choose the pixel width from the camera's own projection.** Project a point one metre sideways from the head and measure the pixel distance, so the width follows the camera rather than a field of view read from elsewhere; derive core width and brightness from it (a tracer core is 0.12 m, minimum 1.5 px, with brightness scaled by what the pixel width lacks):

   ```csharp
   // src/KSArmory/Ksa/ShellTracers.cs L146-L154
                   // Pixels a metre spans across the line of sight at the head, off the camera's own
                   // projection rather than a field of view read from somewhere else.
                   double4 aside = camera.EgoToClipDouble(head + Vec.Unit(Vec.AnyPerpendicular(head)));
                   if (!(aside.W > NearestW)) continue;

                   (double hx, double hy) = Pixel(h, width, height);
                   (double tx, double ty) = Pixel(t, width, height);
                   (double ax, double ay) = Pixel(aside, width, height);
                   double perMetre = Math.Sqrt(((ax - hx) * (ax - hx)) + ((ay - hy) * (ay - hy)));
   ```

   Source: [src/KSArmory/Ksa/ShellTracers.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ShellTracers.cs#L146-L154).

3. **Encode a segment and grow the dirty box.** Each segment is two `float4`: head x, y, NDC depth and a level; tail x, y, NDC depth and the core half-width. A negative level marks a plain round. The union box of every segment, grown by three core widths plus two pixels, becomes the dispatch rectangle:

   ```csharp
   // src/KSArmory/Ksa/ShellTracers.cs L172-L182
                   into[n++] = new Segment
                   {
                       Head = new float4((float)hx, (float)hy, (float)(h.Z / h.W), (float)level),
                       Tail = new float4((float)tx, (float)ty, (float)(t.Z / t.W), (float)(core * 0.5)),
                   };

                   int reach = (int)Math.Ceiling(core * 3.0) + 2;
                   minX = Math.Min(minX, (int)Math.Floor(Math.Min(hx, tx)) - reach);
                   minY = Math.Min(minY, (int)Math.Floor(Math.Min(hy, ty)) - reach);
                   maxX = Math.Max(maxX, (int)Math.Ceiling(Math.Max(hx, tx)) + reach);
                   maxY = Math.Max(maxY, (int)Math.Ceiling(Math.Max(hy, ty)) + reach);
   ```

   Source: [src/KSArmory/Ksa/ShellTracers.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ShellTracers.cs#L172-L182). The loop runs twice, glowing tracers first and plain rounds second, so if the 4096-per-view cap is hit it is the faint rounds that are dropped.

4. **Gather the segments reaching each tile in shared memory.** The shader is `local_size 16x16`. The 256 invocations of a workgroup stride over all the segments, test each segment's halo-padded bounding box against the tile, and append its index to a `shared` list with `atomicAdd` (at most 256 per tile):

   ```glsl
   // src/KSArmory/Shaders/KSArmoryTracer.comp L45-L78
   shared int tileCount;
   shared int tileList[MostPerTile];

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

   Source: [src/KSArmory/Shaders/KSArmoryTracer.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryTracer.comp#L45-L78). Compare the bitmask gather in [the hole approach](reconstruct-scene-positions-from-depth-in-a-compute-shader-2.md): a list is enough here because the result is purely additive, so the order streaks are summed in does not matter (floating-point sum order differences are invisible), whereas overlapping opaque marks would flicker.

5. **Depth-test in NDC, evaluate, and add.** Each pixel walks only its tile's list: distance to the segment, rejection beyond five half-widths plus 1.5 px, and an affine interpolation of NDC depth along the segment compared with the scene depth (reverse-Z, so skip when the streak is behind: `z < sceneZ * 0.9995`, a little slack so a tracer grazing the ground is not dashed):

   ```glsl
   // src/KSArmory/Shaders/KSArmoryTracer.comp L80-L121
       // Reverse-Z: larger is nearer, and zero is the sky.
       float sceneZ = texelFetch(sceneDepth, at, 0).r;
       vec2 p = vec2(at) + 0.5;

       float light = 0.0;
       float ball = 0.0;
       for (int k = 0; k < n; k++)
       {
           Segment s = segments[first + tileList[k]];

           vec2 a = s.Head.xy;
           vec2 ab = s.Tail.xy - a;
           float len2 = dot(ab, ab);
           float t = len2 > 1.0e-6 ? clamp(dot(p - a, ab) / len2, 0.0, 1.0) : 0.0;
           float d = length(p - (a + ab * t));

           float halfW = s.Tail.w;
           if (d > halfW * HaloWidths + 1.5) continue;

           // Depth varies linearly across the screen along a line, so it can be interpolated in
           // pixels. A hair of slack, so a tracer grazing the ground is not cut to dashes.
           float z = mix(s.Head.z, s.Tail.z, t);
           if (z < sceneZ * 0.9995) continue;

           float core = 1.0 - smoothstep(halfW - 0.5, halfW + 0.5, d);

           if (s.Head.w < 0.0)
           {
               ball += -s.Head.w * core;
               continue;
           }

           float halo = exp(-(d * d) / (2.0 * halfW * halfW * HaloWidths * HaloWidths * 0.25));
           float along = mix(1.0, TailShare, t);

           light += s.Head.w * along * (core + pc.Colour.w * halo);
       }

       if (light <= 0.0 && ball <= 0.0) return;

       vec4 was = imageLoad(sceneColor, at);
       imageStore(sceneColor, at, vec4(was.rgb + pc.Colour.rgb * light + pc.BallColour.rgb * ball, was.a));
   ```

   Source: [src/KSArmory/Shaders/KSArmoryTracer.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryTracer.comp#L80-L121). A tracer's level is about 24 (a bright core that blooms), a plain round's is the negative of a daylight-scaled brightness: full at 10 degrees of sun elevation, zero at minus 6 degrees.

6. **Dispatch over the dirty box only, and keep a fallback.** The dispatch is `ceil(w/16) x ceil(h/16)` over the box (see [the storage-buffer approach](../pack-parameters-for-a-compute-effect/pack-parameters-for-a-compute-effect-3.md) for the push header and slice). `ShellTracers.MarkPainted()` is called whether or not anything is drawn; when the pass is not painting (its shader failed, or the hook is missing, within a 0.5 s wall-clock window) the UI pass falls back to gizmo lines, a short segment along each shell's own flight:

   ```csharp
   // src/KSArmory/Ksa/Visuals.cs L103-L108
       public static void DrawShellStream(WeaponSystem system)
       {
           if (ShellTracers.Painting) return;
           if (system.Rounds.Count == 0 || system.Platform is not { } platform) return;
           if (!KsaWorld.BeginDraw(platform, system.PlatformEcl)) return;
   ```

   Source: [src/KSArmory/Ksa/Visuals.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Visuals.cs#L103-L108). The fallback streak is 14 m long and never longer than the round has flown, so it does not start behind the muzzle.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/ShellTracers.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/ShellTracers.cs) | Collection, two-pass projection into segments, painting flag |
| [src/KSArmory/Sim/TracerLook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/TracerLook.cs) | Exposure length, burn, core and ball width, daylight, near-plane clip |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs) | `TracerPush`, per-view slice, dispatch, buffer and pipeline build (L1577-L1721) |
| [src/KSArmory/Shaders/KSArmoryTracer.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryTracer.comp) | Shared-memory gather, depth-tested additive streaks |
| [src/KSArmory/Ksa/Visuals.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Visuals.cs) | `DrawShellStream` gizmo fallback |

## Variations and gotchas

- **Paired placement or nothing.** A tracer resolved from the round's raw ecliptic position instead of the shared placement call sits a step of the planet's roughly 30 km/s away from the round's body and plume.
- **256 per tile.** Past that many streaks through one tile the rest are dropped there; the tile is already saturated. The list is unordered, which is fine for an additive result but would not be for opaque marks.
- **The painting flag is time-based.** `Painting` is a 0.5 s window after the pass last reached the tracers. If `CloudPass.Record` returns early (no nearby celestial, or the weather shadow buffers unreadable), tracers are not reached and gizmo lines take over after 0.5 s.
- **CPU scales with the air, GPU with the screen.** `Build` walks every collected round per recorded view; only the GPU side is bounded by the tile box.
- **Not for opaque things.** The streaks add light and never occlude; anything that must hide what is behind it needs the write-over approach of the ring and hole shaders.
- **Two views in one frame need separate buffer slices** (four per frame in flight are budgeted).

## See also

- Siblings: [rings from depth](reconstruct-scene-positions-from-depth-in-a-compute-shader-1.md), [holes on a moving hull](reconstruct-scene-positions-from-depth-in-a-compute-shader-2.md), [overview](overview.md)
- [Gun tracers added into the scene by one tiled compute dispatch](../../../../mods/ksarmory/features/compute-shell-tracers.md), [World-anchored overlays through KSA's gizmo renderer](../../../../mods/ksarmory/features/gizmo-world-overlays.md), [A mod compute pass recorded into KSA's frame just before bloom](../../../../mods/ksarmory/features/compute-pass-before-bloom.md)
- [Pack parameters and camera data for a compute effect](../pack-parameters-for-a-compute-effect/overview.md), [Project world and sky positions to screen pixels and point at off-screen targets](../../../rendering/overlays/project-world-positions-to-screen/overview.md)
