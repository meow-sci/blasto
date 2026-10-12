---
type: How-To
title: Keep a huge diagram usable with aggregation, search-to-reveal and a hard node ceiling
description: Fold minor bodies above a threshold into one group node, let a whole-universe search pull a single body back out, and refuse to lay out more than 8,000 nodes on the draw thread
category: ui
group: draw-lists
topic: lay-out-a-tree-or-graph-diagram
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/ksa-deltavmap/features/search-focus-isolate-and-aggregation]
techniques: [imgui-window, imgui-drawlist]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, KSA.MinorBody, KSA.Comet, KSA.Astronomical]
tags: [aggregation, search, reveal, isolate, scale, ceiling, minor-bodies]
---
A modded system can carry thousands of asteroids. KSA-DeltaVMap stays usable by folding them: a hub with more than 24 visible minor bodies shows one "+N" group node instead of one lane each, star systems beyond the eight nearest fold into a "+N star systems" node, and a body-name search over the whole universe pulls any single body back out, centres it and optionally isolates the map to it. Finally, a hard ceiling refuses to lay out more than 8,000 nodes. Demonstrated by [Search, focus, isolate and minor-body aggregation for dense systems](../../../../mods/ksa-deltavmap/features/search-focus-isolate-and-aggregation.md). Prerequisite: a tree you build from your data before layout (see [approach 1](lay-out-a-tree-or-graph-diagram-1.md)).

## Background: how the game does it

The classification comes from the game's class hierarchy: `Comet` extends `MinorBody` ([current/decomp/KSA/Comet.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Comet.cs#L3)[^ksa-comet]) which extends `TerrestrialBody` ([current/decomp/KSA/MinorBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MinorBody.cs#L3)[^ksa-minorbody]). The mod treats every `MinorBody` as minor, except the `MinorBody` children of a star or barycenter when the system treats them as planets, and treats `Comet` instances separately for the "show comets" toggle. Search uses the ImGui binding's `ImInputString` overload of `InputTextWithHint`, which returns true only when the text was edited.

## Lifecycle and timing

