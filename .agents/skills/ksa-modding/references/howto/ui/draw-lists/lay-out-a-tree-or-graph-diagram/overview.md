---
type: Game Feature
title: Lay out a tree or graph diagram and keep it usable at scale
description: A game-type-free layout pipeline bounded on the draw thread, plus aggregation, search-to-reveal, stable scene composition and vessel-driven re-rooting for diagrams with thousands of nodes
category: ui
group: draw-lists
topic: lay-out-a-tree-or-graph-diagram
demonstrated_by:
  - mods/ksa-deltavmap/features/three-layout-modes
  - mods/ksa-deltavmap/features/search-focus-isolate-and-aggregation
  - mods/ksa-deltavmap/features/multi-system-scene-composition
  - mods/ksa-deltavmap/features/ego-rerooting-and-vessel-follow
lifecycle: [frame:draw-ui, frame:apply-solvers]
game_types:
  - Brutal.ImGuiApi.ImGui
  - KSA.MinorBody
  - KSA.Comet
  - KSA.Astronomical
  - KSA.Program
  - KSA.Vehicle
  - KSA.IParentBody
  - KSA.CelestialSystem
  - KSA.PhysicsStates
tags: [layout, tree, graph, force-directed, aggregation, search, re-rooting, scale, draw-list]
---
A mod that draws a diagram of the game's bodies (a map of transfers, a part tree, a dependency graph) has to answer four questions the game does not: where does each node go, what happens when there are thousands of nodes, how are several systems drawn side by side without the picture jumping, and what is the root when the player's vessel changes SOI? KSA-DeltaVMap answers all four, and every answer is deliberately independent of the game's types, which keeps it testable. The shared constraint is the draw thread: layout measures labels with `ImGui.CalcTextSize`, which is only valid inside the ImGui frame, so every layout runs in `frame:draw-ui` and is bounded by node ceilings and iteration budgets instead of being moved to a worker. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Engine background

| Piece | File | What it gives you |
|---|---|---|
| `ImGui.CalcTextSize` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8092-L8098)[^brutal-imguiapi-imgui] | Label widths for the measure step; draw-thread only |
| `Astronomical`, `MinorBody`, `Comet` | [current/decomp/KSA/Astronomical.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Astronomical.cs#L12)[^ksa-astronomical], [current/decomp/KSA/MinorBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/MinorBody.cs#L3)[^ksa-minorbody], [current/decomp/KSA/Comet.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Comet.cs#L3)[^ksa-comet] | The type tests that classify a body as minor or as a comet |
| `IParentBody`, `IIndependentRoot` | [current/decomp/KSA/IParentBody.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IParentBody.cs#L9)[^ksa-iparentbody], [current/decomp/KSA/IIndependentRoot.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/IIndependentRoot.cs#L7)[^ksa-iindependentroot] | The parent chain the re-rooting walks; independent roots are star systems |
| `CelestialSystem.HomeBody` | [current/decomp/KSA/CelestialSystem.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/CelestialSystem.cs#L64)[^ksa-celestialsystem] | Fallback root when there is no controlled vehicle |
| `Program.ControlledVehicle`, `Vehicle.Parent` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502-L512)[^ksa-program], [current/decomp/KSA/Vehicle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Vehicle.cs#L373)[^ksa-vehicle] | The vessel and its current SOI body (`Parent => Orbit.Parent`) |
| `PhysicsStates.CheckSoiTransitions` | [current/decomp/KSA/PhysicsStates.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/PhysicsStates.cs#L704)[^ksa-physicsstates] | Where SOI changes are decided, in the vehicle solver |
| `Program.PrepareFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2166-L2167)[^ksa-program] | Applies the vehicle step (`Universe.ApplyVehicleSolvers`) before the UI draws |

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [Three layout modes with Barnes-Hut and an iteration budget](lay-out-a-tree-or-graph-diagram-1.md) | KSA-DeltaVMap | Measure, band, tidy tree, grid snap, routing, labels; deterministic force layout | `frame:draw-ui` | Game-free engine; cost bounded by budget, not by thread |
| 2 | [Threshold aggregation, search-to-reveal and an 8,000-node ceiling](lay-out-a-tree-or-graph-diagram-2.md) | KSA-DeltaVMap | Fold minor bodies above 24, reveal by search, refuse above a ceiling | `frame:draw-ui` | Keeps huge systems usable; folded bodies hidden until searched |
| 3 | [Stable composition of cached parts around a never-moved home layout](lay-out-a-tree-or-graph-diagram-3.md) | KSA-DeltaVMap | Per-system parts cached by (root, expanded), strip placement, anchor-preserving pan | `frame:draw-ui` | No jumps; many tunables in screen pixels |
| 4 | [Reference comparison after PrepareFrame and ego re-rooting](lay-out-a-tree-or-graph-diagram-4.md) | KSA-DeltaVMap | Compare `ControlledVehicle` and `Parent` by reference each frame | `frame:draw-ui` | Cheap; in-system SOI change waits for reopen by design |

## Choosing

- Starting from nothing: approach 1's pipeline order (measure, assign Y, assign X, snap, route, label) is the template; replace its Y pass with your own metric.
- More than a few hundred nodes: approach 2 before you write any drawing code.
- Several graphs on one canvas: approach 3.
- Anything that should follow the player's vessel: approach 4.

## See also

- [Build pannable, zoomable and 3D canvases inside an ImGui window](../../../../howto/ui/draw-lists/build-a-pan-zoom-canvas-in-imgui/overview.md)
- [Build searchable lists, two-pane browsers and file pickers in ImGui](../../../../howto/ui/windows/build-searchable-lists-and-browsers/overview.md)
- [Draw charts, scopes and relief maps with the ImGui draw list](../../../../howto/ui/draw-lists/draw-a-chart-with-imdrawlist/overview.md)
- [Build pannable, zoomable and 3D canvases inside an ImGui window](../build-a-pan-zoom-canvas-in-imgui/overview.md) - related topic
- [Build searchable lists, two-pane browsers and file pickers in ImGui](../../windows/build-searchable-lists-and-browsers/overview.md) - related topic

[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^ksa-astronomical]: KSA.Astronomical (decompiled, build 2026.10.10.5554)
[^ksa-minorbody]: KSA.MinorBody (decompiled, build 2026.10.10.5554)
[^ksa-comet]: KSA.Comet (decompiled, build 2026.10.10.5554)
[^ksa-iparentbody]: KSA.IParentBody (decompiled, build 2026.10.10.5554)
[^ksa-iindependentroot]: KSA.IIndependentRoot (decompiled, build 2026.10.10.5554)
[^ksa-celestialsystem]: KSA.CelestialSystem (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-vehicle]: KSA.Vehicle (decompiled, build 2026.10.10.5554)
[^ksa-physicsstates]: KSA.PhysicsStates (decompiled, build 2026.10.10.5554)
