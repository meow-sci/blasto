---
type: Mod Feature
title: A raymarched mushroom cloud, held body-fixed and drawn before bloom
description: Each nuclear cloud is held in its planet's rotating frame and aged on simulated time, its Glasstone-sized shape packed into a 128-byte push constant, then raymarched per pixel into a layer that a temporal resolve blends into the scene before bloom
mod: ksarmory
category: shaders
techniques:
  - harmony-prefix
  - glsl-shader
  - vulkan-pipeline
  - post-processing
  - reflection
  - raymarching
  - temporal-resolve
  - push-constant-packing
  - body-fixed-anchor
  - gpu-profiling
  - shader-constant-tests
hooks:
  - starmap StarMapAfterGui
  - harmony-prefix KSA.Program.OnFrameCelestials
  - starmap StarMapAfterOnFrame
  - harmony-prefix KSA.SunbloomRenderer.Render
  - reflection KSA.Program._planetTransparenciesRenderer
  - reflection KSA.Atmosphere.Rendering.CloudRenderer._upscaledCloudColorFlipTarget
  - reflection KSA.Atmosphere.Rendering.CloudRenderer.writeHistoryToFlip
  - reflection KSA.Atmosphere.Rendering.CloudShadowsRenderer.PlanetToCloudShadowData
  - reflection KSA.Atmosphere.Rendering.CloudShadowRenderData._staticShadowDataBuffer
lifecycle: [startup:asset-load, frame:draw-ui, frame:celestials, frame:after-on-frame, render:bloom]
game_types:
  - KSA.SunbloomRenderer
  - KSA.Program
  - KSA.Camera
  - KSA.ViewProjection
  - KSA.IViewport
  - KSA.Celestial
  - KSA.AtmosphereRenderer
  - KSA.PlanetTransparenciesRenderer
  - KSA.Atmosphere.Rendering.CloudRenderer
  - KSA.Atmosphere.Rendering.CloudShadowsRenderer
  - KSA.Atmosphere.Rendering.CloudShadowRenderData
  - KSA.GameSettings
  - KSA.Rendering.ComputePipelineWrapper
  - KSA.Rendering.RenderImage
  - KSA.GpuProfiler
  - KSA.FixedController
tags:
  - nuclear
  - mushroom-cloud
  - raymarching
  - volumetrics
  - compute-shader
  - temporal-reprojection
  - push-constants
  - atmosphere-luts
  - weather-clouds
  - gpu-profiling
---

KSArmory's nuclear burst leaves a mushroom cloud that rises, rolls over at the cap, leans and veers
downwind, drops fallout curtains, throws a ring of dust at its foot and stands for about eight
minutes before it fades, leaving burned ground and a fallout plume painted on the terrain. None of it is meshes or particles. The C# side keeps only a list of
bursts, each anchored in its planet's rotating frame and aged on simulated time, and a pure shape
function (`MushroomCloud.At`) that turns age and yield into a handful of dimensions sized from
Glasstone and Dolan.[^ksarmory-mushroomcloud] Every frame, inside the compute pass the mod records
before KSA's bloom, each cloud is one full-screen dispatch of `KSArmoryCloud.comp` that raymarches a
signed-distance torus, dome and column eroded with noise, lit by KSA's own sun, sky LUTs and weather
shadows, into an offscreen layer. A second shader blends that layer with last frame's and composites
it into the HDR scene.[^ksarmory-cloudpass][^ksarmory-cloud-comp][^ksarmory-resolve-comp]

The pass plumbing (the `SunbloomRenderer.Render` prefix, `ComputePipelineWrapper`, barriers,
per-viewport images, the fault fence) is in [Compute pass before bloom](./compute-pass-before-bloom.md),
and the generated include that gives the shader KSA's atmosphere code is in
[Core shader library include](./core-shader-library-include.md). This page covers what the cloud
itself does on top of them.

## How it works

### 1. A cloud is a record in the body's frame

`NuclearClouds.Begin(burstEcl, near, chargeKg, known, burstHeight)` is the single entry point for a
burst. It returns at once below `MushroomCloud.ThresholdKg` (1000 kg TNT), finds the body, and
converts the burst to body-fixed coordinates once:
`(burstEcl - body.GetPositionEcl()).Transform(body.GetCce2Ccf())`. Every later position is an
offset in that frame, so the cloud stays over the ground it burned while the planet spins and
orbits.[^ksarmory-nuclearclouds][^ksa-celestial]

What `Begin` decides before a cloud is stored:

