---
type: How-To
title: Run disco channels on private light templates with owned actuators
description: Give each LightModule a private TemplateData copy, drive colour, cone spread and keyframe actuators from a stateless time sample, and restore exactly what you replaced on stop
category: rendering
group: lights-and-particles
topic: control-part-light-color-and-state
approach: 5
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/zippo-disco-party-lights]
techniques: [runtime-template-swap, reflection]
hooks: [starmap StarMapBeforeGui, harmony-prefix KSA.Program.OnDrawUiConsole, starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:render, physics:vehicle-solver, event:save, event:load]
game_types:
  - KSA.LightModule
  - KSA.ColorRgbReference
  - KSA.FloatReference
  - KSA.KeyframeAnimationModule
  - KSA.KeyframeAnimationData
  - KSA.Part
  - KSA.PowerConsumer
  - KSA.Vehicle
tags: [lights, disco, template-swap, keyframe-animation, actuator, per-instance]
---
Outcome: a repeating party effect on one light or on every light of a vessel, with up to three independent channels: a colour cycle through an editable palette or random hues, a swinging light assembly (a keyframe actuator moved between two goals), and a spotlight whose beam narrows and widens. It runs without disturbing other lights of the same part type and without fighting the game's keyframe actuators, and on stop it restores exactly what it replaced.

Demonstrated by unscience's Zippo Disco ([Disco party lights with per-instance light templates and owned actuators](../../../../mods/unscience/features/zippo-disco-party-lights.md)). Prerequisites: StarMap. It is the isolated sibling of [approach 4](control-part-light-color-and-state-4.md) (shared-template transitions) and [approach 2](control-part-light-color-and-state-2.md) (clone by reflection): here the private copy is built with a typed copy of all ten fields.

## Background: how the game does it

`LightModule.Template` is a public, writable `TemplateData`; the module builds each frame's light from it. `TemplateData` has ten fields ([current/decomp/KSA/LightModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LightModule.cs#L11-L56)): `Id`, `Type`, `Transform`, `Range`, `Intensity`, `ColorRgb`, `InnerAngle`, `OuterAngle`, `RayTracing`, `DisableInIva`. Replacing `module.Template` with a copy isolates one module; other fields stay shared references with the part template. Assembly motion is a `KeyframeAnimationModule`: the vessel worker moves its `State.TimeCurrent` toward `TimeGoal` by the step's `DeltaTime`, so the actuator side runs in simulation time:

```csharp
// current/decomp/KSA/KeyframeAnimationModule.cs L181-L205
if (module.TimeGoal < current.State.TimeCurrent)
{
	if (doubleQuat2.HasValue)
	{
		current.State.DeploymentState = DeploymentState.Retracting;
		module.ApplyAnimationTransforms(current.State.TimeCurrent, doubleQuat2);
		module._transformsDirty = true;
		continue;
	}
	current.State.TimeCurrent -= (float)context.DeltaTime;
	if (module.TimeGoal > current.State.TimeCurrent)
	{
		current.State.TimeCurrent = module.TimeGoal;
	}
}
else
{
	current.State.TimeCurrent += (float)context.DeltaTime;
	if (module.TimeGoal < current.State.TimeCurrent)
	{
		current.State.TimeCurrent = module.TimeGoal;
	}
}
current.State.DeploymentState = DeriveDeploymentState(module.TimeGoal, current.State.TimeCurrent);
module.ApplyAnimationTransforms(current.State.TimeCurrent, doubleQuat2);
```

One animation module moves a whole assembly (`KeyframeAnimationData.PartLookup` lists the sub-parts it moves), and several light sub-parts can belong to one.

## Lifecycle and timing

| Work | Where | Phase |
|---|---|---|
| Recipe editing, Start, inspectors | `RenderContent` in StarMap `AfterGui` | `frame:draw-ui` |
| `UpdateDisco` (template and goal writes) | `ISubmod.Update` from StarMap `BeforeGui`, or a hidden-HUD hook | `frame:draw-ui` |
| Colour and cone angles consumed | `LightModule.UpdateRenderData` reads `Template` per viewport in `Program.Render` | `frame:render`, same frame |
| Actuator moves toward `TimeGoal` | `KeyframeAnimationModule.UpdateModules` on the vessel worker | `physics:vehicle-solver`, next step |

`dt` is player time from `Program.OnDrawUiFrame(dtPlayer)`, so the recipe clock runs in real seconds and keeps running while the game is paused; the actuator side is simulation time, so at time warp the mechanism catches up faster, and while paused it does not move at all even though the goal keeps changing. `TimeGoal` is a plain module field read by the worker without a snapshot and written from the UI phase; a single aligned `float` store is atomic in .NET (an inference from the decompile, not tested in game). See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence).

