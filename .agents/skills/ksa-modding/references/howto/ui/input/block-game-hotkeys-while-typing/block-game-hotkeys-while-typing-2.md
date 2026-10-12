---
type: How-To
title: "Block game hotkeys while typing: gate game input while a terminal has focus"
description: Keep all game input away from a focused window that has no text widget by prefixing Program.OnKey, GameSettings.OnKeyAll and DispatchKeyEvent behind one flag, with a held-key model that never leaks a toggle
category: ui
group: input
topic: block-game-hotkeys-while-typing
approach: 2
difficulty: advanced
prerequisites: [starmap, harmony]
demonstrated_by: [mods/purrtty/features/gate-game-input-while-terminal-focused]
techniques: [harmony-prefix, starmap-lifecycle]
hooks:
  - harmony-prefix KSA.Program.OnKey
  - harmony-prefix KSA.GameSettings.OnKeyAll
  - harmony-prefix KSA.Program.DispatchKeyEvent
  - starmap StarMapAfterGui
lifecycle: [frame:input, frame:draw-ui, startup:all-mods-loaded]
game_types: [KSA.GameSettings, KSA.Program, KSA.InputAction, Brutal.ImGuiApi.ImGuiIOPtr]
tags: [hotkeys, input, focus, terminal, harmony, held-keys, mouse-bindings]
---
`WantTextInput` ([approach 1](block-game-hotkeys-while-typing-1.md)) only covers ImGui text widgets. purrTTY draws a terminal as a window with no text widget at all: it reads keys itself.
ImGui therefore does not claim the keyboard, and without help typing `ls` into a shell would also stage, throttle or toggle game UI. purrTTY closes every path with three small Harmony
prefixes driven by one flag, "a terminal owns the keyboard", and tracks key state so that held game keys are always released and toggles bound to key release never leak.
Demonstrated by purrTTY. Prerequisites: StarMap and Harmony.

## Background: how the game does it

1. `Program` subscribes `OnKey` and `OnMouseButton` directly to the main window's GLFW events at startup; KSA's ImGui GLFW backend also receives every key and feeds ImGui, forwarding to
   `Program.OnKey` itself only for secondary platform windows when `ImGuiBackend.InputFallthrough` is set.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L786-L792)[^ksa-program][current/decomp/KSA/ImGuiBackendGlfwImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendGlfwImpl.cs#L431-L434)[^ksa-imguibackendglfwimpl]
2. `Program.OnKey` dispatches only if `GameSettings.OnKeyAll`, `Popup.OnKeyAll` and `ConsoleWindow.OnKey` all return false, the console is closed and `ImGui.GetIO().WantCaptureKeyboard` is false.
   Then it calls the private `DispatchKeyEvent`, whose `GlfwKeyAction.Release` arm fires toggles such as `ToggleUi`, `ToggleFps`, `ToggleThreadProfiler` and `Screenshot`.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L1745-L1800)[^ksa-program]
3. `GameViewport.DrawImGui` calls `ImGui.SetNextFrameWantCaptureKeyboard(false)` while the 3D view is hovered and no text field is active, so the game keeps keyboard control there even if a
   window is focused.[current/decomp/KSA/GameViewport.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/GameViewport.cs#L238-L246)[^ksa-gameviewport]
4. `Program.OnMouseButton` sends mouse buttons bound to game actions straight to `DispatchKeyEvent` (`Input.MouseBound(button)`), bypassing `OnKey` and `OnKeyAll`.[current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2003-L2022)[^ksa-program]

## Lifecycle and timing

- Key and mouse callbacks run during `Glfw.PollEvents()` in `Program.PrepareFrame` (`frame:input`), before this frame's UI exists. The prefixes therefore read the focus flag computed in the previous
  frame's `frame:draw-ui`, which is exactly the focus the player saw when pressing the key.
- The flag is written in the mod's `[StarMapAfterGui]` method (`frame:draw-ui`) through `_controller.Render()`, which is called every frame **even when the terminal is hidden**: the hidden early-out is the
  only place that clears the flag, and skipping it would leave the game with no keyboard at all.
- StarMap's GUI hook does not fire while `Program.DrawUI` is false, so with the HUD hidden both flags keep the value from the last HUD-visible frame (inferred from the code; in practice the stale
  value is the correct one, since the HUD toggle is itself a key the gate swallows while a terminal owns the keyboard).
