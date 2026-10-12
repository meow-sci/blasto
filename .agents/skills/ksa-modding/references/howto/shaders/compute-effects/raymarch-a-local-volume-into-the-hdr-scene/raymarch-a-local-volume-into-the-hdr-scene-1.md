---
type: How-To
title: "Raymarch a noise-eroded volume into the HDR scene: bounding sphere, jitter and a reprojected resolve"
description: March a signed-distance torus, dome and column eroded by noise inside a bounding sphere, write it to a layer, blend the layer with reprojected history in a second pass and composite into the HDR scene before bloom
category: shaders
group: compute-effects
topic: raymarch-a-local-volume-into-the-hdr-scene
approach: 1
difficulty: expert
prerequisites: [starmap, harmony, vulkan]
demonstrated_by: [mods/ksarmory/features/raymarched-mushroom-cloud]
techniques: [glsl-shader, vulkan-pipeline, harmony-prefix]
hooks: [harmony-prefix KSA.SunbloomRenderer.Render]
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame, render:bloom]
game_types:
  - KSA.SunbloomRenderer
  - KSA.Rendering.RenderImage
  - KSA.Rendering.ComputePipelineWrapper
  - KSA.GpuProfiler
  - KSA.ProfilerTag
  - KSA.Camera
  - KSA.ViewProjection
tags: [raymarch, sdf, noise, volume, temporal, reprojection, history, profiler, compute, ksarmory]
---
KSArmory's nuclear cloud is neither mesh nor particles. The C# side keeps a list of bursts and a pure function that turns an age into a handful of dimensions; every frame, inside a compute pass recorded before bloom, each cloud is one full-screen dispatch of `KSArmoryCloud.comp` that raymarches a signed-distance torus, dome and column eroded by noise into an offscreen layer, and a second shader blends the layer with last frame's and composites it into the HDR scene. This article follows [A raymarched mushroom cloud, held body-fixed and drawn before bloom](../../../../mods/ksarmory/features/raymarched-mushroom-cloud.md) and generalises it: how to bound the march, spend steps in proportion to what the ray crosses, dither with a golden-ratio sequence, resolve temporally with a hand-filtered storage image, and measure the cost from the game's own GPU profiler. Lighting the volume with the game's atmosphere and hiding it behind weather is the next article; the push block packing is in [Pack parameters and camera data for a compute effect](../pack-parameters-for-a-compute-effect/overview.md).

## Background: how the game does it

The game offers the recording point and the data, and its own volumetric passes show the pattern. `SunbloomRenderer.Render` runs per viewport with the scene colour in a storage layout and the depth in a sampled layout, so a prefix can read depth and add or overwrite colour, and the result blooms and tonemaps with everything else ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4905-L4916)). KSA's own weather and exhaust volumes are also raymarched into images and composited, which is why the engine provides `RenderImage.CreateColorStorage` for storage-only images and `ComputePipelineWrapper` for binding them. GPU timing is available through `Profiler.Gpu` and `commandBuffer.TagRegion(ProfilerTag)` ([current/decomp/KSA/GpuRegionExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuRegionExtensions.cs#L10)).

## Lifecycle and timing

| Moment | What happens |
|---|---|
| `frame:draw-ui` (AfterGui postfix), or `frame:celestials` with the HUD hidden, or `frame:after-on-frame` as a fallback | The mod's step advances every cloud's age on simulated time (a pause freezes the cloud) |
| `render:bloom`, prefix on `SunbloomRenderer.Render`, per viewport | `CloudPass.Record`: each cloud's position is converted from the body-fixed frame with the body's state at record time; marches far to near, resolves once, composites |
| Next frame's step | `CloudPassCost.Sample()` reads the newest complete GPU profiler frame |

