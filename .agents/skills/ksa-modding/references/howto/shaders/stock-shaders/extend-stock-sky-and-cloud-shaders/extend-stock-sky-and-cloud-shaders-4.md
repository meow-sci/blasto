---
type: How-To
title: "Reuse stock helpers in a cloud shader: include RingShadows.glsl and select mips without derivatives"
description: Add the rings' shadow to both cloud passes by including the game's RingShadows.glsl from the patched copy and picking the texture mip in compute from the ring coordinate's change across one pixel
category: shaders
group: stock-shaders
topic: extend-stock-sky-and-cloud-shaders
approach: 4
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/realatmospheres/features/cloud-pass-shading-and-ring-shadows]
techniques: [glsl-shader]
hooks: [starmap StarMapBeforeMain]
lifecycle: [startup:starmap-before-main, render:atmosphere, render:planet]
game_types: [KSA.AtmosphereRenderer, KSA.Atmosphere.Rendering.CloudRenderer]
tags: [glsl, clouds, ring-shadows, include, compute, mip-selection, realatmospheres]
---
KSA 2026.9.22 shades the atmosphere and the surface under a planet's rings, but not its clouds, so an opaque cloud deck (RealAtmospheres' Saturn) hides the shadow. The fix reuses what the game already provides: `RingShadows.glsl` and the ring data already in the atmosphere uniform buffer both cloud passes bind. This article shows how to include a stock helper from a
patched copy and how to choose a texture mip in a compute shader, which has no screen-space derivatives. Needs a text-patched shader tree ([Replace or text-patch the game's GLSL without editing game files](../replace-or-patch-game-glsl-without-editing-game-files/overview.md)).

## Background: how the game does it

- **The helper.** `RingShadows.glsl` already provides `TraceToRingPlane` and `GetRingShadowTransmittance`, used by the atmosphere and planet shaders ([current/Content/Core/Shaders/Common/RingShadows.glsl](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Common/RingShadows.glsl#L22-L70)).
- **The data.** `AtmosphereDataUbo.glsl` carries `useRingShadows`, `ringTextureId`, `ringSamplerId`, `ringNormalAndInnerRadius` and `ringOuterRadius`; `FillPlanetAtmosphereData` fills them only for bodies with a `RingsReference` ([current/decomp/KSA/AtmosphereRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AtmosphereRenderer.cs#L1154-L1162)).
- **Compute has no derivatives.** The cloud passes are compute shaders; `texture()` mip selection by `dFdx` is not available, so the mip must be computed.

## Lifecycle and timing

| Step | When | Value |
|---|---|---|
| `PatchCloudRingShadows` edits `2DCloud.comp` and `RaymarchCloud.comp` in the shadow copy | `[StarMapBeforeMain]` | `startup:starmap-before-main` |
| Both cloud passes run | `PlanetTransparenciesRenderer` after the atmosphere | `render:atmosphere` |

## Steps

1. **Include the stock helper with a plain relative path.** Because the whole shader tree is copied, `../Common/RingShadows.glsl` resolves from `Clouds/` exactly as in the game: includes follow the requesting file's directory. The patch inserts the function (and the include) before `main()`:

   ```csharp
   // Payloads.cs L3141-L3159
       public const string CloudRingShadowMarker = "RaCloudRingShadow(";
       public const string CloudMainAnchor = "void main()\n{";

       // inserted before main(); needs atmosphereDataUbo and the bindless set, which both passes have
       public static readonly string CloudRingShadowFunc =
   """
   // ---- Real Atmospheres: the rings' shadow on clouds ----
   // Same ring texture and transmittance as Atmosphere.comp. A compute shader has no derivatives,
   // so the mip comes from how far the ring coordinate moves across one pixel, traced at two
   // neighbouring points.
   #include "../Common/RingShadows.glsl"

   float RaRingCoord(vec3 position, vec3 planetPosition, vec3 directionToSun, vec3 ringNormal,
       float innerRadius, float invWidth)
   {
       vec3 planetToHit;
       TraceToRingPlane(position, planetPosition, directionToSun, ringNormal, planetToHit);
       return (length(planetToHit) - innerRadius) * invWidth;
   }
   ```

   Source: [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L3141-L3159).

2. **Compute the mip from neighbouring pixels.** Trace two points one pixel apart along the camera's right and up axes, measure how far the ring coordinate moves in texels, and pass `log2` of that as the mip. A planet without rings returns 1.0:

   ```glsl
   // Payloads.cs L3162-L3188 (GLSL inside a C# raw string)
   float RaCloudRingShadow(vec3 position)
   {
       if (atmosphereDataUbo.useRingShadows == 0)
           return 1.0;

       vec3 planetPosition = atmosphereDataUbo.planetPositionAndPackedScreenStartCoords.xyz;
       vec3 directionToSun = normalize(global.lighting.sunPosition.xyz - planetPosition);
       vec3 ringNormal = atmosphereDataUbo.ringNormalAndInnerRadius.xyz;
       float innerRadius = atmosphereDataUbo.ringNormalAndInnerRadius.w;
       float outerRadius = atmosphereDataUbo.ringOuterRadius;
       float invWidth = 1.0 / max(outerRadius - innerRadius, 1.0);

       // One pixel at this distance, along the camera's right and up axes
       float pixel = 2.0 * length(position) / (abs(global.camera.projection[1][1]) * float(global.camera.screenHeight));
       vec3 right = global.camera.inverseView[0].xyz;
       vec3 up = global.camera.inverseView[1].xyz;

       float coord = RaRingCoord(position, planetPosition, directionToSun, ringNormal, innerRadius, invWidth);
       float stepX = RaRingCoord(position + right * pixel, planetPosition, directionToSun, ringNormal, innerRadius, invWidth) - coord;
       float stepY = RaRingCoord(position + up * pixel, planetPosition, directionToSun, ringNormal, innerRadius, invWidth) - coord;
       float texels = max(abs(stepX), abs(stepY)) * float(textureSize(sampler2D(
           globalTextures[atmosphereDataUbo.ringTextureId], samplers[atmosphereDataUbo.ringSamplerId]), 0).x);

       return GetRingShadowTransmittance(globalTextures[atmosphereDataUbo.ringTextureId],
           samplers[atmosphereDataUbo.ringSamplerId], position, planetPosition, directionToSun, ringNormal,
           innerRadius, outerRadius, log2(max(texels, 1.0)));
   }
   ```

   Source: [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L3162-L3188).

3. **Apply it where the stock applies the eclipse shadow.** The sites are the stock eclipse lines followed by the new multiply:

   ```csharp
   // Payloads.cs L3191-L3207
       public static readonly (string File, string Stock, string Patched)[] CloudRingShadowSites =
       {
           ("2DCloud.comp",
            "        float eclipseShadow = getCelestialShadow(cloudPosition, global.lighting.sunPosition);\n"
            + "        color.rgb *= eclipseShadow;",
            "        float eclipseShadow = getCelestialShadow(cloudPosition, global.lighting.sunPosition);\n"
            + "        color.rgb *= eclipseShadow;\n"
            + "        color.rgb *= RaCloudRingShadow(cloudPosition);  // Real Atmospheres: the rings' shadow"),
           ("RaymarchCloud.comp",
            "        float eclipseShadow = getCelestialShadow(averageCloudPosition, global.lighting.sunPosition);\n"
            + "        cloudColor.rgb *= eclipseShadow;",
            "        float eclipseShadow = getCelestialShadow(averageCloudPosition, global.lighting.sunPosition);\n"
            + "        cloudColor.rgb *= eclipseShadow;\n"
            + "        cloudColor.rgb *= RaCloudRingShadow(averageCloudPosition);  // Real Atmospheres: the rings' shadow"),
       };
   ```

   Source: [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs#L3191-L3207).

4. **Be a good citizen: only when supported, never twice.** Skip the patch if `RingShadows.glsl` or `ringTextureId` is missing in this build; per file, skip if the game already mentions `RingShadow` or `useRingShadows` there; require both anchors exactly once:

   ```csharp
   // ShadowBuilder.cs L714-L746
       private static void PatchCloudRingShadows(string shadersDir)
       {
           string uboPath = Path.Combine(shadersDir, "Atmosphere", "AtmosphereDataUbo.glsl");
           if (!File.Exists(Path.Combine(shadersDir, "Common", "RingShadows.glsl"))
               || !File.Exists(uboPath) || !ReadLf(uboPath).Contains("ringTextureId"))
           {
               Log("cloud ring shadows: this game build has none to add to");
               return;
           }

           int applied = 0, byGame = 0;
           foreach (var (file, stock, patched) in Payloads.CloudRingShadowSites)
           {
               string path = Path.Combine(shadersDir, "Clouds", file);
               if (!File.Exists(path)) { Log($"WARN: {file} missing, skipping its ring shadows"); continue; }
               string s = ReadLf(path);
               if (s.Contains(Payloads.CloudRingShadowMarker)) { applied++; continue; }

               // A game build that shadows its clouds itself would get the shadow twice
               if (s.Contains("RingShadow") || s.Contains("useRingShadows")) { byGame++; continue; }

               if (CountOf(s, Payloads.CloudMainAnchor) != 1 || CountOf(s, stock) != 1)
               { Log($"WARN: {file} main() or eclipse anchor not found, skipping its ring shadows"); continue; }

               s = s.Replace(Payloads.CloudMainAnchor,
                   Payloads.CloudRingShadowFunc.Trim('\n') + "\n\n" + Payloads.CloudMainAnchor);
               s = s.Replace(stock, patched);
               File.WriteAllText(path, s);
               applied++;
           }
           Log($"cloud ring shadows applied ({applied}/{Payloads.CloudRingShadowSites.Length}"
               + (byGame > 0 ? $", {byGame} already shadowed by the game" : "") + ")");
       }
   ```

   Source: [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs#L714-L746).

## Complete example

| File | Role |
|---|---|
| [Payloads.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/Payloads.cs) | Ring-shadow function and sites |
| [ShadowBuilder.cs](https://github.com/Brimfyr/RealAtmospheres/blob/19dc37a55b2e359bd8eec2f5b41722ab11ae0c46/ShadowBuilder.cs) | `PatchCloudRingShadows` |

## Variations and gotchas

- **Interplay with the side channel.** The mod also uses the ring slots for its haze turn, but only on ring-less bodies and with `useRingShadows == 0`, so this patch's early return keeps the two apart ([Smuggle data into stock shaders through unused uniforms, state bits and sentinel values](../send-extra-data-to-stock-shaders-without-new-uniforms/overview.md)).
- **Other small cloud edits in the same file set:** a terminator fade of the 2D shell, skylight tinted by map colour, Triton's thin shell and Venus's opaque shell, and a thin-cloud ground shadow clamp in `CloudShadows.glsl`; each is applied or skipped on its own and identifies a body-specific layer by its radii.
- **Radii as identifiers** follow from the mod's own XML; change the XML and these patches stop matching.
- **A later game build that shades clouds itself is detected and left alone.**
- **Include path relative to the copy.** The include works because the tree is copied; with an in-memory patch ([Patch vehicle paint and canopy shaders in memory at compile time](../patch-game-shaders-in-memory-at-compile-time/overview.md)) the debug path passed to the compiler must still point into the original folder.

## See also

- Siblings: [sky march](extend-stock-sky-and-cloud-shaders-1.md), [face lighting](extend-stock-sky-and-cloud-shaders-2.md), [map flow and lift](extend-stock-sky-and-cloud-shaders-3.md), [overview](overview.md)
- [Cloud pass shading and ring shadows](../../../../mods/realatmospheres/features/cloud-pass-shading-and-ring-shadows.md)
- [Include KSA's own GLSL library from a mod shader](../../glsl-workflow/include-the-game-shader-library-in-mod-glsl/overview.md), [Replace or text-patch the game's GLSL without editing game files](../replace-or-patch-game-glsl-without-editing-game-files/overview.md)