- The patches are applied at `startup:all-mods-loaded`. See
  [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Compute the flag every frame.** True when any visible terminal window has ImGui focus or its context menu is open (the context menu steals focus but the player is still interacting). Clear it on
   every path that stops drawing:

```csharp
// purrTTY.Display/Ghostty/GhosttyTerminalController.cs L381-L387
public void Render()
{
    if (_disposed || !_isVisible)
    {
        _anyTerminalActive = false;
        return;
    }
```
```csharp
// purrTTY.Display/Ghostty/GhosttyTerminalController.cs L429-L442
    bool hideChrome = _config.HideUiWhenNotHovered;
    bool anyActive = false;
    for (int i = 0; i < _windows.Count; i++)
    {
        var window = _windows[i];
        window.Render(hideChrome, _cursorOn);
        // The grid context menu steals ImGui focus from its window while
        // open, but the user is still interacting with the terminal — keep
        // the game-key gate engaged or hotkeys fire under the popup.
        anyActive |= window.HasFocus || window.IsContextMenuOpen;
    }

    _anyTerminalActive = anyActive;
}
```

   Call `Render()` from the GUI hook unconditionally; the early-out is what releases the keyboard when the terminal hides.
2. **Combine the sources into one property** and keep the combined meaning in a single place:

```csharp
// purrTTY.GameMod/Patcher.cs L168-L172
// The terminal owns the keyboard while a 2D window is focused OR the in-world
// quad is focused — game keys are gated and routed to a shell in either case.
// Shared with Patch04_MouseBindingGate so mouse-bound game actions gate identically.
internal static bool TerminalOwnsKeyboard =>
  GhosttyTerminalController.IsAnyTerminalActive || InWorldTerminalManager.IsInputFocused;
```

3. **Guard `OnKeyAll` for text fields and in-world focus** (the approach 1 prefix, extended). A null console is treated as closed, because the console can be unassigned very early in startup:

```csharp
// purrTTY.GameMod/Patcher.cs L135-L152
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

4. **Gate `OnKey` with a held-key model.** The game may only see a key event for a key whose **press** it received. Swallowing every release strands a held movement key down (the game saw the press before
   focus moved); forwarding every release leaks release-triggered toggles whose press the terminal ate (releasing an F-key typed into a terminal would toggle game UI). So record the keys whose press was forwarded
   and forward a release/repeat only for those:

```csharp
// purrTTY.GameMod/Patcher.cs L154-L163,L174-L176,L191-L216
[HarmonyPatch(typeof(KSA.Program))]
class Patch01
{
  // Keys whose PRESS was forwarded to KSA.Program.OnKey (i.e. pressed while no
  // terminal was capturing input). This models the down-state the game believes
  // each key holds, so a RELEASE is forwarded only for a key the game actually
  // saw go down. Same idea as the per-button _appMousePressSent gating in
  // TerminalWindow.Input. Accessed only from Prefix1, which runs on the GLFW
  // poll (tick) thread — no locking needed.
  private static readonly HashSet<GlfwKey> s_gameHeldKeys = new();
// ...
  [HarmonyPrefix]
  [HarmonyPatch(nameof(KSA.Program.OnKey))]
  static bool Prefix1(GlfwWindow window, GlfwKey key, int scanCode, GlfwKeyAction action, GlfwModifier mods)
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

5. **Gate mouse-bound game actions** (build 2026.9.22 and later). Patch the private `DispatchKeyEvent` by string name, in its own class so a rename cannot take the keyboard gate down with it. Keyboard events pass (already
   gated); a mouse press while the terminal owns the keyboard is swallowed and remembered, and only its release is swallowed. This side tracks the **swallowed** set, because it only sees presses that survived
   `OnMouseButton`'s own filters, so an unknown release is ordinary game traffic and must pass:

```csharp
// purrTTY.GameMod/Patcher.cs L248-L252,L259-L293
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

6. **Reset the statics on unload.** Each patch class has a `Reset()` called from `Patcher.unload()`, because statics survive a StarMap reload that does not unload the assembly load context
   ([purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L82-L84)).

## Complete example

| File | Role |
|---|---|
| [purrTTY.GameMod/Patcher.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/Patcher.cs#L135-L294) | `Patch03_HotkeyGuard`, `Patch01`, `Patch04_MouseBindingGate` |
| [purrTTY.Display/Ghostty/GhosttyTerminalController.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/GhosttyTerminalController.cs#L381-L442) | Computes and clears `IsAnyTerminalActive` |
| [purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L68-L117) | Calls `Render()` unconditionally from the GUI hook |
| [purrTTY.GameMod/InWorld/InWorldTerminalManager.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/InWorld/InWorldTerminalManager.cs) | `IsInputFocused` for in-world terminals |

## Variations and gotchas

- **`DispatchKeyEvent` is private** and patched by string name; the original takes `in GlfwKeyEvent`, and the prefix declares it by value, which Harmony supports for read-only use.
- **Swallowing a press in `Patch01` skips the whole original**, including its `ShiftDown`/`AltDown` bookkeeping for that event, and skipping `GameSettings.OnKeyAll` skips its `Cancel` handling and frame counter for that key.
- **Clear the flag on every path that stops drawing** (hide, close-all, dispose), otherwise the game keyboard dies until the terminal is shown again.
- **Mouse-button bindings** exist only on builds that added them; on older builds `Patch04` is unnecessary but harmless.
- **Version.** Main-window keys reach `Program.OnKey` through `Program`'s own GLFW subscription in 2026.10.10.5554; gating `OnKey` itself, as purrTTY does, works either way.

## See also

- [Overview of block-game-hotkeys-while-typing](overview.md)
- [Approach 1: prefix on GameSettings.OnKeyAll when ImGui wants text input](block-game-hotkeys-while-typing-1.md)
- [Share the keyboard and mouse between ImGui, mod tools and the game](../coordinate-imgui-focus-with-game-input/overview.md)
- [Pick objects under the cursor, approach 3](../pick-objects-under-the-cursor/pick-objects-under-the-cursor-3.md)
- [Isolating patch failures and keeping patch bodies from throwing](../../../patching/safety/isolate-patch-failures-and-fault-proof-patch-bodies/overview.md)
- [Persist mod settings as TOML](../../../persistence/settings-formats/persist-mod-settings-toml/overview.md)
- Mod feature: [Gate game input while a terminal is focused](../../../../mods/purrtty/features/gate-game-input-while-terminal-focused.md)

[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^ksa-imguibackendglfwimpl]: KSA.ImGuiBackendGlfwImpl (decompiled, build 2026.10.10.5554)
[^ksa-gameviewport]: KSA.GameViewport (decompiled, build 2026.10.10.5554)
