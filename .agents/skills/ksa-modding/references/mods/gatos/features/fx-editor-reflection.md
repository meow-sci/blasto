---
type: Mod Feature
title: FX editors via reflection
description: Expose the game's built-in engine-plume, plume-trail, cloud and terrain debug editors as live-editable data by reflecting into private renderer fields, replaying each editor's own apply sequence, and recording pristine values so every change can be reset
mod: gatos
category: rendering
techniques: [reflection, command-queue, health-latch]
hooks:
  - reflection KSA.Program._volumetricTrailRenderer
  - reflection KSA.VolumetricTrailRenderer._plumeTrailSegmentsManager
  - reflection KSA.PlumeTrailSegmentsManager._settings
  - reflection KSA.VolumetricExhaustTemplate.References
  - reflection KSA.Program._planetTransparenciesRenderer
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
  - KSA.PlanetRenderer
tags: [rendering, reflection, exhaust, plumes, clouds, terrain, debug-editors, health-latch]
---

KSA ships ImGui debug editors for volumetric engine exhausts, plume trails, clouds and terrain.
gatOS turns those editors into scriptable data under `/sim/debug/{engineplume,plumetrail,clouds,terrain}`:
read every field, write one, and reset to what was there before. The pattern for any mod that wants
to drive an internal renderer setting: find the object the game's own editor mutates, write the same
fields, then call the same "apply" methods the editor calls.

## How it works

**Reach the objects** ([gatOS.GameMod/Game/Ksa/Fx/FxReflect.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Fx/FxReflect.cs)): `FieldInfo`s are resolved once
and cached; each accessor returns the object or a reason string:

