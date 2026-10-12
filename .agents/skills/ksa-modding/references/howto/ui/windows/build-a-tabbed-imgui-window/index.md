# Game Feature

* [Organise a mod window: tabs, button tabs, tables and pop-out sections](overview.md) - KSA gives mods plain Dear ImGui for tabs, so five mods show five structures - a real tab bar, a per-craft window with panes, hand-drawn tab buttons, button tabs over a table, and sections that pop out into windows

# How-To

* [Tabbed window: a closeable ImGui tab bar with programmatic selection](build-a-tabbed-imgui-window-1.md) - Use BeginTabBar with reorderable, closeable tabs bound to your own session list, force the selected tab from code and ignore the stale selection report after a change
* [Tabbed window: a per-craft window with tabs, pop-out panes and a reopen button](build-a-tabbed-imgui-window-2.md) - Structure a mod UI as independent top-level windows, one per craft with a tab bar, plus pop-out panes toggled by buttons and a recovery button when the main window is closed
* [Tabbed window: button tabs driving a table of collapsing headers](build-a-tabbed-imgui-window-4.md) - Build a settings window whose tabs are a row of disabled-when-active buttons under a scrolling child, and whose main page is a three-column table with one collapsing header per effect
* [Tabbed window: collapsible sections that pop out into their own windows](build-a-tabbed-imgui-window-5.md) - Draw each panel section from one delegate that runs either docked inside the main window or inside its own window, with open and popped state keyed by a stable section id
* [Tabbed window: title-bar-coloured custom tab buttons](build-a-tabbed-imgui-window-3.md) - Draw hand-rolled tab buttons that match the stock console palette and follow the live title-bar colour, instead of using ImGui's tab bar
