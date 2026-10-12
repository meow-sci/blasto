---
type: How-To
title: "Rebindable hotkeys: enum names in JSON and a capture loop over the ImGuiKey range"
description: Poll ImGuiKey hotkeys with IsKeyPressed outside text input, store them as enum names in JSON and capture a new key with a press-a-key button
category: ui
group: input
topic: rebindable-hotkeys
approach: 1
difficulty: beginner
prerequisites: [starmap]
demonstrated_by: [mods/ksa-public-telemetry/features/rebindable-numpad-hotkeys]
techniques: [json-config, imgui-window, starmap-lifecycle]
hooks: [starmap StarMapAfterGui, harmony-postfix KSA.Program.DrawFps]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGuiKey, Brutal.ImGuiApi.ImGuiIOPtr, KSA.Program]
tags: [hotkeys, imgui, imguikey, config, rebinding, capture]
---
KSA-Public-Telemetry has three numpad hotkeys: `.` toggles the overlay, `/` opens the settings window and `*` starts or cancels a launch countdown. All three are stored in `config.json` as `ImGuiKey` enum
names and can be rebound from the settings window by clicking a button and pressing a key. This is the smallest complete recipe for a rebindable hotkey: no game binding is registered, the keys are
polled from ImGui every draw, and the capture is a loop over the key enum. Demonstrated by KSA-Public-Telemetry.

## Background: how the game does it

- ImGui's key state is valid between `ImGui.NewFrame` and `ImGui.Render`. `ImGui.IsKeyPressed(ImGuiKey key, bool repeat = true)` reports a press edge this frame; pass `repeat: false` to ignore OS key repeat.
  [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8146)[^brutal-imguiapi-imgui]
- `ImGuiKey` is an enum starting at `NamedKey_BEGIN = 512`, with the keypad keys at `KeypadDecimal`, `KeypadDivide` and `KeypadMultiply`, ending the keyboard range at `Oem102`; the modifier keys are ordinary members
  (`LeftCtrl`, `LeftShift`, ...).[current/decomp/Brutal.ImGuiApi/ImGuiKey.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiKey.cs#L6)[^brutal-imguiapi-imguikey][current/decomp/Brutal.ImGuiApi/ImGuiKey.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiKey.cs#L117-L126)[^brutal-imguiapi-imguikey]
- This is **not** KSA's input system. KSA's own bindings (`Input`, `InputAction`, `GameSettings.KeyBindings`) are matched in `Program.OnKey`'s `DispatchKeyEvent`; a mod hotkey polled from ImGui does not register there, so a
  key the game also binds does both.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1756-L1776)[^ksa-program]
