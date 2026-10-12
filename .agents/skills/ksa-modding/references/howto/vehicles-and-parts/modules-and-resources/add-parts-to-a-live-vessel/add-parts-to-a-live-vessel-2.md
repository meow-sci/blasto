---
type: How-To
title: Wire FeedConnectors so runtime-created engines find propellant
description: Connect each runtime engine's own declared RocketCore.FeedConnectors to a tank part, align its stage with the tank, rebuild resource managers and verify with ConsumptionOrder
category: vehicles-and-parts
group: modules-and-resources
topic: add-parts-to-a-live-vessel
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/blinky-engine-pixel-feed-wiring]
techniques: [resource-graph-wiring]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:prepare, event:load]
game_types:
  - KSA.RocketCore
  - KSA.PlumbedCore
  - KSA.Part
  - KSA.ResourceManager
  - KSA.Tank
  - KSA.ResourceGroupList
  - KSA.PartTree
  - KSA.FlowOrder
tags: [feed-connectors, propellant, resource-graph, stage, engines, diagnostics]
---
Outcome: engines you create at runtime actually burn the vessel's propellant. Blinky's engine pixels added mass for many
builds but never lit, because KSA's resource graph refused to route propellant to them. The fix: connect the engine's own
declared feed connector to a tank, put the engine in the tank's stage, let the game rebuild its resource managers and
count the tanks the engine can reach.

Prerequisite: the part-building of [article 1](add-parts-to-a-live-vessel-1.md). Demonstrated by
[unscience/blinky-engine-pixel-feed-wiring](../../../../mods/unscience/features/blinky-engine-pixel-feed-wiring.md). The reading side is [article 3](add-parts-to-a-live-vessel-3.md) and
[Read live part module state: engines, tanks, propellant, batteries, panels, docking ports](../read-live-part-module-state/overview.md).

## Background: how the game does it

- In `ResourceManager.CanFlowAcross` the first hop out of the consumer part is rejected unless the connection sits on
  one of the consumer's declared `FeedConnectors` (`IsDeclaredFeedConnection`). The base manager also requires
  `connection.HasCapabilities(RequiredCapability)`, which intersects both endpoints' `EndpointCapabilities`; a plain
  `Part` endpoint has none unless it is a fuel-port part, and the null intersection never contains `BulkFluid`. Both gates
  fail, so the engine's consumption order is empty (Blinky's analysis; `ResourceManager.cs` is the code to read).
