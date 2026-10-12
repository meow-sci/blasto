---
type: How-To
title: Rewrite many nodes with With, If and SetVar, and combine trees with Merge
description: Use With to run operations once per selected node, If to branch on an XPath result, SetVar to keep values, and Merge with _MergeId and _MergePos to combine elements without listing every child
category: assets
group: xml-patching
topic: xpath-xml-patch-recipes
approach: 3
difficulty: advanced
prerequisites: [starmap, kittenextensions]
demonstrated_by: [mods/kittenextensions/features/xpath-patch-operations]
techniques: [xml-patch]
hooks:
  - kittenextensions patch With
  - kittenextensions patch If
  - kittenextensions patch SetVar
  - kittenextensions patch Merge
lifecycle: [startup:asset-load]
game_types: [KSA.AssetBundle, KSA.SystemTemplate, KSA.PlanetMeshCollection]
tags: [xml-patch, xpath, with, if, setvar, merge, readme]
---

When one change has to apply to many nodes, or depends on the value already there, listing every `Id` by
hand does not scale. The patch language has scoping (`With`), branching (`If`, `IfAny`, `IfNone`), variables
(`SetVar`) and a structural `Merge`. This article is the operation reference with the two worked
examples from the KittenExtensions README, and explains how to use them against Core's body data. The
operations are specified by the README; the executor (the XPathPatch library) is a git submodule that is not
in the pinned checkout, so everything below that is not a README quote should be tested with the stepper
([Debug XML patches with the step debugger and the session log](../../validation/debug-xml-patches/overview.md)) before you ship it.

