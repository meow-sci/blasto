---
type: How-To
title: "Branch per body in shared shaders: a relief-aware terminator closing over a sqrt(2 relief / R) band"
description: Replace the smooth-ball self-shadow in the planet and ground-clutter fragment shaders with a horizon test from each point's own height while keeping other bodies' eclipses
category: shaders
group: stock-shaders
topic: branch-per-body-inside-shared-game-shaders
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/realatmospheres/features/relief-softened-terminator]
techniques: [glsl-shader]
hooks: [starmap StarMapBeforeMain]
lifecycle: [startup:starmap-before-main, render:planet]
game_types: [KSA.PlanetRenderer]
tags: [glsl, terminator, shadow, planet, relief, per-body, ground-clutter, realatmospheres]
---
KSA shades a body's shadow on its own terrain as if the body were a smooth ball: sunlight transmittance stops at the mean sphere's horizon inside a fixed band and the body eclipses itself as a 0.999 R sphere. On an airless or thin-air body that makes the terminator a single line from lit to black.
Over real ground the Sun stays visible past the ball's horizon wherever the terrain toward it is lower, by up to `sqrt(2 relief / R)`. RealAtmospheres patches `Planet.frag` and `GroundClutter/Solid.frag` to use that band. The lesson for shared shaders: the per-point radius and the body's
relief are already in the shader, and `celestialIndex` bounded by `bodyCount` tells you whether the body is known. You need a text-patched shader tree ([Replace or text-patch the game's GLSL without editing game files](../replace-or-patch-game-glsl-without-editing-game-files/overview.md)).

## Background: how the game does it

- **Inputs already bound.** `uboPlanet.heightScale` is the body's `SurfaceMaxHeight` and `uboPlanet.celestialIndex` its index in the global body list ([current/decomp/KSA/PlanetRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetRenderer.cs#L724-L730), [current/Content/Core/Shaders/Common/Planet.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/Planet.glsl#L44-L50));
  `global.celestial.bodies[i]` holds each body's centre and radius ([current/Content/Core/Shaders/Common/Global.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/Global.glsl#L75-L82)).
- **Where the smooth-ball shadow is.** `Planet.frag` computes `selfShadowFactor = getSelfCelestialShadow(...)` and multiplies the prefiltered ambient by it; the sunlight transmittance goes through `GetTransmittanceFromLut`, whose result the LUT code softens with `ApplyPlanetSoftShadow`
  ([current/Content/Core/Shaders/Planet/Planet.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Planet/Planet.frag#L296-L322), [current/Content/Core/Shaders/Atmosphere/AtmosphereLuts.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Atmosphere/AtmosphereLuts.glsl#L64-L89)). `getSelfCelestialShadow` also applies every other body's eclipse
  ([current/Content/Core/Shaders/Common/CelestialShadows.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/CelestialShadows.glsl#L7-L121)).
- **Two shaders draw terrain:** the planet and the ground clutter (rocks); the second asks for body index 1 in its stock call and has its own `pushConsts.celestialIndex`.

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| `PatchTerrainShadow` groups the sites by file and patches each file whole or not at all | `[StarMapBeforeMain]`, on the shadow copy | `startup:starmap-before-main` |
| Shaders run in the planet colour pass (stock ids `PlanetFrag` and `ClutterSolidFrag`) | Recorded per frame | `render:planet` |

See the [render sequence of the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence) for where `planet` sits in the interleaved render order.

## Steps

1. **Write the own-shadow function.** Sun elevation at the point, the dip of the horizon of a ball of `limb * R` seen from the point's own radius, and a band of `clamp(sqrt(2 relief / R), 0.01, 0.1)` never narrower than the Sun's angular diameter; the result is `smoothstep(-band, 0, elevation + dip)`:

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

   Source: [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2737-L2752). `RaOwnShadowKnown` is `celestialIndex >= 0 && celestialIndex < global.celestial.bodyCount`; an unknown body returns 1.0, and `RaOtherShadows` and `RaTransmittanceToSun` then fall back to what the game would have computed.

2. **Keep what the game does for other bodies.** `RaOtherShadows` is the stock `getSelfCelestialShadow` minus the body's eclipse of itself: the game's ramp on the sphere normal plus every other body's `applyCelestialShadow`:

   ```glsl
   // Payloads.cs L2756-L2775 (GLSL inside a C# raw string)
   float RaOtherShadows(vec3 worldPosition, vec4 sunPosition, vec3 inNormal, int myCelestialIndex)
   {
       if (!RaOwnShadowKnown(myCelestialIndex))
           return getSelfCelestialShadow(worldPosition, sunPosition, inNormal, myCelestialIndex);
       float shadowFactor = 1.0;
       vec3 bodyPos = global.celestial.bodies[myCelestialIndex].xyz;
       vec3 fragToBody = bodyPos - worldPosition;
       vec3 bodyToSunVec = sunPosition.xyz - bodyPos;
       if (dot(fragToBody, fragToBody) < dot(bodyToSunVec, bodyToSunVec))
           shadowFactor *= smoothstep(0.0, 0.2, dot(inNormal, normalize(bodyToSunVec)) + 0.2);
       for (int i = 0; i < global.celestial.bodyCount; i++)
       {
           if (i == myCelestialIndex)
               continue;
           shadowFactor *= applyCelestialShadow(worldPosition, sunPosition, i);
           if (shadowFactor <= EPSILON)
               break;
       }
       return shadowFactor;
   }
   ```

   Source: [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2756-L2775).

3. **Edit the shaders at exact anchors.** Insert the functions after the `CelestialShadows.glsl` include, replace the self-shadow factor, replace the transmittance lookup, and give ambient light its own factor (sky light keeps the game's cut-off point with `limb = 0.999`). One of the eight sites:

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

   Source: [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L2791-L2826) lists all eight sites. For the clutter shader the stock call asks for body index `1`; the patch passes the clutter's own `pushConsts.celestialIndex`.

4. **Apply the sites per file, all or nothing.** Group the sites by file; if any anchor in a file is not found exactly once, log and leave that file stock:

   ```csharp
   // ShadowBuilder.cs L839-L861
       private static void PatchTerrainShadow(string shadersDir)
       {
           int done = 0, files = 0;
           foreach (var group in Payloads.TerrainShadowSites.GroupBy(site => site.File))
           {
               files++;
               string path = Path.Combine(shadersDir, group.Key.Replace('/', Path.DirectorySeparatorChar));
               if (!File.Exists(path)) { Log($"WARN: {group.Key} missing, its terminator stays the game's"); continue; }
               string s = ReadLf(path);
               if (s.Contains(Payloads.TerrainShadowMarker)) { done++; continue; } // already applied
               var off = group.Where(site => CountOf(s, site.Stock) != 1).ToList();
               if (off.Count > 0)
               {
                   Log($"WARN: {group.Key} anchor found {CountOf(s, off[0].Stock)}x (expected 1), its terminator stays the game's: {Truncate(off[0].Stock)}");
                   continue;
               }
               foreach (var site in group)
                   s = s.Replace(site.Stock, site.Patch);
               File.WriteAllText(path, s);
               done++;
           }
           Log($"terrain terminator softened by relief ({done}/{files} files)");
       }
   ```

   Source: [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L839-L861).

## Complete example

| File | Role |
|---|---|
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs) | `RaOwnShadow`, `RaOtherShadows`, `RaTransmittanceToSun`, the eight sites |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs) | `PatchTerrainShadow` |

## Variations and gotchas

- **Relief is the height map's maximum, taken as deep as it is high.** Bodies with one tall feature get a wide band everywhere; the clamp to 0.1 rad (5.7 degrees) bounds it.
- **Two shaders must agree.** Terrain and rocks are patched separately; each file is all-or-nothing, but one could apply while the other does not after a game update.
- **Not atmosphere-specific.** This changes airless bodies (the Moon, Mercury) too, which is where it matters most.
- **Unknown body, stock behaviour.** If `celestialIndex` is outside `bodyCount`, every function returns what the game would have computed.
- **Sky light keeps the game's cut-off** (`limb = 0.999`) and closes over the same band.

## See also

- Siblings: [density tables](branch-per-body-inside-shared-game-shaders-1.md), [overview](overview.md)
- [Relief-softened terminator](../../../../mods/realatmospheres/features/relief-softened-terminator.md)
- [Extend KSA's sky and cloud shaders: per-pixel haze and cloud lighting fixes](../extend-stock-sky-and-cloud-shaders/overview.md), [Compile-check a mod's GLSL without launching the game](../../glsl-workflow/check-mod-shaders-offline-with-glslang/overview.md)
