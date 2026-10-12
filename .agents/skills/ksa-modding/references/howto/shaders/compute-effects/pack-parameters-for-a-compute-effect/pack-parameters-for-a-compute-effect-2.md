---
type: How-To
title: "Run several effects through one compute pipeline: a kind tag and a per-kind dispatch cap"
description: Reuse one pipeline and one push block for different effects by marking a dispatch with a negative bound and a kind float, then keep only the newest few of each kind so the drawn set is stable
category: shaders
group: compute-effects
topic: pack-parameters-for-a-compute-effect
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony, vulkan]
demonstrated_by: [mods/ksarmory/features/high-altitude-burst-sky]
techniques: [vulkan-pipeline, glsl-shader]
hooks: [harmony-prefix KSA.SunbloomRenderer.Render]
lifecycle: [render:bloom, frame:draw-ui]
game_types: [KSA.Rendering.ComputePipelineWrapper, KSA.Camera, KSA.SunbloomRenderer]
tags: [push-constants, multiplexing, dispatch-budget, compute, sky, ksarmory]
---
KSArmory's high-altitude burst draws four different sky effects (an X-ray glow, an aurora curtain, a debris shell and a red wave) with the very pipeline, push-constant struct and bind call that draw its mushroom cloud. This article shows how one compute shader serves many effects: the push block says which one by a sign and a kind number, and the CPU keeps only the newest few of each kind so the cost is bounded and the choice does not flicker. It is the multiplexing half of [What a high or airless burst lights in the sky, chosen by the air at the burst](../../../../mods/ksarmory/features/high-altitude-burst-sky.md); the shader side of what each kind draws is in [Raymarch a local volume or analytic sky effect into the HDR scene](../raymarch-a-local-volume-into-the-hdr-scene/overview.md).

## Background: how the game does it

