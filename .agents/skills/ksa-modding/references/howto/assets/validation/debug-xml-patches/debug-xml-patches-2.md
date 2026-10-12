---
type: How-To
title: Audit what patches changed from KittenExtensions.log
description: Read the per-session KittenExtensions.log to see every Set, Insert, InsertText and Remove a patch made with old and new values, and the per-file applied and failed summary
category: assets
group: validation
topic: debug-xml-patches
approach: 2
difficulty: beginner
prerequisites: [starmap, kittenextensions]
demonstrated_by: [mods/kittenextensions/features/patch-session-log]
techniques: [xml-patch]
hooks: [starmap StarMapBeforeMain, harmony-prefix KSA.ModLibrary.PrepareAll]
lifecycle: [startup:starmap-before-main, startup:asset-load]
game_types: [KSA.ModLibrary, KSA.ModEntry, KSA.Constants, KSA.Program, KSA.ModManifest]
tags: [xml-patch, logging, audit, kittenextensions]
---

You do not always want to stop in a debugger. Every KittenExtensions (KX) patch run writes a plain-text audit
trail: each document mutation a patch causes gets one line naming the patch file and mod it came from, the file
and mod it changed, the target node, the position and the old and new values. A summary line per patch file follows,
and the same counts go to the game log. "Which mod changed this value?" becomes a text search.

Demonstrated by [Patch session log](../../../../mods/kittenextensions/features/patch-session-log.md). For interactive stepping see [Debug a failing XML patch with the KittenExtensions step debugger](debug-xml-patches-1.md).

## Background: how the game does it

The game has its own log folder; KX writes its file beside it:

```csharp
// current/decomp/KSA/Constants.cs L281-L283
	public static string DocumentsFolderPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "My Games", "Kitten Space Agency");

	public static string LogsFolderPath => Path.Combine(DocumentsFolderPath, "logs");
```

KX computes `<Documents>/My Games/Kitten Space Agency/Logs/KittenExtensions.log` from the mods folder
([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L178-L178)). The game's own folder is lower-case `logs`; the two are the same directory on a
case-insensitive file system and two directories on a case-sensitive one.

## Lifecycle and timing

- `startup:starmap-before-main`: KX's `[StarMapBeforeMain]` method deletes the previous log before it installs the
  Harmony patches, so the file holds the current session only even though the writer opens it in append mode.
