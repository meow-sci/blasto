---
type: How-To
title: Give a compute pass the camera pose without losing precision, or the wrong camera
description: Subtract the camera's double-precision ecliptic position before narrowing to float, project with the camera's row-vector matrices, and read the right camera when the player is in map mode or looking through another viewport
category: shaders
group: compute-effects
topic: pack-parameters-for-a-compute-effect
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony, vulkan]
demonstrated_by: [mods/ksarmory/features/shock-front-refraction, mods/ksarmory/features/burst-flash-and-view-shake]
techniques: [vulkan-pipeline, glsl-shader]
hooks: [harmony-prefix KSA.SunbloomRenderer.Render]
lifecycle: [render:bloom, frame:viewports, frame:draw-ui]
game_types:
  - KSA.Camera
  - KSA.ViewProjection
  - KSA.IGameViewport
  - KSA.ViewportRegistry
  - KSA.GameViewport
  - KSA.Program
  - KSA.GlobalShaderBindings
tags: [camera, precision, camera-relative, row-vector, map-mode, base-camera, push-constants, ksarmory]
---
A mod shader that draws something at a place in the solar system has two problems before it draws anything. The first is numerical: KSA's world is in doubles at planetary scale, and a 32-bit float cannot place a point to a metre at that magnitude. The second is which camera to ask: in map mode the "main camera" is a map camera millions of metres out, and a secondary viewport has a camera of its own. KSArmory's shock-front refraction and flash/shake passes show the working answers, covered here as the sub-step "give a mod shader the camera pose and the game's global data".

## Background: how the game does it

- **The Ego frame is a translation.** KSA's "Ego" coordinates are the ecliptic offset by the camera's position, so `ego = ecl - camera.PositionEcl` is exact when done in double ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L110-L118)). The mod's shader comment states the consequence: the burst arrives already differenced against the camera and the camera sits at the origin in the shader ([src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L31-L34)).
- **Matrices.** `Camera.MVP` and `Camera.VPInv` are `ViewProjection` structs holding a `viewProjection` matrix; `EgoToClipDouble(double3)` applies the row-vector view-projection in double ([current/decomp/KSA/Camera.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Camera.cs#L277-L280)). They are rebuilt by `Camera.OnFrame` during `frame:viewports`, so by `render:bloom` they are this frame's.
- **Which camera.** `GameViewport.GetCamera()` returns `MapCamera` in map mode and `BaseCamera` otherwise ([current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L297-L300)). `Program.GetMainCamera()` is `MainViewport.GetCamera()` and `Program.GetRenderCamera()` is `RenderedViewport.GetCamera()` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L631-L644)). `IGameViewport.BaseCamera` is the flight camera even while the viewport shows the map.
- **Global data.** The shader also gets the game's global set 0 for free (camera frame counter, planet position and radius, sun position and colour); that is `GlobalShaderBindings`, bound automatically by `ComputePipelineWrapper.BindPipeline`. Push only what is specific to your effect.

## Lifecycle and timing

Camera matrices are final after `Camera.OnFrame` in `frame:viewports`. KSArmory builds its push constants in the prefix on `SunbloomRenderer.Render` (`render:bloom`), which runs once per rendered viewport, so `Program.GetRenderCamera()` there is the camera of the viewport being recorded and each camera window gets its own projection. Anything about the player's eye (is a burst in front of me, how bright is it) is computed earlier, in the simulation step (`frame:draw-ui`, or `frame:celestials` with the HUD hidden), from the main viewport's base camera. See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Difference in double, then narrow.** Never convert an ecliptic position to float. Subtract the camera's `PositionEcl` while both are `double3`, test the result for finiteness, and only then cast to `float` for the push block:

   ```csharp
   // src/KSArmory/Ksa/CloudPass.cs L925
               double3 c = burstEcl - camera.PositionEcl;
   ```

   ```csharp
   // src/KSArmory/Ksa/CloudPass.cs L943-L944
                   InvViewProj = camera.VPInv.viewProjection,
                   CentreRadius = new float4((float)c.X, (float)c.Y, (float)c.Z, (float)front),
   ```

   Sources: [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L925), [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L943-L944). Within about a thousand kilometres of the camera a float still resolves centimetres, which is why the narrowing comes last.

