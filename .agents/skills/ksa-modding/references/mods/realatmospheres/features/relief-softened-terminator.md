---
type: Mod Feature
title: Relief-softened terminator
description: Replaces the planet's self-shadow on its terrain and ground clutter with a horizon test that closes over a band set by the body's maximum relief, removing the knife-edge terminator on airless and thin-air bodies
mod: realatmospheres
category: shaders
techniques: [glsl-shader, harmony-prefix]
hooks: [starmap StarMapBeforeMain, harmony-prefix RenderCore.ShaderModuleUtils.FromFile]
lifecycle: [startup:starmap-before-main, render:planet]
game_types: [KSA.PlanetRenderer]
tags: [shaders, glsl, terrain, lighting, terminator, shadows, pluto, triton, moon]
---

KSA shades a body's own shadow on its terrain as if the body were a smooth ball: sunlight
transmittance stops at the mean sphere's horizon within a fixed band (`ApplyPlanetSoftShadow`), and the
body eclipses itself as a 0.999 R sphere (`getSelfCelestialShadow`). Where little air dims the setting
Sun first, ground facing the Sun is still bright at that horizon, so on Pluto the terminator was a
single line from lit to black. Over real ground the Sun stays visible past the ball's horizon wherever
the terrain toward it is lower, by up to `sqrt(2 relief / R)`. The mod uses that band
([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2699-L2712)).[^ra-payloads]

## How it works

