---
type: How-To
title: Write colour, intensity and switch state into the shared PartTemplate light component
description: Find a part's LightModule.TemplateData by reflection, write Intensity and ColorRgb so the colour sticks, switch the light through PowerConsumer.LightIsActive and keep a ledger of originals for restore
category: rendering
group: lights-and-particles
topic: control-part-light-color-and-state
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/zippo-light-control]
techniques: [reflection, starmap-lifecycle]
hooks:
  - starmap StarMapAfterGui
  - reflection KSA.PartTemplate.Components
  - reflection KSA.LightModule+TemplateData.Intensity
  - reflection KSA.ColorRgbReference.R
lifecycle: [frame:draw-ui, frame:render, physics:vehicle-solver, event:save, event:load]
game_types:
  - KSA.Vehicle
  - KSA.Part
  - KSA.PartTemplate
  - KSA.LightModule
  - KSA.ColorRgbReference
  - KSA.FloatReference
  - KSA.PowerConsumer
  - KSA.ModLibrary
tags: [lights, light-part, template, colour, intensity, light-switch]
---
Outcome: pick a vessel and one of its light parts and switch it on or off, set its intensity from 0 to 1 and its colour to any RGB, with the change visible on the next rendered frame. The same calls form a small API other mods can use. The catch to understand up front: the colour and intensity live on the part template, which every instance of that part type shares, so one write changes every light of that type on every vessel.

Demonstrated by unscience's Zippo ([Light part selection and template-level color and intensity writes with a public API](../../../../mods/unscience/features/zippo-light-control.md)). Prerequisites: StarMap. For per-instance changes use [approach 2](control-part-light-color-and-state-2.md).

## Background: how the game does it

A part is a light part when its `PartTemplate.Components` contains a `KSA.LightModule+TemplateData` (a `<Light>` block in the part XML, usually on a sub-part). `LightModule.CreateComponents` hands the template's own `TemplateData` object to every `LightModule` it creates, and `LightModule.UpdateRenderData` builds the point or spot light from `Template.ColorRgb` and `Template.Intensity` every frame:

```csharp
// current/decomp/KSA/LightModule.cs L82-L99
public static void CreateComponents(Part part, PartTemplate template, PartInstance? instance, Part? parent)
{
	foreach (TemplateDataBase component in template.Components)
	{
		if (component is TemplateData templateData)
		{
			LightModule module = new LightModule(templateData.Id, templateData)
			{
				Parent = part
			};
			if (parent != null && parent.SubtreeModules.TryGetTypeList(out Module<AttachedInternal>.List typeList) && typeList.Modules[0].InstanceOf == part.Id)
			{
				part.FullPart.LightSwitch = parent.LightSwitch;
			}
			part.Modules.Add(module);
		}
	}
}
```

```csharp
// current/decomp/KSA/LightModule.cs L101-L108
public void UpdateRenderData(in double4x4 matrixVehicleAsmb2Ego, IViewport viewport, bool isEditedVehicle, int frameIndex)
{
	if (!IsActive || (Template.DisableInIva && viewport.Mode == CameraMode.IVA))
	{
		return;
	}
	double4x4 matrix = base.Parent.MatrixAsmb2Ego(in matrixVehicleAsmb2Ego);
	switch (Template.Type)
```

A write into a template component is therefore a write into every live instance of that part type, from the next frame on. `LightModule.IsActive` is `!Parent.FullPart.IsLightSwitchedOff()` (unless `DisableInIva`), and `Part.IsLightSwitchedOff()` is true when the part's `PowerConsumer.LightSwitch` module is off or unpowered. Colours are `ColorRgbReference`s whose `OnDataLoad` re-derives RGB from `IndexedColor` unless it is `Invalid`, then caches `Value`:

```csharp
// current/decomp/KSA/ColorRgbReference.cs L35-L44
public virtual void OnDataLoad(Mod mod)
{
	if (IndexedColor != IndexedColor.Invalid && TryGetIndexedRgb(IndexedColor, out var rgb))
	{
		R = rgb.X;
		G = rgb.Y;
		B = rgb.Z;
	}
	Value = new float3(R, G, B);
}
```

