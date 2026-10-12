---
type: Mod Feature
title: ImGui keyboard and mouse to terminal input
description: Turn ImGui key presses, the character queue, clipboard chords and mouse gestures into VT byte sequences via the engine encoders, with selection, app mouse reporting and AltGr handling
mod: purrtty
category: ui
techniques: [imgui-window, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:input, frame:draw-ui]
game_types:
  - Brutal.ImGuiApi.ImGui
  - Brutal.ImGuiApi.ImGuiIOPtr
  - Brutal.ImGuiApi.ImGuiKey
  - KSA.ImGuiBackendGlfwImpl
  - KSA.Program
tags: [imgui, input, keyboard, mouse, clipboard, terminal, vt]
---

A focused purrTTY window behaves like a desktop terminal: typing, Ctrl/Alt chords, function keys,
AltGr characters, emoji, Ctrl+Shift+C/V, right-click Copy/Paste, click/double/triple-click
selection with auto-scroll, wheel scrollback, and full mouse reporting for tmux, nvim or htop. All
of it is read from ImGui's IO in the same frame the window is drawn; nothing hooks GLFW directly.
The VT encoding itself is delegated to libghostty-vt's key, mouse and paste encoders, so modes such
as the kitty keyboard protocol, bracketed paste and mouse tracking modes are honored.

## How it works

**Keyboard** (`TerminalInputEncoder.ProcessKeyboard`, shared by 2D windows and in-world terminals,
reading whichever ImGui context is current):

