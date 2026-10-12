---
type: How-To
title: Patch Core or another mod's XML with a patches array in mod.toml
description: List patch files under patches in mod.toml and KittenExtensions applies them to one in-memory document of every manifest mod's XML before the game deserializes anything
category: assets
group: xml-patching
topic: patch-game-xml-data
approach: 1
difficulty: intermediate
prerequisites: [starmap, kittenextensions]
demonstrated_by:
  - mods/kittenextensions/features/gamedata-document
  - mods/kittenextensions/features/xpath-patch-operations
techniques: [xml-patch, harmony-prefix, toml-config]
hooks: [harmony-prefix KSA.ModLibrary.PrepareAll]
lifecycle: [startup:asset-load]
game_types: [KSA.ModLibrary, KSA.Mod, KSA.ModEntry, KSA.ModManifest, KSA.XmlLoader, KSA.AssetBundle]
tags: [xml-patch, mod-toml, kittenextensions, gamedata, patches, startup]
---

You can change a stock part, a gauge canvas, a celestial body or another mod's assets without shipping a
copy of the file. Add a `patches` list to your `mod.toml`, write a small `<Patch>` XML file, and
[KittenExtensions](https://github.com/AMPW-german/KittenExtensions) (KX) applies it to an in-memory
copy of every mod's XML before the game reads any of it. This article covers the plumbing: what KX
does with the `patches` key, the shape of the document your patch sees, the order patches run in, and
what a restart-only, load-time transform can and cannot do. The operations themselves (Copy, Merge,
Delete, If, With, SetVar) are in [Write XPath patch operations for game XML](../xpath-xml-patch-recipes/overview.md).

Demonstrated by KittenExtensions ([Combined GameData XML document](../../../../mods/kittenextensions/features/gamedata-document.md)) and consumed by
KSA-AdvancedFlightComputer (the sibling article [Ship XML patches that apply only when KittenExtensions is installed](patch-game-xml-data-2.md)).

Prerequisites: StarMap and KittenExtensions installed in the player's game, and a StarMap mod folder
with a `mod.toml` (see [Declare a dependency on another StarMap mod, required or optional](../../../getting-started/dependencies/declare-a-mod-dependency/overview.md)).

## Background: how the game does it

The game has no patch mechanism of its own. `Mod` reads a fixed set of `mod.toml` keys through
`[TomlField]` attributes (`name`, `planetMeshes`, `planetRandomHeightmapCollections`, `systems`,
`assets`, `starBinaries`, `fonts`, `console`, and a few quality tables); `patches` is not among them,
so the game ignores it.

```csharp
// current/decomp/KSA/Mod.cs L56-L72
	[TomlField("planetMeshes")]
	private string[] PlanetMeshCollections = Array.Empty<string>();

	[TomlField("planetRandomHeightmapCollections")]
	private string[] PlanetRandomHeightmapCollections = Array.Empty<string>();

	[TomlField("systems")]
	public string[] SystemTemplates = Array.Empty<string>();

	[TomlField("assets")]
	private string[] Assets = Array.Empty<string>();

	[TomlField("starBinaries")]
	private string[] StarBinaries = Array.Empty<string>();

	[TomlField("fonts")]
	private string[] Fonts = Array.Empty<string>();
```

`ModLibrary.PrepareAll` then walks `ModLibrary.Manifest.Mods`, skips disabled entries, finds each mod
folder (`Content/<id>` first, then `<Documents>/My Games/Kitten Space Agency/mods/<id>`), creates a
`Mod` for it and calls `Mod.PrepareSystems`; the XML named by `assets`, `systems` and `planetMeshes`
is deserialized later by `XmlLoader.Load<T>` calls inside the `Mod` loader methods
([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L447)). See [Understand when and in which order KSA loads mod XML](../../asset-xml/mod-xml-load-order/overview.md) for the full sequence.

KX plugs into exactly that gap. It reads every manifest mod's `mod.toml` itself (with its own Tomlet
class that includes `patches`), builds one XML document, runs the patches, and arranges for the game's
loaders to deserialize from the patched document instead of from disk. The redirect half is described
in [Redirect the game to patched copies of its XML and coordinate edits between mods](../redirect-game-xml-loading/overview.md).

## Lifecycle and timing

Stage: `startup:asset-load`. KX registers a Harmony prefix on `ModLibrary.PrepareAll`:

```csharp
// KittenExtensions/Patches.cs L21-L28
  [HarmonyPatch(typeof(ModLibrary), nameof(ModLibrary.PrepareAll)), HarmonyPrefix]
  internal static void ModLibrary_PrepareAll_Prefix()
  {
    // PrepareManifest is called by StarMap, so we shouldn't need to do this here, but try anyways
    if (!ModLibrary.PrepareManifest())
      return;
    XmlPatcher.OnPrepare();
  }
```

