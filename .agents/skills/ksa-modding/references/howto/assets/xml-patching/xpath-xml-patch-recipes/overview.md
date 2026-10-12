---
type: Game Feature
title: Write XPath patch operations for game XML
description: The ModuleManager-style patch operations KittenExtensions runs over XPath 1.0 paths, with worked recipes for appending a gauge button, adding missing elements to bodies and rewriting values with conditionals
category: assets
group: xml-patching
topic: xpath-xml-patch-recipes
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/autostage-gauge-button
  - mods/ksa-advancedflightcomputer/features/hyperbolic-transfer-targets
  - mods/kittenextensions/features/xpath-patch-operations
lifecycle: [startup:asset-load]
game_types:
  - KSA.AssetBundle
  - KSA.GaugeCanvas
  - KSA.SystemTemplate
  - KSA.PlanetMeshCollection
  - KSA.GaugeComponent
  - KSA.GaugeButtonReference
  - KSA.InterstellarCometTemplate
tags: [xml-patch, xpath, kittenextensions, recipes]
---

A KittenExtensions patch file is a `<Patch>` element whose children are operations. Each selects nodes of
the combined GameData document with an XPath 1.0 expression evaluated from `<Root>` and then changes,
copies, merges, deletes, branches or stores. The vocabulary is Copy (with `Pos` of Replace, Append,
Prepend, Before or After), Merge (with `_MergeId` and `_MergePos`), Delete, If, IfAny, IfNone, With and SetVar.
This topic is the cookbook; how the patch files are found and run is [Patch Core or another mod's XML data without copying files](../patch-game-xml-data/overview.md).

## Engine background

- The operations do not exist in the game. They run on an in-memory document before deserialization; the game's
  own serializers then read the result, so patched XML must still map to element names the templates accept.
- Gauge panels: `GaugeCanvas` (`Component` children, `VisibleInContext`) and `GaugeComponent` (Digit, Label,
  Button, Box, Rect children) ([current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L103-L107), [current/decomp/KSA/GaugeComponent.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeComponent.cs#L43-L48)).
- Body templates: `AssetBundle` maps element names such as `InterstellarComet` to template classes
  ([current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L14-L14)); the stock data is in `Astronomicals.xml`.
- Semantics source: the KittenExtensions README ([README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md#L90-L216)). The XPathPatch
  library is a submodule not present in the pinned checkout; test adapted patches with the debug stepper.
- Phase: `startup:asset-load`, see [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [Append a button to a stock gauge canvas with Copy and inline XML](xpath-xml-patch-recipes-1.md) | KSA-AdvancedFlightComputer | `Copy Pos="Append"` with inline XML into a gauge canvas component | `startup:asset-load` | Simplest; a mistyped `Id` fails silently; layout must be kept in sync with stock |
| [Add missing mass and sphere-of-influence elements to a kind of body](xpath-xml-patch-recipes-2.md) | KSA-AdvancedFlightComputer | Select bodies by element name and `Id`, append missing `Mass` and `SphereOfInfluence` | `startup:asset-load` | Explicit and readable; one `Copy` per body unless you use `With` |
| [Rewrite many nodes with With, If and SetVar, and combine trees with Merge](xpath-xml-patch-recipes-3.md) | KittenExtensions | `With`, `If`, `SetVar` for per-node rewrites; `Merge` with `_MergeId` and `_MergePos` | `startup:asset-load` | Powerful; semantics come from the README only, some README examples are stale |

## See also

- [Patch Core or another mod's XML data without copying files](../patch-game-xml-data/overview.md), [Debug XML patches with the step debugger and the session log](../../validation/debug-xml-patches/overview.md), [Redirect the game to patched copies of its XML and coordinate edits between mods](../redirect-game-xml-loading/overview.md),
  [Add to, drive and hide the stock cockpit gauges and navball](../../../ui/menus/work-with-stock-cockpit-gauges/overview.md), [Add to, drive and hide the stock cockpit gauges and navball](../../../ui/menus/work-with-stock-cockpit-gauges/overview.md)
- [XML patch files and XPathPatch operations](../../../../mods/kittenextensions/features/xpath-patch-operations.md), [A custom gauge button action backed by a mod enum](../../../../mods/ksa-advancedflightcomputer/features/autostage-gauge-button.md),
  [Transfer Planner targets on hyperbolic orbits](../../../../mods/ksa-advancedflightcomputer/features/hyperbolic-transfer-targets.md)
