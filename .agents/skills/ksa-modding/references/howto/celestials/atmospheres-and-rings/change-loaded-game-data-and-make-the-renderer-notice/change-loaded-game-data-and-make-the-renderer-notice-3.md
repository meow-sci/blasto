---
type: How-To
title: Assign to the template and call Program.RebuildRenderer
description: Add or remove data on a body template, dispose the one renderer that caches it, refresh its body list and ask the whole renderer to rebuild, accepting a short hitch
category: celestials
group: atmospheres-and-rings
topic: change-loaded-game-data-and-make-the-renderer-notice
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/bloomin-onion-runtime-planetary-rings]
techniques: [reflection]
hooks:
  - reflection KSA.PlanetTransparenciesRenderer._anyRings
  - reflection KSA.PlanetTransparenciesRenderer._ringRendererCreated
lifecycle: [frame:draw-ui]
game_types: [KSA.Program, KSA.PlanetTransparenciesRenderer, KSA.AstronomicalTemplate]
tags: [renderer-rebuild, template, reflection, rings]
---
Outcome: new data on a body template (a ring on a moon) becomes visible, including for bodies that had none before. The heavier hammer: dispose the specific renderer, refresh what decides whether it exists at all, then ask the whole renderer to rebuild. A short hitch is the cost. Demonstrated by unscience's Bloomin' Onion ([Bloomin' Onion runtime planetary rings](../../../../mods/unscience/features/bloomin-onion-runtime-planetary-rings.md)); see [add or restyle planetary rings, approach 1](../add-or-restyle-planetary-rings/add-or-restyle-planetary-rings-1.md) for the construction of the data.

## Background: how the game does it

Whether a rings renderer exists at all is decided in `PlanetTransparenciesRenderer`: the constructor sets `_anyRings = PopulatePlanets()` and creates the renderer only `if (GameSettings.ShowRings() && _anyRings)`; `RebuildFrameResources` takes its create branch when the flag is false and `_anyRings` is true ([current/decomp/KSA/PlanetTransparenciesRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetTransparenciesRenderer.cs#L128-L152), [current/decomp/KSA/PlanetTransparenciesRenderer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlanetTransparenciesRenderer.cs#L374-L430)). A mod that adds the first ring in a system must therefore rewrite `_anyRings` (the public `PopulatePlanets()` recomputes the list and returns the value), or the create branch never runs.

## Lifecycle and timing

Run it synchronously from the button handler in `[StarMapAfterGui]` (`frame:draw-ui`); the mod calls `Program.RebuildRenderer()` itself instead of setting `RendererRebuildNeeded`. A scheduled rebuild would run after the handler returns, so you could not revert the template on failure or free assets that only become unreferenced afterwards. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. Assign the new data to the template, remembering the previous value.
2. Wait for the device, dispose the renderer that cached the old data and clear its created flag.
3. Recompute the list of bodies the renderer cares about and write the result into the private flag:

```csharp
// bloomin-onion.lib/RingRendererRebuilder.cs L28-L69
public static bool Rebuild(out string message)
{
    try
    {
        var program = Program.Instance;
        if (program == null)
        {
            message = "game renderer not ready";
            return false;
        }
        var transparencies = ReflectionHelpers.GetFieldValue<PlanetTransparenciesRenderer>(program, "_planetTransparenciesRenderer");
        if (transparencies == null)
        {
            message = "Program._planetTransparenciesRenderer not found (game update?)";
            return false;
        }

        // In-flight frames may still reference ring pipelines/buffers/textures.
        Program.GetRenderer().Device.WaitIdle();
        DisposeRingsRenderer(transparencies);

        bool anyRings = transparencies.PopulatePlanets();
        ReflectionHelpers.SetFieldValue(transparencies, "_anyRings", anyRings);

        program.RebuildRenderer();
        message = anyRings ? "renderer rebuilt with rings" : "renderer rebuilt (no rings in system)";
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"bloomin-onion: renderer rebuild failed: {ex}");
        message = $"renderer rebuild failed: {ex.Message}";
        return false;
    }
}

/// <summary>True while the game's planetary rings renderer exists.</summary>
public static bool IsRingsRendererCreated()
{
    var transparencies = ReflectionHelpers.GetFieldValue(Program.Instance, "_planetTransparenciesRenderer");
    return ReflectionHelpers.GetFieldValue(transparencies, "_ringRendererCreated") is true;
}
```

4. If the rebuild failed, put the previous value back on the template (the controller does this in its `Apply`):

```csharp
// bloomin-onion.lib/RingDefinitionController.cs L85-L96
if (!_originals.ContainsKey(template)) _originals[template] = template.RingsReference;
var previous = template.RingsReference;
template.RingsReference = reference;

if (!RingRendererRebuilder.Rebuild(out var rebuildMessage))
{
    template.RingsReference = previous;
    message = rebuildMessage;
    return false;
}

_applied[celestial.Id] = new AppliedRing(celestial, definition.Clone(), reference);
```

## Complete example

| File | Role |
|---|---|
| [bloomin-onion.lib/RingRendererRebuilder.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/RingRendererRebuilder.cs) | Dispose, repopulate, set the flag, rebuild |
| [bloomin-onion.lib/RingDefinitionController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/bloomin-onion.lib/RingDefinitionController.cs) | Assign, revert on failure, prune |

## Variations and gotchas

- **Cost.** Two `WaitIdle` calls plus rebuilding every render target.
- **Restore** the template, then rebuild again; removing the last ring lets the rebuild end with no rings renderer.
- **Bodies whose far-away sphere bakes the data** (the `StaticCelestial` distant renderer) need a separate sync.

## See also

- [Overview](overview.md); [approach 1](change-loaded-game-data-and-make-the-renderer-notice-1.md)
- [Bloomin' Onion runtime planetary rings](../../../../mods/unscience/features/bloomin-onion-runtime-planetary-rings.md)
- [Handle renderer rebuilds](../../../rendering/vulkan-passes/handle-renderer-rebuilds/overview.md)
