---
type: Mod Feature
title: XML patch files and XPathPatch operations
description: A mod lists patch files under a patches key in its mod.toml, and each Patch file holds XPathPatch operations (Copy, Merge, Delete, If, IfAny, IfNone, With, SetVar) whose XPath 1.0 Path and From expressions select nodes in the combined GameData document
mod: kittenextensions
category: assets
techniques: [xml-patch, xpath]
hooks: [harmony-prefix KSA.ModLibrary.PrepareAll]
lifecycle: [startup:asset-load]
game_types: [KSA.AssetBundle, KSA.SystemTemplate, KSA.PlanetMeshCollection, KSA.GaugeCanvas]
tags: [xml, xml-patch, xpath, mod-toml, modulemanager, data-patching]
---

KittenExtensions gives KSA a ModuleManager-style patch language (the csproj description calls it
"XML Patching (ModuleManager like)"). A mod adds a `patches` array to its `mod.toml` and ships XML
files whose root is `<Patch>`. Each child of `<Patch>` is an operation that selects nodes with an XPath
1.0 expression and copies, merges, deletes, branches, scopes or stores values. The operations run
once at startup against the [combined GameData document](gamedata-document.md), so they can change
Core's files, another mod's files, or a later patch file.[^kx-readme] [^kx-csproj]

