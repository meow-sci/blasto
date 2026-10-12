---
type: Mod Feature
title: Shared copied-import media catalogs with an ImGui file browser
description: SharedFileLibrary keeps flat, non-overwriting catalogs of user files under .unscience (pngs, glbs, sounds) that several features share, and LibraryFileBrowser is a reusable ImGui filesystem picker that always copies the chosen file into the catalog before handing back its catalog name
mod: unscience
category: ui
techniques: [imgui-window]
hooks: [starmap StarMapAfterGui (browser windows drawn from ISubmod.RenderFloatingWindows)]
lifecycle: [frame:draw-ui]
game_types: []
tags: [imgui, file-browser, import, assets, png, glb, sounds, filesystem]
---

Several unscience features consume user files: Graffiti and Free Fallin take PNG images, Pebbles and
Sphinx import GLB models, BYO Music plays OGG/WAV/MP3. Rather than each feature reading arbitrary paths
(which break when the user moves a file, and which a scene save could not reproduce), every import is
copied once into a shared catalog folder under `My Games/Kitten Space Agency/.unscience/`, and features
refer to files by catalog name. One ImGui browser serves all three catalogs.[^shared-file-library][^repository-index]

Mechanism in one line: `SharedFileLibrary(directory, label, extensions, maxFileBytes)` validates,
copies with `(2)`, `(3)` suffixes instead of overwriting, and scans; `LibraryFileBrowser` is an
ImGui window over `System.IO` whose Import button calls `library.Import` and returns the catalog name
through a callback.

## How it works

Catalogs (all rooted at `KsaPaths.ModDataDir`, the `.unscience` folder):[^ksa-paths]

| Facade | Folder | Extensions | Notes | Users |
|---|---|---|---|---|
| `PngLibrary` | `pngs` | `.png` | Static compatibility facade over an internal `SharedFileLibrary`[^png-library] | Graffiti, Free Fallin, Pebbles, Sphinx |
| `GlbLibrary.Files` | `glbs` | `.glb` | 128 MiB import cap; `SelectionId(name)` encodes a choice as `unscience-glb-file:<name>` so pickers can mix catalog files with built-in meshes and resolve lazily[^glb-library] | Pebbles, Sphinx |
| `SoundLibrary.Files` | `sounds` | `.ogg`, `.wav`, `.mp3` | Decoding is validated by the playback backend[^sound-library] | BYO Music |

`SharedFileLibrary` rules:

- `FullPath(name)` rejects anything that is not a bare file name (no separators, not `.` or `..`), so
  saved names cannot escape the catalog folder.
- `Scan()` lists supported, non-hidden files sorted case-insensitively; errors are logged and yield an
  empty list.
- `Import(source, out error)` checks existence, extension and size; if the source already is the catalog
  file (case-insensitive on Windows) it returns the name unchanged; otherwise it picks the first free
  `stem (n).ext` and copies with `overwrite: false`. Existing catalog files are never replaced.
- `QuickLinks()` offers Home, Desktop, Downloads, Pictures, the catalog itself, and on Windows every ready drive.

`LibraryFileBrowser` (and the `PngFileBrowser` facade) is a self-contained ImGui window: quick-link
buttons, an Up button and Refresh, a filter text box, a bordered child list with `[dir]` entries
(double-click navigates) and files (double-click imports), and an Import/Cancel footer showing errors in
red. Directory listings are cached until the folder changes or Refresh is pressed, so the window does
not hit the disk every frame. The window title uses a stable `###media_browser_<id>` ImGui id so two
features can have browsers open at once.[^library-file-browser]

Features open the browser from a button in `RenderContent` and draw it from
`ISubmod.RenderFloatingWindows`, so it stays up even when the feature's section is collapsed (for
example Free Fallin's `RenderFloatingWindows() => _browser.Render(Import)`).[^free-fallin-submod][^byo-music-submod]

## Lifecycle and timing

