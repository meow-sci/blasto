---
type: How-To
title: Import user files into a catalog folder and refer to them by name
description: Copy PNG, GLB or sound files the player picks into a mod-owned catalog folder with an import size cap and (n) suffixes instead of overwriting, validate catalog names so they cannot escape the folder, and let features store only the catalog name so a saved scene can reproduce the file
category: persistence
group: user-data
topic: persist-user-created-presets-and-content
approach: 6
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/lib-shared-file-library]
techniques: [save-sidecar, imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui]
tags: [files, import, catalog, png, glb, audio, user-content, file-browser, unscience]
---
Outcome: the player picks an image, model or sound anywhere on disk; the mod copies it once into its own catalog folder and from then on refers to it by a bare catalog name. Settings, presets and scene saves hold the name, so they keep working when the original file is moved, and a missing file is a clear dependency error rather than a stale path. unscience does this for three catalogs (`pngs`, `glbs`, `sounds`) shared by several of its features. Demonstrated by [Shared file library and browser](../../../../mods/unscience/features/lib-shared-file-library.md). The ImGui file browser is covered here only as a consumer; its layout and navigation belong to `build-a-filesystem-file-picker`.

## Background: how the game does it

KSA gives mods no import pipeline for user media, only the asset system for content shipped in a mod (see [Ship data files with a mod and let other mods contribute more](../../../assets/asset-xml/ship-and-share-data-files-between-mods/overview.md)). A mod that lets players bring their own files owns the whole path: where the catalog lives, what is accepted, and how names are kept safe. The game's documents folder is the natural parent (`Constants.DocumentsFolderPath`, [current/decomp/KSA/Constants.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Constants.cs#L281)); unscience builds the same folder itself from `Environment.SpecialFolder.MyDocuments` and puts a dot-folder `.unscience` under it.

## Lifecycle and timing

All UI and file I/O run synchronously on the main thread inside the ImGui frame (`[StarMapAfterGui]`, `frame:draw-ui`). There is no background scanning: catalogs are rescanned on explicit actions only (a *Rescan* button, a browser *Refresh*), which keeps disk access out of the frame loop except when the user asks. Copying a large GLB (the cap is 128 MiB) blocks one frame. Because the catalog path is derived from `MyDocuments`, it does not follow a StarMap instance-path redirect and is unaffected by the redirect being removed before `[StarMapUnload]`. See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and [Find where KSA keeps user data and choose where a mod stores its config and data files](../choose-where-a-mod-stores-its-files/overview.md).

## Steps

1. **Pick a data root and keep catalogs flat.** One mod-wide data folder, one subfolder per media kind:

   ```csharp
   // ksa-abstractions.lib/KsaPaths.cs L6-L16
   public static class KsaPaths
   {
       /// <summary>The base KSA user data directory (e.g. My Documents\My Games\Kitten Space Agency)</summary>
       public static string UserDataDir { get; } = Path.Combine(
           Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
           "My Games",
           "Kitten Space Agency");

       /// <summary>The shared custom-data directory used by the Unscience mod suite.</summary>
       public static string ModDataDir { get; } = Path.Combine(UserDataDir, ".unscience");
   }
   ```

2. **Declare each catalog with its extensions and a cap.** A catalog is a folder name, a label, the accepted extensions and an optional maximum size. GLBs get a 128 MiB cap; PNGs and sounds are uncapped. A GLB *selection id* encodes a choice as `unscience-glb-file:<name>`, validated through the same path rules, so pickers can mix catalog files with built-in meshes and resolve the file lazily:

   ```csharp
   // ksa-abstractions.lib/SharedFileLibrary.cs L9-L26
   public sealed class SharedFileLibrary
   {
       private readonly HashSet<string> _extensions;
       private readonly long _maxFileBytes;
       public string DirectoryPath { get; }
       public string Label { get; }
       public string FormatDescription => string.Join(", ", _extensions);

       public SharedFileLibrary(string directory, string label, string[] extensions, string? dataRoot = null, long maxFileBytes = long.MaxValue)
       {
           _maxFileBytes = maxFileBytes;
           DirectoryPath = Path.Combine(dataRoot ?? KsaPaths.ModDataDir, directory);
           Label = label;
           _extensions = new(extensions, StringComparer.OrdinalIgnoreCase);
       }

       public void EnsureDir() => Directory.CreateDirectory(DirectoryPath);
       public bool Supports(string path) => _extensions.Contains(Path.GetExtension(path));
   ```

   ```csharp
   // ksa-abstractions.lib/GlbLibrary.cs L5-L24
   public static class GlbLibrary
   {
       private const string Prefix = "unscience-glb-file:";
       public static SharedFileLibrary Files { get; } = new("glbs", "GLB", new[] { ".glb" }, maxFileBytes: 128L * 1024 * 1024);
       public static string SelectionId(string name)
       {
           _ = Files.FullPath(name);
           return Prefix + name;
       }
       public static bool IsSelection(string id) => id.StartsWith(Prefix, StringComparison.Ordinal);
       public static string FileName(string id)
       {
           if (!IsSelection(id)) throw new ArgumentException("Not a shared GLB file selection.", nameof(id));
           string name = id[Prefix.Length..];
           _ = Files.FullPath(name);
           if (!Files.Supports(name)) throw new ArgumentException("Choose a GLB file.", nameof(id));
           return name;
       }
       public static string Label(string id) => FileName(id) + " · GLB library";
   }
   ```

   ```csharp
   // ksa-abstractions.lib/SoundLibrary.cs L4-L7
   public static class SoundLibrary
   {
       public static SharedFileLibrary Files { get; } = new("sounds", "Sound", new[] { ".ogg", ".wav", ".mp3" });
   }
   ```