The age is advanced in the step rather than in drawing code because a cloud advanced by UI drawing would stop growing whenever the HUD is hidden. Inside the pass the order is fixed: fire light, ground marks, rings, holes, leak streams and sky layers, then the clouds far to near (at most eight, sorted by distance to the camera), then the resolve, the shock-front refraction, tracers and the flash. See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Describe the volume as a density function of position and a few parameters.** The mod's `Density` builds a torus plus a dome for the cap and a flared column for the stem, each a signed distance, from the cap height, radii and stem radius it is pushed:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryCloud.comp L858-L868
   float capH = pc.Shape.x;
   float ringR = max(pc.Shape.y - pc.Shape.z, pc.Shape.z);
   float tubeR = pc.Shape.z;
   // The collars are sized by a surface burst's stem, which the air rises round whatever it
   // lifted; the column of dust is the share of it the burst raised.
   float stemR = stemRadius;
   float columnR = stemR * stemShare;

   vec3 capP = vec3(p.x, p.y - capH, 0.0);
   float cap = min(Torus(capP, ringR, tubeR), CapDome(vec2(p.x, p.y - capH), ringR, tubeR));
   float stem = ColumnStem(p.xy, capH, columnR);
   ```

   Source: [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L858-L868)

   It first undoes the wind lean, then (not shown) advects the poloidal angle by `RollAngle(age)` so the cap rolls over like a vortex ring. Changing the shape is a matter of changing these primitives.

2. **Reject exactly before any noise.** Erosion can pull the surface in by only a bounded multiple of each feature's own radius, so a sample further outside than that cannot come back with density however the noise falls. The test is exact, and the fBm it skips is the most expensive thing in the shader:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryCloud.comp L909-L913
   if (cap > ErodeReach * tubeR && stem > ErodeReach * columnR && above > ErodeReach * tubeR)
   {
       wet = condensed > 0.0 ? 1.0 : 0.0;
       return max(condensed, drawnUp);
   }
   ```

   Source: [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L909-L913)

