---
type: How-To
title: "Paint an exact ring on the ground: depth reconstruction, one dispatch per ring"
description: Draw circles and ellipses that lie on terrain at any distance by rebuilding each pixel's world point from the depth buffer, measuring it against a body-anchored centre, and dispatching only over the ring's screen patch
category: shaders
group: compute-effects
topic: reconstruct-scene-positions-from-depth-in-a-compute-shader
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony, vulkan]
demonstrated_by: [mods/ksarmory/features/depth-painted-ground-rings]
techniques: [glsl-shader, vulkan-pipeline, harmony-prefix]
hooks: [harmony-prefix KSA.SunbloomRenderer.Render]
lifecycle: [frame:draw-ui, render:bloom]
game_types:
  - KSA.SunbloomRenderer
  - KSA.Rendering.ComputePipelineWrapper
  - KSA.Camera
  - KSA.Celestial
  - RenderCore.ReverseDepthBufferUtils
  - KSA.ImageBarrierInfo
tags: [depth, reverse-z, ring, ellipse, ground, body-fixed, tile, compute, ksarmory]
---
KSArmory shows every targeting ring (bomb sight impact ring, landing ring, designation marker, ICBM aim and divert-reach ellipse) as a line painted into the scene image by a compute shader. Each ring is a circle or ellipse measured from the scene point the depth buffer holds under each pixel, so it lies on terrain, a pad or a rock at any size, and its line stays a fixed number of pixels wide however far off or oblique. This article is that technique, from [Targeting rings painted on the depth buffer](../../../../mods/ksarmory/features/depth-painted-ground-rings.md). It assumes the pass is recorded as in [Paint into or rescale the HDR scene with a compute pass shipped as a Shader asset](../../../rendering/vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/overview.md).

## Background: how the game does it

The game draws nothing like this; its own overlays are gizmo lines and ImGui. What it provides is the data. In `Program.RenderGame`, just before the bloom pass, the viewport's colour image is in a storage layout and its depth image in a sampled layout:

```csharp
// current/decomp/KSA/Program.cs L4911-L4917
		BarrierBatch barrierBatch3 = new BarrierBatch(inImages);
		barrierBatch3.Add(_offscreenTarget.ColorImage, ImageBarrierInfo.Presets.StorageReadFc);
		barrierBatch3.Add(_offscreenTarget.DepthImage, ImageBarrierInfo.Presets.DepthSampledReadVfc);
		barrierBatch3.SubmitAndFlush(commandBuffer2);
		_sunbloomRenderer.Render(commandBuffer2, MainViewport, resourceFrameIndex);
		_overallBloomRenderer.Render(commandBuffer2, MainViewport, resourceFrameIndex);
		PartSelectedRenderer.Run(commandBuffer2);
```

Source: [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4911-L4917). A prefix on `SunbloomRenderer.Render` therefore gets storage access to colour and a sampler on depth, and anything it writes is bloomed and tonemapped with the scene. Depth is reverse-Z (near is 1, sky is 0) and `Camera.VPInv` maps clip space to camera-relative world.

## Lifecycle and timing

| Moment | What happens |
|---|---|
| `frame:draw-ui` (the `[StarMapAfterGui]` callback) | `GroundRings.BeginFrame()`, then each overlay adds its rings |
| `render:bloom` (prefix on `SunbloomRenderer.Render`, per viewport) | `CloudPass.Rings` resolves each ring against that viewport's camera and dispatches |

The rings are collected in the UI pass on purpose: a ring is an operator's sight, so it goes with the HUD (KSA skips `OnDrawUiViewports` while the UI is hidden). A wall-clock `Fresh` test lets the last list stand for 0.5 s so screenshots do not flicker. They are resolved to ecliptic positions at record time, against the camera of that instant, so a ring does not lag the planet's roughly 30 km/s. See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Anchor the ring to the body, not to space.** An ecliptic coordinate is left behind at the planet's speed the instant it is written. Store each ring as an offset in the nearest body's own frame, using the conjugate of `Celestial.GetBodyFixed2Ecl()`, and convert back each time you draw:

   ```csharp
   // src/KSArmory/Ksa/KsaWorld.cs L957-L959
               body = nearest;
               anchor = doubleQuat.Conjugate(nearest.GetBodyFixed2Ecl()) * (pointEcl - nearest.GetPositionEcl());
               return Vec.IsFinite(anchor);
   ```

   The reverse is `GetPositionEcl() + GetBodyFixed2Ecl() x anchor` ([src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L957-L959) and `TryGroundAnchorEcl`). For an ellipse, store the centre and the long axis's tip as two anchors on the same body so the axis turns with the planet ([src/KSArmory/Ksa/GroundRings.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/GroundRings.cs#L98-L128)).