| Input | Source in ImGui | Path |
|---|---|---|
| Ctrl+Shift+V / Ctrl+Shift+C | `io.KeyCtrl && io.KeyShift && ImGui.IsKeyPressed(V/C)` | `ImGui.GetClipboardText` -> `Surface.EncodePaste` (bracketed paste aware), or selection -> `ImGui.SetClipboardText`; never sent to the shell |
| Named keys (Enter, arrows, Home/End, PgUp/PgDn, F1-F12, ...) | `ImGui.IsKeyPressed(key)` (repeat on) | `TerminalKeyEvent` -> `Surface.EncodeKey` |
| Ctrl/Alt + letter, digit, Space, `[`, `\`, `]` | `IsKeyPressed` while Ctrl or Alt is held | `EncodeKey` (control bytes, Meta prefix, or kitty protocol CSI u) |
| AltGr text (Windows international layouts) | Ctrl+Alt held and `io.InputQueueCharacters.Count > 0` | Treated as text, not a chord |
| Printable text | `io.InputQueueCharacters` | UTF-8 encode, pairing UTF-16 surrogates into one code point; control chars dropped |

`PURRTTY_KEY_DIAG=1` logs every chord and the bytes sent.

**Mouse** (`TerminalWindow.Input`):

- When the app has mouse tracking enabled and Shift is not held, `HandleAppMouse` sends
  press/release/motion/wheel reports. The cell is computed with the real fractional cell size, then
  re-expressed as the center of that cell in integer metrics, because the engine divides pixels by
  the integer cell size pushed with `SetMouseGeometry` (gotcha 10). Presses require hovering the
  grid; a release is sent only if its press was (`_appMousePressSent`). Motion is offered only when
  the pointer crosses into a new cell; the engine encoder decides whether the active mode wants it.
- Otherwise: wheel scrolls scrollback (`ScrollBy`), single click anchors a selection that
  materializes on drag (`BeginSelectCells` / `ExtendSelectCells`, auto-scrolling when the pointer
  leaves the grid), double click selects a word, triple click a line, right click opens a
  Copy/Paste popup. Shift forces this path even under mouse tracking (xterm behavior).

All produced bytes go to `session.SendInput`, which queues them for the PTY writer thread
(see [PTY threads](./pty-threads-to-frame-tick-handoff.md)); the window raises a payload-free
`InputSent` event that resets the shared cursor-blink phase.

**Swallowing the toggle key.** When the toggle hotkey opens the terminal, `TerminalMod` sets a flag
that the window's `KeyboardSuppression` callback consumes, so a printable toggle key does not also
type into the shell on the same frame.[^terminal-mod]

## Lifecycle and timing

- GLFW key and char callbacks fire during `Glfw.PollEvents()` in `Program.PrepareFrame`
  (`frame:input`). KSA's `ImGuiBackendGlfwImpl.OnKey` / `OnChar` push them into ImGui with
  `AddKeyEvent` / `AddInputCharacter`.[^ksa-glfw-backend] `ImGui.NewFrame()` in `PrepareImGui`
  turns them into this frame's key state and character queue.[^ksa-program]
- purrTTY reads them in `frame:draw-ui` (`[StarMapAfterGui]`), inside the window's `Begin`/`End`,
  only when that window has ImGui focus (`HandleInput` is gated on `_hasFocus`).[^terminal-window]
  `IsKeyPressed` and the character queue are valid for the whole frame, so reading them late in the
  UI pass is safe.
- Encoding calls the native engine and must run on the thread that ticks it (the main thread here).
  The resulting bytes cross to a PTY writer thread through a bounded queue.
- The game itself still receives the same GLFW key events through `Program.OnKey`; stopping that is
  a separate mechanism (see [input gating](./gate-game-input-while-terminal-focused.md)).

## Key code

| Path | Role |
|---|---|
| [purrTTY.Display/Ghostty/TerminalInputEncoder.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalInputEncoder.cs#L87-L193) | Keyboard: clipboard chords, named keys, Ctrl/Alt chords, AltGr, text |
| [purrTTY.Display/Ghostty/TerminalWindow.Input.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.Input.cs#L46-L109) | Selection gestures, wheel scrollback, context-menu open |
| [purrTTY.Display/Ghostty/TerminalWindow.Input.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.Input.cs#L137-L228) | App mouse reporting with integer-metric synthesis |
| [purrTTY.Display/Ghostty/TerminalWindow.Input.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.Input.cs#L304-L332) | Copy and paste through the ImGui clipboard |
| [purrTTY.Display/Ghostty/TerminalWindow.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.cs#L432-L439) | `SetMouseGeometry` with integer cell metrics |
| [purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L88-L100) | Toggle hotkey and first-frame keyboard suppression |
| [purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs) | `EncodeKey` / `EncodeMouse` / `EncodePaste` over the engine encoders |

## Snippets

AltGr and Ctrl/Alt chords versus the character queue:

```csharp
// purrTTY.Display/Ghostty/TerminalInputEncoder.cs L123-L161
        // AltGr (Windows international layouts) is delivered as Ctrl+Alt with the
        // produced character in the ImGui character queue. When both are held AND
        // text arrived, the text is what the user typed (@, {, €, ...) — prefer it
        // over a spurious Ctrl+Alt chord (the standard terminal heuristic).
        bool altGrText = io.KeyCtrl && io.KeyAlt && io.InputQueueCharacters.Count > 0;

        // Ctrl/Alt-modified keys never enter the ImGui character queue, so they are
        // encoded from key presses: Ctrl+letter controls, Alt+letter Meta chords,
        // Ctrl/Alt+digit, and the Ctrl punctuation controls (NUL/ESC/FS/GS).
        if ((io.KeyCtrl || io.KeyAlt) && !altGrText)
        {
            foreach (var (imguiKey, key) in LetterKeys)
            {
                if (ImGui.IsKeyPressed(imguiKey))
                {
                    EncodeAndSend(session, new TerminalKeyEvent(key, KeyAction.Press, mods), onInputSent);
                }
            }
            // ...
        }

        // Printable text. Skip when Ctrl/Alt are held (command combos handled above)
        // unless it's AltGr input, where the queued characters ARE the typed text.
        if (((!io.KeyCtrl && !io.KeyAlt) || altGrText) && io.InputQueueCharacters.Count > 0)
