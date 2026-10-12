---
type: Mod Feature
title: Atmosphere uniform side channel
description: Prefix and postfix around AtmosphereRenderer.FillPlanetAtmosphereData put the sim clock, a feature switch and a spin axis into unused fields of the stock atmosphere uniform data, and invalidate kept LUTs
mod: realatmospheres
category: shaders
techniques: [harmony-prefix, harmony-postfix, reflection, glsl-shader]
hooks:
  - starmap StarMapBeforeMain
  - harmony-prefix KSA.AtmosphereRenderer.FillPlanetAtmosphereData
  - harmony-postfix KSA.AtmosphereRenderer.FillPlanetAtmosphereData
  - reflection KSA.AtmosphereRenderer._planetLutsValid
  - reflection KSA.DistanceReference._value
  - reflection KSA.Universe.GetElapsedTime
  - reflection KSA.Celestial.GetRotationAxisCce
lifecycle: [render:atmosphere]
game_types:
  - KSA.AtmosphereRenderer
  - KSA.Atmosphere.Rendering.ShaderStructs.AtmosphereData
  - KSA.AtmosphereReference
  - KSA.DistanceReference
  - KSA.Universe
  - KSA.UniverseTime
  - KSA.Celestial
  - KSA.PlanetTransparenciesRenderer
tags: [shaders, uniforms, harmony, prefix, postfix, reflection, sim-time, time-warp, lut-invalidation]
---

The patched atmosphere shaders need three things the stock uniform buffer does not carry: a clock
that freezes on pause and speeds up with time warp (for animated haze on Titan, Pluto and Triton), the
player's on/off switch for the haze bands, and the body's spin axis and rotation angle so the haze
turns with the planet. Adding uniforms would mean changing descriptor layouts in C#. Instead the mod
writes the values into fields the shaders already receive and that are unused on those bodies, for
the duration of one method call.[^ra-modmain]

## How it works

- **Where the buffer is filled.** `AtmosphereRenderer.FillPlanetAtmosphereData(Celestial?
  inNearbyCelestial, AtmosphericBody body, AtmosphereReference atmoRef, ...)` builds the
  `AtmosphereData` struct from `atmoRef`, including `OzoneExtent = atmoRef.Visual.Ozone.Extent.InMeters()`
  and, only for ringed bodies, `UseRingShadows = 1` plus the ring normal, inner and outer radius
  ([current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1136-L1164)).[^ksa-atmosphererenderer]
- **Clock and switch in the ozone extent.** The prefix identifies haze bodies by mean radius, reads
  the sim clock (`Universe.GetElapsedTime().Minutes * 60`), and temporarily sets the private `_value`
  (metres) of `atmoRef.Visual.Ozone.Extent` to `simSeconds + 1000`, or its negative when the player
  has switched the bands off; the postfix restores the old value from `__state`
  ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L294-L333), [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L382-L386),
  [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L527-L545)). `DistanceReference` is a class, so the swap is visible to
  `InMeters()` inside the call. The 1000 s offset keeps the value above the shader's 0.5 "is this a
  clock" test at the start of a game; the sign carries the switch (`RaHazeBandsOn(clock)` is
  `clock > -0.5`) ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2205-L2211)). The ozone coefficients on these bodies are
  zero or negligible, so the extent itself has no visual effect.
- **Spin in the ring slots.** If the returned struct still has `UseRingShadows` (int),
  `RingNormalAndInnerRadius` (four floats) and `RingOuterRadius` (float), a second postfix receives the
  struct boxed as `ref object __result` and, for ring-less bodies only, writes the body's
  `GetRotationAxisCce()` into the normal, the haze angle into the inner radius and `-1` into the outer
  radius as a marker ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L388-L455)). The angle is the ground's angular velocity
  plus a per-body wind over radius, times sim seconds, wrapped with `Math.IEEERemainder` in double
  precision ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L457-L502)). The shader treats `useRingShadows == 0 &&
  ringOuterRadius < -0.5` as "turn sent" ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2365-L2390)); every stock use of
  the ring slots is behind `useRingShadows`.
- **Invalidate kept LUTs on change.** The renderer computes transmittance, multiple-scattering and
  ambient LUTs once per body and keeps them while `_planetLutsValid[slot]` is true
  ([current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1059-L1076)).
  Pluto's and Triton's bands are baked into those LUTs, so when the setting flips, the mod clears the
  whole `bool[]` with `Array.Clear`, the same thing the renderer does on a rebuild
  ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L335-L380),
  [current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1354-L1360)).
  The renderer instance comes from a separate prefix on the same method that just stores
  `__instance`.

## Lifecycle and timing

- **`render:atmosphere`, main thread, while recording.** `FillPlanetAtmosphereData` is called from
  `AtmosphereRenderer.BeginFrame` (all atmospheric bodies, per viewport) and `PrepareAtmosphereData`
  (the body being drawn), both from `PlanetTransparenciesRenderer.RenderTransparencies`
  ([current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L442-L458),
  [current/decomp/KSA/PlanetTransparenciesRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetTransparenciesRenderer.cs#L233-L240)).
  Values written here reach the GPU in this frame's uniform buffer.
- **The clock is the last completed sim step.** `Universe.GetElapsedTime()` returns the next time of
  the last applied simulation step ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2398-L2402)),
  so the haze freezes on pause and scales with warp.
