---
type: Mod Feature
title: Turn pasted Part XML into a real, enabled KSA mod folder from inside the game
description: Parts Now validates a mod id, writes mod.toml plus up to three XML bundles atomically (UTF-8 without BOM, LF) under the game's own mods folder, adds an enabled manifest entry, and classifies existing mod folders for loading
mod: unscience
submod: parts-now
category: assets
techniques: [toml-config, xml-asset, imgui-window]
hooks:
  - starmap StarMapAfterGui (via unscience ISubmod.RenderContent, paste form, Validate and Install buttons)
  - starmap StarMapBeforeGui (via unscience ISubmod.Update, WriteModFolder state and manifest stamp)
lifecycle: [frame:draw-ui]
game_types: [KSA.ModLibrary, KSA.ModManifest, KSA.ModEntry, KSA.Mod]
tags: [mod-folder, mod-toml, manifest, xml, file-io, atomic-write, mod-id]
---

The paste workflow lets a player turn XML from another tool, or hand-written XML, into a normal
KSA content mod without leaving the game. Nothing is loaded from memory: the documents are always
written to `<mods>/<mod-id>/` and then loaded from there. The result also loads at the next launch,
so vehicles built with those parts keep resolving them.[^folder-writer]

Mechanism in one line: validate the id against every place KSA could already know it, write the
XML files and then `mod.toml` through temp files, and on a successful load append
`new ModEntry { Id, Enabled = true, New = false }` to `ModLibrary.Manifest` and save it.

## How it works

**The form.** The paste panel has Mod Id, Display Name, Author and Version fields and three XML tabs
(Assets, Part, GameData) of 256 KiB each with paste-from-clipboard and clear buttons. Blank tabs are
skipped.[^xml-tab-editor] The mod id is re-validated only when its text changes, and the panel shows
the resolved absolute target folder under the form.[^paste-panel] **Validate** parses the tabs and
runs the [bundle rules](./parts-now-bundle-validation.md) without writing anything.
**Install & Load** is enabled only when the id is valid and validation is clean, and it calls
`RuntimeModLoader.StartInstall`.[^paste-panel-actions]

**Mod id rules** ([parts-now.lib/Io/ModIdValidator.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Io/ModIdValidator.cs)). Every rule is an error, each runs in its own
`try/catch`, and a rule that cannot run counts as a failure:

