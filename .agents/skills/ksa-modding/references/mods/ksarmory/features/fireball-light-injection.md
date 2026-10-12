---
type: Mod Feature
title: Fireball light through LightDebug, filled in where the light pre-pass dropped it
description: A nuclear fireball's point light is pushed into KSA's light system through the public LightDebug list, and a compute dispatch before bloom adds its diffuse term on craft and structures the clustered pre-pass culled beyond about 3 km, found by reading the pre-pass irradiance image by reflection
mod: ksarmory
category: shaders
techniques:
  - harmony-prefix
  - reflection
  - glsl-shader
  - vulkan-pipeline
  - post-processing
  - lightdebug-light-injection
  - screen-space-relighting
hooks:
  - starmap StarMapAfterGui
  - harmony-prefix KSA.Program.OnFrameCelestials
  - harmony-prefix KSA.SunbloomRenderer.Render
  - reflection KSA.Rendering.Lighting.ClusteredLightSystem._diffuseIrradianceImage
lifecycle: [frame:draw-ui, frame:celestials, render:bloom]
game_types:
  - KSA.Rendering.Lighting.LightDebug
  - KSA.Rendering.Lighting.Light
  - KSA.Rendering.Lighting.ClusteredLightSystem
  - KSA.PrePassRenderer
  - KSA.PrePassData
  - KSA.GpuTextureSystem
  - KSA.AstronomicalTemplate
  - KSA.HapkeScatteringReference
  - KSA.Celestial
  - KSA.Rendering.ComputePipelineWrapper
  - KSA.Rendering.BarrierBatch
  - KSA.SunbloomRenderer
  - KSA.Program
tags:
  - lighting
  - point-light
  - clustered-lighting
  - light-prepass
  - compute-shader
  - nuclear
  - reflection
  - bindless-textures
---

A nuclear burst in KSArmory lights the land around it: the ground, the launch pad and the craft
standing on it. Two halves make that work. The CPU half pushes one point light per frame into KSA's
own light system through the public `LightDebug.Lights` list, which is the only list the engine
re-reads after clearing its lights. The GPU half exists because KSA's clustered light pre-pass
drops any light from craft and structures beyond about 3.16 km before multiplying in its intensity,
so a 50 kt fireball lit the grass and left the pad black. A compute dispatch recorded before bloom
reads what the pre-pass actually wrote (a private image, by reflection) and adds the forward loop's
diffuse term only on the pixels the pre-pass dropped.[^ksarmory-blocked][^ksa-lightprepass]

## How it works

**CPU: the light itself**[^ksarmory-fireball] ([src/KSArmory/Ksa/Fireball.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Fireball.cs)):

