# Game Feature

* [Reading live part module state](overview.md) - How KSA stores per-part module data in typed module lists and struct-of-arrays state lists on the PartTree, and two recipes for reading engines, tanks, propellant and solid motors

# How-To

* [Read a tank's shape and liquid level](read-live-part-module-state-2.md) - Reach the private Tank._template by reflection, build a tank shape, sum liquid mass and volume from the mole states, and find the fill surface against the acceleration the craft feels
* [Read engines, tanks, solid motors and parts with Modules.Get and TryGetFrom, cached per vehicle](read-live-part-module-state-1.md) - Enumerate modules of one type, read their live state through ModuleStateful state lists, and cache structural facts in a ConditionalWeakTable keyed by vehicle
