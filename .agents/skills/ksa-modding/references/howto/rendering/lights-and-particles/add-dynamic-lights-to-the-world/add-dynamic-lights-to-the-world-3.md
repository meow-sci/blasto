---
type: How-To
title: Push a point light through the public LightDebug list, with a compute fill beyond 3 km
description: Light the land around a fireball by appending a Light.CreatePointLight to LightDebug.Lights, the one list the engine re-pushes after clearing its lights, and fill in the pixels the clustered light pre-pass drops past about 3 km
category: rendering
group: lights-and-particles
topic: add-dynamic-lights-to-the-world
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony, reflection]
demonstrated_by: [mods/ksarmory/features/fireball-light-injection]
techniques: [dynamic-lights, reflection, harmony-prefix]
hooks:
  - starmap StarMapAfterGui
  - harmony-prefix KSA.Program.OnFrameCelestials
  - harmony-prefix KSA.SunbloomRenderer.Render
  - reflection KSA.Rendering.Lighting.ClusteredLightSystem._diffuseIrradianceImage
lifecycle: [frame:draw-ui, frame:celestials, frame:lights, render:prepass, render:bloom]
game_types:
  - KSA.Rendering.Lighting.LightDebug
  - KSA.Rendering.Lighting.Light
  - KSA.Rendering.Lighting.ClusteredLightSystem
  - KSA.PrePassRenderer
  - KSA.SunbloomRenderer
  - KSA.Program
tags: [lights, point-light, lightdebug, prepass, compute, fireball]
---
Outcome: a bright, short-lived point light (a nuclear fireball, a flare, a flash) that lights terrain, structures and craft from your mod's own simulation, created from any frame hook without worrying about the engine clearing it. A second, optional half adds light to the pixels KSA's clustered light pre-pass skips beyond about 3.16 km.

Demonstrated by KSArmory ([Fireball light through LightDebug, filled in where the light pre-pass dropped it](../../../../mods/ksarmory/features/fireball-light-injection.md)). Prerequisites: StarMap, Harmony, reflection; the GPU half also needs the compute-pass technique of [modify the HDR scene with a compute pass](../../vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/overview.md).

## Background: how the game does it

`Program.OnFrame` clears the light system and then lets it run its per-frame work:

```csharp
// current/decomp/KSA/Program.cs L2317-L2318
LightSystem.ClearLights();
LightSystem.OnFrame(FrameViewport, dtPlayer);
```

`ClusteredLightSystem.OnFrame` copies the public static `LightDebug.Lights` list into the light system for the frame, which is why that list survives the clear:

```csharp
// current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs L1001-L1010
public void OnFrame(IViewport inViewport, double inDeltaTime)
{
	Profiler.MainThread.Begin(Profiler.Tag.ClusteredLightSystemOnFrame);
	LightDebug.MovePointLights(inViewport.GetCamera(), inDeltaTime);
	if (inViewport.LightMode == ViewportLightMode.Clustered)
	{
		LightDebug.AddToLightSystem(this, inViewport);
	}
	Profiler.MainThread.End();
}
```

```csharp
// current/decomp/KSA.Rendering.Lighting/LightDebug.cs L424-L430
public static void AddToLightSystem(ILightSystem inLightSystem, IViewport inViewport)
{
	foreach (Light light in Lights)
	{
		inLightSystem.CreateLightInstance(light, inViewport);
	}
}
```

