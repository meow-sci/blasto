---
type: How-To
title: "Flash and shake the view: a flash-only mode of a compute shader and a shock-pass shake on the sim clock"
description: Whiteout the scene and shake the picture from compute passes before bloom, driven by a game-free model on the simulation clock, without writing to the camera and surviving the hidden UI
category: shaders
group: screen-effects
topic: refract-and-distort-the-scene
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony, vulkan]
demonstrated_by: [mods/ksarmory/features/burst-flash-and-view-shake]
techniques: [glsl-shader, vulkan-pipeline, harmony-prefix]
hooks:
  - harmony-prefix KSA.SunbloomRenderer.Render
  - starmap StarMapAfterGui
  - harmony-prefix KSA.Program.OnFrameCelestials
lifecycle: [render:bloom, frame:draw-ui, frame:celestials, frame:after-on-frame]
game_types:
  - KSA.SunbloomRenderer
  - KSA.Rendering.ComputePipelineWrapper
  - KSA.ScreenshotCapture
  - KSA.Program
  - KSA.IGameViewport
tags: [whiteout, flash, view-shake, eye-adaptation, compute, hdr, ksarmory, screenshot]
---
A close nuclear burst in KSArmory first whites the screen out (violet at first, warming as the fireball cools) and, when the blast front reaches the camera, jolts and rattles the picture. Both are split the same way: a game-free model on the mod's simulated step decides how much, and compute passes write it into the HDR scene before bloom. This article shows both halves and why neither touches the camera and neither uses ImGui. The technique
generalises to any full-screen flash (a flashbang, a lightning strike) or shake (an impact, an engine rumble). You need the compute-pass plumbing from [Refract and distort the rendered scene, and flash or shake it](overview.md) article 2.

## Background: how the game does it

