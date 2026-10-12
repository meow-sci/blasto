---
type: Mod Feature
title: Feed runtime-created engines through their declared feed connectors
description: Blinky connects each pixel engine's RocketCore.FeedConnectors entry to a tank part, aligns its stage, rebuilds the resource managers and reads Combustor.ResourceManager.ConsumptionOrder to prove every engine can reach propellant
mod: unscience
submod: blinky
category: vehicles-and-parts
techniques: [runtime-part-creation, resource-graph-wiring]
hooks: ["starmap StarMapAfterGui (via unscience ISubmod.RenderContent: Create, Repair Feed, Diagnose, Ignite)"]
lifecycle: [frame:draw-ui, frame:prepare, event:load]
game_types:
  - KSA.RocketCore
  - KSA.PlumbedCore
  - KSA.Combustor
  - KSA.Part
  - KSA.ResourceManager
  - KSA.ResourceManagerBase
  - KSA.FlowOrder
  - KSA.ResourceGroupList
  - KSA.PartTree
  - KSA.ConnectorCapabilityExtensions
  - KSA.Vehicle
  - KSA.VehicleEngine
  - KSA.FlightComputerBurnMode
tags: [engines, propellant, resource-manager, connectors, staging, diagnostics]
---

A part that a mod creates at runtime is not fed just because it is connected to a tank. Blinky's
engine pixels built and added mass for many builds but never lit, because KSA's resource graph
refused to route propellant to them. The fix, documented in the source and the authors' scope
notes, is to connect the engine's own declared feed connector to the tank, put the engine in the
tank's stage, and let the game rebuild its resource managers.[^blinky-readme][^scope-pixel-grids]

Mechanism in one line: for every `RocketCore` on the new part, `Part.Connection.Connect(core.FeedConnectors[i], tankPart)`,
`part.SetStage(tankPart.Stage)`, rebuild the tree (or call `ResourceGroupList.CalculateStages()`),
then count `combustor.ResourceManager.ConsumptionOrder` entries to verify.

## How it works

**Why a plain part-to-part connection fails.** In `ResourceManager.CanFlowAcross` the first hop out
of the consumer part is rejected unless the connection sits on one of the consumer's
`FeedConnectors` (`IsDeclaredFeedConnection`). The base `ResourceManagerBase.CanFlowAcross` also
requires `connection.HasCapabilities(RequiredCapability)`, which intersects both endpoints'
`EndpointCapabilities`.[^ksa-resourcemanager][^ksa-resourcemanagerbase] A `Part` endpoint has
`EndpointCapabilities == null` unless it is a fuel-port part with no tanks, and
`ConnectorCapabilityExtensions.Intersect(null, null)` returns `Electricity | ServiceFluid`, never
`BulkFluid`.[^ksa-part][^ksa-connector-caps] Both gates fail, so the engine's consumption order is
empty.

**What Blinky connects instead.** `GetFeedConnectors(part)` collects the distinct
`Part.Connector` objects in `RocketCore.FeedConnectors` for every core in `part.SubtreeModules`.
The game binds that array in `RocketCore.OnFullPartCreated` -> `BindFeedPoints` from the template's
`ConsumerFeedWiring` / `FeedsFrom` data, so it already exists on a freshly constructed part, before
any tree exists.[^ksa-rocketcore] For EngineA3 both the thrust chamber and the gas generator feed
from `_connector3`, declared with `<Capabilities>BulkFluid</Capabilities>`.[^ksa-propulsion-gamedata]
`ConnectToFuel` calls `Part.Connection.Connect(connector, fuelPart)`. The tank side stays a bare
`Part`: `Part.CanConnect()` always returns true, so one tank can anchor any number of engines, and
the null tank capability intersects to the connector's own `BulkFluid`.[^lcd-grid-builder]

