---
type: How-To
title: "Rebindable hotkeys: key plus modifiers with a modal capture, stored in TOML"
description: Bind a hotkey as an ImGuiKey plus Ctrl, Shift, Alt and Super, capture it in a modal popup, persist it in the mod's TOML config and keep it from also typing into the shell
category: ui
group: input
topic: rebindable-hotkeys
approach: 2
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/purrtty/features/rebindable-toggle-hotkey]
techniques: [toml-config, imgui-window, reflection, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, reflection Brutal.ImGuiApi.ImGuiIOPtr.KeySuper]
lifecycle: [frame:draw-ui, startup:all-mods-loaded]
game_types: [Brutal.ImGuiApi.ImGuiKey, Brutal.ImGuiApi.ImGuiIOPtr]
tags: [hotkeys, imgui, modifiers, capture, modal, toml, rebinding]
---
purrTTY shows and hides its terminal with one hotkey, F12 by default. From the purrTTY menu the player opens "Toggle Hotkey", clicks "Capture Now", presses any key combination (Ctrl, Shift,
Alt, Super plus one of about 90 keys) and saves; the binding persists in purrTTY's TOML config. The hotkey is polled from ImGui IO, not registered with KSA's input bindings, so it works whether or
not the 3D view or a terminal has focus. This article is that binding type, its matching rule, its capture modal and the plumbing around it. Demonstrated by purrTTY.

## Background: how the game does it

