---
type: Mod Feature
title: Combined GameData XML document
description: Before the game reads any mod XML, KittenExtensions parses every manifest mod's mod.toml, imports its planet mesh, system, asset and patch files into one in-memory Root/Mod document with a RelPath on each file root, and runs every Patch in manifest order
mod: kittenextensions
category: assets
techniques: [xml-patch, harmony-prefix, toml-config]
hooks: [harmony-prefix KSA.ModLibrary.PrepareAll]
lifecycle: [startup:asset-load]
game_types: [KSA.ModLibrary, KSA.ModEntry, KSA.ModManifest, KSA.Mod, KSA.Program]
tags: [xml, xml-patch, gamedata, mod-toml, manifest, tomlet, startup]
---

XML patches in KittenExtensions do not edit files on disk. At startup the mod reads the `mod.toml` of
every mod in the player's manifest, loads the XML files those manifests list into one in-memory
document, and runs all patch operations against that document. The game's loaders later deserialize
from the patched document instead of from disk (see
[XmlLoader call-site redirect](xmlloader-call-site-redirect.md)). The player gets mods that change
Core parts, gauges, celestials or other mods' data without shipping copies of those
files.[^kx-readme] [^kx-patcher]

The document is the shared namespace every patch sees, so its shape is the contract for patch
authors: a `<Root>`, one `<Mod Id="...">` per manifest entry in manifest order, and each imported
file's root element carrying a `RelPath` attribute with the path exactly as written in that mod's
`mod.toml`.

## How it works

1. **Hook.** A Harmony prefix on `ModLibrary.PrepareAll` calls `ModLibrary.PrepareManifest()` (a
   no-op after the first successful call) and then `XmlPatcher.OnPrepare()`
   ([KittenExtensions/Patches.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patches.cs#L21-L28)).[^kx-patches]
2. **Walk the manifest.** `LoadData` iterates `ModLibrary.Manifest.Mods` in list order, calls
   `LoadModData` for each entry, then creates the XPathPatch executor and the
   [patch session log](patch-session-log.md) ([KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L104-L110)).
   There is no `Enabled` check, so disabled mods are imported too.
3. **Find and parse `mod.toml`.** `LoadModData` looks for `Content/<id>/mod.toml` first and falls
   back to `<Documents>/My Games/Kitten Space Agency/mods/<id>/mod.toml`, the same order the game's
   own `ModLibrary.PrepareAll` uses
   ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L389-L428)).[^ksa-modlibrary] It parses the
   file with Tomlet into a private `ModToml` that knows only `name`, `planetMeshes`, `systems`,
   `assets` and `patches` ([KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L179-L191)). A parse
   error stops the game through the [fatal error popup](pre-game-popup-loop.md).
4. **Create the `<Mod>` node.** `Domain.AddMod(modEntry.Id)` adds the element; KittenExtensions then
   sets `Name` (the `name` from `mod.toml`) and `Path` (the normalized absolute mod directory) on it.
5. **Import files.** Each listed file is imported with `mod.ImportFile(fullPath)` and its root element
   gets `RelPath`, in this order: `planetMeshes`, `systems`, `assets`, `patches`
   ([KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L151-L177)). An exception while importing any file
   also stops the game.
6. **Run the patches.** Without the debug flag, `RunPatches` calls `Executor.StepToEnd()` and, if the
   executor reports an error, logs, shows the error popup and exits
   ([KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L56-L102)). With the flag, the
   [patch debug stepper](patch-debug-stepper.md) runs instead.