| Family | Path to the live object |
|---|---|
| plume trail | `Program.Instance._volumetricTrailRenderer` (private) then `_plumeTrailSegmentsManager` then `_settings` (`PlumeTrailSettings`, the object the game's "Plume Trails" window edits) |
| engine plume | `VolumetricExhaustTemplate.References` (internal static collection of templates) |
| clouds | `Program.Instance._planetTransparenciesRenderer` (private) then public `GetCloudRenderer()`, plus private `_renderer`, `_cloudShadowsRenderer`, `_worleyNoise3dTarget` for re-apply |
| terrain | public `Program.GetPlanetRenderer()`, its public `Wireframe`, and the privately held mapped terrain UBO rings |

**Declarative field tables** ([gatOS.SimFs/Fx/FxCatalog.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Fx/FxCatalog.cs)): each family is a game-free table
of field names, types, ranges and the action keys that write them, which drives both the `/sim` tree
and validation.

**Apply like the editor**:

- Engine plume ([gatOS.GameMod/Game/Ksa/Fx/PlumeActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Fx/PlumeActuator.cs#L196-L215)): write the template field,
  then for every live nozzle using it call `VolumetricExhaustInstance.OnSettingsChanged()` and
  `RocketNozzle.RecomputeVolumetricExhaustLimits(in instance)`, the post-edit loop of the in-game editor.
  Scope is per template, so every engine using it changes.
- Clouds ([gatOS.GameMod/Game/Ksa/Fx/CloudActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Fx/CloudActuator.cs#L285-L330)): write the body's cloud layer
  reference, then `CloudLayerRenderData.UpdateStaticData(...)` per layer and
  `CloudShadowsRenderer.PopulatePlanets(...)`.
- Terrain ([gatOS.GameMod/Game/Ksa/Fx/TerrainActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Fx/TerrainActuator.cs)): a paired write to the reference on
  `Celestial.BodyTemplate` and to the GPU-mapped per-body UBO struct in every frame-in-flight copy,
  because there is no public method that re-derives the UBO.

**Reset** ([gatOS.GameMod/Game/Ksa/Fx/FxPristine.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Fx/FxPristine.cs)): the first write to each
(family, entity, field) records the previous value; `reset` replays those values through the same
write path and drops the captures.

**Read and health**: `FxEditorReader` samples all families into the snapshot, memoized until a write
lands or a resample interval passes. Each accessor has its own health latch (`fx.trail_settings`,
`fx.cloud_apply`, `fx.terrain_ubo` ...), so one moved private field degrades only that leaf.

## Lifecycle and timing

- Writes run in the Frame-phase command drain on the main thread (main-viewport prefix or
  `StarMapBeforeGui`), before `Program.RenderGame`, so the next recorded frame uses the new values.
- Reads run in the telemetry sampler on the same thread, gated by `debug_namespace`.
- No Harmony patch is involved.

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Fx/FxReflect.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Fx/FxReflect.cs) | cached reflection accessors and health keys |
| [gatOS.GameMod/Game/Ksa/Fx/PlumeActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Fx/PlumeActuator.cs) | template writes and nozzle propagation |
| [gatOS.GameMod/Game/Ksa/Fx/TrailActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Fx/TrailActuator.cs) | plume-trail settings |
| [gatOS.GameMod/Game/Ksa/Fx/CloudActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Fx/CloudActuator.cs) | cloud layer writes and re-apply |
| [gatOS.GameMod/Game/Ksa/Fx/TerrainActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Fx/TerrainActuator.cs) | reference plus UBO paired writes |
| [gatOS.GameMod/Game/Ksa/Fx/FxPristine.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Fx/FxPristine.cs) | pristine capture and reset |
| [gatOS.GameMod/Game/Ksa/Fx/FxEditorReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Fx/FxEditorReader.cs) | memoized read-back |
| [gatOS.SimFs/Fx/FxCatalog.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Fx/FxCatalog.cs) | game-free field tables |

## Snippets

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

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `Program._volumetricTrailRenderer`, `_planetTransparenciesRenderer` (private), `GetPlanetRenderer()` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L177-L205) | renderer instances |
| `VolumetricTrailRenderer` | [current/decomp/KSA/VolumetricTrailRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricTrailRenderer.cs) | trail renderer |
| `VolumetricExhaustTemplate.References` | [current/decomp/KSA/VolumetricExhaustTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustTemplate.cs#L56-L56) | template registry |
| `VolumetricExhaustInstance.OnSettingsChanged` | [current/decomp/KSA/VolumetricExhaustInstance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustInstance.cs#L239-L239) | plume refresh |
| `RocketNozzle.RecomputeVolumetricExhaustLimits` | [current/decomp/KSA/RocketNozzle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketNozzle.cs#L220-L220) | plume limits refresh |
| `PlanetTransparenciesRenderer.GetCloudRenderer()` | [current/decomp/KSA/PlanetTransparenciesRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetTransparenciesRenderer.cs#L78-L78) | cloud renderer |
| `CloudLayerRenderData.UpdateStaticData` | [current/decomp/KSA.Atmosphere.Rendering/CloudLayerRenderData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Atmosphere.Rendering/CloudLayerRenderData.cs) | cloud re-apply |
| `PlanetRenderer.Wireframe` | [current/decomp/KSA/PlanetRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetRenderer.cs#L212-L212) | terrain wireframe |

## Pitfalls and version notes

- These are the most churn-prone bindings in the mod: plume-trail settings moved into
  `PlumeTrailSettings` at 5117, `DebugTrailColor` was removed at 5402 (its leaf retired), and exhaust
  propagation changed at 5438 (gatOS notes). All fields named above exist in 5554.
- Always replay the editor's apply sequence; writing the data alone leaves GPU-side copies stale.
- Terrain edits miss distant LOD spheres until they rebuild (gatOS 5482 notes).

## See also

- [Particle bursts from the emitter pool](face-particle-bursts.md)
- [Ground-clutter texture override](clutter-texture-override.md)
- [KSA anchor churn tracking](ksa-anchor-churn-tracking.md)
- [Game-thread command queue](game-thread-command-queue.md)
- [gatOS overview](../overview.md)
- [Reflect into private editor fields and replay each editor's apply sequence](../../../howto/rendering/lights-and-particles/draw-exhaust-plumes-without-an-engine/draw-exhaust-plumes-without-an-engine-4.md) - how-to article demonstrating this feature

