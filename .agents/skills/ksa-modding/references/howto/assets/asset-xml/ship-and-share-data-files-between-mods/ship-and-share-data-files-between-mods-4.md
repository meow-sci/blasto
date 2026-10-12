---
type: How-To
title: Write a new content mod folder and manifest entry from inside the game
description: Validate a mod id against every place KSA knows it, write XML files and then mod.toml through temp files and moves, and append an enabled manifest entry so the folder loads at the next launch
category: assets
group: asset-xml
topic: ship-and-share-data-files-between-mods
approach: 4
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/parts-now-paste-xml-mod-writer]
techniques: [toml-config, msbuild]
hooks: [starmap StarMapBeforeGui, starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [KSA.Mod, KSA.ModLibrary, KSA.ModManifest, KSA.ModEntry, KSA.Universe, KSA.CelestialSystem]
tags: [mod-writer, manifest, atomic-write, mod-id, toml, runtime]
---

The paste workflow in parts-now (part of the unscience monorepo) turns XML from another tool, or hand-written XML, into a normal KSA
content mod without leaving the game. Nothing is loaded from memory: the documents are always written to `<mods>/<mod-id>/` and then
loaded from there, so the result also loads at the next launch. This article covers the whole writing side: id validation, atomic
writes, the merged `mod.toml` and the manifest entry. The hand-off to the runtime loader is in
[Load, validate and unload part mods in a running game](../../../vehicles-and-parts/parts/load-and-unload-part-mods-at-runtime/overview.md).

Demonstrated by [Turn pasted Part XML into a real, enabled KSA mod folder from inside the game](../../../../mods/unscience/features/parts-now-paste-xml-mod-writer.md).

## Background: how the game does it

The mods folder and the manifest are the game's, and the game rewrites the manifest itself. `LocalModsFolderPath` is
`Documents/.../mods`, `LocalManifestPath` is `manifest.toml`. At boot `PrepareManifest` appends orphaned folders with
`new ModEntry(id, count)`, whose constructor sets `Enabled = false, New = true`, which pops the game's "confirm mods" dialog at next
boot ([current/decomp/KSA/ModEntry.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModEntry.cs#L8-L40), [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L176-L180), [current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L343-L371)). `ModManifest.Save()`
writes only `id` and `enabled` per entry ([current/decomp/KSA/ModManifest.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModManifest.cs#L27-L48)).

## Lifecycle and timing

| Work | Hook | Notes |
|---|---|---|
| Form input, id validation, scan | `[StarMapAfterGui]` (`frame:draw-ui`) | Pure reads of `ModLibrary` and the file system, event-driven |
| `WriteModFolder` and the rest of the job | `[StarMapBeforeGui]` `Update(dt)` | One state per frame on the game thread |
| `EnsureManifestEntry` | Final job state | `ModManifest.Save()` writes `manifest.toml` synchronously |

Disk writes happen on the main thread inside one frame; a few small files, so the hitch is bounded. Nothing touches simulated state.
See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Validate the id against every place KSA could know it.** Each rule runs in its own `try/catch` and a rule that cannot run counts
   as a failure (fail closed). Shape, reserved names, the mods folder, `Content/<id>`, loaded mods and the manifest:

   ```csharp
   // parts-now.lib/Io/ModIdValidator.cs L80-L99
       public static List<string> Validate(string modId)
       {
           var problems = new List<string>();
           string id = (modId ?? string.Empty).Trim();

           if (id.Length == 0)
           {
               problems.Add("mod id is empty.");
               return problems;
           }

           CheckShape(id, problems);
           Check(problems, "reserved names", () => CheckReserved(id));
           Check(problems, "the mods folder", () => CheckModsFolder(id));
           Check(problems, "the game's Content folder", () => CheckContentFolder(id));
           Check(problems, "loaded mods", () => CheckLoadedMods(id));
           Check(problems, "the mod manifest", () => CheckManifest(id));

           return problems;
       }
   ```

   The rules: kebab-case `^[a-z0-9]+(?:-[a-z0-9]+)*$` of 3 to 48 characters; not `Core`, `Sample`, `parts-now` or `unscience`; no folder
   `<mods>/<id>`; no `Content/<id>` (a built-in mod); `ModLibrary.Find(id)` is null; and no entry with that id in
   `ModLibrary.Manifest.Mods` (a null manifest fails closed).

2. **Resolve the target from the game, never hard-code.** `ModLibrary.LocalModsFolderPath` is the source of truth:

   ```csharp
   // parts-now.lib/Io/ModIdValidator.cs L47-L73
       public static string ModsDirectory => _modsDirectory ??= ResolveModsDirectory();

       /// <summary>
       /// The absolute folder a given mod id would be written to. Returns an empty string when the
       /// mods directory is unknown or the id cannot form a path.
       /// </summary>
       /// <param name="modId">The candidate mod id.</param>
       public static string ResolveTargetPath(string modId)
       {
           string root = ModsDirectory;
           string id = (modId ?? string.Empty).Trim();

           if (root.Length == 0 || id.Length == 0)
           {
               return string.Empty;
           }

           try
           {
               return Path.GetFullPath(Path.Combine(root, id));
           }
           catch (Exception ex)
           {
               Console.WriteLine($"parts-now: could not resolve a folder for mod id '{id}': {ex.Message}");
               return string.Empty;
           }
       }
   ```

3. **Write assets first and `mod.toml` last, each through a `.tmp` sibling:**

   ```csharp
   // parts-now.lib/Io/ModFolderWriter.cs L95-L120
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
           catch (Exception ex)
           {
               CleanUpTemporaries(temporaryFiles);
               Console.WriteLine($"parts-now: failed to write mod folder '{modDir}': {ex}");
               return new ModFolderResult(false, modDir, written, ex.Message);
           }
   ```

   ```csharp
   // parts-now.lib/Io/ModFolderWriter.cs L329-L341
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
       // ...
   ```

   (UTF-8 without BOM, `\n` line endings; an existing `mod.toml` is merged, setting `name`, `description`, `version`, `author`, taking the
   union of `assets`, and re-emitting scalar keys before any table, otherwise a parsed `[StarMap]` table written first would swallow the
   `assets` key.)

4. **On success add an enabled, non-new manifest entry.** Do not use `new ModEntry(id, count)`:

   ```csharp
   // parts-now.lib/Io/ModFolderWriter.cs L134-L173
       public static bool EnsureManifestEntry(string modId)
       {
           string id = (modId ?? string.Empty).Trim();
           if (id.Length == 0)
           {
               Console.WriteLine("parts-now: cannot add a manifest entry for an empty mod id.");
               return false;
           }

           try
           {
               ModManifest? manifest = ModLibrary.Manifest;
               if (manifest?.Mods == null)
               {
                   Console.WriteLine(
                       $"parts-now: the game's mod manifest is not available — '{id}' will not load at "
                       + "the next launch. Enable it manually in the mods menu.");
                   return false;
               }

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
       // ...
   ```

5. **Clean up on failure.** If anything after the write fails, delete the folder this job created; an existing folder is itself a
   validation failure and would make the id unusable for the rest of the session. A folder that was already there is never deleted.

6. **Classify folders for the load/reload/unload panel.** A directory with a `mod.toml` is `Content` (non-empty `assets`), `StarMap`
   (entry assembly, no assets), `Both` or `Empty`; whether it is loadable is separate from whether the game loaded it at boot
   (`ModLibrary.Find(id)`).

## Complete example

- [parts-now.lib/Io/ModFolderWriter.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Io/ModFolderWriter.cs), [parts-now.lib/Io/ModIdValidator.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Io/ModIdValidator.cs),
  [parts-now.lib/Io/ModFolderScanner.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Io/ModFolderScanner.cs)
- The hand-off: [parts-now.lib/Runtime/RuntimeModLoaderGpuStates.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/parts-now.lib/Runtime/RuntimeModLoaderGpuStates.cs#L440-L455)

## Variations and gotchas

- **Atomic writes** protect against half-written files if the game exits mid-write; always the temp-then-move pattern.
- **The manifest is saved synchronously** on the game thread; the game's own save writes only `id` and `enabled`, so any extra key
  another tool put in the manifest is lost (for example KittenExtensions' `debug`, [Debug XML patches with the step debugger and the session log](../../validation/debug-xml-patches/overview.md)).
- **An unlisted folder is an orphan.** Without an entry the game appends one disabled on the next boot and shows the confirm dialog;
  writing the entry yourself avoids that.
- **Reserved ids** (`Core`, `Sample`, `parts-now`, `unscience`) are refused regardless of case.
- **Linux paths.** Compare folder names case-sensitively where the platform is (`mkmod`-style kebab-case ids keep that moot).

## See also

- [Load, validate and unload part mods in a running game](../../../vehicles-and-parts/parts/load-and-unload-part-mods-at-runtime/overview.md), [Ship data beside the DLL with an explicit copy list and resolve the mod root from the assembly](ship-and-share-data-files-between-mods-1.md), [Understand when and in which order KSA loads mod XML](../mod-xml-load-order/overview.md),
  [Validate asset XML ids and paths before launching the game (offline)](../../validation/validate-asset-xml-references/overview.md)
- [Turn pasted Part XML into a real, enabled KSA mod folder from inside the game](../../../../mods/unscience/features/parts-now-paste-xml-mod-writer.md)
