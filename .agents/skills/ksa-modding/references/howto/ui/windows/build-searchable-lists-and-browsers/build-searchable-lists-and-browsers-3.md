---
type: How-To
title: "Searchable lists: InputTextWithHint with ranked results and reveal-on-pick"
description: Search every body in every star system as the player types, rank exact then prefix then substring matches, cap the list and act on a pick only after the child region closes
category: ui
group: windows
topic: build-searchable-lists-and-browsers
approach: 3
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksa-deltavmap/features/search-focus-isolate-and-aggregation]
techniques: [imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImInputString, Brutal.ImGuiApi.ImGuiChildFlags]
tags: [search, input-text-with-hint, ranked, selectable, child, reveal, deltavmap]
---
The delta-v map folds thousands of asteroids into a single "+N" group so the map stays readable, which means the player needs a way to find one specific body. Its search panel is a hinted text box above a small bordered result list over every body in every star system, ranked exact, then prefix, then substring, with a "+N more, refine search" line when the cap is hit. Picking a row reveals the body (pulls it out of its group) and centres the map on it. Demonstrated by KSA-DeltaVMap. Prerequisite: StarMap.

## Background: how the game does it

Only ImGui is involved: `InputTextWithHint(label, hint, ImInputString, flags)` draws a text box with grey placeholder text and returns true when the text was edited that frame, re-evaluating the buffer length first.[current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8522-L8538)[^brutal-imguiapi-imgui] The mod uses that return value as the "query changed" signal, so searching runs on the frame the text changes and nothing per frame beyond drawing the cached results. `BeginChild(id, size, ImGuiChildFlags.Borders)` gives a bordered scrolling region, and `Selectable` rows are the entries.

## Lifecycle and timing

Drawn from `[StarMapAfterGui]` (`frame:draw-ui`) inside `MapWindow.DrawPanel`, only while the window is shown. The search scans metadata that is fixed for a loaded system (body ids and their types), so it needs no synchronisation. The rebuild a pick may trigger runs after the child region closes. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Draw the hinted input and search only on edit.** Keep the last query so an edit that leaves the string unchanged does no work:

