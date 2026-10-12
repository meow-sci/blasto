---
type: How-To
title: Derive the data folder from the mod's log folder, with a temp-directory fallback
description: Probe the plausible KSA user folders for an existing Logs directory, log there, and store course files in a sibling KSAGolf/Courses folder, falling back to the temp directory when no KSA folder is found - and why Constants.LogsFolderPath is the better source
category: persistence
group: user-data
topic: choose-where-a-mod-stores-its-files
approach: 4
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksagolf/features/course-grid-and-save-file, mods/ksastructures/features/mod-log-file]
techniques: [json-config]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded]
game_types: [KSA.Constants]
tags: [paths, logs-folder, probing, temp-fallback, linux, case-sensitivity]
---

Outcome: a data folder found without any game types. KSAGolf's `Log.Folder` is the one place the mod knows how to find on every platform, and course files go to `<parent of Logs>/KSAGolf/Courses`; KSAStructures probes the same candidates for its log. Demonstrated by [Course grid model and JSON save file](../../../../mods/ksagolf/features/course-grid-and-save-file.md) and [Per-mod log file beside KSA's logs](../../../../mods/ksastructures/features/mod-log-file.md). Read this as the approach to avoid unless the mod must run with no reference to `KSA.dll`; the last section shows the direct way.

## Background: how the game does it

The game's log folder is `Constants.LogsFolderPath`, the lowercase `logs` directory under the documents folder, created in the `Program` constructor ([current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L283), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L694-L699)). `Constants.DocumentsFolderPath` is `Personal` + `My Games/Kitten Space Agency` ([current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)). These mods do not call either; they re-derive candidate paths themselves so the logging and file code compiles and runs without game references (their tests do too).

## Lifecycle and timing

Resolution is lazy and cached (`EnsureResolved`), triggered by the first log line or the first call to `Log.Folder`, normally in `StarMapAllModsLoaded` (`startup:all-mods-loaded`). Course files are read and written from the builder UI (`frame:draw-ui`). See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **List plausible folders, best first, and use the first that exists.** Only existing directories are used, so a wrong guess costs a failed `Directory.Exists`; the temp directory is the last resort and always works:

   ```csharp
   // src/KSAGolf/Ksa/Log.cs L231-L260
   // Where the log might go, best first. KSA runs on Linux as well as Windows and puts its user
   // data in a different place on each, so this tries the plausible locations rather than assuming
   // one — and only ever uses a directory that already exists, so a wrong guess costs nothing but
   // a failed Directory.Exists. The temp directory is the last resort and always works, which is
   // why the mod can say where it is logging on startup rather than silently going quiet.
   private static IEnumerable<string> CandidateDirectories()
   {
       const string game = "Kitten Space Agency";

       // Windows, and Wine or Proton, which map Documents into the prefix the same way.
       string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
       if (!string.IsNullOrEmpty(documents))
       {
           yield return Path.Combine(documents, "My Games", game, "Logs");
       }

       // Linux. XDG_DATA_HOME when set, otherwise the default it stands for.
       string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
       string xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? "";
       if (string.IsNullOrEmpty(xdgData) && !string.IsNullOrEmpty(home))
       {
           xdgData = Path.Combine(home, ".local", "share");
       }

       if (!string.IsNullOrEmpty(xdgData))
       {
           yield return Path.Combine(xdgData, game, "Logs");
           yield return Path.Combine(xdgData, game);
       }
   ```

   The remaining candidates are the rest of the XDG data folder variants and the temp directory.

2. **Pick once, keep the previous run's log.** The first candidate that accepts the write wins; the previous session's log is copied to `KSAGolf.prev.log` before it is truncated:

   ```csharp
   // src/KSAGolf/Ksa/Log.cs L200-L229
   // Picks a log location once: KSA's own Logs folder if it can be found, otherwise the user's
   // temp directory.
   private static void EnsureResolved()
   {
       if (_resolved) return;
       _resolved = true;

       foreach (string candidate in CandidateDirectories())
       {
           try
           {
               if (!Directory.Exists(candidate)) continue;

               string path = Path.Combine(candidate, "KSAGolf.log");

               // Truncated per session so the file reflects this run, not every run ever -- and the
               // last run kept beside it, because a game relaunched to look into a report is exactly
               // the moment the report's own log would otherwise be lost.
               if (File.Exists(path)) File.Copy(path, Path.Combine(candidate, "KSAGolf.prev.log"), overwrite: true);
               File.WriteAllText(path, $"=== KSAGolf session {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
               _path = path;
               Console.WriteLine($"{Prefix} logging to {path}");
               return;
           }
           catch
           {
               // Try the next candidate.
           }
       }
   }
   ```

