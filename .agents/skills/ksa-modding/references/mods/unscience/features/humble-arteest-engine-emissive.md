---
type: Mod Feature
title: Engine glow override through the dynamic part instance submission
description: A Harmony prefix on PartModelDynamic's private AddInstance rewrites each instance's Temperature and TfiThickness just before it is appended to the frame's instance list, so the game's own ENABLE_TEMPERATURE heat lookup and thin-film shading make chosen engine models glow without any shader change
mod: unscience
submod: humble-arteest
category: rendering
techniques: [harmony-prefix, unsafe-struct-reinterpret]
hooks:
  - harmony-prefix KSA.PartModelDynamic.AddInstance (private PerInstanceData, PerInstanceDent, IViewport, int overload)
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent; settings)
lifecycle: [startup:all-mods-loaded, frame:draw-ui, frame:render]
game_types:
  - KSA.PartModelDynamic
  - KSA.PartModelDynamicModule
  - KSA.PartTreeRenderData
  - KSA.Deformation.PerInstanceDent
  - KSA.FxTemperature
  - KSA.Program
tags: [engines, emissive, heat-glow, harmony, part-models, instancing]
---

Engine Emissive makes engines glow on demand: a global "heat" and thin-film value for every dynamic
part model, or per-model values picked from a list, applied in flight without waiting for real
engine heat.[^emissive-submod]

Mechanism in one line: the game already renders engine heat from a per-instance `Temperature` float
(and thin-film tint from `TfiThickness`); a prefix on the private submission method
`PartModelDynamic.AddInstance` overwrites those two fields in the `PerInstanceData` being appended, so
the stock `MeshIndirect.frag` `ENABLE_TEMPERATURE` path does the rest.[^emissive-patches]

## How it works

1. **Seam.** `PartModelDynamic` has two public `AddInstance` wrappers (with and without dents) that
   both call one private `AddInstance(PerInstanceData, PerInstanceDent, IViewport, int)`, which
   appends to the per-viewport instance list. The mod resolves that overload by its exact four
   parameter types and refuses anything that is not private, so the shared sink is patched once and
   both wrappers are covered.[^emissive-patches][^ksa-partmodeldynamic]
2. **Prefix.** `AddInstancePrefix(PartModelDynamic __instance, ref PerInstanceData inInstanceData)`
   asks `EngineEmissive.TryGetEffective(__instance, ...)` (per-model override first, then the global
   values when enabled) and writes `Temperature` and `TfiThickness` through a mirror struct
   reinterpreted with `Unsafe.As`. `Wetness` (the last 4 bytes since KSA 5018) is never written.
3. **Shader effect.** The dynamic part pipeline is compiled with `ENABLE_TEMPERATURE` (and thin film).
   For `inTemperature > 0` the fragment shader multiplies the heat gradient stored in the green channel
   of the model's TFI texture by `Temperature * 1.75`, looks the result up in the temperature LUT and
   adds it to the light; `TfiThickness` drives thin-film colouring from the red
   channel.[^ksa-meshindirect-frag][^ksa-partmodelrenderer]
4. **Targets.** `ScanAllDynamicParts` walks every vehicle's parts, takes each
   `PartModelDynamicModule.PartModelDynamic` and de-duplicates by reference; the UI offers sliders
   0..1 per entry and globally.[^engine-emissive][^emissive-submod]

## Lifecycle and timing

| Step | Where (build 5554) | Lifecycle |
|---|---|---|
| Prefix installed | `[StarMapAllModsLoaded]` -> unscience `Patcher.Patch()` -> `TryApply("engine-emissive", ...)` | `startup:all-mods-loaded` |
| Settings changed | Engine Emissive panel (`RenderContent` from `[StarMapAfterGui]`) writes static dictionaries | `frame:draw-ui` |
| Prefix runs | `Program.Render` -> `UpdateRenderingResources` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4308-L4375), called at L2468) -> `Vehicle.UpdateRenderData` (L4355) -> `PartTree.UpdateRenderData` -> `PartTreeRenderData.ComposeDynamic`, which builds a `PerInstanceData` per slot from the cached `Temperatures[]` / `TfiThicknesses[]` and calls `AddInstance` (L1185 / L1189), once per slot per visible viewport per frame | `frame:render` |
| Upload | `PartModelRenderer.UpdateRenderData` (L4374) writes the lists to the GPU | `frame:render` |

The cached temperature for sim-driven parts is refreshed every frame from the part's `FxTemperature`
state (`EmissivityFraction`, `ThinFilmFraction`) in `EnsureBuilt` before `ComposeDynamic` runs, so
overriding at the submission point (after the cache, before the list) always wins and never pollutes
the cache.[^ksa-parttreerenderdata][^ksa-program] The prefix runs on the main thread inside the render
data update and reads only static dictionaries; it does not touch simulation state.

## Key code

