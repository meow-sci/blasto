---
type: How-To
title: "Read typed text and key chords from ImGui: character queue versus IsKeyPressed, with clipboard paste chords"
description: Read printable text from ImGui's character queue, Ctrl and Alt chords from key presses, handle AltGr, and copy or paste through ImGui's GLFW-backed clipboard
category: ui
group: input
topic: read-typed-text-and-key-chords-from-imgui
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/purrtty/features/imgui-keyboard-mouse-to-vt-input]
techniques: [imgui-window, starmap-lifecycle]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:input, frame:draw-ui]
game_types:
  - Brutal.ImGuiApi.ImGuiIOPtr
  - Brutal.ImGuiApi.ImGuiKey
  - Brutal.ImGuiApi.ImGui
  - KSA.ImGuiBackendGlfwImpl
tags: [keyboard, text-input, chords, altgr, clipboard, paste, terminal, imgui]
---
purrTTY is a terminal inside the game, so a focused window must behave like a desktop terminal: typing, Ctrl/Alt chords, function keys, AltGr characters, emoji, Ctrl+Shift+C/V and right-click Copy/Paste,
plus mouse selection and mouse reporting. All of it is read from ImGui's IO in the same frame the window is drawn; nothing hooks GLFW directly. This article is the reading side: which ImGui source to use
for text versus chords, the AltGr rule, and the clipboard. What to do with the bytes (VT encoding) is purrTTY-specific and out of scope. Demonstrated by purrTTY.

## Background: how the game does it

- KSA's GLFW ImGui backend feeds ImGui: `OnKey` calls `iO.AddKeyEvent(...)` and `OnChar` calls `GetIO(_context).AddInputCharacter(codepoint)`.[current/decomp/KSA/ImGuiBackendGlfwImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendGlfwImpl.cs#L496-L527)[^ksa-imguibackendglfwimpl]
  `ImGui.NewFrame()` (in `PrepareImGui`) turns the queue into this frame's key state and `ImGuiIOPtr.InputQueueCharacters`, a vector of UTF-16 code units.[current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGuiIOPtr.cs#L236)[^brutal-imguiapi-imguiioptr]
