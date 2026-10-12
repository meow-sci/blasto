---
type: Mod Feature
title: The blast front drawn as a bend in the light, by copying the scene and writing it back at an offset
description: Each nuclear blast front is drawn as a refracting shell, not an object, by two compute dispatches before bloom that copy KSA's HDR scene image and write each pixel back from the copy, displaced where the view ray grazes the front's sphere
mod: ksarmory
category: shaders
techniques:
  - harmony-prefix
  - glsl-shader
  - vulkan-pipeline
  - post-processing
  - screen-space-refraction
  - scene-copy-then-displace
  - analytic-ray-sphere-graze
hooks:
  - starmap StarMapAfterGui
  - harmony-prefix KSA.Program.OnFrameCelestials
  - starmap StarMapAfterOnFrame
  - harmony-prefix KSA.SunbloomRenderer.Render
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame, render:bloom]
game_types:
  - KSA.SunbloomRenderer
  - KSA.Camera
  - KSA.ViewProjection
  - KSA.IViewport
  - KSA.Program
  - KSA.Celestial
  - KSA.Rendering.ComputePipelineWrapper
  - KSA.Rendering.RenderImage
  - KSA.Rendering.BarrierBatch
  - KSA.Rendering.ImageBarrierInfo
  - KSA.GpuRegionExtensions
  - RenderCore.ReverseDepthBufferUtils
tags:
  - shock-wave
  - refraction
  - screen-space-distortion
  - compute-shader
  - nuclear
  - blast-front
  - sedov-taylor
  - scene-copy
---

A nuclear blast front in KSArmory is not a mesh, a particle shell or a translucent sphere. It is
drawn the way high-speed film shows it: a ring racing out from the burst that **bends whatever is
behind it and hides nothing**. Each frame, for every front still strong enough to see, the mod copies
KSA's HDR scene image and then writes each pixel back from the copy at a small screen offset, but only
where the view ray grazes the front's spherical shell. The offset follows the derivative of a
Gaussian across the shell, which is how a density step refracts light.[^ksarmory-shock-comp][^ksarmory-cloudpass]

The two dispatches run inside the mod's compute pass before KSA's bloom (see
[compute pass before bloom](./compute-pass-before-bloom.md) for the hook, barriers and pipeline
plumbing). This page covers what the shock pass adds on top: where the front is, how strong it is,
and the copy-then-displace shader. The same pass also carries the blast's view shake
(`BlastShake.Offset`) in its first push; that half is documented in
[burst flash and view shake](./burst-flash-and-view-shake.md).[^ksarmory-blastshake]

## How it works

### 1. One front per burst, in the burst's own air

`NuclearClouds.Begin` stores a `Cloud` for every burst in air (not for airless bodies, and not for a
fireball that stays under the sea): the body, the burst point in the body's
rotating frame (`BurstCcf`), the local up, the height over the ground, the air at the burst
(`AmbientAir`) and the body's speed of sound. Its `Front` property builds a `BlastFront` from those on
demand.[^ksarmory-nuclearclouds]

| Step | Code | What it computes |
|---|---|---|
| Front | `BlastFront.For(chargeKg, burstAir, sound, burstHeight)` | Reflection `1 + MushroomCloud.GroundCoupling(kt, height)`: 2 for a surface burst, 1 in free air[^ksarmory-blastfront] |
| Radius at an age | `BlastFront.Radius(age)` -> `BlastAltitude.FrontRadius` | Charge scaled by blast efficiency for the burst's air pressure (Glasstone and Dolan table 3.68), density from the burst's air[^ksarmory-blastaltitude] |
| Law | `MushroomCloud.ShockRadiusIn` | Sedov-Taylor `R = 1.03 (reflection x E / rho)^0.2 t^0.4` until the front slows to Mach 1.2, then the speed of sound plus an eased excess so the speed has no step[^ksarmory-mushroomcloud] |
| Per frame | `NuclearClouds.TryFront(i, ...)` | Burst position in ecliptic coordinates (body position + `BurstCcf` rotated by `GetCce2Ccf().Inverse()`), up, `Front.Radius(cloud.Age)`, charge and height. Returns false for a thin-air cloud (`MushroomCloud.IsThin`, under 1% of sea-level density)[^ksarmory-nuclearclouds][^ksa-celestial] |

