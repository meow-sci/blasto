---
type: How-To
title: "Draw analytic sky effects in a compute pass: X-ray glow, curtains and debris shells chosen by the air"
description: Choose which full-screen analytic sky effects a burst starts from the air density at it, place each layer by the body's own isothermal atmosphere, and draw them in the planet frame the game puts in its global block
category: shaders
group: compute-effects
topic: raymarch-a-local-volume-into-the-hdr-scene
approach: 3
difficulty: expert
prerequisites: [starmap, harmony, vulkan]
demonstrated_by: [mods/ksarmory/features/high-altitude-burst-sky]
techniques: [glsl-shader, vulkan-pipeline]
hooks: [harmony-prefix KSA.SunbloomRenderer.Render]
lifecycle: [frame:draw-ui, frame:celestials, render:bloom]
game_types:
  - KSA.PhysicalAtmosphereReference
  - KSA.AtmosphericBody
  - KSA.Celestial
  - KSA.Camera
  - KSA.Program
  - KSA.SunbloomRenderer
  - KSA.Rendering.ComputePipelineWrapper
tags: [sky, analytic, aurora, glow, atmosphere, isothermal, density, planet, compute, ksarmory]
---
Not every effect needs a volume. A burst high in KSArmory's sky lights the air eighty-odd kilometres up with its X-rays, throws its debris along the planet's magnetic field to light an aurora at both ends, and leaves a glowing shell. Each of those is a small game-free model on the CPU and one full-screen analytic compute dispatch that adds light to the HDR scene, with no raymarch through a density field. This article covers the sub-step "draw analytic sky shells and curtains over the planet in a compute pass": reading the air from KSA, deciding which effects the air allows, placing layers by the body's atmosphere, and the planet frame the shaders measure from. How one pipeline serves four kinds is in [Pack parameters and camera data for a compute effect](../pack-parameters-for-a-compute-effect/overview.md) (the kind-tag approach); this is the engine and shader side of [What a high or airless burst lights in the sky, chosen by the air at the burst](../../../../mods/ksarmory/features/high-altitude-burst-sky.md).

## Background: how the game does it

