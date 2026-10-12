---
type: Mod Feature
title: Patch session log
description: An always-on KittenExtensions.log, reset at StarMapBeforeMain, that hooks the patch executor's events to log every applied or failed document mutation with old and new values and a per-file summary
mod: kittenextensions
category: testing-and-tooling
techniques: [xml-patch, starmap-lifecycle, event-subscription, file-logging]
hooks: [starmap StarMapBeforeMain, harmony-prefix KSA.ModLibrary.PrepareAll]
lifecycle: [startup:starmap-before-main, startup:asset-load]
game_types: [KSA.ModLibrary, KSA.Constants, Brutal.Logging.DefaultCategory]
tags: [logging, diagnostics, xml-patch, events, starmap]
---

Besides the interactive [patch debug stepper](patch-debug-stepper.md), every patch run writes a
plain-text audit trail. Each document mutation a patch causes (the `Set`, `Insert`, `InsertText` and
`Remove` action types of XPathPatch, whose source is not in the checkout) gets one line naming the patch file and mod it came from, the
file and mod it changed, the target node, the position, and the old and new values. A summary per
patch file follows, and the same counts go to the game log. A modder can answer "which mod changed
this value" with a text search instead of a debugger.[^kx-patchlogger]

## How it works

1. **Location.** `LogPath` is `Path.Combine(ModLibrary.LocalModsFolderPath, @"..\Logs",
   "KittenExtensions.log")` ([KittenExtensions/Patch/PatchLogger.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/PatchLogger.cs#L14-L16)), i.e.
   `<Documents>/My Games/Kitten Space Agency/Logs/KittenExtensions.log`, because the mods folder is
   `<Documents>/My Games/Kitten Space Agency/mods` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L178)).
   The game's own log folder is `logs` in the same parent ([current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281-L283)),
   the same directory on Windows' case-insensitive file system.[^ksa-modlibrary] [^ksa-constants]
