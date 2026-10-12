---
type: How-To
title: Build grids of light parts at runtime and switch each pixel through its stock light switch
description: Create LightPart instances under a vessel's root, power them from batteries with Part.Connection, flip each part's PowerConsumer.LightIsActive per pixel, and recolour the whole grid with one template write per PartTemplate
category: rendering
group: lights-and-particles
topic: control-part-light-color-and-state
approach: 3
difficulty: advanced
prerequisites: [starmap, harmony, reflection]
demonstrated_by: [mods/unscience/features/its-so-shiny-light-part-grids]
techniques: [runtime-part-creation, harmony-prefix, harmony-postfix, reflection]
hooks:
  - starmap StarMapAfterGui
  - starmap StarMapBeforeGui
  - harmony-prefix KSA.PartTreeRenderData.Compose
  - harmony-postfix KSA.PartTreeRenderData.Compose
  - reflection KSA.PartTemplate.Components
lifecycle: [frame:draw-ui, frame:render, physics:vehicle-solver, event:load]
game_types:
  - KSA.Part
  - KSA.PartTree
  - KSA.PartTemplate
  - KSA.Vehicle
  - KSA.ModLibrary
  - KSA.Battery
  - KSA.PowerConsumer
  - KSA.LightModule
tags: [lights, light-part, pixel-grid, power, battery, runtime-parts]
---
Outcome: a named grid of stock `LightPart` parts (flat or cylindrical) on any loaded vessel, each pixel a real light turned on and off with the same switch KSA's own light key uses. There is no ignition and no propellant: a pixel's light and its emissive lens appear together, and an unlit pixel is invisible.

Demonstrated by unscience's It's So Shiny ([Light-part pixel grids switched through stock light switches](../../../../mods/unscience/features/its-so-shiny-light-part-grids.md)). Prerequisites: StarMap, Harmony (for the lit-only mesh filter), reflection (for the shared colour writes). This composes [approach 1](control-part-light-color-and-state-1.md) with the part-hiding filter from [hide parts or subparts](../../meshes/hide-parts-or-subparts-from-rendering/hide-parts-or-subparts-from-rendering-1.md). Building parts at runtime is a vehicles-and-parts topic that is not yet written; this article shows the minimum for lights.

## Background: how the game does it

`LightPart`'s game data is a single `<PowerConsumer LightSwitch="true">` drawing 40 W ([current/Content/Core/CoreElectricalAGameData.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/CoreElectricalAGameData.xml#L191-L195)). The light's colour and intensity live in the template's `LightModule.TemplateData` (see approach 1), read every frame in `LightModule.UpdateRenderData`. The worker copies the switch flag into the power state each step, then checks battery charge:

```csharp
// current/decomp/KSA/PowerConsumer.cs L93-L108
public static void UpdateModules(ref readonly ModuleUpdateContext context)
{
	if (!context.Updaters.TryGetOrCreateTypeStateUpdater(out ModuleStateful<PowerConsumer, PowerConsumerState, EmptyStruct, EmptyStruct>.StateUpdater typeUpdater) || !ModuleStateful<Battery, BatteryState, EmptyStruct, EmptyStruct>.TryGetOrCreateFrom(context.Updaters, out ModuleStateful<Battery, BatteryState, EmptyStruct, EmptyStruct>.StateUpdater updater))
	{
		return;
	}
	ModuleStateful<PowerConsumer, PowerConsumerState, EmptyStruct, EmptyStruct>.StateUpdater.ModuleAndNewStateEnumerator enumerator = typeUpdater.GetModulesAndNewStates().GetEnumerator();
	while (enumerator.MoveNext())
	{
		ModuleStateful<PowerConsumer, PowerConsumerState, EmptyStruct, EmptyStruct>.StateUpdater.ModuleAndNewStateRef current = enumerator.Current;
		if (current.Module.LightSwitch)
		{
			current.State.Active = current.Module.LightIsActive;
		}
		if (current.State.Active)
		{
```