3. **Never trust a catalog name.** `FullPath` accepts only a bare file name (no separators, not `.` or `..`), so a name read back from a save or a settings file cannot point outside the folder. `Scan` lists supported, non-hidden files in case-insensitive order and returns an empty list on any error:

   ```csharp
   // ksa-abstractions.lib/SharedFileLibrary.cs L28-L49
   public string FullPath(string name)
   {
       if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name is "." or "..")
           throw new ArgumentException("Choose a file from the shared library.", nameof(name));
       return Path.Combine(DirectoryPath, name);
   }

   public string[] Scan()
   {
       try
       {
           if (!Directory.Exists(DirectoryPath)) return Array.Empty<string>();
           return Directory.EnumerateFiles(DirectoryPath).Where(Supports)
               .Select(Path.GetFileName).OfType<string>().Where(n => !n.StartsWith('.'))
               .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
       }
       catch (Exception ex)
       {
           Console.WriteLine($"unscience/{Label}: scan failed: {ex.Message}");
           return Array.Empty<string>();
       }
   }
   ```

4. **Import by copy, never overwrite.** `Import` checks existence, extension and size; if the source already is the catalog file it returns the name unchanged (case-insensitive only on Windows); otherwise it picks the first free `stem (n).ext` and copies with `overwrite: false`. Existing catalog files are never replaced, so a name saved in a scene keeps meaning the file it meant:

   ```csharp
   // ksa-abstractions.lib/SharedFileLibrary.cs L51-L71
   public string? Import(string sourcePath, out string? error)
   {
       error = null;
       try
       {
           if (!File.Exists(sourcePath)) { error = "File not found."; return null; }
           if (!Supports(sourcePath)) { error = $"Supported files: {FormatDescription}."; return null; }
           if (new FileInfo(sourcePath).Length > _maxFileBytes)
           { error = $"File exceeds the {_maxFileBytes / (1024 * 1024)} MiB import limit."; return null; }
           EnsureDir();
           string source = Path.GetFullPath(sourcePath);
           string name = Path.GetFileName(source);
           var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
           if (string.Equals(source, Path.GetFullPath(FullPath(name)), comparison)) return name;
           string stem = Path.GetFileNameWithoutExtension(name), extension = Path.GetExtension(name);
           for (int number = 2; File.Exists(FullPath(name)); number++) name = $"{stem} ({number}){extension}";
           File.Copy(source, FullPath(name), overwrite: false);
           return name;
       }
       catch (Exception ex) { error = $"Import failed: {ex.Message}"; return null; }
   }
   ```

5. **Give consumers one callback that carries the catalog name.** The browser always imports through the library and reports only the resulting catalog name, then closes. The consumer rescans and selects the new entry:

   ```csharp
   // ksa-abstractions.lib/LibraryFileBrowser.cs L155-L164
   private void Pick(Action<string> onImported)
   {
       if (_selectedFile == null)
           return;
       var importedName = _library.Import(Path.Combine(_currentDir, _selectedFile), out _error);
       if (importedName == null)
           return;
       onImported(importedName);
       _open = false;
   }
   ```

   ```csharp
   // free-fallin.lib/FreeFallinSubmod.cs L195-L201
   private void Import(string imported)
   {
       Rescan();
       _selectedTexture = Array.FindIndex(_textures, name => string.Equals(name, imported, StringComparison.OrdinalIgnoreCase));
       _message = $"Imported {imported} into the shared PNG library.";
       _messageIsError = false;
   }
   ```