| Step | Detail |
|---|---|
| Who calls it | `NuclearClouds.Update` walks the standing clouds; for each whose `MushroomCloud.FlashAt(...)` is not spent it calls `Fireball.Draw(centreEcl, flash.Radius, flash.Colour, flash.Glow)`, the centre being the burst's ground point plus `Up x CapCentre` (the ball is the cap). No burning cloud, `Fireball.Clear()`.[^ksarmory-nuclearclouds] |
| Frame | `KsaWorld.TryEclToCameraEgo` subtracts `(GetRenderCamera() ?? GetMainCamera()).PositionEcl`: a `Light` position is camera-relative (Ego).[^ksarmory-ksaworld] |
| Size | Radiance `0.4535 x glow` (the linear albedo the ball's colour was authored at), intensity `radiance x 4 pi r^2` (how KSA sizes an exhaust light), range `60 x r` (3.2 km at 0.3 kt). |
| Created | `Light.CreatePointLight(centreEgo, range, colour, intensity, ELightFlags.None)`: no shadows, because terrain takes the light through the forward list, which has none.[^ksa-light] |
| Pushed | Appended to `LightDebug.Lights`; `_lights = 1`. `Clear()` removes the tail it added. One light at a time: `Illuminate` clears before adding, so with several balls burning the last one drawn (the oldest, since the loop runs backwards) wins. |
| Stand-down | If `LightDebug.Target` is non-null the engine's debug spawner owns the list: `MovePointLights` indexes `BasePositions[i]` and friends for every entry, which only the spawner fills, so a foreign entry is overwritten or throws. The mod then logs once and lights nothing.[^ksa-lightdebug] |

**Engine: where the light goes.** `ClusteredLightSystem.OnFrame` (L1001) calls
`LightDebug.AddToLightSystem` when the viewport's `LightMode` is `Clustered`, and
`CreateLightInstance` (L897) puts every light in two places: the per-viewport forward list (up to
256, read by `SampleForwardLights` in `PunctualLighting.glsl`, used for terrain) and, for the main
viewport only, the clustered collection the compute pre-pass shades craft, parts and static objects
from.[^ksa-clusteredlightsystem][^ksa-punctuallighting] The pre-pass loop's first test is
`subgroupAll(dotNL * rangeAtt < ATT_EPSILON)` with `ATT_EPSILON = 1e-7` and `rangeAtt = falloff / d^2`,
before intensity is applied (LightPrePass.comp L41, L321), so at `d > ~3162 m x sqrt(dotNL)` the light
is skipped for that whole subgroup however bright it is.[^ksa-lightprepass]

**GPU: the fill** (`CloudPass.FireLight`, [src/KSArmory/Shaders/KSArmoryFireLight.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryFireLight.comp)):[^ksarmory-cloudpass][^ksarmory-firelight-comp]

1. Gate: `Fireball.TryPushed` returns the exact light handed to KSA this frame (Ego position, range,
   `Color x Intensity`); viewport must be `Program.MainViewport` (the only one the clustered list
   is built for); `Program.Instance.PrePassRenderer.OpaqueHasValidDepth`; the pre-pass target must
   match the scene's extent; the irradiance image must be readable.
2. Reflection: `ClusteredLightSystem._diffuseIrradianceImage` (private `RenderImage`, created
   `B10G11R11UfloatPack32` at L622). Missing once means `_irradianceMissing` and a single warning;
   the fill is then off rather than guessed.
3. Pipeline (rebuilt when any of the three image views changes): storage `[scene colour]`, depth
   samplers `[scene depth, pre-pass depth]`, read-only views `[pre-pass normal, irradiance]`, plus
   KSA's bindless texture set as external set 2 (`TextureSystem.Layout` / `DescriptorSet`).
4. Barrier the irradiance image from KSA's fragment read (`SampledReadF`, ClusteredLightSystem.cs L1501)
   to `SampledReadC` through `BarrierBatch`, so KSA re-barriers it next frame from the tracked state.
5. Push (128 bytes): `InvViewProj`; `LightRange`; `Radiant.rgb` + planet mean albedo
   (`ScatteringReference.MeanDiffuseLuminosity ^ 2.2`, default 0.218); `Cce2Ccf` as a quaternion
   built from how `Transform` moves each axis; `GroundMap` = bindless handle of the body's
   `DiffuseReference` cube map and `TextureSystem.SamplerClampHandle`, or `-1` for none.
6. Dispatch `ceil(W/16) x ceil(H/8)` (the shader's `local_size`, also LightPrePass's 16x8), then a
   compute write-to-read hazard barrier.

**Per pixel in the shader:**

| Test or term | Why |
|---|---|
| Pre-pass depth `> 0` and within `1e-3` relative of scene depth | Only pre-pass meshes the eye actually sees, not terrain in front of them |
| `FireIrradiance(p, n)`: `Radiant x dotNL / d^2 x saturate(1 - (d^2/R^2)^2)` | The forward loop's own attenuation, so the fill matches how the terrain was lit |
| Skip if pre-pass irradiance luminance `>= 0.5 x` the fireball's | The pre-pass kept it; nothing is lit twice (its own saturates to infinity under the ball) |
| Albedo where sunlit: `was / (sunColor x occlusionColor x N.L / pi)`, clamped to 0.6 | `StaticObject.frag`'s direct term, so the albedo divides out of the pixel |
| Albedo where the sun does not reach | Ground-lying surfaces (`n` within ~25 deg of local up) read the planet's colour cube at LOD 3; others the mean albedo, 40% of the pixel's hue |
| Write | `scene + irradiance x albedo / pi`, alpha kept |

## Lifecycle and timing

| Moment | Where (build 5554) | What happens |
|---|---|---|
| `frame:draw-ui` (or `frame:celestials` with the HUD hidden) | `StepSimulation` tail, `NuclearClouds.Update(_lastSimStep)`, KSArmoryMod.cs L925-L936, run from `StepOnce` | `Fireball.Draw` rewrites the `LightDebug.Lights` entry for this frame's camera[^ksarmory-mod] |
| `frame:lights` | `LightSystem.ClearLights()` / `LightSystem.OnFrame(FrameViewport, dtPlayer)`, Program.cs L2317-L2318 | Engine clears, then copies `LightDebug.Lights` (a `struct Light`) into its lists[^ksa-program] |
| `render:prepass` | `PrePassRenderer.Render` L4713, `LightSystem.DispatchComputePasses` L4726 inside `RenderGame` | Pre-pass writes `_diffuseIrradianceImage`, culling the far fireball |
| `render:bloom` | Harmony prefix on `SunbloomRenderer.Render`, main call at Program.cs L4915 (colour already `StorageReadFc`, depth `DepthSampledReadVfc` from L4910-L4914), before `_overallBloomRenderer.Render` L4916 | `CloudPass.Record` -> `FireLight` writes the fill into scene colour |

Why these points:

- The light must be in `LightDebug.Lights` before L2317. The AfterGui postfix (L2268) is; a light
  created directly through `ILightSystem` would be wiped by `ClearLights` before anything renders.
  When the HUD is hidden the step runs in the `OnFrameCelestials` prefix (L2320), which is after
  L2318, so that frame's light is one frame stale.
- The fill must run after the pre-pass and lights and before bloom, so it reads the finished
  irradiance and blooms with the scene. Inside `Record` it is the first thing drawn, before the scorch
  marks, rings and clouds that composite over those surfaces (CloudPass.cs L472-L474).
- It only runs on the `tint > 0` path of `Record` (`Config.ShaderPass`), after
  `TryWeatherShadowBuffers` succeeds for the camera's `NearbyCelestial` (CloudPass.cs L404-L429).
  Turning the cloud pass off therefore also turns the far-light fill off; the near light (under
  3 km) still comes from KSA.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/Fireball.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Fireball.cs) | Light sizing, `LightDebug` push and clear, `TryPushed` for the GPU half |
