---
type: How-To
title: "Pack a compute effect into 128 bytes: octahedral vectors and an exact flag sum"
description: Fit a whole effect's state into one 128-byte push-constant block by deriving what the shader can derive, packing unit vectors into two floats and summing small integers into one exact float
category: shaders
group: compute-effects
topic: pack-parameters-for-a-compute-effect
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony, vulkan]
demonstrated_by: [mods/ksarmory/features/raymarched-mushroom-cloud]
techniques: [vulkan-pipeline, glsl-shader]
hooks: [harmony-prefix KSA.SunbloomRenderer.Render]
lifecycle: [render:bloom]
game_types: [KSA.Rendering.ComputePipelineWrapper, KSA.Camera, KSA.ViewProjection]
tags: [push-constants, octahedral, flags, packing, compute, ksarmory]
---
KSArmory draws a mushroom cloud with one full-screen compute dispatch per cloud, and everything that dispatch needs travels in a single push-constant block of exactly 128 bytes: one `float4x4` and four `float4`. This article shows how that budget is met, so you can do the same for any per-dispatch effect: derive values in the shader, pack unit vectors into two floats, sum small integers into one float exactly, and keep the constants the shader must hard-code honest with a text test. It uses [A raymarched mushroom cloud, held body-fixed and drawn before bloom](../../../../mods/ksarmory/features/raymarched-mushroom-cloud.md) as the worked example and assumes the compute pass is already recorded as described in [Paint into or rescale the HDR scene with a compute pass shipped as a Shader asset](../../../rendering/vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/overview.md).

## Background: how the game does it