- **Own-shadow function.** `RaOwnShadow` computes the Sun's elevation at the point, the dip of the
  horizon of a ball of `limb * R` seen from the point's own radius, and the band
  `clamp(sqrt(2 relief / R), 0.01, 0.1)` (never narrower than the Sun's angular size), and returns
  `smoothstep(-band, 0, elevation + dip)` ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2733-L2752)). `relief` is
  `uboPlanet.heightScale`, which the game fills with the body's maximum surface height
  ([current/decomp/KSA/PlanetRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetRenderer.cs#L728)).[^ksa-planetrenderer]
- **Other bodies' eclipses kept.** `RaOtherShadows` is the stock `getSelfCelestialShadow` minus the
  body's eclipse of itself: the game's ramp on the sphere normal plus every other body's
  `applyCelestialShadow` ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2754-L2775),
  [current/Content/Core/Shaders/Common/CelestialShadows.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/CelestialShadows.glsl#L121)).
- **Transmittance without the LUT's soft shadow.** `RaTransmittanceToSun` reads the transmittance LUT
  without `ApplyPlanetSoftShadow` when the body is known
  ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2777-L2788),
  [current/Content/Core/Shaders/Atmosphere/AtmosphereLuts.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/AtmosphereLuts.glsl#L64-L89)).
- **Four edits per shader.** In `Planet/Planet.frag` and `Planet/GroundClutter/Solid.frag`: insert the
  functions after the `CelestialShadows.glsl` include, replace the self-shadow factor, replace the
  transmittance lookup, and give ambient light its own factor (sky light keeps the game's cut-off point
  with `limb = 0.999`) ([Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2791-L2826)). The clutter shader's stock call asks
  for body index `1`; the patch passes the clutter's own `pushConsts.celestialIndex`.
- **Unknown body, stock behaviour.** If `celestialIndex` is outside `global.celestial.bodyCount`,
  every function returns what the game would have computed.

## Lifecycle and timing

- **`startup:starmap-before-main`.** `PatchTerrainShadow` groups the sites by file and patches each
  file whole or not at all ([ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L837-L861)).
- **`render:planet` (GPU).** `Planet.frag` and the clutter shader run in the planet colour pass
  (stock ids `PlanetFrag` and `ClutterSolidFrag`,
  [current/Content/Core/DefaultAssets.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/DefaultAssets.xml#L149-L159)).

## Key code

| Path | Role |
|---|---|
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2714-L2789) | `RaOwnShadow`, `RaOtherShadows`, `RaTransmittanceToSun` |
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2791-L2826) | The eight sites in two shaders |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L837-L861) | `PatchTerrainShadow` |

## Snippets

```glsl
// Payloads.cs L2737-L2752 (GLSL inside a C# raw string)
float RaOwnShadow(vec3 worldPosition, vec4 sunPosition, int celestialIndex, float relief, float limb)
{
    if (!RaOwnShadowKnown(celestialIndex))
        return 1.0;
    vec3 centre = global.celestial.bodies[celestialIndex].xyz;
    float radius = global.celestial.bodies[celestialIndex].w;
    vec3 up = worldPosition - centre;
    float r = length(up);
    vec3 toSun = sunPosition.xyz - worldPosition;
    float sunDistance = length(toSun);
    float sunElevation = asin(clamp(dot(up, toSun) / (r * sunDistance), -1.0, 1.0));
    float horizonDip = acos(clamp(limb * radius / r, 0.0, 1.0));
    float sunDiameter = 2.0 * asin(clamp(sunPosition.w / sunDistance, 0.0, 1.0));
    float band = max(clamp(sqrt(2.0 * max(relief, 0.0) / radius), 0.01, 0.1), sunDiameter);
    return smoothstep(-band, 0.0, sunElevation + horizonDip);
}
```

```csharp
// Payloads.cs L2797-L2803
        (PlanetFrag,
         "    float selfShadowFactor = getSelfCelestialShadow(inWorldPosition.xyz, sunPosition, sphereWorldNormal, uboPlanet.celestialIndex);\n",
         "    // PATCHED (Real Atmospheres: the planet's own shadow, softened by its relief): in place of the body's eclipse of\n" +
         "    // itself and the LUT's planet soft shadow; the sky's light keeps the game's cut-off point, closed over the same band\n" +
         "    float raOtherShadows = RaOtherShadows(inWorldPosition.xyz, sunPosition, sphereWorldNormal, uboPlanet.celestialIndex);\n" +
         "    float selfShadowFactor = raOtherShadows * RaOwnShadow(inWorldPosition.xyz, sunPosition, uboPlanet.celestialIndex, uboPlanet.heightScale, 1.0);\n" +
         "    float raSkyShadowFactor = raOtherShadows * RaOwnShadow(inWorldPosition.xyz, sunPosition, uboPlanet.celestialIndex, uboPlanet.heightScale, 0.999);\n"),
```

## Game APIs touched

| Member | Source | Used for |
|---|---|---|
| `Planet.frag` self-shadow, transmittance, ambient | [current/Content/Core/Shaders/Planet/Planet.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Planet/Planet.frag#L296-L322) | Replaced lines |
| `GroundClutter/Solid.frag` | [current/Content/Core/Shaders/Planet/GroundClutter/Solid.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Planet/GroundClutter/Solid.frag#L466-L495) | Replaced lines |
| `getSelfCelestialShadow`, `applyCelestialShadow` | [current/Content/Core/Shaders/Common/CelestialShadows.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/CelestialShadows.glsl#L7-L121) | Reimplemented without the self-eclipse |
| `ApplyPlanetSoftShadow` | [current/Content/Core/Shaders/Atmosphere/AtmosphereLuts.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/AtmosphereLuts.glsl#L64-L89) | Skipped for known bodies |
| `global.celestial.bodies`, `bodyCount` | [current/Content/Core/Shaders/Common/Global.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/Global.glsl#L77-L78) | Body centre and radius |
| `uboPlanet.heightScale`, `celestialIndex` | [current/Content/Core/Shaders/Common/Planet.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/Planet.glsl#L47-L48) | Relief and body index |

## Pitfalls and version notes

- **Relief is the height map's maximum, taken as deep as it is high.** Bodies with one tall feature
  get a wide band everywhere; the clamp to 0.1 rad (5.7 degrees) bounds it.
- **Two shaders must agree.** Terrain and rocks patched separately could light differently at the
  terminator; each shader is all-or-nothing, but one could apply while the other does not after a
  game update.
- **Not atmosphere-specific.** This changes airless bodies (the Moon, Mercury) too, which is where it
  matters most.

## See also

- [Cloud pass shading and ring shadows](cloud-pass-shading-and-ring-shadows.md)
- [Anchor-verified text patching of the shadow tree](shadow-tree-anchor-patching.md)
- [Branch per body in shared shaders: a relief-aware terminator closing over a sqrt(2 relief / R) band](../../../howto/shaders/stock-shaders/branch-per-body-inside-shared-game-shaders/branch-per-body-inside-shared-game-shaders-2.md) - how-to article demonstrating this feature

[^ra-payloads]: Payloads.cs
[^ksa-planetrenderer]: KSA.PlanetRenderer (decompiled, build 2026.10.10.5554)
