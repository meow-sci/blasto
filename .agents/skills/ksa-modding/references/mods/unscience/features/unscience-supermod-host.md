---
type: Mod Feature
title: Supermod host that aggregates 31 feature panels into one window
description: The unscience StarMap entry class builds every ISubmod at StarMapAllModsLoaded, ticks them all each frame, renders them as collapsible sections of one ImGui window and persists only its own window layout and header state
mod: unscience
submod: unscience
category: getting-started
techniques: [starmap-lifecycle, imgui-window, imgui-menu, toml-config]
hooks:
  - "starmap StarMapImmediateLoad (empty, zero-argument signature: never dispatched)"
  - starmap StarMapAllModsLoaded
  - starmap StarMapBeforeGui
  - starmap StarMapAfterGui
  - starmap StarMapUnload
  - harmony-prefix KSA.Program.OnDrawUiConsole (hidden-HUD replay of Update)
lifecycle: [startup:all-mods-loaded, frame:draw-ui]
game_types: []
tags: [starmap, lifecycle, imgui, supermod, toml, window-state]
---

`unscience/Mod.cs` is the only class the game loads from the shipped mod. It is a plain StarMap
`[StarMapMod]` class that owns a list of 31 `ISubmod` instances (one per feature library), wires
the few cross-feature dependencies, applies all patches through one Harmony instance, and draws a
single "Unscience Toolbox" window in which each feature is a collapsing header. Toggle with F11 or
the "Unscience" item it adds to the game's menu bar.[^unscience-mod]

Mechanism in one line: StarMap attributes on one class drive the whole suite: build and patch at
`AllModsLoaded`, `Update` everything at `BeforeGui`, render at `AfterGui`, dispose at `Unload`.

## How it works

`OnFullyLoaded` (`[StarMapAllModsLoaded]`):

1. Construct every sub-mod in display order (keeping references to the three whose objects the
   patcher needs: I Feel Seen's tracker, Camera Controller Override's sequence player, Pebbles'
   clutter controller).
2. `Initialize()` each one and default its visibility to on.
3. Restore UI state from `.unscience/` (see below).
4. Hand dependencies to the static `Patcher` (tracker, sequence player, clutter controller, a menu-bar
   toggle callback), register `UpdateSubmods` as the hidden-HUD replay, create the scene-save
   coordinator, then `Patcher.Patch()`.

Per frame:

| Hook | Host work |
|---|---|
| `[StarMapBeforeGui]` `OnBeforeUi` | `UpdateSubmods(dt)`: `Update(dt)` on every sub-mod, visible or not, each in its own try/catch |
| `HiddenUiFrameHook.BeforeGui` | Same `UpdateSubmods` when the HUD is hidden |
| `[StarMapAfterGui]` `OnAfterUi` | F11 toggle via `ImGui.IsKeyPressed`; if visible, draw the window and run the layout auto-save timer; always call `RenderFloatingWindows()` on every sub-mod |

