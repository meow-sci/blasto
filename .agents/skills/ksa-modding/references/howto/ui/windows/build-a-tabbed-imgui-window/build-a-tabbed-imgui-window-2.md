---
type: How-To
title: "Tabbed window: a per-craft window with tabs, pop-out panes and a reopen button"
description: Structure a mod UI as independent top-level windows, one per craft with a tab bar, plus pop-out panes toggled by buttons and a recovery button when the main window is closed
category: ui
group: windows
topic: build-a-tabbed-imgui-window
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/ksarmory/features/operator-panel-imgui]
techniques: [imgui-window, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, starmap StarMapBeforeGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImGuiTabBarFlags]
tags: [tabs, pop-out, panes, reopen, windows, operator-panel, stable-id]
---
KSArmory's whole operator interface is Dear ImGui, drawn every frame from the `[StarMapAfterGui]` hook right after the simulation step. It is not one big window: it is a set of independent top-level windows - a craft switcher, a per-craft window with tabs, a weapons window, and pop-out session and debug panes. This article is the window structure: one `Begin`/`End` per window each with its own open flag
and a `###id`, a tab bar inside the per-craft window, a table-driven list of pop-out panes, and a button that brings the panel back when it was closed. Demonstrated by KSArmory. Prerequisite: StarMap.

## Background: how the game does it

Nothing in KSA is required beyond ImGui: each window is a `Begin`/`End` pair with its own open flag (the `ref bool` passed to `Begin`), and the mod's rule is that every window is submitted after the previous one's `End` - none is nested inside another's. The tab bar is the same API as [approach 1](build-a-tabbed-imgui-window-1.md).
KSA draws its own menu bar as a separate window named `Menu Bar` after both StarMap GUI hooks; mods add menu entries in other ways (see [approach 1 of the window base class](../subclass-the-stock-imguiwindow/subclass-the-stock-imguiwindow-1.md)).[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3486)[^ksa-program]

## Lifecycle and timing

| Hook | Firing point (build 2026.10.10.5554) | What runs |
|---|---|---|
| `[StarMapBeforeGui]` | Harmony prefix on `Program.OnDrawUiFrame` (`frame:draw-ui`), inside `if (DrawUI)` | The menu entry |
| `[StarMapAfterGui]` | Harmony postfix on `Program.OnDrawUiViewports`, inside `if (DrawUI)` | `StepOnce` first, then `_ui.Draw()`, then the world overlays |

Both sit between `PrepareImGui` and `ImGui.Render()`. The panel vanishes with the HUD (F2); the simulation keeps stepping from fallback hooks but nothing in the panel is drawn or clickable. A button pressed here changes state that the next frame's step consumes. Submission order is layering: the panel is submitted before the full-screen overlays. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and
[keep UI running when the HUD is hidden](../../overlays/draw-ui-while-the-hud-is-hidden/overview.md).

## Steps

1. **Draw the main window with a version-in-title `###id`, and a reopen button when it is closed.** Closing the panel must not strand the operator with no way back; the button is the one route the mod controls. It is a tiny auto-sized, title-less window:

```csharp
// src/KSArmory/Ksa/Ui/Ui.cs L260-L294
if (Visible)
{
    // ###id so the version can ride in the title without the window losing its place
    // every time the mod is bumped.
    if (ImGui.Begin($"KSArmory {Build.Version}###KSArmory", ref Visible))
    {
        // Opens on what exists in the world rather than on whatever the camera is pointed
        // at. Everything below is about the *selected* system, and everything that is not
        // about one particular system is a pane.
        DrawSystemList();
        ImGui.Separator();
        DrawReportFooter();
    }

    ImGui.End();
}
else if (_config.FloatingPanelButton)
{
    // Closing the panel must not strand the operator with no way back, and the button is
    // the only route this mod controls. Both others are somebody else's: appending to
    // KSA's bar is ImGui behaviour rather than a supported hook, and ModMenu's entry is
    // another mod's menu reached by transpiling a private method.
    //
    // Drawn whenever it is wanted rather than being suppressed where ModMenu would
    // provide a route: the recovery cannot be conditional on a third party working,
    // because there is no way back from being wrong -- the setting that would switch this
    // on lives *inside* the panel that is shut.
    if (ImGui.Begin("KSArmory##reopen",
                    ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar))
    {
        if (ImGui.Button("KSArmory")) Visible = true;
    }

    ImGui.End();
}
```

2. **Draw every other window independently**, none nested, each gated only on its own flag, so closing the panel is not a request to shut the map or a half-written report:

```csharp
// src/KSArmory/Ksa/Ui/Ui.cs L296-L307
    // Each of these is its own top-level window, so none may be nested inside the panel's
    // Begin/End -- and none is gated on the panel being open. Every one carries its own open
    // flag and returns on it, so closing the panel is not a request to shut the map, the
    // scope, a report half-written, or the switcher the trigger is pointed at. The manage
    // window is the only one gated at all, on there being something to manage.
    if (anyCrewed) DrawManageWindow();
    DrawPanes();
    DrawReportWindow();
    DrawMapWindow();
    DrawScopeWindow();
    DrawWeaponsWindow();
}
```

3. **One window per craft with a tab bar.** Point the shared fields at this craft before any tab body runs (tab bodies run only for the selected tab). Title is `"{craft}###KSArmorySystem"`, so the window keeps its place while the visible part changes; the tab bodies are gated on what the craft has:

