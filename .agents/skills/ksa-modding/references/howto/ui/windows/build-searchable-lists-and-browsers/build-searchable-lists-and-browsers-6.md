---
type: How-To
title: "Searchable lists: a file browser that copies picks into a flat shared catalogue"
description: Let players pick files from disk in an ImGui browser window and always copy the chosen file into a flat, non-overwriting catalogue folder before handing back a catalogue name
category: ui
group: windows
topic: build-searchable-lists-and-browsers
approach: 6
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/unscience/features/lib-shared-file-library]
techniques: [imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImGuiSelectableFlags, Brutal.ImGuiApi.ImGuiChildFlags]
tags: [file-browser, file-picker, catalogue, import, selectable, double-click, unscience]
---
Several unscience features consume user files (PNG images, GLB models, sound files). Rather than each reading arbitrary paths, which break when the player moves a file, every import is copied once into a catalogue folder and features refer to files by catalogue name. One ImGui window, `LibraryFileBrowser`, serves all three catalogues: quick-link buttons, an Up button and Refresh, a filter box, a bordered list of folders and files, and an Import/Cancel footer. Demonstrated by unscience. Prerequisite: StarMap.

## Background: how the game does it

Nothing in KSA provides a file picker; this is `System.IO` plus ImGui. The ImGui pieces are `BeginChild` with `ImGuiChildFlags.Borders` and a negative height that reserves the footer, `Selectable` with `ImGuiSelectableFlags.AllowDoubleClick` and `IsMouseDoubleClicked`, and `BeginDisabled` for the Import button.[current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8206-L8209)[^brutal-imguiapi-imgui] The window title uses a stable `###media_browser_<id>` id so two features can have browsers open at once.

## Lifecycle and timing

All UI and file I/O run synchronously on the main thread inside the ImGui frame (`frame:draw-ui`). There is no background scanning: directory listings are cached until the folder changes or Refresh is pressed, so the window does not touch the disk every frame; a large copy (up to the library's size cap) blocks one frame. Features open the browser from a button and draw it from the host's floating-window render hook, so it stays up even when the feature's own section is collapsed (per the mod feature page). See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Define the catalogue.** `SharedFileLibrary` validates names (a bare file name only, so saved names cannot escape the folder), scans supported files, and imports by copying with `(2)`, `(3)` suffixes instead of overwriting:

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

2. **Render the browser window.** Refresh the cached listing, begin with a `###` id, draw quick links and the path row, a filter box, a bordered child that leaves room for the footer, and the footer:

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

3. **Draw entries.** Directories open on double-click; files select on click and import on double-click; the id of every row is suffixed with the browser id and the name:

```csharp
// ksa-abstractions.lib/LibraryFileBrowser.cs L103-L135
private void RenderEntries(string filterText, Action<string> onImported)
{
    foreach (var dir in _dirs)
    {
        if (!Matches(dir, filterText)) continue;
        if (ImGui.Selectable($"[dir]  {dir}##media_d_{_id}_{dir}", false,
                ImGuiSelectableFlags.AllowDoubleClick)
            && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            Navigate(Path.Combine(_currentDir, dir));
            return;
        }
    }

    foreach (var file in _files)
    {
        if (!Matches(file, filterText)) continue;
        var selected = _selectedFile == file;
        if (ImGui.Selectable($"{file}##media_f_{_id}_{file}", selected,
                ImGuiSelectableFlags.AllowDoubleClick))
        {
            _selectedFile = file;
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                Pick(onImported);
                return;
            }
        }
    }

    if (_dirs.Length == 0 && _files.Length == 0)
        ImGui.TextDisabled($"No folders or {_library.FormatDescription} files here.");
}
```

4. **Import and report.** The footer's Import button is disabled until a file is selected; errors show in red; a successful import copies the file and hands the catalogue name to the callback:

```csharp
// ksa-abstractions.lib/LibraryFileBrowser.cs L137-L164
private void RenderFooter(Action<string> onImported)
{
    var canImport = _selectedFile != null;
    if (!canImport) ImGui.BeginDisabled();
    if (ImGui.Button($" Import ##media_browser_import_{_id}"))
        Pick(onImported);
    if (!canImport) ImGui.EndDisabled();
    ImGui.SameLine(0, 8);
    if (ImGui.Button($" Cancel ##media_browser_cancel_{_id}"))
        _open = false;
    ImGui.SameLine(0, 12);
    ImGui.AlignTextToFramePadding();
    if (!string.IsNullOrEmpty(_error))
        ImGui.TextColored(new float4(1f, 0.3f, 0.3f, 1f), _error);
    else
        ImGui.TextDisabled(_selectedFile ?? $"Select a {_library.FormatDescription} file");
}

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

5. **Cache directory listings** until the folder changes, catching access errors:

```csharp
// ksa-abstractions.lib/LibraryFileBrowser.cs L175-L203
private void RefreshListing()
{
    if (_listedDir == _currentDir)
        return;
    _listedDir = _currentDir;
    _dirs = Array.Empty<string>();
    _files = Array.Empty<string>();
    try
    {
        _dirs = Directory.EnumerateDirectories(_currentDir)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => !name.StartsWith('.'))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _files = Directory.EnumerateFiles(_currentDir)
            .Where(_library.Supports)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => !name.StartsWith('.'))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _error = null;
    }
    catch (Exception ex)
    {
        _error = ex is UnauthorizedAccessException ? "Access denied." : ex.Message;
    }
}
```

## Complete example

| File | Role |
|---|---|
| [ksa-abstractions.lib/SharedFileLibrary.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/SharedFileLibrary.cs#L28-L71) | Catalogue: validate, scan, import |
| [ksa-abstractions.lib/LibraryFileBrowser.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/ksa-abstractions.lib/LibraryFileBrowser.cs#L13-L207) | The browser window |

## Variations and gotchas

- **Always copy, never reference.** The catalogue name is the contract; if the source file moves, the feature still works.
- **Names must be bare file names.** `FullPath` rejects separators, `.` and `..`.
- **Never overwrite.** If the source already is the catalogue file, its name is returned unchanged; otherwise the first free `stem (n).ext` is chosen.
- **Disk access is on the main thread.** Cache listings and rescan only on explicit action.
- **Hidden files are skipped** (names starting with `.`).
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of build-searchable-lists-and-browsers](overview.md)
- [Approach 4: filterable colour catalogue list](build-searchable-lists-and-browsers-4.md)
- [Persist mod settings as TOML](../../../persistence/settings-formats/persist-mod-settings-toml/overview.md)
- Mod feature: [Shared file library](../../../../mods/unscience/features/lib-shared-file-library.md)

[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
