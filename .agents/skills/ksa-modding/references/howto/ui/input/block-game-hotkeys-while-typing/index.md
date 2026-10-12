# Game Feature

* [Block game hotkeys while typing into an ImGui field](overview.md) - Why KSA bindings fire while a mod text box has focus, the key and mouse dispatch chain they travel, and two ways mods stop them

# How-To

* [Block game hotkeys while typing: gate game input while a terminal has focus](block-game-hotkeys-while-typing-2.md) - Keep all game input away from a focused window that has no text widget by prefixing Program.OnKey, GameSettings.OnKeyAll and DispatchKeyEvent behind one flag, with a held-key model that never leaks a toggle
* [Block game hotkeys while typing: prefix on GameSettings.OnKeyAll when ImGui wants text input](block-game-hotkeys-while-typing-1.md) - Stop KSA bindings from firing while any ImGui text box is active with a one-method Harmony prefix that reports the key as handled
