---
type: Mod Feature
title: Edit the game's shared exhaust templates and refresh every live plume
description: Pyro reproduces the game's hidden Volumetric Exhausts debug editor, writing VolumetricExhaustTemplate absorption, emission, noise, length and quality values, propagating them to every instance and engine nozzle the way the game's editor does, and saving or reverting the edits with the scene
mod: unscience
submod: pyro
category: rendering
techniques: [reflection, imgui-window, save-sidecar]
hooks:
  - "starmap StarMapAfterGui (via unscience ISubmod.RenderContent: Template Editor section)"
  - reflection KSA.VolumetricExhaustTemplate.References (internal static field)
lifecycle: [frame:draw-ui, frame:pre-render, render:exhaust, event:save, event:load]
game_types:
  - KSA.VolumetricExhaustTemplate
  - KSA.VolumetricExhaustInstance
  - KSA.VolumetricExhaustRenderer
  - KSA.RocketNozzle
  - KSA.PartTree
  - KSA.ColorRgbReference
  - KSA.SerializedCollection
  - KSA.Mod
tags: [exhaust, templates, imgui, reflection, persistence, debug-tools]
---

KSA ships a debug window, "Volumetric Exhausts" (View -> Show Exhaust Debug), that edits the exhaust
templates live. Pyro puts the same controls in its own panel: absorption (density, scattering
brightness, phase eccentricity, refraction, clean burn in atmosphere), emission brightness and the
four-colour gradient, Mach diamond lead-in/out and middle radius, density/shape/radial noise, the
four core-length weights, and quality (samples, self-shadow samples, vessel shadows). Edits apply to
every Pyro plume and every real engine using that template, and are saved with the
scene.[^pyro-readme]

Mechanism in one line: write the public fields of the shared `VolumetricExhaustTemplate`, then call
`OnSettingsChanged()` on every instance that uses it and `RecomputeVolumetricExhaustLimits` on every
engine nozzle, mirroring the game editor's own change handler.

## How it works

**Template list.** `VolumetricExhaustTemplate.References` is an `internal static readonly
SerializedCollection<VolumetricExhaustTemplate>`, so `PlumeTemplates.GetTemplateList` reads it with
`AccessTools.Field(...).GetValue(null)` and `GetList()`. If that fails it falls back to probing the
known stock ids with the public `VolumetricExhaustTemplate.Get(id)`.[^plume-templates][^ksa-exhaust-template]

**Editing.** `RenderTemplateEditorSection` shows a filtered combo of template ids and five tree nodes.
Scalars are `DoubleReference.Value` / `BoolReference.Value` fields edited in place with drag widgets
(`PyroUi.GridDragDouble`); quality sample counts use integer sliders. Colors cannot be edited in
place: `EditColor` builds a new `ColorRgbReference(float3)`, calls `OnDataLoad(new Mod())` to resolve
it, and assigns it to the gradient slot. That is exactly how the game's editor does
it.[^template-ui][^pyro-ui][^ksa-colorrgbreference]

**Propagation.** When anything changed, `TemplateRefresher.NotifyTemplateChanged` calls
`OnSettingsChanged()` on each Pyro instance using the template, which rebuilds the template's
animation data and copies template values into the instance's shader data. It then walks
`vehicle.Parts.RocketNozzles.ModulesAndAllStates` on every vessel, and for each nozzle whose
`FxState.VolumetricExhaust` uses the template calls `OnSettingsChanged()` and
`Module.RecomputeVolumetricExhaustLimits(in instance)`, which recomputes the nozzle's
`MinGasVisibilityDensity` and `MaxPlumeExtent`.[^template-refresher][^ksa-exhaust-instance][^ksa-rocketnozzle]
The game editor's change path does the same and then rebakes the transient animation LUT with a
private texture; Pyro skips that step because it does not expose transient curves.[^ksa-exhaust-renderer]

Colour, brightness and noise need no further work: `VolumetricExhaustRenderer.Render` rebuilds the
per-template GPU buffer (`ExhaustTemplateData` via `BuildShaderData`) from the template objects every
frame, and `AddInstanceCore` calls `Template.BuildBlendedValues()` for every submission.

**Save and revert.** The first time a template is edited, `SavedPlumeTemplate.Capture` snapshots its
original values into `_originalTemplates`. The `pyro.templates` save participant (order 30, ahead of
the plume participant) stores the current values of every edited template; on load it captures
originals if needed, applies the saved values and notifies. Reset (a vanilla load, a new scene) and
sub-mod disposal write the originals back, so an edit never leaks into a save that does not carry
it.[^template-persistence][^saved-template][^pyro-persistence][^pyro-submod]

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Field writes and `NotifyTemplateChanged` | Template Editor section of `RenderContent`, `[StarMapAfterGui]` | `frame:draw-ui` |
| Blended template values read per submission | `AddInstanceCore`, from `OnPreRender` (stock) and the Pyro postfix | `frame:pre-render` |
| GPU template buffer rebuilt | `VolumetricExhaustRenderer.Render` in `RenderGame` (`Program.cs` L4891) | `render:exhaust` |
| Capture / replay of edited templates | `pyro.templates` save participant | `event:save`, `event:load` |

