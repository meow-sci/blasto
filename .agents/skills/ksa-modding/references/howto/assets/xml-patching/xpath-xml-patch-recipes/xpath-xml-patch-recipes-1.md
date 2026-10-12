---
type: How-To
title: Append a button to a stock gauge canvas with Copy and inline XML
description: Target a stock gauge canvas by Id with an XPath, append a new Button element with Copy Pos="Append" and inline XML, and pick a canvas that is actually visible when you need it
category: assets
group: xml-patching
topic: xpath-xml-patch-recipes
approach: 1
difficulty: intermediate
prerequisites: [starmap, kittenextensions]
demonstrated_by:
  - mods/ksa-advancedflightcomputer/features/autostage-gauge-button
  - mods/kittenextensions/features/xpath-patch-operations
techniques: [xml-patch]
hooks: [kittenextensions patch Copy]
lifecycle: [startup:asset-load]
game_types: [KSA.AssetBundle, KSA.GaugeCanvas, KSA.GaugeComponent, KSA.GaugeButtonReference]
tags: [xml-patch, xpath, gauges, copy-append, inline-xml]
---

The simplest useful patch adds a new child to a stock element. KSA-AdvancedFlightComputer (AFC) adds an
AUTOSTAGE button to the stock `EngineControl` gauge canvas with one `<Copy Path="..." Pos="Append">`
whose body is the new `<Button>` element. This article walks through how to find the target in
Core's XML, write the XPath, choose the right canvas, and what you still need in code (the UI side is
[Add to, drive and hide the stock cockpit gauges and navball](../../../ui/menus/work-with-stock-cockpit-gauges/overview.md)). The patch mechanism itself is in [Patch Core or another mod's XML with a patches array in mod.toml](../patch-game-xml-data/patch-game-xml-data-1.md).

Demonstrated by [A custom gauge button action backed by a mod enum](../../../../mods/ksa-advancedflightcomputer/features/autostage-gauge-button.md) and
[XML patch files and XPathPatch operations](../../../../mods/kittenextensions/features/xpath-patch-operations.md).

## Background: how the game does it

Core's `Gauges.xml` defines each gauge panel as a `<GaugeCanvas Id="...">` containing named
`<Component Name="...">` children. The `GaugeCanvas` class maps the `Component` element to a list
of `GaugeComponent`, and a `GaugeComponent` collects `Digit`, `Label`, `Button`, `Box` and `Rect`
children into one list ([current/decomp/KSA/GaugeCanvas.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeCanvas.cs#L103-L107), [current/decomp/KSA/GaugeComponent.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GaugeComponent.cs#L43-L48)). A `Button`
element is a `GaugeButtonReference`. So adding a button to an existing panel means appending a
`<Button>` element to the `Buttons` component of that canvas.

The stock EngineControl canvas shows the 2x2 button grid with a free slot at `X=0.006 Y=0.5`:

```xml
<!-- current/Content/Core/Gauges.xml L883-L912 -->
            <Button X="0.006" Y="0" Width="0.4854" Height="0.4994" TextScale="0.33" Label="RCS">
                <Tooltip Value="Toggles the RCS thrusters." />
                <TextColor Name="Black" />
                <BackColor Name="AlmostBlack" A="0" />
                <DisabledColor R="0.1" G="0.1" B="0.1" />
                <IdleColor R="0.175" G="0.18" B="0.19" />
                <ActiveColor Name="Green" />
                <SoundEvent SoundId="LedButtonDown" Action="Pressed" />
                <FlightComputer Action="FlightComputerRCSMode" Value="Enabled" Toggle="true" />
            </Button>
            <Button X="0.512" Y="0" Width="0.4854" Height="0.4994" TextScale="0.33" Label="IGNITE">
                <Tooltip Value="Ignites the engines." />
                <TextColor Name="Black" />
                <BackColor Name="AlmostBlack" A="0" />
                <DisabledColor R="0.1" G="0.1" B="0.1" />
                <IdleColor R="0.175" G="0.18" B="0.19" />
                <ActiveColor Name="Green" />
                <SoundEvent SoundId="LedButtonDown" Action="Pressed" />
                <FlightComputer Action="VehicleEngine" Value="MainIgnite" Toggle="false" />
            </Button>
            <Button X="0.512" Y="0.5" Width="0.4854" Height="0.4994" TextScale="0.33" Label="SHUTDN">
                <Tooltip Value="Shuts down the engines." />
                <TextColor Name="Black" />
                <BackColor Name="AlmostBlack" A="0" />
                <DisabledColor R="0.1" G="0.1" B="0.1" />
                <IdleColor R="0.175" G="0.18" B="0.19" />
                <ActiveColor Name="Red" />
                <SoundEvent SoundId="LedButtonDown" Action="Pressed" />
                <FlightComputer Action="VehicleEngine" Value="MainShutdown" Toggle="false" />
            </Button>
```

There are two canvases with a Buttons component you might think of: `EngineControl` has
`<VisibleInContext>Engines</VisibleInContext>` and `BurnControl` has `<VisibleInContext>Burn</VisibleInContext>`
([current/Content/Core/Gauges.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Gauges.xml#L32-L34), [current/Content/Core/Gauges.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Gauges.xml#L765-L767)). The context
decides when a canvas is drawn at all, so a button on a canvas that is hidden when you need the button is
useless.

## Lifecycle and timing

`startup:asset-load`. The patch is applied to the in-memory document in the `ModLibrary.PrepareAll`
prefix; `Gauges.xml` is deserialized afterwards by the redirected `Mod.LoadAssetBundles`. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

Anything the new element names must exist by then. This button's `<FlightComputer Action="AfcAutoStageToggle" .../>`
binds an enum type by name when the gauge XML loads, so the mod registers that type from
`StarMapImmediateLoad`, which runs earlier (see [Ship XML patches that apply only when KittenExtensions is installed](../patch-game-xml-data/patch-game-xml-data-2.md)).

## Steps

1. **Find the target canvas and component** in `current/Content/Core/Gauges.xml` (the Core mod folder in
   the game install). Note the canvas `Id`, the component `Name` and the free space. List what is
   already there; AFC's comment documents the layout it found:

   ```xml
   <!-- AdvancedFlightComputer/Patches/AutoStageButton.xml L1-L7 -->
   <Patch>
     <!-- Adds an AUTOSTAGE toggle button to the EngineControl gauge panel, in the free lower-left
          slot of its 2x2 button grid (RCS / IGNITE above, AUTOSTAGE / SHUTDN below).

          EngineControl rather than BurnControl, because BurnControl only draws while a burn is
          planned (VisibleInContext=Burn) and both staging triggers work without one. EngineControl
          draws whenever the vehicle carries an engine at all. -->
   <!-- ... -->
   ```

2. **Pick the canvas by when it is drawn**, not by looks. Check its `VisibleInContext`. AFC chose
   `EngineControl` because it draws whenever the vehicle carries an engine, while `BurnControl` draws only
   while a burn is planned.

3. **Write the XPath** from `<Root>` (the context node of every operation). Select by element name and
   `Id`/`Name` attributes, not by position. The `//` prefix matches in every mod's imported XML;
   add `Mod[@Id='Core']/` in front if you want to be certain not to touch another mod's canvas of the
   same `Id`:

   ```text
   //GaugeCanvas[@Id='EngineControl']/Component[@Name='Buttons']
   ```

4. **Append the new element.** `Pos="Append"` adds the inline XML after the target's last child. Use
   the same child elements stock buttons use (`TextColor`, `BackColor`, `DisabledColor`, `IdleColor`,
   `ActiveColor`, `Tooltip`, plus the binding element), copied from a neighbour:

   ```xml
   <!-- AdvancedFlightComputer/Patches/AutoStageButton.xml L8-L20 -->
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
   ```

5. **List the file in `mod.toml`** under `patches` and ship it beside the DLL
   ([Patch Core or another mod's XML with a patches array in mod.toml](../patch-game-xml-data/patch-game-xml-data-1.md)).

6. **Register the code the XML names**, if any (an enum type here), at `startup:immediate-load`, and
   implement what the button does. That is the subject of [Add to, drive and hide the stock cockpit gauges and navball](../../../ui/menus/work-with-stock-cockpit-gauges/overview.md).

7. **Check the result.** Start the game with `debug = true` on the `KittenExtensionsContinued` manifest entry (the game rewrites the manifest without that key when it saves it, so re-add it if the stepper does not appear) to see
   the patched tree and the target XPath's match count ([Debug XML patches with the step debugger and the session log](../../validation/debug-xml-patches/overview.md)).

## Complete example

- [AdvancedFlightComputer/Patches/AutoStageButton.xml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Patches/AutoStageButton.xml) (whole patch)
- [AdvancedFlightComputer/mod.toml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/mod.toml) (the `patches` entry)
- [current/Content/Core/Gauges.xml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/Gauges.xml#L765-L913) (the stock canvas)

## Variations and gotchas

- **Assume a no-match is silent.** If the `Id` or `Name` is mistyped or a game update renames them, `Path` selects
  an empty node-set and nothing is appended; neither the README nor the KittenExtensions code documents an error for an
  empty selection (the executor is not checked out), so use the stepper (a target with zero results) and the session log to find out.
  `Pos` semantics for `Append` on an element are "add to end of children".
- **Appended elements are plain XML.** The patch does not know the game's schema; a misspelled element
  or attribute name loads as an unknown element and is silently dropped by the serializer, or breaks the
  load.
- **Layout is yours to keep in sync.** Coordinates are fractions of the component's size; if stock
  rearranges the grid in a later build your button overlaps another.
- **Other positions.** `Prepend` inserts before the first child, `Before` and `After` insert as siblings
  of the target (use them to put a new element beside an existing button); see
  [Rewrite many nodes with With, If and SetVar, and combine trees with Merge](xpath-xml-patch-recipes-3.md) for the full table.

## See also

- [Add missing mass and sphere-of-influence elements to a kind of body](xpath-xml-patch-recipes-2.md), [Rewrite many nodes with With, If and SetVar, and combine trees with Merge](xpath-xml-patch-recipes-3.md), [Ship XML patches that apply only when KittenExtensions is installed](../patch-game-xml-data/patch-game-xml-data-2.md)
- [A custom gauge button action backed by a mod enum](../../../../mods/ksa-advancedflightcomputer/features/autostage-gauge-button.md), [Add to, drive and hide the stock cockpit gauges and navball](../../../ui/menus/work-with-stock-cockpit-gauges/overview.md)
