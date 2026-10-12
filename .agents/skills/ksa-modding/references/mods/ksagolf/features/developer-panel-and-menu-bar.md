---
type: Mod Feature
title: Developer panel and menu-bar entry
description: An ImGui panel reached from an entry in ImGui's own main menu bar opened before KSA's GUI pass (a separate bar from KSA's), an optional ModMenu entry through a copied attribute, a floating reopen button, wrapped tooltips and slow-motion controls
mod: ksagolf
category: ui
techniques: [imgui-window, imgui-menu, starmap-lifecycle, reflection]
hooks: [starmap StarMapBeforeGui, starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, KSA.Universe, KSA.SimSpeed, KSA.Program]
tags: [imgui, panel, menu-bar, modmenu, tooltips, sim-speed, developer-tools]
---

KSAGolf's single window ("KSAGolf <version>") holds the putting controls, a "Build a course"
button, a world-clock row with slow-motion buttons down to 0.01x, a craft mover toggle, a "send the
controlled craft to another body" row, the "Verbose log" switch and, on a developer install only, a
"Capture for Claude" button. It opens from a `KSAGolf` menu in a main menu bar the mod opens itself
(ImGui's own `##MainMenuBar`, a different window from KSA's "Menu Bar", so the entry does not join
KSA's File and Universe menus), from ModMenu's shared "Mods" menu when that mod is installed, or
from a small floating button that appears when the panel is closed.

The reusable parts are the
zero-dependency ModMenu integration (a copied attribute matched by name), the "never strand the
player" floating button, and the house style for controls and tooltips. The menu-bar entry is a
cautionary example: the code comment says it sits "beside File and Universe", but KSA's bar is not
ImGui's main menu bar (see Pitfalls); a mod that wants a menu inside KSA's own bar postfixes
`Program.DrawProgramMenusHook` instead.

## How it works

| Element | Mechanism |
|---|---|
| Menu-bar entry | `[StarMapBeforeGui]` calls `ImGui.BeginMainMenuBar()` / `BeginMenu("KSAGolf")` / `MenuItem("Panel", ref visible)`; this opens ImGui's own main-menu-bar window, not KSA's "Menu Bar" window; skipped when ModMenu is loaded so the mod is not listed twice; failures are swallowed with one warning[^golf-ui] |
| ModMenu entry | A local copy of `ModMenuEntryAttribute(string menuName, ...)` on a static `DrawModMenu()`; ModMenu (when present) finds it by attribute type name, so there is no assembly reference[^golf-modmenuentry] |
| ModMenu detection | `AppDomain.CurrentDomain.GetAssemblies()` scanned once for an assembly named `ModMenu`[^golf-modmenuentry] |
| Panel | `ImGui.Begin($"KSAGolf {Build.Version}###KSAGolf", ref Visible)`; the `###` id keeps window position and size stable across version changes |
| Floating button | When the panel is closed and `Config.FloatingPanelButton` is on, an auto-resizing title-less window with one `KSAGolf` button reopens it |
| Tooltips | `Tip(text)` uses `ImGui.BeginItemTooltip()` with `PushTextWrapPos(GetFontSize() * 35)`, so explanations wrap like Dear ImGui's help markers |
| Slow motion | Buttons call `Universe.SetSimulationSpeed(new SimSpeed(x))` with 0.01x to 1x; KSA's own control stops at 0.1x but the engine has no floor, only `SimSpeed.MaxSpeed`[^golf-ksaworld] |
| Send to body | `KsaWorld.TryPlaceOnSurface(craft, bodyId, 0, 0)`, which wraps `Vehicle.TeleportToLocation` (see [Craft mover](craft-mover.md)) |
| Verbose log | Sets `Log.Threshold` to Debug or Info at runtime (see [Mod log file with rotation](mod-log-file-with-rotation.md)) |
| Developer controls | Only when `Build.Developer` (a `developer` file beside the DLL); see [File command bridge](file-command-bridge.md) |

Settings live in `Sim/Config.cs` as public fields so the panel can bind them by `ref`; they are not
written to disk and reset each session.[^golf-config]

The repo's own UI rules (CLAUDE.md "The panel"): one field gets one control; a control that opens a
window is a button, never a tick box; an explanation is a tooltip, and the only inline text under a
control is live state the player has to act on.[^golf-claude-md]

## Lifecycle and timing

| Hook | Where it fires | Used for |
|---|---|---|
| `StarMapBeforeGui` | Harmony prefix on `Program.OnDrawUiFrame`, inside `if (DrawUI)` in `Program.OnFrame`[^starmap-programpatcher][^ksa-program] | The mod's own main-menu-bar entry, opened before any KSA window this frame |
| `StarMapAfterGui` | Harmony postfix on `Program.OnDrawUiViewports`, same `DrawUI` guard | The panel, after the mod has stepped its simulation for the frame so the panel reads this frame's state |

Both run after `Program.PrepareImGui` (`ImGui.NewFrame`) and before `ImGui.Render`, on the main
thread, and both are skipped while the HUD is hidden with F2 (`DrawUI == false`). Both also run
after `Program.PrepareFrame` has dispatched the next physics step, which does not matter here: the
panel writes only mod state and `Universe.SetSimulationSpeed`, a main-thread field that
`GetJobSimStep` reads at the next dispatch. KSA itself draws its menu bar later in the same frame, in
`OnDrawUiThreadSafe` -> `DrawMenuBar`, as a separate window, so drawing "before KSA fills the bar"
does not place the mod's menu in it.[^ksa-program]

## Key code

| Path | Role |
|---|---|
| [src/KSAGolf/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Ui/Ui.cs) | Menu-bar entry, ModMenu callback, panel, builder window, tooltips |
| [src/KSAGolf/Ksa/Ui/ModMenuEntry.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/Ui/ModMenuEntry.cs) | Copied `ModMenuEntryAttribute` and `ModMenuPresence` |
| [src/KSAGolf/Ksa/KSAGolfMod.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Ksa/KSAGolfMod.cs#L147-L189) | `OnBeforeGui` and `OnAfterGui` |
| [src/KSAGolf/Sim/Config.cs](https://github.com/LaurensDeV/KSAGolf/blob/f3e6ab72b5b963f615cb4ed41f7765a7bce42688/src/KSAGolf/Sim/Config.cs) | Session settings bound by `ref` |

## Snippets

```csharp
// src/KSAGolf/Ksa/Ui/Ui.cs L27-L52
    public void DrawMenuBarEntry()
    {
        // ModMenu draws this mod's entry when it is installed; a second would list it twice.
        if (ModMenuPresence.Installed) return;

        try
        {
            if (!ImGui.BeginMainMenuBar()) return;

            if (ImGui.BeginMenu("KSAGolf"))
            {
                DrawMenuContents();
                ImGui.EndMenu();
            }

            ImGui.EndMainMenuBar();
        }
        catch (Exception e)
        {
            // An exception here happens inside KSA's own GUI pass; a missing menu is only cosmetic.
            if (_warnedMenuBar) return;

            _warnedMenuBar = true;
            Log.Warn($"menu bar entry failed, use the floating button: {e.Message}");
        }
    }
```

```csharp
// src/KSAGolf/Ksa/Ui/ModMenuEntry.cs L19-L26
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class ModMenuEntryAttribute(string menuName, string? isModMenuActivePropertyName = null)
    : Attribute
{
    public string MenuName { get; } = menuName;

    public string? IsModMenuActivePropertyName { get; } = isModMenuActivePropertyName;
}
```

```csharp
// src/KSAGolf/Ksa/Ui/Ui.cs L93-L104, L111-L126
        if (Visible)
        {
            // ###id so the version can ride in the title without the window losing its place.
            if (ImGui.Begin($"KSAGolf {Build.Version}###KSAGolf", ref Visible))
            {
                if (Build.Developer)
                {
                    DrawCaptureForClaude();
                    ImGui.Separator();
                }

                DrawGolf();
                // ...
                DrawLogging();
            }

            ImGui.End();
        }
        else if (_config.FloatingPanelButton)
        {
            // Closing the panel must not strand the player: both other routes back are somebody
            // else's, and the setting that would switch this on lives inside the shut panel.
            if (ImGui.Begin("KSAGolf##reopen", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
            {
                if (ImGui.Button("KSAGolf")) Visible = true;
            }

            ImGui.End();
        }
```

```csharp
// src/KSAGolf/Ksa/Ui/Ui.cs L321-L332
    private static void Tip(string text)
    {
        if (!ImGui.BeginItemTooltip()) return;

        ImGui.PushTextWrapPos(ImGui.GetFontSize() * TipWidthEms);
        ImGui.TextWrapped(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    // The width Dear ImGui's own help markers wrap at.
    private const float TipWidthEms = 35f;
```

## Game APIs touched

| API | Decomp | Purpose |
|---|---|---|
| `ImGui.BeginMainMenuBar`, `BeginMenu`, `MenuItem`, `Begin`, `BeginItemTooltip` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs) | Menu, panel, tooltips |
| `Program.OnDrawUiFrame` / `OnDrawUiViewports` / `DrawUI` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3131-L3176) | Where StarMap's GUI hooks land |
| `Program.DrawMenuBar` / `DrawProgramMenusHook` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3455-L4020) | KSA's own bar: `ImGui.Begin("Menu Bar", ... MenuBar)` then `BeginMenuBar`; the empty public `DrawProgramMenusHook()` is called inside it, the place to postfix for a menu in KSA's bar (not used by this mod) |
| `Universe.SetSimulationSpeed(SimSpeed)` / `SimulationSpeed` | [current/decomp/KSA/Universe.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Universe.cs) | Slow-motion buttons and the speed readout |

## Pitfalls and version notes

- **The entry does not join KSA's menu bar.** KSA does not use ImGui's main menu bar, in the build
  the mod targets (2026.9.22.5482) or in 2026.10.10.5554: `Program.DrawMenuBar` opens a regular
  window named "Menu Bar" with the `MenuBar` flag and calls `ImGui.BeginMenuBar` inside it.
  `BeginMainMenuBar` creates ImGui's own, separate `##MainMenuBar` window at the top of the main
  viewport, so the mod's `KSAGolf` menu sits in a second bar under or over KSA's rather than beside
  File and Universe as its comments say, and opening it "before KSA fills the bar" changes nothing.
  To add a menu to KSA's bar, postfix `Program.DrawProgramMenusHook()`, which KSA calls between its
  own menus and `EndMenuBar` in both builds. The mod's `docs/BLOCKED-ON-KSA.md` says KSA's bar has
  "no extension point of any kind" and that `BeginMainMenuBar` "appends to the bar"; the first is
  contradicted by `DrawProgramMenusHook`, the second by the separate windows.[^ksa-program][^golf-ui][^golf-blocked]
- The mod's comment says calling `BeginMainMenuBar` from `StarMapAfterGui` returns false because the
  bar has already been ended for the frame. The decomp cannot confirm it (Dear ImGui is native), and
  since the bar is the mod's own window rather than KSA's, KSA's `EndMenuBar` is not what would end
  it; treat the claim as unverified.[^golf-ui][^golf-blocked]
- Both GUI hooks vanish with F2, so nothing that must run every frame belongs in them (the mod's
  simulation step has a fallback; see [Dual-hook frame stepping](dual-hook-frame-stepping.md)).
- ModMenu is not among the analysed repositories here; the "matched by type name" contract comes
  from the mod's own comments.[^golf-modmenuentry]
- `Config` is not persisted, so every session starts with the defaults.[^golf-config]

## See also

- [Mod log file with rotation and a verbose toggle](mod-log-file-with-rotation.md)
- [File command bridge and MCP server](file-command-bridge.md)
- [Craft mover](craft-mover.md)
- [Top-down minigolf course builder](minigolf-course-builder.md)
- [Putting session with a mouse-swung club](putting-session.md)
- [Add a menu with BeginMainMenuBar from StarMapBeforeGui, and why it is not KSA's bar](../../../howto/ui/menus/add-items-to-the-game-menu-bar/add-items-to-the-game-menu-bar-2.md) - how-to article demonstrating this feature
- [Copy the ModMenuEntry attribute into your mod so there is no dependency and no extra DLL](../../../howto/ui/menus/list-a-mod-in-modmenu/list-a-mod-in-modmenu-3.md) - how-to article demonstrating this feature
- [Settings window: a menu entry, a small auto-sized window and wrapped tooltips](../../../howto/ui/windows/build-a-settings-window/build-a-settings-window-4.md) - how-to article demonstrating this feature
- [Settings window: a developer panel opened from a menu, ModMenu or a floating button](../../../howto/ui/windows/build-a-settings-window/build-a-settings-window-6.md) - how-to article demonstrating this feature

[^golf-ui]: src/KSAGolf/Ksa/Ui/Ui.cs
[^golf-modmenuentry]: src/KSAGolf/Ksa/Ui/ModMenuEntry.cs
[^golf-config]: src/KSAGolf/Sim/Config.cs
[^golf-ksaworld]: src/KSAGolf/Ksa/KsaWorld.cs
[^golf-blocked]: docs/BLOCKED-ON-KSA.md
[^golf-claude-md]: CLAUDE.md
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^starmap-programpatcher]: StarMap.Core/Patches/ProgramPatcher.cs