All UI and file I/O run synchronously on the main thread inside the ImGui frame
(`[StarMapAfterGui]`, `frame:draw-ui`). There is no background scanning: catalogs are rescanned only on
explicit actions (Graffiti's "Rescan PNGs", a browser Refresh), which keeps disk access out of the frame
loop except when the user asks. Copying a large GLB (up to 128 MiB) therefore blocks one frame.

## Key code

| Path | Role |
|---|---|
| [ksa-abstractions.lib/SharedFileLibrary.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/SharedFileLibrary.cs) | Catalog: validate, scan, copy-import, quick links |
| [ksa-abstractions.lib/LibraryFileBrowser.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/LibraryFileBrowser.cs) | Reusable ImGui file browser |
| [ksa-abstractions.lib/PngLibrary.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PngLibrary.cs) | PNG catalog facade |
| [ksa-abstractions.lib/GlbLibrary.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/GlbLibrary.cs) | GLB catalog and selection ids |
| [ksa-abstractions.lib/SoundLibrary.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/SoundLibrary.cs) | Sound catalog |
| [ksa-abstractions.lib/PngFileBrowser.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/PngFileBrowser.cs) | PNG browser facade |

## Snippets

```csharp
// ksa-abstractions.lib/SharedFileLibrary.cs L28-L71
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

## Game APIs touched

None; the feature uses `System.IO` and `Brutal.ImGuiApi` (`ImInputString`, `InputTextWithHint`,
`Selectable` with `AllowDoubleClick`, `BeginChild` with `ImGuiChildFlags.Borders`). The filter text box
relies on the [hotkey guard](./lib-hotkey-guard.md) so typing does not trigger game keys.

## Pitfalls and version notes

- Catalog files are user data outside saves: a scene save records catalog names, and a missing file on
  load is a dependency error the feature must report, not crash on.
- The duplicate check is name-based, not content-based: importing the same image twice from different
  folders yields `image.png` and `image (2).png`.
- A `name` that differs only in case maps to the same file on Windows and macOS default filesystems but
  not on Linux; `Import` handles self-import with an OS-dependent comparison.
- `GlbLibrary` selection ids are deliberately lazy ("never frozen asset versions"); Pebbles freezes a
  chosen file to a path/hash id before storing recipes so later edits to the file are detectable.

## See also

- [Pebbles GLB import](./pebbles-glb-import.md)
- [Graffiti bindless decal textures](./graffiti-bindless-decal-textures.md)
- [BYO Music vessel 3D audio](./byo-music-vessel-3d-audio.md)
- [Free Fallin canopy material substitution](./free-fallin-canopy-material-substitution.md)
- [Game-state providers](./lib-game-state-providers.md) (`KsaPaths`)
- [Mod overview](../overview.md)
- [Import user files into a catalog folder and refer to them by name](../../../howto/persistence/user-data/persist-user-created-presets-and-content/persist-user-created-presets-and-content-6.md) - how-to article demonstrating this feature
- [Searchable lists: a file browser that copies picks into a flat shared catalogue](../../../howto/ui/windows/build-searchable-lists-and-browsers/build-searchable-lists-and-browsers-6.md) - how-to article demonstrating this feature

[^shared-file-library]: ksa-abstractions.lib/SharedFileLibrary.cs
[^repository-index]: REPOSITORY_INDEX.md
[^ksa-paths]: ksa-abstractions.lib/KsaPaths.cs
[^png-library]: ksa-abstractions.lib/PngLibrary.cs
[^glb-library]: ksa-abstractions.lib/GlbLibrary.cs
[^sound-library]: ksa-abstractions.lib/SoundLibrary.cs
[^library-file-browser]: ksa-abstractions.lib/LibraryFileBrowser.cs
[^free-fallin-submod]: free-fallin.lib/FreeFallinSubmod.cs
[^byo-music-submod]: byo-music.lib/ByoMusicSubmod.cs