The same `BlastFront` also clamps the cloud's drawn size and places the ground dust ring:
`MushroomCloud.At` takes `f.Radius(age)` and hands the front along the ground to the cloud dispatch as
`Shape.Shock`, packed by `CloudFlags.Pack` into one float. That is the cloud march's business
([raymarched mushroom cloud](./raymarched-mushroom-cloud.md)); the point is that the ring of dust and
the refracting front cannot disagree about where the front is.[^ksarmory-mushroomcloud][^ksarmory-cloudflags]

### 2. CPU: which fronts to draw, and the push constants

`CloudPass.Shock` walks every standing cloud (up to `MaxClouds` = 8 fronts) and builds one
`ShockPush` per front (CloudPass.cs L909-L988):[^ksarmory-cloudpass]

| Quantity | Rule |
|---|---|
| Strength | `Clamp(Warhead.LethalRadius(chargeKg) / front, 0, 1)`; skipped at `<= ShockFaintest` (0.03), so a front fades as `1/R` and is gone by about 33 lethal radii. `LethalRadius` is `7.368 x cbrt(kg)`[^ksarmory-warhead] |
| Bend | `ShockBend` (4.5) `x strength`; the comment calls it fifteen times what air does (a real 20 kt front 3 km out deflects light by about 0.03 deg, one pixel) |
| Shell width | `max(front x 0.03, 3 m)` |
| Camera inside | Skipped when `|burst - camera| < front - 3 x width`: from inside the sphere no ray grazes the shell |
| Burst on screen | Projected through `camera.MVP.viewProjection` in double, row-vector; skipped if `w <= 0` |
| Ground cut | `UpMode.xyz = up x (1 + heightOverGround)`: the vector's length beyond one carries the burst height, saving a float |
| Mode | `UpMode.w`: 0 = copy, 1 = bend (set between the two dispatches) |

All positions are differenced against `camera.PositionEcl` in double before narrowing to float, and
`InvViewProj` is `camera.VPInv.viewProjection`, so the shader works camera-relative with the camera at
the origin.[^ksa-camera]

### 3. GPU: copy, then displace

Per front (CloudPass.cs L963-L987): a compute hazard barrier; the per-view `SceneCopy` image (an
`R16G16B16A16SFloat` `RenderImage.CreateColorStorage`, one per viewport `ShaderSlot`) to
`ImageBarrierInfo.Presets.StorageReadWriteC` through KSA's `BarrierBatch`; a full-screen copy dispatch;
another hazard; a full-screen bend dispatch.[^ksarmory-cloudpass][^ksa-renderimage][^ksa-imagebarrierinfo]
Fronts are drawn one after the other, each copying what the previous bend left, so where two fronts
cross their bends add.

The bend (`RingBend`, KSArmoryShock.comp L51-L91):[^ksarmory-shock-comp]

| Test or term | Why |
|---|---|
| Ray `dir = normalize(WorldAt(uv, 1.0))` | KSA's projection is reverse-Z (`CreatePerspectiveFieldOfViewReverseZ`), so device depth 1 is the near plane[^ksa-reversedepth] |
| `along = dot(centre, dir)`; `x = (|centre - dir x along| - radius) / width` | How far the ray's closest approach to the burst is from the shell, in shell widths |
| `|x| > 3` returns nothing | Only the band where a ray grazes the shell; matches `ShockBandWidths` on the CPU |
| Below `-(overGround + width)` along up returns nothing | The front is a sphere until it meets the ground, a dome after |
| `along > sceneT` returns nothing | Scene depth (device 0 = sky) in front of the closest approach hides the bend |
| `lens = -2x e^(-x^2)` | Derivative of a Gaussian shell density: outward on one side, inward on the other, nothing at the middle |
| Offset `= (uv - burstUv)/|...| x lens x bend x width / along` | Radial on screen; a far front is a fine line, a near one a heavy one |
| Write `CopyAt(from + bend)` | Manual bilinear `imageLoad` of the copy (storage-only image, no sampler); alpha kept |

## Lifecycle and timing

