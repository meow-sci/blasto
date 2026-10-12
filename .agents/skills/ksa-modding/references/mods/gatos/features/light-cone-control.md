---
type: Mod Feature
title: Part light cone control
description: Switch part lights and change their brightness, colour and spotlight inner and outer cone angles at runtime by cloning each light's shared template per instance, with the engine facts from the repo's analysis of how KSA renders spotlights
mod: gatos
category: rendering
techniques: [reflection, template-unsharing, command-queue]
hooks: [harmony-prefix KSA.GameViewport.OnFrame, starmap StarMapBeforeGui]
lifecycle: [frame:viewports, frame:draw-ui, frame:render]
game_types: [KSA.LightModule, KSA.PowerConsumer, KSA.Vehicle, KSA.Rendering.Lighting.Light, KSA.FloatReference]
tags: [lights, spotlight, templates, reflection, rendering, analysis]
---

`/sim/vessels/<id>/lights/<n>/{on,brightness,color,inner_angle,outer_angle}` and `ctl/lights` drive
every light on a vessel. Brightness, colour and cone angles live on the light's template, which is
shared by every instance of that part, so the actuator clones the template for the one light being
edited. The repo also documents why a naive "narrow the beam" control did not narrow anything,
which is a useful engine fact for anyone building spotlights or lasers.

## How it works

