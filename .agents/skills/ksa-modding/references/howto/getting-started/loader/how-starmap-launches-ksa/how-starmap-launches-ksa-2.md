---
type: How-To
title: Understand the mod folder, manifest and enable/ordering rules
description: Where KSA and StarMap look for mods, how manifest.toml enables and orders them, why a mod can seem ignored, and how enabling a new mod leads to StarMap's restart prompt
category: getting-started
group: loader
topic: how-starmap-launches-ksa
approach: 2
difficulty: beginner
prerequisites: [starmap]
demonstrated_by:
  - mods/starmap/features/manifest-mod-discovery
  - mods/starmap/features/new-mods-restart-flow
  - mods/ksagolf/features/content-and-code-mod-layout
techniques: [toml-config, starmap-lifecycle]
hooks: [call KSA.ModLibrary.PrepareManifest, harmony-prefix KSA.ModLibrary.PrepareAll]
lifecycle: [startup:starmap-before-main, startup:asset-load]
game_types: [KSA.ModLibrary, KSA.ModManifest, KSA.ModEntry, KSA.Mod, KSA.Constants]
tags: [manifest, mod-toml, mod-folder, enable, ordering, restart, content-mod]
---

KSA's own mod system is data-only: folders with a `mod.toml`, listed and toggled in a `manifest.toml`.
StarMap reuses exactly that registry, so a code mod is subject to every enable and ordering rule
below. This article explains the rules as StarMap consumes them and answers the common question "I
copied my mod in, why is it ignored?". KSAGolf is the demonstrating mod: one folder that is both a
KSA content mod and a StarMap code mod.[src/KSAGolf/mod.toml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/mod.toml) The full discovery walk is in
[Discover code mods through KSA's mod manifest](../../../../mods/starmap/features/manifest-mod-discovery.md).

## Background: how the game does it

**Roots.** `Constants.DocumentsFolderPath` is `<Personal>/My Games/Kitten Space Agency` (on Windows
`Documents\My Games\Kitten Space Agency`).[current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281-L287)

| Path | Defined as | Contents |
|---|---|---|
| Install mods | `<KSA install>/Content/<id>/mod.toml` | Stock `Core` and anything copied into `Content` |
| Local mods | `ModLibrary.LocalModsFolderPath` = `<Documents>/mods` | Player-installed mods, one folder per mod |
| Manifest | `ModLibrary.LocalManifestPath` = `<Documents>/manifest.toml` | `[[mods]]` entries; first run it is copied from `Content/manifest.toml` |

**Manifest format.** `ModManifest` is a list of `ModEntry` with `id` and `enabled`; `Save()` writes only
those two keys per entry:[current/decomp/KSA/ModManifest.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModManifest.cs#L27-L54)

```toml
# shape written by ModManifest.Save (current/decomp/KSA/ModManifest.cs)
[[mods]]
id = "Core"
enabled = true

[[mods]]
id = "KSAGolf"
enabled = true
```

**The id is the folder name.** `ModLibrary.AddMods` makes a `ModEntry(modDir.Name, count)` for every
subfolder that contains a `mod.toml` and is not yet listed; the constructor sets `Enabled = false` and
`New = true`.[current/decomp/KSA/ModEntry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModEntry.cs#L40-L47) `New` is not serialized, so it exists only for the session that found
the folder.[current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L234-L371) Non-core entries whose `mod.toml` no longer exists in either root are
removed and the manifest is saved. **Order of the entries is load order**: `PrepareAll`, `LoadAll`, and
StarMap's own loop all walk the list top to bottom, and later mods' assets and hooks come after earlier ones.

**What KSA reads from `mod.toml`** is the `Mod` class's `[TomlField]` members, documented in
[Write mod.toml - KSA keys and the [StarMap] table](../mod-toml-reference/overview.md). The `[StarMap]` table and keys such as `version` and `author` have no field and are
ignored by KSA.

## Lifecycle and timing

| Step | When | Where |
|---|---|---|
| StarMap `PrepareManifest`, mod discovery, `StarMapBeforeMain` | `startup:starmap-before-main`, before the game's constructor | `ModLoader.PrepareMods`, reads the manifest as on disk |
| KSA confirms new mods | Program constructor, after window, renderer and ImGui exist | `ModLibrary.HasNewMods()` then `ConfirmMods` popups, `Manifest.Save()` [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L887-L899) |
| StarMap restart check | `startup:asset-load`, as a prefix on `ModLibrary.PrepareAll` | `ModLibraryPatches.BeforePrepareAll` |
| KSA builds `Mod`s, loads assets | `ModLibrary.PrepareAll`, `LoadAll` | `startup:immediate-load` and `startup:asset-load` |

This is why a **new** mod cannot load code on the launch where it is first found: StarMap decided which
DLLs to load before KSA asked the player. Full order: [Game loop and mod lifecycle](../../lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

How to install a mod by hand and check each rule:

1. **Put the folder in a root.** Prefer `<Documents>/My Games/Kitten Space Agency/mods/<ModId>/`: it
   survives game updates. `<KSA install>/Content/<ModId>/` also works but, when both exist, **Content wins**:
   both KSA (`PrepareAll`) and StarMap (`RuntimeMod.TryCreateMod`) check `Content/<id>` first, so a stale
   copy in the install folder shadows a newer one in Documents.[StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L36-L63)
2. **Name it right.** Folder name = manifest id = default `EntryAssembly` (for a mod without a `[StarMap]`
   table, `<folder>/<folder>.dll`). Names are case-sensitive on Linux.
3. **Register it, one of two ways.**
   - Add the entry to `manifest.toml` yourself. KSAGolf's deploy script does it and keeps a backup:[tools/deploy.sh](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/tools/deploy.sh)
     `[[mods]]`, `id = "KSAGolf"`, `enabled = true`.
   - Or just start the game: the new folder appears as a `New`, disabled entry and KSA shows "A new mod
     with id 'X' has been found, do you want it enabled?" with Enable / No.[current/decomp/KSA/ConfirmModPopup.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConfirmModPopup.cs#L14-L24)
4. **Understand the first-launch trap.** StarMap's loop skips entries with `Enabled = false`
   ("Not loading mod: X because it is disabled in manifest"):[StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L52-L104)

   ```csharp
   // StarMap.Core/ModRepository/ModLoader.cs L52-L86
   private void PrepareMods()
   {
       var loadedManifest = ModLibrary.PrepareManifest();

       if (!loadedManifest) return;

       var mods = ModLibrary.Manifest.Mods;
       if (mods is null) return;

       foreach (var mod in mods)
       {
           if (!mod.Enabled)
           {
               Console.WriteLine($"StarMap - Not loading mod: {mod.Id} because it is disabled in manifest");
               continue;
           }

           if (!RuntimeMod.TryCreateMod(mod, _coreAssemblyLoadContext, out var runtimeMod))
               continue;

           ModRegistry.Add(runtimeMod);

           if (!runtimeMod.AllDependenciesLoaded(ModRegistry))
           {
               Console.WriteLine($"StarMap - Delaying load of mod: {runtimeMod.ModId} due to missing dependencies: {string.Join(", ", runtimeMod.NotLoadedModDependencies.Keys)}");
               continue;
           }

           if (!runtimeMod.InitializeMod(ModRegistry))
           {
               Console.WriteLine($"StarMap - Failed to initialize mod: {runtimeMod.ModId} from manifest");
               continue;
           }

           Console.WriteLine($"StarMap - Loaded mod: {runtimeMod.ModId} from manifest");
   ```

   A mod discovered this launch is therefore skipped by StarMap even if the player clicks Enable in
   KSA's dialog a moment later: KSA loads its *content*, StarMap loads no *code*.
5. **Accept the restart prompt.** StarMap's prefix on `ModLibrary.PrepareAll` detects "any entry both
   `New` and `Enabled`", shows a "Game requires restart" popup (Continue / Restart); Restart starts a
   fresh `StarMap.exe` with `--restarted` and exits (details in [Harmony prefix on ModLibrary.PrepareAll plus Process.Start --restarted](../../lifecycle/clean-up-on-game-exit/clean-up-on-game-exit-2.md)). In the new
   process the manifest says enabled and not new, so the code loads. The prompt appears even for content-only
   mods; Continue is right then.
6. **Order dependencies above dependents.** Manifest order drives initialization. For anything beyond
   one level of dependency, list dependencies first (see [Declare a dependency on another StarMap mod, required or optional](../../dependencies/declare-a-mod-dependency/overview.md)).

Diagnosing "my mod is ignored", cheapest first:

| Symptom in the StarMap console | Cause |
|---|---|
| "Not loading mod: X because it is disabled in manifest" | `enabled = false` (new or switched off) |
| Nothing at all for X | no `mod.toml` in either root, or `<EntryAssembly>.dll` not found (silent skip) |
| "Delaying load of mod: X due to missing dependencies" | a `ModDependencies` entry is not registered; see [Declare a dependency on another StarMap mod, required or optional](../../dependencies/declare-a-mod-dependency/overview.md) |
| "Failed to initialize mod: X" | no `[StarMapMod]` class in the entry assembly |
| Loaded but hooks never run | signature or visibility problem; see [Signature checklist against StarMap's binding rules](../../lifecycle/choose-a-starmap-startup-hook/choose-a-starmap-startup-hook-4.md) |

## Complete example

KSAGolf's single folder serves both loaders. Its `mod.toml` lists the content XML for KSA and names the DLL
for StarMap:[src/KSAGolf/mod.toml](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/mod.toml)

```toml
# src/KSAGolf/mod.toml L1-L11
# KSA reads this as a content mod, StarMap reads it as a code mod. One folder does both.

name = "KSAGolf"

# Asset definitions, listed one by one rather than globbed: a file left out of this list is never
# loaded, and nothing reports it. They sit at the mod root beside Meshes/ and Textures/, so their
# relative paths resolve the same whether read against the mod root or the XML's own directory.
assets = ["KSAGolfProps.xml", "KSAGolfSounds.xml", "KSAGolfCourse.xml"]

[StarMap]
EntryAssembly = "KSAGolf"
```

The layout and asset rules are in [Ship assets and code in one mod folder and find it at runtime](../ship-content-and-code-in-one-mod/overview.md).

## Variations and gotchas

- **`name` is not the id.** Only the folder name matters to code; `name` becomes `Mod.Name`.
- **Disabled dependencies count as missing**, and a content-only mod never enters StarMap's registry (no
  DLL), so depending on one is not possible.
- **The restart drops options.** Only `--restarted` is forwarded; a `-InstancePath` argument is lost, while
  the `STARMAP_INSTANCE_PATH` environment variable survives ([Manual Harmony prefix on Constants.DocumentsFolderPath](./how-starmap-launches-ksa-3.md)).
- **Unload does not run** on the restart path: it ends with `Environment.Exit(0)`.
- **Two install roots, one id.** If the same id is in both roots, Content wins. Delete the stale copy.
- **Pre-existing manifests are rewritten** with only `id` and `enabled` when KSA saves; do not store
  comments or extra keys in `manifest.toml`.

## See also

- [Host process, contexts and hook binding walkthrough](./how-starmap-launches-ksa-1.md), [How StarMap launches KSA and loads your mod](./overview.md)
- [Discover code mods through KSA's mod manifest](../../../../mods/starmap/features/manifest-mod-discovery.md), [Restart when new mods are enabled](../../../../mods/starmap/features/new-mods-restart-flow.md),
  [Content-plus-code mod layout](../../../../mods/ksagolf/features/content-and-code-mod-layout.md)
- [Write mod.toml - KSA keys and the [StarMap] table](../mod-toml-reference/overview.md), [Ship assets and code in one mod folder and find it at runtime](../ship-content-and-code-in-one-mod/overview.md), [Declare a dependency on another StarMap mod, required or optional](../../dependencies/declare-a-mod-dependency/overview.md)
