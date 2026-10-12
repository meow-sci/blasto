---
type: How-To
title: Add a fireball's diffuse term where the pre-pass culled it beyond about 3 km
description: Push one point light through the public LightDebug list and fill in the far range with a compute dispatch that reads the pre-pass irradiance by reflection
category: rendering
group: vulkan-passes
topic: modify-the-hdr-scene-with-a-compute-pass
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/ksarmory/features/fireball-light-injection]
techniques: [reflection, vulkan-pipeline, harmony-prefix]
hooks:
  - starmap StarMapAfterGui
  - harmony-prefix KSA.Program.OnFrameCelestials
  - harmony-prefix KSA.SunbloomRenderer.Render
  - reflection KSA.Rendering.Lighting.ClusteredLightSystem._diffuseIrradianceImage
lifecycle: [frame:draw-ui, frame:celestials, frame:lights, render:prepass, render:bloom]
game_types:
  - KSA.Rendering.Lighting.LightDebug
  - KSA.Rendering.Lighting.ClusteredLightSystem
  - KSA.PrePassRenderer
  - KSA.PrePassData
  - KSA.SunbloomRenderer
tags: [compute, light, fireball, lightdebug, prepass, reflection]
---

Outcome: a light so bright it should light craft and structures kilometres away actually does. KSA's clustered light pre-pass drops any
light from craft and structures beyond about 3.16 km before multiplying in its intensity, so a 50 kt fireball lit the grass and left the pad black.
The fix has a CPU half (push a real point light into the engine) and a GPU half (a compute dispatch that adds the forward loop's diffuse term only on pixels the
pre-pass dropped). Demonstrated by KSArmory ([Fireball light through LightDebug, filled in where the light pre-pass dropped it](../../../../mods/ksarmory/features/fireball-light-injection.md)). Builds on approach 1 (the dispatch) and
approach 2 (reading the pre-pass image by reflection).

## Background: how the game does it

