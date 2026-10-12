---
type: How-To
title: "Refract the scene: a scene copy then a displaced write-back in two compute dispatches"
description: Draw a shock ring that bends what is behind it by copying the HDR scene and writing each pixel back from the copy at an offset given by an analytic lens, with a reverse-Z depth test and a ground cut
category: shaders
group: screen-effects
topic: refract-and-distort-the-scene
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony, vulkan]
demonstrated_by: [mods/ksarmory/features/shock-front-refraction]
techniques: [glsl-shader, vulkan-pipeline, harmony-prefix]
hooks: [harmony-prefix KSA.SunbloomRenderer.Render]
lifecycle: [render:bloom, frame:draw-ui, frame:viewports]
game_types:
  - KSA.SunbloomRenderer
  - KSA.Rendering.ComputePipelineWrapper
  - KSA.Rendering.RenderImage
  - RenderCore.ReverseDepthBufferUtils
  - KSA.Camera
  - KSA.IGameViewport
tags: [refraction, shock-wave, lens, compute, scene-copy, reverse-z, bilinear, ksarmory]
---
KSArmory draws a nuclear blast front as a ring racing out from the burst that **bends whatever is behind it and hides nothing**, the way high-speed film shows it. For every front strong enough to see, the mod copies KSA's HDR scene image and writes each pixel back from the copy at a small screen offset, only where the view ray grazes the front's spherical shell. The offset follows the derivative of a Gaussian across the shell,
which is how a density step refracts light. This article is the recipe for such a copy-and-displace pass. Plumbing (the prefix, the barriers, the pipeline) is in [Colour-grade the frame and add an aspect-correct vignette](../colour-grade-the-frame-and-add-a-vignette/overview.md) article 2 and [Paint into or rescale the HDR scene with a compute pass shipped as a Shader asset](../../../rendering/vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/overview.md).

## Background: how the game does it

- **The hook.** `SunbloomRenderer.Render(CommandBuffer, IViewport, int)` is called once per rendered viewport before bloom and the tonemap; recorded there, the displaced pixels are HDR and bloom like the rest of the frame.
- **Matrices.** `Camera.MVP` and `VPInv` are rebuilt in `Camera.OnFrame` (`frame:viewports`); KSA uses row vectors, so project a point with the camera's row-vector convention. Positions are differenced against `camera.PositionEcl` in double and only then narrowed to float; see [Pack parameters and camera data for a compute effect](../../compute-effects/pack-parameters-for-a-compute-effect/overview.md).
- **Depth.** The projection is reverse-Z: device depth 1 is the near plane, 0 is the sky.
- **A storage-only image cannot be sampled,** so the shader filters the copy bilinearly with four `imageLoad`s.

## Lifecycle and timing

| Moment | What happens | Value |
|---|---|---|
| The cloud ages on the simulated step | `NuclearClouds.Update` from `[StarMapAfterGui]` (HUD shown) or a prefix on `OnFrameCelestials` (HUD hidden) | `frame:draw-ui` / `frame:celestials` |
| Matrices settled | `Camera.OnFrame` | `frame:viewports` |
| Copy and bend per front | `CloudPass.Record` from the prefix on `SunbloomRenderer.Render`, after the cloud resolve and before tracers and the whiteout | `render:bloom` |

Order matters: after the clouds so the front bends the cloud behind it, before tracers and the whiteout (which are not bent), before bloom and tonemap. See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Build one push-constant struct per front on the CPU.** Skip fronts that are too faint (`strength <= 0.03`), that the camera is inside of (no ray grazes the shell from inside), or whose centre is behind the camera. Project the burst with the camera's row-vector view-projection in double; pack the up vector's length so its excess over one carries the burst height over the ground:

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

   Source: [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L917-L952). The bend is deliberately exaggerated (`ShockBend` 4.5, about fifteen times the physical deflection; the author measured that the physical strength produced no visible difference).

2. **Record two dispatches per front.** A compute hazard, the scene-copy image to `StorageReadWriteC` through `BarrierBatch`, a copy dispatch (mode 0), another hazard, a bend dispatch (mode 1). Fronts are drawn one after the other, each copying what the previous bend left, so where two fronts cross their bends add:

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

   Source: [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L965-L987). `SceneCopy` is an `R16G16B16A16SFloat` `RenderImage.CreateColorStorage`, one per viewport `ShaderSlot`; the pipeline is built over `[scene colour, scene copy]` storage plus the depth.

3. **Compute the ring's lens in the shader.** For each pixel build the view ray (device depth 1 is the near plane), find where it passes nearest the burst, test whether it grazes the shell within three widths, respect the ground and the scene depth, and offset radially by the derivative of a Gaussian:

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

   Source: [src/KSArmory/Shaders/KSArmoryShock.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryShock.comp#L61-L90). The write is `CopyAt(from + bend)`: a manual bilinear `imageLoad` of the copy, alpha kept.

4. **Where the radius comes from.** The mod's front radius follows a Sedov-Taylor law in the burst's air until the front slows to Mach 1.2, then the speed of sound plus an eased excess; the details are specific to the mod ([src/KSArmory/Sim/MushroomCloud.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/MushroomCloud.cs#L585-L658)). For a generic ring effect, supply any radius as a function of age.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs) | `ShockPush`, front list, culling, the two dispatches |
| [src/KSArmory/Shaders/KSArmoryShock.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryShock.comp) | Copy mode, `RingBend`, manual bilinear read |

## Variations and gotchas

- **No clouds to march, no fronts.** `Shock` is only called on the path of `CloudPass.Record` where at least one non-thin cloud is drawn.
- **A burst behind the camera draws no ring,** because the offset direction is radial from the burst's screen position.
- **Cost.** Two full-screen passes per front (not a tile); the author measured 0.07 ms for one 20 kt front and 0.14 to 0.19 ms for three at 1440x900.
- **Strength uses the raw charge** while the radius uses the efficiency-scaled one, so a high burst's weaker front is drawn as strong as a low one's at the same radius.
- **Rebuild on size change,** not only on image change, because `RenderTarget.Rebuild` rebuilds its images in place.

## See also

- Siblings: [normal-mask refraction](refract-and-distort-the-scene-1.md), [flash and shake](refract-and-distort-the-scene-3.md), [overview](overview.md)
- [The blast front drawn as a bend in the light, by copying the scene and writing it back at an offset](../../../../mods/ksarmory/features/shock-front-refraction.md), [A mod compute pass recorded into KSA's frame just before bloom](../../../../mods/ksarmory/features/compute-pass-before-bloom.md)
- [Pack parameters and camera data for a compute effect](../../compute-effects/pack-parameters-for-a-compute-effect/overview.md), [Raymarch a local volume or analytic sky effect into the HDR scene](../../compute-effects/raymarch-a-local-volume-into-the-hdr-scene/overview.md)