```csharp
// DeltaVMap/Render/MapWindow.cs L845-L864
private void DrawSearchPanel()
{
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

2. **Draw a bounded list.** Height is at most seven rows; `EndChild` is in a `finally` so a clipped region cannot unbalance the window stack; "no match" and "+N more" lines give feedback:

```csharp
// DeltaVMap/Render/MapWindow.cs L866-L900
PhysicalNode? pick = null;
if (_searchResults.Count > 0)
{
    // A bounded, scrollable list so even a broad query stays compact.
    float listH = Math.Min(_searchResults.Count, MaxVisibleSearchRows) * ImGui.GetFrameHeight() + 6f;
    ImGui.BeginChild("##dvsearchresults"u8, new float2?(new float2(0f, listH)), ImGuiChildFlags.Borders);
    try
    {
        for (int i = 0; i < _searchResults.Count; i++)
        {
            PhysicalNode body = _searchResults[i];
            bool isFocus = body.Id == _focusBodyId;
            if (ImGui.Selectable(_searchLabels[i], isFocus, ImGuiSelectableFlags.None, (float2?)null))
                pick = body;
        }
    }
    finally
    {
        // EndChild must always run, even if the region was clipped, to keep the
        // window stack balanced.
        ImGui.EndChild();
    }

    int more = _searchMatchTotal - _searchResults.Count;
    if (more > 0)
    {
        ImGui.TextDisabled(_moreText.IsStale(null, more)
            ? _moreText.Set(null, more, "+" + more.ToString(System.Globalization.CultureInfo.InvariantCulture) + " more, refine search")
            : _moreText.Text);
    }
}
else if (!_queryBlank)
{
    ImGui.TextDisabled("No bodies match.");
}
```

3. **Rank and cap in the search function.** Scan the whole graph (including bodies folded into a group), match case-insensitively on the body id, rank exact then prefix then substring (alphabetical within a rank), truncate, and label bodies of other systems with their system:

```csharp
// DeltaVMap/Render/MapWindow.cs L930-L978
private void UpdateSearchResults(string query)
{
    _searchResults.Clear();
    _searchLabels.Clear();
    _searchMatchTotal = 0;
    if (_graph == null)
        return;
    string q = query.Trim();
    if (q.Length == 0)
        return;

    PhysicalNode? shownSystem = CurrentSystemRoot();
    foreach (PhysicalNode n in _graph.AllNodes)
    {
        if (ReferenceEquals(n, shownSystem))
            continue;
        if (n.Astro.Id.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
            continue;
        _searchMatchTotal++;
        _searchResults.Add(n);
    }

    _searchResults.Sort((a, b) => CompareMatch(a.Astro.Id, b.Astro.Id, q));
    if (_searchResults.Count > MaxSearchResults)
        _searchResults.RemoveRange(MaxSearchResults, _searchResults.Count - MaxSearchResults);

    foreach (PhysicalNode n in _searchResults)
    {
        PhysicalNode system = SystemGraph.SystemRootOf(n);
        _searchLabels.Add(ReferenceEquals(system, shownSystem) || ReferenceEquals(system, n)
            ? n.Id
            : n.Id + " (" + system.Id + ")");
    }
}

private static int CompareMatch(string a, string b, string query)
{
    int byRank = MatchRank(a, query).CompareTo(MatchRank(b, query));
    return byRank != 0 ? byRank : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
}

private static int MatchRank(string id, string query)
{
    if (id.Equals(query, StringComparison.OrdinalIgnoreCase))
        return 0;
    if (id.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        return 1;
    return 2;
}
```

4. **Act on the pick after the child closes.** The reveal may rebuild the visual tree, which must not happen while the loop that draws the list is running:

```csharp
// DeltaVMap/Render/MapWindow.cs L902-L922
    bool isolate = _isolate;
    if (ConsoleUi.CheckboxRow("ISOLATE TO FOUND BODIES".AsSpan(), "DvmIsolate".AsSpan(), ref isolate))
    {
        _isolate = isolate;
        // The effect only exists once something is revealed, so only rebuild when isolate
        // would actually change the current view.
        if (_revealedBodyIds.Count > 0 && _currentRootId != null)
            RebuildPreservingSelection(_currentRootId);
    }
    if (_isolate && _revealedBodyIds.Count == 0)
        ImGui.TextDisabled("Search a body to isolate it");

    if ((_revealedBodyIds.Count > 0 || _focusBodyId != null || _lastSearchQuery.Length > 0)
        && ImGui.SmallButton("Clear find"u8))
        ClearSearch();

    // Act on a pick after the list/child scope closes, so the rebuild it triggers does not
    // run mid-child or mutate anything the loop above is iterating.
    if (pick != null)
        RevealAndFocus(pick);
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
    // Center on the body. When the lines above rebuilt or recomposed, this overrides the
    // pan they anchored; that anchor only stands as a fallback when the focus node cannot
    // be resolved (FocusOnBody leaves the pan alone then).
    FocusOnBody(body.Id);
}
```

5. **Make the reveal win over the folding rules.** A revealed body is built as its own lane instead of being folded into "+N":

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

## Complete example

| File | Role |
|---|---|
| [DeltaVMap/Render/MapWindow.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Render/MapWindow.cs#L845-L1001) | Search panel, ranking, reveal and focus |
| [DeltaVMap/Model/VisualTree.cs](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap/blob/0441981a4c59b90e462fd81fc20d3924b356841a/DeltaVMap/Model/VisualTree.cs#L331-L357) | Aggregation that the reveal overrides |

## Variations and gotchas

- **Cap the result list** (the mod keeps 40 rows) so a broad query does not build thousands of labels each time.
- **Never mutate the list you are drawing.** Store the pick, close the scope, then act.
- **Always call `EndChild`**, even if the region is clipped; `BeginChild` returns false but the `End` call is still required.
- **Isolation is inert until something is revealed**, so the "isolate" checkbox only rebuilds when it changes what is visible.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of build-searchable-lists-and-browsers](overview.md)
- [Approach 2: focus and default focus in a combo](build-searchable-lists-and-browsers-2.md)
- [Lay out a tree or graph diagram and keep it usable at scale](../../draw-lists/lay-out-a-tree-or-graph-diagram/overview.md)
- Mod feature: [Search, focus, isolate and aggregation](../../../../mods/ksa-deltavmap/features/search-focus-isolate-and-aggregation.md)

[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