| Path | Role |
|---|---|
| [humble-arteest.lib/EngineEmissivePatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/EngineEmissivePatches.cs) | Mirror struct, exact overload lookup, prefix |
| [humble-arteest.lib/EngineEmissive.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/EngineEmissive.cs#L26-L102) | Per-model and global settings, `TryGetEffective` |
| [humble-arteest.lib/EngineEmissive.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/EngineEmissive.cs#L144-L197) | Scan of dynamic models across vehicles |
| [humble-arteest.lib/EngineEmissiveSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/humble-arteest.lib/EngineEmissiveSubmod.cs) | ImGui panel |

## Snippets

Binding the private sink and overriding the two fields:

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

Where the game builds the value that the prefix replaces:

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

What `Temperature` does in the shader:

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

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `PartModelDynamic.AddInstance` (two public wrappers, one private sink) | [current/decomp/KSA/PartModelDynamic.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelDynamic.cs#L463-L482) | Patched (prefix on the private overload at L474) |
| `PartModelDynamic.PerInstanceData` | [current/decomp/KSA/PartModelDynamic.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelDynamic.cs#L393-L404) | `Temperature` at 68, `TfiThickness` at 72, `Wetness` at 76 |
| `PartModelDynamicModule.PartModelDynamic` | [current/decomp/KSA/PartModelDynamicModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelDynamicModule.cs#L31-L47) | The model, obtained per template with `PartModelDynamic.Get(template)` |
| `PartTreeRenderData.ComposeDynamic`, `ReadFxTemperature` | [current/decomp/KSA/PartTreeRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTreeRenderData.cs#L1138-L1193) | Caller per slot; stock values come from `FxTemperature` state |
| `PartModelRenderer.BuildPipelineDynamic` | [current/decomp/KSA/PartModelRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartModelRenderer.cs#L220-L253) | Compiles the dynamic pipeline with `ENABLE_TEMPERATURE` (L227, L243) |
| `ENABLE_TEMPERATURE` heat and frost blocks | [current/Content/Core/Shaders/Mesh/MeshIndirect.frag](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Mesh/MeshIndirect.frag#L126-L160) | Heat mask from the TFI texture, frost for negative temperature |

## Pitfalls and version notes

- **Per model, not per engine instance.** The dictionary is keyed by `PartModelDynamic`, which the
  game shares between all parts built from the same template (`PartModelDynamic.Get(template)`), so a
  "per-engine" setting affects every instance of that engine type. The scan de-duplicates by
  reference for the same reason.[^ksa-partmodeldynamicmodule][^engine-emissive]
- **Only textured heat glows.** The glow is `TFI texture G channel x Temperature`, and the TFI texture
  is sampled only when the draw has one (`tfiTextureIndex >= 0`); models without a heat gradient do
  not glow whatever the override.[^ksa-meshindirect-frag]
- **Negative temperature means frost** in the `ENABLE_FROST` variant; the UI clamps to 0..1.
- **The mirror struct is no longer necessary.** In 5554 `PerInstanceData`'s fields are public and
  mutable, so the prefix could assign them directly; the `Unsafe.As` mirror (which must match the
  80-byte layout exactly) is a leftover and the first thing to break if the layout changes.
- **Overload ambiguity.** KSA 5438 added the dent-aware wrapper; binding by name alone picked an
  arbitrary overload and missed one wrapper. Bind the private sink by full signature.[^scope-materials]
- The static `PartModel.AddInstance` sink is no longer reached by static raster parts since 5482, so
  this approach only works for dynamic models; vehicle paint moved to the cached state writers for
  that reason.

## See also

- [Vehicle paint](./humble-arteest-vehicle-paint-shader-patch.md)
- [Part render filter](./lib-part-render-filter.md) (also works on the compose output)
- [Pyro volumetric exhaust plumes](./pyro-volumetric-exhaust-plumes.md) (another engine visual)
- [Humble Arteest sub-mod](../submods/humble-arteest.md) and the [mod overview](../overview.md)
- [Prefix on PartModelDynamic.AddInstance rewrites Temperature and TfiThickness](../../../howto/rendering/textures-and-decals/paint-vessel-parts-with-custom-colours/paint-vessel-parts-with-custom-colours-4.md) - how-to article demonstrating this feature

[^emissive-patches]: humble-arteest.lib/EngineEmissivePatches.cs
[^engine-emissive]: humble-arteest.lib/EngineEmissive.cs
[^emissive-submod]: humble-arteest.lib/EngineEmissiveSubmod.cs
[^scope-materials]: scope/character-and-materials.md (authors' integration analysis)
[^ksa-partmodeldynamic]: KSA.PartModelDynamic (decompiled, build 2026.10.10.5554)
[^ksa-partmodeldynamicmodule]: KSA.PartModelDynamicModule (decompiled, build 2026.10.10.5554)
[^ksa-parttreerenderdata]: KSA.PartTreeRenderData (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-partmodelrenderer]: KSA.PartModelRenderer (decompiled, build 2026.10.10.5554)
[^ksa-meshindirect-frag]: Mesh/MeshIndirect.frag (game shader, build 2026.10.10.5554)