2. **Project with the camera's row-vector convention.** KSA matrices multiply row vectors on the left, so the clip coordinates of `c` are `c.X * M1n + c.Y * M2n + c.Z * M3n + M4n`. Unpack the camera's `viewProjection` into a `double4x4` once and compute in double:

   ```csharp
   // src/KSArmory/Ksa/CloudPass.cs L914-L915
           float4x4 viewProjection = camera.MVP.viewProjection;
           double4x4 vp = double4x4.Unpack(in viewProjection);
   ```

   ```csharp
   // src/KSArmory/Ksa/CloudPass.cs L933-L939
               double w = (c.X * vp.M14) + (c.Y * vp.M24) + (c.Z * vp.M34) + vp.M44;
               if (!(w > 0.0)) continue;

               // Row-vector, as the camera's own projection is.
               double x = ((c.X * vp.M11) + (c.Y * vp.M21) + (c.Z * vp.M31) + vp.M41) / w;
               double y = ((c.X * vp.M12) + (c.Y * vp.M22) + (c.Z * vp.M32) + vp.M42) / w;
               if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
   ```

   Sources: [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L914-L915), [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L933-L939). For a quick projection of an ego-space point there is also `Camera.EgoToClipDouble`, which the mod's tile and hole rectangles use. `w <= 0` means the point is behind the camera: drop it or, as the ring pass does, widen to the whole screen.

3. **Push the inverse so the shader can rebuild rays.** `camera.VPInv.viewProjection` takes clip space back to camera-relative world, which is why the shader needs no camera position: the depth buffer plus this matrix gives metres from the eye.

   ```glsl
   // src/KSArmory/Shaders/KSArmoryCloud.comp L546-L551
   vec3 WorldAt(vec2 uv, float deviceZ)
   {
       vec4 clip = vec4(uv * 2.0 - 1.0, deviceZ, 1.0);
       vec4 p = pc.InvViewProj * clip;
       return p.xyz / p.w;
   }
   ```

   Source: [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L546-L551). Remember KSA's depth is reverse-Z: device depth 1 is the near plane, 0 the far plane.

4. **Carry motion into any history, with the same convention.** Anything reprojected from last frame must apply last frame's matrix to a point moved by how far the effect's centre has shifted against the camera. In row-vector form translating first just adds the shift times the first three rows into the fourth:

   ```csharp
   // src/KSArmory/Ksa/CloudPass.cs L866-L876
       private static float4x4 Reprojection(float4x4 lastViewProjection, double3 shift)
       {
           double4x4 m = double4x4.Unpack(in lastViewProjection);

           m.M41 += (shift.X * m.M11) + (shift.Y * m.M21) + (shift.Z * m.M31);
           m.M42 += (shift.X * m.M12) + (shift.Y * m.M22) + (shift.Z * m.M32);
           m.M43 += (shift.X * m.M13) + (shift.Y * m.M23) + (shift.Z * m.M33);
           m.M44 += (shift.X * m.M14) + (shift.Y * m.M24) + (shift.Z * m.M34);

           return float4x4.Pack(in m);
       }
   ```

   Source: [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L866-L876). The reason is physical: a burst anchored to the planet rides it at about 30 km/s, so the camera-relative position changes every frame even when nothing in the view moved.