3. **Erode the distance with two noise bands.** The coarse band billows (a four-octave fBm at a frequency per cloud rather than per metre, so every yield carries about two dozen billows), the fine band is surface texture (one octave; a second fBm there cost 2.6 ms by itself). Density is the clamped negative distance over a feature-sized falloff, squared for a soft edge:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryCloud.comp L931-L950
   float coarse = Fbm(world * (26.0 / max(radius, 1.0)) + vec3(0.0, around, boil));
   // ONE octave, not an fBm. This band is surface texture at a single scale, and a second
   // four-octave fBm here is the most expensive line in the shader for detail that is then
   // exponentiated into a soft edge -- it cost 2.6 ms a frame on its own.
   float fine = Noise(world * (78.0 / max(radius, 1.0)) + vec3(around, 0.0, boil * 1.6));

   // EACH FEATURE CARRIES ITS OWN SCALE, for both the erosion and the falloff. Driving the whole
   // cloud off the cap's tube radius works only while everything is about that thick: once the
   // stem is a fifth of the cap rather than a half, a tube-sized erosion is wider than the column
   // it is cutting into and a tube-sized falloff caps its density at a third however deep inside
   // it the sample is. The column came out a wisp with the cap floating over it -- the lollipop,
   // arrived at through the density rather than through the proportions.
   float capErode = ((coarse - 0.42) * 1.05 + (fine - 0.50) * 0.30) * tubeR;
   float stemErode = ((coarse - 0.42) * 1.05 + (fine - 0.50) * 0.30) * columnR;

   float capDensity = clamp(-(cap - capErode) / max(tubeR * 0.85, 1.0), 0.0, 1.0);
   float stemDensity = clamp(-(stem - stemErode) / max(columnR * 0.85, 1.0), 0.0, 1.0);

   float density = max(capDensity, stemDensity);
   density = density * density;
   ```

   Source: [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L931-L950)

4. **Bound the march with a sphere and spend steps by chord length.** Intersect the ray with the push-constant bounding sphere; most pixels leave here. The step count is proportional to the chord, clamped to 6..48, so spacing is constant across the screen (a grazing ray used to spend all 48 steps on a sliver). The start is dithered by a hash plus the golden ratio times the frame counter from the global block, so the resolve averages the grain away:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryCloud.comp L2021-L2049
   float along = dot(toCentre, dir);
   float perpSq = dot(toCentre, toCentre) - along * along;
   float rSq = radius * radius;

   // Not a return: the ring of dust below runs far outside this sphere.
   float half_ = sqrt(max(rSq - perpSq, 0.0));
   float t0 = max(along - half_, 0.0);
   float t1 = min(along + half_, sceneT);
   bool throughCloud = perpSq <= rSq && t1 > t0;

   // Uniform march. The step is the span rather than a fixed metre count, so a cloud fills the
   // same number of samples whether it is two kilometres away or twenty.
   // Raised from 32 with the budget the raymarch freed. Grain is what a dither trades banding for,
   // and the only thing that actually removes it is samples: interleaved gradient noise was tried
   // in its place and is NOT in, because it is built to be resolved by temporal accumulation and
   // there is none here -- a single frame of it is diagonal hatching across the stem.
   //
   // The count follows the chord rather than being fixed, so the SPACING is the same everywhere: a ray
   // grazing the bound, or stopped by the ground inside it, took all 48 steps over a sliver of it. The
   // spacing is what the picture is made of, so it looks the same for about two thirds of the samples.
   const int MostSteps = 48;
   const int LeastSteps = 6;
   int Steps = clamp(int(ceil(float(MostSteps) * (t1 - t0) / (2.0 * radius))), LeastSteps, MostSteps);
   float dt = (t1 - t0) / float(Steps);

   // Start each ray a fraction of a step in. Fewer steps band, and a dither turns that banding
   // into grain. Moved on every frame by the golden ratio, so the resolve's history averages the
   // grain away; a pattern fixed per pixel is instead a screen door the cloud moves behind.
   float jitter = fract(Hash(vec3(vec2(at), 0.0)) + float(global.camera.frame % 256) * 0.6180340);
   ```

   Source: [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L2021-L2049)

   `radius` comes from `GrownBound(leanTop, age)`, which grows the pushed bound exactly as `MushroomCloud.GrownBound` does in C#; a test holds them equal (see [Pack parameters and camera data for a compute effect](../pack-parameters-for-a-compute-effect/overview.md)).

