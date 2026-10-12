---
type: How-To
title: Keyboard and mouse-binding gates with a held-key model
description: Three skipping prefixes on Program.OnKey, GameSettings.OnKeyAll and the private Program.DispatchKeyEvent keep game hotkeys from firing while a mod UI owns the keyboard, with a held-key model so releases are never stranded or leaked
category: patching
group: harmony
topic: skip-the-original-method-with-a-prefix
approach: 5
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/purrtty/features/gate-game-input-while-terminal-focused]
techniques: [harmony-prefix]
hooks:
  - harmony-prefix KSA.Program.OnKey
  - harmony-prefix KSA.GameSettings.OnKeyAll
  - harmony-prefix KSA.Program.DispatchKeyEvent
lifecycle: [frame:input, frame:draw-ui]
game_types: [KSA.Program, KSA.GameSettings, KSA.Input]
tags: [harmony, prefix, skip-original, input, keyboard, hotkeys, held-keys]
---

A focused ImGui window does not stop KSA from acting on keys. A terminal window has no active text
widget, so ImGui does not claim the keyboard, and typing `ls` into a shell would also stage, throttle
or toggle game UI. purrTTY closes every path with three small prefixes driven by one flag, "a terminal
owns the keyboard", and tracks which presses the game actually saw so that held game keys are
always released and toggles bound to key *release* never leak.

Prerequisites: a StarMap mod with `0Harmony`. Demonstrated by purrTTY in
[Gate game input while a terminal is focused](../../../../mods/purrtty/features/gate-game-input-while-terminal-focused.md).

## Background: how the game does it

`Program` subscribes `OnKey` and `OnMouseButton` to the main window ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L787-L792)[^ksa-program]).
`OnKey` dispatches only when `GameSettings.OnKeyAll`, `Popup.OnKeyAll` and `ConsoleWindow.OnKey` all
return `false`, the console is closed, and `ImGui.GetIO().WantCaptureKeyboard` is false:

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

Source: [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1745-L1754)

`GameSettings.OnKeyAll` is the first consumer; it returns `true` while a settings text field is being
edited:

```csharp
// current/decomp/KSA/GameSettings.cs L3422-L3435
public static bool OnKeyAll(GlfwKeyEvent keyEvent)
{
	if (_lastFrame > 0 && KSA.Input.MatchReleased(in keyEvent, InputAction.Cancel))
	{
		_show = false;
		return true;
	}
	_lastFrame++;
	if (IsUserTyping)
	{
		return true;
	}
	return false;
}
```