- Ctrl/Alt-modified keys do not produce characters, so chords have to be read from key presses; plain text must come from the queue, which is layout aware, not from key codes.
- The clipboard is wired to GLFW: the backend installs `Platform_GetClipboardTextFn` / `Platform_SetClipboardTextFn`, so `ImGui.GetClipboardText()` returns `Glfw.Clipboard` and
  `ImGui.SetClipboardText(text)` calls `Glfw.SetClipboard`.[current/decomp/KSA/ImGuiBackendGlfwImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendGlfwImpl.cs#L279-L283)[^ksa-imguibackendglfwimpl][current/decomp/KSA/ImGuiBackendGlfwImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendGlfwImpl.cs#L410-L424)[^ksa-imguibackendglfwimpl][current/decomp/Brutal.ImGuiApi/ImGui.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi/ImGui.cs#L8294-L8301)[^brutal-imguiapi-imgui]
- The game still receives the same key events through `Program.OnKey`; stopping that is a separate mechanism, see
  [block game hotkeys while typing](../block-game-hotkeys-while-typing/block-game-hotkeys-while-typing-2.md).

## Lifecycle and timing

GLFW key and char callbacks fire during `Glfw.PollEvents()` in `Program.PrepareFrame` (`frame:input`); `ImGui.NewFrame()` in `PrepareImGui` makes them this frame's state. purrTTY reads them in `frame:draw-ui`
(`[StarMapAfterGui]`), inside the window's `Begin`/`End`, and only when that window has ImGui focus. `IsKeyPressed` and the character queue are valid for the whole frame. Encoding calls into a native library
that must run on the thread that ticks it (the main thread here); the produced bytes cross to a writer thread through a bounded queue. See
[Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Steps

1. **Gate on focus, then read.** The window reads ImGui IO once per frame and passes it to a shared encoder (the same code serves 2D windows and in-world terminals). An optional suppress callback skips a frame, used to
   swallow the key that opened the window:

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.cs L533-L536
if (session != null && _hasFocus)
{
    HandleInput(session, canvasPos, cols, rows, gridHovered);
}
```
```csharp
// purrTTY.Display/Ghostty/TerminalWindow.Input.cs L32-L44
private void HandleInput(TerminalSession session, float2 canvasPos, int cols, int rows, bool gridHovered)
{
    var io = ImGui.GetIO();
    HandleKeyboard(session, io);
    HandleMouse(session, io, canvasPos, cols, rows, gridHovered);
}

// Keyboard encoding is shared with the in-world terminal via TerminalInputEncoder.
private void HandleKeyboard(TerminalSession session, ImGuiIOPtr io)
{
    _raiseInputSent ??= () => InputSent?.Invoke();
    TerminalInputEncoder.ProcessKeyboard(session, io, KeyboardSuppression, _raiseInputSent);
}
```

2. **Handle clipboard chords first and never forward them.** Ctrl+Shift+V reads the system clipboard and sends it through the engine's paste encoder (bracketed-paste aware); Ctrl+Shift+C copies the selection with
   `SetClipboardText`:

```csharp
// purrTTY.Display/Ghostty/TerminalInputEncoder.cs L95-L106
// Standard terminal clipboard chords; never forwarded to the shell.
if (io.KeyCtrl && io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.V))
{
    Paste(session, onInputSent);
    return;
}

if (io.KeyCtrl && io.KeyShift && ImGui.IsKeyPressed(ImGuiKey.C))
{
    Copy(session);
    return;
}
```
```csharp
// purrTTY.Display/Ghostty/TerminalInputEncoder.cs L220-L239
private static void Copy(TerminalSession session)
{
    var text = session.Surface.GetSelectionText();
    if (!string.IsNullOrEmpty(text))
    {
        ImGui.SetClipboardText(text);
    }
}

private static void Paste(TerminalSession session, Action? onInputSent)
{
    var text = ImGui.GetClipboardText();
    if (string.IsNullOrEmpty(text))
    {
        return;
    }

    var encoded = session.Surface.EncodePaste(System.Text.Encoding.UTF8.GetBytes(text));
    Send(session, encoded, onInputSent);
}
```

3. **Named keys use `IsKeyPressed(key)` with repeat on** (what you want for typing, not for a toggle hotkey):

```csharp
// purrTTY.Display/Ghostty/TerminalInputEncoder.cs L115-L121
foreach (var (imguiKey, key) in NamedKeys)
{
    if (ImGui.IsKeyPressed(imguiKey))
    {
        EncodeAndSend(session, new TerminalKeyEvent(key, KeyAction.Press, mods), onInputSent);
    }
}
```

4. **AltGr rule.** On Windows international layouts AltGr arrives as Ctrl+Alt with the produced character in the queue. When both modifiers are held **and** text arrived, the text wins over a spurious chord.
   Chords (Ctrl/Alt with letters, digits, Space, `[`, `\`, `]`) are encoded from key presses:

```csharp
// purrTTY.Display/Ghostty/TerminalInputEncoder.cs L123-L134
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
```

5. **Printable text comes from `InputQueueCharacters`.** Skip it when Ctrl/Alt are held (unless AltGr), pair UTF-16 surrogates into one code point for emoji, and drop control characters:

```csharp
// purrTTY.Display/Ghostty/TerminalInputEncoder.cs L159-L192
// Printable text. Skip when Ctrl/Alt are held (command combos handled above)
// unless it's AltGr input, where the queued characters ARE the typed text.
if (((!io.KeyCtrl && !io.KeyAlt) || altGrText) && io.InputQueueCharacters.Count > 0)
{
    int count = io.InputQueueCharacters.Count;
    Span<char> chars = stackalloc char[2];
    Span<byte> utf8 = stackalloc byte[4];
    for (int i = 0; i < count; i++)
    {
        char ch = (char)io.InputQueueCharacters[i];

        // Astral-plane input arrives as two queue entries (a UTF-16 surrogate
        // pair); encode the pair as one code point — a lone half yields U+FFFD.
        if (char.IsHighSurrogate(ch))
        {
            if (i + 1 < count && char.IsLowSurrogate((char)io.InputQueueCharacters[i + 1]))
            {
                chars[0] = ch;
                chars[1] = (char)io.InputQueueCharacters[++i];
                Send(session, utf8[..System.Text.Encoding.UTF8.GetBytes(chars, utf8)], onInputSent);
            }

            continue; // unpaired high surrogate: drop
        }

        if (char.IsLowSurrogate(ch) || ch < 32 || ch == 127)
        {
            continue;
        }

        chars[0] = ch;
        Send(session, utf8[..System.Text.Encoding.UTF8.GetBytes(chars[..1], utf8)], onInputSent);
    }
}
```

6. **Selection gestures and the context menu.** Click anchors a selection that materializes on drag, double click selects a word, triple click a line (`GetMouseClickedCount`), right click opens a Copy/Paste popup,
   Shift forces this path under app mouse tracking:

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.Input.cs L66-L108
// Selection gestures: single-click+drag selects cells, double-click selects
// a word, triple-click selects the logical line. A plain click that never
// drags clears the selection (it falls through to ClearSelection on press
// and the extend branch never fires), matching real terminals — without
// this, holding the button for a frame painted a one-cell selection that
// could never be deselected.
if (gridHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
{
    int clicks = ImGui.GetMouseClickedCount(ImGuiMouseButton.Left);
    if (clicks >= 3)
    {
        session.Surface.SelectLine(cell);
        _selecting = false;
    }
    else if (clicks == 2)
    {
        session.Surface.SelectWord(cell);
        _selecting = false;
    }
    else
    {
        // Clear now and record the anchor, but defer materializing the
        // selection until the mouse actually drags past the threshold.
        session.Surface.ClearSelection();
        session.Surface.BeginSelectCells(cell);
        _selecting = true;
    }
}
else if (_selecting && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
{
    AutoScrollForDrag(session, canvasPos, rows);
    session.Surface.ExtendSelectCells(cell);
}
else if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
{
    _selecting = false;
}

// Right-click opens the copy/paste context menu over the grid.
if (gridHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
{
    ImGui.OpenPopup(GridContextMenuId);
}
```

7. **Mouse reporting to terminal apps** is covered in
   [pick objects under the cursor, approach 3](../pick-objects-under-the-cursor/pick-objects-under-the-cursor-3.md) (the cell is computed with fractional metrics and re-expressed as the centre of that cell in
   integer metrics).

## Complete example

| File | Role |
|---|---|
| [purrTTY.Display/Ghostty/TerminalInputEncoder.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalInputEncoder.cs) | Keyboard: clipboard chords, named keys, chords, AltGr, text |
| [purrTTY.Display/Ghostty/TerminalWindow.Input.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.Input.cs) | Selection gestures, wheel scrollback, context menu, app mouse |
| [purrTTY.GameMod/TerminalMod.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.GameMod/TerminalMod.cs#L68-L117) | The GUI hook that drives it |

## Variations and gotchas

- **Chords never reach the character queue**, so encode them from key presses; conversely, plain text must come from the queue.
- **`IsKeyPressed` defaults to `repeat: true`**, right for typing, wrong for a toggle.
- **Paste through an encoder**, never raw to a PTY, so bracketed paste is respected.
- **Diagnostics.** `PURRTTY_KEY_DIAG=1` logs every chord and the bytes sent.
- **Version.** Checked against 2026.10.10.5554.

## See also

- [Overview of read-typed-text-and-key-chords-from-imgui](overview.md)
- [Approach 2: selectable rows that copy text only on click](read-typed-text-and-key-chords-from-imgui-2.md)
- [Block game hotkeys while typing](../block-game-hotkeys-while-typing/overview.md)
- [Implement rebindable hotkeys](../rebindable-hotkeys/overview.md)
- Mod feature: [ImGui keyboard and mouse to VT input](../../../../mods/purrtty/features/imgui-keyboard-mouse-to-vt-input.md)

[^ksa-imguibackendglfwimpl]: KSA.ImGuiBackendGlfwImpl (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imguiioptr]: Brutal.ImGuiApi.ImGuiIOPtr (decompiled, build 2026.10.10.5554)
[^brutal-imguiapi-imgui]: Brutal.ImGuiApi.ImGui (decompiled, build 2026.10.10.5554)
