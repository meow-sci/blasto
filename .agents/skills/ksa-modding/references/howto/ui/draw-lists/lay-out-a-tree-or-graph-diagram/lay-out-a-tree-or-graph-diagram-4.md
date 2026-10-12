---
type: How-To
title: "Re-root a diagram when the controlled vessel changes: reference comparison after PrepareFrame"
description: Walk the parent chain into hub levels, compare Program.ControlledVehicle and its Parent by reference each frame, and rebuild or re-root on a vessel switch or star-system crossing but not on an in-system SOI change
category: ui
group: draw-lists
topic: lay-out-a-tree-or-graph-diagram
approach: 4
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksa-deltavmap/features/ego-rerooting-and-vessel-follow]
techniques: [imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, frame:apply-solvers]
game_types: [KSA.Program, KSA.Vehicle, KSA.IParentBody, KSA.CelestialSystem, KSA.PhysicsStates]
tags: [re-root, soi, vehicle, follow, parent-chain, reference-comparison]
---
KSA-DeltaVMap draws the map from the player's point of view: the body the vessel is in is the root, its parent planet and star become horizontal hub buses above it, and every other body hangs off those buses. When the vessel moves to another star system the map re-roots by itself; any other SOI change takes effect the next time the map is opened, and Shift+click re-roots at any body on demand. The detection is a reference comparison against the controlled vehicle and its parent, done each frame in the UI. Demonstrated by [Ego-centric re-rooting that follows the controlled vessel](../../../../mods/ksa-deltavmap/features/ego-rerooting-and-vessel-follow.md). Prerequisite: [approach 1](lay-out-a-tree-or-graph-diagram-1.md) for the layout that the new root feeds.

## Background: how the game does it

`Program.ControlledVehicle` is a static property (null in the editor or with no controlled vehicle; [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502-L512)[^ksa-program]). `Vehicle.Parent` is `Orbit.Parent`, the SOI body as an `IParentBody` ([current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L373)[^ksa-vehicle]). SOI changes are decided in the vehicle solver: `PhysicsStates.CheckSoiTransitions` ([current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L704)[^ksa-physicsstates]) runs on worker threads, and `Program.PrepareFrame` applies the finished vehicle step to the main-thread vehicle with `Universe.ApplyVehicleSolvers` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2166-L2167)[^ksa-program]) before the UI draws. The `Parent` the UI reads is therefore the settled parent for the frame. `CelestialSystem.HomeBody` ([current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L64)[^ksa-celestialsystem]) is the fallback root when there is no vessel.

## Lifecycle and timing

The detection runs in `frame:draw-ui` (`[StarMapAfterGui]` through `MapWindow.DrawContent`, `EnsureBuilt`, `FollowVessel`/`RebuildAt`), after `frame:apply-solvers` in the same frame's `PrepareFrame`. Rebuilding must happen inside the draw because layout measures labels with `ImGui.CalcTextSize`. The rebuild is event-driven (open, switch, crossing, menu, click), never per frame. See [Game loop and mod lifecycle](../../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Walk the parent chain into hub levels.** For each ancestor record the body you came up through and the siblings that remain, in a deterministic order:

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

2. **Materialize an inside-out tree.** The root body's low orbit (or surface) is the tree root; each ancestor is a hub node joined to the previous spine node by a delta-v-free hub link, and each hub branches its siblings. Hubs drawn as buses rather than points are what keeps a re-rooted tree from becoming a starburst ([DeltaVMap/Model/VisualTree.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/VisualTree.cs#L117-L214)).

3. **Choose the root.** Follow the vessel unless a system was picked: `Program.ControlledVehicle.Parent.Id`, else `CelestialSystem.HomeBody`, else the home root. Wrap every read of `Parent` in try/catch, because it can throw for a vehicle without a materialized orbit patch and the code runs inside `ImGui.Begin`/`End`.

4. **Compare by reference every frame.** Keep the last-seen vehicle and parent even while a system is pinned. A new vehicle sets a re-evaluate flag; a parent change that lands in another star system re-roots immediately; a parent change inside the same system waits for the next open, so a manual Shift+click re-root stays put while the vessel flies around:

```csharp
// DeltaVMap/Render/MapWindow.cs L1838-L1877
    private void FollowVessel(CelestialSystem system)
    {
#if DEBUG
        using var perf = new PerfTracker.Scope("DvMap.Follow");
#endif
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

5. **Rebuild at the new root.** `RebuildAt` classifies the vessel's state on the new root's ladder only when its parent is that root, builds the tree, enforces the node ceiling (approach 2), runs the layout, and rebuilds the lookup, the scene and the transfer-window list. Any exception latches `_buildFailedRootId` so a broken root is not rebuilt every frame ([DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L1925-L2115)).

6. **Preserve the view on soft rebuilds.** `RebuildPreservingSelection` captures the root node's screen position before the rebuild and solves the pan afterwards so the root does not jump.

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Model/ReRooter.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/ReRooter.cs) | Parent-chain walk into hub levels |
| [DeltaVMap/Model/VisualTree.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/VisualTree.cs#L117-L214) | Inside-out tree |
| [DeltaVMap/Model/DetailLevel.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/DetailLevel.cs) | Full ladder near the root, core rungs far away |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L1806-L1892) | `DesiredEgoRootId`, `FollowVessel`, `FollowTo` |

## Variations and gotchas

- **Reference, not Id.** Compare the vehicle and its parent with `ReferenceEquals` so a respawned object with the same Id is detected, and keep the last-seen values while a system is pinned so returning to "Follow vessel" does not misfire.
- **Guard `Vehicle.Parent`.** It walks the orbit; an exception inside the draw would skip `ImGui.End` and corrupt the frame.
- **README vs. code.** The mod's README says it re-roots on the next open after an SOI change or crossing; in code the crossing re-roots immediately while the window is open, and only the in-system SOI case waits.
- **Body Ids are unique across all star systems** of a `CelestialSystem`, which lets a selected route target survive a root change.
- **Version.** Depends on `Program.ControlledVehicle` and `Vehicle.Parent` in the checked build.

## See also

- [Overview of tree and graph layout](overview.md)
- [Approach 3: stable composition](lay-out-a-tree-or-graph-diagram-3.md)
- [Show live vehicle and editor data in an ImGui window or HUD](../../../../howto/ui/windows/show-live-vehicle-data-in-a-window/overview.md)
- Mod feature: [Ego-centric re-rooting that follows the controlled vessel](../../../../mods/ksa-deltavmap/features/ego-rerooting-and-vessel-follow.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
