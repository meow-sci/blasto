# Game Feature

* [Implement rebindable hotkeys with ImGui key state](overview.md) - How mods bind, poll and re-capture hotkeys from ImGui key state instead of KSA's input bindings, with enum-name JSON and key-plus-modifier TOML variants

# How-To

* [Rebindable hotkeys: enum names in JSON and a capture loop over the ImGuiKey range](rebindable-hotkeys-1.md) - Poll ImGuiKey hotkeys with IsKeyPressed outside text input, store them as enum names in JSON and capture a new key with a press-a-key button
* [Rebindable hotkeys: key plus modifiers with a modal capture, stored in TOML](rebindable-hotkeys-2.md) - Bind a hotkey as an ImGuiKey plus Ctrl, Shift, Alt and Super, capture it in a modal popup, persist it in the mod's TOML config and keep it from also typing into the shell
