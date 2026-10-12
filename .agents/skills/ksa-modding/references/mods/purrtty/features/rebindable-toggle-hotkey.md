---
type: Mod Feature
title: Rebindable toggle hotkey with a capture modal
description: A configurable key-plus-modifiers hotkey read from ImGui IO each frame, with a press-to-capture modal, text-field suppression and first-frame swallowing when it opens the terminal
mod: purrtty
category: ui
techniques: [imgui-window, reflection, toml-config, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, reflection Brutal.ImGuiApi.ImGuiIOPtr.KeySuper]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImGuiIOPtr, Brutal.ImGuiApi.ImGuiKey]
tags: [imgui, hotkey, input, modal, settings]
---

The terminal is shown and hidden with one hotkey, F12 by default. From the purrTTY menu the player
opens "Toggle Hotkey", clicks "Capture Now", presses any key combination (Ctrl, Shift, Alt, Super
plus one of about 90 keys) and saves; the binding persists in purrTTY's TOML config. The hotkey is
polled from ImGui IO, not registered with KSA's input bindings, so it works whether or not the 3D
view or a terminal has focus.

## How it works

- `ToggleHotkeyBinding` is a readonly struct: an `ImGuiKey` plus four modifier booleans, a static
  list of capturable keys, display/shortcut formatting, and `FromConfiguration` /
  `WriteToConfiguration` against the shared `ThemeConfiguration` (`ToggleHotkeyKey` stored as the
  enum name; default writes nulls so the config stays clean).[^hotkey]
- `MatchesPress(io)` fires only when no ImGui text field wants input (`io.WantTextInput`), the key
  is pressed this frame with `ImGui.IsKeyPressed(key, repeat: false)`, and all four modifiers match
  exactly. `repeat: false` stops a held key from rapid toggling.
- Super is read through reflection (`typeof(ImGuiIOPtr).GetProperty("KeySuper")`), so the code
  still compiles and runs against a Brutal build without that member.
- The capture modal (`RenderToggleHotkeyModal`, an `ImGui.BeginPopupModal`) loops over the
  capturable keys with `IsKeyPressed` and records the first one together with the live modifier
  state. "Use This" or "Save" applies it, and the binding is written into the controller's live
  config instance before `Save()` (gotcha 6: a second loaded copy would later overwrite it).[^terminal-mod][^gotchas]
- When the hotkey opens the terminal, `TerminalMod` sets `_suppressNextTerminalKeyboardInputFrame`;
  the windows' `KeyboardSuppression` callback consumes it, so a printable hotkey is not also typed
  into the shell.
- The toggle is skipped while the modal is open and while an in-world terminal has keyboard focus
  (so the key reaches that shell instead).

## Lifecycle and timing

- Polled in `TerminalMod.OnAfterUi` (`[StarMapAfterGui]`, `frame:draw-ui`) before the controller
  renders, so a hide takes effect before any key is encoded into a terminal that frame.
- Keys reach ImGui during `Glfw.PollEvents` in `PrepareFrame` and are evaluated by
  `ImGui.NewFrame`, so `IsKeyPressed` is valid anywhere in the UI pass.
- Loaded once at init (`LoadToggleHotkeyFromConfiguration` in `InitializeTerminal`,
  `startup:all-mods-loaded`).
- Because `StarMapAfterGui` is skipped while the HUD is hidden, the toggle does nothing then.

## Key code