5. **March front to back with early exit.** Per sample: density, a few sun-ward taps, KSA's sky light (next article), then accumulate:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryCloud.comp L2280-L2297
   // What the step blocks, not its extinction: the two agree only while a step is optically
   // thin, and the march spans the bound in a fixed 48 steps -- kilometres each over a
   // megatonne-class cloud, where extinction times light counted it tens of times over.
   float through = exp(-extinction);

   scattered += transmittance * lit * (1.0 - through) * seenThrough;

   float blocked = transmittance * (1.0 - through);
   depthSum += blocked * (t0 + (float(i) + jitter) * dt);
   depthWeight += blocked;
   sunSum += blocked * sunlight;
   skySum += blocked * dot(sky, vec3(0.2126, 0.7152, 0.0722));
   fireSum += blocked * fireShare;

   transmittance *= through;
   hidden += blocked * seenThrough;

   if (transmittance < 0.03) break;
   ```

   Source: [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L2280-L2297)

   The loop stops at 3 percent transmittance. A blocked-weighted mean depth is kept for reprojection and aerial perspective.

6. **Write a layer, not the scene.** Clouds are dispatched far to near, and each composites over the earlier (farther) ones inside an offscreen layer (colour with transmittance in alpha, and distance), so the dither is resolved before it meets the scene:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryCloud.comp L2529-L2548
   float cloudAt = depthWeight > 0.0 ? depthSum / depthWeight : 1.0e30;
   float ringAt = ringDepthWeight > 0.0 ? ringDepthSum / ringDepthWeight : 1.0e30;

   vec4 under = layer;
   if (ringAt < cloudAt)
   {
       under = vec4(under.rgb * lets + body * strength, under.a * lets);
       under = vec4(under.rgb * ringLets + ringBody * strength, under.a * ringLets);
   }
   else
   {
       under = vec4(under.rgb * ringLets + ringBody * strength, under.a * ringLets);
       under = vec4(under.rgb * lets + body * strength, under.a * lets);
   }

   // The glowing air is round and behind the young cloud as much as in front of it, so it shows
   // where the cloud lets the background through: an aura round the fire rather than a wash over
   // it. Added over the fire, violet on orange came out pink.
   float aura = mix(1.0, sceneShows, 0.85);
   StoreLayerColor(vec4(under.rgb + violet * aura * strength, under.a));
   ```

   Source: [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L2529-L2548)

   The images are created storage-only per viewport, plus two history images that swap each frame:

   ```csharp
   // src/KSArmory/Ksa/CloudPass.cs L286-L296
   LayerColour = RenderImage.CreateColorStorage(renderer, "KSArmory Cloud Layer", extent,
                                                VkFormat.R16G16B16A16SFloat),
   LayerDistance = RenderImage.CreateColorStorage(renderer, "KSArmory Cloud Layer Distance",
                                                  extent, VkFormat.R32SFloat),
   History =
   [
       RenderImage.CreateColorStorage(renderer, "KSArmory Cloud History A", extent,
                                      VkFormat.R16G16B16A16SFloat),
       RenderImage.CreateColorStorage(renderer, "KSArmory Cloud History B", extent,
                                      VkFormat.R16G16B16A16SFloat),
   ],
   ```

   Source: [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L286-L296)