- **The temporary swap is safe** because nothing else reads that ozone extent during the call, and
  the call runs on the main thread only. The LUT flags are cleared before `RenderWithCompute` checks
  them for the body, so the LUTs regenerate the same frame.

## Key code

| Path | Role |
|---|---|
| [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L101-L142) | Shape checks and the three patches on `FillPlanetAtmosphereData` |
| [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L294-L333) | Prefix: haze body test, clock, signed extent swap |
| [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L335-L386) | Settings check, LUT invalidation, restoring postfix |
| [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L388-L502) | Ring-slot postfix, axis and angle, per-body haze wind |
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2365-L2390) | Shader side: read the turn and the clock |

## Snippets

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

```glsl
// Payloads.cs L2376-L2390 (GLSL inside a C# raw string)
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
    // A point of the pattern turns by +angle about the axis, so the direction it is looked up at turns by
    // -angle. The fallback rate: Pluto spins once per 6.39 d (omega ~1.14e-5 rad/s); 1.3e-5 is ~1.14x.
    float rotAngle = raTurnSent ? -atmosphereDataUbo.ringNormalAndInnerRadius.w : -mod(animT * 1.3e-5, 6.28318530718);
    float evolveT  = animT * 3.0e-5;                           // cloud evolution ~2.3x rotation (haze morphs over hours, not seconds)
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `AtmosphereRenderer.FillPlanetAtmosphereData` | [current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1136-L1164) | Prefixed twice and postfixed |
| `AtmosphereRenderer._planetLutsValid`, `InvalidatePlanetLuts` | [current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L187), [current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L413-L419) | Cleared by reflection; a public per-body alternative exists |
| `AtmosphereData` fields `OzoneExtent`, `UseRingShadows`, `RingNormalAndInnerRadius`, `RingOuterRadius` | [current/decomp/KSA.Atmosphere.Rendering.ShaderStructs/AtmosphereData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Atmosphere.Rendering.ShaderStructs/AtmosphereData.cs#L6-L52) | Repurposed slots |
| `AtmosphereDataUbo` (GLSL) | [current/Content/Core/Shaders/Atmosphere/AtmosphereDataUbo.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/AtmosphereDataUbo.glsl#L11-L33) | Shader view of the same fields |
| `DistanceReference._value`, `InMeters` | [current/decomp/KSA/DistanceReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DistanceReference.cs#L28), [current/decomp/KSA/DistanceReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/DistanceReference.cs#L147-L150) | Temporary value swap |
| `Universe.GetElapsedTime`, `UniverseTime.Minutes` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L2398-L2402), [current/decomp/KSA/UniverseTime.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/UniverseTime.cs#L40) | Sim clock |
| `Celestial.GetRotationAxisCce`, `GetAngularVelocity`, `MeanRadius` | [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L184), [current/decomp/KSA/Celestial.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Celestial.cs#L615) | Spin axis and rate |

## Pitfalls and version notes

- **Fails soft, and that has bitten before.** Twice the link broke silently: the clock method was
  renamed, and the atmosphere data moved from a push constant into a uniform buffer (the old hook wrote
  `_atmospherePushConstant`); in both cases the haze ran on wall-clock time with no error
  ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L260-L267), [ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L300-L304)). Log when a fallback is
  taken.
- **Float precision.** The clock is a `float` of raw seconds; the angle is wrapped in double before
  it is narrowed, so it stays exact in long saves, but the morph term `animT * 3e-5` loses precision
  as saves grow very long.
- **Only bodies without rings.** A ringed haze body would keep its rings and fall back to the shader's
  hard-coded axes.
- **Body identification by radius** in both C# and GLSL (`IsHazeBody`, `RaBodyHasHaze`) must be kept in
  step by hand ([ModMain.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ModMain.cs#L504-L508), [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2184-L2197)).
- **Cheaper alternative for invalidation.** `AtmosphereRenderer.InvalidatePlanetLuts(body)` is public
  and clears one body; the mod clears all, which also works when it has no body at hand.

## See also

- [Haze-band sky march](haze-band-sky-march.md)
- [ModMenu page and TOML settings](modmenu-page-and-toml-settings.md)
- [Measured density tables in the atmosphere shaders](measured-density-tables-in-shaders.md)
- [Time in a stock shader: signed simulation seconds smuggled through a stock atmosphere field](../../../howto/shaders/post-processing/send-push-constants-to-a-shaderextensions-shader/send-push-constants-to-a-shaderextensions-shader-4.md) - how-to article demonstrating this feature
- [Smuggle data into stock shaders: a temporary value swap in a prefix, or an edit of the boxed struct in a postfix](../../../howto/shaders/stock-shaders/send-extra-data-to-stock-shaders-without-new-uniforms/send-extra-data-to-stock-shaders-without-new-uniforms-1.md) - how-to article demonstrating this feature

[^ra-modmain]: ModMain.cs
[^ksa-atmosphererenderer]: KSA.AtmosphereRenderer (decompiled, build 2026.10.10.5554)
