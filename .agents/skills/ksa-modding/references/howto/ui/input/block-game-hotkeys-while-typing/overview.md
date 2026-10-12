---
type: Game Feature
title: Block game hotkeys while typing into an ImGui field
description: Why KSA bindings fire while a mod text box has focus, the key and mouse dispatch chain they travel, and two ways mods stop them
category: ui
group: input
topic: block-game-hotkeys-while-typing
demonstrated_by:
  - mods/unscience/features/lib-hotkey-guard
  - mods/purrtty/features/gate-game-input-while-terminal-focused
lifecycle: [frame:input, startup:all-mods-loaded, frame:draw-ui]
game_types: [KSA.GameSettings, KSA.Program, KSA.InputAction, Brutal.ImGuiApi.ImGuiIOPtr]
tags: [hotkeys, input, text-input, focus, harmony, imgui]
---
The first time a player types "m" into a mod's search box and the map opens, the mod has a hotkey problem. KSA's own text fields avoid it with an `IsUserTyping` flag that
`GameSettings.OnKeyAll` consults; nothing tells the game about a mod's ImGui widgets. Mods fix it by reporting the key as handled earlier in the game's key dispatch.

## Engine background

`Program.OnKey` is the GLFW key callback and `Program.OnMouseButton` the mouse one; both are subscribed to the main window by `Program` itself (the ImGui backend subscribes separately and forwards only
for secondary platform windows).[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L786-L792)[^ksa-program]

| Handler (in order) | Role | File |
|---|---|---|
| `GameSettings.OnKeyAll` | Cancel for the settings window; returns true while the stock `IsUserTyping` flag is set | [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L3422-L3435) |
| `Popup.OnKeyAll`, `ConsoleWindow.OnKey`, `!ConsoleWindow.IsOpen` | Popups and the developer console | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1745-L1754) |
| `!ImGui.GetIO().WantCaptureKeyboard` | The engine's own ImGui check | same |
| `DispatchKeyEvent` | Editor, camera, active controller, bindings (the `Release` arm fires toggles: HUD, FPS, profiler, screenshot) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1756-L1800) |

Two details change what a mod must do:

- `GameViewport.DrawImGui` sets next-frame `WantCaptureKeyboard` to false while the 3D view is hovered and `WantTextInput` is false, so a **focused window without a text widget** does not stop the game
  keys; only `WantTextInput` does.[current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L238-L246)[^ksa-gameviewport]
- A mouse button bound to a game action goes straight to `DispatchKeyEvent` from `OnMouseButton`, bypassing `OnKey` and `OnKeyAll`.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2003-L2022)[^ksa-program]

Everything runs in `frame:input`, before `PrepareImGui`, so the ImGui flags the prefixes read are the previous frame's. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| # | Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|---|
| 1 | [Prefix on GameSettings.OnKeyAll when ImGui wants text input](block-game-hotkeys-while-typing-1.md) | unscience | One prefix; returns handled while `WantTextInput` and the console is closed | `frame:input` | Covers every ImGui text widget; blind to focus without text input and to mouse bindings |
| 2 | [Gate game input while a terminal has focus](block-game-hotkeys-while-typing-2.md) | purrTTY | Three prefixes (`OnKey`, `OnKeyAll`, `DispatchKeyEvent`) behind one focus flag, with a held-key model | `frame:input`, `frame:draw-ui` | Complete, but needs a flag you maintain and a private-method patch |

## Choosing

- Text boxes only: approach 1, applied once per mod host.
- A custom widget that reads keys itself (terminal, game console, hotkey capture): approach 2.

## See also

- [Share the keyboard and mouse between ImGui, mod tools and the game](../coordinate-imgui-focus-with-game-input/overview.md)
- [Implement rebindable hotkeys with ImGui key state](../rebindable-hotkeys/overview.md)
- [Read typed text, key chords and the clipboard from ImGui](../read-typed-text-and-key-chords-from-imgui/overview.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