```

Mouse position synthesized in the engine's integer cell metrics:

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.Input.cs L141-L177
        var rawPos = ImGui.GetMousePos() - canvasPos;
        var mods = ReadModifiers(io);

        // Track which cell the pointer is over so motion below can fire only on a
        // cell change. Updated every frame (incl. button-edge frames) so the next
        // move is measured from the current cell, never a stale one. The cell is
        // computed with the *real* (fractional) cell metrics.
        int col = Math.Clamp((int)(rawPos.X / _cellWidth), 0, Math.Max(0, cols - 1));
        int row = Math.Clamp((int)(rawPos.Y / _cellHeight), 0, Math.Max(0, rows - 1));
        bool cellChanged = col != _appMouseCol || row != _appMouseRow;
        _appMouseCol = col;
        _appMouseRow = row;
        // ...
        int cellW = Math.Max(1, (int)_cellWidth);
        int cellH = Math.Max(1, (int)_cellHeight);
        var pos = new float2(col * cellW + cellW * 0.5f, row * cellH + cellH * 0.5f);

        // Press is gated on hover (the click must land on the grid); release fires
        // ungated so a drag that ends off-grid still reports button-up — but only
        // when the matching press was forwarded, so a click that started on the
        // game UI cannot leak a spurious release report.
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _appMousePressSent[(int)MouseButton.Left] = true;
            EncodeMouseAndSend(session, MouseAction.Press, MouseButton.Left, mods, pos);
        }
        else if (_appMousePressSent[(int)MouseButton.Left] && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            _appMousePressSent[(int)MouseButton.Left] = false;
            EncodeMouseAndSend(session, MouseAction.Release, MouseButton.Left, mods, pos);
        }
```

## Game APIs touched

| API | Where | Purpose |
|---|---|---|
| `ImGuiIOPtr.KeyCtrl/KeyShift/KeyAlt`, `InputQueueCharacters`, `MouseWheel` | [current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs) | Modifier state, typed text, wheel[^ksa-imguiio] |
| `ImGui.IsKeyPressed`, `IsMouseClicked/Released/Dragging/Down`, `GetMouseClickedCount`, `GetMousePos` | [current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs) | Edge-triggered key and mouse reads[^ksa-imgui] |
| `ImGui.GetClipboardText` / `SetClipboardText` | same | System clipboard (KSA's GLFW backend serves it from `Glfw.Clipboard`) |
| `ImGuiBackendGlfwImpl.OnKey` / `OnChar` | [current/decomp/KSA/ImGuiBackendGlfwImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendGlfwImpl.cs#L496-L527) | Where GLFW input enters ImGui |
| `Program.PrepareFrame` (`Glfw.PollEvents`), `Program.PrepareImGui` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2133-L2181) | When input is polled and the ImGui frame begins |

## Pitfalls and version notes

- Ctrl/Alt chords never reach the ImGui character queue, so they must be encoded from key presses;
  conversely plain text must come from the queue (layout-aware), not from key codes.
- For the kitty keyboard protocol the engine needs the unshifted codepoint on letter/digit key
  events or it silently emits nothing (gotcha 3; set in `GhosttyTerminalSurface`).[^gotchas]
- Raw float pixels for mouse reports drift columns on wide grids when the cell width is fractional.
- `ImGui.IsKeyPressed` defaults to `repeat: true`; that is what you want for typing but not for a
  toggle hotkey (see [toggle hotkey](./rebindable-toggle-hotkey.md)).
- Paste uses the engine's paste encoder so bracketed paste mode is respected; never write clipboard
  text to the PTY raw.

## See also

- [Gate game input while a terminal is focused](./gate-game-input-while-terminal-focused.md)
- [Headless terminal surface and frame snapshot](./headless-terminal-surface-and-frame-snapshot.md)
- [Vendored libghostty-vt binding](./vendored-libghostty-vt-binding.md) (the encoders)
- [Ray-pick focus for world quads](./ray-pick-focus-for-world-quads.md) (the same encoder used for in-world terminals)
- [OSC 52 clipboard sidecar](./osc52-clipboard-sidecar.md)
- [purrTTY overview](../overview.md)
- [Read typed text and key chords from ImGui: character queue versus IsKeyPressed, with clipboard paste chords](../../../howto/ui/input/read-typed-text-and-key-chords-from-imgui/read-typed-text-and-key-chords-from-imgui-1.md) - how-to article demonstrating this feature

[^terminal-mod]: purrTTY.GameMod/TerminalMod.cs
[^ksa-glfw-backend]: KSA.ImGuiBackendGlfwImpl (decompiled, build 2026.10.10.5554)
[^ksa-program]: KSA.Program (decompiled, build 2026.10.10.5554)
[^terminal-window]: purrTTY.Display/Ghostty/TerminalWindow.cs
[^ksa-imguiio]: Brutal.ImGuiApi.ImGuiIOPtr (decompiled, build 2026.10.10.5554)
[^ksa-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
[^gotchas]: purrTTY docs/gotchas.md