2. **Reset.** `KxMod.Setup`, the `[StarMapBeforeMain]` method, deletes an existing log before it
   installs the Harmony patches ([KittenExtensions/Mod.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Mod.cs#L11-L22)), so the file holds the current
   session only even though the writer opens it in append mode.[^kx-mod]
3. **Attach.** `XmlPatcher.LoadData` calls `PatchLogger.Create(Executor)` right after creating the
   executor, in both normal and debug mode ([KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L104-L110)).
   `Create` prints the path to the console, creates the folder, opens an auto-flushing
   `StreamWriter`, and subscribes to the executor's `ActionStarted`, `ActionCompleted` and
   `ActionFailed` events ([KittenExtensions/Patch/PatchLogger.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/PatchLogger.cs#L29-L47)).[^kx-patcher]
4. **Snapshot on start.** For mutation actions (`Set`, `Insert`, `InsertText`, `Remove`) `OnActionStarted`
   records type, source and target locations, the target's debug name, the position, the old value
   (`Set`: the current value; `Remove`: the node's text form) and the new value (`Set`, `InsertText`:
   the first source result; `Insert`: the source node; `Remove`: `<removed>`)
   ([KittenExtensions/Patch/PatchLogger.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/PatchLogger.cs#L104-L159)). Capturing before execution is what makes
   the old value available.
5. **Write on completion.** `OnActionCompleted` and `OnActionFailed` count operation-level actions
   (`OpCopy` to `OpSetVar`) per patch file and write the stored snapshot as an `applied` or `failed`
   line, with the error text for failures ([KittenExtensions/Patch/PatchLogger.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/PatchLogger.cs#L74-L135)).
   Values are escaped (`\`, `\r`, `\n`, `"`) and cut at 4096 characters
   ([KittenExtensions/Patch/PatchLogger.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/PatchLogger.cs#L179-L184)).
6. **Locate files and mods.** `GetLocation` walks up at most 100 ancestors to the element carrying
   `RelPath` (a file root in the GameData document) and takes the mod from its parent's `Id`
   ([KittenExtensions/Patch/PatchLogger.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/PatchLogger.cs#L161-L174)).
7. **Summarize.** `CompleteLogging` runs `WriteSummary` (one `summary source=... applied=N failed=M`
   line per patch file, plus `KittenExtensions: <file>: N patch operations applied` at Info level, or a
   Warning for a file whose operations all failed) and disposes the writer
   ([KittenExtensions/Patch/PatchLogger.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/PatchLogger.cs#L49-L72)). It is called on success, after the debug
   popup, and before every fatal exit ([KittenExtensions/Patch/Patcher.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/Patcher.cs#L56-L121)).

Every logger callback swallows its own exceptions, so logging can never break a patch run.

## Lifecycle and timing

- `startup:starmap-before-main`: the old file is deleted when StarMap initializes the mod, before the
  game's entry point runs.
- `startup:asset-load`: the log is written during the KittenExtensions prefix on
  `ModLibrary.PrepareAll`, synchronously on the main thread, and closed before the game deserializes
  any XML. Nothing is written after startup.

## Key code

| Path | Role |
|---|---|
| [KittenExtensions/Mod.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Mod.cs#L11-L22) | Deletes the previous log at `StarMapBeforeMain` |
| [KittenExtensions/Patch/PatchLogger.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/PatchLogger.cs#L29-L47) | Opens the file and subscribes to executor events |
| [KittenExtensions/Patch/PatchLogger.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/PatchLogger.cs#L104-L159) | Mutation snapshots with old and new values |
| [KittenExtensions/Patch/PatchLogger.cs](https://github.com/AMPW-german/KittenExtensions/blob/b52467541a4fda3c3771eb2c157ed7574f9e2d7c/KittenExtensions/Patch/PatchLogger.cs#L49-L72) | File summary and game-log echo |

## Snippets

```csharp
// KittenExtensions/Patch/PatchLogger.cs L29-L47
    public static PatchLogger? Create(PatchExecutor executor)
    {
        try
        {
            var path = LogPath;
            Console.WriteLine(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var logger = new PatchLogger(new StreamWriter(path, append: true) { AutoFlush = true });
            executor.ActionStarted += logger.OnActionStarted;
            executor.ActionCompleted += logger.OnActionCompleted;
            executor.ActionFailed += logger.OnActionFailed;
            return logger;
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Warning($"KittenExtensions: unable to create patch log: {ex.Message}");
            return null;
        }
    }
```

```csharp
// KittenExtensions/Patch/PatchLogger.cs L125-L143
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

    private static bool IsPatchOperation(ActionType type) => type is
      ActionType.OpCopy or ActionType.OpMerge or ActionType.OpDelete or
      ActionType.OpIf or ActionType.OpIfAny or ActionType.OpIfNone or
      ActionType.OpWith or ActionType.OpSetVar;

    private static bool IsMutation(ActionType type) => type is
      ActionType.Set or ActionType.Insert or ActionType.InsertText or ActionType.Remove;
```

## Game APIs touched

| Member | What it is for |
|---|---|
| `ModLibrary.LocalModsFolderPath` ([current/decomp/KSA/ModLibrary.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ModLibrary.cs#L178)) | Anchor for the `..\Logs` path |
| `DefaultCategory.Log.Info` / `Warning` | Per-file counts in the game's own log |

## Pitfalls and version notes

- **Counts are keyed by file name only.** `appliedByFile` and `failedByFile` use the patch file's
  `RelPath`, not the mod, so two mods that both name their file `Patches.xml` share one summary line.
  The per-mutation lines do carry the mod.
- **Only mutations get lines.** `If`, `With` and `SetVar` change nothing by themselves; they appear in
  the counts but not as mutation lines.
- **The log path is built with a literal backslash** (`..\Logs`), a Windows path separator. On a
  platform where `\` is not a separator, `Path.Combine` would treat `..\Logs` as one folder name inside
  `mods`.
- **Disabled mods appear** in the log: their patch files are imported like any other mod's and,
  per the README, every `<Patch>` in the document runs (see
  [Combined GameData XML document](gamedata-document.md)).

## See also

- [Patch debug stepper](patch-debug-stepper.md)
- [XML patch files and XPathPatch operations](xpath-patch-operations.md)
- [Combined GameData XML document](gamedata-document.md)
- [Audit what patches changed from KittenExtensions.log](../../../howto/assets/validation/debug-xml-patches/debug-xml-patches-2.md) - how-to article demonstrating this feature

[^kx-patchlogger]: KittenExtensions/Patch/PatchLogger.cs
[^ksa-modlibrary]: KSA.ModLibrary (decompiled, build 2026.10.10.5554)
[^ksa-constants]: KSA.Constants (decompiled, build 2026.10.10.5554)
[^kx-mod]: KittenExtensions/Mod.cs
[^kx-patcher]: KittenExtensions/Patch/Patcher.cs