Nothing in the game multiplexes compute passes for you. A `ComputePipelineWrapper` is one shader module with one descriptor layout and one push range; every effect that must share a pipeline must share a push struct too ([current/decomp/KSA.Rendering/ComputePipelineWrapper.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering/ComputePipelineWrapper.cs#L210-L213)). The reasons to do so are cost of a second pipeline per viewport (the mod keeps at most two pipelines per view) and that the effects share the same global set, scene storage image and depth sampler. The first article, [128 bytes of push constants](pack-parameters-for-a-compute-effect-1.md), explains why the block is full and how it is packed; here the question is only how to give one block several meanings.

## Lifecycle and timing

Sky objects live in lists the mod updates on the simulation step (`frame:draw-ui`, or `frame:celestials` with the HUD hidden). They are turned into push blocks and dispatched inside the `SunbloomRenderer.Render` prefix (`render:bloom`), per viewport, because each viewport has its own camera and its own atmosphere LUT set. The budget is applied when the blocks are gathered, so a frame with many bursts draws a bounded number of full-screen dispatches. See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Pick the discriminator and write the contract down.** The mod uses a negative `CentreRadius.w` to mean "sky dispatch" (its magnitude is the brightness; a cloud's is a positive bounding radius), and `FireSun.z` as the kind. Names live in one small class with the contract in its doc comment:

   ```csharp
   // src/KSArmory/Sim/SkyDispatch.cs L18-L37
       public const float Glow = 0f;

       /// <summary>One end of a burst's field line (<see cref="Aurora"/>). <c>FireSun.w</c> is the sine of the foot's magnetic latitude.</summary>
       public const float Aurora = 1f;

       /// <summary>A thin-air burst's debris shell (<see cref="DebrisShell"/>). <c>FireSun.w</c> is 0.</summary>
       public const float Debris = 2f;

       /// <summary>
       /// The red wave a burst high in the air sends out (<see cref="RedWave"/>). <c>FireSun.w</c> is the
       /// altitude the red survives above.
       /// </summary>
       public const float RedWave = 3f;

       /// <summary>
       /// How many of one kind a frame draws: as many as it pays for. A glow, a shell and a wave cost
       /// 0.04-0.15 ms from orbit. Curtains are the dear kind, and four -- both ends of two bursts' field
       /// lines -- cost 2.2 ms from orbit, what two did, since one off the screen costs next to nothing.
       /// </summary>
       public static int MostOf(float kind) => kind == Aurora ? 4 : kind == RedWave ? 2 : 3;
   ```

   Source: [src/KSArmory/Sim/SkyDispatch.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SkyDispatch.cs#L18-L37). Each kind owns the meaning of `FireSun.w`; write that next to the constant, because the shader and the packer must agree.

2. **Fill the same struct for each kind.** The aurora's block shows the pattern: `InvViewProj` and a camera-relative centre as usual, the negative brightness in `CentreRadius.w`, the kind and two altitudes in `FireSun`, and kind-specific data in `Shape`:

   ```csharp
   // src/KSArmory/Ksa/CloudPass.cs L583-L602
               for (int i = 0; i < NuclearClouds.AuroraCount; i++)
               {
                   if (!NuclearClouds.TryAurora(i, out double3 footEcl, out double3 eastEcl, out double sinLatitude,
                                                out double nits, out double age, out object? over)) continue;
                   if (!ReferenceEquals(over, camera.NearbyCelestial)) continue;

                   double3 foot = footEcl - camera.PositionEcl;
                   if (!Vec.IsFinite(foot)) continue;

                   _sky.Add((SkyDispatch.Aurora, nits, new Push
                   {
                       InvViewProj = camera.VPInv.viewProjection,
                       CentreRadius = new float4((float)foot.X, (float)foot.Y, (float)foot.Z, -(float)nits),
                       FireSun = new float4((float)Aurora.BottomAltitude(KsaWorld.BodyAirOf(camera.NearbyCelestial)),
                                            (float)Aurora.TopAltitude(KsaWorld.BodyAirOf(camera.NearbyCelestial)),
                                            SkyDispatch.Aurora,
                                            (float)sinLatitude),
                       Shape = new float4((float)eastEcl.X, (float)eastEcl.Y, (float)eastEcl.Z, (float)age),
                   }));
               }
   ```

   Source: [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L583-L602). Note the guard `ReferenceEquals(over, camera.NearbyCelestial)`: the shader's planet is whatever `global.lighting.planetPosition` holds, which is the recording camera's nearby atmospheric body, so a sky object over another planet must not be drawn.

3. **Branch in `main`, highest kind first.** The shader tests the negative bound first, then the kind from the highest threshold down, with the lowest kind as the fall-through:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryCloud.comp L1696-L1729
       // Only for a dispatch that is the ground alone: on a cloud's those two floats are the
       // fireball's radius and glow, on a flash-only one there is nothing to offset, and on a sky
       // dispatch (a negative bound) FireSun.w is the dispatch's own -- an aurora's positive for a
       // northern foot, which offset here drew every northern curtain off the screen.
       if (pc.FireSun.w > 0.0 && pc.CentreRadius.w >= 0.0
           && !(pc.AgeStrengthWind.y > 0.0 && pc.CentreRadius.w > 0.0))
       {
           at += ivec2(pc.FireSun.xy);
       }
       // ...
       if (at.x >= size.x || at.y >= size.y) return;

       // A LAYER A HIGH BURST LIT, on a dispatch of its own that the negative bound marks.
       if (pc.CentreRadius.w < 0.0)
       {
           if (pc.FireSun.z > 2.5) DrawRedWave(at, size);
           else if (pc.FireSun.z > 1.5) DrawDebris(at, size);
           else if (pc.FireSun.z > 0.5) DrawAurora(at, size);
           else DrawXRayGlow(at, size);
           return;
       }
   ```

   Source: [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L1696-L1729). Thresholds sit halfway between kind numbers so a float that picked up a rounding error still lands in its own branch. The first condition is a bug fix preserved in the code: another mode (the ground mark) reuses `FireSun.w` as a pixel offset, and without the `CentreRadius.w >= 0.0` guard every northern aurora (positive sine) was shifted off the screen.

4. **Cap each kind and keep the newest.** The CPU gathers every candidate, then keeps up to `MostOf(kind)` per kind, newest first:

   ```csharp
   // src/KSArmory/Sim/SkyDispatch.cs L46-L55
       public static void Choose(IReadOnlyList<float> kinds, List<int> keep)
       {
           keep.Clear();
           for (int i = 0; i < kinds.Count; i++)
           {
               int newer = 0;
               for (int j = i + 1; j < kinds.Count; j++) if (kinds[j] == kinds[i]) newer++;
               if (newer < MostOf(kinds[i])) keep.Add(i);
           }
       }
   ```

   Source: [src/KSArmory/Sim/SkyDispatch.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SkyDispatch.cs#L46-L55). An entry survives if fewer than `MostOf` entries of its kind come after it in the list, and the list is in the order bursts went off. The author chose newest over brightest on purpose: brightness crosses as one burst fades and the next arrives, and a brightness ranking made the drawn curtain jump between the two.

5. **Dispatch each kind in its own profiler region.** `DrawSky` walks the kept list for one kind, puts a compute hazard barrier before every dispatch after the first, binds the shared pipeline and covers the whole screen:

   ```csharp
   // src/KSArmory/Ksa/CloudPass.cs L108-L129
       private static void DrawSky(CommandBuffer commandBuffer, IViewport viewport, Camera camera, int width, int height,
                                   float kind, ProfilerTag tag, ref int marks)
       {
           bool any = false;
           foreach ((float k, _, _) in _sky) any |= k == kind;
           if (!any) return;

           using (commandBuffer.TagRegion(GpuTag))
           using (commandBuffer.TagRegion(tag))
           {
               foreach ((float k, _, Push push) in _sky)
               {
                   if (k != kind) continue;

                   if (marks > 0) Hazard(commandBuffer);

                   BindCloud(commandBuffer, viewport, camera, push);
                   commandBuffer.Dispatch((width + Group - 1) / Group, (height + Group - 1) / Group, 1);
                   marks++;
               }
           }
       }
   ```

   Source: [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L108-L129). One region per kind is what lets the profiler (see [Raymarch a local volume or analytic sky effect into the HDR scene](../raymarch-a-local-volume-into-the-hdr-scene/overview.md)) report glow, aurora, debris and red wave costs separately.

6. **Guard the contract with a text test.** The thresholds and the offset guard are exactly the kind of coupling that breaks silently, so the mod regex-reads the shader. This test finds every `if (pc.FireSun.z > N) DrawX(...)` line, checks each threshold lies below its kind and above the next, and that the lowest kind is the fall-through:

   ```csharp
   // tests/KSArmory.Tests/SkyDispatchShaderTests.cs L28-L49
       [Fact]
       public void EachKindsBranchCatchesItAndNoOther()
       {
           string source = Shader();
           MatchCollection tests = Regex.Matches(source, @"if \(pc\.FireSun\.z > ([0-9.]+)\) (Draw\w+)\(at, size\);");
           Assert.NotEmpty(tests);

           double above = double.PositiveInfinity;
           foreach (Match m in tests)
           {
               double threshold = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
               float kind = Kinds[m.Groups[2].Value];

               // Tested from the highest down: this one's threshold is under its kind and over the next.
               Assert.True(threshold < kind && threshold > kind - 1.0, $"{m.Groups[2].Value} caught at > {threshold}");
               Assert.True(threshold < above, "the kinds are tested from the highest down");
               above = threshold;
           }

           // And what none of the thresholds catch falls through to the lowest kind.
           Assert.Matches(@"else DrawXRayGlow\(at, size\);", source);
       }
   ```

   Source: [tests/KSArmory.Tests/SkyDispatchShaderTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/SkyDispatchShaderTests.cs#L28-L49). A second test finds the mark-offset `if` and asserts its condition contains `pc.CentreRadius.w >= 0.0`, so the guard from step 3 cannot be removed unnoticed.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Sim/SkyDispatch.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/SkyDispatch.cs) | Kind numbers, `FireSun.w` contract, per-kind caps, newest-first choice |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs) | Gathering the four kinds (L552-L652), `KeepTheNewestOfEachKind`, `DrawSky` |
| [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp) | Mark-offset guard, kind branch, the four `Draw*` functions |
| [tests/KSArmory.Tests/SkyDispatchShaderTests.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/tests/KSArmory.Tests/SkyDispatchShaderTests.cs) | Shader-text test of thresholds and the offset guard |

## Variations and gotchas

- **Worst case is still twelve dispatches.** The caps allow 4 + 3 + 3 + 2 full-screen dispatches; the pass does not shed sky work when its measured cost is over budget.
- **Each kind owns `FireSun.w`.** Any mode that interprets a field as an offset, a size or a flag must check the discriminator first; the aurora bug is the cautionary example.
- **Only the camera's body.** The planet block (`global.lighting.planetPosition`) is filled per viewport from the camera's effective nearby celestial and only for atmospheric bodies, so objects over other bodies draw nothing until the camera is near them.
- **Shared gates.** The sky shares every gate of the cloud: shader pass switched on, a nearby celestial, and the weather shadow buffers found; switching them off removes the sky with the cloud.
- **Compile-check the monolith.** One big shader multiplies the cost of a typo; use [Compile-check a mod's GLSL without launching the game](../../glsl-workflow/check-mod-shaders-offline-with-glslang/overview.md).

## See also

- Siblings: [128-byte push block](pack-parameters-for-a-compute-effect-1.md), [storage buffer of segments](pack-parameters-for-a-compute-effect-3.md), [camera-relative positions](pack-parameters-for-a-compute-effect-4.md), [overview](overview.md)
- [What a high or airless burst lights in the sky, chosen by the air at the burst](../../../../mods/ksarmory/features/high-altitude-burst-sky.md), [A mod compute pass recorded into KSA's frame just before bloom](../../../../mods/ksarmory/features/compute-pass-before-bloom.md)
- [Raymarch a local volume or analytic sky effect into the HDR scene](../raymarch-a-local-volume-into-the-hdr-scene/overview.md)
