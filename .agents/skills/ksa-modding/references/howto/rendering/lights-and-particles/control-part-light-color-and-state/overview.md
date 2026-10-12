---
type: Game Feature
title: "Control part lights from code: state, colour, intensity and beam angles"
description: Switch the lights on stock light parts and vehicle lamps, recolour them, set intensity and narrow or widen spotlight cones, for one instance without affecting the shared template, and build grids and animations out of such lights
category: rendering
group: lights-and-particles
topic: control-part-light-color-and-state
demonstrated_by:
  - mods/unscience/features/zippo-light-control
  - mods/gatos/features/light-cone-control
  - mods/unscience/features/its-so-shiny-light-part-grids
  - mods/unscience/features/zippo-light-animation-queue
  - mods/unscience/features/zippo-disco-party-lights
lifecycle:
  - frame:viewports
  - frame:draw-ui
  - frame:pre-render
  - physics:vehicle-solver
  - frame:render
  - event:save
  - event:load
game_types: [KSA.LightModule, KSA.PowerConsumer, KSA.PartTemplate, KSA.Vehicle, KSA.Part, KSA.ColorRgbReference]
tags: [lights, light-part, template, colour, intensity, cone, light-switch]
---
A KSA light part has two halves. The `<Light>` block in the part's game data becomes a `LightModule.TemplateData` in `PartTemplate.Components`: type (spot or point), range, intensity, colour, inner and outer cone angles and transform. A `PowerConsumer LightSwitch="true"` gates it. Each frame `LightModule.UpdateRenderData` builds a `Light` from the template and submits it to the light system ([current/decomp/KSA/LightModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LightModule.cs#L11-L140)). Controlling a light from a mod therefore means writing the template (shared by every instance of that part type unless you replace it) and flipping the switch (which the vessel worker only honours with power, on the next step).

For lights a mod creates itself, rather than lights that live on parts, see [add dynamic lights to the world](../add-dynamic-lights-to-the-world/overview.md).

## Engine background

- **Template sharing.** `LightModule.CreateComponents` hands the template's own `TemplateData` to each module it creates, so writing `Template.Intensity.Value` or `ColorRgb` changes every light of that part type; replacing `LightModule.Template` (a public field) isolates one module.
- **Colour.** `ColorRgbReference.OnDataLoad` re-derives RGB from `IndexedColor` unless it is `Invalid`; set R, G, B, clear `IndexedColor`, then call it ([current/decomp/KSA/ColorRgbReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ColorRgbReference.cs#L35-L44)).
- **Cone.** `Light.CreateSpotLight` swaps inner and outer if inner is larger and clamps outer to `[1e-5, 1.5697963]` rad ([current/decomp/KSA.Rendering.Lighting/Light.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA.Rendering.Lighting/Light.cs#L54-L66)).
- **Switch.** `PowerConsumer.LightIsActive` is a plain field; the worker copies it to the power state each vehicle step (`PowerConsumer.UpdateModules`), so switching on takes effect after the next step and needs power. `Vehicle.ToggleLights` flips every part's switch.
- **Where it runs.** Template writes in `frame:draw-ui` (or any main-thread point) are read in the same frame's `UpdateRenderingResources` (`frame:render`). See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: write the shared template, switch via LightIsActive](control-part-light-color-and-state-1.md) | unscience (Zippo) | `reflection` on `LightModule.TemplateData`, ledger of originals, public API | `frame:draw-ui`, `frame:render`, `physics:vehicle-solver` | Simple and visible at once; changes every light of that part type |
| [2: clone the template per instance, set cone angles](control-part-light-color-and-state-2.md) | gatOS | `template-unsharing`, degrees in, radians stored, inner pulled below outer | `frame:viewports`, `frame:draw-ui`, `frame:render` | Isolated edits and working cones; clone is not persisted |
| [3: runtime light-part grids](control-part-light-color-and-state-3.md) | unscience (It's So Shiny) | `runtime-part-creation`, battery connections, per-template colour write | `frame:draw-ui`, `physics:vehicle-solver`, `frame:render` | Real lights as pixels; colour is global per template, parts are full parts |
| [4: bounded queue of eased transitions](control-part-light-color-and-state-4.md) | unscience (Zippo) | `inter-mod-api`, UI-frame `dt`, re-captured start values | `frame:draw-ui`, `frame:render` | No lag; writes the shared template |
| [5: disco channels on private templates](control-part-light-color-and-state-5.md) | unscience (Zippo) | `runtime-template-swap`, stateless time sample, owned actuators, guarded restore | `frame:draw-ui`, `physics:vehicle-solver` | Isolated per light and per assembly; ten-field copy must track the game |

## See also

- [Add dynamic spot and point lights to the world from a mod](../add-dynamic-lights-to-the-world/overview.md)
- [Hide part meshes or subparts without removing them](../../meshes/hide-parts-or-subparts-from-rendering/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
