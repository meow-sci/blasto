---
type: How-To
title: "Tabbed window: a closeable ImGui tab bar with programmatic selection"
description: Use BeginTabBar with reorderable, closeable tabs bound to your own session list, force the selected tab from code and ignore the stale selection report after a change
category: ui
group: windows
topic: build-a-tabbed-imgui-window
approach: 1
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/purrtty/features/terminal-window-chrome-tabs-and-resize-snap]
techniques: [imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImGuiTabBarFlags]
tags: [tabs, tab-bar, closeable, set-selected, reorderable, imgui]
---
purrTTY's terminal windows have one shell session per tab. The tabs are a real ImGui tab bar (reorderable, closeable, scrolling when there are too many) bound to the window's session list, and two details make it work with state the mod owns: the tab selected by ImGui can be forced from code when a session is created or closed, and the
one-frame stale "previous tab is selected" report must be ignored on that frame. The bar is hidden entirely when there is only one session. Demonstrated by purrTTY. Prerequisite: StarMap.

## Background: how the game does it

Nothing in KSA is involved; this is the Brutal ImGui binding. `ImGui.BeginTabBar(id, ImGuiTabBarFlags)` / `EndTabBar()` bracket a bar, `ImGui.BeginTabItem(label, ref open, ImGuiTabItemFlags)` returns true for the selected tab and a close button sets `open` false when passed by reference; `ImGuiTabItemFlags.SetSelected` forces selection for
one frame. `ImGuiTabBarFlags` has `Reorderable`, `AutoSelectNewTabs` and `FittingPolicyScroll`.[current/decomp/Brutal.ImGuiApi/ImGuiTabBarFlags.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiTabBarFlags.cs#L6-L20)[^brutal-imguiapi-imguitabbarflags][current/decomp/Brutal.ImGuiApi/ImGuiTabItemFlags.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiTabItemFlags.cs#L5-L12)[^brutal-imguiapi-imguitabitemflags] KSA's own `ConsoleStyle.PushWidgetStyle` colours tabs (`Tab`, `TabHovered`, `TabSelected` in `ActiveTool`) if you push it.[current/decomp/KSA/ConsoleStyle.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ConsoleStyle.cs#L575-L650)[^ksa-consolestyle]

## Lifecycle and timing

Drawn in the mod's `[StarMapAfterGui]` method (`frame:draw-ui`): `TerminalMod.OnAfterUi` calls the controller's `Render`, which renders each `TerminalWindow`, which calls `RenderTabBar` between the title strip and the grid. ImGui reports the selected tab from the previous frame's tab bar state, which is why the stale-report guard is
needed. While the HUD is hidden the hook does not run. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Own the list and the active session.** The tab bar is a view: `Sessions.Sessions` is the list and `Sessions.ActiveSession` the selection. Detect an external change of active session (a tab created, the active tab closed) by comparing with last frame's id.
2. **Draw the bar** only when there are two or more sessions, with the flags above. For each session emit one `BeginTabItem` whose label ends in a stable per-session id (`##tab_<guid>`), pass `ref open` to get a close button, and pass `SetSelected` for the active session on the frame it changed:

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.cs L609-L666
private void RenderTabBar(bool showChrome)
{
    var sessions = Sessions.Sessions;
    var active = Sessions.ActiveSession;
    bool activeChanged = active?.Id != _lastActiveSessionId;
    _lastActiveSessionId = active?.Id;

    if (sessions.Count <= 1)
    {
        return;
    }

    // While chrome is hidden the tab bar stays laid out (so the grid height
    // is stable) but is rendered fully transparent. It becomes visible the
    // moment the mouse hovers the window.
    if (!showChrome)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0f);
    }

    if (ImGui.BeginTabBar("##session_tabs",
            ImGuiTabBarFlags.Reorderable | ImGuiTabBarFlags.AutoSelectNewTabs | ImGuiTabBarFlags.FittingPolicyScroll))
    {
        for (int i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            bool isActive = active != null && session.Id == active.Id;
            var tabFlags = isActive && activeChanged ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            bool open = true;
            if (ImGui.BeginTabItem(TabLabel(session), ref open, tabFlags))
            {
                // The !activeChanged guard is load-bearing: on the frame
                // after an external active-session change (new tab created,
                // active closed), ImGui still reports the *previous* tab as
                // selected — switching on that stale report would instantly
                // revert the change the SetSelected flag above is applying.
                if (!isActive && !activeChanged)
                {
                    Sessions.SwitchToSession(session.Id);
                }

                ImGui.EndTabItem();
            }

            if (!open)
            {
                CloseSession(session.Id);
            }
        }

        ImGui.EndTabBar();
    }

    if (!showChrome)
    {
        ImGui.PopStyleVar();
    }
}
```

3. **Apply user selection only when it is the user's.** The `!activeChanged` guard is load-bearing: on the frame after an external change, ImGui still reports the previous tab as selected, and switching on that stale report would revert the change `SetSelected` is applying.
4. **Close on `!open`.** When the user clicks a tab's X, close the session; closing is deliberately synchronous on the render thread here because it disposes a native surface the render thread ticks.
5. **Cache the labels.** Composing `title + guid` strings per tab per frame is measurable garbage; cache per session id and rebuild only when the title or exit code changes:

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.cs L668-L687
// Composed tab labels are cached per session: title + guid formatting per
// tab per frame is measurable garbage on the render path.
private readonly Dictionary<Guid, (string Title, int? ExitCode, string Label)> _tabLabels = new();

private string TabLabel(TerminalSession session)
{
    string title = session.Title;
    int? exitCode = session.ProcessManager.ExitCode;
    if (_tabLabels.TryGetValue(session.Id, out var cached)
        && cached.Title == title && cached.ExitCode == exitCode)
    {
        return cached.Label;
    }

    string label = exitCode is { } code
        ? $"{title} (exit {code})##tab_{session.Id}"
        : $"{title}##tab_{session.Id}";
    _tabLabels[session.Id] = (title, exitCode, label);
    return label;
}
```