Search runs on the frame the text changes (in `MapWindow.DrawPanel`, `frame:draw-ui`); the rebuild runs only on reveal, isolate toggle, visibility toggle or clear. Aggregation and the ceiling happen in `RebuildAt`. Only celestial metadata (body Ids, `MinorBody` / `Comet` type tests) is read, which is fixed for a loaded system. See [Game loop and mod lifecycle](../../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Decide visibility in one function.** Revealed bodies always pass (ahead of every hide rule); isolate hides every un-revealed minor body; "show minor bodies" off hides minor bodies; "show comets" off hides comets. The root and spine ancestors never pass through it:

```csharp
// DeltaVMap/Model/VisualTree.cs L96-L115
private bool Include(PhysicalNode body)
{
    Astronomical a = body.Astro;

    // A revealed (searched) body is always shown, ahead of every hide rule, so a
    // chosen asteroid stays visible under isolate and is pulled out of its group.
    if (_options.IsRevealed(body.Id))
        return true;

    bool minor = body.IsMinor;
    // Isolate keeps only the spine, the major bodies and revealed bodies, so it hides
    // every un-revealed minor body (asteroids and comets alike).
    if (_options.Isolate && minor)
        return false;
    if (!_options.ShowMinorBodies && minor)
        return false;
    if (!_options.ShowComets && a is Comet)
        return false;
    return true;
}
```

2. **Collapse at build time, before layout.** Count the visible minor children; above `MinorGroupThreshold` (default 24, above the busiest stock hub so stock collapses nothing) every un-revealed minor child goes into a list and one synthetic group node hangs off the hub. The lanes and subtrees of thousands of bodies are never built:

```csharp
// DeltaVMap/Model/BuildOptions.cs L18
public const int DefaultMinorGroupThreshold = 24;
```

```csharp
// DeltaVMap/Model/VisualTree.cs L331-L357
private void AddChildren(StateNode hub, IReadOnlyList<PhysicalNode> children, Action<PhysicalNode> buildChild)
{
    int minorCount = 0;
    foreach (PhysicalNode child in children)
    {
        if (Include(child) && child.IsMinor)
            minorCount++;
    }

    bool collapse = minorCount > _options.MinorGroupThreshold;
    List<PhysicalNode>? collapsed = collapse ? new List<PhysicalNode>(minorCount) : null;

    foreach (PhysicalNode child in children)
    {
        if (!Include(child))
            continue;
        // A revealed (searched) minor body is built as its own lane, never folded into
        // the group, so the search pulls exactly that body out of the "+N".
        if (collapse && child.IsMinor && !_options.IsRevealed(child.Id))
            collapsed!.Add(child);
        else
            buildChild(child);
    }

    if (collapsed != null && collapsed.Count > 0)
        AddMinorGroup(hub, collapsed);
}
```

3. **Fold star systems the same way**, with a fixed cap (`SystemGraph.MaxShownSystems`, 8) and a "+N star systems" node, except for a system holding a revealed body.

4. **Search the whole universe, not the drawn tree.** The input uses the binding's edited-this-frame result, so the buffer is converted to a string and searched only on edit:

```csharp
// DeltaVMap/Render/MapWindow.cs L847-L864
ImGui.SeparatorText("Find"u8);

ImGui.PushItemWidth(-1f);
// The binding returns true when the text was edited this frame, so the buffer is read
// into a string and searched only then.
bool edited = ImGui.InputTextWithHint("##dvsearch"u8, "Search bodies..."u8, _searchBuffer);
ImGui.PopItemWidth();

if (edited)
{
    string query = _searchBuffer.ToString();
    if (query != _lastSearchQuery)
    {
        _lastSearchQuery = query;
        _queryBlank = query.Trim().Length == 0;
        UpdateSearchResults(query);
    }
}
```

`UpdateSearchResults` scans every body in every star system (including folded ones), matches a case-insensitive substring, ranks exact then prefix then substring, keeps 40 rows and shows "+N more, refine search". Results are `Selectable` rows in a bordered child; the pick is acted on only after `EndChild`, so the rebuild never runs mid-child.

5. **Reveal and focus.** `RevealAndFocus` adds the body to a revealed set, rebuilds only when the body is not already a node (or this first reveal arms isolate), otherwise recomposes; then it centres the view on the body's most central node and zooms to at least 100 %:

```csharp
// DeltaVMap/Render/MapWindow.cs L983-L1001
private void RevealAndFocus(PhysicalNode body)
{
    _focusBodyId = body.Id;
    bool inTree = BestBodyNode(body.Id) != null;
    bool wasEmpty = _revealedBodyIds.Count == 0;
    bool added = _revealedBodyIds.Add(body.Id);
    // Rebuild when the body must be materialized (it was collapsed, hidden or in a system
    // past the shown cap), or when this first reveal arms isolate (which is inert until
    // something has been revealed).
    bool isolateActivates = _isolate && wasEmpty && added;
    if ((!inTree || isolateActivates) && _currentRootId != null)
        RebuildPreservingSelection(_currentRootId);
    else
        UpdateScene(anchorBodyId: body.Id);
    // Center on the body. When the lines above rebuilt or recomposed, this overrides the
    // pan they anchored; that anchor only stands as a fallback when the focus node cannot
    // be resolved (FocusOnBody leaves the pan alone then).
    FocusOnBody(body.Id);
}
```

6. **Make isolate inert until something is revealed.** The effective option is `_isolate && _revealedBodyIds.Count > 0`; otherwise it would only duplicate "show minor bodies". Clear find resets the query, focus, revealed set and isolate, and rebuilds only if something had been revealed.

7. **Enforce a never-hang ceiling.** After the cheap visual-tree build, refuse to lay out more than 8,000 nodes and say why on the canvas. The comment explains that a background thread is not an option because layout measures labels through ImGui:

```csharp
// DeltaVMap/Render/MapWindow.cs L2027-L2034
// Universal never-hang guard: above the ceiling, refuse to lay out (any mode) and
// show a note instead of risking a multi-second build or unresponsive frames. The
// visual-tree build above is O(nodes) and cheap; the expensive passes (the spring
// settle, label placement) and the per-frame hover/render loops are what this caps.
// Running the build off the draw thread is not an option here: the layout measures
// labels via ImGui, which is only valid on the draw thread. Realistically only a
// system with thousands of MAJOR bodies reaches this - minor bodies aggregate away.
if (visual.Nodes.Count > MaxLayoutNodes)
```

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Model/BuildOptions.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/BuildOptions.cs) | Threshold, isolate, revealed set |
| [DeltaVMap/Model/VisualTree.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/VisualTree.cs#L92-L376) | `Include`, `AddDestinationSystems`, `AddChildren`, `AddMinorGroup` |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L841-L1086) | Search panel, ranking, reveal, focus, clear |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L2027-L2059) | `MaxLayoutNodes` refusal |

## Variations and gotchas

- **Not routable.** A click on the group node is a deliberate no-op so it never clears a selected route.
- **Threshold above stock.** The default of 24 is chosen above the busiest stock hub (Sol with 9 minor children); pick yours from your data, not from a guess.
- **Search over the graph, not the tree.** A folded body is not in the drawn tree, so search the data model.
- **Edit semantics.** The edited-this-frame result of `InputTextWithHint` avoids re-searching every frame; see [Build searchable lists, two-pane browsers and file pickers in ImGui](../../../../howto/ui/windows/build-searchable-lists-and-browsers/overview.md).
- **Version.** Depends on the `MinorBody`/`Comet` class split in the checked build.

## See also

- [Overview of tree and graph layout](overview.md)
- [Approach 1: three layout modes](lay-out-a-tree-or-graph-diagram-1.md)
- [Approach 3: stable composition](lay-out-a-tree-or-graph-diagram-3.md)
- [Build searchable lists, two-pane browsers and file pickers in ImGui](../../../../howto/ui/windows/build-searchable-lists-and-browsers/overview.md)
- Mod feature: [Search, focus, isolate and minor-body aggregation for dense systems](../../../../mods/ksa-deltavmap/features/search-focus-isolate-and-aggregation.md)

[^ksa-comet]: KSA.Comet (decompiled, build 2026.10.10.5554)
[^ksa-minorbody]: KSA.MinorBody (decompiled, build 2026.10.10.5554)
