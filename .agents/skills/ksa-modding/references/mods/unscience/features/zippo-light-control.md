---
type: Mod Feature
title: Light part selection and template-level color and intensity writes with a public API
description: Zippo finds light parts by walking a vessel's part tree for LightModule template data, writes intensity and RGB color into the shared PartTemplate light components by reflection, toggles PowerConsumer.LightIsActive, ledgers originals for save and reset, and exposes the same operations to other mods
mod: unscience
submod: zippo
category: rendering
techniques: [reflection, imgui-window, inter-mod-api, save-sidecar]
hooks:
  - "starmap StarMapAfterGui (via unscience ISubmod.RenderContent: selectors, on/off, intensity, color)"
  - reflection KSA.PartTemplate.Components (light components found by type name KSA.LightModule+TemplateData)
  - reflection KSA.LightModule+TemplateData.Intensity / ColorRgb
  - reflection KSA.FloatReference.Value / KSA.ColorRgbReference.R / G / B / IndexedColor / OnDataLoad
  - "harmony-postfix KSA.GameSave.Populate (shared NativeSaveHooks: capture)"
  - "harmony-prefix KSA.Universe.DeserializeSave (shared NativeSaveHooks: restore template baselines)"
  - "harmony-postfix KSA.Universe.DeserializeSave (shared NativeSaveHooks: replay)"
lifecycle: [frame:draw-ui, frame:render, physics:vehicle-solver, event:save, event:load]
game_types:
  - KSA.Vehicle
  - KSA.Part
  - KSA.PartTemplate
  - KSA.PartTree
  - KSA.LightModule
  - KSA.ColorRgbReference
  - KSA.FloatReference
  - KSA.IndexedColor
  - KSA.PowerConsumer
  - KSA.ModLibrary
  - KSA.KSAColor
  - KSA.Program
tags: [lights, parts, part-tree, reflection, colors, xkcd, power, api, persistence]
---

Zippo's base panel lets a player pick a vessel and one of its light parts, then switch it on or off,
drag its intensity from 0 to 1, choose a colour preset or any custom colour, and see the change on the
next rendered frame. The same calls are a small public API (`ZippoSubmod.Instance`) that other mods use
to query and drive lights, and the static `LightController` they rest on is reused directly by It's So
Shiny for its light grids.[^zippo-readme][^light-controller]

Mechanism in one line: a part is a light part when its `PartTemplate.Components` list contains a
`KSA.LightModule+TemplateData`; intensity is written to that object's `Intensity.Value` and colour to
`ColorRgb.R/G/B` (clearing `IndexedColor` and calling `OnDataLoad` so the cached `Value` updates), all by
reflection, while on/off goes through the part's typed `PowerConsumer.LightIsActive` light switch.

## How it works