| Path | Role |
|---|---|
| [purrTTY.GameMod/ToggleHotkeyBinding.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/ToggleHotkeyBinding.cs#L167-L181) | `MatchesPress` |
| [purrTTY.GameMod/ToggleHotkeyBinding.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/ToggleHotkeyBinding.cs#L128-L165) | Config round-trip |
| [purrTTY.GameMod/ToggleHotkeyBinding.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/ToggleHotkeyBinding.cs#L301-L319) | Reflection read of `KeySuper` |
| [purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L88-L100) | Poll, toggle, first-frame suppression |
| [purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L390-L427) | Capture modal |
| [purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L591-L643) | Capture loop, persist, suppression consumer |
| [purrTTY.Display/Configuration/ThemeConfiguration.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Configuration/ThemeConfiguration.cs) | Stores the binding fields |

## Snippets

```csharp
// purrTTY.GameMod/ToggleHotkeyBinding.cs L167-L181
    public bool MatchesPress(ImGuiIOPtr io)
    {
        // Don't fire while an ImGui text field has focus: a toggle bound to a
        // printable key (e.g. 'T') must not toggle the terminal when that key is
        // typed into the game console or any mod text box. repeat:false stops a
        // held hotkey from rapid-toggling every frame.
        if (io.WantTextInput || !ImGui.IsKeyPressed(Key, repeat: false))
        {
            return false;
        }

        bool super = ImGuiHotkeyHelpers.GetSuperModifier(io);

        return io.KeyShift == Shift && io.KeyCtrl == Ctrl && io.KeyAlt == Alt && super == Super;
    }
```

```csharp
// purrTTY.GameMod/TerminalMod.cs L591-L611
    private void UpdateToggleHotkeyCapture()
    {
        var io = ImGui.GetIO();
        bool super = ImGuiHotkeyHelpers.GetSuperModifier(io);

        foreach (ImGuiKey key in ToggleHotkeyBinding.CapturableKeys)
        {
            if (!ImGui.IsKeyPressed(key))
            {
                continue;
            }

            _lastCapturedToggleHotkey = new ToggleHotkeyBinding(
                key,
                io.KeyShift,
                io.KeyCtrl,
                io.KeyAlt,
                super);
            break;
        }
    }
```

## Game APIs touched

| API | Where | Purpose |
|---|---|---|
| `ImGui.IsKeyPressed(ImGuiKey, bool repeat = true)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8146) | Edge detection without repeat[^ksa-imgui] |
| `ImGuiIOPtr.WantTextInput`, `KeyShift`, `KeyCtrl`, `KeyAlt`, `KeySuper` | [current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs#L144-L186) | Text-field suppression and modifier match[^ksa-imguiio] |
| `ImGui.BeginPopupModal`, `OpenPopup`, `CloseCurrentPopup` | `Brutal.ImGuiApi.ImGui` | Capture modal |

## Pitfalls and version notes

- In build 2026.10.10.5554 `ImGuiIOPtr.KeySuper` exists as a public `ref bool` property, like
  `KeyShift`, so the reflection guard only matters for older Brutal builds; `GetSuperModifier`
  returns `false` when the property is missing. On current builds `io.KeySuper` can be read directly.
- The hotkey is invisible to KSA's own binding system, so nothing prevents a clash with a game
  binding on the same key while the terminal is hidden.
- Always persist through the single live config instance; loading a second `ThemeConfiguration`
  and saving it reverts fields written elsewhere.

## See also

- [Gate game input while a terminal is focused](./gate-game-input-while-terminal-focused.md)
- [Game menu with ModMenu fallback](./game-menu-with-modmenu-fallback.md) (shows the shortcut text)
- [TOML themes and settings](./toml-themes-and-settings.md)
- [purrTTY overview](../overview.md)
- [Rebindable hotkeys: key plus modifiers with a modal capture, stored in TOML](../../../howto/ui/input/rebindable-hotkeys/rebindable-hotkeys-2.md) - how-to article demonstrating this feature

[^hotkey]: purrTTY.GameMod/ToggleHotkeyBinding.cs
[^terminal-mod]: purrTTY.GameMod/TerminalMod.cs
[^gotchas]: purrTTY docs/gotchas.md
[^ksa-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^ksa-imguiio]: Brutal.ImGuiApi.ImGuiIOPtr (decompiled, build 2026.10.10.5554)