- `ImGuiIOPtr.WantTextInput` is true while an ImGui text field is active.[current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs#L144)[^brutal-imguiapi-imguiioptr]

## Lifecycle and timing

`HandleHotkeys` runs once per overlay draw, from `DrawEverything` (`frame:draw-ui`). There are two entry points: the StarMap UI hook, and a `DrawFps` postfix for frames when the game HUD is hidden (F2); exactly one
reaches `HandleHotkeys` per frame, so a press is never seen twice. With the UI hidden, the hotkeys only work when "Stay visible when F2 hides the UI" is on. Keys reach ImGui during `Glfw.PollEvents` in
`PrepareFrame` and are evaluated by `ImGui.NewFrame`, so `IsKeyPressed` is valid anywhere in the UI pass and a toggle takes effect in that frame's overlay draw. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and
[keep UI running when the HUD is hidden](../../overlays/draw-ui-while-the-hud-is-hidden/overview.md).

## Steps

1. **Declare the keys in your config as `ImGuiKey` fields with defaults.**

```csharp
// OverlayConfig.cs L28-L31
// behaviour
public ImGuiKey ToggleKey = ImGuiKey.KeypadDecimal;
public ImGuiKey SettingsKey = ImGuiKey.KeypadDivide;
public ImGuiKey CountdownKey = ImGuiKey.KeypadMultiply;
```

2. **Serialize enums as names** so the file is readable and survives enum reordering:

```csharp
// Config/ConfigStore.cs L16-L23
private static readonly JsonSerializerOptions _options = new()
{
    WriteIndented = true,
    IncludeFields = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    Converters = { new JsonStringEnumConverter() },
};
```

3. **Poll once per draw.** Skip while a text box has focus or a rebinding capture is running; use `repeat: false`:

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

4. **Build the capture.** Clicking the key button enters a capture state; while capturing, show "Press a key... (Esc cancels)". Skip while a text field is active, return the first newly pressed key and let
   Escape cancel:

```csharp
// Rendering/SettingsUi.cs L383-L420
private static bool DrawKeyBinding(ImString label, ref ImGuiKey key, KeySlot slot)
{
    bool changed = false;

    ImGui.PushID((int)slot);
    ImGui.SetNextItemWidth(160f);

    if (_capturing == slot)
    {
        ImGui.Button("Press a key... (Esc cancels)"u8, new float2(200f, 0f));

        if (!ImGui.GetIO().WantTextInput)
        {
            ImGuiKey pressed = PollPressedKey();

            if (pressed == ImGuiKey.Escape)
            {
                _capturing = KeySlot.None;
            }
            else if (pressed != ImGuiKey.None)
            {
                key = pressed;
                _capturing = KeySlot.None;
                changed = true;
            }
        }
    }
    else if (ImGui.Button(key.ToString(), new float2(200f, 0f)))
    {
        _capturing = slot;
    }

    ImGui.SameLine();
    ImGui.Text(label);
    ImGui.PopID();

    return changed;
}
```

5. **Scan the key range, skipping modifiers.** Walk `ImGuiKey.NamedKey_BEGIN` to `ImGuiKey.Oem102` and return the first `IsKeyPressed(key, repeat: false)`. The modifier keys are listed explicitly:

```csharp
// Rendering/SettingsUi.cs L19-L23
private static readonly ImGuiKey[] _modifierKeys =
[
    ImGuiKey.LeftCtrl, ImGuiKey.LeftShift, ImGuiKey.LeftAlt, ImGuiKey.LeftSuper,
    ImGuiKey.RightCtrl, ImGuiKey.RightShift, ImGuiKey.RightAlt, ImGuiKey.RightSuper,
];
```
```csharp
// Rendering/SettingsUi.cs L422-L438
private static ImGuiKey PollPressedKey()
{
    for (ImGuiKey key = ImGuiKey.NamedKey_BEGIN; key <= ImGuiKey.Oem102; key++)
    {
        if (Array.IndexOf(_modifierKeys, key) >= 0)
        {
            continue;
        }

        if (ImGui.IsKeyPressed(key, repeat: false))
        {
            return key;
        }
    }

    return ImGuiKey.None;
}
```

6. **Persist the change** by marking the config dirty (`ConfigStore.MarkDirty()`); the store flushes on a timer. See the persistence topics for the config file itself.

## Complete example

| File | Role |
|---|---|
| [TelemetryOverlayMod.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/TelemetryOverlayMod.cs#L173-L202) | Hotkey polling |
| [Rendering/SettingsUi.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/SettingsUi.cs#L383-L438) | Rebinding capture |
| [OverlayConfig.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/OverlayConfig.cs#L28-L31) | Default keys |
| [Config/ConfigStore.cs](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Config/ConfigStore.cs#L16-L23) | JSON options with enum names |

## Variations and gotchas

- **No modifier support.** Modifiers are skipped during capture and not checked when polling; for Ctrl/Shift/Alt/Super combinations see [approach 2](rebindable-hotkeys-2.md).
- **Range-coupled capture.** The loop's upper bound is `ImGuiKey.Oem102`; keys after it in a future ImGui (gamepad, mouse) are not capturable, which is intended.
- **`WantTextInput` only.** The guard does not check `WantCaptureKeyboard`, so a focused non-text ImGui widget does not block the hotkeys.
- **Not the game's dispatch.** A numpad key can do two things if the game also binds it; check the stock bindings for clashes, and see [block game hotkeys while typing](../block-game-hotkeys-while-typing/overview.md).
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of rebindable-hotkeys](overview.md)
- [Approach 2: key plus modifiers with a modal capture, stored in TOML](rebindable-hotkeys-2.md)
- [Block game hotkeys while typing](../block-game-hotkeys-while-typing/overview.md)
- [Persist mod settings as TOML](../../../persistence/settings-formats/persist-mod-settings-toml/overview.md)
- Mod feature: [Rebindable numpad hotkeys](../../../../mods/ksa-public-telemetry/features/rebindable-numpad-hotkeys.md)

[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imguikey]: Brutal.ImGuiApi.ImGuiKey (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imguiioptr]: Brutal.ImGuiApi.ImGuiIOPtr (decompiled, build 2026.10.10.5554)