The engine is the separate XPathPatch library, a git submodule at `XPathPatch/`
([.gitmodules](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/.gitmodules)) referenced as a project from the csproj
([KittenExtensions/KittenExtensions.csproj](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/KittenExtensions.csproj#L54)). The submodule (commit
`2d91cd2d575fd2362645127a4f3b1b03169bffa9`) is not checked out in the analyzed copy, so the
semantics below come from the KittenExtensions README and from the action types KittenExtensions'
own logger and debugger reference, not from the library source.[^kx-gitmodules]

## How it works

1. **Declare the files.** `patches = [ "MyPatch.xml" ]` in `mod.toml`, paths relative to the mod
   folder ([README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md#L38-L51)). KittenExtensions reads the key with its own Tomlet class
   ([KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L179-L191)); the game ignores it.
2. **Context and paths.** Every operation starts with `<Root>` as its context node, so paths are
   written relative to it (`Mod[@Id='Core']/...`), not from `/`. `Path` must be an XPath 1.0
   expression that yields a node-set and is evaluated from the current context
   ([README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md#L87-L96)).
3. **Positions.** `<Copy>` takes `Pos`:

   | `Pos` | Element target | Text or attribute target |
   |---|---|---|
   | `Replace` (default) | replace the element | replace the value |
   | `Append` | add after the last child | append to the value |
   | `Prepend` | add before the first child | prepend to the value |
   | `Before` | insert as previous sibling | invalid |
   | `After` | insert as next sibling | invalid |

4. **Operations** ([README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md#L109-L179)):

   | Element | Attributes | Effect | Example |
   |---|---|---|---|
   | `<Copy>` | `Path` (default context), `From` (default: element contents), `Pos` | Writes the `From` value or the inline XML at every selected target | `<Copy Path="Mass/@Earths">1</Copy>` |
   | `<Merge>` | `Path`, `From`, `Pos` | Merges each source element into each target element (rules below) | `<Merge Path="Target XPath" From="Source XPath" />` |
   | `<Delete>` | `Path` (default context) | Removes the selected nodes | `<Delete Path="Target XPath" />` |
   | `<If>` | `Path` | Runs `<Any>` children when the result is true, non-zero and non-NaN, a non-empty string or a non-empty node-set, otherwise `<None>` children | `<If Path="sum(Color/@*) > 1.5"><Any>...</Any><None>...</None></If>` |
   | `<IfAny>` / `<IfNone>` | `Path` | Shorthand for `<If><Any>` and `<If><None>` | `<IfNone Path="Mass">...</IfNone>` (illustrative) |
   | `<With>` | `Path` | Runs its children once per selected node, with that node as context | `<With Path="//PlanetaryBody \| //AtmosphericBody">` |
   | `<SetVar>` | `Name`, `Path` (default: element text) | Stores a string, number or node-set as `$Name` for later expressions | `<SetVar Name="myvar1" Path="$myvar1*2" />` |

5. **Merge rules** ([README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md#L181-L216)): attributes of the source replace those of the
   target; each source child is matched to the first target child with the same element name and the
   same `Id` (or the same name when the source child has no `Id`); `_MergeId="Attr"` matches on another
   attribute, `_MergeId="*"` on name only, `_MergeId="-"` never matches; unmatched children and all
   text nodes are added, at the end by default or at the start with `_MergePos="Prepend"` on the source
   parent.
6. **What the executor reports.** KittenExtensions' logger counts operation-level actions of types
   `OpCopy`, `OpMerge`, `OpDelete`, `OpIf`, `OpIfAny`, `OpIfNone`, `OpWith` and `OpSetVar`, and records
   the document mutations they cause as `Set`, `Insert`, `InsertText` and `Remove`
   ([KittenExtensions/Patch/PatchLogger.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/PatchLogger.cs#L137-L143)).[^kx-patchlogger] Those eight
   operation types are the complete operation vocabulary visible from this repository. The
   `ActionType` enum and the parser that maps element names to it live in XPathPatch, so this list is
   evidence of the vocabulary, not proof that no other element name is accepted.

### A real patch: KSA-AdvancedFlightComputer

AFC appends a button to a Core gauge and adds mass and sphere-of-influence elements to comets, using
`<Copy Pos="Append">` with inline XML ([AdvancedFlightComputer/mod.toml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/mod.toml#L1-L2),
[AdvancedFlightComputer/Patches/HyperbolicBodies.xml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Patches/HyperbolicBodies.xml#L7-L19)). The gauge it targets is
`<GaugeCanvas Id="EngineControl">` in Core's `Gauges.xml`
([current/Content/Core/Gauges.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Gauges.xml#L765)).[^afc-modtoml] [^afc-hyperbolic] [^ksa-gauges]

## Lifecycle and timing

`startup:asset-load`. All operations run inside the Harmony prefix on `ModLibrary.PrepareAll`, before
the game deserializes any mod XML and before any `StarMapImmediateLoad` call (see
[Combined GameData XML document](gamedata-document.md#lifecycle-and-timing)). Nothing is re-run later:
a patch cannot see the selected star system, game settings or anything computed in code, and changing
a patch file requires a restart. Operations execute on the main thread, synchronously, in manifest
order and then document order (ordering per the README). Elements a patch adds are deserialized only
later, by the redirected loaders, so they must map to element names the game's serializers know;
custom element types are registered by KittenExtensions' `Program.Main` prefix, which runs before the
`Program` constructor and so before any patch (see
[Custom XML asset types](custom-xml-asset-types.md)).

## Key code

| Path | Role |
|---|---|
| [README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md#L33-L216) | The patch-format reference (operations, positions, merging) |
| [README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md#L218-L264) | Two worked examples |
| [KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L179-L191) | Where `patches` is read from `mod.toml` |
| [KittenExtensions/Patch/PatchLogger.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/PatchLogger.cs#L137-L143) | Operation and mutation action types |
| [AdvancedFlightComputer/Patches/AutoStageButton.xml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Patches/AutoStageButton.xml) | A production patch from another mod |

## Snippets

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

```xml
<!-- AdvancedFlightComputer/Patches/AutoStageButton.xml L1-L21 -->
<Patch>
  <!-- Adds an AUTOSTAGE toggle button to the EngineControl gauge panel, in the free lower-left
       slot of its 2x2 button grid (RCS / IGNITE above, AUTOSTAGE / SHUTDN below).

       EngineControl rather than BurnControl, because BurnControl only draws while a burn is
       planned (VisibleInContext=Burn) and both staging triggers work without one. EngineControl
       draws whenever the vehicle carries an engine at all. -->
  <Copy Path="//GaugeCanvas[@Id='EngineControl']/Component[@Name='Buttons']" Pos="Append">
    <Button X="0.006" Y="0.5" Width="0.4854" Height="0.4994"
            Label="AUTOSTAGE" TextScale="0.28">
      <TextColor Name="Black" />
      <BackColor Name="AlmostBlack" A="0" />
      <DisabledColor R="0.1" G="0.1" B="0.1" />
      <IdleColor R="0.175" G="0.18" B="0.19" />
      <ActiveColor Name="Green" />
      <SoundEvent SoundId="LedButtonDown" Action="Pressed" />
      <FlightComputer Action="AfcAutoStageToggle" Value="Enabled" Toggle="true"/>
      <Tooltip Value="Automatically stage when engines run out of propellant, and drop spent boosters while the rest of the stage keeps firing."/>
    </Button>
  </Copy>
</Patch>
```

## Game APIs touched

Patches touch game data, not game code. The node names they target are the element names of the
game's XML serializers, for example the `AssetBundle` root `<Assets>` and its child element list, or
the `<System>` root of a system template.

| Data | Where it lives in build 2026.10.10.5554 |
|---|---|
| `<System Id="Sol">` with `<LoadFromLibrary Id="Venus" Parent="Sol"/>` | [current/Content/Core/SolSystem.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/SolSystem.xml#L2-L38) |
| `<AtmosphericBody Id="Venus">` under `<Assets>` | [current/Content/Core/Astronomicals.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Astronomicals.xml#L34) |
| `<GaugeCanvas Id="EngineControl">` | [current/Content/Core/Gauges.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Gauges.xml#L765) |

## Pitfalls and version notes

- **`<Remove>` is undocumented.** The README's first example uses `<Remove Path=...>`
  ([README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md#L237)), but the README's operation reference documents only `<Delete>`, and
  the operation action types KittenExtensions' logger handles include `OpDelete` and no remove
  operation (`Remove` exists only as a low-level mutation type). Whether XPathPatch rejects, ignores or
  aliases a `<Remove>` element cannot be checked here because its source is not in the checkout. Use
  the documented `<Delete>`.
- **The README's Venus example does not match current Core data.** It copies
  `/Root/Mod[@Id='Core']/System[@Id='SolSystem']/AtmosphericBody[@Id='Venus']`
  ([README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md#L221-L240)). In build 2026.10.10.5554 the root of `SolSystem.xml` is
  `<System Id="Sol">` and Venus there is a `<LoadFromLibrary>` reference; the full
  `<AtmosphericBody Id="Venus">` definition is in `Astronomicals.xml` under `<Assets>`. A working path
  is `Mod[@Id='Core']/Assets[@RelPath='Astronomicals.xml']/AtmosphericBody[@Id='Venus']`.[^ksa-solsystem] [^ksa-astronomicals]
- **Unscoped `//` paths see patch payloads.** Patch files are part of the document, so
  `//Button` or `//Mass` also match elements sitting inside other mods' `<Copy>` bodies that have not
  run yet. Prefer `Mod[@Id='Core']//...` or a `RelPath` predicate.
- **A failing operation stops the game.** When the executor ends with an error, KittenExtensions shows
  an error dialog with the offending element and exits
  ([KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L85-L102)). Test with the
  [patch debug stepper](patch-debug-stepper.md).
- **`Before` and `After` are invalid for attributes and text.**
- **No cross-mod ordering control.** There is no "after mod X" directive; order is manifest order.
- **Patches cannot add files.** They change the content of files that some `mod.toml` already lists.
  To add new assets, list your own asset file under `assets`.
- **The format is not final.** The README says the patch format may change
  ([README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md#L33-L34)).

## See also

- [Combined GameData XML document](gamedata-document.md)
- [Patch debug stepper](patch-debug-stepper.md)
- [Patch session log](patch-session-log.md)
- [Custom XML asset types via KxAsset attributes](custom-xml-asset-types.md)
- [KSA-AdvancedFlightComputer optional KittenExtensions patches](../../ksa-advancedflightcomputer/features/optional-kittenextensions-patches.md)
- [Patch Core or another mod's XML with a patches array in mod.toml](../../../howto/assets/xml-patching/patch-game-xml-data/patch-game-xml-data-1.md) - how-to article demonstrating this feature
- [Append a button to a stock gauge canvas with Copy and inline XML](../../../howto/assets/xml-patching/xpath-xml-patch-recipes/xpath-xml-patch-recipes-1.md) - how-to article demonstrating this feature
- [Add missing mass and sphere-of-influence elements to a kind of body](../../../howto/assets/xml-patching/xpath-xml-patch-recipes/xpath-xml-patch-recipes-2.md) - how-to article demonstrating this feature
- [Rewrite many nodes with With, If and SetVar, and combine trees with Merge](../../../howto/assets/xml-patching/xpath-xml-patch-recipes/xpath-xml-patch-recipes-3.md) - how-to article demonstrating this feature

[^kx-readme]: KittenExtensions README.md
[^kx-csproj]: KittenExtensions/KittenExtensions.csproj
[^kx-gitmodules]: KittenExtensions .gitmodules
[^kx-patchlogger]: KittenExtensions/Patch/PatchLogger.cs
[^afc-modtoml]: AdvancedFlightComputer/mod.toml
[^afc-hyperbolic]: AdvancedFlightComputer/Patches/HyperbolicBodies.xml
[^ksa-gauges]: Core Gauges.xml (build 2026.10.10.5554)
[^ksa-solsystem]: Core SolSystem.xml (build 2026.10.10.5554)
[^ksa-astronomicals]: Core Astronomicals.xml (build 2026.10.10.5554)
