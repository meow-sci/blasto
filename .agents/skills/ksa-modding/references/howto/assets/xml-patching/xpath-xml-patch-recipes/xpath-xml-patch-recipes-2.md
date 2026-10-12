---
type: How-To
title: Add missing mass and sphere-of-influence elements to a kind of body
description: Select bodies by element name and Id with an XPath and append inline Mass and SphereOfInfluence elements so code that needs them stops skipping the body
category: assets
group: xml-patching
topic: xpath-xml-patch-recipes
approach: 2
difficulty: intermediate
prerequisites: [starmap, kittenextensions]
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/hyperbolic-transfer-targets
  - mods/kittenextensions/features/xpath-patch-operations
techniques: [xml-patch]
hooks: [kittenextensions patch Copy]
lifecycle: [startup:asset-load]
game_types: [KSA.AssetBundle, KSA.SystemTemplate, KSA.PlanetMeshCollection, KSA.InterstellarCometTemplate]
tags: [xml-patch, xpath, celestials, comets, mass, soi]
---

Sometimes the data a feature needs is simply missing from stock: Core's interstellar comets have no
`SphereOfInfluence`, and two of the three have no `Mass`, which is why the stock transfer planner cannot
treat them as targets. KSA-AdvancedFlightComputer (AFC) fills them in with one patch file of three
`<Copy Pos="Append">` operations. This article shows how to select bodies by element name and `Id`, how
to append several children at once, and how to confirm the patch matched.