**Stage alignment and partitioning.** Before the rebuild every pixel part gets
`SetStage(fuelPart.Stage)` so stage-filtered flow rules (`...SameStage` in `FlowRule`, which
`ConsumptionOrder` turns into a same-stage-only `FlowOrder`) still see the tank. Pixels are spread
round-robin over every top-level part that carries a `Tank` module (sub-parts only as a fallback).
The source comment explains the reason: with N engines on one tank, each manager's graph search
visits all N+1 nodes with list-based visited checks, so build time grows roughly as N^3; K anchors cut
it by about K^2.

**Verification.** After the rebuild, `vehicle.Parts.EnsureDerived(DerivedData.ResourceGroups)` forces
the lazily built resource groups, and `VerifyPropellantFeeds` counts tank entries across all levels
of each `Combustor.ResourceManager.ConsumptionOrder` (`PropellantFeedDiagnostics.CountTanks`). The
console reports `N/N pixel parts reached at least one tank`, names the desired mix when nothing is
fed (every A2-A6 thrust chamber burns `Hydrolox`, so the vessel's tanks must hold it), and lists
starved cores separately because gas-generator cores may burn a propellant the vessel does not
carry.[^feed-diagnostics][^ksa-plumbedcore][^ksa-floworder]

**Repair without a rebuild.** `RepairFuelFeeds` re-wires only parts that have no connected feed
connector (scanned grids, grids from older builds, save replay), then calls
`vehicle.Parts.ResourceGroupList.CalculateStages()`. That public method is used because it calls the
internal `PartTree.RecreateResourceManagers`; it is invoked with the default
`reconfigureTankContents: false`, so the vessel's tank mixes are never rewritten.[^ksa-resourcegrouplist][^ksa-parttree]

**Ignition and throttle.** A pixel also needs the vessel ignited and a throttle above zero. Every
pattern, scroll or static call runs `vehicle.SetEnum(VehicleEngine.MainIgnite)`, which sets
`_manualControlInputs.EngineOn`. The window warns when the throttle is zero or the flight computer is
in `FlightComputerBurnMode.Auto` (which, per the UI text, clears ignition every frame), and offers an
Ignite button.[^ksa-vehicle] **Diagnose** logs ignition, throttle and burn mode, the number of
controllers that reach propellant, and for one sample pixel each core's `FlowRule`, level and tank
counts and where each feed connector is connected.[^blinky-submod]

## Lifecycle and timing

| Step | Where | Lifecycle |
|---|---|---|
| Connect, stage align, `MinimumThrottle`, rebuild | `BuildGrid`, from the Create button in `RenderContent` (`[StarMapAfterGui]`) | `frame:draw-ui` |
| Resource managers of the new tree | Forced by `EnsureDerived(DerivedData.ResourceGroups)` for the check; otherwise `PartTree.FlushDirtyDerived` / `FlushDirtyResourceManagers` in the next `PrepareFrame` (`Program.cs` L2218-L2219), before `ExecuteNextVehicleSolvers` | `frame:draw-ui`, `frame:prepare` |
| Repair Feed / Diagnose / Ignite | Buttons in `RenderContent` | `frame:draw-ui` |
| Repair during save replay | `RestoreGrids` in the save participant | `event:load` |

The `PlumbedCore.ResourceManager` getter calls `Parent.FullPart.Tree.EnsureResourceManagersBuilt()`
before returning, so reading it from the UI rebuilds dirty managers on the main thread at that moment
rather than at the next flush.[^ksa-plumbedcore] The managers the vehicle worker uses for the step
already dispatched this frame are not affected until the next `PrepareFrame`; the first frame after a
build therefore runs without the new engines' feeds, which is harmless because pixel activation is
itself applied at the next frame's input phase (see [patterns](./blinky-scroll-animation-and-patterns.md)).

## Key code

| Path | Role |
|---|---|
| [blinky.lib/LcdGridBuilder.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/LcdGridBuilder.cs#L108-L134) | Round-robin anchors, stage alignment, feed connections |
| [blinky.lib/LcdGridBuilder.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/LcdGridBuilder.cs#L270-L316) | `RepairFuelFeeds` with `CalculateStages` |
| [blinky.lib/LcdGridBuilder.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/LcdGridBuilder.cs#L417-L509) | `FindAllFuelParts`, `ConnectToFuel`, `GetFeedConnectors` |
| [blinky.lib/LcdGridBuilder.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/LcdGridBuilder.cs#L619-L651) | `VerifyPropellantFeeds` console report |
| [blinky.lib/PropellantFeedDiagnostics.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/PropellantFeedDiagnostics.cs) | Counts tank entries in a `FlowOrder<Tank>` |
| [blinky.lib/BlinkySubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/BlinkySubmod.cs#L184-L219) | Ignition / throttle / Auto burn warning and Ignite button |
| [blinky.lib/BlinkySubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/blinky.lib/BlinkySubmod.cs#L656-L772) | Diagnose report |

## Snippets

Connecting the declared feed connectors to a tank part:

```csharp
// blinky.lib/LcdGridBuilder.cs L461-L479
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
```

```csharp
// blinky.lib/LcdGridBuilder.cs L495-L509
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

The gate in the game that a bare connection fails:

```csharp
// current/decomp/KSA/ResourceManager.cs L255-L297
	protected override bool CanFlowAcross(Part.Connection connection, Part from, Part to)
	{
		if (!base.CanFlowAcross(connection, from, to))
		{
			return false;
		}
		if (from == _part.FullPart && !IsDeclaredFeedConnection(connection))
		{
			return false;
		}
// ...
	private bool IsDeclaredFeedConnection(Part.Connection connection)
	{
		ReadOnlySpan<Part.Connector> feedConnectors = Consumer.FeedConnectors;
		for (int i = 0; i < feedConnectors.Length; i++)
		{
			Part.Connector connector = feedConnectors[i];
			if (connector.Connection == connection)
			{
				return true;
			}
		}
		return false;
	}
```

Reachability check used after build, repair and in Diagnose:

```csharp
// blinky.lib/PropellantFeedDiagnostics.cs L8-L16
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

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `RocketCore.FeedConnectors`, `OnFullPartCreated` -> `BindFeedPoints` | [current/decomp/KSA/RocketCore.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketCore.cs#L20-L78) | Declared feed connectors, bound at part construction |
| `Part.Connection.Connect(IConnector, IConnector)` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L538-L552) | Creates the feed connection |
| `Part.Connection.HasCapabilities`, `Part.EndpointCapabilities`, `Part.CanConnect()` | [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1183) | Why a bare part endpoint lacks `BulkFluid`, and why one tank can take many connections |
| `ConnectorCapabilityExtensions.Intersect` | [current/decomp/KSA/ConnectorCapabilityExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConnectorCapabilityExtensions.cs#L46-L80) | `null & null` -> `Electricity | ServiceFluid` |
| `ResourceManager.CanFlowAcross` / `IsDeclaredFeedConnection` | [current/decomp/KSA/ResourceManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ResourceManager.cs#L255-L297) | First-hop gate |
| `ResourceManagerBase.CanFlowAcross` / `ConsumptionOrder` | [current/decomp/KSA/ResourceManagerBase.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ResourceManagerBase.cs#L81-L97) | Capability gate; flow-rule view of the order |
| `FlowOrder<T>.LevelCount` / indexer | [current/decomp/KSA/FlowOrder.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlowOrder.cs#L19-L38) | Counting reachable tanks |
| `PlumbedCore.ResourceManager` (base of `Combustor`) | [current/decomp/KSA/PlumbedCore.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlumbedCore.cs#L19-L29) | Lazily rebuilt manager; `DesiredMix` names the propellant |
| `ResourceGroupList.CalculateStages(bool)` | [current/decomp/KSA/ResourceGroupList.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ResourceGroupList.cs#L100) | Public trigger for `RecreateResourceManagers` |
| `PartTree.EnsureDerived` / `RecreateResourceManagers` | [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L521) | Forced resource-group build; internal rebuild |
| `Vehicle.SetEnum(VehicleEngine.MainIgnite)`, `IsSet`, `GetManualThrottle` | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L6409-L6425) | Ignition write and the warning's reads |
| `CorePropulsionA_Prefab_EngineA3` `ConsumerFeedWiring` / `_connector3` | [current/Content/Core/CorePropulsionAGameData.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/CorePropulsionAGameData.xml#L232-L257) | The data the feed connectors are bound from |

## Pitfalls and version notes

- The original wiring, `Connection.Connect(pixelPart, fuelPart)`, compiles and adds mass but is
  never fed; the authors trace the dark grid back to the 5018 fuel rewrite.[^scope-pixel-grids]
  Their earlier research also covered the missing stage alignment and ignition.[^blinky-fix-plan]
- In 5554 `ResourceManager` lives on `PlumbedCore`, the base of `Combustor` and of `ThermalCore`.
  Blinky's checks only look at `Combustor` cores, which covers the CorePropulsionA engines; other
  plumbed cores would be reported as "not a Combustor".
- If the vessel has no tank parts, nothing is connected and the console warns that engines will not
  fire. Since content rev 5475 some fairing parts carry `Tank` modules, so they can become anchors
  (and drain points) too.[^scope-pixel-grids]
- A gas-generator core sharing the same connector may still report as starved when the vessel lacks
  its propellant; the main chamber still fires.
- `CalculateStages()` is relied on for its side effect of rebuilding managers; if a future build stops
  calling `RecreateResourceManagers` there, Repair Feed silently does nothing.

## See also

- [Runtime part grid builder](./blinky-runtime-part-grid-builder.md)
- [Scroll animation, patterns and pixel control](./blinky-scroll-animation-and-patterns.md)
- [Eternal Flame resource refill](./eternal-flame-resource-refill.md) (another mod touching tanks)
- [Blinky sub-mod](../submods/blinky.md)
- [Mod overview](../overview.md)
- [Wire FeedConnectors so runtime-created engines find propellant](../../../howto/vehicles-and-parts/modules-and-resources/add-parts-to-a-live-vessel/add-parts-to-a-live-vessel-2.md) - how-to article demonstrating this feature

[^lcd-grid-builder]: blinky.lib/LcdGridBuilder.cs
[^feed-diagnostics]: blinky.lib/PropellantFeedDiagnostics.cs
[^blinky-submod]: blinky.lib/BlinkySubmod.cs
[^blinky-readme]: blinky/README.md
[^blinky-fix-plan]: plans/BLINKY_FIX.md (authors' ignition regression research)
[^scope-pixel-grids]: scope/pixel-grids-and-render.md (authors' integration analysis)
[^ksa-rocketcore]: KSA.RocketCore (decompiled, build 2026.10.10.5554)
[^ksa-plumbedcore]: KSA.PlumbedCore (decompiled, build 2026.10.10.5554)
[^ksa-resourcemanager]: KSA.ResourceManager (decompiled, build 2026.10.10.5554)
[^ksa-resourcemanagerbase]: KSA.ResourceManagerBase (decompiled, build 2026.10.10.5554)
[^ksa-floworder]: KSA.FlowOrder (decompiled, build 2026.10.10.5554)
[^ksa-part]: KSA.Part (decompiled, build 2026.10.10.5554)
[^ksa-connector-caps]: KSA.ConnectorCapabilityExtensions (decompiled, build 2026.10.10.5554)
[^ksa-resourcegrouplist]: KSA.ResourceGroupList (decompiled, build 2026.10.10.5554)
[^ksa-parttree]: KSA.PartTree (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-propulsion-gamedata]: Core/CorePropulsionAGameData.xml (game content, build 2026.10.10.5554)
