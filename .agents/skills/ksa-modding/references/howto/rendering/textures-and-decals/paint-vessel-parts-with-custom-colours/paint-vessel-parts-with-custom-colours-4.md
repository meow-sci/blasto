---
type: How-To
title: Prefix on PartModelDynamic.AddInstance rewrites Temperature and TfiThickness
description: Override engine heat glow by rewriting the Temperature and TfiThickness fields of the instance data in a prefix on the private PartModelDynamic.AddInstance sink
category: rendering
group: textures-and-decals
topic: paint-vessel-parts-with-custom-colours
approach: 4
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/humble-arteest-engine-emissive]
techniques: [harmony-prefix]
hooks:
  - starmap StarMapAllModsLoaded
  - harmony-prefix KSA.PartModelDynamic.AddInstance
  - starmap StarMapAfterGui
lifecycle: [startup:all-mods-loaded, frame:draw-ui, frame:render]
game_types: [KSA.PartModelDynamic, KSA.PartTreeRenderData, KSA.PartModelRenderer]
tags: [emissive, engine, heat, temperature, partmodeldynamic, harmony-prefix]
---

Outcome: engines glow on demand: a global heat and thin-film value for every dynamic part model, or per-model values picked from a list, applied in flight without waiting for real engine heat. The game already renders engine heat from a per-instance `Temperature` float and thin-film tint from `TfiThickness`; a prefix on the private submission method `PartModelDynamic.AddInstance` overwrites those two fields in the `PerInstanceData` being appended, so the stock `MeshIndirect.frag`
`ENABLE_TEMPERATURE` path does the rest. This is the engine-heat-glow override sub-step. Demonstrated by unscience ([Engine glow override through the dynamic part instance submission](../../../../mods/unscience/features/humble-arteest-engine-emissive.md)). Prerequisites: StarMap, Harmony.

## Background: how the game does it