## Lifecycle and timing

| Step | Where | Phase |
|---|---|---|
| Panel writes and API calls from UI code | `RenderContent` in `StarMapAfterGui` | `frame:draw-ui` |
| Light built from `Template` | `Program.UpdateRenderingResources`, `vehicle.UpdateRenderData`, `PartTree.UpdateRenderData`, `LightModule.UpdateRenderData` per viewport ([current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1174-L1179)) | `frame:render`, same frame |
| Switch state consumed by power | Vessel worker `PowerConsumer.UpdateModules` copies `LightIsActive` into `PowerConsumerState.Active`, cleared when no power is available | `physics:vehicle-solver`, next step |
| Ledger reset and replay | Scene coordinator inside `DeserializeSave` | `event:load` |

Template writes are plain field stores on the main thread read on the main thread during render, so colour and intensity edits show in the same frame and need no physics synchronization. The light switch is different: `Part.IsLightSwitchedOff()` also reads the worker-computed `PowerConsumerState.Active`. Switching off is visible on the next render; switching on waits for the next vehicle step to copy the flag, and stays dark if the vessel has no power ([current/decomp/KSA/PowerConsumer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PowerConsumer.cs#L93-L121)). See [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence), rows 12 to 14 and 22.

## Steps

1. **Discover light parts.** Walk `vehicle.Parts.Parts` recursively through `Part.SubParts` (lights usually live on sub-parts) and keep parts whose template has a light component. Zippo compares component runtime type names; `PartTemplate.Components` and `LightModule.TemplateData` are public in this build, so `component is LightModule.TemplateData` works without reflection and a rename becomes a compile error:

   ```csharp
// zippo.lib/LightController.cs L29-L44
/// <summary>Finds all KSA.LightModule+TemplateData entries in the part template's Components list.</summary>
public static List<object> GetLightComponents(PartTemplate t)
{
    var result = new List<object>();
    var comps = ReflectionHelpers.GetFieldValue(t, "Components") as IList;
    if (comps == null) return result;
    for (int i = 0; i < comps.Count; i++)
    {
        var c = comps[i];
        if (c?.GetType().FullName == "KSA.LightModule+TemplateData")
            { result.Add(c); ComponentOwners[c] = t; }
    }
    return result;
}

public static bool HasLights(PartTemplate t) => GetLightComponents(t).Count > 0;
```

2. **Write intensity and colour so they stick.** Set R, G, B, clear `IndexedColor`, then call `OnDataLoad(null)` so the cached `Value` updates. Without clearing `IndexedColor`, `OnDataLoad` would re-derive RGB from the named colour and discard your write:

   ```csharp
// zippo.lib/LightController.cs L67-L94
public static void WriteIntensity(List<object> lights, float intensity)
{
    foreach (var light in lights)
    {
        RememberOriginal(light);
        var intensityRef = ReflectionHelpers.GetFieldValue(light, "Intensity");
        ReflectionHelpers.SetFieldValue(intensityRef, "Value", intensity);
    }
}

public static void WriteColor(List<object> lights, float3 color)
{
    foreach (var light in lights)
    {
        RememberOriginal(light);
        var colorRef = ReflectionHelpers.GetFieldValue(light, "ColorRgb");
        if (colorRef == null) continue;
        ReflectionHelpers.SetFieldValue(colorRef, "R", color.X);
        ReflectionHelpers.SetFieldValue(colorRef, "G", color.Y);
        ReflectionHelpers.SetFieldValue(colorRef, "B", color.Z);
        // Clear any named/indexed color: ColorRgbReference.OnDataLoad re-derives R/G/B from
        // IndexedColor whenever it isn't Invalid, which would discard the RGB we just wrote.
        ReflectionHelpers.SetFieldValue(colorRef, "IndexedColor", IndexedColor.Invalid);
        // OnDataLoad recomputes Value = new float3(R, G, B)
        try { colorRef.GetType().GetMethod("OnDataLoad", All)?.Invoke(colorRef, new object?[] { null }); }
        catch (Exception ex) { Console.WriteLine($"zippo: SetColor OnDataLoad error: {ex.Message}"); }
    }
}
```

3. **Switch the light through the part's own switch**, not through intensity, when it has one: `part.LightSwitch ?? part.FullPart.LightSwitch`, then `LightIsActive`. Without a switch, fall back to intensity 0 or the last intensity. This is the API shape other mods call:

   ```csharp
// zippo.lib/ZippoSubmod.cs L467-L485
/// <summary>Sets color and/or intensity on a specific light part. Returns error message or null on success.</summary>
public string? SetLightState(string vehicleId, string partId, float3? color, float? intensity, bool? enabled)
{
    var part = ResolvePartInVehicle(vehicleId, partId);
    if (part == null) return $"Part '{partId}' not found on vehicle '{vehicleId}'.";

    StopDisco(part);
    if (color.HasValue) LightController.ApplyColor(part, color.Value);
    if (intensity.HasValue) LightController.ApplyIntensity(part, intensity.Value);
    if (enabled.HasValue)
    {
        var ls = part.LightSwitch ?? part.FullPart?.LightSwitch;
        if (ls != null)
            ls.LightIsActive = enabled.Value;
        else if (!enabled.Value)
            LightController.ApplyIntensity(part, 0f);
    }
    return null;
}
```

4. **Ledger the originals** before the first write so the whole change can be restored on scene reset, and saved/replayed with the game's save:

   ```csharp
// zippo.lib/LightController.Persistence.cs L36-L40
private static void RememberOriginal(object component)
{
    if (!_restoringOriginals && ComponentOwners.ContainsKey(component) && !OriginalSettings.ContainsKey(component))
        OriginalSettings.Add(component, ReadComponent(component));
}
```

   ```csharp
// zippo.lib/LightController.Persistence.cs L67-L80
internal static void ResetTemplateChanges()
{
    _restoringOriginals = true;
    try
    {
        foreach (var (component, original) in OriginalSettings)
        {
            var one = new List<object> { component };
            WriteIntensity(one, original.Intensity); WriteColor(one, original.Color);
        }
        OriginalSettings.Clear(); ComponentOwners.Clear();
    }
    finally { _restoringOriginals = false; }
}
```

## Complete example

| File | Role |
|---|---|
| [zippo.lib/LightController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/LightController.cs) | Discovery, reads, writes, presets, debug dump |
| [zippo.lib/LightController.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/LightController.Persistence.cs) | Original-value ledger, capture, restore, reset |
| [zippo.lib/ZippoSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/ZippoSubmod.cs#L441-L523) | Public API: `GetLightPartInfos`, `SetLightState`, `QueueAnimation`, `ClearAnimationQueue`, `IsAnimating` |
| [zippo.lib/ZippoSubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/ZippoSubmod.Persistence.cs) | `zippo` scene-save participant |

## Variations and gotchas

- **Template scope is the big one.** Changing one light changes that part type everywhere until reset. Approaches 2 and 5 show the per-instance alternatives.
- **First light only for reads.** `ReadIntensity` and `ReadColor` read the first light component; parts with several lights are written uniformly but reported by their first.
- **String reflection is optional.** Zippo reflects by name although the fields are public in this build; a rename then fails silently (zero lights found, or a no-op write). The authors record that an earlier version wrote the XML name `Color` instead of the field `ColorRgb` and colour was silently a no-op.
- **`Part.Id` can repeat** inside one vessel (template or sub-part names); animation code keys on `Part.InstanceId` for that reason.
- **Switch needs power.** Switching on stays dark if the vessel has no power; a lit lens also needs the part's emissive handling, which the light component does not recolour.
- **Presets.** Zippo's four colour presets (Marine, HotPink, RadioactiveGreen, BabyPurple) are hard-coded copies of the game's XKCD constants.

## See also

- [Overview](overview.md)
- [Approach 2](control-part-light-color-and-state-2.md), [3](control-part-light-color-and-state-3.md), [4](control-part-light-color-and-state-4.md), [5](control-part-light-color-and-state-5.md)
- [Light part selection and template-level color and intensity writes with a public API](../../../../mods/unscience/features/zippo-light-control.md)
- [Add dynamic spot and point lights to the world from a mod](../add-dynamic-lights-to-the-world/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