2. **Build one small pipeline: a storage image, the depth sampler, one push range.** Take the images from the viewport's offscreen target and create a `ComputePipelineWrapper` lazily per view:

   ```csharp
   // src/KSArmory/Ksa/CloudPass.cs L1766-L1781
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
   ```

   Source: [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L1766-L1781). The shader's bindings follow the wrapper's order: `set 1, binding 0` the colour image, `binding 1` the depth sampler.

3. **Resolve, project to a tile, fill the 128-byte push, dispatch.** Per ring: difference the centre against the camera in double, compute the screen tile the ring can reach, pack an ellipse's two directions with the octahedral encoding and mark it with a negative last field:

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

   Source: [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L1023-L1056). The packing is explained in [Pack parameters and camera data for a compute effect](../pack-parameters-for-a-compute-effect/overview.md); `Hazard` is a compute-write to compute-read memory barrier between consecutive dispatches that touch the same image.

4. **Bound the dispatch to whole workgroups around the ring.** `TileFor` projects the eight corners of a box around the ring with `Camera.EgoToClipDouble` and takes the screen rectangle, rounded out to workgroups. Any corner behind the camera (`clip.W <= 1e-6`) makes the dispatch full-screen, because projecting such a corner folds the box inside out:

   ```csharp
   // src/KSArmory/Ksa/CloudPass.cs L2111-L2133
           double lowX = double.MaxValue, lowY = double.MaxValue;
           double highX = double.MinValue, highY = double.MinValue;

           for (int corner = 0; corner < 8; corner++)
           {
               // Upwind only by the patch's ragged rim; downwind by the plume's whole run.
               double3 at = centreEgo
                            + (along * ((corner & 1) == 0 ? -radius * ScorchFootprint.Behind : reach))
                            + (side * ((corner & 2) == 0 ? -across : across))
                            + (other * ((corner & 4) == 0 ? -across : across));

               double4 clip = camera.EgoToClipDouble(at);
               if (!(clip.W > 1.0e-6) || !double.IsFinite(clip.W)) return whole;

               double x = ((clip.X / clip.W * 0.5) + 0.5) * width;
               double y = ((clip.Y / clip.W * 0.5) + 0.5) * height;
               if (!double.IsFinite(x) || !double.IsFinite(y)) return whole;

               lowX = Math.Min(lowX, x);
               highX = Math.Max(highX, x);
               lowY = Math.Min(lowY, y);
               highY = Math.Max(highY, y);
           }
   ```

   Source: [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L2111-L2133). A ring on the screen covers a small patch, so the dispatch costs what the ring covers.