- The binding is data over ImGui: `ImGuiKey` plus `ImGuiIOPtr.KeyShift`, `KeyCtrl`, `KeyAlt` and `KeySuper`, all `ref bool` properties on `ImGuiIOPtr`.[current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs#L182-L186)[^brutal-imguiapi-imguiioptr]
  `ImGui.IsKeyPressed(key, repeat: false)` gives the press edge.[current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8146)[^brutal-imguiapi-imgui]
- Modals are plain ImGui: `ImGui.BeginPopupModal`, `OpenPopup`, `CloseCurrentPopup`. Nothing in KSA is involved in the capture, so it works anywhere in the UI pass.
- KSA does not know about the hotkey, so the game also receives the key press: purrTTY consumes a printable hotkey's first frame so it is not also typed into the shell, and it relies on the
  game-input gate described in [block game hotkeys while typing](../block-game-hotkeys-while-typing/block-game-hotkeys-while-typing-2.md) for the rest.

## Lifecycle and timing

- Polled in `TerminalMod.OnAfterUi` (`[StarMapAfterGui]`, `frame:draw-ui`) before the controller renders, so a hide takes effect before any key is encoded into a terminal that frame. Keys reach ImGui during
  `Glfw.PollEvents` in `PrepareFrame` and are evaluated by `ImGui.NewFrame`, so `IsKeyPressed` is valid anywhere in the UI pass.
- The binding is loaded once at init (`LoadToggleHotkeyFromConfiguration` in `InitializeTerminal`, `startup:all-mods-loaded`).
- Because `StarMapAfterGui` is skipped while the HUD is hidden, the toggle does nothing then. See
  [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Model the binding as a readonly struct**: key, four modifier flags, a static capturable-key list, display and shortcut formatting, and a config round trip. A default writes nulls so the config stays clean:

```csharp
// purrTTY.GameMod/ToggleHotkeyBinding.cs L128-L165
public static ToggleHotkeyBinding FromConfiguration(ThemeConfiguration configuration)
{
    if (string.IsNullOrWhiteSpace(configuration.ToggleHotkeyKey))
    {
        return Default;
    }

    if (!Enum.TryParse(configuration.ToggleHotkeyKey, ignoreCase: true, out ImGuiKey key) || !IsCapturableKey(key))
    {
        return Default;
    }

    return new ToggleHotkeyBinding(
        key,
        configuration.ToggleHotkeyShift,
        configuration.ToggleHotkeyCtrl,
        configuration.ToggleHotkeyAlt,
        configuration.ToggleHotkeySuper);
}

public void WriteToConfiguration(ThemeConfiguration configuration)
{
    if (IsDefault)
    {
        configuration.ToggleHotkeyKey = null;
        configuration.ToggleHotkeyShift = false;
        configuration.ToggleHotkeyCtrl = false;
        configuration.ToggleHotkeyAlt = false;
        configuration.ToggleHotkeySuper = false;
        return;
    }

    configuration.ToggleHotkeyKey = Key.ToString();
    configuration.ToggleHotkeyShift = Shift;
    configuration.ToggleHotkeyCtrl = Ctrl;
    configuration.ToggleHotkeyAlt = Alt;
    configuration.ToggleHotkeySuper = Super;
}
```

2. **Match on press, outside text input, with exact modifiers.** `repeat: false` stops a held key toggling every frame; `WantTextInput` stops a printable binding (such as `T`) firing while the player types into any text box:

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

3. **Read Super through reflection** so the code compiles and runs against a Brutal build without the member. In build 2026.10.10.5554 `KeySuper` exists as a public property, so on current builds you can read `io.KeySuper` directly:

```csharp
// purrTTY.GameMod/ToggleHotkeyBinding.cs L301-L319
internal static class ImGuiHotkeyHelpers
{
    private static readonly PropertyInfo? KeySuperProperty = typeof(ImGuiIOPtr).GetProperty("KeySuper", BindingFlags.Public | BindingFlags.Instance);

    public static bool GetSuperModifier(ImGuiIOPtr io)
    {
        if (KeySuperProperty is null)
        {
            return false;
        }

        if (KeySuperProperty.GetValue(io) is bool keySuper)
        {
            return keySuper;
        }

        return false;
    }
}
```

4. **Capture in a modal.** `Capture Now` flips a flag; while capturing, loop over the capturable keys with `IsKeyPressed` and record the first one together with the live modifier state:

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
```csharp
// purrTTY.GameMod/TerminalMod.cs L453-L489
private void RenderCaptureHotkeyModalContent()
{
    ImGui.Text("Press desired hotkey now");
    RenderHotkeySummaryTable("Hot Key", GetCaptureHotkeyDisplay());

    ImGui.Spacing();

    float availW = ImGui.GetContentRegionAvail().X;
    const float gap = 8f;
    float buttonWidth = (availW - gap) / 2f;

    bool hasCapturedHotkey = _lastCapturedToggleHotkey.HasValue;

    if (!hasCapturedHotkey)
    {
        ImGui.BeginDisabled();
    }

    if (ImGui.Button(" Use This ##purrtty_use_captured_hotkey", new float2(buttonWidth, 0f)) &&
        _lastCapturedToggleHotkey is ToggleHotkeyBinding capturedHotkey)
    {
        _draftToggleHotkey = capturedHotkey;
        ApplyToggleHotkeySetting(_draftToggleHotkey);
        _isCapturingToggleHotkey = false;
    }

    if (!hasCapturedHotkey)
    {
        ImGui.EndDisabled();
    }

    ImGui.SameLine(0, gap);
    if (ImGui.Button(" Cancel ##purrtty_cancel_capture_hotkey", new float2(buttonWidth, 0f)))
    {
        _isCapturingToggleHotkey = false;
    }
}
```

5. **Persist through the single live config instance.** Loading a second `ThemeConfiguration` and saving it later reverts fields written elsewhere:

```csharp
// purrTTY.GameMod/TerminalMod.cs L613-L632
private void ApplyToggleHotkeySetting(ToggleHotkeyBinding binding)
{
    _toggleHotkey = binding;
    _toggleHotkeyShortcut = binding.ToShortcutString();
    _draftToggleHotkey = binding;

    try
    {
        // Use the controller's live configuration instance so a later display
        // settings save does not clobber the new hotkey with stale values.
        var config = _controller?.Configuration ?? ThemeConfiguration.Load();
        _toggleHotkey.WriteToConfiguration(config);
        config.Save();
        ModLog.Log.Debug($"purrTTY GameMod: Toggle hotkey updated to {_toggleHotkey.ToDisplayString()}");
    }
    catch (Exception ex)
    {
        ModLog.Log.Debug($"purrTTY GameMod: Failed to persist toggle hotkey: {ex.Message}");
    }
}
```

6. **Poll it** in the GUI hook, skipping while the modal is open and while an in-world terminal has the keyboard (so the key reaches that shell). A shown terminal sets `_suppressNextTerminalKeyboardInputFrame`
   so the hotkey is not also typed:

```csharp
// purrTTY.GameMod/TerminalMod.cs L88-L100
// Handle terminal toggle keybind (dynamic, defaults to F12). Suppressed
// while the in-world terminal is focused so the key reaches that shell.
if (!modalVisible && !InWorldTerminalManager.IsInputFocused && IsToggleHotkeyPressed())
{
    bool wasVisible = IsTerminalVisible;
    ToggleTerminal();

    if (!wasVisible && IsTerminalVisible)
    {
        // Swallow the hotkey press so printable toggle keys do not appear as terminal input when opening.
        _suppressNextTerminalKeyboardInputFrame = true;
    }
}
```

## Complete example

| File | Role |
|---|---|
| [purrTTY.GameMod/ToggleHotkeyBinding.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/ToggleHotkeyBinding.cs) | `MatchesPress`, config round-trip, `KeySuper` reflection |
| [purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L88-L100) | Poll, toggle, first-frame suppression |
| [purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L400-L489) | Capture modal |
| [purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L591-L632) | Capture loop and persist |
| [purrTTY.Display/Configuration/ThemeConfiguration.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Configuration/ThemeConfiguration.cs) | Stores the binding fields |

## Variations and gotchas

- **Exact modifier match.** `MatchesPress` requires Shift, Ctrl, Alt and Super to equal the binding's, so a bare `F12` binding does not fire on `Ctrl+F12`.
- **Invisible to KSA's binding system.** Nothing prevents a clash with a game binding on the same key while the terminal is hidden.
- **Capturable list.** Only about 90 keys are capturable (Enter, Backspace, Tab, Escape, Space, arrows, Home/End, F-keys, digits, punctuation and keypad keys); `Enum.TryParse` plus `IsCapturableKey` guards a hand-edited config.
- **Version.** Checked against 2026.10.10.5554; the reflection guard only matters for older Brutal builds.

## See also

- [Overview of rebindable-hotkeys](overview.md)
- [Approach 1: enum names in JSON and a capture loop](rebindable-hotkeys-1.md)
- [Block game hotkeys while typing](../block-game-hotkeys-while-typing/overview.md)
- [Persist mod settings as TOML](../../../persistence/settings-formats/persist-mod-settings-toml/overview.md)
- Mod feature: [Rebindable toggle hotkey](../../../../mods/purrtty/features/rebindable-toggle-hotkey.md)

[^brutal-imguiapi-imguiioptr]: Brutal.ImGuiApi.ImGuiIOPtr (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
