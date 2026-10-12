---
type: How-To
title: Edit the shared exhaust templates live and propagate to instances and nozzles
description: Change absorption, emission, noise, core-length and quality values of VolumetricExhaustTemplates at runtime, then call OnSettingsChanged on every live instance and RecomputeVolumetricExhaustLimits on every engine nozzle, with originals saved for reset
category: rendering
group: lights-and-particles
topic: draw-exhaust-plumes-without-an-engine
approach: 3
difficulty: intermediate
prerequisites: [starmap, reflection]
demonstrated_by: [mods/unscience/features/pyro-exhaust-template-editor]
techniques: [reflection, imgui-window, save-sidecar]
hooks: [starmap StarMapAfterGui, reflection KSA.VolumetricExhaustTemplate.References]
lifecycle: [frame:draw-ui, frame:pre-render, render:exhaust, event:save, event:load]
game_types:
  - KSA.VolumetricExhaustTemplate
  - KSA.VolumetricExhaustInstance
  - KSA.VolumetricExhaustRenderer
  - KSA.RocketNozzle
  - KSA.PartTree
  - KSA.ColorRgbReference
  - KSA.SerializedCollection
tags: [exhaust, template, editor, live-edit, propagation, volumetric]
---
Outcome: the look of every plume that uses a template (colour gradient, brightness, absorption and scattering, Mach-diamond lead-in and out, noise, core length weights, sample counts) editable in your own panel while the game runs, applied to your engine-free plumes and to every real engine using that template, and saved with the scene.

Demonstrated by unscience's Pyro template editor ([Edit the game's shared exhaust templates and refresh every live plume](../../../../mods/unscience/features/pyro-exhaust-template-editor.md)). Prerequisites: StarMap, a little reflection. The game ships the same controls in its debug window "Volumetric Exhausts" (View, Show Exhaust Debug); this approach reproduces its change handler.

## Background: how the game does it

