---
type: Mod Feature
title: OSC 52 clipboard writes into the game clipboard
description: Tee the PTY byte stream through a small managed OSC scanner so remote and local programs can set the system clipboard via OSC 52, applied with ImGui.SetClipboardText on the main thread
mod: purrtty
category: external-io
techniques: [imgui-window]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui]
game_types: [Brutal.ImGuiApi.ImGui, KSA.ImGuiBackendGlfwImpl]
tags: [clipboard, osc52, escape-sequences, terminal, imgui]
---

Copying from a program inside a purrTTY terminal (tmux, nvim with `clipboard=unnamedplus` over
OSC 52, or a shell in the gatOS VM reached over SSH) lands on the host clipboard, so the player can
paste it into another app or into a KSA text field. libghostty-vt does not surface OSC 52 payloads as
a callback, so purrTTY scans the byte stream itself before handing it to the engine.

## How it works

- `OscSidecar.Feed(span)` is called in `GhosttyTerminalSurface.BuildFrame` on exactly the bytes
  about to go into `VTWrite`. In ground state it jumps to the next `ESC` with a vectorized
  `IndexOf`, so bulk output costs almost nothing; inside `ESC ]` it collects up to 64 KiB of payload
  until `BEL` or `ESC \`.[^osc-sidecar][^surface]
- `CAN`/`SUB` abort a sequence (the surface's inbox-overflow heal relies on this), a payload over
  64 KiB is dropped entirely rather than dispatched truncated (a cut on a base64 boundary would
  silently paste a prefix), and interest is decided on raw bytes (`52;` or `1;`) before any string
  is allocated, because prompts emit many uninteresting OSCs (title, pwd, prompt marks).
- `OSC 52 ; target ; base64` raises `ClipboardRequested` with decoded UTF-8 text; `?` raises a query
  (`Text == null`). `OSC 1` raises `IconNameChanged`.
- `TerminalWindow.WireSession` subscribes and calls `ImGui.SetClipboardText(text)` for non-empty
  text. Clipboard queries are not answered yet (comment in `WireSession`), and the in-world renderer
  does not subscribe, so only 2D windows apply OSC 52.[^terminal-window]
- KSA's GLFW platform backend serves ImGui's clipboard from `Glfw.Clipboard`, which is the OS
  clipboard.[^ksa-glfw-backend]

## Lifecycle and timing

Both the scan and the event run inside `BuildFrame`, so on the KSA main thread in `frame:draw-ui`
(`[StarMapAfterGui]`). That makes the `ImGui.SetClipboardText` call legal without any marshaling.
The subscription is made in the session configurator, before the session is published to the tick.

## Key code

| Path | Role |
|---|---|
| [purrTTY.Terminal/Ghostty/OscSidecar.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Ghostty/OscSidecar.cs#L45-L139) | Framing state machine |
| [purrTTY.Terminal/Ghostty/OscSidecar.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Ghostty/OscSidecar.cs#L148-L227) | Dispatch, overflow discard, base64 decode |
| [purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs#L774-L778) | Tee before `VTWrite` |
| [purrTTY.Display/Ghostty/TerminalWindow.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.cs#L279-L295) | Subscription and `SetClipboardText` |
| [purrTTY.Terminal/ITerminalSurface.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/ITerminalSurface.cs#L6-L14) | `ClipboardRequest` payload type |

## Snippets

```csharp
// purrTTY.Terminal/Ghostty/OscSidecar.cs L148-L184
    private void Dispatch()
    {
        if (_overflowed)
        {
            // Truncated payload — drop the whole sequence (see _overflowed).
            _payload.Clear();
            _overflowed = false;
            return;
        }

        var payload = CollectionsMarshal.AsSpan(_payload);

        // Decide interest in raw bytes before materializing any string: shell
        // prompts emit ignored OSCs (0/2 title, 7 pwd, 133 prompt marks)
        // continuously, and this runs on the tick thread.
        bool isIcon = payload.Length >= 2 && payload[0] == (byte)'1' && payload[1] == (byte)';';
        bool isClipboard = payload.Length >= 3
            && payload[0] == (byte)'5' && payload[1] == (byte)'2' && payload[2] == (byte)';';
        if (!isIcon && !isClipboard)
        {
            _payload.Clear();
            return;
        }

        var text = Encoding.UTF8.GetString(payload);
        _payload.Clear();

        if (isIcon)
        {
            // OSC 1 ; <icon name>
            IconNameChanged?.Invoke(text[2..]);
        }
        else
        {
            DispatchClipboard(text, firstSep: 2);
        }
    }
```

```csharp
// purrTTY.Display/Ghostty/TerminalWindow.cs L283-L295
        // OSC 52: an app asked to set the system clipboard. (A clipboard *query*
        // — Text == null — would need an OSC 52 reply written back to the PTY;
        // that round-trip is deferred.)
        session.Surface.ClipboardRequested += OnClipboardRequested;
    }

    private static void OnClipboardRequested(PurrTTY.Terminal.ClipboardRequest request)
    {
        if (!string.IsNullOrEmpty(request.Text))
        {
            ImGui.SetClipboardText(request.Text);
        }
    }
```

## Game APIs touched

| API | Where | Purpose |
|---|---|---|
| `ImGui.SetClipboardText(ImString)` | `Brutal.ImGuiApi.ImGui` | Writes the system clipboard through the platform backend |
| `ImGuiBackendGlfwImpl` clipboard callbacks | [current/decomp/KSA/ImGuiBackendGlfwImpl.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/ImGuiBackendGlfwImpl.cs#L415-L424) | Backs ImGui's clipboard with `Glfw.Clipboard` |

## Pitfalls and version notes

- OSC 52 lets any program in the terminal overwrite the clipboard; purrTTY applies it without a
  prompt.
- Only the set direction is implemented; OSC 52 reads (`?`) are surfaced but not replied to.
- Keep the scanner allocation-free in ground state; it sees every byte of every session.

## See also

- [Headless terminal surface and frame snapshot](./headless-terminal-surface-and-frame-snapshot.md)
- [ImGui keyboard and mouse to terminal input](./imgui-keyboard-mouse-to-vt-input.md) (Ctrl+Shift+C/V)
- [purrTTY overview](../overview.md)
- [OscSidecar tee on the bytes about to reach the engine, clipboard write on the tick](../../../howto/external-io/terminals/let-a-terminal-program-set-the-system-clipboard/let-a-terminal-program-set-the-system-clipboard.md) - how-to article demonstrating this feature

[^osc-sidecar]: purrTTY.Terminal/Ghostty/OscSidecar.cs
[^surface]: purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs
[^terminal-window]: purrTTY.Display/Ghostty/TerminalWindow.cs
[^ksa-glfw-backend]: KSA.ImGuiBackendGlfwImpl (decompiled, build 2026.10.10.5554)
