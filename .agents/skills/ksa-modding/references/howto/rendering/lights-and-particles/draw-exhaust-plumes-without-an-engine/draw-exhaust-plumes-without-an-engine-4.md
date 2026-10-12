---
type: How-To
title: Reflect into private editor fields and replay each editor's apply sequence
description: Make the game's volumetric exhaust, plume trail and cloud debug editors scriptable by resolving their private objects once by reflection, writing the same fields and calling the same apply methods, with pristine capture for reset
category: rendering
group: lights-and-particles
topic: draw-exhaust-plumes-without-an-engine
approach: 4
difficulty: advanced
prerequisites: [starmap, reflection]
demonstrated_by: [mods/gatos/features/fx-editor-reflection]
techniques: [reflection]
hooks:
  - reflection KSA.Program._volumetricTrailRenderer
  - reflection KSA.VolumetricTrailRenderer._plumeTrailSegmentsManager
  - reflection KSA.PlumeTrailSegmentsManager._settings
  - reflection KSA.VolumetricExhaustTemplate.References
  - reflection KSA.Program._planetTransparenciesRenderer
  - harmony-prefix KSA.GameViewport.OnFrame
  - starmap StarMapBeforeGui
lifecycle: [frame:viewports, frame:draw-ui]
game_types:
  - KSA.Program
  - KSA.VolumetricTrailRenderer
  - KSA.PlumeTrailSegmentsManager
  - KSA.PlumeTrailSettings
  - KSA.VolumetricExhaustTemplate
  - KSA.VolumetricExhaustInstance
  - KSA.RocketNozzle
  - KSA.PlanetTransparenciesRenderer
  - KSA.Atmosphere.Rendering.CloudRenderer
  - KSA.Atmosphere.Rendering.CloudLayerRenderData
tags: [exhaust, plume-trail, clouds, debug-editor, reflection, pristine-reset]
---
Outcome: the effect settings KSA ships only as ImGui debug windows (engine plume templates, plume-trail settings, cloud layers) read and written as data, with a "reset to what was there before". The pattern works for any internal renderer setting: find the object the game's own editor mutates, write the same fields, then call the same "apply" methods the editor calls. No Harmony patch is involved.

Demonstrated by gatOS' `/sim/debug/{engineplume,plumetrail,clouds,terrain}` ([FX editors via reflection](../../../../mods/gatos/features/fx-editor-reflection.md)). Prerequisites: StarMap (or a main-thread command drain), reflection. This is the scriptable counterpart of [approach 3](draw-exhaust-plumes-without-an-engine-3.md).

## Background: how the game does it

| Family | Path to the live object |
|---|---|
| plume trail | `Program.Instance._volumetricTrailRenderer` (private) then `_plumeTrailSegmentsManager` then `_settings` (`PlumeTrailSettings`, the object the "Plume Trails" window edits) |
| engine plume | `VolumetricExhaustTemplate.References` (internal static collection) |
| clouds | `Program.Instance._planetTransparenciesRenderer` (private) then public `GetCloudRenderer()`, plus private `_renderer`, `_cloudShadowsRenderer`, `_worleyNoise3dTarget` for re-apply |

The apply sequences are the ones in the game's editors. For engine plumes, the change handler after an edit runs `OnSettingsChanged()` and `RecomputeVolumetricExhaustLimits(in instance)` on every live nozzle ([current/decomp/KSA/VolumetricExhaustRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustRenderer.cs#L2786-L2810)). For clouds, the editor calls `SelectedLayer.OnDataLoad(Mod.Empty)`, `CloudLayerRenderData.UpdateStaticData(...)` per layer and `_cloudShadowsRenderer.PopulatePlanets(_planetToCloudRenderData, _worleyNoise3dTarget)`:

```csharp
// current/decomp/KSA.Atmosphere.Rendering/CloudRenderer.cs L1773-L1781
for (int num2 = 0; num2 < layers.Count; num2++)
{
	if (!flag2 || layers[num2] == SelectedLayer)
	{
		SelectedLayer.OnDataLoad(Mod.Empty);
		array2[num2].UpdateStaticData(_renderer, SelectedPlanet, layers[num2], transitionStartAltitude, transitionEndAltitude, maxShadowAltitude);
	}
}
_cloudShadowsRenderer.PopulatePlanets(_planetToCloudRenderData, _worleyNoise3dTarget);
```