**Discovery** ([zippo.lib/LightController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/LightController.cs#L29-L44)).
`GetLightParts(vehicle)` is `PartHelpers.GetPartsWhere`, a recursive walk from `vehicle.Parts.Parts`
through every `Part.SubParts`, keeping parts whose template has at least one light component. Light
components are found by comparing each component's runtime type name with
`"KSA.LightModule+TemplateData"`; each hit is remembered with its owning `PartTemplate` for the save
ledger. Lights usually live on sub-parts (the authors' spec quotes a `<SubPart>` with a nested `<Light>`),
which is why the walk must recurse.[^light-controller][^part-helpers][^zippo-spec]

**What the writes touch** (decomp, build 5554). `LightModule.CreateComponents` hands the template's own
`TemplateData` object to every `LightModule` it creates, and `LightModule.UpdateRenderData` builds the
point or spot light from `Template.ColorRgb` and `Template.Intensity` every frame. A write into a
`PartTemplate` component is therefore a write into every live instance of that part type, on every
vessel, from the next frame on.[^ksa-light-module]

| Operation | Code | Game member |
|---|---|---|
| Read intensity / colour | `ReadIntensity`, `ReadColor` (first light component) | `TemplateData.Intensity.Value`, `ColorRgb.R/G/B` |
| Write intensity | `WriteIntensity` | `FloatReference.Value` |
| Write colour | `WriteColor`: set R, G, B; `IndexedColor = IndexedColor.Invalid`; `OnDataLoad(null)` | `ColorRgbReference.OnDataLoad` re-derives RGB from a named colour unless `IndexedColor` is `Invalid`, then sets `Value = new float3(R, G, B)`[^ksa-color-rgb-reference] |
| On / off | `part.LightSwitch ?? part.FullPart.LightSwitch`, then `LightIsActive`; without a switch, intensity 0 or the last intensity | `Part.LightSwitch`, `PowerConsumer.LightIsActive` |

**Panel** ([zippo.lib/ZippoSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/ZippoSubmod.cs#L60-L233)).
Filterable vessel and light-part combos with an explicit "(none)" entry; selecting a part seeds the
intensity, switch state and animation start values, and records the part's original colour the first
time it is seen. The colour combo offers Default (that original), four XKCD presets with hard-coded RGB
(Marine, HotPink, RadioactiveGreen, BabyPurple) and "(Custom)" once the `ColorEdit4` picker is used. The
[light animation queue](./zippo-light-animation-queue.md) uses the full catalog of
[954 named XKCD colours](./lib-xkcd-color-catalog.md). While an animation runs on the selected part the
manual controls are disabled, and every manual write first stops [Disco](./zippo-disco-party-lights.md)
on that part. A Debug header can dump every part's components and fields to the log.[^zippo-submod]

**Public API** ([zippo.lib/ZippoSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/ZippoSubmod.cs#L441-L523)), reached through `ZippoSubmod.Instance`:

| Method | Returns | Notes |
|---|---|---|
| `GetLightPartInfos(string vehicleId)` | `List<LightPartInfo>?` | Part id, display name, intensity, colour, enabled, animating, queued count; `null` if the vessel is unknown |
| `SetLightState(vehicleId, partId, float3? color, float? intensity, bool? enabled)` | error `string?` | Stops Disco on that part, then applies the non-null values |
| `QueueAnimation(vehicleId, partId, LightAnimation)` | error `string?` | Fails when the 25-deep queue is full |
| `ClearAnimationQueue(vehicleId, partId)` | always `null` | Idempotent |
| `IsAnimating(string partId)` | `bool` | Accepts a part id or the internal instance key |

Errors are returned as strings rather than thrown, which suits RPC-style callers. Parts are matched
by `Part.Id` within the vessel, the first match wins.[^zippo-submod][^light-part-info]

**Template ledger** ([zippo.lib/LightController.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/LightController.Persistence.cs)).
Before the first write to a component, `RememberOriginal` stores its intensity and colour in a
reference-keyed dictionary. Because It's So Shiny writes through the same `WriteIntensity`/`WriteColor`,
its changes are ledgered too. `CaptureTemplateChanges` saves template id, light-component ordinal,
original and current values; `RestoreTemplateChange` resolves `ModLibrary.Get<PartTemplate>(id)` and
rejects a changed component layout; `ResetTemplateChanges` writes every original back (with a guard so
the restore itself is not ledgered) and clears the ledger. Zippo's participant (`zippo`, restore order
80) wraps this together with per-part original colours, queues and Disco state.[^light-controller-persistence][^zippo-persistence][^zippo-lib-readme]

## Lifecycle and timing

| Step | Where | Phase |
|---|---|---|
| Panel writes and API calls from UI code | `RenderContent` in StarMap `AfterGui` | `frame:draw-ui` |
| Light built from `Template` | `Program.UpdateRenderingResources` -> `vehicle.UpdateRenderData` (L4355) -> `PartTree.UpdateRenderData` -> `LightModule.UpdateRenderData` per viewport (PartTree.cs L1174-L1179) | `frame:render`, same frame |
| Switch state consumed by power | Vessel worker `PowerConsumer.UpdateModules` copies `LightIsActive` into `PowerConsumerState.Active`, cleared when no power is available | `physics:vehicle-solver`, next step |
| Ledger reset and replay | Scene coordinator inside `DeserializeSave` | `event:load` |

Template writes are plain field stores on the main thread and are read on the main thread during
render, so colour and intensity edits made in the UI show in the same frame and need no physics
synchronization.[^ksa-program][^ksa-part-tree] The light switch is different: `LightModule.IsActive` uses
`Part.IsLightSwitchedOff()`, which is `!LightIsActive || !IsSwitchedOn()`, and `IsSwitchedOn` reads the
worker-computed `PowerConsumerState.Active`. Switching off is visible on the next render; switching on
waits for the next vehicle step to copy the flag, and stays dark if the vessel has no power.[^ksa-part][^ksa-power-consumer]

## Key code

| Path | Role |
|---|---|
| [zippo.lib/LightController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/LightController.cs) | Discovery, reads, writes, presets, debug dump |
| [zippo.lib/LightController.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/LightController.Persistence.cs) | Original-value ledger, capture, restore, reset |
| [zippo.lib/ZippoSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/ZippoSubmod.cs) | Panel, selection state, public API |
| [zippo.lib/ZippoSubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/ZippoSubmod.Persistence.cs) | `zippo` scene-save participant |
| [zippo.lib/LightPartInfo.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/LightPartInfo.cs) | API result record |
| [ksa-abstractions.lib/PartHelpers.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PartHelpers.cs) | Recursive part-tree walk |
| [ksa-abstractions.lib/ReflectionHelpers.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/ReflectionHelpers.cs) | Field get/set by name |
| [scope/celestial-and-lights.md](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/scope/celestial-and-lights.md) | Reflection surface table and per-build checks |

## Snippets

Finding light components and writing a colour that sticks:

```csharp
// zippo.lib/LightController.cs L30-L94
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
    // ...
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

Why a template write reaches every instance: the game passes the template's object to each module.

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

The public setter other mods call:

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

Ledger: remember before the first write, put everything back on scene reset:

```csharp
// zippo.lib/LightController.Persistence.cs L36-L80
    private static void RememberOriginal(object component)
    {
        if (!_restoringOriginals && ComponentOwners.ContainsKey(component) && !OriginalSettings.ContainsKey(component))
            OriginalSettings.Add(component, ReadComponent(component));
    }
    // ...
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

## Game APIs touched

| Member | Decomp | Used for |
|---|---|---|
| `PartTemplate.Components` | [current/decomp/KSA/PartTemplate.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTemplate.cs#L119) | Light component discovery (public field, read by name) |
| `LightModule.TemplateData` (`Intensity`, `ColorRgb`, `Type`, angles) | [current/decomp/KSA/LightModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LightModule.cs#L11-L56) | Values written |
| `LightModule.CreateComponents`, `Template`, `IsActive`, `UpdateRenderData` | [current/decomp/KSA/LightModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LightModule.cs#L62-L140) | Shared template object; per-frame light from `Template` |
| `ColorRgbReference.OnDataLoad`, `R/G/B`, `IndexedColor`, `Value` | [current/decomp/KSA/ColorRgbReference.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ColorRgbReference.cs#L9-L44) | Colour write that survives refresh |
| `Part.LightSwitch`, `Part.FullPart`, `Part.SubParts`, `Part.IsLightSwitchedOff` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1461-L1473) | Switch lookup, tree walk, render gate |
| `PowerConsumer.LightIsActive`, `IsSwitchedOn`, `UpdateModules` | [current/decomp/KSA/PowerConsumer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PowerConsumer.cs#L29-L120) | Switch flag and its worker-side power gate |
| `PartTree.UpdateRenderData` light loop | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L1174-L1179) | Where templates are read each frame |
| `Program.UpdateRenderingResources` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4346-L4357) | Per-viewport vessel render data |

## Pitfalls and version notes

- Template scope is the big one: changing one light changes that part type everywhere until reset.
  [Disco](./zippo-disco-party-lights.md) shows the per-instance alternative (replace
  `LightModule.Template` with a private copy).[^ksa-light-module]
- Everything is string reflection although, in build 5554, `PartTemplate.Components`,
  `LightModule.TemplateData` and its fields are public. A rename fails silently (zero lights found, or a
  no-op write) instead of at compile time; the only typed dependency is `IndexedColor.Invalid`. The
  authors record that an earlier version wrote the XML name `Color` instead of the field `ColorRgb` and
  colour was silently a no-op.[^scope-celestial-lights][^ksa-part-template]
- `ReadIntensity`/`ReadColor` read only the first light component; parts with several lights are
  written uniformly but reported by their first light.
- The four preset RGB values are hard-coded copies of the game's XKCD constants, not looked up.[^light-controller]
- API lookups use `Part.Id`, which is a template or sub-part name and can repeat inside one vessel; the
  animation queue uses the runtime `Part.InstanceId` for that reason.[^zippo-lib-readme]
- `GetLightPartInfos` and `SetLightState` look vessels up with `GetAllVehicles()` (debris excluded),
  while the animation resolver includes debris.[^zippo-submod]

## See also

- [Light animation queue](./zippo-light-animation-queue.md) and [Disco party lights](./zippo-disco-party-lights.md)
- [Light-part pixel grids switched through stock light switches](./its-so-shiny-light-part-grids.md) (reuses `LightController`)
- [Enumerate the game's named XKCD colors](./lib-xkcd-color-catalog.md)
- [Thin static providers](./lib-game-state-providers.md) (`PartHelpers`, `ReflectionHelpers`)
- [Scene save coordinator](./lib-scene-save-coordinator.md)
- [Zippo sub-mod](../submods/zippo.md) and [mod overview](../overview.md)
- [Write colour, intensity and switch state into the shared PartTemplate light component](../../../howto/rendering/lights-and-particles/control-part-light-color-and-state/control-part-light-color-and-state-1.md) - how-to article demonstrating this feature

[^zippo-readme]: zippo/README.md
[^light-controller]: zippo.lib/LightController.cs
[^part-helpers]: ksa-abstractions.lib/PartHelpers.cs
[^zippo-spec]: zippo/SPEC.md
[^ksa-light-module]: KSA.LightModule (decompiled, build 2026.10.10.5554)
[^ksa-color-rgb-reference]: KSA.ColorRgbReference (decompiled, build 2026.10.10.5554)
[^zippo-submod]: zippo.lib/ZippoSubmod.cs
[^light-part-info]: zippo.lib/LightPartInfo.cs
[^light-controller-persistence]: zippo.lib/LightController.Persistence.cs
[^zippo-persistence]: zippo.lib/ZippoSubmod.Persistence.cs
[^zippo-lib-readme]: zippo.lib/README.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-part-tree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-power-consumer]: KSA.PowerConsumer (decompiled, build 2026.10.10.5554)
[^scope-celestial-lights]: scope/celestial-and-lights.md
[^ksa-part-template]: KSA.PartTemplate (decompiled, build 2026.10.10.5554)