The game's own editor runs from `Program.OnDrawUiFrame` (`_volumetricExhaustRenderer.OnDrawUi` at
L3142), the same UI phase.[^ksa-program] All writes happen on the main thread before the frame's
pre-render and render phases read the templates, so a change shows on the same frame.

## Key code

| Path | Role |
|---|---|
| [pyro.lib/PyroSubmod.TemplateUi.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PyroSubmod.TemplateUi.cs) | Editor panel, colour replacement idiom |
| [pyro.lib/TemplateRefresher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/TemplateRefresher.cs) | Propagation to Pyro instances and engine nozzles |
| [pyro.lib/PlumeTemplates.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PlumeTemplates.cs#L17-L49) | Reflection read of the template registry with fallback |
| [pyro.lib/SavedPlumeTemplate.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/SavedPlumeTemplate.cs) | Value-only snapshot, capture and apply |
| [pyro.lib/PyroSubmod.TemplatePersistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PyroSubmod.TemplatePersistence.cs) | Originals, capture, reset, restore |
| [pyro.lib/PyroSubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PyroSubmod.Persistence.cs#L12-L33) | `pyro.templates` participant registration |

## Snippets

Capture originals on first edit, then propagate:

```csharp
// pyro.lib/PyroSubmod.TemplateUi.cs L40-L74
        bool changed = false;
        var original = SavedPlumeTemplate.Capture(template);
        string id = $"##pyro_te_{template.Id}";

        if (ImGui.TreeNodeEx($"Absorption{id}_abs", ImGuiTreeNodeFlags.SpanAvailWidth))
        {
            changed |= RenderAbsorption(template.Absorption, id);
            ImGui.TreePop();
        }
// ...
        if (changed)
        {
            _originalTemplates.TryAdd(template.Id, original);
            TemplateRefresher.NotifyTemplateChanged(template, this);
        }
```

Colours are replaced, not mutated:

```csharp
// pyro.lib/PyroSubmod.TemplateUi.cs L125-L133
    private static bool EditColor(string label, string id, ref ColorRgbReference reference)
    {
        float3 color = reference.Value.AsFloat3;
        if (!PyroUi.GridColor(label, id, ref color)) return false;
        var replacement = new ColorRgbReference(color);
        replacement.OnDataLoad(new Mod());
        reference = replacement;
        return true;
    }
```

Refreshing real engine nozzles:

```csharp
// pyro.lib/TemplateRefresher.cs L34-L45
    private static void RefreshVehicleNozzles(Vehicle vehicle, VolumetricExhaustTemplate template)
    {
        var enumerator = vehicle.Parts.RocketNozzles.ModulesAndAllStates.GetEnumerator();
        while (enumerator.MoveNext())
        {
            var current = enumerator.Current;
            var instance = current.FxState.VolumetricExhaust;
            if (instance == null || instance.Template != template) continue;
            instance.OnSettingsChanged();
            current.Module.RecomputeVolumetricExhaustLimits(in instance);
        }
    }
```

The game editor's own change handler, for comparison:

```csharp
// current/decomp/KSA/VolumetricExhaustRenderer.cs L2790-L2810
		LookupCollection<Astronomical>.TypeFilter<Vehicle> enumerator2 = Universe.CurrentSystem.All.OfType<Vehicle>().GetEnumerator();
		while (enumerator2.MoveNext())
		{
			Vehicle current2 = enumerator2.Current;
			ModuleStateful<RocketNozzle, RocketNozzleState, EmptyStruct, RocketNozzleFxState>.StateList.ModuleAndAllStatesEnumerator enumerator3 = current2.Parts.RocketNozzles.ModulesAndAllStates.GetEnumerator();
			while (enumerator3.MoveNext())
			{
				ModuleStateful<RocketNozzle, RocketNozzleState, EmptyStruct, RocketNozzleFxState>.StateList.ModuleAndAllStatesRef current3 = enumerator3.Current;
				VolumetricExhaustInstance inVolumetricExhaust = current3.FxState.VolumetricExhaust;
				if (inVolumetricExhaust != null)
				{
					inVolumetricExhaust.OnSettingsChanged();
					current3.Module.RecomputeVolumetricExhaustLimits(in inVolumetricExhaust);
				}
			}
		}
// ...
		TransientAnimationLut.BakeAnimationLutData(_renderer, VolumetricExhaustTemplate.References.GetList(), _animationLookUpTexture);
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `VolumetricExhaustTemplate.References` (internal) / `Get(string)` | [current/decomp/KSA/VolumetricExhaustTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustTemplate.cs#L56-L71) | Template registry (reflection) and lookup |
| `VolumetricExhaustTemplate.Absorption` / `Emission` / `Noise` / `LengthWeights` / `Quality` | [current/decomp/KSA/VolumetricExhaustTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustTemplate.cs#L34-L49) | Edited sub-objects |
| `VolumetricExhaustTemplate.BuildShaderData` / `BuildBlendedValues` | [current/decomp/KSA/VolumetricExhaustTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustTemplate.cs#L123-L188) | Read by the renderer every frame |
| `VolumetricExhaustRenderer.OnDrawUi` / `ShowEditor` / change handler | [current/decomp/KSA/VolumetricExhaustRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustRenderer.cs#L2448-L2810) | The game's editor this mirrors (colour idiom at L2584-L2589) |
| `VolumetricExhaustRenderer.Render` | [current/decomp/KSA/VolumetricExhaustRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustRenderer.cs#L1183-L1200) | Per-frame `ExhaustTemplateData` rebuild |
| `VolumetricExhaustInstance.OnSettingsChanged()` | [current/decomp/KSA/VolumetricExhaustInstance.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustInstance.cs#L239-L246) | Rebuild animation data and shader data from the template |
| `RocketNozzle.RecomputeVolumetricExhaustLimits(ref readonly VolumetricExhaustInstance)` | [current/decomp/KSA/RocketNozzle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketNozzle.cs#L220-L235) | Visibility threshold and maximum extent for engines |
| `ColorRgbReference(float3)` / `OnDataLoad(Mod)` | [current/decomp/KSA/ColorRgbReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ColorRgbReference.cs#L22-L35) | Replacement colour references |
| `Program.OnDrawUiFrame` -> exhaust editor; menu item "Show Exhaust Debug" | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3142) | Where the stock editor runs (menu at L3884) |

## Pitfalls and version notes

- Edits are global: they change every engine using the template, on every vessel. Per-plume colour
  is not possible in current builds because colour, brightness and noise live in the per-template GPU
  buffer indexed by `templateIndex`, not in the per-instance struct (since 5348).[^pyro-readme]
  Per-plume absorption and refraction go through the
  [scoped look override](./pyro-scoped-exhaust-look-override.md) instead.
- Startup and shutdown transient curves are not editable here; changing them needs the private
  animation LUT texture the game editor rebakes.[^scope-exhaust]
- `References` is an internal name read by string. If it is renamed, the template combo falls back to
  the six stock ids and loses mod-added templates; the fallback list includes `EngineAAuxiliary`
  since the Vernier and Turbine templates were removed.
- Templates are shared with every save, so the revert-on-reset logic matters: without it an edited
  template would leak into the next vanilla load.
- The game's editor does `new Mod()` for colour resolution; `SavedPlumeTemplate.Apply` uses
  `Mod.Empty`. Both only provide a context for `OnDataLoad`.

## See also

- [Volumetric exhaust plumes](./pyro-volumetric-exhaust-plumes.md)
- [Scoped per-plume look override](./pyro-scoped-exhaust-look-override.md)
- [Isentropic nozzle model](./pyro-isentropic-nozzle-model.md) (uses the same visibility formula)
- [Scene save coordinator](./lib-scene-save-coordinator.md)
- [Pyro sub-mod](../submods/pyro.md)
- [Mod overview](../overview.md)
- [Edit the shared exhaust templates live and propagate to instances and nozzles](../../../howto/rendering/lights-and-particles/draw-exhaust-plumes-without-an-engine/draw-exhaust-plumes-without-an-engine-3.md) - how-to article demonstrating this feature

[^template-ui]: pyro.lib/PyroSubmod.TemplateUi.cs
[^template-refresher]: pyro.lib/TemplateRefresher.cs
[^plume-templates]: pyro.lib/PlumeTemplates.cs
[^saved-template]: pyro.lib/SavedPlumeTemplate.cs
[^template-persistence]: pyro.lib/PyroSubmod.TemplatePersistence.cs
[^pyro-persistence]: pyro.lib/PyroSubmod.Persistence.cs
[^pyro-submod]: pyro.lib/PyroSubmod.cs
[^pyro-ui]: pyro.lib/PyroUi.cs
[^pyro-readme]: pyro/README.md
[^scope-exhaust]: scope/exhaust-plumes.md (authors' integration analysis)
[^ksa-exhaust-template]: KSA.VolumetricExhaustTemplate (decompiled, build 2026.10.10.5554)
[^ksa-exhaust-renderer]: KSA.VolumetricExhaustRenderer (decompiled, build 2026.10.10.5554)
[^ksa-exhaust-instance]: KSA.VolumetricExhaustInstance (decompiled, build 2026.10.10.5554)
[^ksa-rocketnozzle]: KSA.RocketNozzle (decompiled, build 2026.10.10.5554)
[^ksa-colorrgbreference]: KSA.ColorRgbReference (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
