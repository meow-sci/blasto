---
type: Mod Feature
title: Ego-centric re-rooting that follows the controlled vessel
description: Turns the physical celestial tree inside out at the body the vessel is in, climbing a spine of ancestor hub buses, and re-roots on window open, vessel switch, star-system crossing, System menu pick or Shift+click
mod: ksa-deltavmap
category: ui
techniques: [tree-rerooting, change-detection-by-reference]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Program, KSA.Vehicle, KSA.IParentBody, KSA.CelestialSystem, KSA.PhysicsStates]
tags: [ui, map, re-rooting, soi, vessel, tree, subway-map]
---

The map is drawn from the player's point of view: the body the vessel is currently in is the
root, its parent planet and star become horizontal "hub buses" above it, and every other body
hangs off those buses as its own metro line. When the vessel moves to another star system the map
re-roots there by itself; any other SOI change takes effect the next time the map is opened, and
Shift+click re-roots at any body on demand. Mechanism: `ReRooter.ReRoot` walks the parent chain of
the chosen body into a list of `HubLevel`s, `VisualTree.Build` materializes that inside-out tree,
and `MapWindow.FollowVessel` watches `Program.ControlledVehicle` and its `Parent` by reference to
decide when to rebuild.[^dvmap-rerooter][^dvmap-visualtree][^dvmap-mapwindow]

## How it works

**Re-rooting a tree.** `ReRooter.ReRoot(root)` climbs `PhysicalNode.Parent` until the system
root. For each ancestor it records a `HubLevel { Hub, SpineChild, OtherChildren }`: the body it
came up through (reached from below, so not branched again) and that body's siblings, in the
graph's deterministic order. Re-rooting at a moon gives moon -> planet hub -> star hub; at a
planet of a star in a multiple system the spine continues up to the barycenter.

**Materializing the visual tree.** `VisualTree.Build(graph, cache, root, egoState, options)`
turns that into `StateNode`s and `Edge`s:

- The root body's low orbit (or surface for a body too small to hold an orbit) is the tree root;
  its own rungs and moons branch off it.
- Each ancestor is a `StateKind.Hub` node joined to the previous spine node by a dV-free
  `HubLink`. Off a hub branch its own ladder (a transfer from the spine child to the hub's low
  orbit) and one sibling transfer per `OtherChildren` entry, priced from `DvCache.GetTransfer`
  plus a sibling plane change.
- A star or barycenter root ("cruise root") is itself the hub; a vessel on an open orbit around
  a system root with several systems present gets its targets on dV-free `Approach` edges.
- With several star systems, the ego system hub gets one `Interstellar` edge per other system
  (see [Multi-system scene composition](./multi-system-scene-composition.md)).
- `DetailLevel` keeps the full ladder for the root, its moons, the nearest hub's siblings and
  the spine, and only core rungs elsewhere, unless "Full ladder everywhere" is on.[^dvmap-detaillevel]

Hubs drawn as buses rather than points are what keeps a re-rooted tree from turning into a
starburst; the spine link carries no delta-v, so each pairwise `EdgeDv` stays intact on the
hub's outgoing spokes.

**Choosing the root.** `DesiredEgoRootId` follows the vessel unless a system was picked in the
**System** menu:

| Situation | Root |
|---|---|
| Following the vessel | `Program.ControlledVehicle.Parent.Id`, else `CelestialSystem.HomeBody`, else the home root |
| System picked, vessel in that system | The vessel's parent body |
| System picked, home body in that system | The home body |
| System picked, neither | That system's root hub |

`Vehicle.Parent` is `Orbit.Parent` in the decompiled game, i.e. the SOI body.[^ksa-vehicle] The
mod wraps every read of it in try/catch (`TryGetEgoParentId`) because it can throw for a vehicle
without a materialized orbit patch, and the code runs inside `ImGui.Begin/End`.

**When it re-roots.** A `_reevaluateRoot` flag makes the next `EnsureBuilt` recompute the root
and call `RebuildAt` if it changed. It is set by `Open()` (each time the window is shown), the
**Reset Root** menu item, a **System** pick, and `FollowVessel` when the controlled vehicle
object changes. `FollowVessel` compares the vehicle and its parent with the last seen values by
`ReferenceEquals` every frame; a parent change that lands in another star system (detected with
`SystemGraph.SystemRootOf`) re-roots immediately through `FollowTo`, which re-selects the old
route target if its node still exists. A parent change inside the same system does not re-root
until the next open, so a manual Shift+click re-root stays put while the vessel flies around.

**Shift+click.** `HandleInput` routes a click with `io.KeyShift` to `ReRootTo(hit)`, which
resolves the clicked layout node's `StateNode` and calls `RebuildAt(bodyId)`.

**Rebuilding at a root.** `RebuildAt` classifies the vessel's state on the new root's ladder
only when the vessel's parent is that root (`StateClassifier.Classify` or `ClassifyCruise`),
builds the visual tree, enforces the 8,000-node ceiling, runs the layout, rebuilds the lookup
dictionary, the scene, the transfer-window list, and resets the selection and the overlay panel
sizes on a genuine root change. Any exception latches `_buildFailedRootId` so a broken root is
not rebuilt every frame. `RebuildPreservingSelection` (used for visibility toggles) captures the
root node's screen position before the rebuild and solves the pan afterwards so the root does
not jump.

## Lifecycle and timing

- Hook: `[StarMapAfterGui]`, main thread, `frame:draw-ui`, via `MapWindow.DrawContent` ->
  `EnsureBuilt` -> `FollowVessel` / `RebuildAt`. Only while the window is shown and the HUD is
  visible.
