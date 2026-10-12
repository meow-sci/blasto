---
type: How-To
title: Write a generic tree view with static abstract adapters, ChannelsSplit and ImGui storage
description: Build an ImGuiTreeView of N and A where adapters are static abstract, selection fills draw on channel 0 beneath labels on channel 1, and open state lives in ImGui storage keyed by node id
category: ui
group: draw-lists
topic: build-custom-drawn-imgui-widgets
approach: 4
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/kittenextensions/features/imgui-tree-view]
techniques: [imgui-drawlist, imgui-window]
hooks: [harmony-prefix KSA.ModLibrary.PrepareAll]
lifecycle: [startup:asset-load, frame:draw-ui]
game_types:
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImDrawListPtr
  - Brutal.ImGuiApi.ImDrawListExtensions
  - Brutal.ImGuiApi.ImGuiStoragePtr
  - Brutal.Numerics.floatRect
tags: [tree-view, channels, storage, selection, generic, static-abstract]
---
KittenExtensions' patch debugger needs three trees (the patch action tree, action details and the XML document) that can jump to, expand and highlight a node chosen elsewhere in the UI. ImGui's `TreeNode` cannot be told to select-and-scroll like that, so the mod ships its own generic `ImGuiTreeView<N, A>`: about 440 lines that depend only on the Brutal ImGui bindings. This article shows the three design decisions worth copying: static-abstract adapters, a two-channel draw list, and open state in ImGui storage. Demonstrated by [ImGui tree view widget](../../../../mods/kittenextensions/features/imgui-tree-view.md). Prerequisite: C# 14 if you also want the `extension(floatRect)` helpers (the project sets `LangVersion` 14).

## Background: how the game does it