3. **Expose the folder, falling back to temp.**

   ```csharp
   // src/KSAGolf/Ksa/Log.cs L187-L198
   /// <summary>
   /// The folder the log went to, which is the one place this mod knows how to find on every
   /// platform. Anything else it writes goes beside it rather than repeating the search.
   /// </summary>
   public static string Folder
   {
       get
       {
           lock (Gate) EnsureResolved();
           return _path is null ? Path.GetTempPath() : Path.GetDirectoryName(_path) ?? Path.GetTempPath();
       }
   }
   ```

4. **Put other files beside it.** Courses go to the folder's parent plus `KSAGolf/Courses`, so nothing repeats the search:

   ```csharp
   // src/KSAGolf/Ksa/CourseBuilder.cs L442
   public static string Folder => Path.Combine(Directory.GetParent(Log.Folder)?.FullName ?? Log.Folder, "KSAGolf", "Courses");
   ```

   KSAStructures carries the same `Log` class with its own file names ([src/KSAStructures/Ksa/Log.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Log.cs#L187-L260)).

### Doing it directly

5. **Prefer the game's constants when the mod references the game:**

   ```csharp
   // adapted from KSAGolf/src/KSAGolf/Ksa/CourseBuilder.cs L442 and current/decomp/KSA/Constants.cs L281
   public static string Folder =>
       Path.Combine(KSA.Constants.DocumentsFolderPath, "KSAGolf", "Courses");
   ```

   This follows a StarMap instance path and the real folder on every platform the game supports, with no probing. A mod with no game reference can read the property by reflection ([Store config in a mod-named subfolder under Constants.DocumentsFolderPath](choose-where-a-mod-stores-its-files-2.md)).

## Complete example

| File | Role |
|---|---|
| [src/KSAGolf/Ksa/Log.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Log.cs#L187-L260) | Candidate list, selection, `Folder` |
| [src/KSAGolf/Ksa/CourseBuilder.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/CourseBuilder.cs#L442) | Course folder derived from the log folder |
| [src/KSAStructures/Ksa/Log.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Log.cs) | KSAStructures' copy |

## Variations and gotchas

- **Case.** The candidate list names `.../Kitten Space Agency/Logs` with a capital L, while the game creates `.../Kitten Space Agency/logs` (`Constants.LogsFolderPath`). On a case-insensitive file system these are the same folder; on a case-sensitive one the capitalised candidate does not exist, the probe skips it, and the mod falls through to the XDG candidates and finally the temp directory, where course files would then be saved. This is read from the two code paths, not observed on Linux.
- **Linux paths are guesses.** The candidates assume `My Games/Kitten Space Agency` under `MyDocuments`, or an XDG data folder; I did not verify how `Environment.SpecialFolder.Personal` (the game's choice) resolves on Linux, so treat the list as the author's guess.
- **Temp is not durable.** Courses in the temp directory disappear when the OS clears it. The mod says where it logs, so the player can see which case applies.
- **A log folder is the wrong home for user data.** KSArmory's settings store notes that `Logs/` is "a folder people clear out" and files its loose settings beside `saves/` instead; data placed beside the log folder is only one level away from that risk.

## See also

- [Store config in a mod-named subfolder under Constants.DocumentsFolderPath](choose-where-a-mod-stores-its-files-2.md), [Know the folder map and redirect it with a StarMap instance path](choose-where-a-mod-stores-its-files-5.md), [overview](./overview.md)
- [Course grid model and JSON save file](../../../../mods/ksagolf/features/course-grid-and-save-file.md), [Per-mod log file beside KSA's logs](../../../../mods/ksastructures/features/mod-log-file.md)
- [Log from a mod: per-mod log file, game log category and log-once](../../../getting-started/lifecycle/write-a-per-mod-log-file/overview.md), [Persist user-created palettes, presets and content files](../persist-user-created-presets-and-content/overview.md)
