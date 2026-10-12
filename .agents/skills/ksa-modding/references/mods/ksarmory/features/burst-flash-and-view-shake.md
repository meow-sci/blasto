---
type: Mod Feature
title: A burst's whiteout and a blast front's view shake, painted into the scene image instead of moving the camera
description: A nuclear flash whites out the view on an eye-adaptation model and a passing blast front shakes it, both stepped on the simulated clock and written by compute dispatches into the HDR scene before bloom, never into a camera
mod: ksarmory
category: shaders
techniques:
  - harmony-prefix
  - glsl-shader
  - vulkan-pipeline
  - post-processing
  - specialization-constants
  - screen-space-shake
  - sim-boundary
hooks:
  - starmap StarMapAfterGui
  - harmony-prefix KSA.Program.OnFrameCelestials
  - starmap StarMapAfterOnFrame
  - harmony-prefix KSA.SunbloomRenderer.Render
lifecycle: [frame:draw-ui, frame:celestials, frame:after-on-frame, render:bloom]
game_types:
  - KSA.Program
  - KSA.IGameViewport
  - KSA.GameViewport
  - KSA.Camera
  - KSA.Celestial
  - KSA.SunbloomRenderer
  - KSA.ScreenshotCapture
  - KSA.InputAction
  - KSA.Rendering.ComputePipelineWrapper
  - KSA.GpuRegionExtensions
tags:
  - flash
  - whiteout
  - glare
  - eye-adaptation
  - view-shake
  - camera-shake
  - compute-shader
  - nuclear
  - hidden-hud
  - screenshots
---

Two things happen to the player's view when a nuclear burst goes off nearby in KSArmory. First the
screen goes white for a moment (violet at first, warming as the fireball cools) with a glare round
the ball that lasts as long as it burns. Seconds later, when the blast front reaches the camera, the
picture jolts and rattles, as hard as the overpressure there. Both are split the same way: a game-free
model on the mod's simulated step decides **how much**, and the mod's compute pass
([compute pass before bloom](./compute-pass-before-bloom.md)) writes it into KSA's HDR scene image
before bloom.[^ksarmory-burstflash][^ksarmory-blastshake]

**No camera member is written.** The shake moves the rendered picture (translate, roll and a slight
zoom) inside the shock pass rather than turning the camera, "so it needs no hold on a view something
else may be driving and the panel stays still over it". The whiteout is drawn into the scene rather
than as an ImGui overlay, because KSA wraps its whole UI pass in `if (DrawUI)` and both F2 and
`ScreenshotCapture` clear that flag; drawn as UI it vanished from every screenshot the author's
harness took.[^ksarmory-viewshake][^ksa-program][^ksa-screenshotcapture]

## How it works

### 1. The flash model (`BurstFlash.Update`)

Read once per step over every burst still burning (`NuclearClouds.TryBurning`), on any body: a burst
on an airless moon blinds as well as one in air.[^ksarmory-burstflash][^ksarmory-nuclearclouds]

| Step | Code | Rule |
|---|---|---|
| Eye | `KsaWorld.TryMainCameraPose` | `Program.MainViewport.BaseCamera`: position `EgoToEcl(0)` and `GetForwardEcl()`. The base camera, because `GetMainCamera()` returns the map camera in map mode[^ksarmory-ksaworld][^ksa-gameviewport] |
| Field | `KsaWorld.MainViewFovDeg` | `MainViewport.GetCamera().GetFieldOfView()` in degrees; half of it is the "in frame" cone, so a zoomed sight is blinded only by what it looks at |
| Light at the eye, in suns | `FlashGlare.Suns(kt, range, glow, airRatio)` | Thermal power at the pulse peak (`ThermalAltitude.ThermalFraction(air) x yield / (3 x peak time)`), spread over `4 pi r^2`, divided by 1361 W/m2, scaled by `glow / PeakGlow`[^ksarmory-flashglare][^ksarmory-thermalaltitude] |
| Adaptation | `FlashGlare.AdaptedTo(sunElevation)` | 1 sun by day to 0.01 at night, smoothstepped over -8 to +6 deg of sun elevation at the eye |
| Direction | `FlashGlare.Reaching(offAxis, halfField)` | 1 inside the field, smoothstep down to a 0.12 floor over 70 deg past its edge: scattered light never reaches zero |
| What the eye makes of it | `FlashGlare.Level(suns x reaching, adapted)` | `log10(1 + suns / adapted) / 1.5`, clamped to [0, 1]: logarithmic, so four times further is a step dimmer |
| Whiteout | rise-driven | Only the **increase** of the brightest level blinds: `_held` decays as `exp(-dt / fade)`, each rise is added (capped at 1), output clamped to 0.97 |
| Recovery | `FlashGlare.RecoverySeconds(adapted)` | Time constant 0.28 s by day to 2.2 s at night, eased on the log of the adaptation |
| Colour | `FlashGlare.VioletShare(age)` | `exp(-age / 0.8 s)`; the veil keeps the violet of the rises that built it, the halo takes the ball's current colour |
| Glare (halo) | level, not rise | Held at the brightest level, so the halo lasts as long as the ball is bright while the whiteout recovers |