`Program`'s constructor calls `PrepareAll` after the new-mods confirmation dialog and before
`PreloadAssetBundles`, `LoadEditorTags` and `LoadAll` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899-L942)), so the patch run is
finished before any `Mod` object, asset, system or universe exists. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for the whole sequence.

- Patches are pure data transforms on XML. They cannot see run-time state (vehicles, settings, other
  assemblies' fields) and cannot react to anything after startup.
- They run on the main thread and block startup; a failing patch shows an error popup and exits the
  process.
- A change to a patch file needs a game restart.
- Code that must exist when the patched data is bound (for example an enum value an XML attribute
  names) has to be registered from `StarMapImmediateLoad` or earlier; see
  [Choose and use the right StarMap lifecycle hook](../../../getting-started/lifecycle/choose-a-starmap-startup-hook/overview.md).

## Steps

1. **Create the patch file.** Put it in your mod folder, for example `Patches/MyPatch.xml`. The root
   element is `<Patch>` and its children are operations:

   ```xml
   <!-- README.md L47-L50 -->
   <!-- MyMod/MyPatch.xml -->
   <Patch>
     <!-- patches -->
   </Patch>
   ```

2. **List it in `mod.toml`.** Use a path relative to the mod folder, written exactly as you want the
   `RelPath` to read. This is the whole of AdvancedFlightComputer's manifest, with `patches` at top
   level beside `name`:

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

   KX's `ModToml` knows only these keys, which is why a mod can put `patches` in a manifest the game
   happily accepts:

   ```csharp
   // KittenExtensions/Patch/Patcher.cs L179-L191
       private class ModToml
       {
           [TomlField("name")]
           public string Name = "";
           [TomlField("planetMeshes")]
           public string[] PlanetMeshCollections = [];
           [TomlField("systems")]
           public string[] SystemTemplates = [];
           [TomlField("assets")]
           public string[] Assets = [];
           [TomlField("patches")]
           public string[] Patches = [];
       }
   ```

3. **Know the document your operations run against.** KX creates `<Root>`, then one `<Mod Id="...">`
   per manifest entry in manifest order, then imports each listed file in the order `planetMeshes`,
   `systems`, `assets`, `patches`, adding a `RelPath` attribute to each file's root element:

   ```csharp
   // KittenExtensions/Patch/Patcher.cs L133-L163
       private static void LoadModData(ModEntry modEntry)
       {
           var tomlPath = Path.Combine("Content", modEntry.Id, "mod.toml");
           if (!File.Exists(tomlPath))
           {
               tomlPath = Path.Combine(ModLibrary.LocalModsFolderPath, modEntry.Id, "mod.toml");
           }
           var dirPath = Filepath.CorrectSeparators(Path.GetFullPath(Path.GetDirectoryName(tomlPath) ?? ""));
           ModToml mod;
           try
           {
               mod = TomletMain.To<ModToml>(File.ReadAllText(tomlPath));
           }
           catch (Exception ex)
           {
               throw ErrorAndExit(ex.ToString(), strLoc: Path.GetFullPath(tomlPath));
           }

           var patchMod = Domain.AddMod(modEntry.Id);
           patchMod.Node.SetAttribute("Name", mod.Name);
           patchMod.Node.SetAttribute("Path", PatchDomain.NormalizePath(dirPath));

           foreach (var path in mod.PlanetMeshCollections)
               LoadModFile(patchMod, dirPath, path);
           foreach (var path in mod.SystemTemplates)
               LoadModFile(patchMod, dirPath, path);
           foreach (var path in mod.Assets)
               LoadModFile(patchMod, dirPath, path);
           foreach (var path in mod.Patches)
               LoadModFile(patchMod, dirPath, path);
       }
   ```

   The README's picture of the result (the root element of a file keeps its own name, for example
   `<Assets>` or `<System>`, and gains `RelPath`):

   ```xml
   <!-- README.md L56-L73 -->
   <Root>
     <!-- each enabled mod in same order as manifest -->
     <Mod Id="Core">
       <!-- each asset/system/meshcollection/patch file loaded in and RelPath attribute added -->
       <System Id="Sol" RelPath="SolSystem.xml">
         <!-- rest of file contents -->
       </System>
       <Assets RelPath="Astronomicals.xml">
         <!-- ... -->
       </Assets>
     </Mod>
     <Mod Id="MyMod">
       <!-- ... -->
       <Patch RelPath="MyPatch.xml">
         <!-- ... -->
       </Patch>
     </Mod>
   </Root>
   ```

   Every operation starts with `<Root>` as its context node, so a path like `//GaugeCanvas[@Id='X']`
   matches in every mod's imported XML, and so would also match XML inside another mod's pending
   `<Patch>`. Scope with `Mod[@Id='Core']/...` whenever the target must be a specific mod's file.

4. **Write the operation.** For example AdvancedFlightComputer's patch appends missing elements to
   comets (more recipes in [Write XPath patch operations for game XML](../xpath-xml-patch-recipes/overview.md)):

   ```xml
   <!-- AdvancedFlightComputer/Patches/HyperbolicBodies.xml L7-L19 -->
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
   ```

