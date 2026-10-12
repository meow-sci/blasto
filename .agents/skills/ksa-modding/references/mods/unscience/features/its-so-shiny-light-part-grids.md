---
type: Mod Feature
title: Light-part pixel grids switched through stock light switches
description: It's So Shiny builds Blinky-style grids from the stock LightPart, powers them by connecting each light to a battery part, toggles pixels through PowerConsumer.LightIsActive, recolors them with one template write per PartTemplate, and draws a light's mesh only while it is lit
mod: unscience
submod: its-so-shiny
category: rendering
techniques: [runtime-part-creation, part-tree-rebuild, reflection]
hooks:
  - "starmap StarMapAfterGui (via unscience ISubmod.RenderContent: create, scan, appearance, patterns)"
  - "starmap StarMapBeforeGui (via unscience ISubmod.Update: scroll tick, deferred destroy)"
  - harmony-prefix KSA.PartTreeRenderData.Compose / ComposeDynamic / ComposeGlass (shared PartRenderFilter, owner its-so-shiny)
  - harmony-postfix KSA.PartTreeRenderData.Compose / ComposeDynamic / ComposeGlass (shared PartRenderFilter, owner its-so-shiny)
  - reflection KSA.PartTemplate.Components / KSA.LightModule+TemplateData.Intensity / ColorRgb (via zippo LightController)
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
tags: [lights, parts, part-tree, power, grids, render-filter, reflection]
---

It's So Shiny is Blinky with lights instead of engines. The player builds a named grid of stock
`LightPart` parts on any loaded vessel (flat or cylindrical, configurable size, spacing, offset and
scale), picks a color and intensity, and lights pixels with the same pattern buttons. There is no
ignition and no propellant: each pixel is a real light whose stock light switch is flipped, and the
light's emissive mesh is drawn only while it is on, so an unlit pixel is invisible.[^shiny-lib-readme]

Mechanism in one line: create `LightPart` parts under the root, connect each to a battery part,
rebuild the tree once, then set `Part.LightSwitch.LightIsActive` per cell; color and intensity are
written to the light component of the shared `PartTemplate`.

## How it works

**Build.** `ShinyGridBuilder.BuildGrid` follows the
[Blinky builder](./blinky-runtime-part-grid-builder.md): `ModLibrary.Get<PartTemplate>("LightPart")`,
one `new Part($"shiny_{grid}_{row}_{col}", template)` per cell (no a/b pair, nothing to cancel),
`TreeParent = root` plus `root.TreeChildren.Add`, a single `PartTree.CreateFromNewPartTree(root)` and
`vehicle.UpdateVehicleConfiguration()`. Placement differs: the light is yawed by the column angle on
a cylinder and pitched -90 degrees about X (`doubleQuat.Concatenate(qY, qXNeg)`) so it faces outward.
Registration skips the whole-vessel rescan: `ShinyPixelGrid.CreateFromParts` parses only the newly
created parts, and `FindLightPart` walks sub-parts until it finds one whose template has a light
component. All lights are switched off before the grid is returned.[^shiny-builder][^shiny-grid]

**Power instead of fuel.** `FindBatteryParts` enumerates `vehicle.Parts.Modules.Get<Battery>()` and
collects each battery's `Parent.FullPart` (deduplicated), which also finds batteries on sub-parts.[^ksa-parttree]
Each light gets `SetStage(battery.Stage)` and a plain `Part.Connection.Connect(lightPart,
batteryPart)`, assigned round-robin across all anchors; the comment gives the same reason as Blinky,
spreading the per-consumer `PowerManager` graph searches over K anchors. Unlike the engines, lights
need no declared connector. `LightPart`'s game data is a single `<PowerConsumer LightSwitch="true">`
drawing 40 W.[^ksa-electrical-gamedata]