`PartModelDynamic` has two public `AddInstance` wrappers (with and without dents) [current/decomp/KSA/PartModelDynamic.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelDynamic.cs#L463-L471) that both call one private `AddInstance(PerInstanceData, PerInstanceDent, IViewport, int)` [current/decomp/KSA/PartModelDynamic.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelDynamic.cs#L474), which appends to the per-viewport instance list. `PartTreeRenderData.ComposeDynamic` builds a `PerInstanceData` per slot from cached `Temperatures[]` and `TfiThicknesses[]`:

```csharp
// current/decomp/KSA/PartTreeRenderData.cs L1172-L1190
for (int i = 0; i < dynamicBatch.Count; i++)
{
	PartModelDynamic.PerInstanceData inInstanceData = new PartModelDynamic.PerInstanceData
	{
		ModelMatrix = dynamicBatch.Matrices[i] * float4x5,
		StateBitFlag = (dynamicBatch.StateBitFlags[i] | num),
		Temperature = dynamicBatch.Temperatures[i],
		TfiThickness = dynamicBatch.TfiThicknesses[i],
		Wetness = dynamicBatch.Wetness[i]
	};
	var (start, num2) = dynamicBatch.DentRanges[i];
	if (num2 == 0)
	{
		model.AddInstance(inInstanceData, inViewport, inFrameIndex);
	}
	else
	{
		model.AddInstance(inInstanceData, span.Slice(start, num2), inViewport, inFrameIndex);
	}
```

The dynamic part pipeline is compiled with `ENABLE_TEMPERATURE`. For `inTemperature > 0` the fragment shader multiplies the heat gradient stored in the green channel of the model's TFI texture by `Temperature * 1.75`, looks the result up in the temperature LUT and adds it to the light:

```glsl
// current/Content/Core/Shaders/Mesh/MeshIndirect.frag L291-L297
#ifdef ENABLE_TEMPERATURE
if (inTemperature > 0) {
    float heatFactor = sampledHeatTFI.g;
    heatFactor *= inTemperature * 1.75f;
    vec3 heatColor = texture(TEMPERATURE_LUT_SAMPLER, vec2(heatFactor, 0.5)).rgb;
    lightColor += vec3(pow(heatColor, vec3(2))) * 2.25f;
}
```

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Prefix installed | `[StarMapAllModsLoaded]` -> `TryApply` | `startup:all-mods-loaded` |
| Settings changed | the panel (`RenderContent` from `[StarMapAfterGui]`) writes static dictionaries | `frame:draw-ui` |
| Prefix runs | `Program.Render` -> `UpdateRenderingResources` -> `Vehicle.UpdateRenderData` -> `PartTree.UpdateRenderData` -> `ComposeDynamic` -> `AddInstance` | `frame:render` |
| Upload | `PartModelRenderer.UpdateRenderData` writes the lists to the GPU | `frame:render` |

The cached temperature for sim-driven parts is refreshed every frame from the part's `FxTemperature` state before `ComposeDynamic` runs, so overriding at the submission point always wins and never pollutes the cache. The prefix runs on the main thread inside the render data update and reads only static dictionaries. See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Bind the private sink by its full signature** (the four parameter types) and refuse anything that is not private, so both wrappers are covered by one patch.

2. **Prefix** `AddInstancePrefix(PartModelDynamic __instance, ref PerInstanceData inInstanceData)`: look up the effective value (per-model override first, then the global values when enabled) and write `Temperature` and `TfiThickness`. Never write `Wetness`.

   ```csharp
   // humble-arteest.lib/EngineEmissivePatches.cs L76-L102
   private static MethodInfo? ResolveAddInstance()
   {
       MethodBase? submission = AccessTools.Method(typeof(PartModelDynamic),
           nameof(PartModelDynamic.AddInstance), new[]
       {
           typeof(PartModelDynamic.PerInstanceData), typeof(KSA.Deformation.PerInstanceDent),
           typeof(IViewport), typeof(int)
       });
       return submission?.IsPrivate == true ? (MethodInfo)submission : null;
   }

   /// <summary>
   /// Harmony prefix on PartModelDynamic.AddInstance. Overrides Temperature and
   /// TfiThickness when the EngineEmissive system has settings for this instance.
   /// </summary>
   private static void AddInstancePrefix(
       PartModelDynamic __instance,
       ref PartModelDynamic.PerInstanceData inInstanceData)
   {
       if (!EngineEmissive.TryGetEffective(__instance, out var temperature, out var tfi))
           return;

       ref var writable = ref Unsafe.As<PartModelDynamic.PerInstanceData, WritablePerInstanceData>(
           ref inInstanceData);
       writable.Temperature = temperature;
       writable.TfiThickness = tfi;
   }
   ```

3. **List targets** by walking every vehicle's parts, taking each `PartModelDynamicModule.PartModelDynamic` and de-duplicating by reference; offer sliders 0 to 1 per entry and globally.

## Complete example

| File | Role |
|---|---|
| [humble-arteest.lib/EngineEmissivePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/EngineEmissivePatches.cs) | Bind the sink, override the fields |

## Variations and gotchas

- **Per model, not per engine instance:** the dictionary is keyed by `PartModelDynamic`, which the game shares between all parts built from the same template.
- **Only textured heat glows:** the TFI texture is sampled only when the draw has one (`tfiTextureIndex >= 0`); models without a heat gradient do not glow whatever the override.
- **Negative temperature means frost** in the `ENABLE_FROST` variant; clamp to 0 to 1.
- **The mirror struct is unnecessary** in this build (`PerInstanceData` fields are public and mutable); the `Unsafe.As` mirror, which must match the 80-byte layout, is the first thing to break if the layout changes.
- **Overload ambiguity:** a build added the dent-aware wrapper; binding by name alone picked an arbitrary overload and missed one wrapper.
- **Dynamic models only:** the static `PartModel.AddInstance` sink is no longer reached by static raster parts; for those use the cached-state writers (approaches 1 and 3).

## See also

- [Overview](overview.md)
- [Approach 3: ADD_EMISSIVE_COLOR in a postfix on WriteState](paint-vessel-parts-with-custom-colours-3.md)
- [Engine glow override through the dynamic part instance submission](../../../../mods/unscience/features/humble-arteest-engine-emissive.md)