Outputs are static properties read by the render pass: `Whiteout`, `Glare`, `Violet`, `HaloViolet`,
`GlareColour` and `SourceIndex` (an index into the burning list, resolved against each viewport's
camera at record time).

### 2. Drawing the flash (`CloudPass.Flash`, `KSArmoryCloud.comp`)

One full-screen dispatch of the **cloud pipeline**, last in `CloudPass.Record`, skipped when both
`Whiteout` and `Glare` are zero. Nothing new is bound: the flash is a mode of the cloud shader selected
by push-constant values that cannot occur on a cloud or ground-mark dispatch.[^ksarmory-cloudpass][^ksarmory-cloud-comp]

| Push field | Flash dispatch value |
|---|---|
| `CentreRadius` | Ball centre (`NuclearClouds.TryBall`: the cap centre where a cap rises) minus `camera.PositionEcl`, radius **0** |
| `FireSun` | `(Glare, Violet, Whiteout, 0)`; `w = 0` and radius 0 mark "flash only" |
| `Shape` | `(GlareColour.rgb, HaloViolet)` |
| `AgeStrengthWind` | `(ball radius, 1 if a thin-air debris shell, 0, 0)` |

In the shader (KSArmoryCloud.comp L1746-L1797), ahead of every cloud and mark guard:

- `BallVisibleShare` taps the depth buffer at nine points across the ball's disc, so a hull in front
  removes the core and halo and leaves only the whiteout.
- `core = seen / (1 + (off / 6 deg)^2)`, `white = seen / (1 + (off / 20 deg)^2)`: veiling glare
  falls roughly with the inverse square of the angle.
- Brightness `GlareFloorNits x (1 + GlareOverexposure x whiteout^2) + GlareCoreNits x core` with
  defaults 1.1, 1.6 and 3.0. The comment records the calibration: written values of 0.25 come out
  of KSA's post chain at mid grey, 1.0 at 240 and 1.25 and above clip to white.
- The scene is mixed toward that colour by `whiteout x (0.95 + 0.05 core)`, then the halo is mixed in
  toward the ball's colour by `min(Glare x 0.8, 0.85) x core`; over a thin-air debris shell the
  brighter of the two is kept instead.
- All glare constants are Vulkan specialization constants (ids 7-13), live-tunable through
  `ShaderTunables`.[^ksarmory-shadertunables]

### 3. The shake model (`BurstSound` -> `BlastShake` -> `ViewShake`)

