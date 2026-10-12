---
type: Mod Feature
title: Settings menu entry, settings window and optional ModMenu listing
description: Adds a KSA Structures menu from the StarMapBeforeGui hook, a small settings window from StarMapAfterGui, and lists itself in ModMenu through a copied attribute without depending on it
mod: ksastructures
category: ui
techniques: [imgui-menu, imgui-window, starmap-lifecycle, inter-mod-api]
hooks: [starmap StarMapBeforeGui, starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, KSA.Program]
tags: [imgui, menu-bar, settings, modmenu, tooltip, window-id]
---

The player's three settings (first-person view, the garage lamps, a verbose log) live under a
**KSA Structures** menu in the main menu bar and in a small settings window it opens. If the
community ModMenu mod is installed, the mod lists itself there instead of drawing its own menu, but
it never references ModMenu.[^mod-ui][^mod-modmenu]

## How it works

1. **Settings are plain public fields** on a `Settings` object, so ImGui can bind them with `ref`
   and the developer bridge can set them by name through reflection. They last for the session; the
   mod writes no settings file.[^mod-settings][^mod-bridgecommand]
2. **Menu from the GUI prefix.** `[StarMapBeforeGui]` (a prefix on `Program.OnDrawUiFrame`) calls
   `Ui.DrawMenuBarEntry`, which opens `ImGui.BeginMainMenuBar`, adds `BeginMenu("KSA Structures")`
   with three `MenuItem`s bound by `ref`, and ends the bar. Exceptions are swallowed with a one-time
   warning, because the call runs inside KSA's GUI pass.[^mod-ui][^mod-entry][^starmap-program]
   The mod's comments say this opens "the main menu bar before KSA fills it, so this mod's entry sits
   beside File and Universe". That is not what happens in any build in this corpus: KSA never calls
   `BeginMainMenuBar`, so the entry lands in Dear ImGui's own main-menu-bar window, a separate strip
   at the top of the main viewport (see the pitfall below).[^ksa-program]
3. **Window from the GUI postfix.** `[StarMapAfterGui]` calls `Ui.Draw`. The window title carries
   the version and a fixed `###KSAStructures` id so ImGui keeps its position across versions;
   `ImGuiWindowFlags.AlwaysAutoResize`; each checkbox has a wrapped tooltip
   (`BeginItemTooltip`, `PushTextWrapPos` at 35 em). Ticking **Verbose log** sets `Log.Threshold`
   immediately (see [per-mod log file](./mod-log-file.md)).[^mod-ui]
4. **Applying a setting.** The GUI postfix copies `OfficeLamps` into `Underground.LampsOn` every pass;
   first person reads its field in its own step.[^mod-entry]
5. **ModMenu without a dependency.** ModMenu (a separate community mod) finds menu entries by
   scanning loaded assemblies for an attribute by type name, according to the mod's notes, so
   `ModMenuEntry.cs` declares an identical internal `ModMenuEntryAttribute(string menuName, string?
   isModMenuActivePropertyName)` and puts it on a static `DrawModMenu` method that draws the same
   three items.[^mod-modmenu] `ModMenuPresence.Installed` checks once for a loaded assembly named
   `ModMenu`; when present, the mod skips its own menu bar entry so it is not listed twice. ModMenu
   is not in this corpus, so its scanning rules are taken from the mod's comments (unverified).

## Lifecycle and timing

| Phase | Hook | Work |
|---|---|---|
| `frame:draw-ui` (start) | `[StarMapBeforeGui]`, prefix on `Program.OnDrawUiFrame` | Entry in Dear ImGui's main menu bar (not KSA's) |
| `frame:draw-ui` (end of viewport UI) | `[StarMapAfterGui]`, postfix on `Program.OnDrawUiViewports` | Settings window; lamps setting applied |
| ModMenu's draw | ModMenu calls `DrawModMenu` | Same items inside ModMenu's menu |

Both StarMap GUI hooks are skipped while the UI is hidden, which is fine for drawing UI.

## Key code