7. **Execution rules** (from the README): every `<Patch>` file under every `<Mod>` runs in document
   order; the list of `<Mod>` and `<Patch>` nodes is captured before the first patch runs, so
   reordering or deleting those nodes changes nothing, but a patch's contents are read only when it
   runs, so an earlier patch can rewrite a later one; each operation starts with `<Root>` as its
   context node ([README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md#L87-L88)).

The README's picture of the document:

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

### Which `mod.toml` keys land in the document

| `mod.toml` key | Game reader in build 2026.10.10.5554 | In the GameData document |
|---|---|---|
| `planetMeshes` | `Mod.LoadPlanetMeshes` (`PlanetMeshCollection`) | yes |
| `systems` | `Mod.PrepareSystems` (`SystemInfo`), `Mod.LoadSystems` (`SystemTemplate`) | yes |
| `assets` | `Mod.LoadAssetBundles` (`AssetBundle`), `Mod.LoadEditorTagDefinitions` | yes |
| `patches` | not read by the game; the game's `Mod` class has no such key | yes, as `<Patch RelPath="...">` |
| `planetRandomHeightmapCollections` | `Mod.LoadPlanetRandomHeightmaps` (`RandomHeightmapCollection`) | no |
| `starBinaries`, `fonts` | binary star data and `.ttf` files | no (not XML) |

The game-side keys are the `TomlField` attributes on `KSA.Mod`
([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L56-L72)); Core uses all of them in its own manifest
([current/Content/Core/mod.toml](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/Content/Core/mod.toml#L14-L24)).[^ksa-mod] [^ksa-core-modtoml]

## Lifecycle and timing

- **Where:** `startup:asset-load`, at the very start of `ModLibrary.PrepareAll`. The `Program`
  constructor calls `PrepareAll` right after the game's new-mod confirmation dialog
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L886-L899)).[^ksa-program]
- **Before everything else that reads mod XML:** the prefix finishes before `PrepareAll`'s per-mod
  loop, so before `Mod.MakeUsing`, before every `StarMapImmediateLoad` (StarMap's prefix on
  `Mod.PrepareSystems`, [StarMap.Core/Patches/ModPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModPatches.cs#L11-L22)) and before
  `PreloadAssetBundles`, `LoadEditorTags` and `LoadAll`
  ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L899-L942)). `StarMapAllModsLoaded` (postfix on `LoadAll`)
  and `ModLibrary.AttachGameData` ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L985)) come much
  later.[^starmap-modpatches]
- **What exists:** `ModLibrary.Manifest` is final for this session (new mods have been confirmed),
  the window, renderer and ImGui exist (the constructor has already drawn its loading screen and, when new mods were found, the confirmation dialog), but no
  `Mod` object, asset, system or universe has been created.
- **Why here:** it is the last moment before the first XML deserialization and the first moment the
  manifest is settled. Patches therefore cannot react to anything computed at run time; they are pure
  data transforms.
- **Thread:** main thread, synchronous; startup waits for the whole patch run.
- **Neighbours on the same method:** StarMap also prefixes `PrepareAll` with its restart prompt for
  newly enabled mods ([StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L30-L76)). Neither patch
  sets a Harmony priority, so Harmony runs them in registration order. KittenExtensions registers its
  prefix in its `[StarMapBeforeMain]`, inside `ModLoader.PrepareMods`, and `StarMapCore.Init` applies
  StarMap's own patches only after that ([StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L25-L29)).
  The patch run (and, in debug mode, the stepper) therefore happens before the restart prompt; a
  player who then chooses to restart has waited through one patch run for nothing.[^starmap-modlibrarypatches] [^starmap-starmapcore]

## Key code