`VolumetricExhaustTemplate` [current/decomp/KSA/VolumetricExhaustTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VolumetricExhaustTemplate.cs#L30-L71) holds `LengthWeights`, `Absorption`, `Emission`, `Noise` and `Quality` objects of public `DoubleReference`/`BoolReference` fields, plus startup and shutdown transients. All templates live in `internal static readonly SerializedCollection<VolumetricExhaustTemplate> References`; resolution by id is the public `Get(string)`. `VolumetricExhaustRenderer.Render` rebuilds the per-template GPU buffer (`BuildShaderData`) from the template objects every frame, and `AddInstanceCore` calls `Template.BuildBlendedValues()` for every submission, so colour, brightness and noise show up without further work. What does need work are the per-instance copies: `VolumetricExhaustInstance.OnSettingsChanged()` rebuilds animation data and the instance's shader data from the template, and `RocketNozzle.RecomputeVolumetricExhaustLimits` recomputes a nozzle's `MinGasVisibilityDensity` and `MaxPlumeExtent`. The game editor's change handler does exactly that after any edit:

```csharp
// current/decomp/KSA/VolumetricExhaustRenderer.cs L2786-L2810
if (!changed || Universe.CurrentSystem == null)
{
	return;
}
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
for (int i = 0; i < _testExhaustInstances.Length; i++)
{
	_testExhaustInstances[i].OnSettingsChanged();
}
TransientAnimationLut.BakeAnimationLutData(_renderer, VolumetricExhaustTemplate.References.GetList(), _animationLookUpTexture);
```

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Field writes and propagation | the editor section of your panel, `[StarMapAfterGui]` | `frame:draw-ui` |
| Blended template values read per submission | `AddInstanceCore`, from `OnPreRender` (stock) and your postfix | `frame:pre-render` |
| GPU template buffer rebuilt | `VolumetricExhaustRenderer.Render` in `RenderGame` | `render:exhaust` |
| Capture and replay of edited templates | save participant | `event:save`, `event:load` |

The game's own editor runs in the same UI phase (`Program.OnDrawUiFrame`, `_volumetricExhaustRenderer.OnDrawUi`, L3142). All writes happen on the main thread before the frame's pre-render and render read the templates, so a change shows on the same frame. See rows 12 to 14 and 21 of [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence).

## Steps

1. **Enumerate the templates** by reading the internal collection by name, falling back to probing known stock ids with the public `Get` if the field is renamed:

   ```csharp
// pyro.lib/PlumeTemplates.cs L43-L49
/// <summary>Reads the game's internal <c>VolumetricExhaustTemplate.References</c> collection.</summary>
public static List<VolumetricExhaustTemplate>? GetTemplateList()
{
    var field = AccessTools.Field(typeof(VolumetricExhaustTemplate), "References");
    var collection = field?.GetValue(null) as SerializedCollection<VolumetricExhaustTemplate>;
    return collection?.GetList();
}
```

2. **Edit the public fields in place** with drag widgets: scalar values are `DoubleReference.Value` or `BoolReference.Value` edited through `ref`. Capture the original values the first time a template changes (`SavedPlumeTemplate.Capture`):

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
if (ImGui.TreeNodeEx($"Emission{id}_emi", ImGuiTreeNodeFlags.SpanAvailWidth))
{
    changed |= RenderEmission(template.Emission, id);
    ImGui.TreePop();
}
if (ImGui.TreeNodeEx($"Noise{id}_noise", ImGuiTreeNodeFlags.SpanAvailWidth))
{
    changed |= RenderNoise(template.Noise, id);
    ImGui.TreePop();
}
if (ImGui.TreeNodeEx($"Length weights{id}_lw", ImGuiTreeNodeFlags.SpanAvailWidth))
{
    changed |= RenderLengthWeights(template.LengthWeights, id);
    ImGui.TreePop();
}
if (ImGui.TreeNodeEx($"Quality{id}_q", ImGuiTreeNodeFlags.SpanAvailWidth))
{
    changed |= RenderQuality(template.Quality, id);
    ImGui.TreePop();
}

if (changed)
{
    _originalTemplates.TryAdd(template.Id, original);
    TemplateRefresher.NotifyTemplateChanged(template, this);
}
```

3. **Replace colours, do not mutate them.** `ColorRgbReference` caches a derived `Value`; construct a new reference, resolve it with `OnDataLoad`, and assign it to the gradient slot, as the game's editor does:

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

4. **Propagate**: `OnSettingsChanged()` on each of your instances that use the template, then every real engine nozzle in the current system whose `FxState.VolumetricExhaust` uses it:

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

5. **Save and revert.** Snapshot the original values the first time a template is edited; store the current values of every edited template in a scene-save participant that restores before the plume participant; on a vanilla load or sub-mod disposal write the originals back so an edit never leaks into a save that does not carry it.

## Complete example

| File | Role |
|---|---|
| [pyro.lib/PyroSubmod.TemplateUi.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PyroSubmod.TemplateUi.cs) | Editor panel, colour replacement idiom |
| [pyro.lib/TemplateRefresher.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/TemplateRefresher.cs) | Propagation to instances and engine nozzles |
| [pyro.lib/PlumeTemplates.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PlumeTemplates.cs) | Reflection read of the template registry with fallback |
| [pyro.lib/SavedPlumeTemplate.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/SavedPlumeTemplate.cs) | Value-only snapshot, capture and apply |
| [pyro.lib/PyroSubmod.TemplatePersistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/pyro.lib/PyroSubmod.TemplatePersistence.cs) | Originals, capture, reset, restore |

## Variations and gotchas

- **Edits are global.** They change every engine using the template, on every vessel. Colour, brightness and noise live in the per-template GPU buffer indexed by template, not in the per-instance struct, so per-plume colour is not possible; per-plume absorption and refraction need a scoped override on the final instance submission.
- **Transient curves are not editable here.** Startup and shutdown curves need the private animation LUT texture the game editor rebakes (`TransientAnimationLut.BakeAnimationLutData`, the last line of the handler above).
- **`References` is internal and read by string.** A rename makes the template combo fall back to the stock ids and lose mod-added templates; the fallback list should not include removed templates (the Vernier and Turbine ones are gone in this build).
- **Save hygiene.** Templates are shared with every save; revert-on-reset matters.
- **Colour resolution context.** The game's editor does `new Mod()` for `OnDataLoad`; `Mod.Empty` serves the same purpose.

## See also

- [Overview](overview.md)
- [Approach 2](draw-exhaust-plumes-without-an-engine-2.md), [approach 4](draw-exhaust-plumes-without-an-engine-4.md)
- [Edit the game's shared exhaust templates and refresh every live plume](../../../../mods/unscience/features/pyro-exhaust-template-editor.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