| Moment | Where (build 5554) | What happens |
|---|---|---|
| Burst | `NuclearClouds.Begin`, called by the weapon code on detonation, the panel's burst tool or a scenario | `Cloud` added with its air and height[^ksarmory-nuclearclouds] |
| `frame:draw-ui` (HUD shown) | `StepOnce` from `[StarMapAfterGui]` (postfix on `Program.OnDrawUiViewports`, Program.cs L2268) -> `StepSimulation` -> `NuclearClouds.Update(_lastSimStep)`, KSArmoryMod.cs L933-L935 | Cloud ages on the simulated clock (frozen while paused, faster under warp)[^ksarmory-mod][^ksa-program] |
| `frame:celestials` (HUD hidden) | Same step from the Harmony prefix on `Program.OnFrameCelestials` (L2320) | Still before the render |
| `frame:after-on-frame` | `[StarMapAfterOnFrame]` fallback, only if neither hook claimed the frame | The front drawn this frame is a step behind |
| `frame:viewports` | `Camera.OnFrame` (Camera.cs L487) rebuilds `MVP` and `VPInv` | The matrices the pass reads at record time[^ksa-camera] |
| `render:bloom` | `CloudPass.Record`, from the prefix on `SunbloomRenderer.Render` (main call Program.cs L4915), after the cloud resolve (CloudPass.cs L776) and before tracers and the whiteout (L781, L783) | Copy and bend per front[^ksa-sunbloom] |

Order matters three ways:

- **After the clouds.** The resolve has already composited the mushroom into the scene, so the front
  bends the cloud behind it as well as terrain and sky.
- **Before tracers and the whiteout.** Those are drawn after and are not bent (or shaken).
- **Before bloom and tonemap.** The displaced pixels are HDR and bloom like the rest of the frame.

