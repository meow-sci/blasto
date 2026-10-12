---
type: How-To
title: Save every live window as a named layout in one TOML file and apply it only on request
description: Observe each window's last position and size every frame, capture all live windows and in-world panels into one hand-emitted TOML file per layout written atomically, and recreate them when the player asks, skipping name collisions and clamping saved geometry into the current work area
category: persistence
group: user-data
topic: persist-imgui-window-state
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/purrtty/features/saved-terminal-layouts]
techniques: [toml-config, imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, startup:all-mods-loaded]
game_types: [Brutal.ImGuiApi.ImGui, KSA.LayoutSaves, KSA.Vehicle]
tags: [layouts, windows, toml, workspace, geometry, in-world, apply-on-request, purrtty]
---
Outcome: the player arranges several windows (and in-world panels), names the arrangement, and later recreates it with one click. The layout is data the player owns: one TOML file per layout, with a position and size in pixels for every 2D window, an anchor and extent for every in-world panel, and the shell and startup command for each terminal. Nothing is applied at game start. Demonstrated by [Saved terminal layouts](../../../../mods/purrtty/features/saved-terminal-layouts.md). The example windows are purrTTY terminals; the pattern fits any mod with several user-created windows. Theme handling inside a layout is covered by [Load bundled and user color themes from TOML files and let the user's theme win a name clash](../persist-user-created-presets-and-content/persist-user-created-presets-and-content-2.md).

## Background: how the game does it

KSA has its own layout system, but for HUD gauges, not for windows: `LayoutSaves` keeps one folder per layout under `<docs>/HUDLayouts` (`Constants.DocumentsFolderPath`), `LayoutSave.Write` replaces the folder through `SaveDirectory.TryReplace`, and `SerializedLayout.CaptureCurrent` exports every non-code-only `GaugeCanvas` ([current/decomp/KSA/LayoutSaves.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LayoutSaves.cs#L119), [current/decomp/KSA/LayoutSave.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/LayoutSave.cs#L84-L116), [current/decomp/KSA/SerializedLayout.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/SerializedLayout.cs#L20-L32)). A mod's windows are not part of it, and ImGui's ini cannot express a *set* of windows to create. A mod that wants named workspaces owns the capture, the file and the apply step. The window position and size it reads come from `ImGui.GetWindowPos` and `GetWindowSize` inside the window's own `Begin`/`End`.

## Lifecycle and timing

- **Observe geometry every frame in `frame:draw-ui`.** Each terminal window records its last position and size right after its `Begin`, so capture later needs no ImGui call.
- **Capture and apply run from buttons in the layout dialog,** which `TerminalMod.OnAfterUi` (`[StarMapAfterGui]`) draws each frame. Apply must run inside the UI pass: creating an in-world panel allocates GPU resources and measures the font cell, which needs an active ImGui frame.
- **Shell start is asynchronous**, so Apply returns quickly; each terminal's startup command is sent when its shell has started.
- **Nothing is read at startup** beyond listing the folder when the dialog opens.

See the [game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Record each window's geometry as it is drawn.** Only a window with a real size counts, so a collapsed or not-yet-laid-out window reports "no geometry" and is saved without one:

   ```csharp
   // purrTTY.Display/Ghostty/TerminalWindow.cs L385-L397
   ImGui.Begin(_imguiName, flags);
   ImGui.PopStyleVar(2);

   UpdateFocusState(ImGui.IsWindowFocused());

   var windowPos = ImGui.GetWindowPos();
   var windowSize = ImGui.GetWindowSize();
   if (windowSize.X > 0f && windowSize.Y > 0f)
   {
       LastKnownPosition = windowPos;
       LastKnownSize = windowSize;
       HasObservedGeometry = true;
   }
   ```

2. **Model an entry with nullable, kind-specific fields.** 2D windows persist position and size in pixels only (cols, rows and font are derived live); in-world entries persist their extent and anchor instead. Unused fields stay null so a window entry never carries in-world data on disk:

   ```csharp
   // purrTTY.Display/Layouts/LayoutDefinition.cs L24-L72
   /// <summary>
   /// One terminal in a layout. Kind-specific placement fields are nullable so a window
   /// entry never carries in-world fields on disk and vice-versa (the GameMod mapper
   /// supplies defaults for absent values). 2D windows persist <b>position + size only</b>
   /// — cols/rows and font are derived live by the emulator from the window's pixel size
   /// and the resolved theme; in-world terminals persist cols/rows (authoritative texture
   /// extent) plus the anchor and transform.
   /// </summary>
   public sealed class TerminalEntry
   {
       public string Name { get; set; } = "";

       public TerminalKind Kind { get; set; } = TerminalKind.Window;

       /// <summary>Named theme to apply (resolved via the ThemeCatalog); null = the global default.</summary>
       public string? Theme { get; set; }

       // ---- 2D window placement: position + size (px) ONLY. Null on in-world entries. ----
       public float? PosX { get; set; }
       public float? PosY { get; set; }
       public float? Width { get; set; }
       public float? Height { get; set; }

       // ---- in-world placement. Null on window entries. cols/rows are authoritative. ----
       public int? Cols { get; set; }
       public int? Rows { get; set; }
       public string? Mode { get; set; }            // "part" | "billboard"
       public string? VehicleId { get; set; }
       public string? PartId { get; set; }
       public string? PartName { get; set; }         // informational only (human re-pick aid)
       public string? SubPartId { get; set; }
       public float? OffsetX { get; set; }
       public float? OffsetY { get; set; }
       public float? OffsetZ { get; set; }
       public float? RotationX { get; set; }
       public float? RotationY { get; set; }
       public float? RotationZ { get; set; }
       public float? WidthMeters { get; set; }
       public float? HeightMeters { get; set; }
       public float? BillboardDistance { get; set; }
       public float? BillboardOffsetX { get; set; }
       public float? BillboardOffsetY { get; set; }
       public float? BillboardWidthMeters { get; set; }
       public float? BillboardHeightMeters { get; set; }
       public float? BillboardRotationX { get; set; }
       public float? BillboardRotationY { get; set; }
       public float? BillboardRotationZ { get; set; }
       public bool? BillboardAlwaysOnTop { get; set; }
       public bool? BillboardClickToFocus { get; set; }
   ```

3. **Capture every live window into entries.** The capture reads the recorded geometry and the theme *name*; custom colors persist only if the player saved them as a named theme:

   ```csharp
   // purrTTY.Display/Ghostty/GhosttyTerminalController.cs L179-L201
   public IReadOnlyList<WindowLayoutRecord> CaptureWindows()
   {
       var result = new List<WindowLayoutRecord>();
       for (int i = 0; i < _windows.Count; i++)
       {
           var window = _windows[i];
           if (!window.IsOpen)
           {
               continue;
           }

           result.Add(new WindowLayoutRecord
           {
               Name = window.Name,
               Position = window.HasObservedGeometry ? window.LastKnownPosition : null,
               Size = window.HasObservedGeometry ? window.LastKnownSize : null,
               ThemeName = window.Settings.ThemeName,
               Launch = window.Sessions.ActiveSession?.LaunchOptions?.Clone(),
           });
       }

       return result;
   }
   ```

   ```csharp
   // purrTTY.GameMod/Layouts/LayoutManager.cs L187-L212
   public void CaptureCurrentAs(string name, string? description = null)
   {
       var layout = new TerminalLayout
       {
           Header = new LayoutHeader
           {
               Name = name.Trim(),
               Description = string.IsNullOrWhiteSpace(description) ? null : description!.Trim(),
           },
       };

       if (_inWorld is not null)
       {
           foreach (var instance in _inWorld.Instances)
           {
               layout.Terminals.Add(FromInWorld(instance.Record));
           }
       }

       foreach (var window in _controller.CaptureWindows())
       {
           layout.Terminals.Add(FromWindow(window));
       }

       _catalog.Save(layout);
   }
   ```

4. **Emit the TOML by hand and write atomically.** The writer is a `StringBuilder` that appends only non-null keys, with the shell sub-table last (TOML requires a table's scalars before its sub-tables). It deliberately avoids Tomlyn's reflection serializer, which pulls `Microsoft.Extensions.ObjectPool` that the test reference set lacks:

   ```csharp
   // purrTTY.Display/Layouts/LayoutTomlFormat.cs L133-L156, L184-L186, L209-L211
   public static void Save(string filePath, TerminalLayout layout)
   {
       var sb = new StringBuilder();

       sb.Append("[layout]\n");
       sb.Append("name = ").Append(Quote(layout.Header.Name)).Append('\n');
       if (!string.IsNullOrEmpty(layout.Header.Description))
       {
           sb.Append("description = ").Append(Quote(layout.Header.Description)).Append('\n');
       }

       foreach (var e in layout.Terminals)
       {
           sb.Append("\n[[terminal]]\n");
           sb.Append("name = ").Append(Quote(e.Name)).Append('\n');
           sb.Append("kind = ").Append(Quote(e.Kind == TerminalKind.InWorld ? "inworld" : "window")).Append('\n');
           AppendStr(sb, "theme", e.Theme);

           // 2D window placement (px) — position + size only.
           AppendNum(sb, "pos_x", e.PosX);
           AppendNum(sb, "pos_y", e.PosY);
           AppendNum(sb, "width", e.Width);
           AppendNum(sb, "height", e.Height);

   ..
           // Shell sub-table LAST (TOML requires a table's scalars before its sub-tables).
           sb.Append("\n[terminal.shell]\n");
           sb.Append("shell_type = ").Append(Quote(e.Shell.ShellType.ToString())).Append('\n');
   ..
       Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
       AtomicFile.WriteAllText(filePath, sb.ToString());
   }
   ```

5. **Make the catalog a thin file layer.** The folder sits beside the settings file, names are sanitised into file names, `All()` lists `*.toml`, and a missing or corrupt file loads as null instead of throwing:

   ```csharp
   // purrTTY.Display/Layouts/LayoutCatalog.cs L14-L19, L53-L84, L130-L138
   public static string GetLayoutsDirectory()
   {
       var configDir = Path.GetDirectoryName(ThemeConfiguration.GetConfigFilePath())
                       ?? Environment.CurrentDirectory;
       return Path.Combine(configDir, "layouts");
   }
   ..
   /// <summary>Loads a layout by name; returns null if missing or unparsable.</summary>
   public TerminalLayout? Load(string name)
   {
       var path = PathFor(name);
       if (!File.Exists(path))
       {
           return null;
       }

       var layout = LayoutTomlFormat.Load(path);
       if (layout is null)
       {
           return null;
       }

       // The file name is the source of truth for the layout's identity, so a
       // renamed/copied file still loads under the right name if [layout].name drifted.
       if (string.IsNullOrWhiteSpace(layout.Header.Name))
       {
           layout.Header.Name = name;
       }

       return layout;
   }

   /// <summary>Writes a layout (atomically), using its header name as the file name.</summary>
   public void Save(TerminalLayout layout)
   {
       var name = string.IsNullOrWhiteSpace(layout.Header.Name) ? "Layout" : layout.Header.Name;
       var path = PathFor(name);
       LayoutTomlFormat.Save(path, layout);
   }
   ..
   private static string PathFor(string name)
       => Path.Combine(GetLayoutsDirectory(), SanitizeFileName(name) + ".toml");

   private static string SanitizeFileName(string name)
   {
       var invalid = Path.GetInvalidFileNameChars();
       var chars = name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
       return new string(chars);
   }
   ```

6. **Apply per entry, skipping what cannot be created.** Blank names are skipped; a name that collides with a live terminal is logged and skipped (never renamed or replaced); the rest still load. Created terminals are tracked per layout so that *Tear down* removes exactly that set:

   ```csharp
   // purrTTY.GameMod/Layouts/LayoutManager.cs L57-L87, L113-L130
   public LayoutApplyResult Apply(string layoutName)
   {
       var skipped = new List<(string Name, string Reason)>();
       var layout = _catalog.Load(layoutName);
       if (layout is null)
       {
           ModLog.Log.Error($"Layout '{layoutName}' could not be loaded.");
           return new LayoutApplyResult(0, skipped);
       }

       var created = new List<INamedTerminal>();
       bool anyWindow = false;

       foreach (var entry in layout.Terminals)
       {
           string name = entry.Name?.Trim() ?? string.Empty;
           if (name.Length == 0)
           {
               skipped.Add(("(unnamed)", "blank terminal name"));
               continue;
           }

           // Name-collision policy: log + skip; never rename or replace a live terminal.
           if (!TerminalTargetRegistry.IsNameAvailable(name))
           {
               ModLog.Log.Error($"Layout '{layoutName}': terminal name '{name}' collides with a live terminal — skipped.");
               skipped.Add((name, "name already in use by a live terminal"));
               continue;
           }

           var theme = _themes.Find(entry.Theme);
   ..
       if (anyWindow)
       {
           _controller.IsVisible = true;
       }

       if (created.Count > 0)
       {
           if (_loaded.TryGetValue(layoutName, out var existing))
           {
               existing.AddRange(created);
           }
           else
           {
               _loaded[layoutName] = created;
           }
       }

       return new LayoutApplyResult(created.Count, skipped);
   ```

7. **Clamp saved geometry into the current work area.** A layout saved on a larger monitor setup can land fully off-screen; with the chrome hidden there would be nothing to grab. The window applies a pending placement once, with `ImGuiCond.Always`, after clamping:

   ```csharp
   // purrTTY.Display/Ghostty/TerminalWindow.cs L349-L358
   if (_pendingPlacement is { } placement)
   {
       // Saved/cascaded geometry can land fully off-screen (different
       // monitor layout since last run); with chrome hidden there would be
       // nothing visible to grab, so clamp into the viewport work area.
       var (clampedPos, clampedSize) = ClampToWorkArea(placement.Pos, placement.Size);
       ImGui.SetNextWindowPos(clampedPos, ImGuiCond.Always);
       ImGui.SetNextWindowSize(clampedSize, ImGuiCond.Always);
       _pendingPlacement = null;
   }
   ```

   ```csharp
   // purrTTY.Display/Ghostty/TerminalWindow.cs L757-L772
   private static (float2 Pos, float2 Size) ClampToWorkArea(float2 pos, float2 size)
   {
       var viewport = ImGui.GetMainViewport();
       var workPos = viewport.WorkPos;
       var workSize = viewport.WorkSize;
       if (workSize.X <= 0f || workSize.Y <= 0f)
       {
           return (pos, size);
       }

       float w = Math.Clamp(size.X, Math.Min(100f, workSize.X), workSize.X);
       float h = Math.Clamp(size.Y, Math.Min(60f, workSize.Y), workSize.Y);
       float x = Math.Clamp(pos.X, workPos.X, Math.Max(workPos.X, workPos.X + workSize.X - w));
       float y = Math.Clamp(pos.Y, workPos.Y, Math.Max(workPos.Y, workPos.Y + workSize.Y - h));
       return (new float2(x, y), new float2(w, h));
   }
   ```

8. **Tear down through the normal close paths.** Skip terminals the player already closed, close windows immediately, and hand in-world panels to their manager's deferred GPU free:

   ```csharp
   // purrTTY.GameMod/Layouts/LayoutManager.cs L139-L170
   public int TeardownSet(string layoutName)
   {
       if (!_loaded.TryGetValue(layoutName, out var terminals))
       {
           return 0;
       }

       int removed = 0;
       foreach (var terminal in terminals)
       {
           // Skip anything the user already closed (it unregistered itself).
           if (!TerminalTargetRegistry.All.Contains(terminal))
           {
               continue;
           }

           switch (terminal)
           {
               case TerminalWindow window:
                   _controller.CloseWindow(window);
                   removed++;
                   break;
               case InWorldTerminalInstance instance:
                   _inWorld?.Remove(instance);
                   removed++;
                   break;
           }
       }

       _loaded.Remove(layoutName);
       return removed;
   }
   ```

## Complete example

| File | Role |
|---|---|
| [purrTTY.Display/Layouts/LayoutDefinition.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Layouts/LayoutDefinition.cs) | Layout and entry model |
| [purrTTY.Display/Layouts/LayoutTomlFormat.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Layouts/LayoutTomlFormat.cs#L133-L211) | Hand-emitted save; DOM load above it |
| [purrTTY.Display/Layouts/LayoutCatalog.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Layouts/LayoutCatalog.cs) | List, load, save, delete, rename |
| [purrTTY.GameMod/Layouts/LayoutManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Layouts/LayoutManager.cs#L57-L212) | Apply, tear down, capture |
| [purrTTY.GameMod/Layouts/UI/LayoutManagerUI.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Layouts/UI/LayoutManagerUI.cs) | The dialog: list, load, tear down, delete, save current, per-layout editor |
| [purrTTY.Display/Ghostty/TerminalWindow.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.cs#L349-L397) | Geometry observation and one-shot placement |

## Variations and gotchas

- **In-world anchors are best-effort across restarts.** Only `Vehicle.Id` is reliably stable; part and sub-part ids resolve to the first match (the mod's own note in `docs/gotchas.md`). Anchoring by a stable key is the subject of [Identify vessels and parts stably across saves](../../saves/identify-vessels-and-parts-across-saves/overview.md).
- **Never auto-apply a layout on load.** A stale layout could spawn shells the player did not ask for.
- **Name collisions skip, not rename,** so a partially loaded layout is possible; the dialog reports how many were skipped.
- **Editing a file changes only the file.** Live terminals are untouched until the layout is loaded again.
- **Pixels, not text units.** The file stores pixels for windows. A change of the game's font settings rebuilds the ImGui font atlas (`FontManager.RegenerateFonts` clears it, [current/decomp/KSA/FontManager.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/FontManager.cs#L55-L57)); pixel rectangles survive, while columns and rows derived from font metrics change when the font does, which is why windows do not store them.
- **Layout names are file names.** They go through the same invalid-character replacement as theme names; two names that differ only in case are one file on case-insensitive file systems.
- **Where the file lives.** The layouts folder derives from the same `MyDocuments`-based root as the settings file, so it ignores a StarMap instance-path redirect (see [Find where KSA keeps user data and choose where a mod stores its config and data files](../choose-where-a-mod-stores-its-files/overview.md)).

## See also

- Sibling approaches: [Plain ImGui.Begin so imgui.ini restores geometry](./persist-imgui-window-state-1.md), [Filtered ImGui ini section saved on a timer and at unload](./persist-imgui-window-state-2.md), [overview](./overview.md)
- [Saved terminal layouts](../../../../mods/purrtty/features/saved-terminal-layouts.md)
- [Persist mod settings as TOML](../../settings-formats/persist-mod-settings-toml/overview.md), [Write config files atomically and load hand-edited ones without losing them](../../settings-formats/write-and-load-config-files-safely/overview.md)
