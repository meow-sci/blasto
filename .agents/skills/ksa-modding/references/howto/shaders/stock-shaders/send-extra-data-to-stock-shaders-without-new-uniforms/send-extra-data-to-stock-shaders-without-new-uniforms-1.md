---
type: How-To
title: "Smuggle data into stock shaders: a temporary value swap in a prefix, or an edit of the boxed struct in a postfix"
description: Write a clock and a switch into atmosphere uniform fields the haze bodies do not use for the length of one call, send spin through unused ring slots, and clear the LUT-valid flags when the switch changes
category: shaders
group: stock-shaders
topic: send-extra-data-to-stock-shaders-without-new-uniforms
approach: 1
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/realatmospheres/features/atmosphere-uniform-side-channel]
techniques: [harmony-prefix, harmony-postfix, reflection, glsl-shader]
hooks:
  - harmony-prefix KSA.AtmosphereRenderer.FillPlanetAtmosphereData
  - harmony-postfix KSA.AtmosphereRenderer.FillPlanetAtmosphereData
  - reflection KSA.AtmosphereRenderer._planetLutsValid
  - reflection KSA.DistanceReference._value
lifecycle: [render:atmosphere, frame:render, startup:starmap-before-main]
game_types:
  - KSA.AtmosphereRenderer
  - KSA.Atmosphere.Rendering.ShaderStructs.AtmosphereData
  - KSA.DistanceReference
  - KSA.Universe
tags: [side-channel, uniform, atmosphere, reflection, lut-invalidation, haze, realatmospheres]
---
RealAtmospheres' haze shaders need three things the atmosphere uniform buffer does not carry: a clock that freezes on pause and speeds up with warp, the player's on/off switch for the haze bands, and the body's spin axis and
angle. Adding uniforms would mean changing descriptor layouts in C#. Instead the mod writes the values into fields the shaders already receive and that are unused on those bodies, for the duration of one method call. This
article covers the swap technique and the LUT-validity half of it; the clock alone is also in [Send per-frame values and time to a shader with push constants, written at the right point in the frame](../../post-processing/send-push-constants-to-a-shaderextensions-shader/overview.md) article 4. Everything is reached by reflection, so the mod needs no game references.

## Background: how the game does it

