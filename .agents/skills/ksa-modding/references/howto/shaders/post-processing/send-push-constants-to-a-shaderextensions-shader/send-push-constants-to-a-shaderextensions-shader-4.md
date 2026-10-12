---
type: How-To
title: "Time in a stock shader: signed simulation seconds smuggled through a stock atmosphere field"
description: Give a patched stock atmosphere shader a clock that freezes on pause and scales with warp by writing Universe.GetElapsedTime into an unused uniform field for the length of one call
category: shaders
group: post-processing
topic: send-push-constants-to-a-shaderextensions-shader
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/realatmospheres/features/atmosphere-uniform-side-channel]
techniques: [harmony-prefix, harmony-postfix, glsl-shader, reflection]
hooks:
  - harmony-prefix KSA.AtmosphereRenderer.FillPlanetAtmosphereData
  - harmony-postfix KSA.AtmosphereRenderer.FillPlanetAtmosphereData
lifecycle: [render:atmosphere, frame:render]
game_types: [KSA.Universe, KSA.AtmosphereRenderer, KSA.DistanceReference, KSA.Program]
tags: [sim-clock, time-warp, pause, stock-shader, side-channel, realatmospheres, universe]
---
ShaderExtensions only reaches passes that ShaderExtensions builds. A stock shader that a mod patches has no push constant of yours, but it can still be
given a simulation clock: RealAtmospheres writes `Universe.GetElapsedTime()` into an atmosphere uniform field the haze bodies do not use, for the
duration of the call that fills the uniform buffer. This article is the clock half of that technique; the general idea of unused fields is
[Smuggle data into stock shaders through unused uniforms, state bits and sentinel values](../../stock-shaders/send-extra-data-to-stock-shaders-without-new-uniforms/overview.md). It needs Harmony and a text-patched shader (see
[Replace or text-patch the game's GLSL without editing game files](../../stock-shaders/replace-or-patch-game-glsl-without-editing-game-files/overview.md)).

## Background: how the game does it

`AtmosphereRenderer.FillPlanetAtmosphereData(Celestial?, AtmosphericBody, AtmosphereReference, ...)` builds the `AtmosphereData` struct, including
`OzoneExtent = atmoRef.Visual.Ozone.Extent.InMeters()` ([current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1136-L1164)). It is called from
`AtmosphereRenderer.BeginFrame` and `PrepareAtmosphereData`, both from `PlanetTransparenciesRenderer.RenderTransparencies`. `DistanceReference` is a
class, so a temporary swap of its private `_value` is visible to `InMeters()` inside the call. `Universe.GetElapsedTime()` returns the next time of
the last applied simulation step, so the value freezes on pause and scales with warp, unlike a wall-clock.

## Lifecycle and timing

`render:atmosphere` on the main thread while the command buffer is being recorded: the prefix and postfix bracket the fill call, so the value reaches
the GPU in this frame's uniform buffer. The clock is the last completed sim step, not live physics.

## Steps

1. **Find the simulation clock, tolerating renames.** The mod looks up `KSA.Universe.GetElapsedTime`, falling back to the older name, and logs when
   neither exists, because a silent miss makes the haze run on wall-clock time:

   ```csharp
   // ModMain.cs L268-L283 (RealAtmospheres)
       private static MethodInfo? SimClock()
       {
           if (_simClockLookedUp) return _getElapsedSimTime;
           _simClockLookedUp = true;

           _universeType ??= AccessTools.TypeByName("KSA.Universe");
           if (_universeType != null)
               foreach (string name in new[] { "GetElapsedTime", "GetElapsedSimTime" })
               {
                   _getElapsedSimTime = AccessTools.Method(_universeType, name);
                   if (_getElapsedSimTime != null) return _getElapsedSimTime;
               }

           ShadowBuilder.Log("WARN: no simulation clock found; haze animation falls back to wall-clock time");
           return null;
       }
   ```

   Source: [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L268-L283).

2. **Swap the value in a prefix, restore it in a postfix.** For the haze bodies (identified by mean radius) the prefix sets the private `_value` (metres) of
   the ozone extent to `simSeconds + 1000`, or its negative when the player has the bands switched off; the postfix restores the body's own value from
   `__state`:

   ```csharp
   // ModMain.cs L309-L324
       public static void FillAtmosphereDataPrefix(object body, object atmoRef, out object? __state)
       {
           __state = null;
           try
           {
               if (body.GetType().GetProperty("MeanRadius")?.GetValue(body) is not double meanRadius
                   || !IsHazeBody(meanRadius))
                   return;
               double? seconds = HazeClock();
               if (seconds == null) return;
               object? extent = OzoneExtent(atmoRef);
               DistanceSwap? swap = extent == null ? null : SetDistanceMetres(extent, HazeClockFor(seconds.Value, HazeBandsNow()));
               __state = new HazeState(swap, meanRadius, seconds.Value);
           }
           catch { __state = null; }
       }
   ```

   Source: [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L309-L324). The 1000 s offset keeps the value above the shader's 0.5 "is this a clock" test at the start of a game; the sign carries the
   switch ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L331-L333)). Check the target's parameter names before patching: Harmony injects `body` and `atmoRef` by name and throws on a missing one.

