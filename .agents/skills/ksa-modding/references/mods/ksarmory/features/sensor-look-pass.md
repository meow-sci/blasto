---
type: Mod Feature
title: TV and thermal look for a camera window, recorded before bloom
description: A two-dispatch compute pass recorded from the SunbloomRenderer.Render prefix turns one secondary viewport's HDR scene into a TV or white-hot/black-hot picture mapped around its log-mean brightness, measured first into a 1x1 image, so bloom and the tonemap expose it normally
mod: ksarmory
category: shaders
techniques:
  - harmony-prefix
  - glsl-shader
  - vulkan-pipeline
  - post-processing
  - per-viewport-pipeline-cache
  - two-pass-reduction
hooks:
  - starmap StarMapAfterGui
  - harmony-prefix KSA.Program.OnFrameCelestials
  - harmony-prefix KSA.SunbloomRenderer.Render
lifecycle: [frame:draw-ui, frame:celestials, render:bloom]
game_types:
  - KSA.SunbloomRenderer
  - KSA.IViewport
  - KSA.Rendering.RenderTarget
  - KSA.Rendering.RenderImage
  - KSA.Rendering.ComputePipelineWrapper
  - KSA.Rendering.BarrierBatch
  - KSA.Rendering.ImageBarrierInfo
  - KSA.ViewportOptionFlags
  - KSA.Program
tags:
  - camera-window
  - secondary-viewport
  - thermal
  - flir
  - tv
  - compute-shader
  - tonemapping
  - per-viewport-post-process
---

A director (an EO mast head or a targeting pod) can drive one of KSA's four secondary viewports,
shown in the mod's own camera window. The window's mode button steps TV, white-hot, black-hot and
colour, the order a pod's mode button does. Everything except colour is a screen-space look applied
by the mod's own compute shader to that viewport's HDR scene image, recorded from the same
`SunbloomRenderer.Render` prefix as the cloud pass and after it. It runs before bloom and the
tonemap, so the grey picture is exposed like the colour one, and the burst, rings, holes and tracers
the cloud pass painted into that viewport go grey with it.[^ksarmory-sensorpass][^ksarmory-cloudpasshook]

It is a look, not a temperature: KSA has no per-pixel heat, so "heat" is scene luminance with the
sky (no depth) read cold.[^ksarmory-sensor-comp]

## How it works

**Who asks.** `KSArmoryMod.DriveCameraWindows` calls `SensorPass.BeginFrame()` (clears the wanted
set), then for every optical head driving a camera window it calls
`SensorPass.Want(index, policy.Sensor)`. `Want` ignores `Colour`, resolves the window index to the
`IViewport` object through `KsaWorld.TryGameViewport` and keys the wanted mode by that object,
because the renderer hands the prefix the viewport itself, not an index. The main view is never
wanted: `DriveCameraWindows` skips `index == main`.[^ksarmory-mod][^ksarmory-ksaworld]
`OpticConfig.Sensor` defaults to `SensorMode.Tv` ("as a pod comes up in"), and the window's toggle
cycles it with `SensorModes.Next`.[^ksarmory-opticconfig][^ksarmory-sensormode][^ksarmory-directorwindow]

**Per-viewport resources** (`SensorPass.ViewFor`, cached by `viewport.ShaderSlot`):

| Resource | Detail |
|---|---|
| Mean image | `RenderImage.CreateColorStorage(renderer, "KSArmory Sensor Mean", 1x1, R32SFloat)` |
| Pipeline | `ComputePipelineWrapper(storage [scene colour, mean], depth [scene depth], ...)` with one push range; shader `KSArmorySensorCompute` from `ModLibrary.TryGet<ShaderReference>` (declared in `KSArmoryParticles.xml` L566)[^ksarmory-particles-xml] |
| Bindings | set 1: 0 = `sceneColor` (rgba16f storage), 1 = `meanLevel` (r32f storage), 2 = `sceneDepth` sampler; the wrapper numbers storage images first, then samplers[^ksa-computepipelinewrapper][^ksa-utils] |
| Rebuild key | Same `IRenderImage` reference **and** same width and height |

The size check matters because `RenderTarget.Rebuild(w, h)` rebuilds its colour and depth images
in place (RenderTarget.cs L178-L185): the object is the same, the `VkImageView` the descriptor set
names is not.[^ksa-rendertarget]

**Recording** (`SensorPass.Record`, per viewport the prefix fires for):

1. Return unless this viewport is in the wanted set and has an `OffscreenTarget` with colour and
   depth images.
