---
type: Mod Feature
title: Search, focus, isolate and minor-body aggregation for dense systems
description: Collapses a hub's minor bodies into one "+N" node above a threshold, folds distant star systems past eight, and lets a body-name search reveal, center and isolate any single body out of thousands, behind an 8,000-node never-hang ceiling
mod: ksa-deltavmap
category: ui
techniques: [imgui-window, level-of-detail, node-aggregation]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.MinorBody, KSA.Comet, KSA.Astronomical]
tags: [ui, search, aggregation, scalability, imgui, input-text, performance]
---

A modded system can carry thousands of asteroids. The map stays usable by folding them: a hub
with more than 24 visible minor bodies shows one "+N" group node instead of one lane each, star
systems beyond the eight nearest fold into a "+N star systems" node, and a body-name search over
the whole universe pulls any single body back out, centers it and optionally isolates the map to
it. Mechanism: `BuildOptions` drives `VisualTree.Builder.AddChildren` and `Include`, the search
fills a revealed-body set that overrides every hide rule, and `MapWindow.RebuildAt` refuses to lay
out more than 8,000 nodes.[^dvmap-buildoptions][^dvmap-visualtree][^dvmap-mapwindow]

## How it works

**Aggregation at build time.** `AddChildren(hub, children, buildChild)` first counts the
children that pass `Include` and are minor (`PhysicalNode.IsMinor`). If that count is above
`MinorGroupThreshold` (default `DefaultMinorGroupThreshold = 24`, chosen above the busiest stock
hub, Sol with 9 minor children, so stock collapses nothing), every minor child that is not revealed
is recorded in a list instead of being built, and one synthetic `StateKind.MinorGroup` node with
Id `"<hub>.MinorGroup"` and the full count in its label hangs off the hub by a dV-free group link.
The lanes and subtrees of thousands of bodies are therefore never built. The same idea applies to
star systems: `AddDestinationSystems` builds at most `SystemGraph.MaxShownSystems` (8) other
systems and folds the rest into `"<root>.SystemGroup"` labelled "+N star systems", except a system
that holds a revealed body.

**Visibility toggles.** `Include(body)` applies, in order: revealed bodies always pass; isolate
hides every un-revealed minor body; "show minor bodies" off hides minor bodies; "show comets" off
hides `Comet` instances. The root and spine ancestors are never passed through `Include`, so the
player's own position is never hidden. `ShowMinorBodies` and `ShowComets` live in `ViewOptions`
and trigger a rebuild when changed.[^dvmap-viewoptions]

**Search.** `DrawSearchPanel` draws `ImGui.InputTextWithHint("##dvsearch", "Search bodies...",
_searchBuffer)` on an `ImInputString(64)`. The binding's `ImInputString` overload returns true
only when the text was edited and then re-evaluates the buffer length, so the mod converts the
buffer to a string and searches only on edit.[^imgui-binding][^imgui-inputstring]
`UpdateSearchResults` scans `SystemGraph.AllNodes` (every body in every star system, including
ones folded into a group), matches a case-insensitive substring of the body Id, skips the root of
the shown system, ranks exact then prefix then substring (alphabetical within), keeps 40 rows and
shows "+N more, refine search". Bodies of another system are labelled `"<body> (<system>)"`.
Rows are `ImGui.Selectable` inside a bordered `BeginChild` sized to at most seven rows; the pick is
acted on only after `EndChild`, so the rebuild it may trigger never runs mid-child.

**Reveal and focus.** `RevealAndFocus(body)` adds the Id to `_revealedBodyIds`. It rebuilds
(`RebuildPreservingSelection`) only when the body is not already a node of the visual tree, or when
this first reveal arms isolate; otherwise it just recomposes the scene to open the body's star
system. `FocusOnBody` then picks the body's most central node (low orbit, intercept, surface,
stationary, SOI edge, you-are-here, hub, in that order), zooms to at least 100%, pans it to the
canvas center and cancels the pending auto-fit that `RebuildAt` queued.

**Isolate.** A console checkbox row (`ConsoleUi.CheckboxRow`, built on the game's
`ConsoleWidgets`) toggles `_isolate`.[^dvmap-consoleui] It is inert until something is revealed
(it would only duplicate "show minor bodies" otherwise), so the effective option passed to the
build is `_isolate && _revealedBodyIds.Count > 0`. **Clear find** empties the query, focus,
revealed set and isolate, and rebuilds only if something had been revealed.