## Steps

1. **Sample time statelessly.** A repeating hold-then-transition sampler returns a step index and an eased mix as a pure function of elapsed time, so skipped or long frames need no catch-up:

   ```csharp
// zippo.lib/DiscoTiming.cs L30-L44
public (long Step, float Mix) Sample(double elapsed)
{
    double period = (double)Transition + Hold;
    elapsed = double.IsFinite(elapsed) ? Math.Max(0d, elapsed) : 0d;
    long step = (long)Math.Floor(elapsed / period);
    float t = (float)Math.Clamp((elapsed % period - Hold) / Transition, 0d, 1d);
    t = Easing switch
    {
        1 => t * t * t,
        2 => 1f - MathF.Pow(1f - t, 3f),
        3 => t * t * (3f - 2f * t),
        _ => t,
    };
    return (step, t);
}
```

2. **Give every light module a private template** at start: copy all ten fields into a new `TemplateData`, replace `ColorRgb` with a fresh `ColorRgbReference` (and the two angle references for spot lights when spread is enabled), assign `module.Template = owned`. Remember the original:

   ```csharp
// zippo.lib/DiscoLight.cs L36-L67
foreach (var module in part.Modules.Get<LightModule>())
{
    var original = module.Template;
    var owned = new LightModule.TemplateData
    {
        Id = original.Id,
        Type = original.Type,
        Transform = original.Transform,
        Range = original.Range,
        Intensity = original.Intensity,
        ColorRgb = original.ColorRgb,
        InnerAngle = original.InnerAngle,
        OuterAngle = original.OuterAngle,
        RayTracing = original.RayTracing,
        DisableInIva = original.DisableInIva,
    };

    if (Recipe.Color)
    {
        owned.ColorRgb = new ColorRgbReference((float3)original.ColorRgb);
        owned.ColorRgb.OnDataLoad(null!);
    }

    if (Recipe.Spread && owned.Type == LightModule.TemplateData.LightType.Spot)
    {
        owned.InnerAngle = new FloatReference(original.InnerAngle.Value);
        owned.OuterAngle = new FloatReference(original.OuterAngle.Value);
    }

    _lights.Add((module, original, owned));
    module.Template = owned;
}
```

3. **Per frame, write only if the module still holds your template.** Each channel samples `Elapsed + its own phase offset` (random in 0 to `PhaseJitter`) so a vessel-wide show does not move in lockstep. Colour channel: palette entries cycle, or a hash of step and seed picks a saturated hue; spread: ping-pong between two cone-angle pairs; actuation: ping-pong between min and max times the animation duration written to `TimeGoal`:

   ```csharp
// zippo.lib/DiscoLight.cs L100-L142
public void Update(double dt)
{
    if (Paused) return;
    if (double.IsFinite(dt) && dt > 0d) Elapsed += dt;

    var (colorStep, colorMix) = Recipe.ColorTiming.Sample(Elapsed + _colorPhaseOffset);
    float3 start = ColorAt(colorStep);
    float3 end = ColorAt(colorStep + 1);
    float3 color = start + (end - start) * colorMix;

    var (spreadStep, spreadMix) = Recipe.SpreadTiming.Sample(Elapsed + _spreadPhaseOffset);
    if (spreadStep % 2 != 0) spreadMix = 1f - spreadMix;

    foreach (var (module, _, owned) in _lights)
    {
        // Another feature may deliberately replace the module template. Do not overwrite it.
        if (!ReferenceEquals(module.Template, owned)) continue;

        if (Recipe.Color)
        {
            owned.ColorRgb.R = color.X;
            owned.ColorRgb.G = color.Y;
            owned.ColorRgb.B = color.Z;
            owned.ColorRgb.IndexedColor = IndexedColor.Invalid;
            owned.ColorRgb.OnDataLoad(null!);
        }

        if (Recipe.Spread && owned.Type == LightModule.TemplateData.LightType.Spot)
        {
            owned.InnerAngle.Value = Lerp(Recipe.InnerMin, Recipe.InnerMax, spreadMix) * MathF.PI / 180f;
            owned.OuterAngle.Value = Lerp(Recipe.OuterMin, Recipe.OuterMax, spreadMix) * MathF.PI / 180f;
        }
    }

    var (actuationStep, actuationMix) = Recipe.ActuationTiming.Sample(Elapsed + _actuationPhaseOffset);
    if (actuationStep % 2 != 0) actuationMix = 1f - actuationMix;
    foreach (var actuator in Actuators)
    {
        float goal = Lerp(Recipe.ActuationMin, Recipe.ActuationMax, actuationMix) * actuator.Shared.Duration;
        actuator.TimeGoal = goal;
        _goals[actuator] = (_goals[actuator].Original, goal);
    }
}
```

