---
type: How-To
title: Ship XML patches that apply only when KittenExtensions is installed
description: Declare KittenExtensions as an optional StarMap dependency, ship patch files beside the DLL, make the code side degrade cleanly when the patch did not run, and package the patch files in the csproj
category: assets
group: xml-patching
topic: patch-game-xml-data
approach: 2
difficulty: intermediate
prerequisites: [starmap, kittenextensions]
demonstrated_by: [mods/ksa-advancedflightcomputer/features/optional-kittenextensions-patches]
techniques: [xml-patch, toml-config, msbuild]
hooks: [starmap StarMapImmediateLoad, starmap StarMapAllModsLoaded]
lifecycle: [startup:asset-load, startup:immediate-load, startup:all-mods-loaded]
game_types: [KSA.ModLibrary, KSA.Mod, KSA.ModEntry, KSA.ModManifest, KSA.XmlLoader, KSA.AssetBundle]
tags: [xml-patch, optional-dependency, mod-toml, msbuild, kittenextensions, degradation]
---

A code mod often needs one or two bits of changed game data (a button on a stock gauge, a missing
element on some bodies) that C# cannot supply at the right moment, but should not force players to
install a second mod for the whole of the mod's value. KSA-AdvancedFlightComputer (AFC) does this
with two KittenExtensions patch files and an optional dependency: with KittenExtensions the data is
patched, without it every other feature still works and the two XML-dependent features degrade.