- `startup:asset-load`: the log is written during KX's prefix on `ModLibrary.PrepareAll`, synchronously on the main
  thread, and closed before the game deserializes any XML. Nothing is written after startup. See [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Run the game once**, then open `Documents/My Games/Kitten Space Agency/Logs/KittenExtensions.log`. The file is
   deleted when the game starts, so copy it before the next launch if you want to keep it.

   ```csharp
   // KittenExtensions/Mod.cs L11-L22
       [StarMapBeforeMain]
       public void Setup()
       {
           var logFile = PatchLogger.LogPath;
           if (File.Exists(logFile))
           {
               File.Delete(logFile);
           }

           var harmony = new Harmony("KittenExtensions");
           harmony.PatchAll();
       }
   ```

2. **Read the mutation lines.** Each starts with `applied` or `failed` and has fixed key=value fields:

   ```csharp
   // KittenExtensions/Patch/PatchLogger.cs L125-L136
       private void WriteMutation(string outcome, MutationSnapshot mutation, string? error = null)
       {
           writer.Write(
             $"{outcome} type={mutation.Type} mod={Quote(mutation.Source.Mod)} " +
             $"source={Quote(mutation.Source.File)} targetFile={Quote(mutation.Target.File)} " +
             $"targetMod={Quote(mutation.Target.Mod)} targetObject={Quote(mutation.TargetObject)} position={mutation.Position} " +
             $"oldValue={Quote(mutation.OldValue)} newValue={Quote(mutation.NewValue)}");
           if (!string.IsNullOrEmpty(error))
               writer.Write($" error={Quote(error)}");
           writer.WriteLine();
       }

   ```

   `type` is `Set`, `Insert`, `InsertText` or `Remove`; `mod` and `source` name the patch that did it; `targetFile`,
   `targetMod` and `targetObject` name what changed; `oldValue` and `newValue` are escaped (`\`, newline, `"`) and cut
   at 4096 characters.

3. **Search it.** Examples that answer the usual questions (adapted, plain text search):

   ```text
   # adapted: not from the mod; searches over the log format shown above
   grep 'targetFile="Astronomicals.xml"' KittenExtensions.log      # who touched this file
   grep 'mod="AdvancedFlightComputer"' KittenExtensions.log         # everything one mod changed
   grep '^failed' KittenExtensions.log                              # only failures, with the error text
   ```

4. **Check the summary.** After the run `WriteSummary` writes one line per patch file and echoes it to the game log:

   ```csharp
   // KittenExtensions/Patch/PatchLogger.cs L49-L72
       public void WriteSummary()
       {
           try
           {
               writer.WriteLine($"[{DateTimeOffset.UtcNow:O}] Patch session summary");
               foreach (var (file, applied) in appliedByFile)
               {
                   failedByFile.TryGetValue(file, out var failed);
                   writer.WriteLine($"summary source={file} applied={applied} failed={failed}");
                   DefaultCategory.Log.Info($"KittenExtensions: {file}: {applied} patch operations applied{(failed > 0 ? $", {failed} failed" : "")}.");
               }
               foreach (var (file, failed) in failedByFile)
               {
                   if (appliedByFile.ContainsKey(file))
                       continue;

                   writer.WriteLine($"summary source={file} applied=0 failed={failed}");
                   DefaultCategory.Log.Warning($"KittenExtensions: {file}: 0 patch operations applied, {failed} failed.");
               }
           }
           catch
           {
           }
       }
   ```

   `applied` counts operations that completed (`Copy`, `Merge`, `Delete`, `If`, `With` and so on, including nested
   ones), not changes, so a `Copy` whose path selected nothing probably still counts as applied (inferred; the executor
   is not checked out). The summary only lists files that had at least one completed or failed operation, so a patch
   file missing from it never ran any operation: check that it is listed under `patches` in `mod.toml`. A file with
   `applied=N` but no `applied type=` mutation lines under its name ran but changed nothing, which usually means its
   paths selected nothing. A file whose operations all failed gets a Warning in the game log. Both are cues to open the stepper ([Debug a failing XML patch with the KittenExtensions step debugger](debug-xml-patches-1.md)).

5. **Find who and where.** The location of a node is found by walking up to the ancestor that carries `RelPath`,
   i.e. the imported file root, and reading its parent's `Id` as the mod:

   ```csharp
   // KittenExtensions/Patch/PatchLogger.cs L161-L174
       private static SourceLocation GetLocation(XPNodeRef node)
       {
           for (var depth = 0; node.Valid && depth < 100; depth++, node = node.Parent)
           {
               var file = node.Attribute("RelPath");
               if (file.Valid)
               {
                   var mod = node.Parent.Attribute("Id");
                   return new(file.Value, mod.Valid ? mod.Value : "<unknown>");
               }
           }

           return new("<unknown>", "<unknown>");
       }
   ```

## Complete example

- [KittenExtensions/Patch/PatchLogger.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/PatchLogger.cs) (whole logger)
- [KittenExtensions/Mod.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Mod.cs) (the reset), [KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L104-L121) (create and complete)

## Variations and gotchas

- **Only mutations are lines.** An operation that selects nothing writes no mutation line; it only shows up in the
  counts. Zero mutation lines for a file whose operations did run usually means nothing was selected (or an `If` guard
  skipped the change).
- **Logging never breaks a patch run.** Every callback swallows its own exceptions, and a failure to create the file
  logs a Warning and runs on.
- **Summary reflects operation-level actions** (`Copy`, `Merge`, `Delete`, `If`, `IfAny`, `IfNone`, `With`, `SetVar`),
  not mutations, so counts and lines differ.
- **The game log gets the summary too.** Look for `KittenExtensions: <file>: N patch operations applied` in the game's
  own log when you cannot find the KX file.
- **Per-session only.** The file is deleted on each launch; archive it if you need to compare two runs.

## See also

- [Debug a failing XML patch with the KittenExtensions step debugger](debug-xml-patches-1.md), [Patch Core or another mod's XML data without copying files](../../xml-patching/patch-game-xml-data/overview.md), [Write XPath patch operations for game XML](../../xml-patching/xpath-xml-patch-recipes/overview.md)
- [Log from a mod: per-mod log file, game log category and log-once](../../../getting-started/lifecycle/write-a-per-mod-log-file/overview.md), [Validate asset XML ids and paths before launching the game (offline)](../validate-asset-xml-references/overview.md)
- [Patch session log](../../../../mods/kittenextensions/features/patch-session-log.md)