4. **Claim actuators exclusively.** One animation module moves a complete assembly, so exactly one disco light owns it; a later start takes over, and each claimed module records its original `TimeGoal`:

   ```csharp
// zippo.lib/ZippoSubmod.Disco.cs L205-L232
private void StartDisco(IEnumerable<Part> targets)
{
    var claimed = new HashSet<KeyframeAnimationModule>();
    foreach (var part in targets)
    {
        StopDisco(part);
        _animationManager.CancelAll(PartKey(part));

        var live = new DiscoLight(part, _disco);
        _discoLights[part] = live;
        if (_disco.Actuation)
        {
            // One animation module moves a complete assembly, so exactly one Disco light owns it.
            foreach (var module in part.FullPart.Modules.Get<KeyframeAnimationModule>())
            {
                if (module.Shared.Duration <= 0f || !module.Shared.PartLookup.ContainsKey(part.Id)
                    || !claimed.Add(module))
                {
                    continue;
                }

                foreach (var other in _discoLights.Values) other.ReleaseActuator(module);
                live.AddActuator(module);
            }
        }
        live.Update(0d);
    }
}
```

5. **Restore only what is still yours.** Put back the original template if the module still holds yours, the original goal if it still holds the value you wrote, and the original switch value if nobody changed it:

   ```csharp
// zippo.lib/DiscoLight.cs L166-L187
public void Dispose()
{
    foreach (var (module, original, owned) in _lights)
    {
        if (ReferenceEquals(module.Template, owned)) module.Template = original;
    }

    foreach (var (module, goal) in _goals)
    {
        if (module.TimeGoal == goal.Written) module.TimeGoal = goal.Original;
    }

    if (_lightSwitch != null && _writtenSwitchState.HasValue
        && _lightSwitch.LightIsActive == _writtenSwitchState.Value)
    {
        _lightSwitch.LightIsActive = _originalSwitchState;
    }

    _lights.Clear();
    Actuators.Clear();
    _goals.Clear();
}
```

6. **Prune per frame and exclude other writers.** Build the set of all parts of all vessels (debris included) by reference each tick and dispose effects whose part disappeared; starting a disco on a part cancels its ordinary animation queue and any ordinary edit stops disco first:

   ```csharp
// zippo.lib/ZippoSubmod.Disco.cs L244-L260
private void UpdateDisco(double dt)
{
    if (_discoLights.Count == 0) return;

    var alive = new HashSet<Part>(
        VehicleProvider.GetAllVehicles(includeDebris: true).SelectMany(PartHelpers.GetAllParts),
        ReferenceEqualityComparer.Instance);
    foreach (var (part, live) in _discoLights.ToArray())
    {
        if (!alive.Contains(part))
        {
            StopDisco(part);
            continue;
        }
        live.Update(dt);
    }
}
```

## Complete example

| File | Role |
|---|---|
| [zippo.lib/DiscoRecipe.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/DiscoRecipe.cs) | Authoring settings, validation, deep clone |
| [zippo.lib/DiscoTiming.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/DiscoTiming.cs) | Hold-then-transition sampler with easing |
| [zippo.lib/DiscoLight.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/DiscoLight.cs) | Template cloning, channel update, random hues, owned restore |
| [zippo.lib/DiscoLight.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/DiscoLight.Persistence.cs) | Saved effect DTO and restore constructor |
| [zippo.lib/ZippoSubmod.Disco.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/ZippoSubmod.Disco.cs) | Editor UI, start with actuator claiming, stop, per-frame update |

## Variations and gotchas

- **Complete-copy risk.** The field-by-field copy is only complete while `TemplateData` has exactly these ten fields; a game update that adds one needs the copy updated.
- **Partly shared.** `Intensity`, `Range` and `Transform` stay shared with the part template, so an ordinary intensity edit on the same part type still reaches a disco light.
- **Value-guarded restores, not versioned.** If another feature writes the same `TimeGoal` or switch value you last wrote, you will still restore over it.
- **Pause.** Very short actuation cycles outpace the mechanism; pause stops the mechanism but not the recipe clock.
- **Point lights** skip the spread channel; parts without a matching actuator have no actuation channel.
- **Debris** is part of the alive set, so a light that breaks off with its part keeps its effect; a part that is destroyed is disposed on the next tick.

## See also

- [Overview](overview.md)
- [Approach 2](control-part-light-color-and-state-2.md), [approach 4](control-part-light-color-and-state-4.md)
- [Disco party lights with per-instance light templates and owned actuators](../../../../mods/unscience/features/zippo-disco-party-lights.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