- **No camera member is written.** The shake moves the rendered picture (translate, roll and a slight zoom) in the shock pass rather than turning the camera, so it needs no hold on a view something else may be driving and the HUD stays still over it.
- **The flash is not an ImGui overlay.** `Program.OnFrame` wraps the UI pass in `if (DrawUI)` and both F2 and `ScreenshotCapture` clear that flag ([current/decomp/KSA/ScreenshotCapture.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ScreenshotCapture.cs#L225-L233)); drawn as UI it vanished from every screenshot the author's harness took. Drawn into the scene image it is part of every capture.
- **The eye is the base camera.** `Program.MainViewport.BaseCamera` gives the scene camera even in map mode (`GetMainCamera()` returns the map camera there), so the mod reads position and forward from it.

## Lifecycle and timing

| Moment | What happens | Value |
|---|---|---|
| HUD shown | The mod's step runs from `[StarMapAfterGui]` and updates the flash and shake models with the applied sim step | `frame:draw-ui` |
| HUD hidden (F2, a screenshot) | The same step runs from a prefix on `Program.OnFrameCelestials`, still before the render | `frame:celestials` |
| Fallback | `[StarMapAfterOnFrame]` if the prefix could not be installed; the values then reach the screen a frame late | `frame:after-on-frame` |
| Recording | The shock pass (with the shake) after the clouds' resolve, then tracers, then the flash last, from the `SunbloomRenderer.Render` prefix | `render:bloom` |

Because the values are on the simulated step, both freeze while paused: `BurstFlash.Update` skips its decay when `dt` is 0, and `ViewShake.At` is a pure function of time since arrival. See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Model the whiteout by the rise of the brightest level, not the level.** An eye adapts while the source is still there, so what blinds is the change. Held at the level, the ball kept the view at full white for the whole 1.9 s burn and then began a two-and-a-half-second decay; driving by the rise and always recovering fixes it. Add the rises (capped), decay with `exp(-dt / fade)`, keep the violet of
   what blinded:

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

   Source: [src/KSArmory/Ksa/BurstFlash.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BurstFlash.cs#L158-L189). The brightness at the eye is logarithmic: `FlashGlare.Level` is `log10(1 + suns / adapted) / 1.5` clamped to [0, 1], with adaptation from 1 sun by day to 0.01 at night over -8 to +6 degrees of sun elevation, a 0.12 off-axis floor and a recovery time constant of 0.28 s by day to 2.2 s at night.

2. **Draw the flash as a mode of an existing compute pipeline.** The flash is one full-screen dispatch, last in the pass, selected by push-constant values that cannot occur on a cloud dispatch (`CentreRadius.w <= 0` and `FireSun.w <= 0`), so no new pipeline is bound. The shader mixes the scene toward the glare colour by the whiteout and adds a halo shaped by the angle to the ball, with the ball's visibility
   tested against the depth buffer:

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

   Source: [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L1746-L1796). The glare constants are Vulkan specialization constants, live-tunable. The mod's calibration comment: values written as 0.25 come out of KSA's post chain at mid grey, 1.0 at 240 and 1.25 and above clip to white; a change to KSA's exposure or tonemap changes how white it reads.

3. **Model the shake as a pure function of time since arrival.** Strength `min(1, sqrt(p / 34,474 Pa))`, duration `min(0.4 + 1.5 x positive phase, 3)` s, an envelope with a 0.03 s attack and `exp(-3t/T)` decay, and a seeded sum of sines (7 to 12 Hz) per axis with roll half the translation, all in shares of the screen height:

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

   Source: [src/KSArmory/Sim/ViewShake.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ViewShake.cs#L50-L63). The shake is struck by the sound code when the front reaches the camera, so the bang and the jolt are one arrival.

4. **Apply the shake as a UV transform in the pass that already copies the scene.** Rotate the aspect-corrected UV about the screen centre by the roll, divide by `1 + 2(|x| + |y| + |roll|)` (zooms in just enough that the edge the move uncovers stays off screen), offset by `(x, y)` and read the copy there:

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

   Source: [src/KSArmory/Shaders/KSArmoryShock.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryShock.comp#L105-L125). The first front's push carries `Shake = (x, y, roll, 1)`; with no front to draw but a shake running, one push with zero bend is dispatched only to move the picture.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/BurstFlash.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BurstFlash.cs), [src/KSArmory/Sim/FlashGlare.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/FlashGlare.cs) | Flash model |
| [src/KSArmory/Sim/ViewShake.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/ViewShake.cs), [src/KSArmory/Ksa/BlastShake.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BlastShake.cs) | Shake model |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs) | `Flash` dispatch and the shake handed to the shock pass |
| [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp), [src/KSArmory/Shaders/KSArmoryShock.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryShock.comp) | The two shaders |

## Variations and gotchas

- **Drawn only when a cloud is marched.** The shock pass (and the shake with it) is called only on the branch of `CloudPass.Record` where at least one non-thin cloud is drawn (read from the code, not observed).
- **Main view's eye, every view's picture.** The whiteout and shake are computed once for the main view's base camera, but neither dispatch filters by viewport, so camera windows recorded through the same pass white out and shake with the main view.
- **What is not shaken.** Tracers and the whiteout are recorded after the shock pass, and KSA's post-bloom draws (orbit lines, gizmos, grid, gauges) and the ImGui HUD come later, so those stay still while the scene moves.
- **Everything is behind the burst-visual gates;** turning the feature off clears the model state each step.
- **Rise, not level.** The author flew the level-driven version and found it still near white at 3.8 s.

## See also

- Siblings: [normal-mask refraction](refract-and-distort-the-scene-1.md), [compute shock ring](refract-and-distort-the-scene-2.md), [overview](overview.md)
- [A burst's whiteout and a blast front's view shake, painted into the scene image instead of moving the camera](../../../../mods/ksarmory/features/burst-flash-and-view-shake.md), [The blast front drawn as a bend in the light, by copying the scene and writing it back at an offset](../../../../mods/ksarmory/features/shock-front-refraction.md)
- [Paint into or rescale the HDR scene with a compute pass shipped as a Shader asset](../../../rendering/vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/overview.md), [Capture rendered frames from KSA](../../../rendering/vulkan-passes/capture-rendered-frames/overview.md)