| Path | Role |
|---|---|
| [src/KSAStructures/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Ui/Ui.cs#L19-L51) | Menu bar entry and menu items |
| [src/KSAStructures/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Ui/Ui.cs#L56-L77) | ModMenu callback |
| [src/KSAStructures/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Ui/Ui.cs#L79-L123) | Settings window and tooltips |
| [src/KSAStructures/Ksa/Ui/ModMenuEntry.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/Ui/ModMenuEntry.cs#L1-L47) | Copied attribute and presence check |
| [src/KSAStructures/Sim/Settings.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Sim/Settings.cs#L1-L17) | The settings |
| [src/KSAStructures/Ksa/KSAStructuresMod.cs](https://github.com/LaurensDeV/KSAStructures/blob/c24759358381fb10290c61f9a6980b7d832ba3d4/src/KSAStructures/Ksa/KSAStructuresMod.cs#L139-L172) | GUI hooks calling the UI |

## Snippets

The menu, drawn before KSA's own GUI pass:

```csharp
// src/KSAStructures/Ksa/Ui/Ui.cs L19-L51
    public void DrawMenuBarEntry()
    {
        // ModMenu draws this mod's entry when it is installed; a second would list it twice.
        if (ModMenuPresence.Installed) return;

        try
        {
            if (!ImGui.BeginMainMenuBar()) return;

            if (ImGui.BeginMenu(Title))
            {
                DrawMenuContents();
                ImGui.EndMenu();
            }

            ImGui.EndMainMenuBar();
        }
        catch (Exception e)
        {
            // Inside KSA's own GUI pass; a missing menu is only cosmetic.
            if (_warnedMenuBar) return;

            _warnedMenuBar = true;
            Log.Warn($"menu bar entry failed; V and the lift keys still work: {e.Message}");
        }
    }

    private void DrawMenuContents()
    {
        ImGui.MenuItem("First-person kitten (V)", default, ref settings.FirstPerson, true);
        ImGui.MenuItem("Office garage lamps", default, ref settings.OfficeLamps, true);
        ImGui.MenuItem("Settings window", default, ref Visible, true);
    }
```

The settings window with a stable id and wrapped tooltips:

```csharp
// src/KSAStructures/Ksa/Ui/Ui.cs L79-L109
    public void Draw()
    {
        Current = this;
        if (!Visible) return;

        // ###id so the version can ride in the title without the window losing its place.
        if (ImGui.Begin($"{Title} {Build.Version}###KSAStructures", ref Visible, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Checkbox("First-person kitten (V)", ref settings.FirstPerson);
            Tip("See through the eyes of a kitten on foot. Hold the right mouse button and drag to look around.");

            ImGui.Checkbox("Office garage lamps", ref settings.OfficeLamps);
            Tip("The ceiling lamps under the office tower. Off, the garage has only what daylight reaches it.");

            ImGui.Separator();
            ImGui.TextDisabled("In the lift: 1 garage, 2 lobby, 3 office.");
            ImGui.TextDisabled("In the office chair: Enter plays the computer.");
            ImGui.Separator();

            if (ImGui.Checkbox("Verbose log", ref settings.VerboseLog))
            {
                Log.Threshold = settings.VerboseLog ? Log.Level.Debug : Log.Level.Info;
                Log.Info(settings.VerboseLog ? "verbose logging on" : "verbose logging off");
            }

            Tip("More detail in the log, which is what a bug report wants.");
            ImGui.TextDisabled("  -> Logs/KSAStructures.log");
        }

        ImGui.End();
    }
```

Listing in ModMenu without referencing it:

```csharp
// src/KSAStructures/Ksa/Ui/ModMenuEntry.cs L11-L46
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class ModMenuEntryAttribute(string menuName, string? isModMenuActivePropertyName = null)
    : Attribute
{
    public string MenuName { get; } = menuName;

    public string? IsModMenuActivePropertyName { get; } = isModMenuActivePropertyName;
}

/// <summary>Whether ModMenu is loaded, so this mod does not draw its own menu as well.</summary>
internal static class ModMenuPresence
{
    private static bool? _present;

    /// <summary>
    /// True when ModMenu is in the process. Resolved once: assemblies do not come and go, and this
    /// is asked every frame by the menu-bar draw.
    /// </summary>
    public static bool Installed => _present ??= Detect();

    private static bool Detect()
    {
        try
        {
            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name == "ModMenu") return true;
            }
        }
        catch
        {
            // A mod that cannot tell either way should draw its own menu rather than none.
        }

        return false;
    }
```

## Game APIs touched

| API | Source | Use |
|---|---|---|
| `Program.OnDrawUiFrame`, `OnDrawUiViewports`, `OnDrawUiThreadSafe` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3131-L3241) | Where StarMap's GUI hooks sit and where KSA calls its menu bar |
| `Program.DrawMenuBar`, `DrawProgramMenusHook` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3455-L3487), [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L4005-L4020) | KSA's "Menu Bar" window and the empty hook inside it |
| `ImGui.BeginMainMenuBar`, `BeginMenu`, `MenuItem`, `Begin`, `Checkbox`, `BeginItemTooltip` | Brutal.ImGui | Dear ImGui bindings |

## Pitfalls and version notes

- **KSA's own menu bar is not ImGui's main menu bar.** `Program.DrawMenuBar` is called from
  `OnDrawUiThreadSafe` (after both StarMap GUI hooks) and opens its own window, `ImGui.Begin("Menu Bar")`
  with `ImGuiWindowFlags.MenuBar | NoBackground`, placed at the main viewport's position at full
  width, then `ImGui.BeginMenuBar`. No KSA code calls `BeginMainMenuBar` (it appears only in the
  `Brutal.ImGuiApi` bindings).[^ksa-program] Every build in the assemblies history (2026.9.10.5438,
  2026.9.22.5482, the mod's target 2026.10.7.5541 and 2026.10.10.5554) draws its bar this way, so the
  mod's comment that its entry "sits beside File and Universe" was not true of
  its target build either. A `BeginMainMenuBar` call from a mod creates Dear ImGui's separate
  main-menu-bar window at the same place as KSA's transparent bar; it is not inside KSA's bar and
  KSA's auto-hide setting does not apply to it. The mod's other comment, that `BeginMainMenuBar` would
  return false from `StarMapAfterGui` "because the bar has already been ended", has no basis in KSA
  for the same reason.
- **The supported way into KSA's menu bar** is a postfix on `Program.DrawProgramMenusHook()`, an empty
  public method KSA calls inside its bar, after its own menus and before `EndMenuBar`; several mods use
  it (see the [lifecycle reference](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md)).
  It runs in `OnDrawUiThreadSafe`, so only when the HUD is shown.[^ksa-program]
- **ModMenu detection is by assembly name**, resolved once and cached; a renamed ModMenu assembly
  would produce two entries.
- **Settings are not persisted** across sessions.

## See also

- [Per-mod log file](./mod-log-file.md) - the verbose log switch
- [First-person kitten view](./first-person-kitten-view.md) - the first setting
- [Garage spot lights](./garage-spot-lights.md) - the lamps setting
- [Developer file bridge and MCP server](./dev-file-bridge-and-mcp.md) - setting fields by name
- [Add a menu with BeginMainMenuBar from StarMapBeforeGui, and why it is not KSA's bar](../../../howto/ui/menus/add-items-to-the-game-menu-bar/add-items-to-the-game-menu-bar-2.md) - how-to article demonstrating this feature
- [Copy the ModMenuEntry attribute into your mod so there is no dependency and no extra DLL](../../../howto/ui/menus/list-a-mod-in-modmenu/list-a-mod-in-modmenu-3.md) - how-to article demonstrating this feature
- [Settings window: a menu entry, a small auto-sized window and wrapped tooltips](../../../howto/ui/windows/build-a-settings-window/build-a-settings-window-4.md) - how-to article demonstrating this feature
- [Stable window ids: Label###stable-id for renameable windows and a fixed id for plain windows](../../../howto/ui/windows/subclass-the-stock-imguiwindow/subclass-the-stock-imguiwindow-4.md) - how-to article demonstrating this feature

[^mod-ui]: src/KSAStructures/Ksa/Ui/Ui.cs
[^mod-modmenu]: src/KSAStructures/Ksa/Ui/ModMenuEntry.cs
[^mod-settings]: src/KSAStructures/Sim/Settings.cs
[^mod-entry]: src/KSAStructures/Ksa/KSAStructuresMod.cs
[^mod-bridgecommand]: src/KSAStructures/Sim/BridgeCommand.cs
[^starmap-program]: StarMap.Core/Patches/ProgramPatcher.cs
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