- SOI changes are decided in the vehicle solver: `PhysicsStates.CheckSoiTransitions` switches
  the closest parent when the vessel leaves an SOI, enters a child's SOI, or (with several roots)
  crosses to another `IIndependentRoot` via `IIndependentRoot.FindOwner`.[^ksa-physicsstates] Those
  results are applied to the main-thread vehicle in `Program.PrepareFrame` before the UI draws,
  so `vehicle.Parent` read at AfterGui is the settled parent for this frame.[^ksa-program]
- Rebuilding must happen inside the draw because the layout measures labels with
  `ImGui.CalcTextSize`; the rebuild is event-driven (open, switch, crossing, menu, click), never
  per frame.

## Key code

| Path | Role |
|---|---|
| [DeltaVMap/Model/ReRooter.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/ReRooter.cs) | Parent-chain walk into hub levels |
| [DeltaVMap/Model/VisualTree.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/VisualTree.cs#L117-L214) | Inside-out tree: root ladder, hub spine, siblings, destination systems |
| [DeltaVMap/Model/DetailLevel.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/DetailLevel.cs) | Full ladder near the root, core rungs far away |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L1806-L1892) | `DesiredEgoRootId`, `FollowVessel`, `FollowTo` |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L1925-L2115) | `RebuildPreservingSelection`, `RebuildAt` |

## Snippets

```csharp
// DeltaVMap/Model/ReRooter.cs L35-L64
public static ReRootResult ReRoot(PhysicalNode root)
{
    var spine = new List<HubLevel>();

    PhysicalNode spineChild = root;
    PhysicalNode? hub = root.Parent;
    while (hub != null)
    {
        // Children are already sorted deterministically in the SystemGraph, so
        // filtering the spine child out preserves that order for the siblings.
        var others = new List<PhysicalNode>(hub.Children.Count);
        foreach (PhysicalNode child in hub.Children)
        {
            if (!ReferenceEquals(child, spineChild))
                others.Add(child);
        }

        spine.Add(new HubLevel
        {
            Hub = hub,
            SpineChild = spineChild,
            OtherChildren = others
        });

        spineChild = hub;
        hub = hub.Parent;
    }

    return new ReRootResult { Root = root, Spine = spine };
}
```

```csharp
// DeltaVMap/Render/MapWindow.cs L1838-L1877
private void FollowVessel(CelestialSystem system)
{
    // ...
    Vehicle? vehicle;
    IParentBody? parent;
    try
    {
        vehicle = Program.ControlledVehicle;
        parent = vehicle?.Parent;
    }
    catch (Exception ex)
    {
        LogHelper.WarnOnce("follow-vessel", $"[DvMap] Could not read the controlled vehicle: {ex.Message}");
        return;
    }

    bool vehicleChanged = !ReferenceEquals(vehicle, _egoVehicle);
    bool parentChanged = !ReferenceEquals(parent, _egoParent);
    _egoVehicle = vehicle;
    _egoParent = parent;
    if (_mapSystemId != null || !_built)
        return;
    if (vehicleChanged)
    {
        _reevaluateRoot = true;
        return;
    }
    if (!parentChanged || parent == null || _graph == null || !_graph.HasSeveralSystems)
        return;

    PhysicalNode? node = _graph.Find(parent.Id);
    PhysicalNode? shown = CurrentSystemRoot();
    if (node == null || shown == null || ReferenceEquals(SystemGraph.SystemRootOf(node), shown))
        return;
    string? root = DesiredEgoRootId(system);
    if (root != null && root != _buildFailedRootId)
        FollowTo(root);
}
```

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `Program.ControlledVehicle` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs) | The vessel the map follows (null in the editor) |
| `Vehicle.Parent` (`=> Orbit.Parent`) | [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs) | Current SOI body, root of the map |
| `CelestialSystem.HomeBody` | (see [live celestial system graph](./live-celestial-system-graph.md)) | Fallback root without a vessel |
| `PhysicsStates.CheckSoiTransitions` | [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs) | Not called; where the parent change the mod watches originates |

## Pitfalls and version notes

- Compare the vehicle and its parent by reference, not by Id, and keep the last-seen values even
  while a system is pinned, so returning to "Follow vessel" does not misfire on a stale change.
- `Vehicle.Parent` walks the orbit; guard it. A freshly spawned or mid-transition vehicle can
  throw, and an exception inside `DrawContent` would otherwise skip `ImGui.End`.
- README wording: the map "re-roots automatically on the next open after an SOI change or a
  crossing into another star system". In code the crossing case re-roots immediately while the
  window is open (`FollowTo`); the in-system SOI case waits for the next `Open()`.
- Body Ids are unique across all star systems of a `CelestialSystem`, which is what lets
  `FollowTo` keep a selected route target after the root moves to another system.

## See also

- [Live celestial system graph](./live-celestial-system-graph.md)
- [Body state ladder and "you are here"](./body-state-ladder-and-you-are-here.md)
- [Route finding and accumulation](./route-finding-and-accumulation.md)
- [Search, focus, isolate and minor-body aggregation](./search-focus-isolate-and-aggregation.md)
- [Three layout modes](./three-layout-modes.md)
- [Re-root a diagram when the controlled vessel changes: reference comparison after PrepareFrame](../../../howto/ui/draw-lists/lay-out-a-tree-or-graph-diagram/lay-out-a-tree-or-graph-diagram-4.md) - how-to article demonstrating this feature

[^dvmap-rerooter]: DeltaVMap/Model/ReRooter.cs
[^dvmap-visualtree]: DeltaVMap/Model/VisualTree.cs
[^dvmap-mapwindow]: DeltaVMap/Render/MapWindow.cs
[^dvmap-detaillevel]: DeltaVMap/Model/DetailLevel.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