2. Barrier the mean image to `StorageReadWriteC` through KSA's `BarrierBatch`.
3. Push `ModeTime = (mode, seconds, 0, 0)`; hazard; `Dispatch(1,1,1)`: the measuring pass.
4. Hazard; push `ModeTime.z = 1`; `Dispatch(ceil(W/8), ceil(H/8), 1)`: the mapping pass.
5. Hazard (destination `AllCommandsBit`, since bloom and the composite follow).
6. Any exception: one warning, clear the wanted set, never throw into the render loop.

**Shader** (`local_size 8x8`):

| Pass | What it does |
|---|---|
| Measure (`z < 0.5`) | Invocation (0,0) alone samples a 16x16 grid of `sceneColor`, averages `log(max(luma, 1e-4))` and stores `exp(mean)` (a log-average, like auto-exposure) into the 1x1 image. Own dispatch, because the mapping pass rewrites the pixels it would read. |
| Map, TV (mode 1) | `t = y / (y + mid)`, then `smoothstep`; grain 0.02 |
| Map, white-hot (2) / black-hot (3) | Sky (`depth <= 0`, reverse-Z far plane) multiplied by 0.12 ("cold"); `t = clamp((t - 0.18) x 1.6)`; black-hot inverts; a fixed per-column pattern of +-1% like an uncooled detector; grain 0.025 |
| Both | Vignette `1 - 0.6 |uv - 0.5|^2`; clamp to `[0, 0.999]`; back to scene scale `g = mid x t / (1 - t)`; write `vec3(g)`, alpha kept |

Mapping `y -> y/(y+mid)` and back with `mid x t/(1-t)` is the trick: the curve and the inversion
act on a 0..1 value centred on the frame's own mean, and the result goes back into scene units so
KSA's bloom and tonemap expose it exactly as they would have exposed the colour picture.

## Lifecycle and timing

| Moment | Where (build 5554) | What happens |
|---|---|---|
| `frame:draw-ui` (HUD shown) or `frame:celestials` (HUD hidden) | `StepOnce` -> `DriveCameras` -> `DriveCameraWindows` (KSArmoryMod.cs L1065-L1107) | Wanted modes rebuilt for this frame |
| `render:bloom`, secondary viewports | `RenderGame` loops `ViewportRegistry.Views` (Program.cs L4672-L4684) into `RenderViewport` (L4468); inside `if (viewport.HasAny(ViewportOptionFlags.RenderAtmosphere))` colour goes to `StorageReadFc`, depth to `DepthSampledReadVfc` (L4578-L4582), then `_sunbloomRenderer.Render(commandBuffer, viewport, frameIndex)` (L4584) | The prefix calls `CloudPass.Record` then `SensorPass.Record` for that window[^ksa-program] |
| `render:bloom`, main viewport | Program.cs L4915, before `_overallBloomRenderer.Render` L4916 | Prefix fires; `SensorPass.Record` returns (main view is never wanted) |

Why there:

- KSA has no per-viewport post-process hook; `SunbloomRenderer.Render(CommandBuffer, IViewport, int)`
  is public, called once per rendered viewport, and hands over that viewport.[^ksa-sunbloomrenderer]
- The four secondary viewports are created with the `RenderAtmosphere` flag (Program.cs L951-L953;
  `ViewportPresets.SECONDARY_GAME`), so the prefix does fire for camera windows.[^ksa-viewportpresets]
- At that instant the scene colour is already in `General` layout for storage access and the depth
  is sampleable (`ImageBarrierInfo.Presets`), so the pass needs no image barriers of its own except
  for its private mean image.[^ksa-imagebarrierinfo]
