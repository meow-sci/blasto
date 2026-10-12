---
type: Game Feature
title: Creating and attaching parts to a live vessel and feeding runtime engines
description: How to build real parts on a flying vessel by hand-wiring the part tree, why a runtime engine finds no propellant until its declared feed connector is connected, and how to read the resource graph
category: vehicles-and-parts
group: modules-and-resources
topic: add-parts-to-a-live-vessel
demonstrated_by:
  - mods/unscience/features/blinky-runtime-part-grid-builder
  - mods/unscience/features/blinky-engine-pixel-feed-wiring
  - mods/kitten-engineer-redux/features/custom-stage-delta-v-estimator
lifecycle: [frame:draw-ui, frame:prepare, event:load, editor:draw-ui]
game_types:
  - KSA.Part
  - KSA.PartTree
  - KSA.Vehicle
  - KSA.RocketCore
  - KSA.PlumbedCore
  - KSA.ResourceManager
  - KSA.Tank
  - KSA.FlowOrder
  - KSA.ResourceGroupList
tags: [part-tree, runtime-parts, resource-graph, feed-connectors, tanks, engines]
---
KSA builds a vessel's part tree in the editor or from a save. A mod that wants real parts on a vessel that is already
flying has to do by hand what those paths do: create `Part` objects, wire `TreeParent`/`TreeChildren`, swap in a rebuilt
`PartTree`, tell the solver state about the new configuration, and connect the parts to the resource graph. The last step
is what trips almost everyone: a connected engine is not a fed engine.

## Engine background

- **Part creation.** `new Part(id, template)` builds a part and its subparts from a `PartTemplate`; `PositionParentAsmb`,
  `Asmb2ParentAsmb` and `Scale` place it [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L1490-L1500)[^ksa-part]. Tree links are the public fields
  `Part.TreeParent` and `Part.TreeChildren` [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L666-L668).
- **Tree rebuild.** `PartTree.CreateFromNewPartTree(root)` walks the whole `TreeChildren` hierarchy, transfers every
  part and module into a new tree and recomputes derived data (since build 5464 it only marks it dirty; the next frame's
  flush rebuilds) [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L291-L296)[^ksa-parttree] [current/decomp/KSA/PartTree.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PartTree.cs#L475-L478). The stock tree surgery (`Vehicle.Split`,
  docking merges) runs from `InputEvents.ApplyInputEvents`.
- **Solver state.** `Vehicle.UpdateVehicleConfiguration()` calls `Parts.ReinitializeDerivedValues()`, then
  `_threadWorkerUpdateState.InitializeUpdateData()` (which re-sizes the worker's output lists to the new gimbal count),
  then `UpdateAfterPartTreeModification()` [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L1877-L1892)[^ksa-vehicle]. `UpdateAfterPartTreeModification` alone leaves
  the worker's lists stale and `UpdateTvcParams` throws on the next step (per Blinky's source comment).
- **Feeds.** `RocketCore.FeedConnectors` is bound in `OnFullPartCreated` from the template's `ConsumerFeedWiring` /
  `FeedsFrom` data, so it exists on a freshly constructed part [current/decomp/KSA/RocketCore.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketCore.cs#L20)[^ksa-rocketcore] [current/decomp/KSA/RocketCore.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketCore.cs#L76)
  [current/decomp/KSA/RocketCore.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/RocketCore.cs#L107). `Part.Connection.Connect(IConnector, IConnector)` joins two connectors
  [current/decomp/KSA/Part.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Part.cs#L538).
- **The resource graph.** A `PlumbedCore.ResourceManager.ConsumptionOrder` is a `FlowOrder<Tank>` of levels (nearest to
  farthest); the getter calls `Tree.EnsureResourceManagersBuilt()` [current/decomp/KSA/PlumbedCore.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PlumbedCore.cs#L60-L100)[^ksa-plumbedcore] [current/decomp/KSA/FlowOrder.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FlowOrder.cs#L19)[^ksa-floworder].

## Approaches

| Article | Mod | Technique | Subject |
|---|---|---|---|
| [1: an N x M engine grid with one tree rebuild](add-parts-to-a-live-vessel-1.md) | unscience (Blinky) | `runtime-part-creation` | create, wire, rebuild once, reinitialise the solver |
| [2: wire `FeedConnectors` after the grid build](add-parts-to-a-live-vessel-2.md) | unscience (Blinky) | `resource-graph-wiring` | why engines find no propellant, and the fix |
| [3: walk `ConsumptionOrder` levels](add-parts-to-a-live-vessel-3.md) | Kitten-Engineer-Redux | `stage-model-recompute` | which tanks an engine can reach |

## See also

- [game loop and lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-3.md), [Create, destroy and mutate vehicles, part state and mass without racing the vehicle solver](../../vehicles/change-vehicles-without-racing-the-solver/overview.md)
- [Switch engines on and off, set throttle and RCS commands, and choose which RCS jets fire](../command-engines-throttle-and-rcs-from-code/overview.md), [Spawn a vehicle from code (from the vehicle library or a hand-built part)](../../vehicles/spawn-a-vehicle-at-runtime/overview.md), [Read staged delta-v, thrust and Isp from the game or from your own stage model](../../staging-and-engines/read-staged-delta-v-from-the-game/overview.md)
- Mod features: [unscience/blinky-runtime-part-grid-builder](../../../../mods/unscience/features/blinky-runtime-part-grid-builder.md), [unscience/blinky-engine-pixel-feed-wiring](../../../../mods/unscience/features/blinky-engine-pixel-feed-wiring.md), [kitten-engineer-redux/custom-stage-delta-v-estimator](../../../../mods/kitten-engineer-redux/features/custom-stage-delta-v-estimator.md)

[^ksa-rocketcore]: current/decomp/KSA/RocketCore.cs (decompiled, build 2026.10.10.5554)
[^ksa-plumbedcore]: current/decomp/KSA/PlumbedCore.cs (decompiled, build 2026.10.10.5554)
[^ksa-floworder]: current/decomp/KSA/FlowOrder.cs (decompiled, build 2026.10.10.5554)
[^ksa-part]: current/decomp/KSA/Part.cs (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: current/decomp/KSA/Vehicle.cs (decompiled, build 2026.10.10.5554)
[^ksa-parttree]: current/decomp/KSA/PartTree.cs (decompiled, build 2026.10.10.5554)