5. **Declare KittenExtensions.** Patches only run if KX is installed, enabled and loaded. A mod that
   requires them lists KX as a (non-optional) StarMap dependency; a mod that can live without them
   marks it optional (the next article, [Ship XML patches that apply only when KittenExtensions is installed](patch-game-xml-data-2.md)).

6. **Deploy the patch file beside `mod.toml`.** KX resolves each entry against the mod folder
   (`Path.Combine(dirPath, path)`), so `Patches/MyPatch.xml` must exist at that relative path in the
   installed folder, not just in your source tree.

7. **Run the game and check.** On success nothing is printed apart from the KX session log
   (`KittenExtensions.log`, see [Debug XML patches with the step debugger and the session log](../../validation/debug-xml-patches/overview.md)). Neither the README nor the KittenExtensions code
   says a path that selects nothing is reported (the executor source is not checked out), so assume it is silent;
   use the step debugger or the log to find out.

## Complete example

- [KittenExtensions/Patches.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patches.cs) is the hook; the
  patch executor lives in the XPathPatch library, whose source is not in the pinned checkout (the
  `XPathPatch` folder is an empty submodule), so operation semantics below come from the README.
- Patch loader and document builder: [KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L56-L110).
- A real consumer: [AdvancedFlightComputer/mod.toml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/mod.toml#L1-L9),
  [AdvancedFlightComputer/Patches/HyperbolicBodies.xml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Patches/HyperbolicBodies.xml) and
  [AdvancedFlightComputer/Patches/AutoStageButton.xml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/Patches/AutoStageButton.xml).

## Variations and gotchas

- **Disabled mods still patch.** `LoadData` loops over `ModLibrary.Manifest.Mods` without an `Enabled`
  check. A mod that is installed but disabled still has its patch files imported and run. Tell
  players to remove a mod folder, not just disable it.
- **Order is manifest order only.** StarMap's dependency graph does not reorder patches. A mod later
  in `manifest.toml` runs later and wins conflicts. Within one mod, files run in `patches` order.
- **Patch bodies are read when they run.** The list of `<Mod>` and `<Patch>` nodes is captured up
  front, but an earlier patch can rewrite the content of a later patch file (README, Patch
  Execution).
- **Files you generate at run time are not seen.** The document is built once, so a mod that writes
  or rewrites one of its listed XML files in `StarMapImmediateLoad` or later does not influence what
  the redirected loaders read. Generate such files in `StarMapBeforeMain`.
- **Strictness.** With KX installed, an exception while parsing any manifest mod's `mod.toml` or
  importing any listed XML file shows the error popup and exits; the stock `XmlLoader` would log and
  carry on ([current/decomp/KSA/XmlLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlLoader.cs#L1-L52)).
- **Not every file is patchable.** `planetRandomHeightmapCollections`, `fonts` and `starBinaries`
  are not imported into the document, and `Mod.LoadPlanetRandomHeightmaps` still reads its files
  from disk. Editor tag definitions are the subtle case: the `assets` files are in the document, but
  `ModLibrary.LoadEditorTags` (`Mod.LoadEditorTagDefinitions`) is not redirected and reads them from
  disk, so a patch that changes or adds an `<EditorTagDef>` never takes effect (the unpatched pass registers the tag first and
  `SerializedCollection.Register` keeps the first entry) ([XmlLoader call-site redirect](../../../../mods/kittenextensions/features/xmlloader-call-site-redirect.md)).
- **Fork identity.** The README documents the Continued fork (mod id `KittenExtensionsContinued`,
  assembly name still `KittenExtensions`); StarMap dependency ids must match the manifest id of the
  KX build your players use.
- **Unverified here:** the README's second example uses `<Remove Path=... />` while the operation
  list documents `<Delete>`. The executor source is not checked out, so test the name you use.

## See also

- [Ship XML patches that apply only when KittenExtensions is installed](patch-game-xml-data-2.md) (optional dependency variant), [Write XPath patch operations for game XML](../xpath-xml-patch-recipes/overview.md),
  [Debug XML patches with the step debugger and the session log](../../validation/debug-xml-patches/overview.md), [Redirect the game to patched copies of its XML and coordinate edits between mods](../redirect-game-xml-loading/overview.md), [Understand when and in which order KSA loads mod XML](../../asset-xml/mod-xml-load-order/overview.md)
- [Combined GameData XML document](../../../../mods/kittenextensions/features/gamedata-document.md), [XML patch files and XPathPatch operations](../../../../mods/kittenextensions/features/xpath-patch-operations.md),
  [XmlLoader call-site redirect](../../../../mods/kittenextensions/features/xmlloader-call-site-redirect.md)
- [Declare a dependency on another StarMap mod, required or optional](../../../getting-started/dependencies/declare-a-mod-dependency/overview.md), [Add to, drive and hide the stock cockpit gauges and navball](../../../ui/menus/work-with-stock-cockpit-gauges/overview.md)