| Rule | Check |
|---|---|
| Shape | `^[a-z0-9]+(?:-[a-z0-9]+)*$`, 3 to 48 characters |
| Reserved | `Core`, `Sample`, `parts-now`, `unscience` (case-insensitive) |
| Mods folder | `<mods>/<id>` does not exist |
| Content folder | `Content/<id>` (relative to the install folder, where KSA's built-in mods live) does not exist |
| Loaded mods | `ModLibrary.Find(id)` is null |
| Manifest | `ModLibrary.Manifest.Mods` has no entry with that id; a null manifest fails closed |

The mods directory always comes from `ModLibrary.LocalModsFolderPath` (`Documents/.../mods`, built
from `Constants.DocumentsFolderPath`). It is compared with the shared `KsaPaths.UserDataDir + "mods"`
helper and a mismatch is only logged; the game's value wins.[^mod-id-validator][^ksa-modlibrary]

**Writing** ([parts-now.lib/Io/ModFolderWriter.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Io/ModFolderWriter.cs)), inside the job's `WriteModFolder` state:

```
<mods>/<mod-id>/
    mod.toml                  name / description / version / author / assets[]
    <mod-id>-assets.xml       only if the Assets tab was non-empty
    <mod-id>-part.xml
    <mod-id>-gamedata.xml
```

- Each file goes to a `.tmp` sibling with `UTF8Encoding(false)` and `\n` line endings, then
  `File.Move` into place. Leftover temp files are deleted on failure.
- XML files are written first and `mod.toml` last, so a failed write never advertises a missing file.
- An existing `mod.toml` is merged (Tomlyn): `name`, `description`, `version`, `author` are set,
  `assets` is the union of old and new entries, and every scalar key is re-emitted before any table.
  Without that reordering a parsed `[StarMap]` table written first would swallow the `assets` key.
- `Write` never throws; it returns a `ModFolderResult`.

**Failure cleanup.** If anything after `WriteModFolder` fails, `Fail()` deletes the folder this job
created. Otherwise the id would be unusable for the rest of the session, because an existing folder
is itself a validation failure. A folder that was already there is never deleted.[^loader-core]

**Manifest.** Only after the whole job succeeds does `CompleteJob` call `EnsureManifestEntry`. It
leaves an existing entry exactly as the user set it. KSA's own `ModEntry(string id, int count)`
constructor sets `Enabled = false, New = true`; at boot `PrepareManifest` uses it for orphaned
folders, so a folder without an entry would come back disabled. The object-initializer form avoids
that.[^loader-gpu-states][^ksa-mod-entry][^ksa-modlibrary]

**Folder classification for the load/reload/unload panel**
([parts-now.lib/Io/ModFolderScanner.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Io/ModFolderScanner.cs)): every directory under the mods folder with a
`mod.toml` is read with Tomlyn and classified twice.

| Kind | Rule | Loadable |
|---|---|---|
| `Content` | non-empty `assets` array | yes |
| `StarMap` | `[StarMap] EntryAssembly` and no assets | no ("StarMap-only mod") |
| `Both` | assets and an entry assembly | yes (content only) |
| `Empty` | neither | no |

| State | Test order | Effect |
|---|---|---|
| `LoadedByPartsNow` | `RuntimeModRegistry` first | Reload and Unload offered |
| `LoadedAtBoot` | then `ModLibrary.Find(id)` (an exception also lands here) | "loaded at startup — restart the game to reload" |
| `NotLoaded` | otherwise | Load offered if at least one listed asset exists |

The registry is checked first because a reload of a folder that KSA also enabled at boot reuses
KSA's own `Mod` object, so `Find` would wrongly report it as boot-loaded.[^folder-scanner] The scan
is cached and redone only on demand (first render, Rescan, selection change, job end), never per
frame.[^folder-panel]

## Lifecycle and timing

| Work | Hook | Notes |
|---|---|---|
| Form input, id validation, Validate button, scan | `RenderContent`, i.e. `[StarMapAfterGui]` (postfix on `Program.OnDrawUiViewports`) | Pure reads of `ModLibrary` and the file system; event-driven, not per frame |
| `StartInstall` | Same button handler | Only checks preconditions and creates the job |
| `WriteModFolder`, `CreateMod`, the rest of the job | `Update(dt)`, i.e. `[StarMapBeforeGui]` (prefix on `OnDrawUiFrame`) | One state per frame on the game thread |
| `EnsureManifestEntry` | Final job state in `Update(dt)` | `ModManifest.Save()` writes `manifest.toml` synchronously on the game thread |

Disk writes happen on the main thread inside one frame. They are a few small files, so the hitch is
bounded. Nothing here touches simulated state, so the position relative to the physics solvers does
not matter.

## Key code

| Path | Role |
|---|---|
| [parts-now.lib/Io/ModFolderWriter.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Io/ModFolderWriter.cs) | Folder write, `mod.toml` merge, atomic writes, manifest entry |
| [parts-now.lib/Io/ModIdValidator.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Io/ModIdValidator.cs) | Id rules, mods-folder discovery, target path |
| [parts-now.lib/Io/ModFolderScanner.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Io/ModFolderScanner.cs) | Read-only survey: kind, state, loadability |
| [parts-now.lib/Runtime/RuntimeModLoaderStates.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModLoaderStates.cs#L101-L132) | `WriteModFolder` state[^loader-states] |
| [parts-now.lib/Runtime/RuntimeModLoader.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModLoader.cs#L419-L442) | Deletes the folder a failed install wrote |
| [parts-now.lib/Runtime/RuntimeModLoaderApi.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModLoaderApi.cs#L30-L76) | `StartInstall` re-validates the id before starting[^loader-api] |
| [parts-now.lib/Ui/PastePanel.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Ui/PastePanel.cs) / [parts-now.lib/Ui/XmlTabEditor.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Ui/XmlTabEditor.cs) | Form, tabs, buttons |

## Snippets

XML first, `mod.toml` last:

```csharp
// parts-now.lib/Io/ModFolderWriter.cs L95-L114
        try
        {
            Directory.CreateDirectory(modDir);

            var assetNames = new List<string>(assetFiles.Count);
            foreach (PlannedFile file in assetFiles)
            {
                WriteAtomic(Path.Combine(modDir, file.FileName), file.Content, temporaryFiles);
                assetNames.Add(file.FileName);
                written.Add(file.FileName);
            }

            // mod.toml goes last so a failed asset write never advertises a file that is not there.
            string toml = BuildModToml(modDir, request, assetNames);
            WriteAtomic(Path.Combine(modDir, ModLibrary.MOD_TOML), toml, temporaryFiles);
            written.Insert(0, ModLibrary.MOD_TOML);

            Console.WriteLine($"parts-now: wrote {written.Count} file(s) to {modDir}");
            return new ModFolderResult(true, modDir, written, null);
        }
```

The atomic write:

```csharp
// parts-now.lib/Io/ModFolderWriter.cs L329-L342
    private static void WriteAtomic(string destination, string content, List<string> temporaryFiles)
    {
        string temp = destination + ".tmp";
        temporaryFiles.Add(temp);

        File.WriteAllText(temp, NormaliseLineEndings(content), Utf8NoBom);

        // overwrite:false is what the plan asks for and is what a fresh mod folder always hits; a
        // reused folder (or a merged mod.toml) has the destination already present, and there
        // overwrite:true is the only way the move can succeed.
        File.Move(temp, destination, overwrite: File.Exists(destination));

        temporaryFiles.Remove(temp);
    }
```

An enabled, non-new manifest entry:

```csharp
// parts-now.lib/Io/ModFolderWriter.cs L154-L174
            foreach (ModEntry existing in manifest.Mods)
            {
                if (existing == null || !string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!existing.Enabled)
                {
                    Console.WriteLine(
                        $"parts-now: '{id}' is already in the mod manifest but disabled — leaving it "
                        + "alone; enable it in the mods menu to load it at the next launch.");
                }

                return true;
            }

            manifest.Mods.Add(new ModEntry { Id = id, Enabled = true, New = false });
            manifest.Save();
            Console.WriteLine($"parts-now: added '{id}' to {ModLibrary.LocalManifestPath}");
            return true;
```

The constructor it avoids (game code):

```csharp
// current/decomp/KSA/ModEntry.cs L40-L47
	public ModEntry(string id, int modsCount)
	{
		Id = id;
		Enabled = false;
		Core = false;
		Index = modsCount;
		New = true;
	}
```

## Game APIs touched

| Member | Decomp | Use |
|---|---|---|
| `ModLibrary.LocalModsFolderPath`, `LocalManifestPath`, `MOD_TOML`, `CONTENT_FOLDER` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L148-L180) | Where to write, the manifest file, file names |
| `ModLibrary.Manifest`, `ModLibrary.Find` | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L160-L190) | Id collision checks; boot-loaded test |
| `ModLibrary.PrepareManifest` (orphan folders) | [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L234-L372) | Adds unknown folders as `new ModEntry(name, count)` (disabled, new) at boot |
| `ModEntry` fields and constructors | [current/decomp/KSA/ModEntry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModEntry.cs#L6-L55) | `Enabled` is TOML-serialized, `New` is not |
| `ModManifest.Save` | [current/decomp/KSA/ModManifest.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModManifest.cs#L27) | Persists the new entry[^ksa-mod-manifest] |

## Pitfalls and version notes

- The new folder contains only XML. `StartInstall` refuses an id whose folder exists, so nothing can
  be placed there beforehand. A pasted bundle that declares `<MeshAtlas Path>` or `<Texture Path>`
  would fail at `RunLoaders` (file missing) and be rolled back. In practice pasted parts can only
  reference meshes, textures and materials that are already registered (inferred from the code path).
  To ship new GLB/KTX2 files, create the folder by hand and use the folder Load flow.
- The `mod.toml` merge and `overwrite: true` only matter for a folder that already exists. The
  install path validates that it does not, so they are defensive.
- `EnsureManifestEntry` runs only on success. A paste that installs but later fails is rolled back
  and its folder deleted, so no stale entry remains.
- `ModEntry.New` is `[TomlNonSerialized]`, so it never reaches `manifest.toml`; what matters on disk
  is `enabled = true`.[^ksa-mod-entry]
- Removing a pasted mod folder by hand breaks any saved vehicle that uses its parts.

## See also

- [Runtime part loading state machine](./parts-now-runtime-part-loading.md)
- [Bundle validation rules V1-V15](./parts-now-bundle-validation.md)
- [Runtime unload and purge](./parts-now-runtime-unload-purge.md)
- [Parts Now sub-mod](../submods/parts-now.md)
- [Mod overview](../overview.md)
- [Write a new content mod folder and manifest entry from inside the game](../../../howto/assets/asset-xml/ship-and-share-data-files-between-mods/ship-and-share-data-files-between-mods-4.md) - how-to article demonstrating this feature
- [Load a part mod into a running game, one loader state per frame](../../../howto/vehicles-and-parts/parts/load-and-unload-part-mods-at-runtime/load-and-unload-part-mods-at-runtime-1.md) - how-to article demonstrating this feature

[^folder-writer]: parts-now.lib/Io/ModFolderWriter.cs
[^xml-tab-editor]: parts-now.lib/Ui/XmlTabEditor.cs
[^paste-panel]: parts-now.lib/Ui/PastePanel.cs
[^paste-panel-actions]: parts-now.lib/Ui/PastePanelActions.cs
[^mod-id-validator]: parts-now.lib/Io/ModIdValidator.cs
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^loader-core]: parts-now.lib/Runtime/RuntimeModLoader.cs
[^loader-gpu-states]: parts-now.lib/Runtime/RuntimeModLoaderGpuStates.cs
[^ksa-mod-entry]: KSA.ModEntry (decompiled, build 2026.10.10.5554)
[^folder-scanner]: parts-now.lib/Io/ModFolderScanner.cs
[^folder-panel]: parts-now.lib/Ui/ModFolderPanel.cs
[^loader-states]: parts-now.lib/Runtime/RuntimeModLoaderStates.cs
[^loader-api]: parts-now.lib/Runtime/RuntimeModLoaderApi.cs
[^ksa-mod-manifest]: KSA.ModManifest (decompiled, build 2026.10.10.5554)