`AtmosphereRenderer.FillPlanetAtmosphereData(Celestial?, AtmosphericBody, AtmosphereReference, float3, float, int)` builds an `AtmosphereData` struct from the body's `AtmosphereReference`: `OzoneExtent = atmoRef.Visual.Ozone.Extent.InMeters()`
and, only for ringed bodies, `UseRingShadows = 1` plus the ring normal, inner and outer radius ([current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1136-L1164)). `DistanceReference` is a class whose `InMeters()` returns its private `_value`
([current/decomp/KSA/DistanceReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DistanceReference.cs#L147-L150)), so a temporary change to `_value` is visible inside the call. The renderer computes transmittance, multiple-scattering and ambient LUTs once per body and keeps them while
`_planetLutsValid[slot]` is true; `InvalidatePlanetLuts(body)` is public and `OnRebuildFrameResources` clears the whole array ([current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L413-L419), [current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1354-L1360)).

## Lifecycle and timing

`render:atmosphere`, main thread, while recording: `FillPlanetAtmosphereData` is called from `AtmosphereRenderer.BeginFrame` (all atmospheric bodies, per viewport) and `PrepareAtmosphereData` (the body being drawn), both from
`PlanetTransparenciesRenderer.RenderTransparencies`, so the values reach the GPU in this frame's uniform buffer. The patches are installed at `startup:starmap-before-main`. The LUT flags are cleared before the LUT pass checks
them for the body, so the tables regenerate the same frame. See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Install fail-soft, by name, after checking the shape.** Look up the method with `AccessTools`, verify the parameters Harmony will inject by name (`body`, `atmoRef`) and that the returned struct still has the ring slots, then patch with
   a prefix and a postfix:

   ```csharp
   // ModMain.cs L105-L124
           Type? atmoRenderer = AccessTools.TypeByName("KSA.AtmosphereRenderer");
           MethodInfo? fillAtmo = atmoRenderer == null ? null : AccessTools.Method(atmoRenderer, "FillPlanetAtmosphereData");
           if (fillAtmo != null && Patches.CanInjectSimTime(fillAtmo))
           {
               // The haze's turning about the body's own pole goes out in the data the method returns, when
               // that still has the slots for it: see Patches.FillAtmosphereDataTurnPostfix.
               bool turn = Patches.CanInjectHazeTurn(fillAtmo);
               if (!turn)
                   ShadowBuilder.Log("WARN: the atmosphere data has no free ring slots; the haze turns about fixed axes");
               try
               {
                   harmony.Patch(fillAtmo,
                       prefix: new HarmonyMethod(typeof(Patches), nameof(Patches.FillAtmosphereDataPrefix)),
                       postfix: new HarmonyMethod(typeof(Patches),
                           turn ? nameof(Patches.FillAtmosphereDataTurnPostfix) : nameof(Patches.FillAtmosphereDataPostfix)));
               }
               catch (Exception e)
               {
                   ShadowBuilder.Log($"WARN: could not hook FillPlanetAtmosphereData ({e.GetType().Name}); haze animation is not sim-linked");
               }
   ```

   Source: [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L105-L142) (excerpt of the block). Harmony injects `body` and `atmoRef` by name and throws on a missing one, which would take the rest of the mod down with it, so the names are checked before patching.

2. **Swap in the prefix, restore from `__state` in the postfix.** The prefix identifies haze bodies by mean radius, reads the sim clock, and sets the ozone extent's private `_value` to `simSeconds + 1000` (negative when the player switched the bands
   off). The 1000 s offset keeps the value above the shader's 0.5 "is this a clock" test at the start of a game; the sign carries the switch:

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

   ```csharp
   // ModMain.cs L382-L386
       public static void FillAtmosphereDataPostfix(object? __state)
       {
           if (__state is HazeState { Swap: DistanceSwap swap })
               _distanceValue?.SetValue(swap.Reference, swap.Metres);
       }
   ```

   Sources: [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L309-L324), [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L382-L386). It is safe because nothing else reads that extent during the call and the call runs on the main thread; the ozone coefficients on these bodies are zero or negligible, so the extent has no visual effect.

3. **Edit the returned struct for more data.** Harmony boxes a value-type result for a `ref object __result`, so a second postfix can write fields by reflection and Harmony copies them back. For ring-less haze bodies the mod puts the spin axis in the ring normal, the haze angle
   in the inner radius and a marker `-1` in the outer radius:

   ```csharp
   // ModMain.cs L443-L455
       public static bool WriteHazeTurn(object data, object body, double meanRadius, double simSeconds)
       {
           if (!TurnSlots(data.GetType()) || _useRings!.GetValue(data) is not int rings || rings != 0) return false;
           if (HazeTurn(body, meanRadius, simSeconds) is not (double x, double y, double z, double angle)) return false;
           object normal = _ringNormal!.GetValue(data)!;
           _ringNormalParts![0]!.SetValue(normal, (float)x);
           _ringNormalParts[1]!.SetValue(normal, (float)y);
           _ringNormalParts[2]!.SetValue(normal, (float)z);
           _ringNormalParts[3]!.SetValue(normal, (float)angle);
           _ringNormal.SetValue(data, normal);
           _ringOuter!.SetValue(data, HazeTurnMarker);
           return true;
       }
   ```

   Source: [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L443-L455). The guard `rings != 0` leaves ringed bodies alone: every stock use of the ring slots is behind `useRingShadows`.

4. **Decode in the patched shader.** The data is read where the haze march needs it; the shader treats `useRingShadows == 0 && ringOuterRadius < -0.5` as "turn sent":

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

   Source: [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2376-L2386). The fallback keeps the shader working when the mod's hook is missing.

5. **Clear the LUT-valid flags when the switch changes.** Pluto's and Triton's bands are baked into the kept LUTs, so when the player flips the setting the mod clears the whole `bool[]` the same way the renderer does on a rebuild:

   ```csharp
   // ModMain.cs L370-L380
       public static bool ForgetTables()
       {
           try
           {
               if (_renderer == null || _tablesValid?.GetValue(_renderer) is not bool[] valid) return false;
               Array.Clear(valid);
               return true;
           }
           catch { return false; }
       }
   ```

   Source: [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L370-L380). The renderer instance comes from a separate prefix on the same method that only stores `__instance` (`RendererSeenPrefix`). A cheaper alternative is the public `AtmosphereRenderer.InvalidatePlanetLuts(body)`, which clears one body.

## Complete example

| File | Role |
|---|---|
| [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs) | Shape checks, the prefix, postfixes, clock lookup, LUT invalidation |
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs) | The shader side that reads the slots |
| [Settings.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Settings.cs) | The player switch (`HazeBands`) |

## Variations and gotchas

- **Fails soft, and that has bitten before.** The clock method was renamed once, and the atmosphere data moved from a push constant into a uniform buffer; both times the haze silently ran on wall-clock time. Log when a fallback is taken.
- **Only bodies without rings,** and only bodies you can identify; the mod keys on mean radius in both C# and GLSL and keeps them in step by hand.
- **The ozone extent must be harmless at any value** on those bodies, including the negative one.
- **Float precision.** The clock is a `float` of raw seconds; the angle is wrapped in double before narrowing.
- **Pick fields that no stock path reads for the case at hand.** Re-check on each game update that the carrier is still unused.

## See also

- Siblings: [state-flag bits](send-extra-data-to-stock-shaders-without-new-uniforms-2.md), [material marker](send-extra-data-to-stock-shaders-without-new-uniforms-3.md), [sentinel dispatch](send-extra-data-to-stock-shaders-without-new-uniforms-4.md), [overview](overview.md)
- [Atmosphere uniform side channel](../../../../mods/realatmospheres/features/atmosphere-uniform-side-channel.md), [Haze-band sky march](../../../../mods/realatmospheres/features/haze-band-sky-march.md)
- [Recreate mod render targets when KSA rebuilds its renderer](../../../rendering/vulkan-passes/handle-renderer-rebuilds/overview.md), [Extend KSA's sky and cloud shaders: per-pixel haze and cloud lighting fixes](../extend-stock-sky-and-cloud-shaders/overview.md)