Writing `LightIsActive` from the main thread is what the game does itself: the stock part window's "Light Switch" checkbox binds the field, and `Vehicle.ToggleLights` loops over parts setting it ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1396-L1409)).

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Create, scan, color or intensity edits, pattern buttons | `RenderContent` from `[StarMapAfterGui]` | `frame:draw-ui` |
| Scroll tick, deferred destroy | submod `Update` from `[StarMapBeforeGui]`, or a hidden-HUD replay | `frame:draw-ui` |
| Switch state copied into the power state and power drawn | `PowerConsumer.UpdateModules` in the vehicle step | `physics:vehicle-solver` |
| Light colour or intensity read; mesh compaction by `LightIsActive` | `LightModule.UpdateRenderData` and `PartTreeRenderData.Compose*` in `UpdateRenderingResources` | `frame:render` |
| Save replay | save participant restore | `event:load` |

A toggle reaches the light state at the next step and the mesh filter at the next render-resource update. The tree rebuild when creating or destroying a grid happens on the UI thread in `frame:draw-ui`; see [the lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence).

## Steps

1. **Create the parts, power them, rebuild once.** `ModLibrary.Get<PartTemplate>("LightPart")`, one `new Part($"shiny_{grid}_{row}_{col}", template)` per cell, `TreeParent = root` plus `root.TreeChildren.Add`, a power `Part.Connection.Connect` to a battery part (round-robin over all battery anchors to spread the power-graph searches), one `PartTree.CreateFromNewPartTree(root)` and `vehicle.UpdateVehicleConfiguration()`:

   ```csharp
// its-so-shiny.lib/ShinyGridBuilder.cs L40-L51, L53-L64, L73-L104
        // Find every battery anchor on the vehicle BEFORE creating new parts so that
        // round-robin partitioning across all available batteries is maximised. With K
        // distinct battery anchors the per-PowerConsumer DFS in PowerManager.PopulateGraph
        // and CreateOrders only sees ~N/K consumers, taking total cost from O(N^3) to
        // ~O(N^3/K^2). Enumerating Modules.Get<Battery>() picks up batteries that live
        // on sub-parts too, not just top-level parts.
        var batteryParts = FindBatteryParts(vehicle);
        timings.Add(($"FindBatteryParts ({batteryParts.Count} found)", sw.ElapsedMilliseconds));
        if (batteryParts.Count > 0)
            Console.WriteLine($"its-so-shiny: found {batteryParts.Count} battery anchor(s) for partitioned connections: {string.Join(", ", batteryParts.Select(p => p.Id))}");
        else
            Console.WriteLine("its-so-shiny: WARNING - no battery parts found; light switches may not receive power");
// ...
        var createdParts = new List<Part>(config.TotalParts);

        sw.Restart();
        for (int row = 0; row < config.Height; row++)
        {
            for (int col = 0; col < config.Width; col++)
            {
                var part = CreateLightPartInstance(template, gridName, row, col, config);
                if (part != null)
                    createdParts.Add(part);
            }
        }
// ...
        sw.Restart();
        foreach (var part in createdParts)
        {
            part.TreeParent = root;
            root.TreeChildren.Add(part);
        }
        timings.Add(($"Tree wiring ({createdParts.Count} parts)", sw.ElapsedMilliseconds));

        if (batteryParts.Count > 0)
        {
            sw.Restart();
            for (int i = 0; i < createdParts.Count; i++)
            {
                var batteryPart = batteryParts[i % batteryParts.Count];
                createdParts[i].SetStage(batteryPart.Stage);
                ConnectToPower(createdParts[i], batteryPart);
            }
            timings.Add(($"Power connections + stage align ({createdParts.Count} parts, {batteryParts.Count} anchors)", sw.ElapsedMilliseconds));
        }

        sw.Restart();
        vehicle.Parts = PartTree.CreateFromNewPartTree(root);
        timings.Add(("PartTree.CreateFromNewPartTree", sw.ElapsedMilliseconds));

        sw.Restart();
        vehicle.UpdateVehicleConfiguration();
        timings.Add(("UpdateVehicleConfiguration", sw.ElapsedMilliseconds));

        sw.Restart();
        var grid = ShinyPixelGrid.CreateFromParts(createdParts, gridName);
        TurnOffLights(grid);
        timings.Add(($"CreateFromParts + TurnOffLights ({grid.Count} pixels)", sw.ElapsedMilliseconds));
```

