---
type: How-To
title: Latch on the Universe.CurrentSystem reference, not its Id
description: Rebuild a per-system graph and every cache derived from it only when the CelestialSystem object changes, because reloading the same id creates new body objects
category: celestials
group: systems-and-stars
topic: cache-and-key-body-data-per-loaded-system
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksa-deltavmap/features/live-celestial-system-graph]
techniques: [per-system-cache]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Universe, KSA.CelestialSystem]
tags: [cache, invalidation, reference-latch, currentsystem]
---
Outcome: a graph, a transfer cost cache and a colour palette that are rebuilt exactly when the loaded system is replaced, and never otherwise. Demonstrated by KSA-DeltaVMap ([Live celestial system graph](../../../../mods/ksa-deltavmap/features/live-celestial-system-graph.md)).

## Background: how the game does it

`Universe.LoadSystem` builds `CurrentSystem = new CelestialSystem(systemTemplate)` every time ([current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs#L232-L240)), so reloading the same system id gives new body objects. A node that holds an old body reads orbits that no longer update. Id equality therefore proves nothing; reference equality of the `CelestialSystem` does. Within a loaded system Keplerian orbits are fixed, so pair-wise derived data never needs invalidating.

## Lifecycle and timing

The check runs every frame in the window's draw (`[StarMapAfterGui]`, `frame:draw-ui`), via `MapWindow.EnsureBuilt`. The build is lazy: it runs on the first frame the window is shown after a load, not at `Universe.OnLoaded`; while the window is closed nothing is read. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Latch on the object, rebuild everything per system together:**

```csharp
// DeltaVMap/Render/MapWindow.cs L1742-L1777
private bool EnsureBuilt()
{
    CelestialSystem? system = Universe.CurrentSystem;
    if (system == null)
        return false;

    // Latched on the system instance, not its Id: loading the same system again builds new
    // bodies, and nodes holding the old ones would read orbits that no longer update.
    if (_graph == null || !ReferenceEquals(_graph.System, system))
    {
        SystemGraph? graph = SystemGraph.Build(system);
        if (graph == null)
            return false;
        _graph = graph;
        _cache = new DvCache();
        _palette = ColorPalette.Build(graph);
        _currentRootId = null;
        _rootLabel = null;
        _mapSystemId = null;
        _interstellar.Reset();
        _canvas.Reset();
        _tooltips.Reset();
        _routePanel.Reset();
        _egoVehicle = null;
        _egoParent = null;
        _buildFailedRootId = null;
        _selectedId = null;
        _routeSummary = null;
        _routeNodeIds = null;
        _routeSiblingId = null;
        _built = false;

        // A new system invalidates the search state (the bodies are different).
        _revealedBodyIds.Clear();
        _searchResults.Clear();
        _searchLabels.Clear();
```

2. **Key pair caches by an ordinal-ordered pair of ids,** valid across all star systems because ids are unique across the `CelestialSystem`: a transfer between two bodies is computed once and never invalidated within a loaded system (`DvCache`).

3. **Reset UI state with it** (search results, selection, focus, routes): after a system change the bodies are different.

4. **Remember a failed build** (`_buildFailedRootId`) so a tree that threw is not rebuilt every frame.

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L1740-L1804) | `EnsureBuilt` reference latch |
| [DeltaVMap/Dv/DvCache.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Dv/DvCache.cs) | Per-system transfer cache keyed by body pair |
| [DeltaVMap/Render/ColorPalette.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/ColorPalette.cs) | Per-system colours built with the graph |

## Variations and gotchas

- **Do not latch on the id.** The same id reloaded is a different object graph.
- **Do not key by `WorldSun`;** it moves with the camera (see [enumerate star systems](../enumerate-star-systems-and-bodies/overview.md)).
- **Keep vehicles out of the cached graph.** Read the controlled vehicle separately per frame.

## See also

- [Overview](overview.md); [approach 2](cache-and-key-body-data-per-loaded-system-2.md)
- [Live celestial system graph](../../../../mods/ksa-deltavmap/features/live-celestial-system-graph.md)
- [Enumerate star systems and bodies](../../../celestials/systems-and-stars/enumerate-star-systems-and-bodies/overview.md)