`LightDebug` is a public static class whose `Lights` list [current/decomp/KSA.Rendering.Lighting/LightDebug.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/LightDebug.cs#L11) is the only light list the engine re-reads after `ClearLights`. `LightDebug.Target`
[current/decomp/KSA.Rendering.Lighting/LightDebug.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/LightDebug.cs#L25) is non-null while the engine's own debug spawner owns the list. `ClusteredLightSystem.CreateLightInstance` puts every light in two
places: the per-viewport forward list used for terrain, and, for the main viewport only, the clustered collection the pre-pass shades craft, parts and static objects from. In the
pre-pass loop the first test is `subgroupAll(dotNL * rangeAtt < ATT_EPSILON)` with `ATT_EPSILON = 1e-7` and `rangeAtt = falloff / d^2`, evaluated before intensity is applied
[current/Content/Core/Shaders/Lighting/LightPrePass.comp](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Lighting/LightPrePass.comp#L308-L322), so beyond roughly 3 km the light is skipped however bright it is.

## Lifecycle and timing

| Moment | Where | What happens |
|---|---|---|
| `frame:draw-ui` (or `frame:celestials` with the HUD hidden) | simulation tail from `[StarMapAfterGui]` or the `OnFrameCelestials` prefix | `Fireball.Draw` rewrites the `LightDebug.Lights` entry for this frame's camera |
| `frame:lights` | `LightSystem.ClearLights` / `OnFrame` in `Program.OnFrame` | Engine clears, then copies `LightDebug.Lights` into its lists |
| `render:prepass` | `PrePassRenderer.Render`, `LightSystem.DispatchComputePasses` in `RenderGame` | Pre-pass writes the irradiance image, culling the far fireball |
| `render:bloom` | prefix on `SunbloomRenderer.Render` | The fill writes into the scene colour |

The light must be in `LightDebug.Lights` before the engine's clear; a light created directly through `ILightSystem` would be wiped before anything renders. See
[the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for the order of `frame:lights`.

## Steps

1. **CPU: push one point light.** Size it the way the engine sizes an exhaust light (radiance times the sphere's area), use a camera-relative (Ego) position, no shadows
   (the terrain takes this light through the forward list, which has none), and stand down if `LightDebug.Target` is set:

   ```csharp
   // src/KSArmory/Ksa/Fireball.cs L84-L121
   // Through LightDebug's list rather than the light system directly, and that is the only route
   // that works: the system clears its lights after the mod's hook and re-pushes this list, so a
   // light created directly is wiped before anything renders.
   private static void Illuminate(double3 centreEgo, double radiusMetres, float3 colour, float glow)
   {
       Clear();

       // Stand down entirely if the engine's own light spawner is live. MovePointLights walks
       // this list against parallel lists only that spawner fills, so an entry of ours is
       // rewritten at best and throws out of the frame loop at worst -- and its target is never
       // cleared once set.
       if (LightDebug.Target is not null)
       {
           if (!_stoodDown)
           {
               _stoodDown = true;
               Log.Warn("fireball light stood down: KSA's own light spawner has the list. Bursts "
                        + "will glow but will not light anything.");
           }

           return;
       }

       // The engine sizes an exhaust light this way: a uniformly emitting sphere puts out its own
       // surface radiance over its whole area, so intensity is that times the area.
       float radiance = AlbedoLinear * glow;
       float intensity = radiance * 4f * MathF.PI * (float)(radiusMetres * radiusMetres);

       // Out to where a burst is watched from, so the land round the watcher is lit by it; the
       // falloff has made it faint by then. No shadows: the terrain takes this light through KSA's
       // forward list, which has none, so a shadow map would cost a cube of the whole landscape a
       // frame for nothing on the ground.
       _pushed = Light.CreatePointLight(
           centreEgo, (float)(radiusMetres * RangeInRadii), colour, intensity, ELightFlags.None);
       LightDebug.Lights.Add(_pushed);

       _lights = 1;
   }
   ```

2. **GPU: read what the pre-pass wrote.** The diffuse irradiance image is private; read it by reflection once, warn once on failure and turn the fill off rather than guessing:

   ```csharp
   // src/KSArmory/Ksa/CloudPass.cs L1928-L1952
   private static bool TryPrePassIrradiance(out RenderImage? image)
   {
       image = null;
       if (_irradianceMissing) return false;

       try
       {
           if (Program.LightSystem is not ClusteredLightSystem lights) return false;
           _irradianceField ??= typeof(ClusteredLightSystem).GetField(
               "_diffuseIrradianceImage", BindingFlags.NonPublic | BindingFlags.Instance);
           image = _irradianceField?.GetValue(lights) as RenderImage;
       }
       catch
       {
           image = null;
       }

       if (image is null)
       {
           _irradianceMissing = true;
           Warn("KSA's light pre-pass result could not be read; a fireball will not light craft beyond 3 km");
       }

       return image is not null;
   }
   ```

3. **Dispatch before bloom** from the same `SunbloomRenderer.Render` prefix (approach 1): storage colour, depth samplers for scene depth and pre-pass depth, read-only views of the
   pre-pass normal and irradiance, KSA's bindless set as external set 2; dispatch `ceil(W/16) x ceil(H/8)` to match `local_size`.

4. **In the shader**, restrict the work to where the pre-pass dropped the light:

   ```glsl
   // src/KSArmory/Shaders/KSArmoryFireLight.comp L90-L105
   float raw = texelFetch(prePassDepth, pixel, 0).r;
   if (raw <= 0.0) return;

   // Only where the object is what the eye sees, rather than terrain standing in front of it.
   float scene = texelFetch(sceneDepth, pixel, 0).r;
   if (abs(scene - raw) > 1e-3 * raw) return;

   vec2 uv = (vec2(pixel) + 0.5) / vec2(size);
   vec3 p = WorldAt(uv, raw);
   vec3 n = normalize(texelFetch(prePassNormal, pixel, 0).xyz);

   vec3 irradiance = FireIrradiance(p, n);
   if (Luminance(irradiance) <= 0.0) return;

   // KSA's own saturates to infinity under the ball, and infinity counts as kept.
   if (Luminance(texelFetch(prePassIrradiance, pixel, 0).rgb) >= KeptShare * Luminance(irradiance)) return;
   ```

   Test the pre-pass depth against the scene depth (only meshes the eye sees), compute the forward loop's own attenuation so the fill matches how terrain was lit, skip pixels whose
   pre-pass irradiance is already at least half the fireball's, recover albedo by dividing out the direct sun term (or from the planet's colour cube where the sun does not reach), and
   write `scene + irradiance x albedo / pi`.

5. Gate it: main viewport only, `PrePassRenderer.OpaqueHasValidDepth`, pre-pass target extent equals the scene's, irradiance image readable.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/Fireball.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Fireball.cs) | The light pushed through `LightDebug.Lights` |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L1928-L1952) | Reflection onto the pre-pass irradiance |
| [src/KSArmory/Shaders/KSArmoryFireLight.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryFireLight.comp) | The fill |

For lights in general see [Add dynamic spot and point lights to the world from a mod](../../lights-and-particles/add-dynamic-lights-to-the-world/overview.md).

## Variations and gotchas

- **`LightDebug` is a debug class.** If `LightDebug.Target` is set the list belongs to the engine's spawner, and a foreign entry is overwritten or throws; check every frame and never touch it while set.
- **One light, last writer wins.** Several bursts at once light only from one ball.
- **Main viewport only.** Camera windows get neither the clustered light nor the fill.
- **Reflection degrades to dark, never to a guess.** A renamed private field disables the fill for the session.
- **HUD hidden.** The step then runs in the `OnFrameCelestials` prefix, after the engine's light update, so that frame's light is one frame stale.
- The author notes the engine fix would be to multiply `intensity` into the pre-pass early-out.

## See also

- [Overview](overview.md)
- [Approach 1: depth-reading compute dispatches](modify-the-hdr-scene-with-a-compute-pass-1.md)
- [Approach 2: in-place rescale](modify-the-hdr-scene-with-a-compute-pass-2.md)
- [Fireball light through LightDebug, filled in where the light pre-pass dropped it](../../../../mods/ksarmory/features/fireball-light-injection.md)
- [Add dynamic spot and point lights to the world from a mod](../../lights-and-particles/add-dynamic-lights-to-the-world/overview.md), [Insert a custom Vulkan pass into KSA's frame](../insert-a-custom-pass-into-render-game/overview.md)
