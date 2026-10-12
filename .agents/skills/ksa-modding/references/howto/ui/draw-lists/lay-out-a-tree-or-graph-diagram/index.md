# Game Feature

* [Lay out a tree or graph diagram and keep it usable at scale](overview.md) - A game-type-free layout pipeline bounded on the draw thread, plus aggregation, search-to-reveal, stable scene composition and vessel-driven re-rooting for diagrams with thousands of nodes

# How-To

* [Compose cached per-system layouts around a home layout that never moves](lay-out-a-tree-or-graph-diagram-3.md) - Lay out each destination as a cached part, place the parts in a strip beside the unmoved home map, route connectors as a pure function of bounds, and re-anchor the pan only when the clicked node would leave the canvas
* [Keep a huge diagram usable with aggregation, search-to-reveal and a hard node ceiling](lay-out-a-tree-or-graph-diagram-2.md) - Fold minor bodies above a threshold into one group node, let a whole-universe search pull a single body back out, and refuse to lay out more than 8,000 nodes on the draw thread
* [Lay out a tree in three modes with a game-free pipeline, Barnes-Hut and an iteration budget](lay-out-a-tree-or-graph-diagram-1.md) - Run measure, band or well assignment, tidy-tree X, grid snap, octilinear routing and greedy label placement, or a deterministic force layout whose iteration count shrinks as the node count grows
* [Re-root a diagram when the controlled vessel changes: reference comparison after PrepareFrame](lay-out-a-tree-or-graph-diagram-4.md) - Walk the parent chain into hub levels, compare Program.ControlledVehicle and its Parent by reference each frame, and rebuild or re-root on a vessel switch or star-system crossing but not on an in-system SOI change
