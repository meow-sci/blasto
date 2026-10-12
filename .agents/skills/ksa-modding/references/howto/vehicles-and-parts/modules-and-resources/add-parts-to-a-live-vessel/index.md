# Game Feature

* [Creating and attaching parts to a live vessel and feeding runtime engines](overview.md) - How to build real parts on a flying vessel by hand-wiring the part tree, why a runtime engine finds no propellant until its declared feed connector is connected, and how to read the resource graph

# How-To

* [Build an N x M grid of engines on a live vessel with a single part-tree rebuild](add-parts-to-a-live-vessel-1.md) - Create parts from a template, place them, wire TreeParent and TreeChildren by hand, rebuild the tree once and reinitialise the solver state so the vessel keeps flying
* [Find the tanks an engine can draw from by walking ConsumptionOrder levels](add-parts-to-a-live-vessel-3.md) - Read PlumbedCore.ResourceManager.ConsumptionOrder, a FlowOrder of tank levels, to sum reachable propellant per engine for a custom stage or delta-v model
* [Wire FeedConnectors so runtime-created engines find propellant](add-parts-to-a-live-vessel-2.md) - Connect each runtime engine's own declared RocketCore.FeedConnectors to a tank part, align its stage with the tank, rebuild resource managers and verify with ConsumptionOrder
