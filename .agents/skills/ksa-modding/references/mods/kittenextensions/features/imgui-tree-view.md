---
type: Mod Feature
title: ImGui tree view widget
description: ImGuiTreeView<N, A> draws large trees with custom arrows, guide lines and selection fills on a split draw list, keeps open state in ImGui storage and uses static abstract adapters for node equality and parents
mod: kittenextensions
category: ui
techniques: [imgui-drawlist, imgui-window, static-abstract-interface, extension-members]
hooks: [harmony-prefix KSA.ModLibrary.PrepareAll]
lifecycle: [startup:asset-load]
game_types:
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImDrawListPtr
  - Brutal.ImGuiApi.ImGuiStoragePtr
  - Brutal.Numerics.floatRect
tags: [imgui, tree-view, draw-list, widget, generics, csharp-14]
---

The patch debugger needs three trees (the action tree, the action details and the XML document) that
can jump to, expand and highlight a node chosen somewhere else in the UI, so KittenExtensions ships its
own generic tree widget,
`ImGuiTreeView<N, A>`. It is about 440 lines, depends only on the game's `Brutal.ImGuiApi` bindings and
the small `ImGuiEx.IndentScope` helper next to it, and can be lifted into any mod.[^kx-imguiex-tree]

## How it works

1. **Typed nodes, static adapters.** `N` is any node value (`PatchAction`, `XPNodeRef`, `int`) and
   `A : ITreeNodeAdapter<N>` supplies `static abstract` `NodesEqual` and `NodeParent`
   ([KittenExtensions/Patch/ImGuiEx.Tree.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/ImGuiEx.Tree.cs#L9-L18)). The adapters are empty classes with static
   methods ([KittenExtensions/Patch/Patcher.Debug.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.Debug.cs#L610-L629)), so the widget compares and
   walks nodes without virtual calls, delegates or allocations.[^kx-patcher-debug]
2. **Frame protocol.** `Begin()` takes the window draw list, splits it into two channels, caches
   style colours (frame background, active button, `TreeLines`, text), line height and the ImGui state
   storage, and draws on channel 1; `End()` merges the channels and checks that every node was closed
   ([KittenExtensions/Patch/ImGuiEx.Tree.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/ImGuiEx.Tree.cs#L100-L133)).
3. **Nodes are scopes.** `TreeNode(value, label, out open, out clicked, ...)` pushes a `NodeState` on
   a fixed 1024-entry stack, lays out an arrow cell and a text cell on one line (multi-line labels
   grow the cell), draws an invisible open button and an invisible or dummy select button, then the
   label, and returns a `NodeScope` whose `Dispose` unindents, pops and finalizes the node; callers
   write `using var scope = view.TreeNode(...)` and recurse when `open`
   ([KittenExtensions/Patch/ImGuiEx.Tree.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/ImGuiEx.Tree.cs#L135-L203),
   [KittenExtensions/Patch/ImGuiEx.Tree.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/ImGuiEx.Tree.cs#L395-L406)).
4. **Open state** lives in ImGui's state storage under the node's ID; a node with no stored state is
   open when its depth is less than `defaultExpandDepth` ([KittenExtensions/Patch/ImGuiEx.Tree.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/ImGuiEx.Tree.cs#L291-L305)).
5. **Selection as a path.** `SetSelected(node)` records the chain of ancestors via `A.NodeParent`;
   during the next frame nodes on that path are forced open (when `expand`) and the selected node calls
   `ImGui.SetScrollHereY()` (when `scrollTo`) ([KittenExtensions/Patch/ImGuiEx.Tree.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/ImGuiEx.Tree.cs#L80-L98)).
6. **Drawing under the text.** When a node is finalized, its vertical guide line and arrow (filled
   triangle, or a dot for leaves) are drawn, and if it is selected the selection fill is drawn on
   channel 0, beneath the labels on channel 1, spanning the node and its open children
   ([KittenExtensions/Patch/ImGuiEx.Tree.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/ImGuiEx.Tree.cs#L263-L289)).
7. **Highlights.** Passing `highlightCr` colours the node's arrow and the guide lines all the way up
   its ancestors, which is how the debugger paints the failing action red and the next action green
   ([KittenExtensions/Patch/ImGuiEx.Tree.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/ImGuiEx.Tree.cs#L349-L367)).
8. **Extras.** `TreeNodeExtra` adds a non-node line (closing tags, "N results hidden"), and
   `InlineButton` places a hover-only small button at the right edge of the current row
   ([KittenExtensions/Patch/ImGuiEx.Tree.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/ImGuiEx.Tree.cs#L205-L243)).
9. **Rect helpers with C# 14 extension members.** `extension(floatRect rect) { ... }` adds `L`, `R`,
   `CX`, `TL`, `LC`, `CutWidth` and friends as properties on the game's `floatRect`
   ([KittenExtensions/Patch/ImGuiEx.Tree.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/ImGuiEx.Tree.cs#L409-L438)); the project sets `LangVersion` 14
   ([KittenExtensions/KittenExtensions.csproj](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/KittenExtensions.csproj#L11)).[^kx-csproj]

## Lifecycle and timing

The widget has no hook of its own; it must be used between `ImGui.NewFrame` and `ImGui.Render`, inside
a window or child. In KittenExtensions that is the private startup frame loop of the
[Pre-game ImGui popup loop](pre-game-popup-loop.md), during the `ModLibrary.PrepareAll` prefix
(`startup:asset-load`). In an ordinary mod the same code would run in `frame:draw-ui`, for example from
`StarMapAfterGui`.

## Key code

| Path | Role |
|---|---|
| [KittenExtensions/Patch/ImGuiEx.Tree.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/ImGuiEx.Tree.cs#L9-L18) | Adapter interface and widget declaration |
| [KittenExtensions/Patch/ImGuiEx.Tree.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/ImGuiEx.Tree.cs#L135-L203) | `TreeNode`: layout, buttons, label, scope |
| [KittenExtensions/Patch/ImGuiEx.Tree.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/ImGuiEx.Tree.cs#L263-L393) | Finalize, lines, highlight, arrows |
| [KittenExtensions/Patch/Patcher.Debug.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.Debug.cs#L36-L43) | Three widget instances with different node types and options |
| [KittenExtensions/Patch/ImGuiEx.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/ImGuiEx.cs#L14-L85) | `ImGuiEx.Space` and `IndentScope` used by the widget |

## Snippets

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

## Game APIs touched

| Member | What it is for |
|---|---|
| `ImDrawListPtr.ChannelsSplit` / `ChannelsSetCurrent` / `ChannelsMerge` ([current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImDrawListExtensions.cs#L436)) | Draw fills under labels |
| `ImGui.GetStateStorage` ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8087)) | Persist open state per node ID |
| `ImGui.GetTreeNodeToLabelSpacing` ([current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L7255)) | Indent for `Space.TreeIndent` |
| `floatRect` ([current/decomp/Brutal.Numerics/floatRect.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.Numerics/floatRect.cs)) | Cell geometry, extended with C# 14 extension members |

## Pitfalls and version notes

- **IDs come from labels.** A node's ID is `ImGui.GetID(label)`, so identical labels under the same
  parent collide; the debugger wraps each child in `ImGui.PushID(index)`.
- **Depth is capped** by the fixed 1024-entry stack.
- **Unbalanced use throws.** `Begin` twice, `End` with open nodes, or extras outside a node throw
  `InvalidOperationException`, which inside an ImGui frame leaves the frame half-built.
- **C# 14 required** for the `extension(...)` block; older compilers need ordinary extension methods.

## See also

- [Patch debug stepper](patch-debug-stepper.md)
- [Pre-game ImGui popup loop and fatal patch errors](pre-game-popup-loop.md)
- [Write a generic tree view with static abstract adapters, ChannelsSplit and ImGui storage](../../../howto/ui/draw-lists/build-custom-drawn-imgui-widgets/build-custom-drawn-imgui-widgets-4.md) - how-to article demonstrating this feature

[^kx-imguiex-tree]: KittenExtensions/Patch/ImGuiEx.Tree.cs
[^kx-patcher-debug]: KittenExtensions/Patch/Patcher.Debug.cs
[^kx-csproj]: KittenExtensions/KittenExtensions.csproj
