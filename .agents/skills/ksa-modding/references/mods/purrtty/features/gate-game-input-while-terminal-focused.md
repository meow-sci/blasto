---
type: Mod Feature
title: Gate game input while a terminal is focused
description: Harmony prefixes on Program.OnKey, GameSettings.OnKeyAll and the private Program.DispatchKeyEvent keep KSA hotkeys and mouse bindings from firing while the player types into a terminal, without stranding held keys
mod: purrtty
category: patching
techniques: [harmony-prefix, starmap-lifecycle]
hooks:
  - harmony-prefix KSA.Program.OnKey
  - harmony-prefix KSA.GameSettings.OnKeyAll
  - harmony-prefix KSA.Program.DispatchKeyEvent
  - starmap StarMapAfterGui
lifecycle: [frame:input, frame:draw-ui]
game_types:
  - KSA.Program
  - KSA.GameSettings
  - RenderCore.Input.GlfwKeyEvent
  - Brutal.ImGuiApi.ImGuiIOPtr
  - KSA.ImGuiBackendGlfwImpl
  - KSA.GameViewport
  - Brutal.ImGuiApi.Abstractions.ConsoleWindow
tags: [harmony, input, keyboard, hotkeys, focus, imgui]
---

A focused ImGui window does not, by itself, stop KSA from acting on keys: the game subscribes
`Program.OnKey` directly to the main GLFW window, and a terminal window has no active text widget,
so ImGui does not claim the keyboard. Without help, typing `ls` into a shell would also stage,
throttle or toggle game UI. purrTTY closes every path with three small Harmony prefixes driven by
one flag, "a terminal owns the keyboard", and tracks key state so that held game keys are always
released and toggles bound to key release never leak.

## How it works

**The game's input chain (build 2026.10.10.5554).**

1. `Program` subscribes `OnKey` and `OnMouseButton` to the main window's GLFW events at startup;
   KSA's ImGui GLFW backend also receives every key and feeds ImGui (`AddKeyEvent`), forwarding to
   `Program.OnKey` itself only for secondary platform windows when `ImGuiBackend.InputFallthrough`
   is set.[^ksa-program][^ksa-glfw-backend]
2. `Program.OnKey` dispatches only if `GameSettings.OnKeyAll`, `Popup.OnKeyAll` and
   `ConsoleWindow.OnKey` all return false, the console is closed, and
   `ImGui.GetIO().WantCaptureKeyboard` is false. Then it calls the private `DispatchKeyEvent`,
   whose `GlfwKeyAction.Release` arm fires toggles such as `ToggleUi`, `ToggleFps`,
   `ToggleThreadProfiler` and `Screenshot`.[^ksa-program]
3. `GameViewport.DrawImGui` calls `ImGui.SetNextFrameWantCaptureKeyboard(false)` while the 3D view
   is hovered and no text field is active, so the game keeps keyboard control there.[^ksa-gameviewport]
4. `Program.OnMouseButton` sends mouse buttons bound to game actions straight to
   `DispatchKeyEvent` (`Input.MouseBound(button)`), bypassing `OnKey`.[^ksa-program]

**The flag.** `Patch01.TerminalOwnsKeyboard` is
`GhosttyTerminalController.IsAnyTerminalActive || InWorldTerminalManager.IsInputFocused`. The
controller recomputes `IsAnyTerminalActive` in every `Render` (true if any visible window has ImGui
focus or its grid context menu open) and clears it in the hidden early-out.[^controller]

**The patches** (all in `Patcher.cs`, classified as required):[^patcher]

| Patch | Target | Rule |
|---|---|---|
| `Patch01.Prefix1` | prefix `Program.OnKey` | Press: if the terminal owns the keyboard, skip the original; else record the key in `s_gameHeldKeys`. Repeat: pass only if the game holds the key and no terminal owns the keyboard. Release: pass only if the key was in `s_gameHeldKeys` (and remove it) |
| `Patch03_HotkeyGuard.Prefix` | prefix `GameSettings.OnKeyAll` | When the console is not open and `ImGui.GetIO().WantTextInput` or an in-world terminal is focused, set `__result = true` and skip, so `OnKey` treats the key as consumed |
| `Patch04_MouseBindingGate.Prefix` | prefix private `Program.DispatchKeyEvent` (by string name) | Keyboard events pass (already gated). Mouse press while the terminal owns the keyboard: remember it in `s_swallowedButtons`, return "not handled". Mouse release: swallow only if its press was swallowed |

Why "press-gated" for keys: swallowing every release strands a held movement key down (the game
saw the press before focus moved), while forwarding every release leaks release-triggered toggles
whose press the terminal ate. Tracking which presses the game actually saw satisfies both.
`Patch04` tracks the opposite set (presses it swallowed) because it only sees buttons that survived
`OnMouseButton`'s own filters, so unknown releases must pass through.