Three Brutal ImGui facilities do the work. `ImDrawListExtensions.ChannelsSplit(int)`, `ChannelsSetCurrent(int)` and `ChannelsMerge()` ([current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L436-L449)[^brutal-imguiapi-imdrawlistextensions]) let you submit shapes out of order: on merge, channel 0 is drawn first, so a selection fill submitted after the label still lies beneath it. `ImGui.GetStateStorage()` ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8087-L8090)[^brutal-imguiapi-imgui]) returns an `ImGuiStoragePtr` whose `GetBool/SetBool(ImGuiID, ...)` extension methods ([current/decomp/Brutal.ImGuiApi/ImGuiStorageExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiStorageExtensions.cs#L23-L31)[^brutal-imguiapi-imguistorageextensions]) keep per-ID flags inside the current window. `ImGui.SetScrollHereY()` scrolls the selected row into view. `floatRect` (`Min`, `Max`, `Extent`; [current/decomp/Brutal.Numerics/floatRect.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Numerics/floatRect.cs#L7-L11)[^brutal-numerics-floatrect]) carries cell geometry.

## Lifecycle and timing

The widget has no hook: it must be used between `ImGui.NewFrame` and `ImGui.Render`, inside a window or child. KittenExtensions uses it in its private pre-game frame loop, run in the `ModLibrary.PrepareAll` prefix (`startup:asset-load`), before the game's own `Program.OnFrame` exists. In an ordinary mod the same code runs in `frame:draw-ui`, for example from `[StarMapAfterGui]`. `Begin()` and `End()` must bracket one frame's tree. See [Game loop and mod lifecycle](../../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Declare the adapter interface with static abstract members**, so the widget compares and walks nodes with no virtual calls, delegates or allocations, and the node type can be anything:

```csharp
// KittenExtensions/Patch/ImGuiEx.Tree.cs L9-L18
public interface ITreeNodeAdapter<N>
{
  public static abstract bool NodesEqual(N node1, N node2);
  public static abstract bool NodeParent(N node, out N parent);
}

public class ImGuiTreeView<N, A>(
  bool clickable = true, bool clickSelectable = true, bool treeSelectHighlight = true,
  int defaultExpandDepth = int.MaxValue
) where A : ITreeNodeAdapter<N>
```

2. **Implement an adapter as an empty class with static methods** (one per node type):

```csharp
// KittenExtensions/Patch/Patcher.Debug.cs L610-L623
private class PatchActionAdapter : ITreeNodeAdapter<PatchAction>
{
  public static bool NodesEqual(PatchAction node1, PatchAction node2) => node1 == node2;
  public static bool NodeParent(PatchAction node, out PatchAction parent) =>
    (parent = node.Parent) != null;
}

private class XPNodeRefAdapter : ITreeNodeAdapter<XPNodeRef>
{
  public static bool NodesEqual(XPNodeRef node1, XPNodeRef node2) =>
    node1.FirstVersion.SameAs(node2.FirstVersion);
  public static bool NodeParent(XPNodeRef node, out XPNodeRef parent) =>
    (parent = node.Parent).Valid && parent.Type is not XPType.Document;
}
```

3. **Per frame: split the draw list and cache the style.** `Begin()` takes the window draw list, splits it into two channels, caches colours from the current style (including `TreeLines`) and the storage pointer, and draws on channel 1. `End()` merges and throws if any node is still open:

```csharp
// KittenExtensions/Patch/ImGuiEx.Tree.cs L100-L133
public void Begin()
{
  if (started)
    throw new InvalidOperationException($"Last frame not ended");
  started = true;
  drawList = ImGui.GetWindowDrawList();
  drawList.ChannelsSplit(2);
  crSelected = ImGui.ColorConvertFloat4ToU32(ImGui.GetStyleColorVec4(ImGuiCol.FrameBg));
  crHovered = ImGui.ColorConvertFloat4ToU32(ImGui.GetStyleColorVec4(ImGuiCol.FrameBg));
  crActive = ImGui.ColorConvertFloat4ToU32(ImGui.GetStyleColorVec4(ImGuiCol.ButtonActive));
  crLine = ImGui.ColorConvertFloat4ToU32(ImGui.GetStyleColorVec4(ImGuiCol.TreeLines));
  crTextF = ImGui.GetStyleColorVec4(ImGuiCol.Text);
  crText = ImGui.ColorConvertFloat4ToU32(crTextF);
  spacing = ImGui.GetStyle().ItemSpacing.X;
  indent = lineHeight = ImGui.GetTextLineHeight();
  storage = ImGui.GetStateStorage();
  inSelected = false;

  drawList.ChannelsSetCurrent(1);
}

public void End()
{
  if (!started)
    throw new InvalidOperationException($"Frame not started");
  if (depth > 0)
    throw new InvalidOperationException($"Tree nodes not closed");
  drawList.ChannelsMerge();
  started = false;
  drawList = null;
  storage = null;
  expandSelected = false;
  scrollToSelected = false;
}
```

4. **Make nodes scopes.** `TreeNode` pushes a `NodeState` on a fixed 1024-entry stack, lays out an arrow cell and a text cell on one line, draws an open button and a select button, then the label, and returns a `NodeScope` that unindents and finalizes on `Dispose`. Callers write `using var scope = view.TreeNode(...)` and recurse when `open`:

```csharp
// KittenExtensions/Patch/ImGuiEx.Tree.cs L135-L203
public NodeScope TreeNode(
  N nodeVal, ImString text, out bool open, out bool clicked, bool? clickable = null,
  bool hasChildren = true, float4? highlightCr = null, float4? textCr = null)
{
  var nodeID = ImGui.GetID(text);
  ref var node = ref Push(nodeID);

  var fullRect = new floatRect()
  {
    Min = ImGui.GetCursorScreenPos(),
    Extent = new(ImGui.GetContentRegionAvail().X, lineHeight),
  };

  node.Node = nodeVal;
  node.HasChildren = hasChildren;
  node.OnSelectedPath = hasSelected
    && (node.Depth == 0 || stack[node.Depth - 1].OnSelectedPath)
    && node.Depth < selectedPath.Count
    && A.NodesEqual(nodeVal, selectedPath[node.Depth]);
  node.Selected = node.OnSelectedPath && node.Depth == selectedPath.Count - 1;
  node.Clickable = clickable ?? this.clickable;
  (node.Arrow, node.Text) = fullRect.CutWidth(indent);
  node.InlineX = node.Text.R;
  node.MaxHorizY = node.Arrow.B;
  node.VertStart = node.Arrow.CB;

  if (text.AsSpan().Contains((byte)'\n'))
  {
    var textHeight = ImGui.CalcTextSize(text).Y;
    node.Text.Max = new(node.Text.L, node.Text.T + textHeight);
  }
  node.BottomY = node.Text.B;

  inSelected = inSelected || node.Selected;

  ImGui.PushID(text);

  if (hasChildren)
    DrawOpenButton(ref node);

  open = node.Open;
  if (expandSelected)
  {
    var shouldExpand = node.OnSelectedPath ||
      node.Depth < defaultExpandDepth ||
      (inSelected && (node.Depth - selectedPath.Count) < defaultExpandDepth);
    if (shouldExpand != node.Open)
      storage.SetBool(node.Id, open = node.Open = shouldExpand);
  }
  clicked = DrawSelectButton(ref node);

  ImGui.SetCursorScreenPos(node.Text.TL + new float2(spacing, 0));

  ImGui.TextColored(textCr ?? highlightCr ?? crTextF, text);

  if (node.Selected && scrollToSelected)
    ImGui.SetScrollHereY();

  if (node.Depth > 0)
  {
    ref var pnode = ref stack[node.Depth - 1];
    pnode.MaxHorizY = node.Arrow.CY;
  }
  if (highlightCr != null)
    Highlight(highlightCr.Value);

  ImGui.PopID();
  return new(this, nodeID, indent);
}
```

```csharp
// KittenExtensions/Patch/ImGuiEx.Tree.cs L395-L406
public readonly struct NodeScope(ImGuiTreeView<N, A> view, ImGuiID id, float indent) : IDisposable
{
  private readonly ImGuiTreeView<N, A> view = view;
  private readonly ImGuiID id = id;
  private readonly ImGuiEx.IndentScope indent = new(indent);

  public void Dispose()
  {
    indent.Dispose();
    view.Finalize(ref view.Pop(id));
  }
}
```

5. **Keep open state in ImGui storage**, keyed by the node id and defaulting from depth. A click on the invisible arrow button toggles it and writes it back:

```csharp
// KittenExtensions/Patch/ImGuiEx.Tree.cs L291-L305
private void DrawOpenButton(ref NodeState node)
{
  node.Open = storage.GetBool(node.Id, node.Depth < defaultExpandDepth);

  ImGui.SetCursorScreenPos(node.Arrow.TL);
  if (ImGui.InvisibleButton("open", node.Arrow.Extent))
  {
    node.Open = !node.Open;
    storage.SetBool(node.Id, node.Open);
  }
  if (ImGui.IsItemActive())
    drawList.AddRectFilled(node.Arrow.TL, node.Arrow.BR, crActive);
  else if (ImGui.IsItemHovered())
    drawList.AddRectFilled(node.Arrow.TL, node.Arrow.BR, crHovered);
}
```

6. **Select by path.** `SetSelected(node)` records the chain of ancestors through `A.NodeParent`; in the next frame nodes on that path are forced open and the selected node calls `SetScrollHereY`:

```csharp
// KittenExtensions/Patch/ImGuiEx.Tree.cs L80-L98
public void SetSelected(N sel, bool scrollTo = true, bool expand = true)
{
  hasSelected = true;
  selected = sel;
  scrollToSelected = scrollTo;
  expandSelected = expand;
  selectedPath.Clear();
  do
  {
    selectedPath.Add(sel);
  } while (A.NodeParent(sel, out sel));
  selectedPath.Reverse();
}

public void ClearSelected()
{
  hasSelected = false;
  selectedPath.Clear();
}
```

7. **Draw the selection fill on channel 0 when the node is finalized**, spanning the node and its open children, so labels on channel 1 stay on top:

```csharp
// KittenExtensions/Patch/ImGuiEx.Tree.cs L263-L289
private void Finalize(ref NodeState node)
{
  DrawVert(ref node, crLine);

  if (!node.Highlighted)
    DrawArrow(ref node, crText);

  if (node.Selected)
  {
    drawList.ChannelsSetCurrent(0);

    var end = new float2(node.Text.R, node.BottomY);
    drawList.AddRectFilled(node.Arrow.TL, end, crSelected);

    drawList.ChannelsSetCurrent(1);

    inSelected = false;
  }

  if (node.Depth > 0 && treeSelectHighlight)
    stack[node.Depth - 1].BottomY = node.BottomY;

  if (node.Depth == 0 || node.Highlighted)
    return;

  DrawHoriz(ref node, crLine);
}
```

8. **Use it.** The debugger draws its XML document recursively, wrapping each child in `PushID(index)`:

```csharp
// KittenExtensions/Patch/Patcher.Debug.cs L540-L579
private void DrawDocTree(XPNodeRef node)
{
  var line = new LineBuilder(buffer);

  var hasChildren = node.FirstContent.Valid;
  var inline = true;
  if (node.Type is XPType.Element)
  {
    inline = ShouldInline(node);
    if (inline)
      line.ElementInline(node);
    else
      line.ElementOpen(node, !hasChildren);
  }
  else
    line.NodeInline(node);

  var cr = NodeColor(node).AsFloat4();

  using var nodeScope = xmlView.TreeNode(
    node, line.Line,
    out var open, out _,
    hasChildren: hasChildren && !inline, textCr: cr);

  if (!inline && hasChildren && open)
  {
    var child = node.FirstContent;
    var index = 0;
    while (child.Valid)
    {
      ImGui.PushID(index++);
      DrawDocTree(child);
      child = child.NextSibling;
      ImGui.PopID();
    }

    line.Clear();
    line.ElementClose(node.Name);
    xmlView.TreeNodeExtra(line.Line, cr);
  }
```

## Complete example

| File | Role |
|---|---|
| [KittenExtensions/Patch/ImGuiEx.Tree.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/ImGuiEx.Tree.cs) | The widget, `NodeScope`, `floatRect` extension helpers |
| [KittenExtensions/Patch/Patcher.Debug.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.Debug.cs#L36-L43) | Three widget instances with different node types and options |
| [KittenExtensions/Patch/ImGuiEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/ImGuiEx.cs#L14-L85) | `ImGuiEx.Space` and `IndentScope` used by the widget |

## Variations and gotchas

- **IDs come from labels.** A node's id is `ImGui.GetID(text)`, so identical labels under one parent collide; wrap children in `PushID(index)`.
- **Depth is capped** at the 1024-entry stack.
- **Unbalanced use throws** `InvalidOperationException` (double `Begin`, `End` with open nodes, extras outside a node); inside an ImGui frame this leaves the frame half-built, so catch at the window boundary.
- **Channels are per draw list.** Anything else you draw on the same list between `Begin` and `End` also lands on channel 1.
- **C# 14.** The `extension(floatRect rect) { ... }` helpers need a C# 14 compiler; ordinary extension methods work on older ones.
- **Version.** Brutal ImGui only.

## See also

- [Overview of custom-drawn widgets](overview.md)
- [Approach 1: widget kit](build-custom-drawn-imgui-widgets-1.md)
- [Build searchable lists, two-pane browsers and file pickers in ImGui](../../../../howto/ui/windows/build-searchable-lists-and-browsers/overview.md)
- Mod feature: [ImGui tree view widget](../../../../mods/kittenextensions/features/imgui-tree-view.md)

[^brutal-imguiapi-imdrawlistextensions]: Brutal.ImGuiApi.ImDrawListExtensions (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imguistorageextensions]: Brutal.ImGuiApi.ImGuiStorageExtensions (decompiled, build 2026.10.10.5554)
[^brutal-numerics-floatrect]: Brutal.Numerics.floatRect (decompiled, build 2026.10.10.5554)