`CreateLightInstance` puts every light in two places: the per-viewport forward list (up to 256, read by the forward loop used for terrain) and, for the main viewport only, the clustered collection the compute pre-pass shades craft, parts and static objects from ([current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/ClusteredLightSystem.cs#L897-L920)). The pre-pass loop's first test skips a light for a whole subgroup when `dotNL * rangeAtt < 1e-7`, before the intensity is applied, so beyond roughly 3.16 km x sqrt(dotNL) the light is dropped however bright it is:

```glsl
// current/Content/Core/Shaders/Lighting/LightPrePass.comp L308-L322
vec3 posToLight = lightData[lightIdx].position - worldPos;
float distSq = dot(posToLight, posToLight);
float invDist = inversesqrt(max(distSq, DIST_EPSILON));
vec3 lightDir = posToLight * invDist;

float dotNL = max(dot(N, lightDir), 0.0);

float lightRange = lightData[lightIdx].range;
float invRange = 1.0 / max(lightRange, RANGE_EPSILON);
float x2 = distSq * invRange * invRange;
float falloff = saturate(1.0 - x2 * x2);
float rangeAtt = (invDist * invDist) * mix(1.0, falloff, step(RANGE_EPSILON, lightRange));

if (subgroupAll(dotNL * rangeAtt < ATT_EPSILON))
    continue;
```

`LightDebug.Target` (a `Vehicle`) is non-null while the engine's own debug light spawner is running; `MovePointLights` then indexes parallel lists for every entry, which only the spawner fills ([current/decomp/KSA.Rendering.Lighting/LightDebug.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/LightDebug.cs#L11-L25)).

## Lifecycle and timing

| Moment | Where | What happens |
|---|---|---|
| `frame:draw-ui`, or `frame:celestials` with the HUD hidden | the mod's simulation step, from `[StarMapAfterGui]` or a prefix on `Program.OnFrameCelestials` | rewrite the `LightDebug.Lights` entry for this frame's camera |
| `frame:lights` | `LightSystem.ClearLights()` and `OnFrame` (Program.cs L2317-L2318) | engine clears, then copies `LightDebug.Lights` |
| `render:prepass` | `PrePassRenderer.Render`, `LightSystem.DispatchComputePasses` | pre-pass writes the irradiance image, culling the far fireball |
| `render:bloom` | prefix on `SunbloomRenderer.Render` (Program.cs L4915) | the compute fill writes into scene colour |

The light must be in `LightDebug.Lights` before the clear-and-copy at L2317-L2318: the AfterGui postfix is; a light created directly through `ILightSystem` would be wiped before anything renders. With the HUD hidden the step runs in the `OnFrameCelestials` prefix, which is after L2318, so that frame's light is one frame stale. The fill must run after the pre-pass and lights and before bloom so it reads the finished irradiance and blooms with the scene. See the RenderGame table in [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#render-sequence), rows 2, 4 and 16.

## Steps

1. **Light position is camera-relative.** Convert the burst centre to the camera's ego frame each frame (`KsaWorld.TryEclToCameraEgo` subtracts `(GetRenderCamera() ?? GetMainCamera()).PositionEcl`).

2. **Create the point light and append it to `LightDebug.Lights`**, clearing your previous entry first (the light is a struct, copied by value). Size it like the engine sizes an exhaust light: intensity is surface radiance times sphere area. No shadows: terrain takes this light through the forward list, which has none. Stand down if `LightDebug.Target` is set:

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

3. **Remove your entry when there is no fireball**, only the tail you added:

   ```csharp
// src/KSArmory/Ksa/Fireball.cs L66-L82
/// <summary>Drops the light, for a frame with no fireball in it.</summary>
public static void Clear()
{
    if (_lights <= 0) return;

    try
    {
        int from = LightDebug.Lights.Count - _lights;
        if (from >= 0) LightDebug.Lights.RemoveRange(from, _lights);
    }
    catch
    {
        // Somebody else has been at the list. Ours are gone either way.
    }

    _lights = 0;
}
```

4. **Call it every rendered frame**, from the simulation step the mod runs from `StarMapAfterGui` and the `OnFrameCelestials` prefix (the scene gates it, not the craft, so a cloud keeps rising after its victim is gone):

   ```csharp
// src/KSArmory/Ksa/KSArmoryMod.cs L924-L936
// A cloud belongs to the world rather than to any weapon, and it is here rather than with
// the drawing because it is a thing in the world, not a duration somebody is watching. In
// the UI pass it would stop growing whenever the UI is hidden, which is the one time a
// player is watching it and nothing else.
//
// The scene gates it, not the craft. A mushroom cloud does not stop rising because whoever
// was flying has just been killed by it.
using (_budget.Measure("clouds"))
{
    NuclearClouds.RedWaves = _config.RedWave;
    if (_config.NuclearClouds) NuclearClouds.Update(_lastSimStep);
    else NuclearClouds.Clear();
}
```

5. **Optional GPU half: read what the pre-pass wrote.** The pre-pass's vote runs per subgroup, so it cannot be repeated on the CPU. The only way to know which pixels lost the light is to read its private irradiance image by reflection, and to disable the fill (with one warning) if the field is gone:

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

6. **Dispatch the fill before bloom.** Gate on the main viewport only (the one the pre-pass runs for), valid pre-pass depth and matching extents; barrier the irradiance image from KSA's fragment read to a compute read through `BarrierBatch`; push the light as KSA was handed it; dispatch `ceil(W/16) x ceil(H/8)`:

   ```csharp
// src/KSArmory/Ksa/CloudPass.cs L1799-L1844
private static void FireLight(CommandBuffer commandBuffer, IViewport viewport, Camera camera, View view,
                              IRenderImage depth)
{
    if (!Fireball.TryPushed(out double3 lightEgo, out float range, out float3 radiant)) return;
    if (!ReferenceEquals(viewport, Program.MainViewport)) return;
    if (Program.Instance?.PrePassRenderer is not { OpaqueHasValidDepth: true } prePass) return;
    if (prePass.OpaquePrePassData.Target is not { } target) return;
    if (target.ColorImage is not { } normal || target.DepthImage is not { } prePassDepth) return;
    if (target.Extent.Width != view.Width || target.Extent.Height != view.Height) return;
    if (!TryPrePassIrradiance(out RenderImage? irradiance)) return;

    if (view.FireLight is null
        || !view.FireLightDepth.Equals(prePassDepth.ImageView)
        || !view.FireLightNormal.Equals(normal.ImageView)
        || !view.FireLightIrradiance.Equals(irradiance!.ImageView))
    {
        if (!BuildFireLight(view, depth, prePassDepth, normal, irradiance!)) return;
    }

    FireLightPush push = new()
    {
        InvViewProj = camera.VPInv.viewProjection,
        LightRange = new float4((float)lightEgo.X, (float)lightEgo.Y, (float)lightEgo.Z, range),
        Radiant = new float4(radiant.X, radiant.Y, radiant.Z, (float)MeanAlbedo(camera.NearbyCelestial)),
        Cce2Ccf = Cce2CcfQuaternion(camera.NearbyCelestial),
        GroundMap = GroundMapFor(camera.NearbyCelestial),
    };

    using (commandBuffer.TagRegion(GpuTag))
    using (commandBuffer.TagRegion(FireLightTag))
    {
        // From the fragment read KSA left it in to a compute read, through KSA's own tracked
        // state, so the engine barriers it back next frame from wherever this left it.
        Span<VkImageMemoryBarrier2> one = stackalloc VkImageMemoryBarrier2[1];
        BarrierBatch toSample = new(one);
        toSample.Add(irradiance!, ImageBarrierInfo.Presets.SampledReadC);
        toSample.SubmitAndFlush(commandBuffer);

        Span<VkDescriptorSet> sets = stackalloc VkDescriptorSet[1];
        sets[0] = Program.Instance.TextureSystem.DescriptorSet;
        view.FireLight!.BindPipeline(commandBuffer, viewport.ShaderSlot, sets, default, push);
        commandBuffer.Dispatch((view.Width + FireLightGroupX - 1) / FireLightGroupX,
                               (view.Height + FireLightGroupY - 1) / FireLightGroupY, 1);
        Hazard(commandBuffer);
    }
}
```

   It is hooked as a prefix on `SunbloomRenderer.Render(CommandBuffer, IViewport, int)`:

   ```csharp
// src/KSArmory/Ksa/CloudPassHook.cs L61-L86
    try
    {
        MethodInfo? target = typeof(SunbloomRenderer).GetMethod(
            nameof(SunbloomRenderer.Render), [typeof(CommandBuffer), typeof(IViewport), typeof(int)]);

        if (target is null)
        {
            Log.Warn("KSA has no SunbloomRenderer.Render(CommandBuffer, IViewport, int) to hook; "
                     + "this mod's own shader pass will not draw");
            return;
        }

        MethodInfo prefix = typeof(CloudPassHook).GetMethod(
            nameof(BeforeSunbloom), BindingFlags.NonPublic | BindingFlags.Static)!;

        _harmony = new Harmony(HarmonyId);
        _harmony.Patch(target, prefix: new HarmonyMethod(prefix));

        Installed = true;
        Log.Info("the mod's own shader pass runs before bloom, via SunbloomRenderer.Render");
    }
    catch (Exception e)
    {
        Log.Warn($"could not hook the shader pass: {e.Message}; it will not draw");
    }
}
```

7. **In the shader, add the forward loop's diffuse term only where the pre-pass dropped it.** The pixel must be an object the eye sees (pre-pass depth matches scene depth), and the pre-pass irradiance must hold less than half the fireball's share; otherwise nothing is lit twice:

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

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/Fireball.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Fireball.cs) | Light sizing, `LightDebug` push and clear, `TryPushed` for the GPU half |
| [src/KSArmory/Ksa/CloudPass.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPass.cs#L1796-L1990) | Gates, barrier, dispatch, reflection into the irradiance image, pipeline build |
| [src/KSArmory/Ksa/CloudPassHook.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/CloudPassHook.cs) | Prefix on `SunbloomRenderer.Render` |
| [src/KSArmory/Shaders/KSArmoryFireLight.comp](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Shaders/KSArmoryFireLight.comp) | Per-pixel kept or dropped test and the diffuse fill |
| [docs/BLOCKED-ON-KSA.md](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/docs/BLOCKED-ON-KSA.md#L762-L798) | The author's account of the pre-pass cut and what would unblock it |

## Variations and gotchas

- **`LightDebug` is a debug class.** It is the only public list the engine re-reads after `ClearLights`, but the moment the engine's light spawner is used (`Target` set) the list belongs to it. Check `Target` every frame and never touch the list while it is set.
- **One light, last writer wins.** The mod clears its previous entry before adding, so several bursts at once light only from one ball.
- **Main viewport only.** `CreateLightInstance` adds to the clustered collection only for the main viewport, and `OnFrame` pushes the debug list only for a `Clustered` viewport; camera windows get neither the clustered light nor the fill.
- **Reflection degrades to dark, never to a guess.** A renamed `_diffuseIrradianceImage` disables the fill for the session.
- **What would remove the fill:** multiplying `intensity` into the pre-pass early-out; the author notes it as an engine change request.
- **Do not copy a light that must cast shadows this way.** The point light is created with `ELightFlags.None`.

## See also

- [Overview](overview.md)
- [Approach 1](add-dynamic-lights-to-the-world-1.md), [approach 2](add-dynamic-lights-to-the-world-2.md)
- [Fireball light through LightDebug, filled in where the light pre-pass dropped it](../../../../mods/ksarmory/features/fireball-light-injection.md)
- [Modify the HDR scene with a compute pass](../../vulkan-passes/modify-the-hdr-scene-with-a-compute-pass/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