Each patch class has a `Reset()` that `Patcher.unload()` calls, because statics survive a StarMap
reload that does not unload the assembly load context.

## Lifecycle and timing

- Key and mouse callbacks run during `Glfw.PollEvents()` in `Program.PrepareFrame` (`frame:input`),
  on the main thread, before this frame's UI exists.[^ksa-program] The prefixes therefore read the
  focus flag computed in the previous frame's `frame:draw-ui`, which is exactly the focus the
  player saw when pressing the key.
- The flag is written in `TerminalMod.OnAfterUi` (`[StarMapAfterGui]`) through
  `_controller.Render()`, which is called every frame even when the terminal is hidden: the hidden
  early-out is the only place that clears the flag, and skipping it would leave the game with no
  keyboard at all (gotcha 21).[^terminal-mod][^gotchas]
- "Every frame" means every frame in which `Program.OnDrawUiViewports` runs, which is only when
  `Program.DrawUI` is true. While the HUD is hidden, `StarMapAfterGui` does not fire, the terminals
  are not drawn and both flags keep the value from the last HUD-visible frame. Hiding the HUD from
  the keyboard (`ToggleUi`) is itself a key the gate swallows while a terminal owns the keyboard,
  and screenshot capture hides the HUD for one frame only, so in practice the stale value is the
  correct one (inferred from the code).[^ksa-program]
- The mouse-binding gate runs in the same `Glfw.PollEvents` call, inside `Program.OnMouseButton`,
  after the popup and ImGui-capture checks and before the game's click and camera handling.
- The patches are applied at `startup:all-mods-loaded` (`Patcher.patch()` in `OnFullyLoaded`).

## Key code

| Path | Role |
|---|---|
| [purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L154-L217) | `Patch01`: `OnKey` gate with the held-key model |
| [purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L130-L152) | `Patch03_HotkeyGuard`: `OnKeyAll` guard for text fields and in-world focus |
| [purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L219-L294) | `Patch04_MouseBindingGate`: `DispatchKeyEvent` gate for mouse-bound actions |
| [purrTTY.Display/Ghostty/GhosttyTerminalController.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/GhosttyTerminalController.cs#L381-L442) | Computes and clears `IsAnyTerminalActive` |
| [purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L102-L113) | Calls `Render()` unconditionally |
| [purrTTY.GameMod/InWorld/InWorldTerminalManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/InWorldTerminalManager.cs) | `IsInputFocused` for in-world terminals |

## Snippets

```csharp
// purrTTY.GameMod/Patcher.cs L171-L216
  internal static bool TerminalOwnsKeyboard =>
    GhosttyTerminalController.IsAnyTerminalActive || InWorldTerminalManager.IsInputFocused;

  [HarmonyPrefix]
  [HarmonyPatch(nameof(KSA.Program.OnKey))]
  static bool Prefix1(GlfwWindow window, GlfwKey key, int scanCode, GlfwKeyAction action, GlfwModifier mods)
  {
    // ...
    switch (action)
    {
      case GlfwKeyAction.Press:
        if (TerminalOwnsKeyboard)
        {
          // Terminal owns the keyboard: swallow the press. The game never sees
          // the key go down, so its release stays swallowed too (not tracked).
          return false;
        }
        s_gameHeldKeys.Add(key);
        return true;

      case GlfwKeyAction.Repeat:
        // Only sustain a repeat the game already owns and only while no terminal
        // owns the keyboard; otherwise the terminal owns it.
        return !TerminalOwnsKeyboard && s_gameHeldKeys.Contains(key);

      case GlfwKeyAction.Release:
        // Forward iff the game believes this key is held; remove either way so
        // the held-key model drains as keys come back up.
        return s_gameHeldKeys.Remove(key);

      default:
        return true;
    }
  }
```

The game-side gate chain the prefixes plug into:

```csharp
// current/decomp/KSA/Program.cs L1745-L1754
	public void OnKey(GlfwWindow window, GlfwKey key, int scanCode, GlfwKeyAction action, GlfwModifier mods)
	{
		GlfwKeyEvent keyEvent = new GlfwKeyEvent(window, action, key, mods);
		ShiftDown = EnumEx.IsSet((int)mods, 1);
		AltDown = EnumEx.IsSet((int)mods, 4);
		if (IsLoaded && !GameSettings.OnKeyAll(keyEvent) && !Popup.OnKeyAll(keyEvent) && !ConsoleWindow.OnKey(window, key, scanCode, action, mods) && !ConsoleWindow.IsOpen && !ImGui.GetIO().WantCaptureKeyboard)
		{
			DispatchKeyEvent(in keyEvent);
		}
	}
```

## Game APIs touched

| API | Where | Purpose |
|---|---|---|
| `Program.OnKey` (public) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1745-L1754) | Keyboard entry point; prefixed |
| `Program.DispatchKeyEvent(in GlfwKeyEvent)` (private) | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1756-L1830) | Action switch incl. release-arm toggles; prefixed for mouse events |
| `Program.OnMouseButton` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2003-L2025) | Sends bound mouse buttons to `DispatchKeyEvent` |
| `Program` startup subscriptions | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L787-L792) | `_window.OnKey += OnKey`, `_window.OnMouseButton += OnMouseButton` |
| `GameSettings.OnKeyAll(GlfwKeyEvent)` | [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L3422-L3434) | First consumer in `OnKey`; prefixed[^ksa-gamesettings] |
| `GlfwKeyEvent.IsMouse` / `Button` / `Action` | [current/decomp/RenderCore.Input/GlfwKeyEvent.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Input/GlfwKeyEvent.cs) | Distinguishes mouse-originated events[^ksa-keyevent] |
| `ImGuiBackendGlfwImpl.OnKey`, `ShouldForwardKeyboardToGame` | [current/decomp/KSA/ImGuiBackendGlfwImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendGlfwImpl.cs#L431-L522) | ImGui key feed and secondary-window forwarding |
| `GameViewport.DrawImGui` (`SetNextFrameWantCaptureKeyboard(false)`, `InputFallthrough`) | [current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L230-L246) | Why ImGui does not claim the keyboard over the 3D view |
| `Program.ConsoleWindow`, `ConsoleWindow.IsOpen` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L285) | Console exemption in `Patch03` |