The shake is struck by the sound code, so the bang and the jolt are one arrival
([burst sound at blast arrival](./burst-sound-at-blast-arrival.md)). `BurstSound.Update` follows each
burst's `BlastFront` to the main camera every step; when the front radius reaches the camera's range
it computes the peak overpressure there (`BlastAltitude.PeakPascals`, Sachs-scaled to the air at the
eye, with the ground's reflection gain) and, if `BurstHearing.Heard` (at least 50 Pa), calls
`BlastShake.Struck(pascals, positivePhaseSeconds, seed)`.[^ksarmory-burstsound][^ksarmory-bursthearing][^ksarmory-blastaltitude]

| Term | `ViewShake` rule |
|---|---|
| Strength | `min(1, sqrt(p / 34,474 Pa))`: full at 5 psi |
| Duration | `min(0.4 + 1.5 x positive phase, 3)` s |
| Envelope | `strength x 0.03 x min(1, t / 0.03 s) x exp(-3 t / T)`; nothing under 0.0008 (under a pixel at 1080 lines) |
| Motion | Per axis, the mean of three sines at `7 + 5.3k + 1.7axis` Hz with seeded phases; roll is half the translation, in radians |
| Units | Shares of the screen height, so the same shake at every resolution |

`BlastShake.Update(dt)` advances every running rattle, sums them and clamps each axis to the 0.03
maximum, publishing `Offset` (x, y, roll) and `Shaking`.[^ksarmory-viewshake][^ksarmory-blastshake]

### 4. Drawing the shake (`CloudPass.Shock`, `KSArmoryShock.comp`)

The shock pass ([shock-front refraction](./shock-front-refraction.md)) copies the scene and writes it
back from the copy. The first front's push carries `Shake = (x, y, roll, 1)`; with no front to draw but
`Shaking` true, one push with zero bend is dispatched only to move the picture.[^ksarmory-cloudpass]
The shader rotates the aspect-corrected UV about the screen centre by the roll, divides it by
`1 + 2(|x| + |y| + |roll|)` (zooms in just enough that the edge the move uncovers stays off screen),
offsets it by `(x, y)` and reads the copy there.[^ksarmory-shock-comp]

## Lifecycle and timing

| Moment | Where (build 5554) | What happens |
|---|---|---|
| `frame:viewports` | `GameViewport.OnFrame` -> `Camera.OnFrame` (Camera.cs L487), from `Program.OnFrameViewports` (Program.cs L2263) | KSA settles this frame's camera pose and matrices; the mod only reads them[^ksa-camera] |
| `frame:draw-ui` (HUD shown) | `[StarMapAfterGui]` postfix on `Program.OnDrawUiViewports` (L2268) -> `StepOnce` -> `StepSimulation` tail | `NuclearClouds.Update` (KSArmoryMod.cs L933-L935), `BurstFlash.Update` (L940), `BurstSound.Update` (L943, may strike a shake), `BlastShake.Update` (L946), all with `_lastSimStep`[^ksarmory-mod] |
| `frame:celestials` (HUD hidden) | Harmony prefix on `Program.OnFrameCelestials` (L2320), `PreRenderHook` | Same step when the UI pass did not run (F2 or a screenshot)[^ksarmory-prerenderhook] |
| `frame:after-on-frame` | `[StarMapAfterOnFrame]` | Fallback only if the prefix could not be installed; the values then reach the screen a frame late |
| `render:bloom` | `Program.Render` (L2324) -> `RenderGame` -> prefix on `SunbloomRenderer.Render` (L4915) -> `CloudPass.Record` | Shock pass (with the shake) after the clouds' resolve, then tracers, then `Flash` last (CloudPass.cs L776-L783)[^ksa-sunbloom] |

So on a normal frame the flash and shake computed in the step are recorded into the **same** frame's
command buffer: both step hooks run before `Program.Render`. The step is gated on the scene, not the
craft, so a whiteout continues after the controlled vehicle is destroyed by the burst.

Because the values are on the simulated step (`_lastSimStep` is the engine's applied step, 0 when
nothing advanced), both freeze while paused: `BurstFlash.Update` skips its decay when `dt` is 0, and
`ViewShake.At` is a pure function of time since arrival, so a paused frame keeps its current displacement.

## Key code

| Path | Role |
|---|---|
| [src/KSArmory/Ksa/BurstFlash.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BurstFlash.cs#L87-L196) | Brightest burst, rise-driven whiteout, recovery, violet veil, halo level |
| [src/KSArmory/Sim/FlashGlare.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/FlashGlare.cs) | Suns at the eye, adaptation, log response, recovery, off-axis floor |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L2022-L2068) | `Flash`: the flash-only push and its full-screen dispatch |
| [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L1730-L1797) | Whiteout, glare core, violet veil and halo |
| [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L553-L590) | `BallVisibleShare`: is the ball hidden behind something |
| [src/KSArmory/Ksa/BlastShake.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BlastShake.cs) | Running rattles, summed and clamped `Offset`, `Struck` |
| [src/KSArmory/Sim/ViewShake.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ViewShake.cs) | Strength, duration, envelope and rattle |
| [src/KSArmory/Ksa/BurstSound.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BurstSound.cs#L240-L264) | `Arrive`: the pressure at the eye strikes the shake and plays the bang |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L954-L987) | The shake handed to the shock pass's first push |
| [src/KSArmory/Shaders/KSArmoryShock.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryShock.comp#L105-L125) | Rotate, zoom and offset the picture |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4708-L4734) | `TryMainCameraPose` (base camera); `MainViewFovDeg` at L4017-L4034 |
| [tests/KSArmory.Tests/FlashGlareTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/FlashGlareTests.cs) | Game-free tests of the glare laws ([tests/KSArmory.Tests/ViewShakeTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/ViewShakeTests.cs) for the shake)[^ksarmory-flashglare-tests][^ksarmory-viewshake-tests] |

## Snippets

```csharp
// src/KSArmory/Ksa/BurstFlash.cs L158-L189
            // Driven by the RISE and recovering always, rather than held at whatever is burning.
            // Held at the level, the ball keeps the view at full white for the whole of its 1.9 s
            // burn and only then begins a two and a half second decay -- flown, and still near
            // white at 3.8 s, which is a fault rather than a flash. An eye adapts while the source
            // is still there, so what blinds is the change.
            double rise = Math.Max(0.0, brightest - _seen);
            _seen = brightest;

            // ADDED, not the larger of the two: a flash that arrives over a few frames blinds as much
            // as one that arrives in one. Held at the largest step, a burst reaching full brightness
            // in two frames stops at the first one's 0.6.
            if (dt > 0.0) _held *= Math.Exp(-dt / _fadeSeconds);
            double added = Math.Min(_held + rise, 1.0) - _held;

            // THE VEIL KEEPS THE COLOUR OF WHAT BLINDED IT. It is the eye's afterimage, so it is
            // violet if the violet flash drove it, however far the ball has warmed since -- read
            // off the ball's age instead, a night whiteout stayed clipped white while the violet
            // passed and cleared peach. Mixed by how much of the whiteout each rise put there.
            if (added > 0.0)
            {
                _veilViolet = ((_held * _veilViolet) + (added * violetNow)) / (_held + added);
            }

            _held += added;
            Violet = (float)_veilViolet;

            Whiteout = (float)Math.Clamp(_held, 0.0, MostOpaque);
            if (Whiteout <= 0.004f) Whiteout = 0f;

            Glare = (float)Math.Clamp(brightest, 0.0, 1.0);
            if (Glare <= 0.004f) Glare = 0f;
            if (source >= 0) GlareColour = new float3((float)colour.X, (float)colour.Y, (float)colour.Z);
```

```glsl
// src/KSArmory/Shaders/KSArmoryCloud.comp L1746-L1796
    bool flashDispatch = pc.CentreRadius.w <= 0.0 && pc.FireSun.w <= 0.0;
    if (flashDispatch && (pc.FireSun.z > 0.0 || pc.FireSun.x > 0.0))
    {
        vec4 was = imageLoad(sceneColor, at);
        float whiteout = pc.FireSun.z;
        // ...
        float core = 0.0;
        float white = 0.0;
        if (dot(pc.CentreRadius.xyz, pc.CentreRadius.xyz) > 0.0)
        {
            vec3 ray = normalize(WorldAt((vec2(at) + 0.5) / vec2(size), 1.0));
            float offDeg = degrees(acos(clamp(dot(ray, normalize(pc.CentreRadius.xyz)), -1.0, 1.0)));
            float seen = BallVisibleShare(pc.CentreRadius.xyz, pc.AgeStrengthWind.x, size);
            core = seen / (1.0 + (offDeg / GlareCoreDeg) * (offDeg / GlareCoreDeg));
            white = seen / (1.0 + (offDeg / GlareWhiteDeg) * (offDeg / GlareWhiteDeg));
        }
        // ...
        vec3 hue = mix(GlareWarm, GlareHot, white * (0.35 + 0.65 * whiteout));
        // ...
        float violetShare = clamp(pc.FireSun.y, 0.0, 1.0);
        hue = mix(hue, mix(VeilViolet, VeilLilac, white), violetShare);
        // ...
        float nits = GlareFloorNits * (1.0 + GlareOverexposure * whiteout * whiteout)
                   + GlareCoreNits * core;

        // The edge veils a little less, so the scene shows there first as the flash recovers.
        float veil = whiteout * (0.95 + 0.05 * core);
        vec3 blinded = mix(was.rgb, hue * nits, veil);
        // ...
        vec3 ball = pc.Shape.xyz / max(max(pc.Shape.x, max(pc.Shape.y, pc.Shape.z)), 1e-3);
        ball = mix(ball, VeilLilac, 0.7 * clamp(pc.Shape.w, 0.0, 1.0));
        float halo = min(pc.FireSun.x * HaloGain, HaloMost) * core;
        vec3 haloed = mix(blinded, ball * (0.9 + 1.2 * core), halo);
        // ...
        blinded = pc.AgeStrengthWind.y > 0.5 ? max(blinded, haloed) : haloed;

        imageStore(sceneColor, at, vec4(blinded, was.a));
```

```csharp
// src/KSArmory/Sim/ViewShake.cs L50-L63
    public static (double X, double Y, double Roll) At(double strength, double since, double seconds, int seed)
    {
        if (!(strength > 0.0) || !(since >= 0.0) || !(seconds > 0.0) || since > seconds) return (0.0, 0.0, 0.0);

        double envelope = strength * MostShare * Math.Min(1.0, since / AttackSeconds)
                          * Math.Exp(-3.0 * since / seconds);
        if (envelope < FaintestShare) return (0.0, 0.0, 0.0);

        double x = Rattle(since, seed, 0) * envelope;
        double y = Rattle(since, seed, 1) * envelope;
        double roll = Rattle(since, seed, 2) * envelope * RollPerShare;

        return (x, y, roll);
    }
```

```glsl
// src/KSArmory/Shaders/KSArmoryShock.comp L105-L125
    vec2 uv = (vec2(at) + 0.5) / vec2(size);
    vec2 from = uv;
    bool shaking = pc.Shake.w > 0.5;

    // The picture thrown and turned about its centre, and pushed in by as much as it moved so the
    // edge it leaves behind stays off the screen.
    if (shaking)
    {
        float aspect = float(size.x) / float(size.y);
        vec2 c = (uv - 0.5) * vec2(aspect, 1.0);
        float s = sin(pc.Shake.z), k = cos(pc.Shake.z);
        c = vec2(k * c.x - s * c.y, s * c.x + k * c.y);
        c /= 1.0 + 2.0 * (abs(pc.Shake.x) + abs(pc.Shake.y) + abs(pc.Shake.z));
        from = (c + pc.Shake.xy) / vec2(aspect, 1.0) + 0.5;
    }

    vec2 bend = RingBend(uv);
    if (!shaking && bend == vec2(0.0)) return;

    vec4 was = imageLoad(sceneColor, at);
    imageStore(sceneColor, at, vec4(CopyAt(from + bend, size).rgb, was.a));
```

## Game APIs touched

| API | Decompiled file | Use |
|---|---|---|
| `Program.MainViewport`, `GetRenderCamera`, `DrawUI` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L484) | Main view (L484) for the eye; render camera (L641) for drawing; `DrawUI` (L526) guards the UI pass at L2265 and is toggled by `InputAction.ToggleUi` at L1800-L1802 |
| `IGameViewport.BaseCamera`, `GameViewport.GetCamera` | [current/decomp/KSA/IGameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IGameViewport.cs#L19) | The scene camera even in map mode; `GetCamera` returns `MapCamera` in map mode ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L297-L300) L297)[^ksa-igameviewport][^ksa-gameviewport] |
| `Camera.EgoToEcl`, `GetForwardEcl`, `GetFieldOfView`, `PositionEcl`, `VPInv`, `OnFrame` | [current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L176) | Read only: forward (L176), position (L223), field of view (L790), inverse view-projection (L65); all rebuilt by KSA in `OnFrame` (L487) |
| `ScreenshotCapture` | [current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L225-L233) | Sets `Program.DrawUI = false` for a capture unless told to keep the HUD |
| `InputAction.ToggleUi` bound to F2 | [current/decomp/KSA/Input.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Input.cs#L202) | The player's HUD toggle[^ksa-input] |
| `Celestial.GetPositionEcl`, `GetCce2Ccf` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L386) | Burst and ball positions re-derived each frame from the body's rotating frame (L386, L538)[^ksa-celestial] |
| `SunbloomRenderer.Render` | [current/decomp/KSA/SunbloomRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SunbloomRenderer.cs#L326) | Prefix target where both are recorded |
| `ComputePipelineWrapper.BindPipeline<T>` | [current/decomp/KSA.Rendering/ComputePipelineWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ComputePipelineWrapper.cs#L210) | Cloud pipeline reused for the flash; shock pipeline for the shake[^ksa-cpw] |
| `GpuRegionExtensions.TagRegion` | [current/decomp/KSA/GpuRegionExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GpuRegionExtensions.cs#L10) | `KSArmory Cloud: flash` and `: fronts` timing regions[^ksa-gpuregion] |

## Pitfalls and version notes

- **The shake is drawn only when a cloud is marched.** `Shock` (and with it the shake) is called only
  on the branch of `CloudPass.Record` where at least one non-thin cloud is in the draw list; with none,
  `Record` draws tracers and the flash and returns (CloudPass.cs L654-L659). A front struck by a burst
  in air under 1% of sea-level density (no mushroom, but `BurstSound` still follows its front and
  strikes the shake when the eye's pressure is high enough) is therefore not visibly shaken unless
  another cloud is standing. This is read from the code, not observed in game.
- **Everything is behind the burst-visual gates.** `Config.ShaderPass` at 0, no `NearbyCelestial` on
  the render camera, or a failed weather-shadow lookup returns from `Record` before either effect;
  `Config.NuclearClouds` off calls `NuclearClouds.Clear()` every step, which resets `BurstFlash` and
  `BlastShake` too.[^ksarmory-mod][^ksarmory-nuclearclouds]
- **Main view's eye, every view's picture.** The whiteout level, its source and the shake are
  computed once for the main view's base camera, but neither `Flash` nor `Shock` filters by viewport,
  so camera windows recorded through the same pass white out and shake with the main view.
- **What is not shaken.** Tracers and the whiteout are recorded after the shock pass, and KSA's
  post-bloom draws (orbit lines, gizmos, grid, gauges) and the ImGui HUD come later in the frame, so
  those stay still while the scene under them moves.
- **The whiteout is HDR, then tonemapped.** The nits constants were calibrated by writing known
  values through KSA's post chain at its default exposure; a change to KSA's exposure or tonemap
  changes how white it reads.[^ksarmory-cloud-comp]
- **Rise, not level.** Driving the whiteout by the level held the screen white for the whole burn;
  the author flew it "still near white at 3.8 s" and switched to the rise.[^ksarmory-burstflash]
  docs/NUCLEAR-NEXT.md records the flown tuning (for example the 0.3 kt whiteout at 2.4 km).[^ksarmory-nuclear-next]
- **Doc drift.** The audit list in docs/NUCLEAR-ALTITUDE.md (item 9) says the shake uses reflection 2
  and cites `BlastShake.cs:104`; the current `BlastShake.cs` is 73 lines and `BurstSound.Arrive`
  passes the ground's `GainAt` gain, as that file's own step 9 says was fixed.[^ksarmory-nuclear-altitude]

## See also

- [Compute pass before bloom](./compute-pass-before-bloom.md)
- [Shock-front refraction](./shock-front-refraction.md)
- [Burst sound at blast arrival](./burst-sound-at-blast-arrival.md)
- [Raymarched mushroom cloud](./raymarched-mushroom-cloud.md)
- [Fireball light injection](./fireball-light-injection.md)
- [High-altitude burst sky](./high-altitude-burst-sky.md)
- [Sensor look pass](./sensor-look-pass.md)
- [Borrow main view camera](./borrow-main-view-camera.md)
- [Read the scene camera with MainViewport.BaseCamera when the main camera may be the map camera](../../../howto/camera/controllers/understand-camera-modes-and-controllers/understand-camera-modes-and-controllers-2.md) - how-to article demonstrating this feature
- [Depth-reading compute dispatches for rings, holes, tracers and relighting](../../../howto/rendering/vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/modify-the-hdr-scene-with-a-compute-pass-1.md) - how-to article demonstrating this feature
- [Give a compute pass the camera pose without losing precision, or the wrong camera](../../../howto/shaders/compute-effects/pack-parameters-for-a-compute-effect/pack-parameters-for-a-compute-effect-4.md) - how-to article demonstrating this feature
- [Flash and shake the view: a flash-only mode of a compute shader and a shock-pass shake on the sim clock](../../../howto/shaders/screen-effects/refract-and-distort-the-scene/refract-and-distort-the-scene-3.md) - how-to article demonstrating this feature

[^ksarmory-burstflash]: src/KSArmory/Ksa/BurstFlash.cs
[^ksarmory-flashglare]: src/KSArmory/Sim/FlashGlare.cs
[^ksarmory-blastshake]: src/KSArmory/Ksa/BlastShake.cs
[^ksarmory-viewshake]: src/KSArmory/Sim/ViewShake.cs
[^ksarmory-cloudpass]: src/KSArmory/Ksa/CloudPass.cs
[^ksarmory-cloud-comp]: src/KSArmory/Shaders/KSArmoryCloud.comp
[^ksarmory-shock-comp]: src/KSArmory/Shaders/KSArmoryShock.comp
[^ksarmory-nuclearclouds]: src/KSArmory/Ksa/NuclearClouds.cs
[^ksarmory-burstsound]: src/KSArmory/Ksa/BurstSound.cs
[^ksarmory-bursthearing]: src/KSArmory/Sim/BurstHearing.cs
[^ksarmory-blastaltitude]: src/KSArmory/Sim/BlastAltitude.cs
[^ksarmory-thermalaltitude]: src/KSArmory/Sim/ThermalAltitude.cs
[^ksarmory-ksaworld]: src/KSArmory/Ksa/KsaWorld.cs
[^ksarmory-mod]: src/KSArmory/Ksa/KSArmoryMod.cs
[^ksarmory-prerenderhook]: src/KSArmory/Ksa/PreRenderHook.cs
[^ksarmory-shadertunables]: src/KSArmory/Sim/ShaderTunables.cs
[^ksarmory-flashglare-tests]: tests/KSArmory.Tests/FlashGlareTests.cs
[^ksarmory-viewshake-tests]: tests/KSArmory.Tests/ViewShakeTests.cs
[^ksarmory-nuclear-next]: docs/NUCLEAR-NEXT.md
[^ksarmory-nuclear-altitude]: docs/NUCLEAR-ALTITUDE.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-igameviewport]: KSA.IGameViewport (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^ksa-camera]: KSA.Camera (decompiled, build 2026.10.10.5554)
[^ksa-celestial]: KSA.Celestial (decompiled, build 2026.10.10.5554)
[^ksa-screenshotcapture]: KSA.ScreenshotCapture (decompiled, build 2026.10.10.5554)
[^ksa-input]: KSA.Input (decompiled, build 2026.10.10.5554)
[^ksa-sunbloom]: KSA.SunbloomRenderer (decompiled, build 2026.10.10.5554)
[^ksa-cpw]: KSA.Rendering.ComputePipelineWrapper (decompiled, build 2026.10.10.5554)
[^ksa-gpuregion]: KSA.GpuRegionExtensions (decompiled, build 2026.10.10.5554)