Source: [current/decomp/KSA/GameSettings.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameSettings.cs#L3422-L3435)[^ksa-gamesettings]

The private `DispatchKeyEvent(in GlfwKeyEvent)` is the action switch. Its `GlfwKeyAction.Release`
arm fires toggles such as `ToggleThreadProfiler`, `ToggleFps`, `ToggleUi` and `Screenshot`
([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1756-L1810)). `Program.OnMouseButton` also calls it directly when the player has bound
a mouse button to a game action, bypassing both `OnKey` and `OnKeyAll`
(`Input.MouseBound(button) && DispatchKeyEvent(...)`, [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2020)).

## Lifecycle and timing

- Key and mouse callbacks run inside `Glfw.PollEvents()` in `Program.PrepareFrame` (`frame:input`), on the
  main thread, before this frame's UI exists ([current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2179)). The prefixes therefore read the
  flag computed during the *previous* frame's `frame:draw-ui`, which is exactly the focus the player saw
  when pressing the key.
- The flag is written by `GhosttyTerminalController.Render()`, which `TerminalMod` calls every frame even
  when the terminal is hidden: the hidden early-out is the only place that clears the flag, and skipping
  it would leave the game with no keyboard at all ([purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L102-L113)[^purrtty-terminalmod],
  [purrTTY.Display/Ghostty/GhosttyTerminalController.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/GhosttyTerminalController.cs#L381-L387)[^purrtty-ghosttyterminalcontroller]).
- "Every frame" means every frame in which the draw-ui phase runs, which is only while the HUD is
  visible; see [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) for what runs when the HUD is hidden.
- The patches are applied at `startup:all-mods-loaded` (`Patcher.patch()`).

## Steps

1. **Define the one flag.** Both keyboard and mouse gates ask the same question. In purrTTY it is true
   when any visible terminal window has ImGui focus (or its context menu is open) or an in-world
   terminal quad is focused. Whatever your UI is, compute it where your UI draws and clear it on
   *every* path that stops drawing (hide, close-all, dispose).

2. **Gate `Program.OnKey` with a held-key model.** The naive rule "swallow press and repeat, forward
   release" strands a held movement key if the terminal gets focus while it is down, and the rule
   "always forward releases" leaks release-triggered toggles whose press the terminal ate. The fix is to
   forward a key event only for keys whose *press* the game received. The set `s_gameHeldKeys` is that
   model:

```csharp
// purrTTY.GameMod/Patcher.cs L171-L177, L191-L216
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

Source: [purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L171-L216)[^purrtty-patcher]

   Swallowing a press skips the whole original, including its `ShiftDown` and `AltDown` bookkeeping for
   that event.

3. **Guard `GameSettings.OnKeyAll` while a text field is active.** Setting `__result = true` and
   returning `false` makes `Program.OnKey` treat the key as consumed. The in-game console is exempt so
   it keeps working. Skipping `OnKeyAll` also skips its own `Cancel` handling and frame counter for that
   key:

```csharp
// purrTTY.GameMod/Patcher.cs L130-L152
/// <summary>
/// Blocks game hotkeys (GameSettings.OnKeyAll) whenever an ImGui text input has
/// keyboard focus (e.g. the save-theme name field), so typing does not trigger
/// game key bindings. The in-game console is exempt so it keeps working.
/// </summary>
[HarmonyPatch(typeof(GameSettings), nameof(GameSettings.OnKeyAll))]
static class Patch03_HotkeyGuard
{
  [HarmonyPrefix]
  static bool Prefix(ref bool __result)
  {
    // Program.ConsoleWindow can be unassigned very early in startup; treat a
    // null/closed console as "not open" so a focused text field still suppresses
    // game hotkeys without NREing on the static field.
    bool consoleOpen = Program.ConsoleWindow is { IsOpen: true };
    if (!consoleOpen && (ImGui.GetIO().WantTextInput || InWorldTerminalManager.IsInputFocused))
    {
      __result = true;
      return false;
    }
    return true;
  }
}
```

Source: [purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L130-L152)

4. **Close the mouse-binding path in a separate class.** A bound mouse button reaches the private
   `DispatchKeyEvent` without passing `OnKey` or `OnKeyAll`. This prefix tracks the *opposite* set:
   presses it swallowed, because it only sees buttons that survived `OnMouseButton`'s own filters, so an
   unknown release must pass through. Swallowing reports "not handled" (`__result = false`), so
   `OnMouseButton` carries on with its ordinary click and camera handling. The private target is
   named with a string, and the original's `in GlfwKeyEvent` parameter is declared by value in the
   prefix, which Harmony supports for read-only use:

```csharp
// purrTTY.GameMod/Patcher.cs L247-L252, L259-L293
[HarmonyPatch(typeof(KSA.Program), "DispatchKeyEvent")]
static class Patch04_MouseBindingGate
{
  // Mouse buttons whose PRESS this prefix swallowed; their release is swallowed too.
  // Accessed only from the GLFW poll thread (same as Patch01's s_gameHeldKeys).
  private static readonly HashSet<GlfwMouseButton> s_swallowedButtons = new();
// ...
  [HarmonyPrefix]
  static bool Prefix(GlfwKeyEvent keyEvent, ref bool __result)
  {
    if (!keyEvent.IsMouse)
    {
      return true;
    }

    switch (keyEvent.Action)
    {
      case GlfwKeyAction.Press:
        if (Patch01.TerminalOwnsKeyboard)
        {
          s_swallowedButtons.Add(keyEvent.Button);
          __result = false;
          return false;
        }
        // The game sees this press; drop any stale entry (a swallowed press whose release
        // never reached DispatchKeyEvent, e.g. it came up over an ImGui window) so it
        // can't eat this press's release.
        s_swallowedButtons.Remove(keyEvent.Button);
        return true;

      case GlfwKeyAction.Release:
        if (s_swallowedButtons.Remove(keyEvent.Button))
        {
          __result = false;
          return false;
        }
        return true;

      default:
        return true;
    }
  }
```

Source: [purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L247-L293)

   Keeping this in its own class means a rename of `DispatchKeyEvent` cannot take the keyboard gate down
   with it ([CreateClassProcessor per patch class, required versus optional](../../safety/isolate-patch-failures-and-fault-proof-patch-bodies/isolate-patch-failures-and-fault-proof-patch-bodies-1.md)).

5. **Apply each class independently and reset on unload.** purrTTY applies every patch class with
   `CreateClassProcessor(type).Patch()` inside its own `try`, and calls `Reset()` on `Patch01` and
   `Patch04_MouseBindingGate` at unload because statics survive a StarMap reload that does not unload the
   assembly load context ([purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L13-L60)).

## Complete example

| File | Role |
|---|---|
| [purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L130-L294) | The three prefixes |
| [purrTTY.Display/Ghostty/GhosttyTerminalController.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/GhosttyTerminalController.cs#L381-L442) | Computes and clears `IsAnyTerminalActive` |
| [purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L102-L113) | Calls `Render()` unconditionally |

## Variations and gotchas

- **Press-gated versus pair-gated.** `Patch01` tracks presses *forwarded*; `Patch04` tracks presses
  *swallowed*. Pick the set that matches where your prefix sits in the pipeline: at the entry that sees
  every press, track forwarded presses; behind a filter that hides some, track what you swallowed.
- **Clear the flag on every path.** If the "terminal active" flag stays `true` after the terminal hides,
  the game keyboard stays dead until it is shown again.
- **Mouse-button bindings are a build-dependent path.** purrTTY's comments date them to KSA 5482 (rev
  5449); on older builds `Patch04` is unnecessary but harmless.
- **ImGui backend notes.** The purrTTY gotchas say the game only receives input while
  `ImGuiBackend.InputFallthrough` is set. In build 2026.10.10.5554 that applies to secondary platform
  windows; main-window keys reach `Program.OnKey` through `Program`'s own GLFW subscription, and gating
  `OnKey` works either way.
- **Skipping hides your input from other mods too.** Anything patching `Program.OnKey` at a lower
  priority will not see a swallowed press.

## See also

- [Reproduce the original body for marked objects and return false](skip-the-original-method-with-a-prefix-1.md), [Rewrite by-ref arguments and skip when a limit would throw](skip-the-original-method-with-a-prefix-2.md), [Re-inject a private field before a rebuild so nothing can change it back](skip-the-original-method-with-a-prefix-3.md), [Mouse prefix that pairs presses with releases and repairs private latches](skip-the-original-method-with-a-prefix-4.md)
- [Skipping Harmony prefixes](overview.md)
- [Gate game input while a terminal is focused](../../../../mods/purrtty/features/gate-game-input-while-terminal-focused.md)
- The topics block-game-hotkeys-while-typing, coordinate-imgui-focus-with-game-input and rebindable-hotkeys apply this to tasks

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-gamesettings]: KSA.GameSettings (decompiled, build 2026.10.10.5554)
[^purrtty-terminalmod]: purrtty/purrTTY.GameMod/TerminalMod.cs
[^purrtty-ghosttyterminalcontroller]: purrtty/purrTTY.Display/Ghostty/GhosttyTerminalController.cs
[^purrtty-patcher]: purrtty/purrTTY.GameMod/Patcher.cs