The window (`RenderWindow`) has a menu bar: **View** (show/hide all, a per-sub-mod visibility
checkbox list sorted by name, tooltip toggle; `ImGuiItemFlags.AutoClosePopups` is pushed off so the
menu stays open while ticking boxes), **Collapse**/**Expand** (force all headers via
`SetNextItemOpen(..., ImGuiCond.Always)` for one frame), and **State** (layout auto-save toggle and
interval). Below it the scene-save status line, then one `CollapsingHeader` per visible sub-mod whose
`RenderContent()` is wrapped in try/catch so an exception prints a red error line instead of
breaking the window. (The README still mentions a gear icon; the code uses the View menu.)[^unscience-readme]

UI state persistence ([unscience/UnscienceState.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/UnscienceState.cs)):

- `window.ini`: `ImGui.SaveIniSettingsToMemory()` filtered down to the `[Window][Unscience Toolbox]`
  section, restored with `ImGui.LoadIniSettingsFromMemory`. Persisting only its own section avoids
  touching the game's ImGui layout.[^unscience-state]
- `state.toml` (Tomlyn): `header_open` and `visibility` tables keyed by sub-mod name, plus
  `settings.save_interval` (1-30 s, default 5), `auto_save_enabled`, `show_mod_tooltips`.
- Both live in `My Games/Kitten Space Agency/.unscience/`, saved on the interval while the window is
  open, on demand, and at unload.

This is global UI preference, separate from per-save scene state (see the scene-save sidecar).

## Lifecycle and timing

| Attribute | StarMap dispatch | Frame position (build 5554) |
|---|---|---|
| `[StarMapImmediateLoad]` | Never: StarMap only registers a method with one `KSA.Mod` parameter and skips other signatures[^starmap-baseattributes][^starmap-runtimemod] | Empty and zero-argument in unscience, so the skip is harmless |
| `[StarMapAllModsLoaded]` | After `ModLibrary.LoadAll` | `startup:all-mods-loaded`; asset bundles loaded, but `ModLibrary.Bind` (GPU) and `ModLibrary.AttachGameData` have not run yet (`Program.cs` L978, L985), so part templates lack their game data; no scene yet |
| `[StarMapBeforeGui]` | Prefix of `Program.OnDrawUiFrame`[^starmap-program-patcher] | `Program.OnFrame` L2267, after `PrepareFrame` (physics dispatched), `PrepareImGui`, `OnFrameViewports`; skipped while `DrawUI` is false |
| `[StarMapAfterGui]` | Postfix of `Program.OnDrawUiViewports` | L2268; inside the ImGui frame, before `OnDrawUiThreadSafe` (menu bar) and `ImGui.Render` |
| `[StarMapUnload]` | `StarMapCore.DeInit` -> `ModLoader.Dispose`, after `Program.Main` returned and after StarMap's id-less `UnpatchAll()` | Save layout, dispose sub-mods, dispose save coordinator, `Patcher.Unload()`. At game exit `Program.Dispose` has already destroyed the renderer and Vulkan device, GLFW and FMOD, so only managed cleanup and file writes are meaningful here[^starmap-gamesurveyer][^ksa-program] |

Consequences: sub-mod `Update` sees the camera state already computed for this frame and the vehicle
state committed at the start of the frame, but physics for the next step is already running, so the
host never mutates simulated state itself.[^ksa-program] The menu-bar entry is drawn later in the same
frame (`OnDrawUiThreadSafe`), so a click there toggles the window for the next frame. `[StarMapAfterOnFrame]`
is not used.[^scope-architecture]

## Key code

| Path | Role |
|---|---|
| [unscience/Mod.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/Mod.cs) | Entry class, lifecycle, window |
| [unscience/UnscienceState.cs](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/UnscienceState.cs) | Filtered ImGui ini and TOML UI state |
| [unscience/mod.toml](https://github.com/meow-sci/unscience/blob/1829b3b298dd0fa5a67342cafff29074c8c29286/unscience/mod.toml) | Manifest (`name = "unscience"`, `EntryAssembly = "MeowSci.Unscience"`)[^unscience-toml] |

## Snippets

```csharp
// unscience/Mod.cs L157-L192
    private void UpdateSubmods(double dt)
    {
        if (!_isInitialized || _isDisposed) return;

        // Update ALL submods every frame, even hidden ones
        foreach (var submod in _submods)
        {
            try { submod.Update(dt); }
            catch (Exception ex) { Console.WriteLine($"unscience/{submod.Name}: Update error: {ex.Message}"); }
        }
    }

    [StarMapAfterGui]
    public void OnAfterUi(double dt)
    {
        try
        {
            if (!_isInitialized || _isDisposed) return;

            if (ImGui.IsKeyPressed(ImGuiKey.F11))
                _windowVisible = !_windowVisible;

            if (_windowVisible)
            {
                RenderWindow();

                if (_autoSaveEnabled)
                {
                    _timeSinceLastSave += dt;
                    if (_timeSinceLastSave >= UnscienceState.SaveIntervalSeconds)
                    {
                        _timeSinceLastSave = 0;
                        SaveAll();
                    }
                }
            }
```

Persisting only the mod's own ImGui window section:

```csharp
// unscience/UnscienceState.cs L44-L87
    public static void SaveImGuiWindowState()
    {
        try
        {
            var fullIni = ImGui.SaveIniSettingsToMemory().ToString();
            var filtered = FilterIniForUnscienceWindows(fullIni);
            if (string.IsNullOrEmpty(filtered)) return;

            Directory.CreateDirectory(_stateDir);
            File.WriteAllText(Path.Combine(_stateDir, WindowIniFile), filtered);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"unscience: Failed to save window state: {ex.Message}");
        }
    }

    private static string FilterIniForUnscienceWindows(string iniData)
    {
        // Extract only the [Window][Unscience Toolbox] section from ImGui ini data
        // so we don't persist/restore state for unrelated game windows
        var sb = new StringBuilder();
        var lines = iniData.Split('\n');
        bool capturing = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r');

            if (trimmed.StartsWith("[Window][", StringComparison.Ordinal))
            {
                capturing = trimmed == $"[Window][{WindowName}]";
            }
            else if (trimmed.Length > 0 && trimmed[0] == '[')
            {
                capturing = false;
            }

            if (capturing)
                sb.AppendLine(trimmed);
        }

        return sb.ToString();
    }
```

Per-section rendering with error containment:

```csharp
// unscience/Mod.cs L300-L321
            foreach (var submod in _submods)
            {
                if (!_submodVisibility[submod.Name]) continue;

                if (_expandAll)
                    ImGui.SetNextItemOpen(true, ImGuiCond.Always);
                else if (_collapseAll)
                    ImGui.SetNextItemOpen(false, ImGuiCond.Always);
                else
                    ImGui.SetNextItemOpen(_headerOpen.GetValueOrDefault(submod.Name, false), ImGuiCond.Once);

                var headerLabel = _showModTooltips ? $"{submod.Name}  (?)" : submod.Name;
                bool isOpen = ImGui.CollapsingHeader(headerLabel, ImGuiTreeNodeFlags.None);
                _headerOpen[submod.Name] = isOpen;
                if (_showModTooltips && ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
                    ImGui.SetTooltip(submod.Tooltip);

                if (isOpen)
                {
                    try { submod.RenderContent(); }
                    catch (Exception ex) { ImGui.TextColored(new float4(1f, 0.3f, 0.3f, 1f), $"Error: {ex.Message}"); }
                }
```

## Game APIs touched

No game types directly; the host uses `Brutal.ImGuiApi` (`Begin` with `ImGuiWindowFlags.MenuBar`,
`CollapsingHeader`, `SetNextItemOpen`, `PushItemFlag(ImGuiItemFlags.AutoClosePopups, false)`,
`SaveIniSettingsToMemory`/`LoadIniSettingsFromMemory`, `IsKeyPressed(ImGuiKey.F11)`), Tomlyn, and
StarMap's attributes, which StarMap dispatches from Harmony patches on `Program.OnDrawUiFrame`,
`Program.OnDrawUiViewports` and `Program.OnFrame`.

## Pitfalls and version notes

- Sub-mod names are dictionary keys for visibility and header state; renaming a sub-mod's `Name`
  resets its saved UI state.
- `F11` is read with `ImGui.IsKeyPressed`, not a game input binding, so it is not remappable and is
  not blocked by the hotkey guard.
- Window-layout state is loaded at `AllModsLoaded`; the ImGui context must already exist then (it does
  in current builds since StarMap fires this after `ModLibrary.LoadAll` in the running program).
- Initialization is one try/catch: an exception in any sub-mod constructor or `Initialize` leaves the
  host uninitialized (nothing ticks or draws) and is only logged.

## See also

- [ISubmod contract](./lib-isubmod-contract.md)
- [Consolidated patcher](./unscience-consolidated-patcher.md)
- [Menu-bar entry](./unscience-menu-bar-entry.md)
- [Hidden-HUD frame hook](./lib-hidden-ui-frame-hook.md)
- [Scene-save sidecar](./unscience-scene-save-sidecar.md)
- [Mod overview](../overview.md)
- [Scaffold a mod with mkmod.ts and grow it into a library hosted by a supermod](../../../howto/getting-started/loader/create-a-minimal-starmap-mod/create-a-minimal-starmap-mod-2.md) - how-to article demonstrating this feature
- [Persist only your own ImGui window section with SaveIniSettingsToMemory and LoadIniSettingsFromMemory](../../../howto/persistence/user-data/persist-imgui-window-state/persist-imgui-window-state-2.md) - how-to article demonstrating this feature

[^unscience-mod]: unscience/Mod.cs
[^unscience-readme]: unscience/README.md
[^unscience-state]: unscience/UnscienceState.cs
[^unscience-toml]: unscience/mod.toml
[^starmap-program-patcher]: StarMap.Core/Patches/ProgramPatcher.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^scope-architecture]: scope/00-architecture-and-abstractions.md (authors' lifecycle map)
[^starmap-baseattributes]: StarMap.API/BaseAttributes.cs
[^starmap-runtimemod]: StarMap.Core/ModRepository/RuntimeMod.cs
[^starmap-gamesurveyer]: StarMap/GameSurveyer.cs