- **On/off** ([gatOS.GameMod/Game/Ksa/Actuators/LightActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/LightActuator.cs#L34-L53)): the master sets
  `Vehicle.LightsOn` and every `PowerConsumer` with `LightSwitch` to `LightIsActive = on` (a set,
  rather than KSA's toggle); per light it sets the part's power switch `LightIsActive`.
- **Per-instance template** ([gatOS.GameMod/Game/Ksa/Actuators/LightActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/LightActuator.cs#L127-L151)):
  `LightModule.Template` is a shared `TemplateData`; before the first write gatOS shallow-clones it
  (and its `Intensity`, `ColorRgb`, `OuterAngle`, `InnerAngle` reference objects) with
  `RuntimeHelpers.GetUninitializedObject` and a field-by-field copy across the type hierarchy, assigns
  the clone to that module, and remembers it in a `ConditionalWeakTable`.
- **Values**: brightness writes `Intensity.Value`; colour writes `ColorRgb.R/G/B` then calls
  `OnDataLoad` to recompute the rendered value; angles are degrees in, radians stored, outer clamped
  to KSA's `[1e-5, 1.5697963]` band, and writing the outer angle also pulls the inner angle down to
  stay at or below it ([gatOS.GameMod/Game/Ksa/Actuators/LightActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/LightActuator.cs#L92-L119)).

## Engine findings from the light analysis

From [docs/LIGHT_PART_ANALYSIS.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/docs/LIGHT_PART_ANALYSIS.md), checked against 5554:

- A light part has two halves: a `<Light>` block in the part's GameData XML (type, range, colour,
  intensity, half-angles in radians, transform) read into `LightModule.TemplateData`
  ([current/decomp/KSA/LightModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LightModule.cs#L12-L62)), and a mesh/material half in the Assets XML for the
  glowing lens. A `PowerConsumer` with `LightSwitch` gates it.
- Each frame `LightModule.UpdateRenderData` builds a `Light` with
  `Light.CreateSpotLight(position, direction, Range, OuterAngle, InnerAngle, ColorRgb, Intensity, ...)`
  and registers it with `Program.LightSystem.CreateLightInstance` (or the ray-tracing list)
  ([current/decomp/KSA/LightModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LightModule.cs#L101-L137)). Template edits therefore apply at the next render-data pass.
- `CreateSpotLight` first **swaps** inner and outer if inner is larger, then clamps outer to
  `[1e-5, 1.5697963]` rad and inner to `[0, outer]` ([current/decomp/KSA.Rendering.Lighting/Light.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/Light.cs#L54-L63)).
  Writing only the outer angle below an untouched inner angle (22.5 degrees on the standard spotlight
  per the analysis) makes the swap restore the wide cone, which is why gatOS lowers the inner angle too.
- The light pre-pass falloff divides by `max(innerAngle - outerAngle, SPOT_DENOM_EPSILON)` with
  `SPOT_DENOM_EPSILON = 1e-4` ([current/Content/Core/Shaders/Lighting/LightPrePass.comp](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Shaders/Lighting/LightPrePass.comp#L320-L332));
  the analysis computes that below about 0.81 degrees half-angle the beam dims rather than narrows,
  so very tight cones need more intensity.
- Spotlights only light surfaces; there is no volumetric beam. A pure-XML narrow "laser" part example
  ships in the repo ([docs/examples/CoreLaserAGameData.xml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/docs/examples/CoreLaserAGameData.xml)).

## Lifecycle and timing

- Writes run in the Frame-phase command drain on the main thread (main-viewport prefix or
  `StarMapBeforeGui`). `LightModule.UpdateRenderData` reads the template later in the same frame:
  `PartTree.UpdateRenderData` calls it from `Vehicle.UpdateRenderData` inside
  `Program.UpdateRenderingResources`, which `Program.Render` runs after `OnPreRender`
  (`frame:render`, not `frame:pre-render`), so a write drained in the viewport prefix lights the
  frame being rendered.
- Read-back comes from the sampler's light pass ([gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs#L817-L839)).

## Key code

| Path | Role |
|---|---|
| [gatOS.GameMod/Game/Ksa/Actuators/LightActuator.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Actuators/LightActuator.cs) | switches, per-instance template clone, value writes |
| [gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/VesselReader.cs#L809-L839) | light read-back including angles |
| [gatOS.GameMod/Game/Ksa/Readers/AnimationLinks.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Ksa/Readers/AnimationLinks.cs) | light deploy-animation links |
| [docs/LIGHT_PART_ANALYSIS.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/docs/LIGHT_PART_ANALYSIS.md) | the analysis |

## Snippets

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/LightActuator.cs L92-L104
    internal static CommandResult SetOuterAngle(Vehicle vehicle, int ordinal, double degrees)
    {
        if (GetLight(vehicle, ordinal) is not { } light)
            return new CommandResult(CommandOutcome.NotFound, $"light {ordinal} does not exist");
        EnsureUnshared(light);
        var outer = (float)Math.Clamp(degrees * DegToRad, MinOuterAngleRad, MaxOuterAngleRad);
        light.Template.OuterAngle.Value = outer;
        // Keep inner <= outer so KSA's CreateSpotLight swap can't fire (which would pin the cone back to
        // the old inner angle and silently undo the narrowing).
        if (light.Template.InnerAngle.Value > outer)
            light.Template.InnerAngle.Value = outer;
        return CommandResult.Ok;
    }
```

```csharp
// gatOS.GameMod/Game/Ksa/Actuators/LightActuator.cs L127-L138
    private static void EnsureUnshared(LightModule light)
    {
        if (Unshared.TryGetValue(light, out _))
            return;
        var clone = ShallowClone(light.Template);
        clone.Intensity = ShallowClone(light.Template.Intensity);
        clone.ColorRgb = ShallowClone(light.Template.ColorRgb);
        clone.OuterAngle = ShallowClone(light.Template.OuterAngle);
        clone.InnerAngle = ShallowClone(light.Template.InnerAngle);
        light.Template = clone;
        Unshared.Add(light, Marker);
    }
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `LightModule.Template` (`TemplateData`: `Intensity`, `ColorRgb`, `InnerAngle`, `OuterAngle`) | [current/decomp/KSA/LightModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LightModule.cs#L12-L62) | per-light settings |
| `LightModule.UpdateRenderData` | [current/decomp/KSA/LightModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LightModule.cs#L101-L137) | where the template is read |
| `Light.CreateSpotLight` | [current/decomp/KSA.Rendering.Lighting/Light.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/Light.cs#L54-L78) | swap and clamp |
| `PowerConsumer.LightSwitch`, `LightIsActive` | [current/decomp/KSA/PowerConsumer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PowerConsumer.cs#L28-L30) | power gate |
| `Vehicle.LightsOn` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L267-L267) | master flag |

## Pitfalls and version notes

- Writing a shared template recolours every light of that part type in the universe; clone first.
- The analysis also explains why the emissive lens stays white regardless of light colour; gatOS has
  not implemented lens recolouring.
- The analysis line numbers refer to an older build; the members cited here exist unchanged in 5554.

## See also

- [Ignition, staging and module actuators](ignition-staging-and-module-actuators.md)
- [Part and module state reads](part-module-state-reads.md)
- [Particle bursts from the emitter pool](face-particle-bursts.md)
- [gatOS overview](../overview.md)
- [Clone the shared light template per instance and set cone angles](../../../howto/rendering/lights-and-particles/control-part-light-color-and-state/control-part-light-color-and-state-2.md) - how-to article demonstrating this feature