Demonstrated by [XML patch files and XPathPatch operations](../../../../mods/kittenextensions/features/xpath-patch-operations.md). Mechanism: [Patch Core or another mod's XML with a patches array in mod.toml](../patch-game-xml-data/patch-game-xml-data-1.md).

## Background: how the game does it

The game has no equivalent; the operations run on an in-memory XML document that KittenExtensions builds
from all manifest mods' XML, and the game later deserializes the result. What matters about the game's data
for these recipes:

- Body templates live in `Astronomicals.xml` as `PlanetaryBody` and `AtmosphericBody` elements whose
  children include `Color` (R, G, B attributes) and `Mass`. For example Mercury:

  ```xml
  <!-- current/Content/Core/Astronomicals.xml L10-L33 -->
      <PlanetaryBody Id="Mercury" Parent="Sol">
          <Orbit DefinitionFrame="Ecliptic">
              <!-- JPL Horizons ephemerides at Julian Day 2461009.500000000 -->
              <SemiMajorAxis Km="5.790896153292818E+07" />
              <Inclination Degrees="7.003433958539783E+00" />
              <Eccentricity Value="2.056462028967717E-01" />
              <LongitudeOfAscendingNode Degrees="4.829884437379905E+01" />
              <ArgumentOfPeriapsis Degrees="2.919855011314794E+01" />
              <TimeAtPeriapsis Seconds="-563615.3399035392" />
          </Orbit>
          <Rotation DefinitionFrame="Ecliptic">
              <!-- JPL Horizons ephemerides at Julian Day 2461009.500000000 -->
              <SiderealPeriod Days="58.6463" />
              <Tilt Degrees="7.0354" />
              <Azimuth Degrees="228.20282" />
              <InitialParentFacingLongitude Degrees="179.685634" />
          </Rotation>

          <MeshCollection Id="Default"/>
          <MeanRadius Km="2439.7" />
          <Mass Yg="330.2" />
          <Color R="1" G="1" B="1" />
      </PlanetaryBody>

  ```

- `Merge` matches children by element name plus `Id`. Most asset elements in Core carry an `Id`, which
  is what makes merging mod data into an existing asset workable.

## Lifecycle and timing

`startup:asset-load`: patches run once in the `ModLibrary.PrepareAll` prefix, on the main thread, before
any XML is deserialized. A patch sees the data, not run-time state; variables set by `SetVar` live only for
the patch run. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Positions, as used by Copy.** `Pos` has these meanings (default `Replace`):

   ```text
   # README.md L101-L107
   | `Pos` | Element | Text/Attribute |
   | --- | --- | --- |
   | `Replace` | replace entire element | replace value |
   | `Append` | add to end of children | add to end of value |
   | `Prepend` | add to beginning of children | add to beginning of value |
   | `Before` | insert as previous sibling | Invalid |
   | `After` | insert as following sibling | Invalid |
   ```

2. **Run operations once per node with `<With>`.** The `Path` selects the context nodes; relative paths
   inside use that node. The context starts as `<Root>`. Union with `|` selects several kinds of body:

   ```xml
   <!-- README.md L159-L161 -->
   <With Path="Context XPath">
     <!-- any patch op element -->
   </With>
   ```

3. **Branch with `<If>`, and rewrite attributes with `Copy From`.** The README's example brightens or
   darkens every body's orbit colour depending on the sum of its RGB attributes. `From` is an XPath
   expression evaluated at each selected attribute, with `.` the attribute value:

   ```xml
   <!-- README.md L245-L263 -->
   <Patch>
     <!-- run for each PlanetaryBody and AtmosphericBody anywhere in the GameData document -->
     <With Path="//PlanetaryBody | //AtmosphericBody">
       <If Path="sum(Color/@*) > 1.5">
         <Any>
           <!-- if R+G+B of orbit color is >1.5 make each RGB val brighter -->
           <With Path="Color/@*">
             <Copy From="1-(1-.)*0.5" />
           </With>
         </Any>
         <None>
           <!-- otherwise make each RGB val darker -->
           <With Path="Color/@*">
             <Copy From=".*0.5" />
           </With>
         </None>
       </If>
     </With>
   </Patch>
   ```

   What happens: for each `PlanetaryBody` or `AtmosphericBody` anywhere in the document, if
   `sum(Color/@*) > 1.5` then every `Color` attribute `c` becomes `1-(1-c)*0.5`; otherwise `c*0.5`.
   `<If>` runs the `<Any>` children when the path is true, a non-zero number, a non-empty string or a
   non-empty node-set, and the `<None>` children otherwise. `<IfAny>` and `<IfNone>` are the one-armed forms:

   ```xml
   <!-- README.md L136-L153 -->
   <If Path="XPath Expression">
     <Any>
       <!-- any patch op element -->
       <!-- runs when Path is true, non-zero and non-NaN, a non-empty string, or a non-empty node-set -->
     </Any>
     <None>
       <!-- any patch op element -->
       <!-- runs when Path is false, zero or NaN, an empty string, or an empty node-set -->
     </None>
   </If>
   <IfAny Path="XPath Expression">
     <!-- any patch op element -->
     <!-- equivalient to <If><Any>...</Any></If> -->
   </IfAny>
   <IfNone Path="XPath Expression">
     <!-- any patch op element -->
     <!-- equivalient to <If><None>...</None></If> -->
   </IfNone>
   ```

4. **Keep values in variables with `<SetVar>`.** A variable can hold a string, a number or a node-set and
   is referenced as `$name` in later paths (it can reference its own previous value):

   ```xml
   <!-- README.md L167-L178 -->
   <!-- sets $myvar0 to the string 'Value' -->
   <SetVar Name="myvar0">Value</SetVar>
   <!-- sets $myvar to the sum of all attribute values holding positive numbers -->
   <SetVar Name="myvar1" Path="sum(//@*[.>0])" />
   <!-- overwrites $myvar1, referencing its last value in the expression -->
   <SetVar Name="myvar1" Path="$myvar1*2" />
   <!-- stores a node list in $myvar2 -->
   <SetVar Name="myvar2" Path="Mod/Assets" />
   <!-- use the stored $myvar2 node list in a path expression -->
   <With Path="$myvar2/Character">
     <!-- ... -->
   </With>
   ```

5. **Combine trees with `<Merge>`.** Each selected source element is merged into each selected target:
   attributes are copied over, then each source child is matched to a target child. The README's table:

   ```text
   # README.md L181-L194
   #### Merging
   When using the `<Merge>` operation, each selected source element is merged with each selected target element.
   - All attributes are copied from the source element (replacing the existing value if present)
   - Each child of the source is matched with a child node in the target
     - If the source element has an `Id` attribute, it matches with the first child element of the target with the same element name and `Id`
     - If the source element does not have an `Id`, it matches with the first child element of the target with the same element name
     - The attribute used to match can be configured for each source element with a `_MergeId` attribute
       - `_MergeId="*"` means any matching element name
       - `_MergeId="-"` means never match
       - `_MergeId="AttrName"` means match non-empty values of the `AttrName` attribute
     - If a match is not found (and for all text elements), the source child node is added as a child of the target element
     - The position the child is inserted can be controlled with a `_MergePos` attribute on the source parent element
       - `_MergePos="Append"` (default) adds unmatched nodes after existing children
       - `_MergePos="Prepend"` adds unmatched nodes before existing children
   ```

   and its worked example with the result:

   ```xml
   <!-- README.md L197-L215 -->
   <!-- Merging -->
   <Source _MergePos="Prepend" A="B">
     <X Id="1">a</X>
     <X Id="2" Name="Y" _MergeId="Name">b</X>
     <X Id="3" _MergeId="-">c</X>
   </Source>
   <!-- Into -->
   <Target>
     <X Id="1">d</X>
     <X Id="2">e</X>
     <X Id="3" Name="Y" Z="true">f</X>
   </Target>
   <!-- Produces -->
   <Target A="B">
     <X Id="3">c</X> <!-- Source X 3 prepended since _MergeId set to not match -->
     <X Id="1">a</X>
     <X Id="2">e</X> <!-- not matched since _MergeId set to Name -->
     <X id="2" Name="Y" Z="true">b</X> <!-- Source X 2 merged into Target X 3 matched by Name -->
   </Target>
   ```

   (The README's result block writes the last element as `<X id="2" ...>` with a lowercase `id`; that is a
   typo, the merged element keeps the source's `Id="2"`.) Use `_MergeId="-"` on a child you always want added, `_MergeId="Name"` to match on a different attribute
   and `_MergePos="Prepend"` on the source parent to place unmatched children first.

6. **Scope to the file you mean.** Every operation starts with `<Root>` as the context. Unscoped `//` paths also
   match elements that sit inside other mods' unexecuted `<Copy>` payloads and in your own patch file.
   Prefer `Mod[@Id='Core']/Assets[@RelPath='Astronomicals.xml']/...` when the target is Core's file.

7. **Test the patch** in the debugger: it shows each action's target and source results and the document
   after each step ([Debug XML patches with the step debugger and the session log](../../validation/debug-xml-patches/overview.md)).

## Complete example

- The README sections: [README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md#L90-L216) (operations) and
  [README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md#L218-L264) (examples).
- The matched data: [current/Content/Core/Astronomicals.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Astronomicals.xml#L10-L33) (Mercury).
- A real patch file using plain `Copy` is [AdvancedFlightComputer/Patches/HyperbolicBodies.xml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Patches/HyperbolicBodies.xml)
  ([Add missing mass and sphere-of-influence elements to a kind of body](xpath-xml-patch-recipes-2.md)).

## Variations and gotchas

- **`Remove` versus `Delete`.** The README's Venus example (L237) writes `<Remove Path="Clouds/CloudType[@Name='Stratus']" />`
  while the operation reference documents `<Delete Path="..." />`. The action types KittenExtensions' own
  logger handles include `OpDelete` and no remove operation (`Remove` exists only as a low-level mutation
  type, [KittenExtensions/Patch/PatchLogger.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/PatchLogger.cs#L137-L143)), so use `<Delete>`.
- **The README's Venus example is out of date with Core.** It copies
  `/Root/Mod[@Id='Core']/System[@Id='SolSystem']/AtmosphericBody[@Id='Venus']`. In this game build the root of
  `SolSystem.xml` is `<System Id="Sol">` (not `SolSystem`) and it only references Venus with
  `<LoadFromLibrary Id="Venus" Parent="Sol"/>`
  ([current/Content/Core/SolSystem.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/SolSystem.xml#L38-L38)); the full `AtmosphericBody`
  definition is in `Astronomicals.xml` under `<Assets>`, so the working path is
  `Mod[@Id='Core']/Assets[@RelPath='Astronomicals.xml']/AtmosphericBody[@Id='Venus']`.
- **The colour example also hits other mods' bodies.** `//PlanetaryBody | //AtmosphericBody` matches every
  mod's bodies in the document. Add a `Mod[@Id='Core']` prefix if the change is meant for stock only.
- **`Before` and `After` are invalid on attributes and text.** They only work when the target is an element.
- **No cross-mod ordering directive.** Order is manifest order; use `If`/`IfNone` guards so two patches that
  both add the same element do not duplicate it.
- **A failing operation exits the game** with an error popup showing the element; develop with `debug = true`.

## See also

- [Append a button to a stock gauge canvas with Copy and inline XML](xpath-xml-patch-recipes-1.md), [Add missing mass and sphere-of-influence elements to a kind of body](xpath-xml-patch-recipes-2.md), [Patch Core or another mod's XML with a patches array in mod.toml](../patch-game-xml-data/patch-game-xml-data-1.md)
- [XML patch files and XPathPatch operations](../../../../mods/kittenextensions/features/xpath-patch-operations.md), [Redirect the game to patched copies of its XML and coordinate edits between mods](../redirect-game-xml-loading/overview.md), [Add to, drive and hide the stock cockpit gauges and navball](../../../ui/menus/work-with-stock-cockpit-gauges/overview.md)