The game offers no helper for packing. `ComputePipelineWrapper.BindPipeline<T>` pushes whatever unmanaged struct you give it at offset zero ([current/decomp/KSA.Rendering/ComputePipelineWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ComputePipelineWrapper.cs#L210-L213)), against a `VkPushConstantRange` you declared when you built the pipeline. Vulkan guarantees 128 bytes everywhere, and the mod's own comment records that KSA's volumetric shader takes four `float4` already, so it found no evidence of headroom to borrow ([src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L200-L203)).

The game also hands the shader a lot for free through the global set 0: `global.camera.frame`, `global.lighting.planetPosition` and `sunPosition`. Anything in that block is a value you do not need to push (see [Include KSA's own GLSL library from a mod shader](../../glsl-workflow/include-the-game-shader-library-in-mod-glsl/overview.md) for pulling the game's GLSL in).

## Lifecycle and timing

The block is built on the CPU inside the prefix on `SunbloomRenderer.Render` (`render:bloom`), once per dispatch, from state the mod updated earlier in the frame (`frame:draw-ui`, or `frame:celestials` with the HUD hidden). Building at record time matters because positions are differenced against that viewport's camera, which is not known any earlier. Matrices are settled by `Camera.OnFrame` (`frame:viewports`). See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Declare the block twice, byte for byte.** The C# struct is sequential with `Pack = 4` and one `float4x4` followed by `float4` fields; the GLSL `push_constant` block lists the same members in the same order:

   ```csharp
   // src/KSArmory/Ksa/CloudPass.cs L190-L198
       [StructLayout(LayoutKind.Sequential, Pack = 4)]
       private struct Push
       {
           public float4x4 InvViewProj;
           public float4 AgeStrengthWind;   // age, strength, and the downwind packed into two floats
           public float4 CentreRadius;
           public float4 FireSun;       // fireball radius and glow, the whiteout, the scorch
           public float4 Shape;        // cap centre, cap radius, cap tube, stem radius
       }
   ```

   ```glsl
   // src/KSArmory/Shaders/KSArmoryCloud.comp L63-L71
   layout (push_constant) uniform PushConstants
   {
       mat4 InvViewProj;      // clip -> camera-relative world
       vec4 AgeStrengthWind;  // x seconds since the burst, y strength (0 draws nothing), zw downwind
       vec4 CentreRadius;     // xyz burst, camera-relative metres; w the sphere that bounds it all
       vec4 FireSun;          // fireball radius, its glow, the heat left in the cloud (the whiteout
                              // on the flash's own dispatch), flags
       vec4 Shape;            // cap centre height, cap radius, cap tube, stem radius
   } pc;
   ```

   Sources: [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L190-L198) and [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L63-L71). 4 x 4 x 4 = 64 bytes of matrix plus 4 x 16 = 64 bytes of vectors is exactly 128. Declare the range with `Size = Marshal.SizeOf<Push>()` and `StageFlags = ComputeBit`.

2. **Do not send what the shader can derive.** The target size is `imageSize(sceneColor)`; the cloud's up direction comes from the burst position and `global.lighting.planetPosition`; the sun direction from `global.lighting.sunPosition`; the frame clock for jitter from `global.camera.frame`. Each of those is a `vec3` or `int` you did not have to find room for.

3. **Pack unit vectors into two floats.** An octahedral encoding stores a unit vector in two floats, so "downwind" costs the `zw` of `AgeStrengthWind` instead of three slots. The C# side:

   ```csharp
   // src/KSArmory/Ksa/CloudPass.cs L203-L220
       private static float2 OctahedralPack(double3 unit)
       {
           double sum = Math.Abs(unit.X) + Math.Abs(unit.Y) + Math.Abs(unit.Z);
           if (!(sum > 0.0)) return new float2(0f, 0f);

           double x = unit.X / sum;
           double y = unit.Y / sum;

           if (unit.Z < 0.0)
           {
               double fx = (1.0 - Math.Abs(y)) * (x >= 0.0 ? 1.0 : -1.0);
               double fy = (1.0 - Math.Abs(x)) * (y >= 0.0 ? 1.0 : -1.0);
               x = fx;
               y = fy;
           }

           return new float2((float)x, (float)y);
       }
   ```

   and the GLSL side, which undoes it:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryCloud.comp L75-L80
   vec3 OctahedralUnpack(vec2 o)
   {
       vec3 n = vec3(o.x, o.y, 1.0 - abs(o.x) - abs(o.y));
       if (n.z < 0.0) n.xy = (1.0 - abs(n.yx)) * vec2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
       return normalize(n);
   }
   ```

   Sources: [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L203-L220), [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L75-L80). The input must be unit length; the result loses a few bits of angle, which a wind direction does not miss.

4. **Sum small integers into one float, exactly.** Flags and coarse quantities go into a single float as an integer sum, negated so that a sign can say which kind of dispatch this is. Keep every term below 2^24 so a 32-bit float holds the integer exactly:

   ```csharp
   // src/KSArmory/Sim/CloudFlags.cs L23-L30
       public static float Pack(bool water, bool weather, bool first, double shockMetres, double dryness = 0.0)
       {
           double shock = Math.Clamp(Math.Round(shockMetres), 0.0, MostShockMetres);
           double dry = Math.Round(Math.Clamp(double.IsFinite(dryness) ? dryness : 0.0, 0.0, 1.0) * DrySteps);

           return -(float)((water ? 1.0 : 0.0) + (weather ? 0.0 : 2.0) + (first ? 4.0 : 0.0) + (8.0 * dry)
                           + (64.0 * shock));
       }
   ```

   Bits 1, 2, 4 are flags, 8 x dryness in sevenths takes the next three bits, and 64 x the front radius in whole metres sits above them, up to 262,143 m. The shader reads it back with only `mod`, `floor` and divisions by powers of two, which are exact on integers:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryCloud.comp L1813-L1820
       float cloudFlags = wantsCloud ? -pc.FireSun.w : 0.0;
       float flagBits = mod(cloudFlags, 64.0);
       float shockRadius = floor(cloudFlags / 64.0);
       cloudDryness = floor(flagBits / 8.0) / 7.0;
       flagBits = mod(flagBits, 8.0);
       bool columnIsWater = mod(flagBits, 2.0) > 0.5;
       bool weatherBound = wantsCloud ? (mod(floor(flagBits / 2.0), 2.0) < 0.5) : (pc.Shape.y > 0.5);
       bool firstCloud = flagBits > 3.5;
   ```

   Sources: [src/KSArmory/Sim/CloudFlags.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/CloudFlags.cs#L23-L30), [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L1813-L1820).

5. **Use the sign as a discriminator.** In this shader a positive `FireSun.w` means "ground mark" and a positive `CentreRadius.w` means "cloud", so the flags are negated and one pipeline serves every dispatch kind. Adding a mode costs a sign convention, not a field. [The next article](pack-parameters-for-a-compute-effect-2.md) generalises this.

6. **Pack fractional parts too.** Two quantities can share one float when one is an integer and the other a fraction, and three can when levels are small. `FireSun.z` carries the core heat in the fraction and the ground coupling and stem share as two numbers of 63 levels each in the integer part (stepped by two):

   ```csharp
   // src/KSArmory/Sim/MushroomCloud.cs L287-L293
       public static float PackHeat(double heat, double coupling, double stemShare)
       {
           double h = Math.Clamp(double.IsFinite(heat) ? heat : 0.0, 0.0, 0.99);
           double c = Math.Round(Math.Clamp(double.IsFinite(coupling) ? coupling : 1.0, 0.0, 1.0) * PackLevels);
           double s = Math.Round(Math.Clamp(double.IsFinite(stemShare) ? stemShare : 1.0, 0.0, 1.0) * PackLevels);
           return (float)((2.0 * (c + ((PackLevels + 1.0) * s))) + h);
       }
   ```

   ```glsl
   // src/KSArmory/Shaders/KSArmoryCloud.comp L220-L226
   void UnpackHeat(float packed)
   {
       float q = floor(packed / 2.0);
       cloudHeat = packed - 2.0 * q;
       groundCoupling = mod(q, 64.0) / 63.0;
       stemShare = floor(q / 64.0) / 63.0;
   }
   ```

   Sources: [src/KSArmory/Sim/MushroomCloud.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/MushroomCloud.cs#L287-L293), [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L220-L226). `Shape.w` does the same with the stem radius in whole metres and the column-top share in the fraction (`UnpackStem`, L213-L218). The accuracy lost is the trade, so choose levels the look can bear and clamp the fraction below 1 so it cannot carry.

7. **Hold the shader's hard-coded constants to the C# with a text test.** What the block cannot carry, the shader hard-codes, and a drifted copy is silent. The mod's unit tests read the `.comp` as text and compare:

   ```csharp
   // tests/KSArmory.Tests/MushroomCloudShaderTests.cs L15-L32
       private static double Constant(string name)
       {
           string dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "KSArmory", "Shaders");
           string source = File.ReadAllText(Path.Combine(dir, "KSArmoryCloud.comp"));
           Match m = Regex.Match(source, @"const float " + name + @" = ([0-9.]+);");

           Assert.True(m.Success, $"no 'const float {name}' in the shader");
           return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
       }

       [Fact]
       public void TheShaderKnowsTheStandAsTheLawDoes()
       {
           Assert.Equal(MushroomCloud.RiseSeconds, Constant("StandBeginsSeconds"), 9);
           Assert.Equal(MushroomCloud.StandSeconds, Constant("StandLastsSeconds"), 9);
           Assert.Equal(MushroomCloud.AgedShear, Constant("AgedShear"), 9);
           Assert.Equal(MushroomCloud.BoundMargin, Constant("BoundMargin"), 9);
       }
   ```

   Source: [tests/KSArmory.Tests/MushroomCloudShaderTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/MushroomCloudShaderTests.cs#L15-L32). A companion test round-trips every flag combination through `Pack` and `Unpack` and asserts the shader still contains the same `mod(cloudFlags, 64.0)` and `floor(flagBits / 8.0) / 7.0` expressions ([tests/KSArmory.Tests/CloudFlagsTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/CloudFlagsTests.cs#L12-L45)).

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs) | `Push`, `OctahedralPack`, the per-cloud build of the block and the dispatch (L692-L759) |
| [src/KSArmory/Sim/CloudFlags.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/CloudFlags.cs) | The flag sum and a test-only inverse |
| [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp) | Push block, `OctahedralUnpack`, flag decode |
| [tests/KSArmory.Tests/MushroomCloudShaderTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/MushroomCloudShaderTests.cs) | Regex-reads shader constants and holds them to the C# |

The assembled block for one cloud is in [A raymarched mushroom cloud, held body-fixed and drawn before bloom](../../../../mods/ksarmory/features/raymarched-mushroom-cloud.md) (section "Packing a cloud into 128 bytes"): `AgeStrengthWind = (age, tint x fade, windOct.x, windOct.y)`, `CentreRadius = (centre - camera, radius)`, `FireSun = (fireball radius, glow, packed heat, negated flags)` and `Shape = (cap centre, cap radius, cap tube, packed stem)`.

## Variations and gotchas

- **The block is full.** Every addition displaces something; the mod derives the target size, up direction and sun direction in the shader rather than sending them. Prefer reading the game's global set to adding a field.
- **Exactness has a ceiling.** Integers must stay below 2^24; the front radius is clamped to 262,143 m before the sum so the float stays exact. A value past the ceiling is clamped, not wrapped.
- **Quantisation is visible only where you let it be.** Coupling and stem share are held at 64 levels each; if a quantity drives an edge, test it at the extremes.
- **Hard-coded constants drift silently.** The text test is cheap insurance; without it, a bound the shader grows differently from the C# cuts a sheared cap off at the bounding sphere.
- **Check the shader offline.** A packing bug is a decode bug; compile the GLSL with the tooling in [Compile-check a mod's GLSL without launching the game](../../glsl-workflow/check-mod-shaders-offline-with-glslang/overview.md) before blaming the push.
- **Not for ShaderExtensions passes.** A post-processing pass declares its push constant in XML and has different alignment rules; see [Send per-frame values and time to a shader with push constants, written at the right point in the frame](../../post-processing/send-push-constants-to-a-shaderextensions-shader/overview.md).

## See also

- Siblings: [one pipeline, many kinds](pack-parameters-for-a-compute-effect-2.md), [storage buffer of segments](pack-parameters-for-a-compute-effect-3.md), [camera-relative positions](pack-parameters-for-a-compute-effect-4.md), [overview](overview.md)
- [A raymarched mushroom cloud, held body-fixed and drawn before bloom](../../../../mods/ksarmory/features/raymarched-mushroom-cloud.md), [A mod compute pass recorded into KSA's frame just before bloom](../../../../mods/ksarmory/features/compute-pass-before-bloom.md)
- [Raymarch a local volume or analytic sky effect into the HDR scene](../raymarch-a-local-volume-into-the-hdr-scene/overview.md), [Reconstruct scene positions from the depth buffer and paint on them](../reconstruct-scene-positions-from-depth-in-a-compute-shader/overview.md)