`Program.GetRenderCamera()` returns the camera of the viewport being recorded (`_renderedViewport` is
set at Program.cs L4470 for each camera window and L4684 for the main view), so each window gets its
own fronts against its own camera and its own `SceneCopy`.[^ksa-program]

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L878-L988) | `ShockPush`, tuning constants, `Shock`: front list, culling, the copy and bend dispatches |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L1992-L2020) | `BuildShock`: pipeline over `[scene colour, scene copy]` storage plus depth |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L654-L783) | Where `Shock` sits in `Record`: only on the path with clouds to march |
| [src/KSArmory/Shaders/KSArmoryShock.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryShock.comp) | Copy mode, `RingBend`, manual bilinear read, shake |
| [src/KSArmory/Ksa/NuclearClouds.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/NuclearClouds.cs#L609-L657) | `TryFront`: position, up, radius, charge, height; thin air refused |
| [src/KSArmory/Sim/BlastFront.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BlastFront.cs) | One front per burst: radius at an age, arrival at a distance |
| [src/KSArmory/Sim/BlastAltitude.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BlastAltitude.cs#L135-L148) | `FrontRadius`: efficiency-scaled charge in the burst's air |
| [src/KSArmory/Sim/MushroomCloud.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/MushroomCloud.cs#L585-L658) | `ShockRadiusIn` and its bisected inverse |
| [src/KSArmory/KSArmoryParticles.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryParticles.xml#L565) | `<Shader Id="KSArmoryShockCompute" .../>` asset[^ksarmory-particles-xml] |

## Snippets

```csharp
// src/KSArmory/Ksa/CloudPass.cs L917-L952
        for (int i = 0; i < NuclearClouds.Count && _fronts.Count < MaxClouds; i++)
        {
            if (!NuclearClouds.TryFront(i, out double3 burstEcl, out double3 up, out double front,
                                        out double chargeKg, out double overGround)) continue;

            double strength = Math.Clamp(Warhead.LethalRadius(chargeKg) / front, 0.0, 1.0);
            if (strength <= ShockFaintest) continue;

            double3 c = burstEcl - camera.PositionEcl;
            double width = Math.Max(front * ShockShellShare, ShockShellFloorMetres);

            // A front that has swept past the camera bends nothing: from inside the sphere every ray
            // passes the burst nearer than the camera is, so none grazes the shell -- which is the
            // only place the shader bends, three widths either side of it.
            if (Vec.Len(c) < front - (ShockBandWidths * width)) continue;

            double w = (c.X * vp.M14) + (c.Y * vp.M24) + (c.Z * vp.M34) + vp.M44;
            if (!(w > 0.0)) continue;

            // Row-vector, as the camera's own projection is.
            double x = ((c.X * vp.M11) + (c.Y * vp.M21) + (c.Z * vp.M31) + vp.M41) / w;
            double y = ((c.X * vp.M12) + (c.Y * vp.M22) + (c.Z * vp.M32) + vp.M42) / w;
            if (!double.IsFinite(x) || !double.IsFinite(y)) continue;

            _fronts.Add(new ShockPush
            {
                InvViewProj = camera.VPInv.viewProjection,
                CentreRadius = new float4((float)c.X, (float)c.Y, (float)c.Z, (float)front),
                CentreUvStrength = new float4((float)((x * 0.5) + 0.5), (float)((y * 0.5) + 0.5),
                                              ShockBend * (float)strength,
                                              (float)width),
                // Unit up, lengthened by the burst's height over the ground: KSArmoryShock.comp.
                UpMode = new float4((float)(up.X * (1.0 + overGround)), (float)(up.Y * (1.0 + overGround)),
                                    (float)(up.Z * (1.0 + overGround)), 0f),
            });
        }
```

```csharp
// src/KSArmory/Ksa/CloudPass.cs L965-L987
        using (commandBuffer.TagRegion(FrontsTag))
        {
            for (int n = 0; n < _fronts.Count; n++)
            {
                ShockPush push = _fronts[n];
                push.Shake = shaking && n == 0 ? new float4((float)shakeX, (float)shakeY, (float)shakeRoll, 1f) : default;

                Hazard(commandBuffer);

                BarrierBatch copy = new(one);
                copy.Add(view.SceneCopy, ImageBarrierInfo.Presets.StorageReadWriteC);
                copy.SubmitAndFlush(commandBuffer);

                view.Shock!.BindPipeline(commandBuffer, viewport.ShaderSlot, default, default, push);
                commandBuffer.Dispatch((view.Width + Group - 1) / Group, (view.Height + Group - 1) / Group, 1);

                Hazard(commandBuffer);

                push.UpMode.W = 1f;
                view.Shock.BindPipeline(commandBuffer, viewport.ShaderSlot, default, default, push);
                commandBuffer.Dispatch((view.Width + Group - 1) / Group, (view.Height + Group - 1) / Group, 1);
            }
        }
```

```glsl
// src/KSArmory/Shaders/KSArmoryShock.comp L61-L90
    // Where this ray passes nearest the burst, and how near. A ray grazing the shell crosses the
    // most of it, which is where a front bends the light: the rim of the ring, not its face.
    float along = dot(centre, dir);
    if (along <= 0.0) return vec2(0.0);

    vec3 nearest = dir * along;
    float x = (length(centre - nearest) - radius) / max(width, 1.0);
    // Three widths is CloudPass.ShockBandWidths, which skips a front the camera is that far inside.
    if (abs(x) > 3.0) return vec2(0.0);

    // Only what is above the ground; the rest is underground. The up vector's length past one is how
    // far over the ground the burst was, so an air burst's front is a sphere until it reaches it.
    vec3 up = normalize(pc.UpMode.xyz);
    float overGround = max(length(pc.UpMode.xyz) - 1.0, 0.0);
    if (dot(nearest - centre, up) < -(overGround + width)) return vec2(0.0);

    // And only where nothing in the scene stands in front of it.
    float deviceZ = texture(sceneDepth, uv).r;
    float sceneT = deviceZ > 0.0 ? length(WorldAt(uv, deviceZ)) : 1.0e30;
    if (along > sceneT) return vec2(0.0);

    // A lens across the shell: outward on one side of it, inward on the other, nothing at its middle
    // -- the derivative of the shell's density, which is what refraction follows. Scaled by how wide
    // the shell looks from here, so a front far off is a fine line and one close by a heavy one.
    float lens = -2.0 * x * exp(-x * x);
    vec2 outward = uv - pc.CentreUvStrength.xy;
    float spread = length(outward);
    if (spread < 1.0e-5) return vec2(0.0);

    return (outward / spread) * (lens * pc.CentreUvStrength.z * width / along);
```

```csharp
// src/KSArmory/Sim/MushroomCloud.cs L620-L636
    internal static double ShockRadiusIn(double yieldKt, double age, double densityKgPerM3,
                                         double soundMetresPerSecond, double reflection)
    {
        if (yieldKt <= 0.0 || age <= 0.0 || !(densityKgPerM3 > 0.0) || !(soundMetresPerSecond > 0.0)) return 0.0;

        double k = 1.03 * Math.Pow(reflection * yieldKt * JoulesPerKiloton / densityKgPerM3, 0.2);

        // Where the Sedov speed, 0.4 R/t, falls to the release Mach number.
        double released = Math.Pow(0.4 * k / (StrongShockMach * soundMetresPerSecond), 1.0 / 0.6);
        if (age <= released) return k * Math.Pow(age, 0.4);

        double since = age - released;
        double excess = (StrongShockMach - 1.0) * soundMetresPerSecond;

        return (k * Math.Pow(released, 0.4)) + (soundMetresPerSecond * since)
               + (excess * released * (1.0 - Math.Exp(-since / released)));
    }
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `SunbloomRenderer.Render(CommandBuffer, IViewport, int)` | [current/decomp/KSA/SunbloomRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SunbloomRenderer.cs#L326) | Prefix target that hands the pass the command buffer (shared with every effect in the pass) |
| `Program.GetRenderCamera`, `_renderedViewport`, `GetRenderer`, `PointClampedSampler` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L641) | Camera of the viewport being recorded (L641; set at L4470 and L4684); renderer (L557) and sampler (L468) for the pipeline |
| `Camera.MVP`, `VPInv`, `PositionEcl`, `OnFrame` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L63-L65) | Projecting the burst to screen (L63), unprojecting rays (L65), camera-relative centre (L110); matrices rebuilt in `OnFrame` (L487) |
| `ViewProjection.viewProjection` | [current/decomp/KSA/ViewProjection.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewProjection.cs) | The packed matrix pushed to the shader[^ksa-viewprojection] |
| `IViewport.ShaderSlot`, `OffscreenTarget` | [current/decomp/KSA/IViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IViewport.cs) | Per-view resources and the global set; the scene colour and depth[^ksa-iviewport] |
| `ReverseDepthBufferUtils.CreatePerspectiveFieldOfViewReverseZ` | [current/decomp/RenderCore/ReverseDepthBufferUtils.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore/ReverseDepthBufferUtils.cs#L8) | Why device depth 1 is near and 0 is sky in the shader |
| `ComputePipelineWrapper` ctor, `BindPipeline<T>` | [current/decomp/KSA.Rendering/ComputePipelineWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ComputePipelineWrapper.cs#L210) | Shock pipeline; push constants per dispatch, no external sets[^ksa-cpw] |
| `RenderImage.CreateColorStorage` | [current/decomp/KSA.Rendering/RenderImage.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderImage.cs#L118) | The scene copy |
| `BarrierBatch`, `ImageBarrierInfo.Presets.StorageReadWriteC` | [current/decomp/KSA.Rendering/ImageBarrierInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ImageBarrierInfo.cs#L51) | Copy image to `General`, compute read/write ([current/decomp/KSA.Rendering/BarrierBatch.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/BarrierBatch.cs))[^ksa-barrierbatch] |
| `GpuRegionExtensions.TagRegion` | [current/decomp/KSA/GpuRegionExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuRegionExtensions.cs#L10) | `KSArmory Cloud: fronts` GPU timing region[^ksa-gpuregion] |
| `Celestial.GetPositionEcl`, `GetCce2Ccf` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L386) | Burst point and up from the body's rotating frame each frame (L386, L538) |

## Pitfalls and version notes

- **No clouds to march, no fronts.** `Shock` is called only after the cloud loop, on the path where
  `_order` (non-thin clouds that resolved) is non-empty; with `_order.Count == 0` `Record` draws
  tracers and the flash and returns (CloudPass.cs L654-L659). A front whose own cloud is thin or
  missing is therefore never drawn, and `Config.ShaderPass` at 0 or a failed weather-shadow lookup
  disables it with the rest of the burst visuals.
- **A burst behind the camera draws no ring.** The CPU skips a front whose centre projects with
  `w <= 0`, because the shader's offset direction is radial from the burst's screen position, even
  if part of the shell is in front of the camera.
- **Deliberately exaggerated.** `ShockBend` is about fifteen times the physical deflection, and the
  author measured that the physical strength produced no visible difference between captures.[^ksarmory-nuclear-next]
- **Cost per front is two full-screen passes**, not a tile: the author measured 0.07 ms for one 20 kt
  front and 0.14-0.19 ms for three at 1440x900 (docs/NUCLEAR-NEXT.md L38, L101).
- **Thin air has no front.** `TryFront` refuses clouds under `MushroomCloud.ThinAirRatio` (1% of
  sea-level density); those bursts are drawn as a debris shell instead
  ([high-altitude burst sky](./high-altitude-burst-sky.md)).
- **Storage image, manual filtering.** The copy is created with storage usage only, so the shader
  bilinearly filters with four `imageLoad`s; a sampler on it would read garbage (shader comment L33-L34).
- **Doc drift.** docs/NUCLEAR-NEXT.md L38 says the dome runs on `MushroomCloud.ShockRadius` (the
  sea-level law); the code reads each cloud's `BlastFront` in the burst's air, a change recorded in
  docs/NUCLEAR-ALTITUDE.md (step 13).[^ksarmory-nuclear-altitude]
- **Strength uses the raw charge.** The radius uses the efficiency-scaled blast charge, but the fade
  is `Warhead.LethalRadius(cloud.ChargeKg) / front`, so a high burst's weaker front is drawn as
  strong as a low one's at the same radius.
- `BlastWave.SoundMetresPerSecond` (340) and `SurfaceReflection` (2) are the sea-level defaults the
  older `MushroomCloud.ShockRadius` still uses.[^ksarmory-blastwave]

## See also

- [Compute pass before bloom](./compute-pass-before-bloom.md)
- [Burst flash and view shake](./burst-flash-and-view-shake.md)
- [Raymarched mushroom cloud](./raymarched-mushroom-cloud.md)
- [High-altitude burst sky](./high-altitude-burst-sky.md)
- [Burst sound at blast arrival](./burst-sound-at-blast-arrival.md)
- [Blast damage and part health](./blast-damage-and-part-health.md)
- [Blast shove impulse](./blast-shove-impulse.md)
- [Core shader library include](./core-shader-library-include.md)
- [Give a compute pass the camera pose without losing precision, or the wrong camera](../../../howto/shaders/compute-effects/pack-parameters-for-a-compute-effect/pack-parameters-for-a-compute-effect-4.md) - how-to article demonstrating this feature
- [Refract the scene: a scene copy then a displaced write-back in two compute dispatches](../../../howto/shaders/screen-effects/refract-and-distort-the-scene/refract-and-distort-the-scene-2.md) - how-to article demonstrating this feature

[^ksarmory-shock-comp]: src/KSArmory/Shaders/KSArmoryShock.comp
[^ksarmory-cloudpass]: src/KSArmory/Ksa/CloudPass.cs
[^ksarmory-nuclearclouds]: src/KSArmory/Ksa/NuclearClouds.cs
[^ksarmory-blastfront]: src/KSArmory/Sim/BlastFront.cs
[^ksarmory-blastaltitude]: src/KSArmory/Sim/BlastAltitude.cs
[^ksarmory-blastwave]: src/KSArmory/Sim/BlastWave.cs
[^ksarmory-mushroomcloud]: src/KSArmory/Sim/MushroomCloud.cs
[^ksarmory-warhead]: src/KSArmory/Sim/Warhead.cs
[^ksarmory-cloudflags]: src/KSArmory/Sim/CloudFlags.cs
[^ksarmory-blastshake]: src/KSArmory/Ksa/BlastShake.cs
[^ksarmory-particles-xml]: src/KSArmory/KSArmoryParticles.xml
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-nuclear-next]: docs/NUCLEAR-NEXT.md
[^ksarmory-nuclear-altitude]: docs/NUCLEAR-ALTITUDE.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-sunbloom]: KSA.SunbloomRenderer (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-viewprojection]: KSA.ViewProjection (decompiled, build 2026.10.10.5554)
[^ksa-iviewport]: KSA.IViewport (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-cpw]: KSA.Rendering.ComputePipelineWrapper (decompiled, build 2026.10.10.5554)
[^ksa-renderimage]: KSA.Rendering.RenderImage (decompiled, build 2026.10.10.5554)
[^ksa-barrierbatch]: KSA.Rendering.BarrierBatch (decompiled, build 2026.10.10.5554)
[^ksa-imagebarrierinfo]: KSA.Rendering.ImageBarrierInfo (decompiled, build 2026.10.10.5554)
[^ksa-gpuregion]: KSA.GpuRegionExtensions (decompiled, build 2026.10.10.5554)
[^ksa-reversedepth]: RenderCore.ReverseDepthBufferUtils (decompiled, build 2026.10.10.5554)