| [src/KSArmory/Ksa/NuclearClouds.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/NuclearClouds.cs#L1057-L1110) | One `Fireball.Draw` per burning ball, at the cap centre |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L1786-L1844) | `FireLightPush`, gates, barrier, dispatch |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L1846-L1920) | Mean albedo, planet rotation quaternion, bindless ground map handles |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L1922-L1990) | Reflection into the irradiance image; pipeline build |
| [src/KSArmory/Shaders/KSArmoryFireLight.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryFireLight.comp) | Per-pixel kept/dropped test and the diffuse fill |
| [docs/BLOCKED-ON-KSA.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/BLOCKED-ON-KSA.md#L762-L798) | The author's account of the pre-pass cut and what would unblock it |

## Snippets

```csharp
// src/KSArmory/Ksa/Fireball.cs L84-L121
    // Through LightDebug's list rather than the light system directly, and that is the only route
    // that works: the system clears its lights after the mod's hook and re-pushes this list, so a
    // light created directly is wiped before anything renders.
    private static void Illuminate(double3 centreEgo, double radiusMetres, float3 colour, float glow)
    {
        Clear();

        // Stand down entirely if the engine's own light spawner is live. MovePointLights walks
        // this list against parallel lists only that spawner fills, so an entry of ours is
        // rewritten at best and throws out of the frame loop at worst -- and its target is never
        // cleared once set.
        if (LightDebug.Target is not null)
        {
            // ...
            return;
        }

        // The engine sizes an exhaust light this way: a uniformly emitting sphere puts out its own
        // surface radiance over its whole area, so intensity is that times the area.
        float radiance = AlbedoLinear * glow;
        float intensity = radiance * 4f * MathF.PI * (float)(radiusMetres * radiusMetres);

        // ...
        _pushed = Light.CreatePointLight(
            centreEgo, (float)(radiusMetres * RangeInRadii), colour, intensity, ELightFlags.None);
        LightDebug.Lights.Add(_pushed);

        _lights = 1;
    }
```

```glsl
// current/Content/Core/Shaders/Lighting/LightPrePass.comp L308-L322
        vec3 posToLight = lightData[lightIdx].position - worldPos;
        float distSq = dot(posToLight, posToLight);
        float invDist = inversesqrt(max(distSq, DIST_EPSILON));
        vec3 lightDir = posToLight * invDist;

        float dotNL = max(dot(N, lightDir), 0.0);

        float lightRange = lightData[lightIdx].range;
        float invRange = 1.0 / max(lightRange, RANGE_EPSILON);
        float x2 = distSq * invRange * invRange;
        float falloff = saturate(1.0 - x2 * x2);
        float rangeAtt = (invDist * invDist) * mix(1.0, falloff, step(RANGE_EPSILON, lightRange));

        if (subgroupAll(dotNL * rangeAtt < ATT_EPSILON))
            continue;
```

```csharp
// src/KSArmory/Ksa/CloudPass.cs L1928-L1952
    private static bool TryPrePassIrradiance(out RenderImage? image)
    {
        image = null;
        if (_irradianceMissing) return false;

        try
        {
            if (Program.LightSystem is not ClusteredLightSystem lights) return false;
            _irradianceField ??= typeof(ClusteredLightSystem).GetField(
                "_diffuseIrradianceImage", BindingFlags.NonPublic | BindingFlags.Instance);
            image = _irradianceField?.GetValue(lights) as RenderImage;
        }
        catch
        {
            image = null;
        }

        if (image is null)
        {
            _irradianceMissing = true;
            Warn("KSA's light pre-pass result could not be read; a fireball will not light craft beyond 3 km");
        }

        return image is not null;
    }
```

```glsl
// src/KSArmory/Shaders/KSArmoryFireLight.comp L90-L105
    float raw = texelFetch(prePassDepth, pixel, 0).r;
    if (raw <= 0.0) return;

    // Only where the object is what the eye sees, rather than terrain standing in front of it.
    float scene = texelFetch(sceneDepth, pixel, 0).r;
    if (abs(scene - raw) > 1e-3 * raw) return;

    vec2 uv = (vec2(pixel) + 0.5) / vec2(size);
    vec3 p = WorldAt(uv, raw);
    vec3 n = normalize(texelFetch(prePassNormal, pixel, 0).xyz);

    vec3 irradiance = FireIrradiance(p, n);
    if (Luminance(irradiance) <= 0.0) return;

    // KSA's own saturates to infinity under the ball, and infinity counts as kept.
    if (Luminance(texelFetch(prePassIrradiance, pixel, 0).rgb) >= KeptShare * Luminance(irradiance)) return;
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `LightDebug.Lights`, `Target`, `MovePointLights`, `AddToLightSystem` | [current/decomp/KSA.Rendering.Lighting/LightDebug.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/LightDebug.cs#L11-L25) | The re-pushed light list (L11), the spawner flag (L25); `MovePointLights` L251, `AddToLightSystem` L424 |
| `Light.CreatePointLight` | [current/decomp/KSA.Rendering.Lighting/Light.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/Light.cs#L38) | Builds the point light (a `struct`, copied by value into the engine's lists) |
| `ClusteredLightSystem.ClearLights`, `OnFrame`, `CreateLightInstance`, `DispatchComputePasses`, `_diffuseIrradianceImage` | [current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs#L897-L933) | Forward and clustered lists (L897), clear (L923), per-frame push (L1001), pre-pass dispatch (L1230), the private irradiance image (L251) read by reflection |
| `Program.LightSystem`, `PrePassRenderer`, `TextureSystem`, `MainViewport` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L117-L163) | Light system (L147), pre-pass renderer (L163), bindless textures (L117), main viewport (L484) |
| `PrePassRenderer.OpaqueHasValidDepth`, `OpaquePrePassData`; `PrePassData.Target` | [current/decomp/KSA/PrePassRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PrePassRenderer.cs#L216-L222) | Pre-pass depth and normal images ([current/decomp/KSA/PrePassData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PrePassData.cs#L12-L16) L12)[^ksa-prepassrenderer][^ksa-prepassdata] |
| `GpuTextureSystem.Layout`, `DescriptorSet`, `SamplerClampHandle` | [current/decomp/KSA/GpuTextureSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuTextureSystem.cs#L24-L40) | Bindless set 2 and the clamp sampler index[^ksa-gputexturesystem] |
| `AstronomicalTemplate.DiffuseReference`, `ScatteringReference`; `HapkeScatteringReference.MeanDiffuseLuminosity` | [current/decomp/KSA/AstronomicalTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AstronomicalTemplate.cs#L30-L36) | Ground colour cube map and mean albedo ([current/decomp/KSA/HapkeScatteringReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/HapkeScatteringReference.cs#L11) L11, default 0.5)[^ksa-astronomicaltemplate][^ksa-hapke] |
| `Celestial.GetCce2Ccf` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L538) | World to body-fixed, for the cube-map lookup[^ksa-celestial] |
| `ComputePipelineWrapper` (ctor, `BindPipeline<T>`) | [current/decomp/KSA.Rendering/ComputePipelineWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ComputePipelineWrapper.cs#L32) | Read-only views go in `ShaderReadOnlyOptimal` after the depth samplers; set 0 global, set 1 own, externals from 2[^ksa-computepipelinewrapper] |
| `global.lighting.sunColor`, `occlusionColor`, `planetPosition` | [current/Content/Core/Shaders/Common/Global.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/Global.glsl#L44) | Sun term the albedo is divided out of[^ksa-global-glsl] |

## Pitfalls and version notes

- **`LightDebug` is a debug class.** It is the only public list the engine re-reads after
  `ClearLights`, but the moment someone uses KSA's light spawner (`Target` set) the list belongs to
  it. The mod checks `Target` every frame and never touches the list while it is set.
- **One light, last writer wins.** `Illuminate` clears its previous entry first, so several bursts
  at once light only from one ball.
- **Main viewport only.** `CreateLightInstance` adds to the clustered collection only for
  `inViewport.IsMain()` (L908), and `OnFrame` pushes the debug list only for a `Clustered` viewport;
  camera windows get neither the clustered light nor the fill.
- **Reflection degrades to dark, never to a guess.** A renamed `_diffuseIrradianceImage` disables the
  fill for the session with one warning; the albedo heuristics depend on the pixel's existing
  sunlight and err dark in shadow.
- **The fill depends on the cloud pass.** It sits behind `tint > 0` and the weather-shadow buffer
  lookup in `CloudPass.Record`; with the shader pass at zero, craft beyond 3 km stay dark.
- **Doc drift:** `docs/NUCLEAR-EFFECT.md` L330-L331 still says `Ksa/Fireball.cs` draws "an emissive
  sphere through the generic mesh renderer plus a real point light"; the code is the light alone,
  the ball being the cloud pass's.[^ksarmory-nuclear-effect] The shader header says the engine code is
  in `docs/NUCLEAR-EFFECT.md`; it is actually in `docs/BLOCKED-ON-KSA.md` L762-L798. That section
  cites `ClusteredLightSystem.cs:899` for `CreateLightInstance`; in build 5554 the method starts at
  L897 and the cull is LightPrePass.comp L321, both matching its description.
- **What would remove the fill:** multiplying `intensity` into the pre-pass early-out (one float the
  loop loads three lines later anyway), as the author notes.

## See also

- [Compute pass before bloom](./compute-pass-before-bloom.md)
- [Core shader library include](./core-shader-library-include.md)
- [Raymarched mushroom cloud](./raymarched-mushroom-cloud.md)
- [Burst flash and view shake](./burst-flash-and-view-shake.md)
- [High-altitude burst sky](./high-altitude-burst-sky.md)
- [Dual-hook simulation step](./dual-hook-simulation-step.md)
- [Push a point light through the public LightDebug list, with a compute fill beyond 3 km](../../../howto/rendering/lights-and-particles/add-dynamic-lights-to-the-world/add-dynamic-lights-to-the-world-3.md) - how-to article demonstrating this feature
- [Depth-reading compute dispatches for rings, holes, tracers and relighting](../../../howto/rendering/vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/modify-the-hdr-scene-with-a-compute-pass-1.md) - how-to article demonstrating this feature
- [Add a fireball's diffuse term where the pre-pass culled it beyond about 3 km](../../../howto/rendering/vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/modify-the-hdr-scene-with-a-compute-pass-3.md) - how-to article demonstrating this feature

[^ksarmory-fireball]: src/KSArmory/Ksa/Fireball.cs
[^ksarmory-nuclearclouds]: src/KSArmory/Ksa/NuclearClouds.cs
[^ksarmory-cloudpass]: src/KSArmory/Ksa/CloudPass.cs
[^ksarmory-firelight-comp]: src/KSArmory/Shaders/KSArmoryFireLight.comp
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-blocked]: docs/BLOCKED-ON-KSA.md
[^ksarmory-nuclear-effect]: docs/NUCLEAR-EFFECT.md
[^ksa-lightdebug]: KSA.Rendering.Lighting.LightDebug (decompiled)
[^ksa-light]: KSA.Rendering.Lighting.Light (decompiled)
[^ksa-clusteredlightsystem]: KSA.Rendering.Lighting.ClusteredLightSystem (decompiled)
[^ksa-lightprepass]: Core/Shaders/Lighting/LightPrePass.comp
[^ksa-punctuallighting]: Core/Shaders/Lighting/PunctualLighting.glsl
[^ksa-global-glsl]: Core/Shaders/Common/Global.glsl
[^ksa-program]: KSA.Program (decompiled)
[^ksa-prepassrenderer]: KSA.PrePassRenderer (decompiled)
[^ksa-prepassdata]: KSA.PrePassData (decompiled)
[^ksa-gputexturesystem]: KSA.GpuTextureSystem (decompiled)
[^ksa-astronomicaltemplate]: KSA.AstronomicalTemplate (decompiled)
[^ksa-hapke]: KSA.HapkeScatteringReference (decompiled)
[^ksa-celestial]: KSA.Celestial (decompiled)
[^ksa-computepipelinewrapper]: KSA.Rendering.ComputePipelineWrapper (decompiled)