5. **Choose the camera deliberately.** Three answers exist, and they differ:
   - For drawing in the prefix, use the camera of the viewport being recorded (`Program.GetRenderCamera()`, or the viewport passed to `Render`). Every camera window gets its own result.
   - For "where is the player's eye in the world" (is a burst in front of me, sound arrival, a measuring camera), use the main viewport's `BaseCamera`, not `GetMainCamera()`. In map mode `GetMainCamera()` returns the map camera, which sits millions of metres from the vehicle. The mod wraps this once:

   ```csharp
   // src/KSArmory/Ksa/KsaWorld.cs L4708-L4734
       /// <summary>
       /// Where the main view's scene camera is and what it is looking along, both this frame.
       ///
       /// <para>The base camera, not <c>GetMainCamera()</c>: that answers with the <em>map</em> camera
       /// in map mode, which sits millions of metres out. The base camera is the one Fixed mode drives
       /// and therefore the one being taken over, and in map mode it still holds the last pose the
       /// player had in the world.</para>
       /// </summary>
       public static bool TryMainCameraPose(out double3 positionEcl, out double3 forwardEcl)
       {
           positionEcl = forwardEcl = Vec.Zero;

           try
           {
               if (Program.MainViewport?.BaseCamera is not { } camera) return false;

               positionEcl = camera.EgoToEcl(Vec.Zero);
               forwardEcl = camera.GetForwardEcl();

               return Vec.IsFinite(positionEcl) && Vec.Len2(forwardEcl) > 1e-12;
           }
           catch (Exception e)
           {
               Log.Warn($"could not read the main camera pose: {e.Message}");
               return false;
           }
       }
   ```

   Source: [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L4708-L4734). The flash code calls it each step to decide whether a burst is in frame, with half the field of view as the cone ([src/KSArmory/Ksa/BurstFlash.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/BurstFlash.cs#L91-L100)).

6. **Mind the one effect that is not per-viewport.** The mod's flash and shake are computed once, for the main view's base camera, but the whiteout and shake dispatches do not filter by viewport, so camera windows recorded through the same pass white out and shake with the main view. If your effect depends on the eye, either compute it per recorded camera or accept that.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs) | `Shock` (L903-L990): camera-relative centre, row-vector projection, `ShockPush`; `Reprojection`; tile and hole rectangles through `EgoToClipDouble` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs) | `TryMainCameraPose` |
| [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp) | Camera-relative convention, `WorldAt` |

The refraction itself is in [Refract and distort the rendered scene, and flash or shake it](../../screen-effects/refract-and-distort-the-scene/overview.md); the screen-space use of the same camera for tile gathers is in [Reconstruct scene positions from the depth buffer and paint on them](../reconstruct-scene-positions-from-depth-in-a-compute-shader/overview.md).

## Variations and gotchas

- **Using the render camera for the eye is wrong in a camera window.** A secondary viewport's `GetCamera()` is a different camera; an effect keyed to "the player's view" should read the main viewport's base camera.
- **The base camera still holds the last in-world pose in map mode.** That is why `TryMainCameraPose` is stable in map mode; it also means an effect keyed to it will continue to react to the world while the player looks at the map.
- **Float casts hide in helpers.** Any intermediate `float3` or `float4x4.Pack` before the subtraction reintroduces the error; keep ecliptic vectors `double3` until the camera has been subtracted.
- **Behind the camera.** `w <= 0` gives an inverted, mirrored projection; test `w` before dividing.
- **Camera windows.** Only the main view gets weather clouds; the pass draws other viewports with the "no weather" flag. See [Raymarch a local volume or analytic sky effect into the HDR scene](../raymarch-a-local-volume-into-the-hdr-scene/overview.md).
- **Related tooling.** To project world positions to screen for ImGui overlays instead of shader work, see [Project world and sky positions to screen pixels and point at off-screen targets](../../../rendering/overlays/project-world-positions-to-screen/overview.md); for choosing a render frame to anchor effects in, [Choose a render frame and anchor effects to bodies, parts and terrain](../../../rendering/overlays/choose-a-render-frame-and-anchor-effects/overview.md).

## See also

- Siblings: [128-byte push block](pack-parameters-for-a-compute-effect-1.md), [one pipeline, many kinds](pack-parameters-for-a-compute-effect-2.md), [storage buffer of segments](pack-parameters-for-a-compute-effect-3.md), [overview](overview.md)
- [The blast front drawn as a bend in the light, by copying the scene and writing it back at an offset](../../../../mods/ksarmory/features/shock-front-refraction.md), [A burst's whiteout and a blast front's view shake, painted into the scene image instead of moving the camera](../../../../mods/ksarmory/features/burst-flash-and-view-shake.md), [A mod compute pass recorded into KSA's frame just before bloom](../../../../mods/ksarmory/features/compute-pass-before-bloom.md)
- [Send per-frame values and time to a shader with push constants, written at the right point in the frame](../../post-processing/send-push-constants-to-a-shaderextensions-shader/overview.md), [Refract and distort the rendered scene, and flash or shake it](../../screen-effects/refract-and-distort-the-scene/overview.md)