- `RocketCore.FeedConnectors` is bound in `OnFullPartCreated` -> `BindFeedConnectors` from the template's
  `ConsumerFeedWiring` / `FeedsFrom`, so it already exists on a freshly constructed part, before any tree exists
  [current/decomp/KSA/RocketCore.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketCore.cs#L20)[^ksa-rocketcore] [current/decomp/KSA/RocketCore.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketCore.cs#L76) [current/decomp/KSA/RocketCore.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketCore.cs#L107-L119).
- `Part.Connection.Connect(IConnector, IConnector)` requires both sides to `CanConnect()`; a bare `Part` always can, so one
  tank can anchor any number of engines [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L538-L552)[^ksa-part].
- Flow rules such as `NearestToFurtherestSameStage` filter by stage, so engines default-staged 0 see only tanks of stage 0
  (`FlowOrder.SameStageOnly`) [current/decomp/KSA/FlowOrder.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlowOrder.cs#L15-L20)[^ksa-floworder].
- `PlumbedCore.ResourceManager`'s getter calls `Tree.EnsureResourceManagersBuilt()`, so reading it rebuilds dirty managers
  on the calling thread [current/decomp/KSA/PlumbedCore.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlumbedCore.cs#L60-L100)[^ksa-plumbedcore]. `ResourceGroupList.CalculateStages(reconfigureTankContents)` is
  public and calls the internal `RecreateResourceManagers` [current/decomp/KSA/ResourceGroupList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ResourceGroupList.cs#L100)[^ksa-resourcegrouplist].

## Lifecycle and timing

| Step | Where | Frame |
|---|---|---|
| connect, stage-align, rebuild | the build, from `StarMapAfterGui` | `frame:draw-ui` |
| resource managers of the new tree | forced by `EnsureDerived(DerivedData.ResourceGroups)` for the check; otherwise flushed in the next `PrepareFrame` before `ExecuteNextVehicleSolvers` | `frame:draw-ui`, `frame:prepare` |
| repair during save replay | `RestoreGrids` in the save participant | `event:load` |

The managers the worker uses for the step already dispatched this frame are not affected until the next `PrepareFrame`, so
the first frame after a build runs without the new engines' feeds. The forced `EnsureDerived` / `CalculateStages` rebuild itself runs on the UI thread while that step is in flight, so it carries the same race as the build in [article 1](add-parts-to-a-live-vessel-1.md) (see its known-risk paragraph); route both through a settled window if the vessel is being stepped. See [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md).

## Steps

1. **Collect the part's distinct feed connectors** from every core in `part.SubtreeModules` (cores often share one
   connector):

```csharp
// blinky.lib/LcdGridBuilder.cs L495-509
    private static List<Part.Connector> GetFeedConnectors(Part part)
    {
        var result = new List<Part.Connector>();
        var cores = part.SubtreeModules.Get<RocketCore>();
        for (int i = 0; i < cores.Length; i++)
        {
            var feeds = cores[i].FeedConnectors;
            for (int j = 0; j < feeds.Length; j++)
            {
                if (!result.Contains(feeds[j]))
                    result.Add(feeds[j]);
            }
        }
        return result;
    }
```

2. **Connect each to a tank part.** The tank side stays a bare `Part`:

```csharp
// blinky.lib/LcdGridBuilder.cs L461-486
    private static bool ConnectToFuel(Part pixelPart, Part fuelPart)
    {
        try
        {
            bool connected = false;
            foreach (var connector in GetFeedConnectors(pixelPart))
            {
                if (!connector.CanConnect()) continue;

                if (Part.Connection.Connect(connector, fuelPart))
                    connected = true;
                else
                    Console.WriteLine($"blinky: Connection.Connect returned false for '{pixelPart.Id}' connector '{connector.Id}'");
            }

            if (!connected)
                Console.WriteLine($"blinky: '{pixelPart.Id}' has no free declared feed connector — it will reach no propellant");

            return connected;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"blinky: error connecting '{pixelPart.Id}' to fuel: {ex.Message}");
            return false;
        }
    }
```

3. **Align stages and spread the load.** Set each engine's stage to its tank's stage before the rebuild, and distribute
   engines round-robin over every part with a `Tank` module so build time falls by about K^2 for K anchors:

```csharp
// blinky.lib/LcdGridBuilder.cs L108-134
        // ── Fuel connections — round-robin across all fuel parts ─────────────────
        // Each engine is assigned to exactly one fuel part (by index mod K).
        // This creates K isolated subgraphs of ~N/K engines each, reducing the
        // O(N³) PopulateGraph cost to O(N³/K²).
        //
        // Stage alignment: pixel parts default to stage 0. KSA's resource managers
        // use FlowRule.NearestToFurtherestSameStage, so they only find tanks whose
        // stage matches the engine's stage. We set each pixel part's stage to match
        // its fuel anchor before the PartTree rebuild so resource managers are
        // created with correct stage filtering.
        if (fuelParts.Count > 0)
        {
            sw.Restart();
            int fedParts = 0;
            for (int i = 0; i < createdParts.Count; i++)
            {
                var fuelPart = fuelParts[i % fuelParts.Count];
                createdParts[i].SetStage(fuelPart.Stage);
                if (ConnectToFuel(createdParts[i], fuelPart))
                    fedParts++;
            }
            Console.WriteLine($"blinky: stage-aligned {createdParts.Count} pixel parts to stage {fuelParts[0].Stage} (fuel anchor: '{fuelParts[0].Id}')");
            Console.WriteLine($"blinky: fuel-fed {fedParts}/{createdParts.Count} pixel parts via their declared feed connectors");
            if (fedParts < createdParts.Count)
                Console.WriteLine($"blinky: WARNING — {createdParts.Count - fedParts} pixel part(s) got no propellant feed and will never light");
            timings.Add(($"Fuel connections + stage align ({createdParts.Count} parts, {fuelParts.Count} anchors)", sw.ElapsedMilliseconds));
        }
```

4. **Force the lazily built resource groups** if you will read them now, then verify by counting tank entries across all
   levels of each combustor's `ConsumptionOrder`:

```csharp
// blinky.lib/LcdGridBuilder.cs L165-172
        // ── Propellant-feed verification ─────────────────────────────────────────
        // KSA 5482 recomputes derived tree data lazily (normally at the next frame's flush), so
        // resource managers do not exist yet. Build the resource groups now, as 5438 did eagerly,
        // or every pixel engine reports no reachable tank.
        sw.Restart();
        vehicle.Parts.EnsureDerived(DerivedData.ResourceGroups);
        VerifyPropellantFeeds(createdParts);
        timings.Add(("VerifyPropellantFeeds", sw.ElapsedMilliseconds));
```

```csharp
// blinky.lib/PropellantFeedDiagnostics.cs L8-16
    internal static int CountTanks(ResourceManager? manager)
    {
        if (manager == null) return 0;
        var order = manager.ConsumptionOrder;
        int count = 0;
        for (int level = 0; level < order.LevelCount; level++)
            count += order[level].Length;
        return count;
    }
```

```csharp
// blinky.lib/LcdGridBuilder.cs L619-651
    private static void VerifyPropellantFeeds(List<Part> parts)
    {
        int fedParts = 0;
        var starvedCores = new Dictionary<string, int>();
        string desiredMix = "?";

        foreach (var part in parts)
        {
            bool anyFed = false;
            var cores = part.SubtreeModules.Get<RocketCore>();
            for (int i = 0; i < cores.Length; i++)
            {
                if (cores[i] is not Combustor combustor) continue;

                if (desiredMix == "?") desiredMix = combustor.DesiredMix?.Name ?? "?";

                if (PropellantFeedDiagnostics.CountTanks(combustor.ResourceManager) > 0)
                    anyFed = true;
                else
                    starvedCores[combustor.TemplateId] = starvedCores.GetValueOrDefault(combustor.TemplateId) + 1;
            }
            if (anyFed) fedParts++;
        }

        Console.WriteLine($"blinky: propellant feed check — {fedParts}/{parts.Count} pixel parts reached at least one tank");
        if (fedParts == 0)
            Console.WriteLine($"blinky: WARNING — no pixel can light. The engines burn '{desiredMix}'; the vehicle's tanks must carry it, and the pixel parts must share the tanks' stage.");
        else if (fedParts < parts.Count)
            Console.WriteLine("blinky: WARNING — starved pixel parts will never light; check stage alignment and tank contents");

        foreach (var (coreId, count) in starvedCores)
            Console.WriteLine($"blinky:   starved core '{coreId}' x{count} (expected for gas-generator cores burning a propellant the vehicle does not carry)");
    }
```

5. **Repair without a rebuild** for parts that have no connected feed connector (scanned grids, older builds, save replay):
   re-wire them and call `vehicle.Parts.ResourceGroupList.CalculateStages()` (default `reconfigureTankContents: false`, so
   the vessel's tank mixes are never rewritten).
6. **Also ignite and throttle.** A pixel needs the vessel ignited (`Vehicle.SetEnum(VehicleEngine.MainIgnite)`, see
   [Switch engines on and off, set throttle and RCS commands, and choose which RCS jets fire](../command-engines-throttle-and-rcs-from-code/overview.md)) and a throttle above zero; warn when the burn mode is Auto.

## Complete example

- [blinky.lib/LcdGridBuilder.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/LcdGridBuilder.cs#L108-L134)[^unscience-lcdgridbuilder], [blinky.lib/LcdGridBuilder.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/LcdGridBuilder.cs#L270-L316) - connect and repair
- [blinky.lib/PropellantFeedDiagnostics.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/PropellantFeedDiagnostics.cs)[^unscience-propellantfeeddiagnostics]

## Variations and gotchas

- **The mix.** Every A2-A6 thrust chamber burns Hydrolox in Blinky's case, so the vessel's tanks must hold it; the
  verification message names the desired mix when nothing is fed. Gas-generator cores may burn a propellant the vessel
  does not carry (expected starved cores).
- **Do not call the all-tanks reconfiguration** unless you mean to: `CalculateStages(true)` rewrites tank contents.
- **Stage first, rebuild second.** Stage filtering is applied when resource managers are created.
- **Diagnose.** Log each core's `FlowRule`, level and tank counts and where each feed connector is connected for one sample
  part; that was how the original problem was found.

## See also

- [Overview](overview.md), [Article 1](add-parts-to-a-live-vessel-1.md), [Article 3](add-parts-to-a-live-vessel-3.md)
- [unscience/blinky-engine-pixel-feed-wiring](../../../../mods/unscience/features/blinky-engine-pixel-feed-wiring.md)
- [Read live part module state: engines, tanks, propellant, batteries, panels, docking ports](../read-live-part-module-state/overview.md), [Switch engines on and off, set throttle and RCS commands, and choose which RCS jets fire](../command-engines-throttle-and-rcs-from-code/overview.md)

[^ksa-resourcegrouplist]: current/decomp/KSA/ResourceGroupList.cs (decompiled, build 2026.10.10.5554)
[^ksa-rocketcore]: current/decomp/KSA/RocketCore.cs (decompiled, build 2026.10.10.5554)
[^ksa-plumbedcore]: current/decomp/KSA/PlumbedCore.cs (decompiled, build 2026.10.10.5554)
[^ksa-floworder]: current/decomp/KSA/FlowOrder.cs (decompiled, build 2026.10.10.5554)
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^unscience-lcdgridbuilder]: unscience: blinky.lib/LcdGridBuilder.cs
[^unscience-propellantfeeddiagnostics]: unscience: blinky.lib/PropellantFeedDiagnostics.cs