Demonstrated by [Transfer Planner targets on hyperbolic orbits](../../../../mods/ksa-advancedflightcomputer/features/hyperbolic-transfer-targets.md); the code that uses the new
data is explained there. The patch mechanism is [Patch Core or another mod's XML with a patches array in mod.toml](../patch-game-xml-data/patch-game-xml-data-1.md).

## Background: how the game does it

Core defines bodies in `Astronomicals.xml` under the `<Assets>` root, one element per body whose name
selects the template type: `StellarBody`, `PlanetaryBody`, `AtmosphericBody`, `InterstellarComet` and so
on. The `AssetBundle` class maps each element name to its template class
([current/decomp/KSA/AssetBundle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/AssetBundle.cs#L14-L14)). The comet entries carry an `Id`, a `Parent`, an `Orbit` and a `MeanRadius`;
`3I_ATLAS` also has a `Mass`:

```xml
<!-- current/Content/Core/Astronomicals.xml L4423-L4434 -->
    <InterstellarComet Id="Oumuamua" Parent="Sol">
        <Orbit>
            <Periapsis Au="0.255916"/>
            <Inclination Degrees="122.5" />
            <Eccentricity Value="1.20113" />
            <LongitudeOfAscendingNode Degrees="24.597" />
            <ArgumentOfPeriapsis Degrees="241.1" />
            <TimeAtPeriapsis Months="-3"/>
        </Orbit>

        <MeanRadius Km="0.1" />
    </InterstellarComet>
```

Because the XML is read by the standard serializers, an appended `<Mass Gg="4.19"/>` is only valid if
`Mass` is a child element the body template already accepts, exactly like the elements already used by
other bodies.

## Lifecycle and timing

`startup:asset-load`, in the `ModLibrary.PrepareAll` prefix, before `Astronomicals.xml` is deserialized.
Body data is then loaded normally. The code that uses the new `SphereOfInfluence` runs much later; AFC's
postfix on the transfer planner logs once and skips a body whose SOI is still missing, which is how it
detects "patch did not run". See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Read the stock definition** of the body kind in `Astronomicals.xml` and note what is missing
   (here `Mass` for two comets and `SphereOfInfluence` for all three).

2. **Select each body by element name and `Id`.** The element name narrows the scope and the `Id` selects
   one body, so a body of another type with the same `Id` is not touched. `//` finds it anywhere in
   the combined document:

   ```text
   //InterstellarComet[@Id='Oumuamua']
   ```

3. **Append the missing children**, with inline XML as the body of `<Copy>`. One `<Copy>` per body, each
   with exactly the elements that body lacks (`3I_ATLAS` already has a mass, so it gets only the SOI):

   ```xml
   <!-- AdvancedFlightComputer/Patches/HyperbolicBodies.xml L1-L20 -->
   <Patch>
     <!--
       Interstellar comets need a Mass and a SphereOfInfluence before the Transfer Planner can target
       them. Stock already gives 3I_ATLAS a Mass.
     -->

     <Copy Path="//InterstellarComet[@Id='Oumuamua']" Pos="Append">
       <Mass Gg="4.19" />
       <SphereOfInfluence Km="50000" />
     </Copy>

     <Copy Path="//InterstellarComet[@Id='Borisov']" Pos="Append">
       <Mass Gg="524" />
       <SphereOfInfluence Km="50000" />
     </Copy>

     <Copy Path="//InterstellarComet[@Id='3I_ATLAS']" Pos="Append">
       <SphereOfInfluence Km="50000" />
     </Copy>
   </Patch>
   ```

4. **Ship and list the file** under `patches` ([Patch Core or another mod's XML with a patches array in mod.toml](../patch-game-xml-data/patch-game-xml-data-1.md)).

5. **Handle absence in code.** Check the value you patched and fall back or log once when it is missing
   (see [Ship XML patches that apply only when KittenExtensions is installed](../patch-game-xml-data/patch-game-xml-data-2.md)).

## Complete example

- [AdvancedFlightComputer/Patches/HyperbolicBodies.xml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Patches/HyperbolicBodies.xml)
- The consuming code: [AdvancedFlightComputer/Features/HyperbolicTargets/HyperbolicTargets.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/HyperbolicTargets.cs)
  and [AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs)
- The stock bodies: [current/Content/Core/Astronomicals.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Astronomicals.xml#L4423-L4462)

## Variations and gotchas

- **Every comet at once (adapted, not from the mod).** The README's `<With>` and `<IfNone>` can express
  "add the SOI to every comet that lacks one" without listing Ids. Operations inside `<With>` run once
  per selected node with that node as the context; `<IfNone>` runs its children when the path is empty:

  ```xml
  <!-- adapted from KittenExtensions/README.md (operation reference) and AFC HyperbolicBodies.xml; not run by either mod -->
  <Patch>
    <With Path="//InterstellarComet">
      <IfNone Path="SphereOfInfluence">
        <Copy Pos="Append"><SphereOfInfluence Km="50000" /></Copy>
      </IfNone>
    </With>
  </Patch>
  ```

  The README documents these operations ([README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md#L133-L162)); the XPathPatch source is
  not checked out, so test an adapted patch with the stepper before shipping it.
- **Another mod's bodies.** The same path also matches bodies that other mods define in their own
  `Assets` files, because the document holds every manifest mod's XML. Scope with
  `Mod[@Id='Core']/Assets[@RelPath='Astronomicals.xml']/...` when you only want Core's.
- **Inline XML is validated late.** The patch engine copies whatever you write; a typo shows up as a
  deserialization problem when the bundle loads, not at the patch step.
- **Values are modelling choices.** AFC's 50,000 km SOI for comets is its own choice, not stock data.
- **Re-appending.** Run twice, `Append` would add a second `SphereOfInfluence`; patches run once per
  launch against a fresh document, but guard with `<IfNone>` if another patch might also add it.

## See also

- [Append a button to a stock gauge canvas with Copy and inline XML](xpath-xml-patch-recipes-1.md), [Rewrite many nodes with With, If and SetVar, and combine trees with Merge](xpath-xml-patch-recipes-3.md), [Patch Core or another mod's XML with a patches array in mod.toml](../patch-game-xml-data/patch-game-xml-data-1.md)
- [Transfer Planner targets on hyperbolic orbits](../../../../mods/ksa-advancedflightcomputer/features/hyperbolic-transfer-targets.md), [Debug XML patches with the step debugger and the session log](../../validation/debug-xml-patches/overview.md)
