# Game Feature

* [Make mod UI look like the game: console widgets, window skin and global theme](overview.md) - How KSA's console style is built from public ConsoleStyle and ConsoleWidgets helpers, and four ways mods use them or the global ImGuiStyle to match or re-theme the UI

# How-To

* [Match the stock UI: a plain ImGui.Begin window plus PushWidgetStyle, with custom tab buttons](match-the-stock-console-ui-style-2.md) - Skin a normal saved-placement ImGui window with ConsoleStyle.PushWidgetStyle and ConsoleWidgets, avoiding ConsoleStyle.BeginWindow, and paint tab buttons in the live title-bar colour
* [Match the stock UI: ConsoleStyle window skin unpinned from the main viewport and mirrored row helpers](match-the-stock-console-ui-style-3.md) - Wrap ConsoleStyle.BeginWindow with pinToMainViewport false so a console-skinned panel can leave the game window, and re-create the Transfer Planner's private row helpers on the public ConsoleWidgets
* [Match the stock UI: ConsoleWidgets rows inside an ImGuiWindow subclass without console chrome](match-the-stock-console-ui-style-1.md) - Use the game's public ConsoleWidgets and ConsoleStyle helpers for label-and-control rows, tooltips, buttons, headers and status text in a mod window that keeps the plain ImGuiWindow chrome
* [Re-theme every KSA and mod window through the global ImGuiStyle (capture, restore and TOML presets)](match-the-stock-console-ui-style-4.md) - Change the look of the whole game UI by writing the global ImGuiStyle, capturing the original at startup, restoring it at unload and storing themes as TOML
