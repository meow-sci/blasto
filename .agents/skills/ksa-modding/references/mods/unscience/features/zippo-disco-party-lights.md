---
type: Mod Feature
title: Disco party lights with per-instance light templates and owned actuators
description: Zippo's Disco runs repeating color, assembly-actuation and spotlight-spread channels on one light or a whole vessel by swapping each LightModule's Template for a private copy, claiming each keyframe actuator for one owner, and restoring only the values it still owns
mod: unscience
submod: zippo
category: rendering
techniques: [imgui-window, save-sidecar, starmap-lifecycle, runtime-template-swap]
hooks:
  - "starmap StarMapBeforeGui (via unscience ISubmod.Update: Disco tick)"
  - harmony-prefix KSA.Program.OnDrawUiConsole (shared HiddenUiFrameHook replays Update while the HUD is hidden)
  - "starmap StarMapAfterGui (via unscience ISubmod.RenderContent: recipe editor, start, inspectors)"
  - "harmony-postfix KSA.GameSave.Populate (shared NativeSaveHooks: capture)"
  - "harmony-prefix KSA.Universe.DeserializeSave (shared NativeSaveHooks: stop and restore)"
  - "harmony-postfix KSA.Universe.DeserializeSave (shared NativeSaveHooks: replay)"
lifecycle: [frame:draw-ui, frame:render, physics:vehicle-solver, event:save, event:load]
game_types:
  - KSA.LightModule
  - KSA.ColorRgbReference
  - KSA.FloatReference
  - KSA.IndexedColor
  - KSA.KeyframeAnimationModule
  - KSA.KeyframeAnimationData
  - KSA.Part
  - KSA.ModuleList
  - KSA.PowerConsumer
  - KSA.Vehicle
  - KSA.PartTree
tags: [lights, animation, spotlights, actuators, keyframe-animation, ownership, rainbow, persistence]
---

Disco turns one light, or every light on the selected vessel, into a repeating party effect with up to
three independent channels: a colour cycle through an editable 1 to 32 colour palette or random rainbow
hues, a moving light assembly swinging between two positions, and a spotlight whose beam narrows and
widens. Each channel has its own transition, hold and easing, a phase jitter keeps a vessel-wide show
from moving in lockstep, and every running light can be paused, have its switch toggled, have its recipe
copied back to the editor, or be stopped and restored.[^zippo-readme]

Mechanism in one line: unlike the ordinary [light control](./zippo-light-control.md), which writes the
shared part template, `DiscoLight` gives every `LightModule` on the target part a complete private
`TemplateData` copy (with private colour and cone-angle references for the enabled channels), drives it
from a stateless time sample each frame, and on stop puts back exactly the template, actuator goal and
switch value it replaced, unless someone else changed them in the meantime.[^disco-light]

## How it works

**Recipe and timing** ([zippo.lib/DiscoRecipe.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/DiscoRecipe.cs), [zippo.lib/DiscoTiming.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/DiscoTiming.cs)).
`DiscoRecipe` holds the channel flags, random-colour flag, phase jitter, palette, one `DiscoTiming` per
channel, the normalized actuation range and two inner/outer cone half-angle pairs in degrees. `Validate`
enforces 1 to 32 palette colours in 0..1, jitter 0 to 3600 s, `0 <= ActuationMin <= ActuationMax <= 1`,
angles 0.1 to 89 degrees with inner not above outer, and each timing's 0.01 to 3600 s transition, 0 to
3600 s hold and easing 0 to 3. `Clone` is deep, and every running light gets its own clone, so editing
the panel never alters a running effect.[^disco-recipe][^disco-timing]

`DiscoTiming.Sample(elapsed)` returns the step index `floor(elapsed / (hold + transition))` and a mix
that stays 0 during the hold and then eases to 1 over the transition (linear, cubic in, cubic out, or
smoothstep). Because the sample is a pure function of elapsed time, skipped or long frames need no
catch-up.[^disco-timing]