6. **Keep the bar in layout when hiding it.** To fade chrome without changing the grid size, push `ImGuiStyleVar.Alpha = 0` around the bar instead of skipping it (see [chrome-less windows](../subclass-the-stock-imguiwindow/subclass-the-stock-imguiwindow-5.md)).

## Complete example

| File | Role |
|---|---|
| [purrTTY.Display/Ghostty/TerminalWindow.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.cs#L609-L687) | `RenderTabBar` and cached labels |
| [purrTTY.Display/Ghostty/GhosttyTerminalController.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/GhosttyTerminalController.cs) | `OpenWindow` and `OpenTab`; the window list |
| [purrTTY.Terminal/Sessions/SessionManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Sessions/SessionManager.cs) | Sessions that back the tabs |

## Variations and gotchas

- **`##` in the tab label keeps tabs distinct but a changing title changes the id** (the visible part is before `##`, the identity includes it); purrTTY's labels embed the title before `##tab_<guid>`, so a title change makes ImGui treat the tab as new, which can reset its position in a reorderable bar (inferred from ImGui's id rule; not tested in game). Use `###tab_<guid>` for an identity that survives title changes.
- **Reorderable bars** reorder ImGui's internal order, not your list; if order matters, read it back.
- **Hide when one tab.** A single-tab bar costs height for nothing.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of build-a-tabbed-imgui-window](overview.md)
- [Approach 3: title-bar-coloured custom tab buttons](build-a-tabbed-imgui-window-3.md)
- [Chrome-less plain-ImGui window](../subclass-the-stock-imguiwindow/subclass-the-stock-imguiwindow-5.md)
- Mod feature: [Terminal window chrome, tabs and resize snap](../../../../mods/purrtty/features/terminal-window-chrome-tabs-and-resize-snap.md)

[^brutal-imguiapi-imguitabbarflags]: Brutal.ImGuiApi.ImGuiTabBarFlags (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imguitabitemflags]: Brutal.ImGuiApi.ImGuiTabItemFlags (decompiled, build 2026.10.10.5554)
[^ksa-consolestyle]: KSA.ConsoleStyle (decompiled, build 2026.10.10.5554)