```csharp
// src/KSArmory/Ksa/Ui/Ui.cs L781-L798
bool open = true;

// The title is the craft's identity, so the pane below it does not repeat either half.
// ###id keeps the window in place while the visible part changes.
string flying = ReferenceEquals(craft, KsaWorld.ControlledVehicle) ? "" : " - not flying";

if (ImGui.Begin($"{KsaWorld.DisplayName(craft)}{flying}###KSArmorySystem", ref open))
{
    // Above the tabs, so it is on screen whichever one is open. What it carries is the
    // answer to why the system is or is not shooting, which is the question most often
    // asked while looking at some other tab.
    if (armed) DrawSystemHeader();

    if (ImGui.BeginTabBar("##systemtabs"))
    {
        // First, and the only one a craft always has: what it is made of. Every other tab
        // is about a weapons system, which a craft carrying one director does not have.
        if (ImGui.BeginTabItem("Components")) { DrawComponents(craft); ImGui.EndTabItem(); }
```
```csharp
// src/KSArmory/Ksa/Ui/Ui.cs L820-L834
                if (scope is not null && ImGui.BeginTabItem(scope))
                {
                    DrawScope();
                    DrawTrackList();
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Tuning")) { DrawTuning(); ImGui.EndTabItem(); }
            }
            ImGui.EndTabBar();
        }
    }
    ImGui.End();

    if (!open) _managed = null;
}
```

4. **Pop-out panes as data.** A class with a title, a body delegate, a group and an open flag (a class so `Open` is shared with the button that toggles it); a lazily built array in button order; a loop that begins each open pane (its title doubles as its ImGui id, so position and size persist) and calls its body:

```csharp
// src/KSArmory/Ksa/Ui/Ui.cs L74-L107
// What a pane is about. Anything belonging to one installation is a tab in that system's own
// window; Debug is for whoever is working on the mod rather than playing with it.
private enum PaneGroup { Session, Debug }

// One pop-out window: what it is called, whether it is open, and what it draws. A class
// rather than a struct so Open is shared with the button that toggles it.
private sealed class Pane(string title, Action body, PaneGroup group)
{
    public readonly string Title = title;
    public readonly Action Body = body;
    public readonly PaneGroup Group = group;

    public bool Open;
}

private Pane[]? _panes;

// Built lazily because every Body is an instance method. Order is the order the buttons
// appear in, which runs roughly from what an operator touches most to what they touch once.
private Pane[] Panes => _panes ??=
[
    new("KSArmory settings", DrawSettingsPane, PaneGroup.Session),

    // Who fights whom, in one place: a team's rules are every system on it.
    new("Teams", DrawTeamsWindow, PaneGroup.Session),

    // Its own button beside settings, rather than a collapsed header inside that window. Sim
    // speed, the target spawner and the log are all reached *while* an engagement is running,
    // and a fold two windows deep is not a place to keep those.
    new("Debug tools", DrawDebugPane, PaneGroup.Session),

    new("Test targets", DrawTestTargets, PaneGroup.Debug),
    new("Log", DrawLog, PaneGroup.Debug),
];
```
```csharp
// src/KSArmory/Ksa/Ui/Ui.cs L917-L929
private void DrawPanes()
{
    for (int i = 0; i < Panes.Length; i++)
    {
        Pane pane = Panes[i];
        if (!pane.Open) continue;

        // Title doubles as the ImGui id, so each pane keeps its own size and position
        // across sessions the way any other window does.
        if (ImGui.Begin(pane.Title, ref pane.Open)) pane.Body();
        ImGui.End();
    }
}
```

5. **Buttons that toggle panes** show their state ("(open)" and a green tint) and flip `pane.Open`.

## Complete example

| File | Role |
|---|---|
| [src/KSArmory/Ksa/Ui/Ui.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/Ui.cs) | Window inventory, `Draw`, per-craft window with tabs, `Pane` records |
| [src/KSArmory/Ksa/Ui/UiSystem.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiSystem.cs) | Status panes |
| [src/KSArmory/Ksa/Ui/UiTuning.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiTuning.cs) | Live sliders on shared profiles |
| [src/KSArmory/Ksa/Ui/UiSession.cs](https://github.com/LaurensDeV/KSArmory/blob/cbd63d6d84da7a48a13641a641d3557bad216435/src/KSArmory/Ksa/Ui/UiSession.cs) | Session settings pane |

## Variations and gotchas

- **Tab bodies run only for the selected tab.** Do not fill state another window reads from inside a tab body; fill it before the windows are drawn.
- **A craft switch must never edit the wrong system.** `Focus` is re-run inside the window that is drawing, because the top-of-frame focus came from last frame's selection.
- **Gate recovery on nothing.** The reopen button is drawn whenever wanted, not only where ModMenu would provide a route; the setting that would switch it on lives inside the panel that is shut.
- **Order tabs by what a craft always has.** The Components tab is first and the only unconditional one.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of build-a-tabbed-imgui-window](overview.md)
- [Approach 1: closeable ImGui tab bar](build-a-tabbed-imgui-window-1.md)
- [Approach 5: collapsible sections that pop out](build-a-tabbed-imgui-window-5.md)
- [Stable window ids](../subclass-the-stock-imguiwindow/subclass-the-stock-imguiwindow-4.md)
- [List a mod in ModMenu with the ModMenuEntry attribute](../../menus/list-a-mod-in-modmenu/overview.md)
- Mod feature: [Operator panel (ImGui)](../../../../mods/ksarmory/features/operator-panel-imgui.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