| Question | How | Result |
|---|---|---|
| Is there air? | `KsaWorld.HasAtmosphere` reads `AtmosphereReference.Physical.Height > 0`[^ksarmory-ksaworld] | No air: dust ejecta through particles instead, no cloud (see [High-altitude burst sky](./high-altitude-burst-sky.md)) |
| Land, sea surface or under water? | `KsaWorld.SettingOf` against terrain height and sea level, using the peak fireball radius | Under water: no column, logged once. Sea surface: spray albedo (`Water`) |
| How high over the ground? | the fire-control height if given, else `KsaWorld.BurstHeightOf` | Every height in the shape is measured from the ground under the burst |
| How thin is the air? | `KsaWorld.AirDensityRatioAt` against sea level | Under 1% (`MushroomCloud.ThinAirRatio`, about 30 km on Earth) the cloud is kept but drawn as a debris shell, never marched |
| Is it the same burst as one still burning? | `MushroomCloud.IsTheSameBurst` (inside the fireball, within `SameBurstSeconds` = 0.5 s) | Charge added to the existing cloud, glow and fireball instead of a second set: six MIRV warheads become one burst |
| Which way is downwind? | `DownwindAt(groundCcf)`, a bearing hashed from the body-fixed position | Fixed for the cloud's life; the same vector leans the column and lays the fallout plume |

The stored `Cloud` holds body, burst and up in CCF, height, air ratio, the burst's air and speed of
sound (`BlastFront.For(...)`), a dryness for condensation, downwind, water flag, age and a serial
number that survives index shifts.[^ksarmory-nuclearclouds][^ksarmory-blastfront]

### 2. The shape is a pure function of age

`MushroomCloud.At(chargeKg, age, burstHeight, airRatio, front)` returns a `Shape` record: cap centre
height, cap radius, cap tube radius, stem top and radius, surge, roll, fade, the blast front's
radius along the ground, ground coupling, stem share and column top.[^ksarmory-mushroomcloud]

| Law or choice | Value |
|---|---|
| Cloud top, cap radius, fireball radius | `3000 x W^(1/3)`, `600 x W^0.37`, `67 x W^0.4` m (W in kt) |
| Drawn cap width | x1.9 (`DrawnCapWidening`), easing to x0.6 at Tsar Bomba's 50 Mt, because the law is narrower than the photographs |
| Tropopause | 11 km; above it the rise bends onto 41% (Castle Bravo) to 65% (Tsar) of the law and the lost height goes into width (the anvil) |
| Rise | 57 s (`RiseSeconds`), about five times faster than real, following the theodolite track of Teapot Wasp's cloud top plus one 4% overshoot |
| Stand and fade | 480 s stand (cap spreads 60%, stem loses 65% of its width, upper cloud shears 1.2x downwind), the last 90 s fading as a square |
| Held inside the front | While the burst's own blast front is inside the drawn cloud's reach, the whole cloud is scaled about the burst to fit it (bisection in `InsideFront`) |
| Air burst | The cap starts at the burst height; the dust column waits for the front to reach the ground, then climbs over `ColumnClimbSeconds` |

The bounding sphere the shader marches inside starts from `MushroomCloud.RisenBound`: the larger of
the drawn cloud top and the shape's reach at the overshoot's peak plus a 15% margin. The shader grows
it with the stand's spread and shear exactly as `MushroomCloud.GrownBound` does in C#.[^ksarmory-mushroomcloud][^ksarmory-cloud-comp]

### 3. Packing a cloud into 128 bytes

`CloudPass` sends each cloud as one `Push` struct: a `float4x4` and four `float4`, Vulkan's
guaranteed minimum push-constant size, so every float is spoken for.[^ksarmory-cloudpass]

| Field | x | y | z | w |
|---|---|---|---|---|
| `InvViewProj` | camera's `VPInv.viewProjection` (clip to camera-relative world) | | | |
| `AgeStrengthWind` | age (s) | `tint x Shape.Fade` (0 draws nothing) | downwind, octahedral | (packed with z) |
| `CentreRadius` | ground under the burst minus camera position, differenced in `double` then narrowed | | | risen bound (> 0 marks a cloud dispatch) |
| `FireSun` | fireball radius | fireball glow | `PackHeat`: core heat in the fraction, coupling and stem share at 64 levels each above it | `CloudFlags.Pack`, negated |
| `Shape` | cap centre height | cap radius | cap tube | `PackStem`: stem radius in whole metres, column-top share in the fraction |

`CloudFlags.Pack` sums 1 (spray), 2 (no weather images bound), 4 (first cloud this frame),
8 x dryness in sevenths and 64 x the front's radius in whole metres (up to 262,143 m so the float
stays an exact integer), then negates it, because a positive `FireSun.w` is what tells the shader a
dispatch is a ground mark.[^ksarmory-cloudflags] The cloud's up vector and the sun direction are not
sent: the shader derives up from the burst and `global.lighting.planetPosition`, and the sun from
`global.lighting.sunPosition`, both in KSA's global set 0.[^ksarmory-cloud-comp][^ksa-global-glsl]
One shader serves every burst dispatch (cloud, ground mark, flash and four sky layers), told apart
by the signs of `CentreRadius.w`, `FireSun.w` and `AgeStrengthWind.y`.