- **KSA's air is isothermal.** `PhysicalAtmosphereReference` gives pressure and density as sea-level value times `exp(-altitude / scaleHeight)`, and a boundary height where both have thinned to fixed floors:

  ```csharp
  // current/decomp/KSA/PhysicalAtmosphereReference.cs L43-L48
  public void CalculateBoundaryHeight()
  {
      double val = (0.0 - ScaleHeight.InMeters()) * Math.Log(1E-09 / (double)SeaLevelDensity);
      double val2 = (0.0 - ScaleHeight.InMeters()) * Math.Log(0.0001 / (double)SeaLevelPressure);
      _height = DistanceReference.FromMeters(Math.Max(val, val2));
  }
  ```

  Source: [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L43-L48)

  ```csharp
  // current/decomp/KSA/PhysicalAtmosphereReference.cs L80-L88
  public double GetAtmosphericPressureAtAltitude(double altitudeInMeters)
  {
      return (double)SeaLevelPressure * Math.Exp((0.0 - altitudeInMeters) / ScaleHeight.InMeters());
  }

  public double GetAtmosphericDensityAtAltitude(double altitudeInMeters)
  {
      return (double)SeaLevelDensity * Math.Exp((0.0 - altitudeInMeters) / ScaleHeight.InMeters());
  }
  ```

  Source: [current/decomp/KSA/PhysicalAtmosphereReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicalAtmosphereReference.cs#L80-L88)

  The boundary is the larger of the altitude where density falls to 1e-9 and the altitude where pressure falls to 1e-4 Pa (about 167 km on KSA's Earth).
- **The planet block is per viewport and only for atmospheric bodies.** `Program.UpdatePlanetShaderData` writes `PlanetPosition`, `PlanetRadius` and the LUT layer into the lighting block of the viewport's global data, from the camera's effective nearby celestial, and only if that celestial is an `AtmosphericBody`:

  ```csharp
  // current/decomp/KSA/Program.cs L3004-L3015
  private void UpdatePlanetShaderData(Celestial? nearbyCelestial, IViewport viewport)
  {
      if (nearbyCelestial is AtmosphericBody atmosphericBody)
      {
          double3 vector = viewport.GetCamera().GetPositionEgo(atmosphericBody);
          double3 double5 = double3.Unpack(_lightingData[viewport.ShaderSlot].SunPositionRadius.XYZ);
          double meanRadius = atmosphericBody.MeanRadius;
          Line line = new Line(double5, double3.Zero);
          BoundingSphere3D inSphere = new BoundingSphere3D(vector, meanRadius);
          _lightingData[viewport.ShaderSlot].PlanetPosition = new float4((float)vector.X, (float)vector.Y, (float)vector.Z, 1f);
          _lightingData[viewport.ShaderSlot].PlanetRadius = (float)meanRadius;
          _lightingData[viewport.ShaderSlot].AtmosphereLutLayer = PlanetAtmosphereRenderer.GetLutLayer(atmosphericBody);
  ```

  Source: [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3004-L3015)

  So a compute shader that wants "the planet" reads `global.lighting.planetPosition` and `planetRadius`, camera-relative, and is only right for the camera's own body.

## Lifecycle and timing

| Moment | What happens |
|---|---|
| `frame:draw-ui` or `frame:celestials` | A detonation reads the air at the burst and starts only the effects that air allows; `NuclearClouds.Update` ages every glow, wave and curtain on the simulated step |
| `render:bloom` (prefix on `SunbloomRenderer.Render`) | The lists become push blocks for the camera's body, the newest few of each kind are kept, and each is dispatched over the whole screen with a barrier between dispatches |

The models are anchored in the body's rotating frame and converted to camera-relative ecliptic in double precision when the pass asks, so a curtain stays over the same ground as the planet turns. Ageing on the simulated step means a pause freezes the sky. See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence).

## Steps

1. **Turn the air at the point into a ratio.** The mod expresses density as a fraction of a fixed 1.225 kg/m3, zero at or above the declared boundary and zero when the body declares no physical atmosphere, and one when the read throws (so a failed read gives a dense-air mushroom rather than a sky of the wrong kind):

   ```csharp
   // src/KSArmory/Ksa/KsaWorld.cs L2526-L2537
   AtmosphereReference? atmosphere = body.GetAtmosphereReference();
   if (atmosphere?.Physical is not { } air) return 0.0;

   double seaLevel = air.SeaLevelDensity;
   double scaleHeight = air.ScaleHeight.InMeters();
   if (!(seaLevel > 0.0) || !(scaleHeight > 0.0)) return 0.0;

   if (altitude < 0.0) altitude = 0.0;
   if (altitude >= air.Height) return 0.0;

   double ratio = air.GetAtmosphericDensityAtAltitude(altitude) / Medium.ReferenceDensityKgPerM3;
   return double.IsFinite(ratio) && ratio >= 0.0 ? ratio : 1.0;
   ```

   Source: [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L2526-L2537)

   Whether the body has air at all is asked separately, so a body with no atmosphere takes the airless branch (particle emitters) instead:

   ```csharp
   // src/KSArmory/Ksa/KsaWorld.cs L2401-L2413
   public static bool HasAtmosphere(Celestial body)
   {
       try
       {
           return body.GetAtmosphereReference()?.Physical is { } air && air.Height > 0.0;
       }
       catch
       {
           // Unreadable reads as having one, so a burst falls back to the cloud rather than to the
           // airless effect: a cloud that does not draw is a smaller fault than dust on Earth.
           return true;
       }
   }
   ```

   Source: [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs#L2401-L2413)

2. **Mirror KSA's model on the Sim side.** `BodyAir` carries sea-level pressure and density, the declared scale height and the boundary as plain numbers, so the models never read a game object. A layer is placed where the mass of air above it reaches a calibrated column (density times scale height), which moves it sensibly on another body:

   ```csharp
   // src/KSArmory/Sim/BodyAir.cs L61-L69
   public double AltitudeOfColumn(double columnKgPerM2)
   {
       if (!HasAir || !(columnKgPerM2 > 0.0)) return 0.0;

       double surface = SeaLevelDensity * ScaleHeightMetres;
       if (columnKgPerM2 >= surface) return 0.0;

       return Math.Min(ScaleHeightMetres * Math.Log(surface / columnKgPerM2), BoundaryMetres);
   }
   ```

   Source: [src/KSArmory/Sim/BodyAir.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BodyAir.cs#L61-L69)

   ```csharp
   // src/KSArmory/Sim/XRayGlow.cs L35-L36
   public static double LayerAltitude(BodyAir air)
       => air.AltitudeOfColumn(LayerColumnKgPerM2 / Math.Max(air.Traits.XRayOpacity, 1.0e-6));
   ```

   Source: [src/KSArmory/Sim/XRayGlow.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/XRayGlow.cs#L35-L36)

3. **Start only the effects the air allows.** The thresholds are fixed ratios of sea-level air (for KSA's Earth: debris shell below 0.01, about 37 km; red wave between 0 and 2.5e-4, about 66 km to the boundary; aurora below 1e-4, about 74 km; X-ray glow below 1e-5, about 92 km). A faint glow is not worth a dispatch for ten minutes, so strength is checked too:

   ```csharp
   // src/KSArmory/Ksa/NuclearClouds.cs L900-L918
   double layer = XRayGlow.LayerAltitude(KsaWorld.BodyAirOf(body));
   double overLayer = Vec.Len(burstCcf) - body.MeanRadius - layer;
   if (hasAir && Aurora.Lights(airRatio)) Light(body, burstCcf, chargeKg);

   if (hasAir && RedWave.Lights(airRatio))
   {
       if (_waves.Count >= MaxGlows) _waves.RemoveAt(0);
       _waves.Add(new Glow { Body = body, BurstCcf = burstCcf, ChargeKg = chargeKg });
   }

   if (hasAir && XRayGlow.Lights(airRatio)
       && XRayGlow.Strength(kt, overLayer, XRayGlow.RiseSeconds) > XRayGlow.FaintestNits)
   {
       if (_glows.Count >= MaxGlows) _glows.RemoveAt(0);
       _glows.Add(new Glow { Body = body, BurstCcf = burstCcf, ChargeKg = chargeKg });
       Log.Info($"nuclear burst over the air: its X-rays light the layer {layer / 1000.0:F0} km "
                + $"up, {XRayGlow.Strength(kt, overLayer, 2.0):F2} "
                + "nits under it at first, red for minutes");
   }
   ```

   Source: [src/KSArmory/Ksa/NuclearClouds.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/NuclearClouds.cs#L900-L918)

4. **Only draw for the camera's body.** The shader's planet is the global block's, so a sky object over any other body must not be drawn. The push block carries a camera-relative centre, a negative brightness as the tag, and the layer's altitude and thickness over the planet:

   ```csharp
   // src/KSArmory/Ksa/CloudPass.cs L559-L581
   for (int i = 0; i < NuclearClouds.GlowCount; i++)
   {
       // Drawn against the planet the camera is near, which is the only one the shader has:
       // a glow over another body would be drawn as a shell round this one.
       if (!NuclearClouds.TryGlow(i, out double3 glowEcl, out double nits, out double green,
                                  out object? over)) continue;
       if (!ReferenceEquals(over, camera.NearbyCelestial)) continue;

       double3 lit = glowEcl - camera.PositionEcl;
       if (!Vec.IsFinite(lit)) continue;

       // Its size is the layer's, in the fireball's first two floats, and its brightness the bound's:
       // the layer where this body's air stops the X-rays, not a height.
       BodyAir glowAir = KsaWorld.BodyAirOf(camera.NearbyCelestial);
       _sky.Add((SkyDispatch.Glow, nits, new Push
       {
           InvViewProj = camera.VPInv.viewProjection,
           CentreRadius = new float4((float)lit.X, (float)lit.Y, (float)lit.Z, -(float)nits),
           FireSun = new float4((float)XRayGlow.LayerAltitude(glowAir), (float)XRayGlow.LayerThickness(glowAir),
                                SkyDispatch.Glow, 0f),
           Shape = new float4((float)green, 0f, 0f, 0f),
       }));
   }
   ```

   Source: [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L559-L581)

5. **Draw it analytically.** The X-ray glow intersects the view ray with two spheres around the planet (the layer's lower and upper surfaces), takes eight taps along each span, weights each tap by inverse square distance from the burst and skips taps the planet shadows from the burst. Light is added, and the sum is capped so a ray along the limb does not become a solid band:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryCloud.comp L1619-L1680
   void DrawXRayGlow(ivec2 at, ivec2 size)
   {
       vec2 uv = (vec2(at) + 0.5) / vec2(size);
       vec3 dir = normalize(WorldAt(uv, 1.0));
       float deviceZ = texture(sceneDepth, uv).r;
       float sceneT = deviceZ > 0.0 ? length(WorldAt(uv, deviceZ)) : 1.0e30;

       vec3 planet = global.lighting.planetPosition.xyz;
       float radius = global.lighting.planetRadius;
       float nits = -pc.CentreRadius.w;
       vec3 burst = pc.CentreRadius.xyz;
       float lower = radius + pc.FireSun.x - 0.5 * pc.FireSun.y;
       float upper = radius + pc.FireSun.x + 0.5 * pc.FireSun.y;
       float over = max(length(burst - planet) - (radius + pc.FireSun.x), 10000.0);

       // Where the ray is between the two spheres: in and out of the upper, less what the lower holds.
       float along = dot(planet, dir);
       float near2 = dot(planet, planet) - along * along;
       if (near2 > upper * upper) return;

       float outerHalf = sqrt(upper * upper - near2);
       vec2 spans[2];
       int count = 1;
       spans[0] = vec2(along - outerHalf, along + outerHalf);
       if (near2 < lower * lower)
       {
           float innerHalf = sqrt(lower * lower - near2);
           spans[1] = vec2(along + innerHalf, along + outerHalf);
           spans[0].y = along - innerHalf;
           count = 2;
       }

       float glow = 0.0;
       for (int k = 0; k < count; k++)
       {
           float t0 = max(spans[k].x, 0.0);
           float t1 = min(spans[k].y, sceneT);
           if (t1 <= t0) continue;

           const int Taps = 8;
           float dt = (t1 - t0) / float(Taps);
           for (int i = 0; i < Taps; i++)
           {
               vec3 p = dir * (t0 + (float(i) + 0.5) * dt);
               vec3 toP = p - burst;

               // Shadowed by the planet from the burst: the nearest the line from it comes to the centre.
               float s = clamp(dot(planet - burst, toP) / max(dot(toP, toP), 1.0), 0.0, 1.0);
               if (length(burst + toP * s - planet) < lower) continue;

               glow += over * over / max(dot(toP, toP), 1.0) * dt;
           }
       }

       if (glow <= 0.0) return;

       // Straight through the layer under the burst is one; red, with the green that is there first. Seen
       // edge-on along the limb a ray runs through fifty times the layer, which read as a solid band of
       // saturated red round the whole planet: the brightening is real, the fifty is not worth drawing.
       vec3 colour = mix(vec3(1.00, 0.10, 0.06), vec3(0.45, 1.00, 0.55), clamp(pc.Shape.x, 0.0, 1.0));
       float through = min(glow / max(pc.FireSun.y, 1.0), LimbGain);
       vec4 was = imageLoad(sceneColor, at);
   ```

   Source: [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp#L1619-L1680)

   The aurora (four noisy arcs standing on dipole field lines, the ray cut to a slab tilted with the field, 6 to 48 steps of at most 20 km), the debris shell (32 taps through an ellipsoid) and the red wave (chords through two spheres in closed form) follow the same pattern. All four add light and stop at the scene depth, so terrain and craft occlude them.

6. **Keep the budget.** The caps allow up to 4 aurora, 3 glow, 3 debris and 2 red-wave dispatches per frame; the author's prices at 1440x900 from orbit were glow 0.13 ms, debris 0.06-0.09 ms and aurora 2.5 ms after reworking the slab (27.9 ms before). Nothing sheds sky work automatically when the measured cost is over budget.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/NuclearClouds.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/NuclearClouds.cs) | `Begin`: air read, which kinds start (L848-L945); `TryGlow`, `TryAurora`, `TryWave` |
| [src/KSArmory/Ksa/KsaWorld.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/KsaWorld.cs) | `AirDensityRatioAt`, `HasAtmosphere`, `BodyAirOf` |
| [src/KSArmory/Sim/BodyAir.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/BodyAir.cs) | The isothermal model as numbers, `AltitudeOfColumn` |
| [src/KSArmory/Sim/XRayGlow.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/XRayGlow.cs), [src/KSArmory/Sim/Aurora.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/Aurora.cs), [src/KSArmory/Sim/RedWave.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Sim/RedWave.cs) | Layer heights, strengths, lifetimes |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs) | Gathers the kinds into push blocks (L552-L652), caps and dispatches |
| [src/KSArmory/Shaders/KSArmoryCloud.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryCloud.comp) | `DrawXRayGlow`, `DrawAurora`, `DrawDebris`, `DrawRedWave` |

## Variations and gotchas

- **The thresholds are Earth's air ratios.** Which kinds start is decided by fixed ratios; only the heights they are drawn at move with the body's column. The body-aware versions the author wrote (`BurstRegime`) are not called from the mod.
- **Several `Bodies.xml` traits are parsed and never read** (field tilt, azimuth, airglow); curtains start on any body with air whatever its field, and the dipole is always on the spin axis.
- **A failed air read is sea level.** `AirDensityRatioAt` returns 1.0 from its `catch`, so a high burst over a body whose atmosphere throws gets a dense-air mushroom and no sky.
- **Only the camera's body.** A burst over another planet draws nothing until the camera's `NearbyCelestial` is that planet, and a body that is not an `AtmosphericBody` never has the planet block filled.
- **Shared gates.** The sky shares every gate of the cloud (shader pass on, a nearby celestial, weather shadow buffers found; see [the lighting article](raymarch-a-local-volume-into-the-hdr-scene-2.md)). Only the red wave has its own switch.
- **Do not trust KSA's `IsValid()` on atmosphere distances.** `DistanceReference.IsValid` requires more than 100 km, an astronomical-scale check, so it is false for every realistic scale height; check the terms you divide by instead.
- **Airless bodies.** With no air there is no sky to light; the mod hands over to one-shot particle emitters (see [What a high or airless burst lights in the sky, chosen by the air at the burst](../../../../mods/ksarmory/features/high-altitude-burst-sky.md)).

## See also

- Siblings: [march and resolve](raymarch-a-local-volume-into-the-hdr-scene-1.md), [lighting and weather](raymarch-a-local-volume-into-the-hdr-scene-2.md), [overview](overview.md)
- [What a high or airless burst lights in the sky, chosen by the air at the burst](../../../../mods/ksarmory/features/high-altitude-burst-sky.md), [A mod compute pass recorded into KSA's frame just before bloom](../../../../mods/ksarmory/features/compute-pass-before-bloom.md), [Point gravity, body fall and one-reference air for mod-integrated bodies](../../../../mods/ksarmory/features/point-gravity-and-atmosphere-model.md)
- [Pack parameters and camera data for a compute effect](../pack-parameters-for-a-compute-effect/overview.md), [Choose a render frame and anchor effects to bodies, parts and terrain](../../../rendering/overlays/choose-a-render-frame-and-anchor-effects/overview.md), [Extend KSA's sky and cloud shaders: per-pixel haze and cloud lighting fixes](../../stock-shaders/extend-stock-sky-and-cloud-shaders/overview.md)