Plume-trail settings moved onto `PlumeTrailSettings` in an earlier refactor (the object is `PlumeTrailSegmentsManager._settings`, private, constructed at [current/decomp/KSA/PlumeTrailSegmentsManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlumeTrailSegmentsManager.cs#L19)).

## Lifecycle and timing

- Writes run in a frame-phase command drain on the main thread (a prefix on the main viewport's frame, or `StarMapBeforeGui`: `frame:viewports` or `frame:draw-ui`), before `Program.RenderGame`, so the next recorded frame uses the new values.
- Reads run in a telemetry sampler on the same thread.
- The editors themselves run in `Program.OnDrawUiFrame`; see rows 12 to 14 of [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence).

## Steps

1. **Resolve each private member once, cache it, and return a reason string on failure.** Every accessor is null-tolerant: a member that moved yields `null` plus a reason, which the caller turns into a health latch for that capability only:

   ```csharp
// gatOS.GameMod/Game/Ksa/Fx/FxReflect.cs L93-L112
internal static VolumetricTrailRenderer? Trail(out string error)
{
    error = "";
    if (Program.Instance is not { } program)
    {
        error = "the game program is not running yet";
        return null;
    }

    if (!_trailResolved)
    {
        _trailResolved = true;
        _trailField = typeof(Program).GetField("_volumetricTrailRenderer", AnyInstance);
    }

    if (_trailField?.GetValue(program) is VolumetricTrailRenderer trail)
        return trail;
    error = "Program._volumetricTrailRenderer is missing in this build";
    return null;
}
```

   Two private hops for the trail settings (hence higher churn risk):

   ```csharp
// gatOS.GameMod/Game/Ksa/Fx/FxReflect.cs L137-L155
internal static PlumeTrailSettings? TrailSettings(VolumetricTrailRenderer trail, out string error)
{
    error = "";
    if (!_trailSettingsResolved)
    {
        _trailSettingsResolved = true;
        _trailManagerField = typeof(VolumetricTrailRenderer)
            .GetField("_plumeTrailSegmentsManager", AnyInstance);
        _trailSettingsField = typeof(PlumeTrailSegmentsManager).GetField("_settings", AnyInstance);
    }

    if (_trailManagerField?.GetValue(trail) is PlumeTrailSegmentsManager manager
        && _trailSettingsField?.GetValue(manager) is PlumeTrailSettings settings)
        return settings;

    error = "VolumetricTrailRenderer._plumeTrailSegmentsManager/PlumeTrailSegmentsManager._settings "
            + "are missing in this build";
    return null;
}
```

2. **Write like the editor.** For an engine plume, capture the pristine value, write the template field, then run the post-edit propagation pass over every vehicle in the current system, each isolated so one mid-teardown vessel cannot abort the rest:

   ```csharp
// gatOS.GameMod/Game/Ksa/Fx/PlumeActuator.cs L22-L37
internal static CommandResult Set(string id, FxFieldSpec spec, string field,
    IReadOnlyList<double> values)
{
    if (Resolve(id) is not { } template)
        return new CommandResult(CommandOutcome.NotFound, $"exhaust template '{id}' is gone");

    var pristine = new double[spec.Arity];
    if (TryRead(template, spec, pristine))
        FxPristine.Capture(FxFamily.EnginePlume, id, field, pristine);
    if (!TryWrite(template, spec, values))
        return new CommandResult(CommandOutcome.Invalid, $"'{field}' is not bound in this game build");

    Propagate();
    FxEditorReader.Invalidate();
    return CommandResult.Ok;
}
```

   ```csharp
// gatOS.GameMod/Game/Ksa/Fx/PlumeActuator.cs L205-L231
private static void Propagate()
{
    if (Universe.CurrentSystem is not { } system)
        return;

    foreach (var astronomical in system.All.UnsafeAsList())
    {
        if (astronomical is not Vehicle vehicle)
            continue;
        try
        {
            var nozzles = vehicle.Parts?.RocketNozzles;
            if (nozzles is null)
                continue;
            foreach (var module in nozzles.ModulesAndAllStates)
            {
                if (module.FxState.VolumetricExhaust is not { } instance)
                    continue;
                instance.OnSettingsChanged();
                module.Module.RecomputeVolumetricExhaustLimits(in instance);
            }
        }
        catch (Exception)
        {
            // A vehicle mid-teardown must not abort the propagation for the rest.
        }
    }
```

3. **Clouds: write the layer reference, then replay the editor's apply.** `OnDataLoad(Mod.Empty)` per layer, `UpdateStaticData` with the private renderer handles, then one shadow-atlas repopulate. A missing private handle degrades only the apply: the data write already landed and appears on the next natural repopulate:

   ```csharp
// gatOS.GameMod/Game/Ksa/Fx/CloudActuator.cs L303-L329
private static CommandResult Apply(KsaHealth health, AtmosphericBody body, CloudsReference clouds, int layer)
{
    if (FxReflect.Clouds(out var rendererError) is not { } renderer)
        return Degraded(health, FxReflect.CloudRendererAccessor, rendererError);
    FxReflect.Healthy(health, FxReflect.CloudRendererAccessor);

    if (FxReflect.CloudApply(renderer, out var applyError) is not { } handles)
        return Degraded(health, FxReflect.CloudApplyAccessor, applyError);
    FxReflect.Healthy(health, FxReflect.CloudApplyAccessor);

    if (!renderer._planetToCloudRenderData.TryGetValue(body.Hash, out var renderData))
        return CommandResult.Ok; // body not currently rendered; the data write stands

    var start = (float)(double)clouds.OrbitTransitionStartAltitude;
    var end = (float)(double)clouds.OrbitTransitionEndAltitude;
    var maxShadow = (float)(double)clouds.MaxShadowsAltitude;
    for (var i = 0; i < clouds.Layers.Count && i < renderData.Length; i++)
    {
        if (layer >= 0 && i != layer)
            continue;
        clouds.Layers[i].OnDataLoad(KSA.Mod.Empty);
        renderData[i].UpdateStaticData(handles.Renderer, body, clouds.Layers[i], start, end, maxShadow);
    }

    handles.Shadows.PopulatePlanets(renderer._planetToCloudRenderData, handles.WorleyNoise);
    return CommandResult.Ok;
}
```

4. **Capture pristine values on the first write** to each (family, entity, field) and replay them through the same write path on reset or at unload:

   ```csharp
// gatOS.GameMod/Game/Ksa/Fx/FxPristine.cs L45-L69
internal static void Capture(FxFamily family, string entity, string field, double[] values)
{
    var key = (family, entity);
    if (!Captures.TryGetValue(key, out var fields))
        Captures[key] = fields = new Dictionary<string, double[]>(StringComparer.Ordinal);
    fields.TryAdd(field, values);
}

/// <summary>
///     Replays every captured field of one entity and drops the captures. A reset with nothing
///     captured is a successful no-op (the entity is already pristine). Returns the number of
///     fields restored.
/// </summary>
internal static int Restore(FxFamily family, string entity)
{
    var key = (family, entity);
    if (!Captures.Remove(key, out var fields))
        return 0;

    var restored = 0;
    foreach (var (field, values) in fields)
        if (Apply(family, entity, field, values))
            restored++;
    return restored;
}
```

5. **Declare the field tables game-free** (names, types, ranges, action keys) so validation and the exposed tree do not depend on game types; give each accessor its own health key so one moved private field degrades only that leaf.

## Complete example

| File | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Fx/FxReflect.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Fx/FxReflect.cs) | Cached reflection accessors and health keys |
| [gatOS.GameMod/Game/Ksa/Fx/PlumeActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Fx/PlumeActuator.cs) | Template writes and nozzle propagation |
| [gatOS.GameMod/Game/Ksa/Fx/TrailActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Fx/TrailActuator.cs) | Plume-trail settings |
| [gatOS.GameMod/Game/Ksa/Fx/CloudActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Fx/CloudActuator.cs) | Cloud layer writes and re-apply |
| [gatOS.GameMod/Game/Ksa/Fx/FxPristine.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Fx/FxPristine.cs) | Pristine capture and reset |
| [gatOS.SimFs/Fx/FxCatalog.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Fx/FxCatalog.cs) | Game-free field tables |

## Variations and gotchas

- **Most churn-prone bindings.** Plume-trail settings moved into `PlumeTrailSettings` in a refactor, a debug colour field was removed later and exhaust propagation changed in another; the fields named above exist in this build.
- **Always replay the editor's apply sequence.** Writing the data alone leaves GPU-side copies stale.
- **Terrain edits** miss distant LOD spheres until they rebuild, and need a paired write to the reference and to the mapped per-body UBO in every frame-in-flight copy because no public method re-derives the UBO.
- **Scope.** Template writes are per template, not per engine: every plume using it changes.
- **Cloud renderer exists with clouds off** when plume trails or explosions are on, so cloud writes can run against layers that are not drawn.

## See also

- [Overview](overview.md)
- [Approach 3](draw-exhaust-plumes-without-an-engine-3.md), [approach 5](draw-exhaust-plumes-without-an-engine-5.md)
- [FX editors via reflection](../../../../mods/gatos/features/fx-editor-reflection.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