**Channels** ([zippo.lib/DiscoLight.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/DiscoLight.cs#L100-L162)):

| Channel | Value per frame | Written to |
|---|---|---|
| Colour | `lerp(ColorAt(step), ColorAt(step + 1), mix)`; palette entries cycle, or a hash of step and the light's seed picks a fully saturated hue | Private `ColorRgbReference`: R, G, B, `IndexedColor = Invalid`, `OnDataLoad(null)` |
| Spread | Ping-pong (odd steps run backwards) between the two angle pairs, degrees to radians | Private `InnerAngle.Value` / `OuterAngle.Value`, spot lights only |
| Actuation | Ping-pong between min and max, times the animation's `Shared.Duration` | `KeyframeAnimationModule.TimeGoal` of each owned actuator |

Each channel samples `Elapsed + its own phase offset`. The three offsets and the colour seed are drawn
once per `DiscoLight` (offsets uniformly in 0 to `PhaseJitter` seconds), so lights and channels drift
apart but stay deterministic for the life of the effect; jitter 0 restores lockstep.[^disco-light]

**Per-instance templates** ([zippo.lib/DiscoLight.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/DiscoLight.cs#L24-L68)).
The constructor walks `part.Modules.Get<LightModule>()`, copies all ten `TemplateData` fields into a
new instance, replaces `ColorRgb` with a new `ColorRgbReference` (when colour is on) and the two angle
`FloatReference`s (when spread is on and the light is a spot), and assigns `module.Template = owned`. The
game builds each frame's light from `Template`, so the change is isolated to this module; other fields
such as `Intensity`, `Range` and `Transform` remain shared references with the part template.[^disco-light][^ksa-light-module]
Every write in `Update` and the restore in `Dispose` first checks `ReferenceEquals(module.Template, owned)`;
if another feature has replaced the template in the meantime it is left alone and the inspector shows
"template replaced externally".[^zippo-disco]

**Actuator ownership** ([zippo.lib/ZippoSubmod.Disco.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/ZippoSubmod.Disco.cs#L205-L232)).
A keyframe animation module moves a whole assembly, and several light sub-parts can belong to one. For
each target, `StartDisco` considers the `KeyframeAnimationModule`s of `part.FullPart` whose animation
has a positive `Shared.Duration` and whose `Shared.PartLookup` contains this light's `Part.Id`. A module
already claimed earlier in the same start is skipped; otherwise it is released from any other running
Disco light and added to this one, which records its original `TimeGoal`. So one assembly is driven by
exactly one Disco light, and a later start takes over.[^zippo-disco][^ksa-keyframe-data]

**Light switch.** Start leaves `PowerConsumer.LightIsActive` alone. The inspector's switch toggle goes
through `SetEnabled`, which records the written value; stop restores the original only if the switch
still holds Zippo's value.[^disco-light]

**Lifecycle of an effect** ([zippo.lib/ZippoSubmod.Disco.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/ZippoSubmod.Disco.cs#L234-L260)).
Effects are keyed by `Part` reference. Starting replaces the target's previous Disco and cancels its
ordinary [animation queue](./zippo-light-animation-queue.md); any ordinary edit or queued animation on
that part stops Disco first. Each frame `UpdateDisco` builds the set of all parts of all vessels
(debris included) by reference and disposes effects whose part disappeared, then updates the rest.
`Dispose` of the sub-mod stops all effects, which restores everything Zippo still owns.[^zippo-disco][^zippo-submod]

**Persistence** ([zippo.lib/DiscoLight.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/DiscoLight.Persistence.cs)).
`SavedDiscoLight` stores a [stable part reference](./lib-save-part-identity.md), the recipe, paused
and switch state, the original switch value and whether Zippo owned it, elapsed time, seed, the three
phase offsets, and for each actuator its ordinal within `FullPart.Modules.Get<KeyframeAnimationModule>()`
plus the original goal. Restore validates the recipe and ranges, rejects missing lights, duplicate
targets, changed actuator layouts and actuators already claimed by another restored light, rebuilds the
private templates through the normal constructor, re-applies seed, phases and elapsed, samples once with
`Update(0)`, and only then applies the paused flag. Scene reset stops all Disco effects before the
template ledger restores shared originals.[^disco-light-persistence][^zippo-persistence][^zippo-lib-readme]

## Lifecycle and timing

| Work | Where | Phase |
|---|---|---|
| Recipe editing, Start, inspectors | `RenderContent` in StarMap `AfterGui` | `frame:draw-ui` |
| `UpdateDisco` (template and goal writes) | `ISubmod.Update` from StarMap `BeforeGui`, or `HiddenUiFrameHook` while F2 hides the HUD (unscience only) | `frame:draw-ui` |
| Colour and cone angles consumed | `LightModule.UpdateRenderData` reads `Template` per viewport in `Program.Render` | `frame:render`, same frame |
| Actuator moves toward `TimeGoal` | `KeyframeAnimationModule.UpdateModules` on the vessel worker | `physics:vehicle-solver`, next step |

`dt` is player time from `Program.OnDrawUiFrame(dtPlayer)`, so the recipe clock runs in real seconds
and keeps running while the game is paused. The actuator side is simulation time: the worker moves the
module's `State.TimeCurrent` toward `TimeGoal` by the step's `DeltaTime` (one second of animation per
simulated second), so at time warp the mechanism catches up faster, and while the game is paused it
does not move at all even though Disco keeps changing the goal.[^ksa-keyframe-module][^zippo-readme]
`TimeGoal` is a plain module field read by the worker without a snapshot, and Disco writes it from the
UI phase while that worker may be running; a single aligned `float` store is atomic in .NET, so the
worker sees either the old or the new goal (inference from the decomp, not tested in game).[^ksa-keyframe-module]

## Key code

| Path | Role |
|---|---|
| [zippo.lib/DiscoRecipe.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/DiscoRecipe.cs) | Authoring settings, validation, deep clone |
| [zippo.lib/DiscoTiming.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/DiscoTiming.cs) | Hold-then-transition sampler with easing |
| [zippo.lib/DiscoLight.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/DiscoLight.cs) | Template cloning, channel update, random hues, owned restore |
| [zippo.lib/DiscoLight.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/DiscoLight.Persistence.cs) | Saved effect DTO and restore constructor |
| [zippo.lib/ZippoSubmod.Disco.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/ZippoSubmod.Disco.cs) | Editor UI, start with actuator claiming, stop, per-frame update |
| [zippo.lib/ZippoSubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/ZippoSubmod.Persistence.cs#L118-L134) | Disco replay inside the `zippo` participant |
| [scope/celestial-and-lights.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/celestial-and-lights.md) | Authors' Disco integration table (D1 to D7) |

## Snippets

Stateless repeating sample:

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

A private template per light module:

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

One owner per assembly actuator:

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

Restore only what is still ours:

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

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `LightModule.Template` (public, writable) | [current/decomp/KSA/LightModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LightModule.cs#L62) | Swapped for the private copy |
| `LightModule.TemplateData` fields, `LightType.Spot` | [current/decomp/KSA/LightModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LightModule.cs#L11-L56) | The ten fields copied; spot-only spread |
| `LightModule.UpdateRenderData` | [current/decomp/KSA/LightModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LightModule.cs#L101-L140) | Builds point and spot lights from `Template` every frame (cone from `OuterAngle`/`InnerAngle`) |
| `ColorRgbReference(float3)`, `OnDataLoad`, `IndexedColor` | [current/decomp/KSA/ColorRgbReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ColorRgbReference.cs#L28-L44) | Private colour reference and its cached `Value` |
| `KeyframeAnimationModule.TimeGoal`, `Shared` | [current/decomp/KSA/KeyframeAnimationModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KeyframeAnimationModule.cs#L74-L76) | Actuator goal and its animation data |
| `KeyframeAnimationModule.UpdateModules` | [current/decomp/KSA/KeyframeAnimationModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KeyframeAnimationModule.cs#L156-L208) | Worker moves `TimeCurrent` toward `TimeGoal` by `DeltaTime` |
| `KeyframeAnimationData.PartLookup`, `Duration` | [current/decomp/KSA/KeyframeAnimationData.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/KeyframeAnimationData.cs#L223-L225) | Which sub-parts an animation moves; timeline length |
| `Part.Modules`, `Part.FullPart`, `Part.LightSwitch` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L680-L688) | Module lists and the switch |

## Pitfalls and version notes

- The field-by-field copy is only complete as long as `TemplateData` has exactly these ten fields; a
  game update that adds one requires updating the copy (the authors list this as a review item).[^scope-celestial-lights]
- `Intensity`, `Range` and `Transform` stay shared with the part template. An ordinary Zippo intensity
  edit on the same part type still reaches a Disco light, although the ordinary UI stops Disco on the
  edited part itself.
- Restores are value-guarded, not versioned: if another feature writes the same `TimeGoal` or switch
  value Zippo last wrote, Zippo will still restore over it.
- Very short actuation cycles outpace the mechanism, and pause stops the mechanism but not the recipe
  clock; the README warns that a mechanism may finish moving toward its last goal after pause.[^zippo-readme]
- Debris is part of the alive set, so a light that breaks off with its part keeps its effect; a part that
  is destroyed is disposed on the next tick.
- Point lights skip the spread channel; parts without a matching actuator report "No actuator is owned by
  this light" in the inspector.[^zippo-disco]
- Mirrored-part fan-out of keyframe animations and native colour isolation on multi-light craft are
  listed by the authors as unverified live checks.[^scope-celestial-lights]

## See also

- [Light control](./zippo-light-control.md) (shared-template writes and the public API)
- [Light animation queue](./zippo-light-animation-queue.md) (mutually exclusive with Disco)
- [Light-part pixel grids](./its-so-shiny-light-part-grids.md)
- [Stable vehicle and part identity across save and reload](./lib-save-part-identity.md)
- [Scene save coordinator](./lib-scene-save-coordinator.md)
- [Zippo sub-mod](../submods/zippo.md) and [mod overview](../overview.md)
- [Run disco channels on private light templates with owned actuators](../../../howto/rendering/lights-and-particles/control-part-light-color-and-state/control-part-light-color-and-state-5.md) - how-to article demonstrating this feature

[^zippo-readme]: zippo/README.md
[^disco-light]: zippo.lib/DiscoLight.cs
[^disco-recipe]: zippo.lib/DiscoRecipe.cs
[^disco-timing]: zippo.lib/DiscoTiming.cs
[^ksa-light-module]: KSA.LightModule (decompiled, build 2026.10.10.5554)
[^zippo-disco]: zippo.lib/ZippoSubmod.Disco.cs
[^ksa-keyframe-data]: KSA.KeyframeAnimationData (decompiled, build 2026.10.10.5554)
[^zippo-submod]: zippo.lib/ZippoSubmod.cs
[^disco-light-persistence]: zippo.lib/DiscoLight.Persistence.cs
[^zippo-persistence]: zippo.lib/ZippoSubmod.Persistence.cs
[^zippo-lib-readme]: zippo.lib/README.md
[^ksa-keyframe-module]: KSA.KeyframeAnimationModule (decompiled, build 2026.10.10.5554)
[^scope-celestial-lights]: scope/celestial-and-lights.md
