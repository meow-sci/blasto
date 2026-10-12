---
type: Mod Feature
title: Rebindable numpad hotkeys
description: Polls ImGui.IsKeyPressed for numpad toggle, settings and countdown keys stored as ImGuiKey values in the JSON config, skips them while a text field has focus, and rebinds them with a press-a-key capture loop over the ImGuiKey range
mod: ksa-public-telemetry
category: ui
techniques: [imgui-input, json-config]
hooks: [starmap StarMapAfterGui, harmony-postfix KSA.Program.DrawFps]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, Brutal.ImGuiApi.ImGuiKey]
tags: [hotkeys, input, imgui, keybinding, config]
---

Numpad `.` toggles the overlay, numpad `/` opens the settings window and numpad `*` starts or cancels a
launch countdown ("numpad period key (I know)", says the README).[^kpt-readme] All three are stored as
`ImGuiKey` enum names in `config.json` and can be rebound in the settings by clicking a button and
pressing a key.

## How it works

1. **Defaults.** `ToggleKey = ImGuiKey.KeypadDecimal`, `SettingsKey = ImGuiKey.KeypadDivide`,
   `CountdownKey = ImGuiKey.KeypadMultiply` ([OverlayConfig.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/OverlayConfig.cs#L28-L31)). The JSON serializer uses
   `JsonStringEnumConverter`, so they are written as names ([Config/ConfigStore.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/ConfigStore.cs#L16-L23)). The overlay
   itself starts disabled (`Enabled = false`), so the toggle key is how a new install first shows it
   ([OverlayConfig.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/OverlayConfig.cs#L9-L10)).
2. **Polling.** `HandleHotkeys` runs once per overlay draw. It returns early when
   `ImGui.GetIO().WantTextInput` (a text box has focus) or while a rebinding capture is active, then
   checks each key with `ImGui.IsKeyPressed(key, repeat: false)` ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L173-L202)).
3. **Capture.** In the "Behaviour" settings, a key button switches to "Press a key... (Esc cancels)";
   `PollPressedKey` walks `ImGuiKey.NamedKey_BEGIN` to `ImGuiKey.Oem102`, skips the eight modifier keys,
   and returns the first `IsKeyPressed`; Escape cancels ([Rendering/SettingsUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/SettingsUi.cs#L383-L438)).

## Lifecycle and timing

- **`frame:draw-ui`.** ImGui's key state for the frame is valid between `NewFrame` and `Render`, which is
  where the StarMap UI hook and the `DrawFps` postfix run. Exactly one of them reaches `HandleHotkeys`
  per frame (the postfix returns while `DrawUI` is true), so a press is never seen twice. With the UI
  hidden the hotkeys only work when "Stay visible when F2 hides the UI" is on, because
  `DrawWhileGameUiHidden` returns before `DrawEverything` otherwise
  ([TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L88-L96)).
- **After the game's own input.** The GLFW key callback, fired by `Glfw.PollEvents` in `PrepareFrame`,
  queues the key for ImGui (`AddKeyEvent`) and then, if `ShouldForwardKeyboardToGame`, forwards it to
  `Program.OnKey`
  ([current/decomp/KSA/ImGuiBackendGlfwImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendGlfwImpl.cs#L496-L522)); ImGui applies the queue in `ImGui.NewFrame`,
  so the mod sees the press later in the same frame and a toggle or countdown start takes effect in
  that frame's overlay draw.[^ksa-imguibackendglfwimpl]
- **Not the game's key dispatch.** The mod does not register with the game's key bindings
  (`frame:input`), so a numpad key can do two things if the game also binds it.

## Key code

| Path | Role |
|---|---|
| [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L173-L202) | Hotkey polling |
| [Rendering/SettingsUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/SettingsUi.cs#L383-L438) | Rebinding capture |
| [OverlayConfig.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/OverlayConfig.cs#L28-L31) | Default keys |

## Snippets

```csharp
// TelemetryOverlayMod.cs L173-L202
    private static void HandleHotkeys(OverlayConfig config)
    {
        if (ImGui.GetIO().WantTextInput || SettingsUi.IsCapturingKey)
        {
            return;
        }

        if (ImGui.IsKeyPressed(config.ToggleKey, repeat: false))
        {
            config.Enabled = !config.Enabled;
            ConfigStore.MarkDirty();
        }

        if (ImGui.IsKeyPressed(config.SettingsKey, repeat: false))
        {
            SettingsUi.ToggleWindow();
        }

        if (ImGui.IsKeyPressed(config.CountdownKey, repeat: false))
        {
            if (TelemetrySampler.IsCountingDown)
            {
                TelemetrySampler.CancelCountdown();
            }
            else
            {
                TelemetrySampler.StartCountdown(config.CountdownSeconds);
            }
        }
    }
```

## Game APIs touched

| Member | Decompiled source | Used for |
|---|---|---|
| `ImGui.IsKeyPressed(ImGuiKey, bool repeat)` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8146) | Key press edges |
| `ImGui.GetIO`, `ImGuiIOPtr.WantTextInput` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L5438), [current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs#L144) | Skip while typing |
| `ImGuiKey.NamedKey_BEGIN`, `KeypadDecimal`, `Oem102` | [current/decomp/Brutal.ImGuiApi/ImGuiKey.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiKey.cs#L6), [current/decomp/Brutal.ImGuiApi/ImGuiKey.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiKey.cs#L117), [current/decomp/Brutal.ImGuiApi/ImGuiKey.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiKey.cs#L126) | Default key and capture range |

## Pitfalls and version notes

- **No modifier support.** Modifiers are skipped during capture and not checked when polling.
- **Range-coupled capture.** The loop's upper bound is `ImGuiKey.Oem102`; keys after it in a future
  ImGui (gamepad, mouse) are not capturable, which is intended.
- **WantTextInput only.** The guard does not check `WantCaptureKeyboard`, so a focused non-text ImGui
  widget does not block the hotkeys.

## See also

- [ModMenu entry with a standalone fallback window](modmenu-entry-with-standalone-fallback.md)
- [Mission clock and liftoff detection](mission-clock-and-liftoff-detection.md)
- [JSON config in the KSA documents folder](json-config-in-documents.md)
- [Rebindable hotkeys: enum names in JSON and a capture loop over the ImGuiKey range](../../../howto/ui/input/rebindable-hotkeys/rebindable-hotkeys-1.md) - how-to article demonstrating this feature

[^kpt-readme]: README.md
[^ksa-imguibackendglfwimpl]: KSA.ImGuiBackendGlfwImpl (decompiled, build 2026.10.10.5554)