### 4. The march (`KSArmoryCloud.comp`, 8x8 workgroups)

1. The first cloud of the frame clears the layer (`rgb = 0`, `a = 1`) everywhere.
2. The pixel's ray comes from `InvViewProj`; the scene distance from the depth buffer (reversed Z,
   so `0` is the far plane). KSA's weather clouds write no depth, so the shader also samples the
   weather renderer's colour and distance images and places up to eight "sheets" along the ray;
   `WeatherInFront(t)` then dims any sample behind them.[^ksarmory-ksaworld]
3. Ray against the bounding sphere; most pixels leave here. Inside, a uniform march of 6 to 48
   steps (proportional to the chord, so spacing is constant), with a per-pixel start jitter advanced
   each frame by the golden ratio on `global.camera.frame`.
4. `Density`: undo the wind lean (`0.095 x top x t^1.4`, veering 0.55 rad with height, plus the
   stand's downwind shear), then a torus plus dome for the cap and a flared column for the stem, each
   eroded by a 4-octave fBm and one fine octave scaled per feature. The poloidal angle is advected by
   `RollAngle(age)` so the cap rolls over like a vortex ring. An exact early-out skips the noise for
   samples further outside than the erosion can reach. Fallout curtains hang from the cap after
   27 s, condensation collars and a crown veil appear early, and an inflow trumpet feeds the cap.
5. Light per sample: four sun-ward taps plus a closed-form chord through the cap dome for the stem
   in its shadow, a two-lobe stand-in for multiple scattering, KSA's `GetAmbient` sky from the
   ambient LUT, KSA's weather shadow (its own lowest-quality path over the weather shadow buffers),
   earthshine, the fireball's light from inside the cap, incandescent pockets while the core is
   hot, and the white-hot ball itself while `FireSun.y` is high.[^ksa-atmosphereluts-glsl]
6. Front-to-back accumulation stops at 3% transmittance; a blocked-weighted mean depth is kept for
   reprojection and for aerial perspective.
7. A separate 32-step march (`DustSteps`) draws the ring of dust (base surge) out to the blast
   front's radius along the ground, at most 5.2 stem radii, for the first 60 s of a burst not over
   water; a violet glow decaying with a 0.7 s time constant surrounds a young fireball.
8. Aerial perspective: `GetAerialPerspectiveForObjectInAtmosphere` at the weighted depth, so a far
   cloud hazes like terrain at that range.
9. Write into the layer, compositing over earlier (farther) clouds: `layer.rgb * lets + light`,
   `layer.a * lets`; and the layer distance.

With the `MarchScale` specialization constant above 1 (default 1), one invocation marches one pixel
of an N x N block, rotating which pixel every frame, and writes the block.[^ksarmory-shadertunables]

### 5. The resolve (`KSArmoryCloudResolve.comp`)

One full-screen dispatch after the last cloud. It reads the current layer, clamps last frame's
history to the 3x3 neighbourhood's range, and reprojects it through last frame's view-projection
shifted by how far the nearest cloud (the "reference", by serial) moved against the camera, since the
burst rides the planet at orbital speed. History is used only when the previous frame was resolved
in the same view around the same reference; then `mix(history, current, 0.125)` (`NewShare`). The
result goes to the other history image and into the scene as
`scene.rgb * resolved.a + resolved.rgb`.[^ksarmory-resolve-comp][^ksarmory-cloudpass] The history
images are storage-only, so the bilinear tap is done by hand with four `imageLoad`s.

### 6. The ground it burned

A burst over land that reached the ground (`setting == BurstSetting.Land` and ground coupling over
1%, NuclearClouds.cs L891 and L933) also calls `Burn`, which stores a `Scorch` in a third list that
outlives the cloud: body, burst point in CCF, the same downwind vector, charge, coupling and height
over the sea. Its radius is `Warhead.LethalRadius(charge)` under air and `AirlessBurst.ScorchRadius`
in vacuum. A burst inside a standing mark adds its charge to it; at most 12 stand, oldest dropped,
and nothing else expires them.[^ksarmory-nuclearclouds]

Each mark is its own dispatch of the cloud shader, with `CentreRadius.w = 0` (no march) and
`FireSun.w` = the mark's radius, which is what selects the scorch branch. It is dispatched only over
its screen tile (`TileFor` with `ScorchFootprint.Reach` = 3.3 patch radii downwind, 1.0 for an
airless mark), the tile's origin riding in `FireSun.xy`.[^ksarmory-cloudpass][^ksarmory-scorchfootprint]
Per pixel, the shader reconstructs the scene point from depth, measures it against the burst's up and
downwind, and darkens it towards ash: a ragged disc out to the radius, plus a cigar-shaped fallout
plume downwind scaled by the coupling (none without air). It fades with height above the patch's
centre (so a craft flying through the radius is not stained), fades out over the last 2 m down to sea
level, and is dimmed by weather in front (KSArmoryCloud.comp L1901-L2002).[^ksarmory-cloud-comp]

### 7. Measuring it

`CloudWatch.Update(followed, pose)` pins the main view across the wind from the newest burst, at
`ChaseView.ObserverRadii` cloud radii out and a fixed elevation, aimed 45% up the top, by writing
`FixedController.CameraOffset`/`CameraRotation` through `KsaWorld.TryLookFromMainViewport`. It runs
from the developer bridge's camera pose and from the drop scenario's linger phase, so
`CloudPassCost` (which reads the `KSArmory Cloud` and `KSArmory Cloud: march`/`resolve` regions from
`Profiler.Gpu`) reports the same view in every run.[^ksarmory-cloudwatch][^ksarmory-dropscenario][^ksarmory-cloudpasscost][^ksa-fixedcontroller][^ksa-gpuprofiler]

## Lifecycle and timing

| Moment | Where (build 5554) | What happens |
|---|---|---|
| `startup:asset-load` | `<Shader Id="KSArmoryCloudCompute">` and `KSArmoryCloudResolveCompute`, compiled by `ModLibrary.LoadAll` | Shader modules exist; pipelines are built lazily on the first recorded frame |
| `frame:draw-ui` | `[StarMapAfterGui]` (postfix on `Program.OnDrawUiViewports`, Program.cs L2268) -> `StepOnce` -> `StepSimulation`: `e.Weapon.Update` (KSArmoryMod.cs L776) -> `WeaponSystem.Detonate` -> `NuclearClouds.Begin` | A burst creates its cloud[^ksarmory-mod][^ksarmory-weaponsystem] |
| `frame:draw-ui` | Same step, KSArmoryMod.cs L925-L936: `NuclearClouds.Update(_lastSimStep)` | Ages advance on simulated time (a pause freezes the cloud); spent clouds are removed; `Fireball.Draw` per burning ball |
| `frame:celestials` | Harmony prefix on `Program.OnFrameCelestials` (L2320) | The same step on frames with the HUD hidden, still before the render |
| `frame:after-on-frame` | `[StarMapAfterOnFrame]` | Fallback if neither ran; the step then lands after the render and shows next frame |
| `render:bloom` | Prefix on `SunbloomRenderer.Render`: main view at Program.cs L4915, camera windows at L4584 | `CloudPass.Record` calls `NuclearClouds.TryAt` for each cloud, so the ground point is converted from CCF with the body's state at record time; marches and resolves[^ksa-program] |
| next frame's step | `CloudPassCost.Sample()` (KSArmoryMod.cs L922) | Reads the newest complete GPU profiler frame |

Inside `Record` the cloud's order is fixed: fire light, ground marks, rings, holes, leak streams and
the sky layers are drawn first, then the clouds far to near (the first `MaxClouds` = 8 non-thin
clouds in the order they were made, sorted by distance to the camera), then the resolve, the shock-front refraction,
tracers and the flash.[^ksarmory-cloudpass] So the cloud covers ground marks under it, and the flash
covers the cloud.

Why these points:

- **Simulated, not player, time.** `_lastSimStep` is the step the engine actually applied
  (`KsaWorld.ConsumeSimStep`, zero when nothing advanced), so the rise follows time warp and stops
  on pause; player time would keep a cloud growing in a frozen world.[^ksarmory-mod]
- **In the step, not in drawing code.** The comment at KSArmoryMod.cs L924-L927 says a cloud advanced
  by the UI drawing would stop growing whenever the HUD is hidden; the step itself has the
  `OnFrameCelestials` prefix for those frames.
- **Positions at record time.** `TryAt` runs inside the render prefix and reads
  `Program.GetRenderCamera()`, which returns the camera of `RenderedViewport` (set per viewport in
  `RenderViewport`, L4470), so each camera window gets its own centre, its own `View` and its own
  history.[^ksa-program][^ksa-camera]
- **Before bloom.** KSA has finished its weather clouds (`_planetTransparenciesRenderer.Render`,
  L4875) and translucency by L4915, so the weather images the shader reads are this frame's, and the
  cloud blooms and tonemaps with the scene.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/NuclearClouds.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/NuclearClouds.cs#L848-L1032) | `Begin`: body-fixed anchor, setting, thin air, merge, cloud record |
| [src/KSArmory/Ksa/NuclearClouds.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/NuclearClouds.cs#L1035-L1111) | `Update`: ageing, expiry, fireball light |
| [src/KSArmory/Ksa/NuclearClouds.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/NuclearClouds.cs#L739-L789) | `TryAt`: what the pass reads per cloud |
| [src/KSArmory/Sim/MushroomCloud.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/MushroomCloud.cs#L1306-L1372) | `At`: the shape at an age, held inside the front |
| [src/KSArmory/Sim/MushroomCloud.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/MushroomCloud.cs#L254-L304) | `PackStem`, `PackHeat` |
| [src/KSArmory/Sim/CloudFlags.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/CloudFlags.cs) | The flags float |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L661-L776) | Sort, layer barriers, far-to-near loop, push, dispatch, resolve call |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L825-L876) | `Resolve`, `Reprojection` |
| [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L848-L1016) | `Density`: lean, SDF shape, erosion, fire mask, fallout, condensation |
| [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L2004-L2555) | The march, dust ring, aerial perspective, layer write |
| [src/KSArmory/Shaders/KSArmoryCloudResolve.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloudResolve.comp) | Temporal resolve and composite |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L1264-L1441) | Weather images and weather shadow buffers by reflection |
| [src/KSArmory/Ksa/NuclearClouds.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/NuclearClouds.cs#L385-L506) | `Scorch`, `TryScorch`, `Burn`: the burned-ground list |
| [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L1901-L2002) | Scorch disc and fallout plume from depth |
| [src/KSArmory/Sim/ScorchFootprint.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ScorchFootprint.cs) | A mark's dispatch box, restating the shader's plume constants |
| [src/KSArmory/Ksa/CloudWatch.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudWatch.cs) | Pinned measuring camera |
| [tests/KSArmory.Tests/MushroomCloudShaderTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/MushroomCloudShaderTests.cs) | Reads constants out of the `.comp` by regex and holds them to the C# |

## Snippets

```csharp
// src/KSArmory/Ksa/NuclearClouds.cs L758-L783
        Cloud cloud = _clouds[index];

        try
        {
            groundEcl = cloud.Body.GetPositionEcl()
                       + cloud.GroundCcf.Transform(cloud.Body.GetCce2Ccf().Inverse());
            up = cloud.Up.Transform(cloud.Body.GetCce2Ccf().Inverse());
            ageSeconds = cloud.Age;
            shape = cloud.Shape;

            // The ball, so the pass can light the cloud from inside it while it burns.
            flash = MushroomCloud.FlashAt(cloud.ChargeKg, cloud.Age, cloud.Height, cloud.AirRatio);
            heat = MushroomCloud.Incandescence(cloud.ChargeKg, cloud.Age);
            water = cloud.Water;

            // Chosen at the burst rather than per frame, so the column leans one way for its whole
            // life instead of wandering.
            downwind = cloud.Downwind.Transform(cloud.Body.GetCce2Ccf().Inverse());

            // The bound as the risen cloud has it, which is also what the lean is measured against;
            // the shader grows it for its march, as MushroomCloud.GrownBound does.
            double kt = MushroomCloud.KilotonsFor(cloud.ChargeKg);
            radiusMetres = MushroomCloud.RisenBound(kt, cloud.Height, cloud.AirRatio);

            return Vec.IsFinite(groundEcl) && Vec.IsFinite(up) && Vec.IsFinite(downwind)
                   && radiusMetres > 0.0;
```

```csharp
// src/KSArmory/Ksa/CloudPass.cs L692-L759
                    double3 centre = burstEcl - camera.PositionEcl;
                    if (!Vec.IsFinite(centre)) continue;

                    float2 windOct = OctahedralPack(Vec.Unit(downwind));

                    Push push = new()
                    {
                        InvViewProj = camera.VPInv.viewProjection,
                        // ...
                        AgeStrengthWind = new float4((float)age, tint * (float)shape.Fade,
                                                     windOct.X, windOct.Y),
                        CentreRadius = new float4((float)centre.X, (float)centre.Y, (float)centre.Z,
                                                  (float)radius),
                        // ...
                        FireSun = new float4((float)flash.Radius, (float)flash.Glow,
                                             MushroomCloud.PackHeat(heat, shape.Coupling, shape.StemShare),
                                             KSArmory.CloudFlags.Pack(water, weather, first: drawn == 0, shape.Shock,
                                                                      NuclearClouds.DrynessAt(_order[n].Index))),
                        // ...
                        Shape = new float4((float)shape.CapCentre, (float)shape.CapRadius,
                                           (float)shape.CapTube,
                                           MushroomCloud.PackStem(shape.StemRadius, shape.ColumnTop)),
                    };
                    // ...
                    if (drawn > 0 || marks > 0) Hazard(commandBuffer);
                    // ...
                    using (commandBuffer.TagRegion(MarchTag))
                    {
                        int scale = Math.Max((int)ShaderTunables.ValueOf(ShaderTunables.MarchScale, 1.0), 1);
                        int across = (width + scale - 1) / scale;
                        int down = (height + scale - 1) / scale;

                        BindCloud(commandBuffer, viewport, camera, push);
                        commandBuffer.Dispatch((across + Group - 1) / Group, (down + Group - 1) / Group, 1);
                    }
```

```glsl
// src/KSArmory/Shaders/KSArmoryCloud.comp L2041-L2297
    const int MostSteps = 48;
    const int LeastSteps = 6;
    int Steps = clamp(int(ceil(float(MostSteps) * (t1 - t0) / (2.0 * radius))), LeastSteps, MostSteps);
    float dt = (t1 - t0) / float(Steps);
    // ...
    float jitter = fract(Hash(vec3(vec2(at), 0.0)) + float(global.camera.frame % 256) * 0.6180340);
    // ...
    for (int i = 0; i < (throughCloud ? Steps : 0); i++)
    {
        vec3 world = dir * (t0 + (float(i) + jitter) * dt) - toCentre;   // relative to the burst

        float fireMask;
        float wet;
        float density = Density(world, up, radius, fireMask, wet);
        // ...
        float extinction = density * Extinction * dt * (1.0 + SootOpacity * soot);
        float seenThrough = WeatherInFront(t0 + (float(i) + jitter) * dt);
        // ...
        float through = exp(-extinction);

        scattered += transmittance * lit * (1.0 - through) * seenThrough;

        float blocked = transmittance * (1.0 - through);
        depthSum += blocked * (t0 + (float(i) + jitter) * dt);
        depthWeight += blocked;
        // ...
        transmittance *= through;
        hidden += blocked * seenThrough;

        if (transmittance < 0.03) break;
```

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

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `SunbloomRenderer.Render` call sites; `Program.GetRenderCamera`, `RenderedViewport`, `PlanetAtmosphereRenderer`, `GetCloudShadowsRenderer`, private `_planetTransparenciesRenderer` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4910-L4916) | Recording point (L4915, L4584); per-viewport camera (L641, L490, set at L4470); atmosphere LUT owner (L464); weather shadows (L567); weather renderer owner (L177, reflected)[^ksa-program] |
| `Camera.VPInv`, `MVP`, `PositionEcl`, `NearbyCelestial` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L63-L71) | Inverse view-projection for rays; last frame's view-projection for reprojection; camera-relative centre; the body whose weather and planet the shader uses[^ksa-camera] |
| `IViewport.ShaderSlot`, `OffscreenTarget` | [current/decomp/KSA/IViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IViewport.cs#L13-L31) | Per-viewport `View`, global set and target size[^ksa-iviewport] |
| `Celestial.GetPositionEcl`, `GetCce2Ccf`, `GetRotationAxisCce` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L538) | Body-fixed anchor and its conversion back each frame (L386, L538, L615)[^ksa-celestial] |
| `AtmosphereRenderer.GetAtmosphereLutsDescriptorSet(int)` | [current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L280) | Set 3: transmittance, aerial perspective and ambient LUTs for this viewport[^ksa-atmosphererenderer] |
| `PlanetTransparenciesRenderer.GetCloudRenderer` | [current/decomp/KSA/PlanetTransparenciesRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetTransparenciesRenderer.cs#L78) | The weather renderer[^ksa-planettransparencies] |
| `CloudRenderer` private `_upscaledCloudColorFlip/FlopTarget`, `_upscaledDistanceFlip/FlopTarget`, `writeHistoryToFlip`; public `GetLowResolutionCloudColorTarget`, `GetLowResolutionCloudDistanceTarget` | [current/decomp/KSA.Atmosphere.Rendering/CloudRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Atmosphere.Rendering/CloudRenderer.cs#L286-L345) | Weather colour and distance for occlusion; `PerformUpscaling` (L1221-L1257) flips the flag after each write[^ksa-cloudrenderer] |
| `CloudShadowsRenderer.PlanetToCloudShadowData`, `_noShadowData` (private) | [current/decomp/KSA.Atmosphere.Rendering/CloudShadowsRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Atmosphere.Rendering/CloudShadowsRenderer.cs#L25-L33) | Per-body weather shadow data, keyed by `Celestial.Hash`[^ksa-cloudshadowsrenderer] |
| `CloudShadowRenderData._staticShadowDataBuffer`, `_dynamicShadowDataBuffer` (private), `DynamicUboStride` | [current/decomp/KSA.Atmosphere.Rendering/CloudShadowRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Atmosphere.Rendering/CloudShadowRenderData.cs#L20-L30) | Bound as the cloud pipeline's uniform and dynamic uniform buffers (bindings 6 and 7)[^ksa-cloudshadowrenderdata] |
| `GameSettings.ShowClouds` | [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L3203) | Clouds off means the no-shadow stand-in[^ksa-gamesettings] |
| `global.camera.frame`, `global.lighting.*` | [current/Content/Core/Shaders/Common/Global.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/Global.glsl#L13-L71) | Jitter clock; sun, planet, radii, sun colour, occlusion colour, LUT layer[^ksa-global-glsl] |
| `GetAmbient`, `GetAerialPerspectiveForObjectInAtmosphere` | [current/Content/Core/Shaders/Atmosphere/AtmosphereLuts.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/AtmosphereLuts.glsl#L392-L471) | KSA's own sky light and haze on the cloud[^ksa-atmosphereluts-glsl] |
| `Profiler.Gpu` (`GpuProfiler.Frames`, `LastCompleteFrameIndex`, `IsActive`, `Paused`) | [current/decomp/KSA/GpuProfiler.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuProfiler.cs#L73-L83) | Pass cost[^ksa-gpuprofiler] |
| `FixedController.CameraOffset`, `CameraRotation` | [current/decomp/KSA/FixedController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FixedController.cs#L9-L44) | The pinned measuring camera[^ksa-fixedcontroller] |

## Pitfalls and version notes

- **The push block is full.** At 128 bytes, every addition displaces something: the target size is
  read with `imageSize`, up and sun direction are derived in the shader, and three values share one
  float. Integers in `CloudFlags` stay below 2^24 so the float is exact; coupling and stem share
  are quantised to 64 levels. `CloudFlagsTests` round-trips the packing.[^ksarmory-cloudflags-tests]
- **C# and GLSL restate the same constants.** The shader has no room to be told the stand's start
  and length, the shear, the bound margin or the dust front's speed, so it hard-codes them;
  `MushroomCloudShaderTests` regex-reads those `const float`s from the `.comp` and asserts they equal
  the C# values, and checks that the bound the shader grows equals `MushroomCloud.DrawnBound` at every
  age. Without that, a sheared cap is cut off at the sphere.[^ksarmory-shader-tests]
- **Weather occlusion is reflection.** `Program._planetTransparenciesRenderer` and four private
  `CloudRenderer` images plus `writeHistoryToFlip` are read by name; a rename falls back to the
  public low-resolution pair (whose jittered edges crawl) or, if the owner is gone, draws the cloud
  in front of KSA's clouds. Build 5554 also has public `GetUpscaledCloudColorFlipTarget` (and the
  other three) and `IsUpscaledHistoryInFlip` on `CloudRenderer`, which would make that reflection
  unnecessary.[^ksa-cloudrenderer]
- **Weather shadows are reflection too, and gate the whole pass.** If `TryWeatherShadowBuffers`
  fails, `Record` returns before anything is drawn on that path: cloud, marks, sky and flash, and
  also the rings, holes, leak streams and tracers that path would have painted.[^ksarmory-cloudpass]
- **Main view only for weather.** KSA renders weather clouds for the main viewport alone (camera
  windows call `RenderAtmosphereOnly`), so in a camera window the cloud draws with the "no weather"
  flag and ignores clouds in front of it.[^ksa-program]
- **One reprojection reference.** History is reprojected by the nearest cloud's motion; it is
  dropped whenever the reference serial changes or a frame is skipped in that view, so the first
  frame after a change shows the raw dither.
- **Cost.** Each cloud is a full-screen dispatch (most pixels leave at the bounding sphere), capped
  at eight; the author's note puts a standing cloud at "about three and a half milliseconds at the
  watching pose" (MushroomCloud.cs L395-L396).[^ksarmory-mushroomcloud]
- **Thin air and no air are other effects.** Below 1% of sea-level density the record exists but
  `IsThin` keeps it out of the march; airless bodies get particle ejecta; deep underwater bursts get
  nothing. All three are in [High-altitude burst sky](./high-altitude-burst-sky.md).
- **The cloud never drifts.** By design it stays rooted over its burst and shears instead; and
  `Config.NuclearClouds = false` calls `NuclearClouds.Clear()` every step, which also resets the
  flash, the pending bangs and the shake.[^ksarmory-nuclearclouds]
- **Doc drift.** `docs/NUCLEAR-EFFECT.md` L11 points to a section called "The raymarched cloud" that
  is not in the file, and L330-L331 still describe the retired route (trail-renderer pens and an
  emissive mesh fireball).[^ksarmory-nuclear-effect]

## See also

- [Compute pass before bloom](./compute-pass-before-bloom.md)
- [Core shader library include](./core-shader-library-include.md)
- [Fireball light injection](./fireball-light-injection.md)
- [High-altitude burst sky](./high-altitude-burst-sky.md)
- [Shock-front refraction](./shock-front-refraction.md)
- [Burst flash and view shake](./burst-flash-and-view-shake.md)
- [Burst sound at blast arrival](./burst-sound-at-blast-arrival.md)
- [Depth-painted ground rings](./depth-painted-ground-rings.md)
- [Borrow the main view camera](./borrow-main-view-camera.md)
- [Point gravity and atmosphere model](./point-gravity-and-atmosphere-model.md)
- [Pack a compute effect into 128 bytes: octahedral vectors and an exact flag sum](../../../howto/shaders/compute-effects/pack-parameters-for-a-compute-effect/pack-parameters-for-a-compute-effect-1.md) - how-to article demonstrating this feature
- [Raymarch a noise-eroded volume into the HDR scene: bounding sphere, jitter and a reprojected resolve](../../../howto/shaders/compute-effects/raymarch-a-local-volume-into-the-hdr-scene/raymarch-a-local-volume-into-the-hdr-scene-1.md) - how-to article demonstrating this feature
- [Light a mod volume with KSA's sky and hide it behind weather clouds](../../../howto/shaders/compute-effects/raymarch-a-local-volume-into-the-hdr-scene/raymarch-a-local-volume-into-the-hdr-scene-2.md) - how-to article demonstrating this feature
- [Check shaders offline: keep GLSL constants in step with C# using a regex text test](../../../howto/shaders/glsl-workflow/check-mod-shaders-offline-with-glslang/check-mod-shaders-offline-with-glslang-3.md) - how-to article demonstrating this feature
- [Read a mod pass's GPU cost from KSA's profiler while a pinned camera keeps the runs comparable](../../../howto/testing-and-tooling/in-game-testing/measure-and-budget-mod-performance/measure-and-budget-mod-performance-4.md) - how-to article demonstrating this feature

[^ksarmory-nuclearclouds]: src/KSArmory/Ksa/NuclearClouds.cs
[^ksarmory-mushroomcloud]: src/KSArmory/Sim/MushroomCloud.cs
[^ksarmory-blastfront]: src/KSArmory/Sim/BlastFront.cs
[^ksarmory-cloudflags]: src/KSArmory/Sim/CloudFlags.cs
[^ksarmory-cloudpass]: src/KSArmory/Ksa/CloudPass.cs
[^ksarmory-scorchfootprint]: src/KSArmory/Sim/ScorchFootprint.cs
[^ksarmory-cloud-comp]: src/KSArmory/Shaders/KSArmoryCloud.comp
[^ksarmory-resolve-comp]: src/KSArmory/Shaders/KSArmoryCloudResolve.comp
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-weaponsystem]: src/KSArmory/Ksa/WeaponSystem.cs
[^ksarmory-cloudwatch]: src/KSArmory/Ksa/CloudWatch.cs
[^ksarmory-cloudpasscost]: src/KSArmory/Ksa/CloudPassCost.cs
[^ksarmory-dropscenario]: src/KSArmory/Ksa/DropScenario.cs
[^ksarmory-shadertunables]: src/KSArmory/Sim/ShaderTunables.cs
[^ksarmory-shader-tests]: tests/KSArmory.Tests/MushroomCloudShaderTests.cs
[^ksarmory-cloudflags-tests]: tests/KSArmory.Tests/CloudFlagsTests.cs
[^ksarmory-nuclear-effect]: docs/NUCLEAR-EFFECT.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-iviewport]: KSA.IViewport (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-atmosphererenderer]: KSA.AtmosphereRenderer (decompiled, build 2026.10.10.5554)
[^ksa-planettransparencies]: KSA.PlanetTransparenciesRenderer (decompiled, build 2026.10.10.5554)
[^ksa-cloudrenderer]: KSA.Atmosphere.Rendering.CloudRenderer (decompiled, build 2026.10.10.5554)
[^ksa-cloudshadowsrenderer]: KSA.Atmosphere.Rendering.CloudShadowsRenderer (decompiled, build 2026.10.10.5554)
[^ksa-cloudshadowrenderdata]: KSA.Atmosphere.Rendering.CloudShadowRenderData (decompiled, build 2026.10.10.5554)
[^ksa-gamesettings]: KSA.GameSettings (decompiled, build 2026.10.10.5554)
[^ksa-gpuprofiler]: KSA.GpuProfiler (decompiled, build 2026.10.10.5554)
[^ksa-fixedcontroller]: KSA.FixedController (decompiled, build 2026.10.10.5554)
[^ksa-global-glsl]: Core/Shaders/Common/Global.glsl (game shader, build 2026.10.10.5554)
[^ksa-atmosphereluts-glsl]: Core/Shaders/Atmosphere/AtmosphereLuts.glsl (game shader, build 2026.10.10.5554)