**Pixel on/off.** `ShinyPixelCell.SetEnabled` writes `LightIsActive` on the light part's
`LightSwitch` (falling back to the full part's), and only if neither has a switch does it fall back to
writing template intensity. `ApplyPattern`, `DisplayStatic` (same diffing as Blinky), `TurnOff` and
`ShinyScrollAnimation` (same column-stepped marquee) all go through it.[^shiny-cell][^shiny-manager][^shiny-scroll]

**Appearance, deduplicated per template.** Color and intensity live on the part template's
`LightModule.TemplateData` (`Intensity`, `ColorRgb`), not on the part. `ShinyGridManager.ApplyAppearance`
collects the distinct `PartTemplate` objects behind the grid's light parts (by reference) and calls
Zippo's `LightController.WriteColor` / `WriteIntensity` once per template instead of once per cell.
Those helpers find `KSA.LightModule+TemplateData` entries in `PartTemplate.Components` by reflection,
write `Intensity.Value` and `ColorRgb.R/G/B`, clear `IndexedColor` and re-run `OnDataLoad` so the RGB
sticks.[^zippo-light-controller] The game rebuilds lights from `Template.Intensity` and
`Template.ColorRgb` every frame in `LightModule.UpdateRenderData`, so the change shows on the next
frame.[^ksa-lightmodule]

**Lit-only meshes.** `ShinyPatches.Apply` registers the owner `its-so-shiny` with the shared
[part render filter](./lib-part-render-filter.md). The predicate hides a shiny part unless
"Render light meshes" is on, the part has no light switch, or `fullPart.LightSwitch.LightIsActive` is
true. Membership is `Id.StartsWith("shiny_")` or a `HashSet<Part>` of registered host parts, since
native saves drop the names.[^shiny-patches][^shiny-persistence]

**Destroy and saves.** Destroy switches every light off, disconnects, unlinks and rebuilds once
(deferred 0.25 s after the off step). The `its-so-shiny` save participant stores host and light part
paths, color, intensity, active mask and scroll state; replay rebinds the native parts and re-applies
appearance through `Register`.[^shiny-submod][^shiny-persistence]

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Create, scan, color/intensity edits, pattern buttons | `RenderContent` from `[StarMapAfterGui]` | `frame:draw-ui` |
| Scroll tick, deferred destroy | `ItsSoShinySubmod.Update` from `[StarMapBeforeGui]` or the hidden-HUD replay | `frame:draw-ui` |
| Switch state copied into the power state and power drawn | `PowerConsumer.UpdateModules`, called from `VehicleUpdateState` in the vehicle step | `physics:vehicle-solver` |
| Light color/intensity read; mesh compaction by `LightIsActive` | `LightModule.UpdateRenderData` and `PartTreeRenderData.Compose*` in `UpdateRenderingResources` | `frame:render` |
| Save replay | Save participant restore | `event:load` |

`LightIsActive` is a plain public field. The vehicle worker copies it into
`PowerConsumerState.Active` each step (`current.State.Active = current.Module.LightIsActive`) and only
then checks battery charge.[^ksa-powerconsumer][^ksa-vehicleupdatestate] Writing it from the UI is
what the game itself does: the stock part window's "Light Switch" checkbox binds the field directly,
and `Vehicle.ToggleLights` loops over parts setting it.[^ksa-vehicle] A toggle reaches the light state
at the next step and the mesh filter at the next render-resource update. The tree rebuild has the same UI-thread
timing caveat as the [Blinky builder](./blinky-runtime-part-grid-builder.md).

## Key code

| Path | Role |
|---|---|
| [its-so-shiny.lib/ShinyGridBuilder.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/its-so-shiny.lib/ShinyGridBuilder.cs#L12-L112) | Build: battery anchors, light parts, one rebuild, switch off |
| [its-so-shiny.lib/ShinyGridBuilder.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/its-so-shiny.lib/ShinyGridBuilder.cs#L152-L228) | Placement, battery discovery, power connection |
| [its-so-shiny.lib/ShinyPixelCell.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/its-so-shiny.lib/ShinyPixelCell.cs) | Per-cell switch and appearance |
| [its-so-shiny.lib/ShinyPixelGrid.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/its-so-shiny.lib/ShinyPixelGrid.cs) | `shiny_*` id parsing, light sub-part discovery |
| [its-so-shiny.lib/ShinyGridManager.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/its-so-shiny.lib/ShinyGridManager.cs#L184-L227) | Per-template appearance writes |
| [its-so-shiny.lib/ShinyPatches.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/its-so-shiny.lib/ShinyPatches.cs) | Lit-only render predicate |
| [zippo.lib/LightController.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/zippo.lib/LightController.cs#L30-L100) | Reflection writes to light template data |
| [its-so-shiny.lib/ItsSoShinySubmod.Persistence.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/its-so-shiny.lib/ItsSoShinySubmod.Persistence.cs) | Save participant, pixel membership |

## Snippets

Battery anchors and power connections:

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
// ...
    }
```

A pixel is a light switch:

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

One appearance write per distinct template:

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

Mesh visible only while the light is on:

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

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `PowerConsumer.LightIsActive` / `UpdateModules` / `DrawStateInfo` checkbox | [current/decomp/KSA/PowerConsumer.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PowerConsumer.cs#L30-L134) | Switch field, worker read, stock UI write |
| `VehicleUpdateState` -> `PowerConsumer.UpdateModules` | [current/decomp/KSA/VehicleUpdateState.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/VehicleUpdateState.cs#L404) | The vehicle-step call site |
| `Part.LightSwitch` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L688) | Per-part switch; reset and re-resolved when module properties are rebuilt (L1936-L1943) |
| `PartTree.Modules` + `ModuleList.Get<Battery>()` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L62) | Battery discovery across sub-parts |
| `LightModule.TemplateData.Intensity` / `ColorRgb`, `UpdateRenderData` | [current/decomp/KSA/LightModule.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LightModule.cs#L12-L128) | Template values written by reflection, read each frame |
| `Vehicle.ToggleLights` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1396-L1408) | Stock example of writing `LightIsActive` from the main thread |
| `LightPart` game data | [current/Content/Core/CoreElectricalAGameData.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/CoreElectricalAGameData.xml#L191-L195) | `PowerConsumer LightSwitch="true"`, 40 W |

## Pitfalls and version notes

- Appearance is global per template. Every `LightPart` in the game, including stock lights on other
  vessels and other shiny grids, shares the color and intensity of the last write. The authors note
  that per-grid colors cannot be isolated with this approach; Zippo's template ledger records the
  originals.[^shiny-lib-readme]
- The mesh predicate ignores power: a switched-on light on a dead battery still shows its mesh. It
  must read `LightSwitch` live, because `Part.ResetModuleProperties` replaces it.[^scope-pixel-grids][^ksa-part]
- No battery on the vessel means no connection and lights that never draw power; the build logs a
  warning but still succeeds.
- Lights are full parts with colliders and mass, like Blinky's engines; the default scale is 0.5.
- `ScanAllVehicles` registers discovered grids with the window's current color and intensity, which
  immediately rewrites the shared template.
- Raytraced IVA views bypass the render filter.

## See also

- [Blinky runtime part grid builder](./blinky-runtime-part-grid-builder.md)
- [Blinky scroll animation and patterns](./blinky-scroll-animation-and-patterns.md)
- [Part render filter](./lib-part-render-filter.md)
- [Zippo light control](./zippo-light-control.md) (owner of `LightController`)
- [It's So Shiny sub-mod](../submods/its-so-shiny.md)
- [Mod overview](../overview.md)
- [Build grids of light parts at runtime and switch each pixel through its stock light switch](../../../howto/rendering/lights-and-particles/control-part-light-color-and-state/control-part-light-color-and-state-3.md) - how-to article demonstrating this feature

[^shiny-builder]: its-so-shiny.lib/ShinyGridBuilder.cs
[^shiny-cell]: its-so-shiny.lib/ShinyPixelCell.cs
[^shiny-grid]: its-so-shiny.lib/ShinyPixelGrid.cs
[^shiny-manager]: its-so-shiny.lib/ShinyGridManager.cs
[^shiny-scroll]: its-so-shiny.lib/ShinyScrollAnimation.cs
[^shiny-patches]: its-so-shiny.lib/ShinyPatches.cs
[^shiny-persistence]: its-so-shiny.lib/ItsSoShinySubmod.Persistence.cs
[^shiny-submod]: its-so-shiny.lib/ItsSoShinySubmod.cs
[^zippo-light-controller]: zippo.lib/LightController.cs
[^shiny-lib-readme]: its-so-shiny.lib/README.md
[^scope-pixel-grids]: scope/pixel-grids-and-render.md (authors' integration analysis)
[^ksa-powerconsumer]: KSA.PowerConsumer (decompiled, build 2026.10.10.5554)
[^ksa-lightmodule]: KSA.LightModule (decompiled, build 2026.10.10.5554)
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-parttree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-vehicleupdatestate]: KSA.VehicleUpdateState (decompiled, build 2026.10.10.5554)
[^ksa-electrical-gamedata]: Core/CoreElectricalAGameData.xml (game content, build 2026.10.10.5554)