6. **Draw the browser from a place that always runs.** Free Fallin draws it from `RenderFloatingWindows`, called every frame from the mod's host, so the window stays open when the feature's own section is collapsed:

   ```csharp
   // free-fallin.lib/FreeFallinSubmod.cs L51
   public void RenderFloatingWindows() => _browser.Render(Import);
   ```

   The browser window itself is a plain `ImGui.Begin` with a stable `###media_browser_<id>` title id, so two features can have browsers open at once (a filter box, a bordered list child, an import footer):

   ```csharp
   // ksa-abstractions.lib/LibraryFileBrowser.cs L51-L76
   public void Render(Action<string> onImported)
   {
       if (!_open)
           return;

       RefreshListing();
       ImGui.SetNextWindowSize(new float2(620, 480), ImGuiCond.FirstUseEver);
       if (ImGui.Begin($"{_windowTitle}###media_browser_{_id}", ref _open))
       {
           RenderQuickLinks();
           RenderPathRow();

           ImGui.SetNextItemWidth(-1f);
           ImGui.InputTextWithHint($"##media_browser_filter_{_id}", "filter..."u8, _filter);
           var filterText = _filter.ToString().Trim();

           var footerHeight = ImGui.GetFrameHeightWithSpacing() * 2f;
           ImGui.BeginChild($"##media_browser_list_{_id}", new float2(0, -footerHeight),
               ImGuiChildFlags.Borders);
           RenderEntries(filterText, onImported);
           ImGui.EndChild();

           RenderFooter(onImported);
       }
       ImGui.End();
   }
   ```

7. **Store the name, not the path.** Features keep the catalog name in their state and resolve it with `FullPath` when they need bytes. A scene save records the name; on load, a missing file is reported as a dependency error (see [Save a whole mod's scene state as versioned sidecar records, with reset and replay on load](../../saves/save-mod-state-with-the-scene/overview.md)).

## Complete example

| File | Role |
|---|---|
| [ksa-abstractions.lib/SharedFileLibrary.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/SharedFileLibrary.cs) | Catalog: validate, scan, copy-import, quick links |
| [ksa-abstractions.lib/LibraryFileBrowser.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/LibraryFileBrowser.cs) | Reusable ImGui browser; picking always imports |
| [ksa-abstractions.lib/PngLibrary.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PngLibrary.cs) | Static PNG facade over a `SharedFileLibrary` |
| [ksa-abstractions.lib/GlbLibrary.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/GlbLibrary.cs) | GLB catalog, size cap, selection ids |
| [ksa-abstractions.lib/SoundLibrary.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/SoundLibrary.cs) | Sound catalog |
| [ksa-abstractions.lib/KsaPaths.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/KsaPaths.cs) | Data root |
| [free-fallin.lib/FreeFallinSubmod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/free-fallin.lib/FreeFallinSubmod.cs#L51) | A consumer drawing the browser |

## Variations and gotchas

- **Duplicates are name-based.** Importing the same image from two folders yields `image.png` and `image (2).png`; there is no content hash.
- **Case.** Two names that differ only in case are the same file on Windows and macOS default file systems and different files on Linux; `Import` handles the self-import check with an OS-dependent comparison but `FullPath` does not normalise case.
- **The import blocks the frame.** There is no background copy; a 128 MiB GLB stalls one frame, the cost accepted in exchange for simple error handling. Move the copy to a worker if your files are larger (see [Write config files atomically and load hand-edited ones without losing them](../../settings-formats/write-and-load-config-files-safely/overview.md) for the thread-handoff rules).
- **Catalog files are user data outside saves.** Deleting a file from the folder breaks every scene that names it. Report it; do not crash.
- **Lazy versus frozen ids.** GLB selection ids resolve the current file at use time, so editing the file changes every user. A feature that needs stable content stores a path-and-hash id instead (Pebbles freezes a chosen file before storing recipes).
- **Browser filter text** relies on a hotkey guard so typing does not trigger game keys; see [Block game hotkeys while typing into an ImGui field](../../../ui/input/block-game-hotkeys-while-typing/overview.md).

## See also

- Sibling approaches: [Snake_case JSON course file](./persist-user-created-presets-and-content-5.md), [Bundled and user theme TOML files](./persist-user-created-presets-and-content-2.md), [overview](./overview.md)
- [Shared file library and browser](../../../../mods/unscience/features/lib-shared-file-library.md)
- [Build searchable lists and browsers](../../../ui/windows/build-searchable-lists-and-browsers/overview.md), [Build a mod settings window (and developer panel)](../../../ui/windows/build-a-settings-window/overview.md)