## Pitfalls and version notes

- `DispatchKeyEvent` is private and patched by string name; `Patch04` is its own class so a rename
  cannot take `Patch01` down with it (see [failure-isolated patching](./failure-isolated-harmony-patching.md)).
  The original takes `in GlfwKeyEvent`; the prefix declares it by value, which Harmony supports for
  read-only use.
- Swallowing a press in `Patch01` skips the whole original, including its `ShiftDown`/`AltDown`
  bookkeeping for that event.
- Skipping `GameSettings.OnKeyAll` in `Patch03` also skips its own side effects (its `Cancel`
  handling and frame counter) for that key.
- purrTTY's gotcha 21 says the game only receives input while `ImGuiBackend.InputFallthrough` is set.
  In build 2026.10.10.5554 that applies to secondary platform windows; main-window keys reach
  `Program.OnKey` through `Program`'s own GLFW subscription. Gating `OnKey` itself, as purrTTY does,
  works either way.
- Mouse-button bindings arrived in KSA 5482 (rev 5449, per the purrTTY comments); on older builds
  `Patch04` is unnecessary but harmless.
- Clear the "terminal active" flag on every path that stops drawing (hide, close-all, dispose),
  otherwise the game keyboard dies until the terminal is shown again.

## See also

- [ImGui keyboard and mouse to terminal input](./imgui-keyboard-mouse-to-vt-input.md)
- [Rebindable toggle hotkey](./rebindable-toggle-hotkey.md)
- [Failure-isolated Harmony patching](./failure-isolated-harmony-patching.md)
- [Ray-pick focus for world quads](./ray-pick-focus-for-world-quads.md) (sets `IsInputFocused`)
- [Click-through lock mode and hot zone](./click-through-lock-mode-hot-zone.md)
- [purrTTY overview](../overview.md)
- [Hook any frame point with Harmony patches on Program and Universe](../../../howto/getting-started/lifecycle/game-loop-and-lifecycle/game-loop-and-lifecycle-2.md) - how-to article demonstrating this feature
- [Keyboard and mouse-binding gates with a held-key model](../../../howto/patching/harmony/skip-the-original-method-with-a-prefix/skip-the-original-method-with-a-prefix-5.md) - how-to article demonstrating this feature
- [Block game hotkeys while typing: gate game input while a terminal has focus](../../../howto/ui/input/block-game-hotkeys-while-typing/block-game-hotkeys-while-typing-2.md) - how-to article demonstrating this feature

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-glfw-backend]: KSA.ImGuiBackendGlfwImpl (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
[^controller]: purrTTY.Display/Ghostty/GhosttyTerminalController.cs
[^patcher]: purrTTY.GameMod/Patcher.cs
[^terminal-mod]: purrTTY.GameMod/TerminalMod.cs
[^gotchas]: purrTTY docs/gotchas.md
[^ksa-gamesettings]: KSA.GameSettings (decompiled, build 2026.10.10.5554)
[^ksa-keyevent]: RenderCore.Input.GlfwKeyEvent (decompiled, build 2026.10.10.5554)