2. **Place each part.** Yaw by column angle on a cylinder, pitched -90 degrees about X so it faces outward, scaled:

   ```csharp
// its-so-shiny.lib/ShinyGridBuilder.cs L152-L194
private static Part? CreateLightPartInstance(PartTemplate template, string gridName, int row, int col, ShinyGridConfig config)
{
    try
    {
        string partId = ShinyPixelGrid.MakeCellId(gridName, row, col);
        var part = new Part(partId, template);

        double px;
        double py;
        double pz;
        double rotY;

        if (config.Layout == ShinyGridLayout.Cylinder)
        {
            double radius = (config.Width * config.Spacing) / (2.0 * Math.PI);
            double theta = col * 2.0 * Math.PI / config.Width;
            px = config.OffsetX + radius * Math.Sin(theta);
            py = config.OffsetY - row * config.Spacing;
            pz = config.OffsetZ + radius * Math.Cos(theta);
            rotY = theta;
        }
        else
        {
            px = config.OffsetX + col * config.Spacing;
            py = config.OffsetY - row * config.Spacing;
            pz = config.OffsetZ;
            rotY = 0.0;
        }

        part.PositionParentAsmb = new double3(px, py, pz);
        double halfAngle = rotY / 2.0;
        var qY    = new doubleQuat(0, Math.Sin(halfAngle), 0, Math.Cos(halfAngle));
        var qXNeg = new doubleQuat(-Math.Sin(Math.PI / 4.0), 0, 0, Math.Cos(Math.PI / 4.0));
        part.Asmb2ParentAsmb = doubleQuat.Concatenate(qY, qXNeg);
        part.Scale = new double3(config.PartScale, config.PartScale, config.PartScale);
        return part;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"its-so-shiny: error creating shiny_{gridName}_{row}_{col}: {ex.Message}");
        return null;
    }
}
```

3. **Discover battery anchors** with `vehicle.Parts.Modules.Get<Battery>()`, resolving each module's owner to its `FullPart` (this also finds batteries on sub-parts):

   ```csharp
// its-so-shiny.lib/ShinyGridBuilder.cs L201-L228
private static List<Part> FindBatteryParts(Vehicle vehicle)
{
    var result = new List<Part>();
    var seen = new HashSet<Part>();
    var batteries = vehicle.Parts.Modules.Get<Battery>();
    for (int i = 0; i < batteries.Length; i++)
    {
        var battery = batteries[i];
        if (battery?.Parent == null) continue;
        var anchor = battery.Parent.FullPart;
        if (anchor != null && seen.Add(anchor))
            result.Add(anchor);
    }
    return result;
}

private static void ConnectToPower(Part lightPart, Part batteryPart)
{
    try
    {
        if (!Part.Connection.Connect(lightPart, batteryPart))
            Console.WriteLine($"its-so-shiny: Connection.Connect returned false for '{lightPart.Id}'");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"its-so-shiny: error connecting '{lightPart.Id}' to power: {ex.Message}");
    }
}
```

4. **A pixel is a light switch.** Write `LightIsActive` on the part's switch (falling back to the full part's); only if neither has a switch fall back to template intensity:

   ```csharp
// its-so-shiny.lib/ShinyPixelCell.cs L22-L32
public void SetEnabled(bool enabled, float onIntensity)
{
    var lightSwitch = LightPart.LightSwitch ?? LightPart.FullPart.LightSwitch;
    if (lightSwitch != null)
    {
        lightSwitch.LightIsActive = enabled;
        return;
    }

    LightController.ApplyIntensity(LightPart, enabled ? onIntensity : 0f);
}
```