3. **Read it in the patched shader**, with a wall-clock fallback when the field does not look like a clock:

   ```glsl
   // Payloads.cs L2376-L2386 (GLSL inside a C# raw string, RealAtmospheres)
       bool raTurnSent = atmosphereDataUbo.useRingShadows == 0 && atmosphereDataUbo.ringOuterRadius < -0.5;
       vec3 spinAxis = raTurnSent
           ? atmosphereDataUbo.ringNormalAndInnerRadius.xyz
           : atmosphereData.bottomRadius > 2000000.0
               ? normalize(vec3(0.09, 0.46, 0.88))
               : RaHazeBodyIsTriton(atmosphereData.bottomRadius)
                   ? normalize(vec3(-0.45, 0.61, -0.65))
                   : normalize(vec3(-0.68, 0.62, -0.39));
       // SIM-LINKED time: the mod injects elapsed SIM seconds into ozoneExtent for the haze bodies
       // (freezes on pause, scales with time-warp). Falls back to wall-clock if not injected.
       float animT = atmosphereData.ozoneExtent > 0.5 ? atmosphereData.ozoneExtent : global.camera.time;
   ```

   Source: [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2376-L2386). The stock `global.camera.time` is player time, `(float)_lastPlayerTime`, written per viewport slot in `Program` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2875)), a player-time clock rather than the simulation clock `Universe.GetElapsedTime()` that the mod injects.

## Complete example

[ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs) (shape checks and the patches on `FillPlanetAtmosphereData`) and [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs) (the patched shader text).

## Variations and gotchas

- **Fails soft.** Twice the link broke silently: the clock method was renamed, and the atmosphere data moved from a push constant to a uniform buffer; both times the
  haze ran on wall-clock time with no error. Log when a fallback is taken.
- **Float precision.** The clock is a `float` of raw seconds; the mod wraps the rotation angle in double before narrowing, but terms like `animT * 3e-5` lose precision in very long saves.
- **Only bodies you can identify.** The mod keys on mean radius in both C# and GLSL; keep the two in step by hand.
- **Same effect with your own passes:** use `ShaderTime.TimeSinceStart` (previous article) instead; this trick is for shaders you did not build.

## See also

- Siblings: [enabled flag per pass](send-push-constants-to-a-shaderextensions-shader-1.md), [per-side clock](send-push-constants-to-a-shaderextensions-shader-2.md), [ShaderTime block](send-push-constants-to-a-shaderextensions-shader-3.md), [overview](overview.md)
- [Atmosphere uniform side channel](../../../../mods/realatmospheres/features/atmosphere-uniform-side-channel.md), [Haze-band sky march](../../../../mods/realatmospheres/features/haze-band-sky-march.md)
- [Smuggle data into stock shaders through unused uniforms, state bits and sentinel values](../../stock-shaders/send-extra-data-to-stock-shaders-without-new-uniforms/overview.md), [Patch vehicle paint and canopy shaders in memory at compile time](../../stock-shaders/patch-game-shaders-in-memory-at-compile-time/overview.md)