Prerequisites: the plumbing in [Patch Core or another mod's XML with a patches array in mod.toml](patch-game-xml-data-1.md) (the `patches` key, the document, run order)
and a StarMap code mod ([Declare a dependency on another StarMap mod, required or optional](../../../getting-started/dependencies/declare-a-mod-dependency/overview.md)). Demonstrated by
[Optional KittenExtensions XML patches from mod.toml](../../../../mods/ksa-advancedflightcomputer/features/optional-kittenextensions-patches.md).

## Background: how the game does it

The game knows nothing about patches or about KittenExtensions. Two things make this pattern work.

- StarMap reads `[[StarMap.ModDependencies]]` from `mod.toml` and has an `Optional` flag
  ([StarMap.Core/Config/StarMapConfig.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Config/StarMapConfig.cs#L13-L26)). A mod whose only missing dependencies are
  optional is still loaded, after all other mods have had their chance
  ([StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L106-L131)).
- KittenExtensions reads each mod's `mod.toml` itself, from the manifest, so the patch files are
  picked up regardless of what StarMap decided about load order. The game's `Mod` ignores the unknown
  `patches` key ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L56-L72)).

## Lifecycle and timing

| What | Stage | Why |
|---|---|---|
| KittenExtensions applies the patches | `startup:asset-load`, prefix on `ModLibrary.PrepareAll` | Before any system or asset XML is read; see [Patch Core or another mod's XML with a patches array in mod.toml](patch-game-xml-data-1.md) |
| Code that the patched data names (here an enum type looked up by gauge XML) | `startup:immediate-load` (`[StarMapImmediateLoad]`) | `PrepareAll` calls every mod's `PrepareSystems` before `LoadAll`, where `GaugeButtonFlightComputer.OnDataLoad` resolves the `Action` name against the registered enum types (an unregistered name logs `unknown enum type '...'`); `StarMapAllModsLoaded` is a postfix on `LoadAll` and too late |
| Code that is independent of the patched data | `startup:all-mods-loaded` | AFC applies its Harmony patch sets there |

AFC's `Mod.cs` shows both hooks side by side
([AdvancedFlightComputer/Mod.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Mod.cs#L18-L28)). The full sequence is in [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **List the patch files and mark KittenExtensions optional** in `mod.toml`. `patches` is a top-level
   key; the dependency goes in the StarMap table with `Optional = true`:

   ```toml
   # AdvancedFlightComputer/mod.toml L1-L9
   name = "AdvancedFlightComputer"
   patches = [ "Patches/HyperbolicBodies.xml", "Patches/AutoStageButton.xml" ]

   [StarMap]
   EntryAssembly = "AdvancedFlightComputer"

   [[StarMap.ModDependencies]]
   ModId = "KittenExtensions"
   Optional = true
   ```

2. **Write the patch files** under `Patches/`. Keep each file to one purpose so a failure points at one
   file, and put the reason in a comment, as AFC does. This one adds a button to the stock
   `EngineControl` gauge canvas (it is the UI half of the feature described in
   [Write XPath patch operations for game XML](../xpath-xml-patch-recipes/overview.md)):

   ```xml
   <!-- AdvancedFlightComputer/Patches/AutoStageButton.xml L1-L8 -->
   <Patch>
     <!-- Adds an AUTOSTAGE toggle button to the EngineControl gauge panel, in the free lower-left
          slot of its 2x2 button grid (RCS / IGNITE above, AUTOSTAGE / SHUTDN below).

          EngineControl rather than BurnControl, because BurnControl only draws while a burn is
          planned (VisibleInContext=Burn) and both staging triggers work without one. EngineControl
          draws whenever the vehicle carries an engine at all. -->
     <Copy Path="//GaugeCanvas[@Id='EngineControl']/Component[@Name='Buttons']" Pos="Append">
   <!-- ... -->
   ```

   The second file only appends data the game forgot to define for some bodies:

   ```xml
   <!-- AdvancedFlightComputer/Patches/HyperbolicBodies.xml L1-L12 -->
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
   <!-- ... -->
   ```

3. **Make the code side robust to the patch not having run.** The code that consumes patched data must
   check for it and degrade, not throw. AFC's target-list postfix skips a comet whose sphere of
   influence is missing and logs the reason once:

   ```csharp
   // AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs L36-L47
                   if (body is Vehicle || body is not IOrbiter orbiter) continue;
                   if (orbiter.Orbit == null || orbiter.Orbit.Eccentricity < 1.0) continue;
                   if (orbiter.Parent != frame) continue;
                   if (orbiter.Id == source.Id || orbiter.Id == source.Parent?.Id) continue;

                   if (orbiter.SphereOfInfluence <= 0.0 || double.IsNaN(orbiter.SphereOfInfluence))
                   {
                       LogHelper.WarnOnce($"soi-missing-{orbiter.Id}",
                           $"[AFC] {orbiter.Id} has no SOI, XML patch may be missing");
                       continue;
                   }

   ```

   For the button, AFC registers the enum the XML names at `startup:immediate-load`, and offers the
   same switch on its own settings page for installs without KittenExtensions (see
   [Add to, drive and hide the stock cockpit gauges and navball](../../../ui/menus/work-with-stock-cockpit-gauges/overview.md)):

   ```csharp
   // AdvancedFlightComputer/Mod.cs L25-L28

       // The AUTOSTAGE gauge button resolves its enum by name while the game reads Gauges.xml, which happens before any AllModsLoaded hook.
       [StarMapImmediateLoad]
       public void OnImmediateLoad(KSA.Mod mod) => AutoStageFeature.InjectGaugeEnumAtLoad();
   ```

4. **Copy the patch files into the install folder.** `mod.toml` entries are resolved against the mod
   directory, so `Patches/*.xml` has to land next to the DLL. AFC declares an MSBuild item once and
   lets the shared deploy targets and the release packager both use it:

   ```xml
   <!-- AdvancedFlightComputer/AdvancedFlightComputer.csproj L56-L58 -->
     <ItemGroup>
       <PatchFile Include="Patches\*.xml" />
     </ItemGroup>
   ```

   ```xml
   <!-- build/ModDeploy.targets L93-L98 -->
       <Copy SourceFiles="$(MSBuildProjectDirectory)\mod.toml" DestinationFolder="$(ModDeployDir)" />
       <MakeDir Directories="$(ModDeployDir)Patches\" Condition="'@(PatchFile)' != ''" />
       <Copy SourceFiles="@(PatchFile)" DestinationFolder="$(ModDeployDir)Patches\" Condition="'@(PatchFile)' != ''" />
       <Copy SourceFiles="@(ModPayloadFile)"
             DestinationFiles="@(ModPayloadFile->'$(ModDeployDir)%(TargetPath)')"
             Condition="'@(ModPayloadFile)' != ''" />
       <!-- ... -->
   ```

   The Release target copies the same items into the zip:

   ```xml
   <!-- AdvancedFlightComputer/AdvancedFlightComputer.csproj L90-L93 -->
       <MakeDir Directories="$(ReleaseDir)Patches\" />
       <Copy SourceFiles="$(OutputPath)$(AssemblyName).dll" DestinationFolder="$(ReleaseDir)" />
       <Copy SourceFiles="mod.toml" DestinationFolder="$(ReleaseDir)" />
       <Copy SourceFiles="@(PatchFile)" DestinationFolder="$(ReleaseDir)Patches\" />
   ```

   `mod.toml` itself is copied the same way (`None Update="mod.toml"` with `CopyToOutputDirectory`).

5. **Tell players the dependency is optional and what they lose without it.** AFC's README says
   KittenExtensions is only required for hyperbolic targets and the AUTOSTAGE gauge button.

## Complete example

- [AdvancedFlightComputer/mod.toml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/mod.toml)
- [AdvancedFlightComputer/Patches/AutoStageButton.xml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Patches/AutoStageButton.xml)
- [AdvancedFlightComputer/Patches/HyperbolicBodies.xml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Patches/HyperbolicBodies.xml)
- [AdvancedFlightComputer/AdvancedFlightComputer.csproj](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/AdvancedFlightComputer.csproj) and
  [build/ModDeploy.targets](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/build/ModDeploy.targets) for the packaging
- [AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Features/HyperbolicTargets/TransferPlannerPatches.cs)
  for the code that tolerates the absent patch

## Variations and gotchas

- **Optional means unordered.** An optional StarMap dependency only affects whether and when your
  assembly loads. KittenExtensions patches run from `ModLibrary.PrepareAll` regardless, so the patch
  and the dependency are independent: removing the dependency line would still apply patches, and
  declaring it does not make them apply.
- **Check the id players actually have.** AFC names `ModId = "KittenExtensions"`; the pinned
  KittenExtensions README documents a fork that installs as `KittenExtensionsContinued` (and says
  mods that depend on it should require it non-optionally). With an optional dependency a mismatch
  is harmless; with a required one the mod would not load.
- **Disabled mods still patch.** KX imports every manifest entry's `patches`, enabled or not. AFC's
  README tells players to delete the folders of the standalone mods it replaces rather than disable
  them, because their patches would otherwise keep running.
- **A patch can fail the whole game.** A broken patch file shows KX's error popup and exits. Keep
  patches small; test them with the stepper ([Debug XML patches with the step debugger and the session log](../../validation/debug-xml-patches/overview.md)).
- **Data and code must agree.** The patch adds a button naming `AfcAutoStageToggle`; the code
  registers that type. If the code registration is skipped but the patch applies, you get a button
  bound to nothing. AFC skips the injection with a warning in that case and logs the mismatch.
- **Game updates.** Patch paths are XPath over stock element and attribute names
  (`GaugeCanvas[@Id='EngineControl']`), so a game update that renames them makes the patch match
  nothing silently. Add a test or a log check that proves the data is present (AFC's `WarnOnce`).

## See also

- [Patch Core or another mod's XML with a patches array in mod.toml](patch-game-xml-data-1.md) (the mechanism), [Write XPath patch operations for game XML](../xpath-xml-patch-recipes/overview.md), [Debug XML patches with the step debugger and the session log](../../validation/debug-xml-patches/overview.md),
  [Understand when and in which order KSA loads mod XML](../../asset-xml/mod-xml-load-order/overview.md)
- [A custom gauge button action backed by a mod enum](../../../../mods/ksa-advancedflightcomputer/features/autostage-gauge-button.md),
  [Transfer Planner targets on hyperbolic orbits](../../../../mods/ksa-advancedflightcomputer/features/hyperbolic-transfer-targets.md),
  [Combined GameData XML document](../../../../mods/kittenextensions/features/gamedata-document.md)