- It is recorded after `CloudPass.Record` in the same prefix (CloudPassHook.cs L104-L109), whatever
  `Config.ShaderPass` is set to: "a sensor sees the burst too, and its look is the director's setting
  rather than a display one".

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/SensorPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/SensorPass.cs) | Wanted set, per-viewport pipeline cache, two dispatches |
| [src/KSArmory/Shaders/KSArmorySensor.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmorySensor.comp) | Log-mean measure, TV/thermal mapping |
| [src/KSArmory/Ksa/CloudPassHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPassHook.cs#L100-L110) | The prefix: cloud pass first, sensor pass last |
| [src/KSArmory/Ksa/KSArmoryMod.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KSArmoryMod.cs#L1065-L1107) | `DriveCameraWindows`: `BeginFrame` and `Want` per driven window |
| [src/KSArmory/Sim/SensorMode.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SensorMode.cs) | Mode enum, cycle order, labels (TV, WHOT, BHOT, CLR) |
| [src/KSArmory/Ksa/Ui/DirectorWindow.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/DirectorWindow.cs#L95-L99) | The window's mode toggle |
| [src/KSArmory/KSArmoryParticles.xml](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/KSArmoryParticles.xml#L563-L571) | `<Shader Id="KSArmorySensorCompute" .../>` asset |

## Snippets

```csharp
// src/KSArmory/Ksa/CloudPassHook.cs L100-L110
    private static void BeforeSunbloom(CommandBuffer commandBuffer, IViewport viewport, int frameIndex)
    {
        // Always: at zero the pass skips only the cloud, and the rings, holes and tracers it paints
        // have no other way onto the screen.
        float tint = _tint?.Invoke() ?? 0f;
        CloudPass.Record(commandBuffer, viewport, frameIndex, Math.Max(0f, tint));

        // Last and whatever the cloud pass is set to: a sensor sees the burst too, and its look is
        // the director's setting rather than a display one.
        SensorPass.Record(commandBuffer, viewport);
    }
```

```csharp
// src/KSArmory/Ksa/SensorPass.cs L57-L90
    public static void Record(CommandBuffer commandBuffer, IViewport viewport)
    {
        try
        {
            if (!_wanted.TryGetValue(viewport, out SensorMode mode)) return;
            if (viewport.OffscreenTarget is not { } target) return;
            if (target.ColorImage is not { } colour || target.DepthImage is not { } depth) return;

            int width = (int)target.Extent.Width;
            int height = (int)target.Extent.Height;
            if (width <= 0 || height <= 0) return;

            View? view = ViewFor(viewport, colour, depth, width, height);
            if (view?.Pipeline is null) return;

            Span<VkImageMemoryBarrier2> one = stackalloc VkImageMemoryBarrier2[1];
            BarrierBatch toStorage = new(one);
            toStorage.Add(view.Mean, ImageBarrierInfo.Presets.StorageReadWriteC);
            toStorage.SubmitAndFlush(commandBuffer);

            float seconds = (float)(Environment.TickCount64 % 100_000L) * 0.001f;
            Push push = new() { ModeTime = new float4((float)mode, seconds, 0f, 0f) };

            Hazard(commandBuffer);
            view.Pipeline.BindPipeline(commandBuffer, viewport.ShaderSlot, default, default, push);
            commandBuffer.Dispatch(1, 1, 1);

            Hazard(commandBuffer);
            push.ModeTime.Z = 1f;
            view.Pipeline.BindPipeline(commandBuffer, viewport.ShaderSlot, default, default, push);
            commandBuffer.Dispatch((width + Group - 1) / Group, (height + Group - 1) / Group, 1);

            Hazard(commandBuffer);
        }
```

```glsl
// src/KSArmory/Shaders/KSArmorySensor.comp L61-L94
    vec4 c = imageLoad(sceneColor, px);
    float y = max(dot(c.rgb, Luma), 0.0);
    float mid = max(imageLoad(meanLevel, ivec2(0)).r, 1.0e-4);

    vec2 uv = (vec2(px) + 0.5) / vec2(size);
    bool sky = texture(sceneDepth, uv).r <= 0.0;

    // Into 0..1 about the mean, where a curve and an inversion mean something.
    float heat = y;
    if (mode != 1 && sky) heat *= 0.12;
    float t = heat / (heat + mid);

    if (mode == 1)
    {
        t = smoothstep(0.0, 1.0, t);
    }
    else
    {
        // Thermal detail is coarse and contrasty; the hottest things clip.
        t = clamp((t - 0.18) * 1.6, 0.0, 1.0);
        if (mode == 3) t = 1.0 - t;
        // A faint fixed column pattern, as an uncooled detector leaves.
        t += (Hash(vec2(px.x, 0.0)) - 0.5) * 0.02;
    }

    float grain = (Hash(vec2(px) + fract(pc.ModeTime.y * 13.7)) - 0.5) * (mode == 1 ? 0.02 : 0.025);
    vec2 d = uv - 0.5;
    float vignette = 1.0 - dot(d, d) * 0.6;

    t = clamp((t + grain) * vignette, 0.0, 0.999);

    // Back out of 0..1 onto the scene's scale, so the tonemap exposes it as it did the colour.
    float g = mid * t / (1.0 - t);
    imageStore(sceneColor, px, vec4(vec3(g), c.a));
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `SunbloomRenderer.Render(CommandBuffer, IViewport, int)` | [current/decomp/KSA/SunbloomRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SunbloomRenderer.cs#L326) | Patched (prefix); called per rendered viewport |
| `Program.RenderGame`, `RenderViewport` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4468-L4584) | Secondary-viewport recording and the barriers before the prefix |
| `IViewport.OffscreenTarget`, `ShaderSlot` | [current/decomp/KSA/IViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IViewport.cs#L13-L31) | The window's colour/depth images; the per-viewport global set index[^ksa-iviewport] |
| `RenderTarget.ColorImage`, `DepthImage`, `Extent`, `Rebuild` | [current/decomp/KSA.Rendering/RenderTarget.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderTarget.cs#L36-L48) | Image access; in-place rebuild on resize (L178-L185) |
| `RenderImage.CreateColorStorage` | [current/decomp/KSA.Rendering/RenderImage.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/RenderImage.cs#L118) | The 1x1 mean image[^ksa-renderimage] |
| `ComputePipelineWrapper` ctor, `BindPipeline<T>` | [current/decomp/KSA.Rendering/ComputePipelineWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ComputePipelineWrapper.cs#L32-L222) | Descriptor set and pipeline from a `<Shader>` asset; set 0 = this viewport's global set |
| `ImageBarrierInfo.Presets.StorageReadWriteC`; `BarrierBatch` | [current/decomp/KSA.Rendering/ImageBarrierInfo.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ImageBarrierInfo.cs#L51) | Mean image into `General` for compute read/write |
| `ViewportPresets.SECONDARY_GAME` | [current/decomp/KSA/ViewportPresets.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ViewportPresets.cs#L7) | Includes `RenderAtmosphere`, which gates the prefix call in `RenderViewport` |

## Pitfalls and version notes

- **Main view excluded by design.** The main view is the player's own and always colour
  (`OpticConfig.Sensor` doc comment); `DriveCameraWindows` never calls `Want` for it.
- **The wanted set is a frame's state, not a setting.** It is rebuilt only in `StepOnce`: with the
  HUD hidden the `OnFrameCelestials` prefix still rebuilds it before the render; if the step only ran
  from the `OnFrame` postfix, that frame's render used the previous frame's set.
- **Grain is on wall time.** `Environment.TickCount64` drives the noise, so it keeps moving while
  the game is paused (unlike the cloud pass, which is on simulated time).
- **Rebuilt views are not disposed.** `ViewFor` replaces a cached view (and `Release` clears them)
  without disposing the old mean image or pipeline; the cloud pass buries its images for 16 frames
  instead (`CloudPass.Bury`, `GraveFrames`).[^ksarmory-cloudpass] Observed in the code, not measured.
- **Doc drift:** `docs/BLOCKED-ON-KSA.md` "A thermal or FLIR channel on the optical head" (L611-L662)
  still plans a full-screen post-process routed through another mod's post shader, says a secondary
  viewport draws no planet or terrain, and says there are no scorch decals. The built feature is a
  per-viewport compute look from the `SunbloomRenderer.Render` prefix, windows draw terrain since
  2026.10.7.5541 (as `OpticConfig.Viewport`'s own comment says), and the mod now paints shell holes
  and scorch marks.[^ksarmory-blocked] The physics half (real per-object heat) remains blocked: KSA
  has no temperature per pixel.

## See also

- [Director camera window](./director-camera-window.md)
- [Optical director heads](./optical-director-heads.md)
- [Compute pass before bloom](./compute-pass-before-bloom.md)
- [Raymarched mushroom cloud](./raymarched-mushroom-cloud.md)
- [Compute shell tracers](./compute-shell-tracers.md)
- [Dual-hook simulation step](./dual-hook-simulation-step.md)
- [Colour-grade a camera feed: a two-dispatch measure and map pass per viewport with a cached pipeline](../../../howto/shaders/screen-effects/colour-grade-the-frame-and-add-a-vignette/colour-grade-the-frame-and-add-a-vignette-2.md) - how-to article demonstrating this feature

[^ksarmory-sensorpass]: src/KSArmory/Ksa/SensorPass.cs
[^ksarmory-sensor-comp]: src/KSArmory/Shaders/KSArmorySensor.comp
[^ksarmory-cloudpasshook]: src/KSArmory/Ksa/CloudPassHook.cs
[^ksarmory-sensormode]: src/KSArmory/Sim/SensorMode.cs
[^ksarmory-opticconfig]: src/KSArmory/Sim/OpticConfig.cs
[^ksarmory-directorwindow]: src/KSArmory/Ksa/Ui/DirectorWindow.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-particles-xml]: src/KSArmory/KSArmoryParticles.xml
[^ksarmory-blocked]: docs/BLOCKED-ON-KSA.md
[^ksarmory-cloudpass]: src/KSArmory/Ksa/CloudPass.cs
[^ksa-program]: KSA.Program (decompiled)
[^ksa-sunbloomrenderer]: KSA.SunbloomRenderer (decompiled)
[^ksa-iviewport]: KSA.IViewport (decompiled)
[^ksa-rendertarget]: KSA.Rendering.RenderTarget (decompiled)
[^ksa-renderimage]: KSA.Rendering.RenderImage (decompiled)
[^ksa-computepipelinewrapper]: KSA.Rendering.ComputePipelineWrapper (decompiled)
[^ksa-utils]: KSA.Rendering.Utils (decompiled)
[^ksa-imagebarrierinfo]: KSA.Rendering.ImageBarrierInfo (decompiled)
[^ksa-viewportpresets]: KSA.ViewportPresets (decompiled)