5. **Reconstruct the scene point.** In the shader, skip sky (reverse-Z depth 0), and turn the pixel and its depth into a camera-relative point with the pushed inverse matrix:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryRing.comp L34-L39
   vec3 WorldAt(vec2 uv, float deviceZ)
   {
       vec4 clip = vec4(uv * 2.0 - 1.0, deviceZ, 1.0);
       vec4 p = pc.InvViewProj * clip;
       return p.xyz / p.w;
   }
   ```

   Source: [src/KSArmory/Shaders/KSArmoryRing.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryRing.comp#L34-L39).

6. **Measure against the ring and gate on height.** Take the height `h` of the point over the ring's plane and its distance `r` from the axis. A point more than `max(R, 50 m)` off the plane is not the ground (a craft or a tower), and is skipped:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryRing.comp L62-L85
       ivec2 size = imageSize(sceneColor);
       ivec2 at = ivec2(gl_GlobalInvocationID.xy) + ivec2(pc.TileWidth.xy);
       if (at.x >= size.x || at.y >= size.y) return;

       // Reverse-Z: zero is the sky, which a ring on the ground has no business with.
       float deviceZ = texelFetch(sceneDepth, at, 0).r;
       if (deviceZ <= 0.0) return;

       vec2 uv = (vec2(at) + 0.5) / vec2(size);
       vec3 p = WorldAt(uv, deviceZ);

       bool ellipse = pc.TileWidth.w < 0.0;
       vec3 up = ellipse ? OctahedralUnpack(pc.UpInner.xy) : pc.UpInner.xyz;
       vec3 d = p - pc.CentreRadius.xyz;
       float h = dot(d, up);
       vec3 across = d - up * h;
       float r = length(across);
       float R = pc.CentreRadius.w;

       // A craft or a tower standing well over the ring is not the ground it circles.
       if (abs(h) > max(R, 50.0) || r < 1.0e-3) return;

       vec3 dir = normalize(p);
       vec3 radial = across / r;
   ```

   Source: [src/KSArmory/Shaders/KSArmoryRing.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryRing.comp#L62-L85). For an ellipse the shader works with a normalised radius `n` and converts the excess over its gradient into a distance, so the line has one width all round (L87-L103).

7. **Keep the line a fixed number of pixels wide.** The width in world metres must follow how big a pixel is on the ground there. Rebuild the neighbour pixels at the same depth, slide each step along the view ray onto the ring's tangent plane, and use the radial component as the footprint:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryRing.comp L41-L58
   // How far along the ring's radius the ground moves for one pixel's step, off the tangent plane at
   // the ring: the step across the view at this depth, slid along the ray back onto the plane. Exact
   // for flat ground, and what keeps the line one width from the rim to the far side of the circle.
   float RadialStep(vec3 p, vec3 dir, vec3 step, vec3 up, vec3 radial)
   {
       float facing = dot(dir, up);
       facing = sign(facing) * max(abs(facing), 0.05);
       vec3 onGround = step - dir * (dot(step, up) / facing);
       return abs(dot(onGround, radial));
   }

   // Coverage of a line of the given half-width at radius R, and of the dark border round it.
   float Line(float r, float R, float footprint, float halfWidth, out float border)
   {
       float px = abs(r - R) / footprint;
       border = 1.0 - smoothstep(halfWidth + 0.5, halfWidth + 1.5, px);
       return 1.0 - smoothstep(halfWidth - 0.5, halfWidth + 0.5, px);
   }
   ```

   Source: [src/KSArmory/Shaders/KSArmoryRing.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryRing.comp#L41-L58). A footprint from the raw neighbour distance would swell the line toward the horizon; the tangent-plane step prevents it (clamped at a facing of 0.05).

8. **Shade relative to the ground and write.** The image is not yet exposed, so a fixed brightness would glare at night and vanish by day; the ink is the ground's luminance times five, with a floor, over a dark border:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryRing.comp L134-L143
       // Brighter than the ground it lies on by a fixed factor rather than by a fixed amount: the
       // image is not yet exposed, so a set brightness is a glare at night and invisible by day. The
       // floor keeps it readable on ground too dark to multiply.
       vec4 was = imageLoad(sceneColor, at);
       float ground = dot(was.rgb, vec3(0.2126, 0.7152, 0.0722));
       vec3 ink = pc.Colour.rgb * max(ground * 5.0, pc.Colour.w * 4.0);

       vec3 c = mix(was.rgb, was.rgb * 0.15, border * 0.85);
       c = mix(c, ink, cover);
       imageStore(sceneColor, at, vec4(c, was.a));
   ```

   Source: [src/KSArmory/Shaders/KSArmoryRing.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryRing.comp#L134-L143). Dashes are cut by the `atan` angle around `up` (55% duty) so they are the same share of the ring at any size.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/GroundRings.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/GroundRings.cs) | Per-frame ring list, body anchors, `Fresh`, `TryAt` |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs) | `Rings`, `RingPush`, `TileFor`, `BuildRings` |
| [src/KSArmory/Shaders/KSArmoryRing.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryRing.comp) | Depth reconstruction, circle and ellipse distance, fixed-pixel line |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs) | `TryAnchorToGround`, `TryGroundAnchorEcl` |

## Variations and gotchas

- **No fallback.** If the compute shader is missing or the hook is not installed, the rings do not draw at all; the author chose that over the earlier draped lines (80 terrain lookups a frame).
- **The 50 m height gate** means a ring will not paint on a structure much taller than the ring is wide; under a tall tower there is a gap.
- **Behind the camera.** Any box corner with `w <= 0` makes the dispatch full-screen, which is correct but costs a whole-screen pass.
- **Staleness is wall-clock.** `Fresh` uses a stopwatch, so a paused game with the HUD visible keeps renewing rings.
- **A craft in front of the ring** hides it, because the point under the pixel is the craft; the height gate stops the craft being painted as if it were the ground.
- **Order with clouds.** With a burst on screen the rings go after the scorch marks and before the clouds, so a cloud over a target covers its ring.

## See also

- Siblings: [procedural holes on a moving part](reconstruct-scene-positions-from-depth-in-a-compute-shader-2.md), [depth-tested streaks](reconstruct-scene-positions-from-depth-in-a-compute-shader-3.md), [overview](overview.md)
- [Targeting rings painted on the depth buffer](../../../../mods/ksarmory/features/depth-painted-ground-rings.md), [A mod compute pass recorded into KSA's frame just before bloom](../../../../mods/ksarmory/features/compute-pass-before-bloom.md), [World-anchored overlays through KSA's gizmo renderer](../../../../mods/ksarmory/features/gizmo-world-overlays.md)
- [Pack parameters and camera data for a compute effect](../pack-parameters-for-a-compute-effect/overview.md), [Project decals onto vessels, terrain and clutter](../../../rendering/textures-and-decals/project-decals-onto-scene-geometry/overview.md)