| Path | Role |
|---|---|
| [KittenExtensions/Patches.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patches.cs#L21-L28) | `ModLibrary.PrepareAll` prefix that starts everything |
| [KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L56-L83) | `OnPrepare`: build, then run patches or open the debugger |
| [KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L104-L110) | `LoadData`: every manifest entry, executor, logger |
| [KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L133-L177) | `LoadModData` and `LoadModFile`: `mod.toml` lookup, `<Mod>` node, `RelPath` |
| [KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L179-L191) | The keys KittenExtensions reads from `mod.toml` |

## Snippets

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

## Game APIs touched

| Member | What it is for |
|---|---|
| `ModLibrary.PrepareAll` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L373-L447)) | Patched with a prefix; the patch run happens before its per-mod loop |
| `ModLibrary.PrepareManifest` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L234-L326)) | Loads and reconciles `manifest.toml`; cached after the first call |
| `ModLibrary.Manifest`, `ModEntry.Id` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L160)) | Source of the mod list and its order |
| `ModLibrary.LocalModsFolderPath` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L178)) | Fallback folder for `mod.toml` |
| `Mod` `TomlField` keys ([current/decomp/KSA/Mod.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Mod.cs#L56-L72)) | The game's own manifest keys, compared above |

## Pitfalls and version notes

- **Disabled mods still patch.** The manifest walk does not check `ModEntry.Enabled`
  ([KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L106-L107)), so an installed but disabled mod's
  files, including its `patches` files, are imported into the document. The `<Mod>` node carries only
  `Id`, `Name` and `Path`, so the executor has nothing to tell enabled from disabled mods by, and the
  README says every `<Patch>` under every `<Mod>` runs (the executor source is in the XPathPatch
  submodule, which is not checked out). The README's own picture promises "each enabled mod"
  ([README.md](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/README.md#L57)); the code at this ref imports all of them. Remove a mod's folder
  to stop its patches: `PrepareManifest` then drops the entry.
- **Files written after the import are not seen.** The document is built once, before the
  per-mod loop, so a mod that writes or rewrites one of its listed XML files in
  `StarMapImmediateLoad` (or later) does not affect what the redirected loaders read. Generate such
  files earlier, for example in `[StarMapBeforeMain]`.
- **Order is manifest order only.** StarMap's dependency graph does not reorder patches. A mod later
  in `manifest.toml` runs later and wins conflicts.
- **Stricter than stock.** The stock `XmlLoader` logs a bad file and returns `null`
  ([current/decomp/KSA/XmlLoader.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/XmlLoader.cs#L20-L52)); with KittenExtensions installed any exception
  while parsing a `mod.toml` or importing a listed XML file, in any manifest mod, shows the error
  popup and exits the process.[^ksa-xmlloader]
- **Extra attributes.** Besides the README's `Id`, each `<Mod>` carries `Name` and `Path`; `Path` is
  an absolute, machine-specific directory, so do not match on it.
- **Random heightmap collections are invisible** to patches in build 2026.10.10.5554: KittenExtensions
  does not read `planetRandomHeightmapCollections`.
- **Everything is in one document.** Core alone contributes six system files and 63 asset
  files, and every mod's patch files are in the document too, so an unscoped `//Element` path can
  match payload inside another mod's not-yet-run `<Copy>`. Scope paths with `Mod[@Id='...']`.
- KSA-AdvancedFlightComputer is a real consumer: it lists two patch files in its `mod.toml`
  ([AdvancedFlightComputer/mod.toml](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer/blob/77c0ccf5608e49c459eec72c3a56d0b30d62da63/AdvancedFlightComputer/mod.toml#L1-L9)).[^afc-modtoml]

## See also

- [XML patch files and XPathPatch operations](xpath-patch-operations.md)
- [XmlLoader call-site redirect](xmlloader-call-site-redirect.md)
- [Patch debug stepper](patch-debug-stepper.md)
- [Patch session log](patch-session-log.md)
- [Pre-game ImGui popup loop and fatal patch errors](pre-game-popup-loop.md)
- [StarMap startup lifecycle hooks](../../starmap/features/startup-lifecycle-hooks.md)
- [KSA-AdvancedFlightComputer optional KittenExtensions patches](../../ksa-advancedflightcomputer/features/optional-kittenextensions-patches.md)
- [Game loop and lifecycle](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Find out when KSA reads each mod.toml key and where hooks can see the data](../../../howto/assets/asset-xml/mod-xml-load-order/mod-xml-load-order.md) - how-to article demonstrating this feature
- [Patch Core or another mod's XML with a patches array in mod.toml](../../../howto/assets/xml-patching/patch-game-xml-data/patch-game-xml-data-1.md) - how-to article demonstrating this feature

[^kx-readme]: KittenExtensions README.md
[^kx-patcher]: KittenExtensions/Patch/Patcher.cs
[^kx-patches]: KittenExtensions/Patches.cs
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-mod]: KSA.Mod (decompiled, build 2026.10.10.5554)
[^ksa-core-modtoml]: Core mod.toml (build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^starmap-modpatches]: StarMap.Core/Patches/ModPatches.cs
[^starmap-modlibrarypatches]: StarMap.Core/Patches/ModLibraryPatches.cs
[^starmap-starmapcore]: StarMap.Core/StarMapCore.cs
[^ksa-xmlloader]: KSA.XmlLoader (decompiled, build 2026.10.10.5554)
[^afc-modtoml]: AdvancedFlightComputer/mod.toml