**Never-hang ceiling.** After `VisualTree.Build` (linear and cheap), `RebuildAt` checks
`visual.Nodes.Count > MaxLayoutNodes` (8,000). Above it the map lays out nothing and shows "This
system has N bodies to lay out - too many to render without risking a freeze". The comment
explains why the build cannot simply move to a worker thread: the layout measures labels with
`ImGui.CalcTextSize`, which is only valid on the draw thread.

**Not routable.** A click on a `MinorGroup` node is a deliberate no-op so it never clears a
selected route; the group shows its own tooltip.

## Lifecycle and timing

- Hook: `[StarMapAfterGui]`, main thread, `frame:draw-ui`, inside `MapWindow.DrawPanel`
  (search) and `RebuildAt` (aggregation). Only while the window is shown.
- Search runs only on the frame the text changes; the rebuild runs only on reveal, isolate
  toggle, visibility toggle or clear. Nothing here runs per frame except drawing the cached
  result list.
- Only celestial metadata is read (body Ids, `MinorBody` / `Comet` type tests), which is fixed
  for a loaded system.

## Key code

| Path | Role |
|---|---|
| [DeltaVMap/Model/BuildOptions.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/BuildOptions.cs) | Threshold, isolate, revealed set |
| [DeltaVMap/Model/VisualTree.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/VisualTree.cs#L92-L376) | `Include`, `AddDestinationSystems`, `AddChildren`, `AddMinorGroup` |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L841-L1086) | Search panel, ranking, reveal, focus, clear |
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L2027-L2059) | `MaxLayoutNodes` refusal |
| [DeltaVMap/Render/ConsoleUi.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/ConsoleUi.cs) | Checkbox rows on the stock console widgets |

## Snippets

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
    // ...
    FocusOnBody(body.Id);
}
```

## Game APIs touched

| API | Decomp | Used for |
|---|---|---|
| `ImGui.InputTextWithHint(ImString, ImString, ImInputString, ...)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs) | Search box; returns true on edit and calls `ImInputString.EvaluateLength` |
| `ImInputString` (`Capacity`, `Clear`, `ToString`) | [current/decomp/Brutal.ImGuiApi/ImInputString.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImInputString.cs) | Reusable UTF-8 text buffer |
| `KSA.MinorBody`, `KSA.Comet` | (type tests only) | Which bodies are minor and which are comets |

## Pitfalls and version notes

- The mod's csproj references `Brutal.Core.Common` only so that the C# compiler can resolve the
  `Ptr` type in other `InputTextWithHint` overloads, even though the mod calls the
  `ImInputString` overload (stated in the csproj comment).
- Act on a list pick after `EndChild`. Rebuilding or mutating the list inside the child loop
  either corrupts iteration or runs a heavy rebuild with an unbalanced child stack.
- A threshold-based collapse keeps small systems pixel-identical to the uncollapsed map; pick the
  threshold above the busiest real hub rather than a percentage.
- The 8,000-node cap is a pure safety net: after aggregation the stock system and the author's
  dense test system both land near stock size.

## See also

- [Ego re-rooting and vessel follow](./ego-rerooting-and-vessel-follow.md)
- [Subway-map canvas rendering](./subway-map-canvas-rendering.md)
- [Multi-system scene composition](./multi-system-scene-composition.md)
- [Live celestial system graph](./live-celestial-system-graph.md)
- [Keep a huge diagram usable with aggregation, search-to-reveal and a hard node ceiling](../../../howto/ui/draw-lists/lay-out-a-tree-or-graph-diagram/lay-out-a-tree-or-graph-diagram-2.md) - how-to article demonstrating this feature
- [Searchable lists: InputTextWithHint with ranked results and reveal-on-pick](../../../howto/ui/windows/build-searchable-lists-and-browsers/build-searchable-lists-and-browsers-3.md) - how-to article demonstrating this feature

[^dvmap-mapwindow]: DeltaVMap/Render/MapWindow.cs
[^dvmap-buildoptions]: DeltaVMap/Model/BuildOptions.cs
[^dvmap-visualtree]: DeltaVMap/Model/VisualTree.cs
[^dvmap-viewoptions]: DeltaVMap/Render/ViewOptions.cs
[^dvmap-consoleui]: DeltaVMap/Render/ConsoleUi.cs
[^imgui-binding]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^imgui-inputstring]: Brutal.ImGuiApi.ImInputString (decompiled, build 2026.10.10.5554)