5. **One appearance write per distinct template**, not per cell, reusing approach 1's `WriteColor` and `WriteIntensity`:

   ```csharp
// its-so-shiny.lib/ShinyGridManager.cs L184-L206
private static void ApplyAppearance(ShinyGridState state, float3 color, float intensity)
{
    state.Color = color;
    state.Intensity = Math.Clamp(intensity, 0f, 25f);

    foreach (var template in GetDistinctLightTemplates(state))
        ApplyAppearance(template, state.Color, state.Intensity);
}

private static IEnumerable<PartTemplate> GetDistinctLightTemplates(ShinyGridState state)
{
    var templates = new List<PartTemplate>();
    foreach (var cell in state.ShinyGrid.Grid.Cells.Values)
    {
        var template = cell.LightPart.Template;
        if (template == null || ContainsTemplate(templates, template))
            continue;

        templates.Add(template);
    }

    return templates;
}
```

   ```csharp
// its-so-shiny.lib/ShinyGridManager.cs L219-L227
private static void ApplyAppearance(PartTemplate template, float3 color, float intensity)
{
    var lights = MeowSci.ZippoLib.LightController.GetLightComponents(template);
    if (lights.Count == 0)
        return;

    MeowSci.ZippoLib.LightController.WriteColor(lights, color);
    MeowSci.ZippoLib.LightController.WriteIntensity(lights, intensity);
}
```

6. **Draw the lens only while lit.** Register an owner predicate with the shared part-render filter: hide a shiny part unless "Render light meshes" is on, the part has no switch, or its switch is active:

   ```csharp
// its-so-shiny.lib/ShinyPatches.cs L18-L22
public static void Apply(Harmony harmony)
{
    PartRenderFilter.Register(harmony, FilterOwner, fullPart => !ShouldRenderShinyPart(fullPart));
    Console.WriteLine("its-so-shiny.lib: render-skip patches applied");
}
```

   ```csharp
// its-so-shiny.lib/ShinyPatches.cs L32-L39
private static bool ShouldRenderShinyPart(Part fullPart)
{
    if (!ShinyGridManager.IsPixelPart(fullPart)) return true;
    if (ShinyPatchState.RenderShinyParts) return true;
    var ls = fullPart.LightSwitch;
    if (ls == null) return true; // no switch — can't determine state, render to be safe
    return ls.LightIsActive;
}
```

## Complete example

| File | Role |
|---|---|
| [its-so-shiny.lib/ShinyGridBuilder.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/its-so-shiny.lib/ShinyGridBuilder.cs) | Build: battery anchors, light parts, one rebuild, switch off |
| [its-so-shiny.lib/ShinyPixelCell.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/its-so-shiny.lib/ShinyPixelCell.cs) | Per-cell switch and appearance |
| [its-so-shiny.lib/ShinyPixelGrid.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/its-so-shiny.lib/ShinyPixelGrid.cs) | `shiny_*` id parsing, light sub-part discovery |
| [its-so-shiny.lib/ShinyGridManager.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/its-so-shiny.lib/ShinyGridManager.cs#L184-L227) | Per-template appearance writes |
| [its-so-shiny.lib/ShinyPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/its-so-shiny.lib/ShinyPatches.cs) | Lit-only render predicate |
| [zippo.lib/LightController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/LightController.cs#L30-L100) | Reflection writes to light template data |

## Variations and gotchas

- **Appearance is global per template.** Every `LightPart` in the game, including stock lights on other vessels and other shiny grids, shares the colour and intensity of the last write; per-grid colours cannot be isolated this way (approach 5 shows the per-instance route).
- **No battery on the vessel** means no connection and lights that never draw power; the build logs a warning but still succeeds.
- **The mesh predicate ignores power**: a switched-on light on a dead battery still shows its mesh. Read `LightSwitch` live, because `Part.ResetModuleProperties` replaces it.
- **Lights are full parts** with colliders and mass; the default scale is 0.5.
- **Raytraced IVA views bypass the render filter.**
- **Scanning existing grids** registers them with the window's current colour and intensity, which immediately rewrites the shared template.

## See also

- [Overview](overview.md)
- [Approach 1](control-part-light-color-and-state-1.md), [approach 4](control-part-light-color-and-state-4.md)
- [Light-part pixel grids switched through stock light switches](../../../../mods/unscience/features/its-so-shiny-light-part-grids.md)
- [Hide part meshes or subparts without removing them](../../meshes/hide-parts-or-subparts-from-rendering/overview.md)
- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