7. **Resolve with reprojected, clamped history.** One full-screen dispatch after the last cloud reads the layer, builds the 3x3 neighbourhood range of the current frame, reprojects last frame's history through last frame's view-projection shifted by how far the nearest cloud moved against the camera, clamps it into that range and blends. The history is storage-only, so bilinear filtering is done by hand with four `imageLoad`s (sampling an image created without sampled usage returned garbage in flight). Finally it composites `scene.rgb * resolved.a + resolved.rgb`:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryCloudResolve.comp L90-L117
   float own = imageLoad(layerDistance, at).r;
   float distance = own > 0.0 ? own : (nearest > 0.0 ? nearest : NoCloudMetres);

   vec2 uv = (vec2(at) + 0.5) / vec2(size);
   vec4 resolved = current;

   // A perspective matrix has zeros in places, but never a whole zero column.
   if (pc.Reproject[0] != vec4(0.0) || pc.Reproject[1] != vec4(0.0))
   {
       vec3 world = normalize(WorldAt(uv, 1.0)) * distance;
       vec4 before = pc.Reproject * vec4(world, 1.0);

       if (before.w > 1.0e-6)
       {
           vec2 then = (before.xy / before.w) * 0.5 + 0.5;

           if (all(greaterThanEqual(then, vec2(0.0))) && all(lessThanEqual(then, vec2(1.0))))
           {
               vec4 history = clamp(HistoryAt(then, size), lo, hi);
               resolved = mix(history, current, NewShare);
           }
       }
   }

   imageStore(historyOut, at, resolved);

   vec4 scene = imageLoad(sceneColor, at);
   imageStore(sceneColor, at, vec4(scene.rgb * resolved.a + resolved.rgb, scene.a));
   ```

   Source: [src/KSArmory/Shaders/KSArmoryCloudResolve.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloudResolve.comp#L90-L117)

   The C# side keeps the history valid only if the previous frame was resolved in the same view around the same reference cloud:

   ```csharp
   // src/KSArmory/Ksa/CloudPass.cs L828-L860
       int from = view.Parity;
       int to = 1 - from;

       if (view.Resolve[from] is null && !BuildResolve(view, from)) return;

       bool continues = view.ResolvedAt == view.Frames - 1 && view.LastSerial == reference
                        && reference != 0;

       Hazard(commandBuffer);

       Span<VkImageMemoryBarrier2> two = stackalloc VkImageMemoryBarrier2[2];
       BarrierBatch history = new(two);
       history.Add(view.History[from], ImageBarrierInfo.Presets.StorageReadWriteC);
       history.Add(view.History[to], ImageBarrierInfo.Presets.StorageReadWriteC);
       history.SubmitAndFlush(commandBuffer);

       ResolvePush push = new()
       {
           InvViewProj = camera.VPInv.viewProjection,
           Reproject = continues
                           ? Reprojection(view.LastViewProjection, view.LastCentre - referenceCentre)
                           : default,
       };

       view.Resolve[from]!.BindPipeline(commandBuffer, viewport.ShaderSlot, default, default, push);
       commandBuffer.Dispatch((view.Width + Group - 1) / Group, (view.Height + Group - 1) / Group, 1);

       view.Parity = to;
       view.ResolvedAt = view.Frames;
       view.LastViewProjection = camera.MVP.viewProjection;
       view.LastSerial = reference;
       view.LastCentre = referenceCentre;
   }
   ```

   Source: [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L828-L860)

   The reprojection matrix is last frame's row-vector view-projection with the shift folded into its fourth row; see [Pack parameters and camera data for a compute effect](../pack-parameters-for-a-compute-effect/overview.md) approach 4 for `Reprojection`.

8. **Name the regions and read them back.** Put a parent tag around the whole pass and a nested tag around each stage, so one frame yields a total and a split:

   ```csharp
   // src/KSArmory/Ksa/CloudPass.cs L62-L71
   private static readonly ProfilerTag GpuTag = new("KSArmory Cloud"u8);

   // Each stage inside that region, so what the pass costs can be split by what costs it. Nested
   // rather than beside it: the pass's own total is the outer regions, and a stage tagged with the
   // outer tag is counted twice.
   private static readonly ProfilerTag MarksTag = new("KSArmory Cloud: marks"u8);
   private static readonly ProfilerTag MarchTag = new("KSArmory Cloud: march"u8);
   private static readonly ProfilerTag ResolveTag = new("KSArmory Cloud: resolve"u8);
   private static readonly ProfilerTag FrontsTag = new("KSArmory Cloud: fronts"u8);
   private static readonly ProfilerTag FireLightTag = new("KSArmory Cloud: fire light"u8);
   ```

   Source: [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L62-L71)

   Wrap a dispatch with `using (commandBuffer.TagRegion(MarchTag)) { ... }`. A later frame reads the results from `Profiler.Gpu`: set `IsActive` and clear `Paused`, take `LastCompleteFrameIndex`, walk `Frames[index].AsSpan()` and total samples by name, counting only root samples for the frame total:

   ```csharp
   // src/KSArmory/Ksa/CloudPassCost.cs L44-L57
   public static void Begin()
   {
       try
       {
           Profiler.Gpu.IsActive = true;
           Profiler.Gpu.Paused = false;
       }
       catch (Exception e)
       {
           Warn($"could not start the GPU profiler: {e.Message}");
       }

       Reset();
   }
   ```

   Source: [src/KSArmory/Ksa/CloudPassCost.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPassCost.cs#L44-L57)

   ```csharp
   // src/KSArmory/Ksa/CloudPassCost.cs L81-L112
   if (!Profiler.Gpu.IsActive) return;

   int index = Profiler.Gpu.LastCompleteFrameIndex;
   if (index < 0 || index == _lastFrameIndex) return;

   ProfilerFrame[] frames = Profiler.Gpu.Frames;
   if (index >= frames.Length) return;

   ProfilerFrame frame = frames[index];
   if (!frame.IsValid) return;

   _lastFrameIndex = index;

   double pass = 0.0;
   double whole = 0.0;
   _frameStages.Clear();

   foreach (Sample sample in frame.AsSpan())
   {
       double ms = ProfilerWindowBase.TicksToMs(sample.Ticks);

       // Root samples only for the frame's own total: a nested region is already inside
       // its parent, so summing everything counts the same microsecond several times.
       if (sample.ParentIndex == ushort.MaxValue) whole += ms;

       string name = sample.Id.ToString();
       if (name == TagName) pass += ms;
       else if (name.StartsWith(StagePrefix, StringComparison.Ordinal))
       {
           _frameStages[name] = _frameStages.GetValueOrDefault(name) + ms;
       }
   }
   ```

   Source: [src/KSArmory/Ksa/CloudPassCost.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPassCost.cs#L81-L112)

9. **Pin the camera for a measurement.** The pass costs what it marches, and that depends on where the camera is. `CloudWatch.Update` writes `FixedController.CameraOffset` and `CameraRotation` through the main viewport to stand across the wind from the newest burst at a fixed number of cloud radii and elevation, aimed at 45 percent of the top, so two runs are comparable. The author's note puts a standing cloud at about three and a half milliseconds from that pose.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp) | `Density` (L848-L1016), the march (L2004-L2555) |
| [src/KSArmory/Shaders/KSArmoryCloudResolve.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloudResolve.comp) | Temporal resolve and composite |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs) | Sort, layer barriers, far-to-near loop, push, dispatch, `Resolve`, `Reprojection` |
| [src/KSArmory/Ksa/CloudPassCost.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPassCost.cs) | GPU cost from `Profiler.Gpu` |
| [src/KSArmory/Ksa/CloudWatch.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudWatch.cs) | Pinned measuring camera |

## Variations and gotchas

- **Cost is per cloud and per pixel.** Each cloud is a full-screen dispatch (most pixels leave at the bounding sphere), capped at eight.
- **`MarchScale`.** A specialization constant above 1 makes one invocation march one pixel of an N x N block, rotating which pixel every frame, and write the block; the default is 1.
- **History is dropped** whenever the reference serial changes or a frame is skipped in that view, so the first frame after a change shows the raw dither.
- **One reprojection reference.** History is reprojected by the nearest cloud's motion only.
- **The cloud never drifts.** By design it stays rooted over its burst and shears instead.
- **Weather applies to the main view only.** In a camera window the cloud draws with the "no weather" flag.
- **Compile-check the shader offline.** The file is more than two thousand lines; use [Compile-check a mod's GLSL without launching the game](../../glsl-workflow/check-mod-shaders-offline-with-glslang/overview.md), and note that a GLSL fault arrives at game load, not in the C# build.
- **Pipelines are dropped, not destroyed.** Replaced images wait 16 recorded frames in a graveyard before being disposed so no frame in flight reads a freed image.

## See also

- Siblings: [lighting and weather occlusion](raymarch-a-local-volume-into-the-hdr-scene-2.md), [analytic sky dispatches](raymarch-a-local-volume-into-the-hdr-scene-3.md), [overview](overview.md)
- [A raymarched mushroom cloud, held body-fixed and drawn before bloom](../../../../mods/ksarmory/features/raymarched-mushroom-cloud.md), [A mod compute pass recorded into KSA's frame just before bloom](../../../../mods/ksarmory/features/compute-pass-before-bloom.md), [Fireball light through LightDebug, filled in where the light pre-pass dropped it](../../../../mods/ksarmory/features/fireball-light-injection.md)
- [Pack parameters and camera data for a compute effect](../pack-parameters-for-a-compute-effect/overview.md), [Refract and distort the rendered scene, and flash or shake it](../../screen-effects/refract-and-distort-the-scene/overview.md), [Paint into or rescale the HDR scene with a compute pass shipped as a Shader asset](../../../rendering/vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/overview.md)
