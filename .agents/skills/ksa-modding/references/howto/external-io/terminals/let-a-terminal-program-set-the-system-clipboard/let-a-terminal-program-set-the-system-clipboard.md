---
type: How-To
title: OscSidecar tee on the bytes about to reach the engine, clipboard write on the tick
description: Scan the terminal byte stream with an allocation-free OSC state machine, decode OSC 52 payloads and apply them with ImGui.SetClipboardText on the main thread
category: external-io
group: terminals
topic: let-a-terminal-program-set-the-system-clipboard
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/purrtty/features/osc52-clipboard-sidecar]
techniques: [imgui-window, background-thread]
hooks: [starmap StarMapAfterGui]
lifecycle: [frame:draw-ui, thread:background]
game_types: [KSA.ImGuiBackendGlfwImpl, Brutal.ImGuiApi.ImGui]
tags: [terminal, osc52, clipboard, escape-sequences, scanner, imgui]
---

Outcome: a program running inside a terminal tab (tmux, a remote editor, a shell in a VM reached over SSH) copies text with the OSC 52 escape sequence and the text lands on the player's system clipboard, so it can be pasted into another application or into a game text field. The terminal engine does not hand OSC 52 payloads to the host, so the mod scans the byte stream itself with a small allocation-free state machine and applies the result on the main thread. Prerequisites: a terminal surface that ticks on the main thread ([Host a terminal with pty sessions and frame handoff](../host-a-terminal-with-pty-sessions-and-frame-handoff/overview.md)). Demonstrated by [OSC 52 clipboard sidecar](../../../../mods/purrtty/features/osc52-clipboard-sidecar.md) in purrTTY.

## Background: how the game does it

KSA exposes the clipboard through ImGui. `ImGui.SetClipboardText(ImString)` calls the native function, and KSA's GLFW platform backend registers the clipboard callbacks on ImGui's platform IO, so a mod never talks to GLFW or the operating system directly:

```csharp
// current/decomp/KSA/ImGuiBackendGlfwImpl.cs L282-L283
		platformIO.Platform_GetClipboardTextFn = Marshal.GetFunctionPointerForDelegate(_getClipboardTextFunction);
		platformIO.Platform_SetClipboardTextFn = Marshal.GetFunctionPointerForDelegate(_setClipboardTextFunction);
```

```csharp
// current/decomp/KSA/ImGuiBackendGlfwImpl.cs L410-L414
	private unsafe void SetClipboardText(ImGuiContextPtr context, byte* text)
	{
		ReadOnlySpan<byte> nullTerminatedSpan = Utf8.GetNullTerminatedSpan(text);
		Glfw.SetClipboard(Utf8.GetString(nullTerminatedSpan));
	}
```

The game itself uses the same call for its "copy" buttons, for example `CameraController` copies a position vector with `ImGui.SetClipboardText(clipboardText)` ([current/decomp/RenderCore.Input.Controllers/CameraController.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/RenderCore.Input.Controllers/CameraController.cs#L37)). ImGui calls are legal on the main thread between `ImGui.NewFrame` and `ImGui.Render`, which is why the clipboard write must happen in the UI tick (see the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#the-imgui-window)).

## Lifecycle and timing

| Work | Where |
|---|---|
| Bytes arrive from the pty or SSH pump | `thread:background`, into the surface inbox |
| `OscSidecar.Feed` over the bytes about to reach the engine | inside `BuildFrame`, on the main thread at `frame:draw-ui` (`StarMapAfterGui`) |
| `ClipboardRequested` event | raised from that same call, so on the tick thread |
| `ImGui.SetClipboardText` | the event handler, on the tick thread; no marshalling needed |

The scan is not on the pump thread: the surface tees the drained inbox into the sidecar and then into the engine. With the HUD hidden nothing ticks, so no clipboard request is applied until the HUD returns.

## Steps

1. **Scan the stream yourself.** The sidecar frames OSC sequences (`ESC ]` to `BEL` or `ESC \`) and parses only what the engine does not expose. Its class comment states the scope:

   ```csharp
   // purrTTY.Terminal/Ghostty/OscSidecar.cs L7-L43
   /// A managed scanner that tees the raw terminal output stream and surfaces the
   /// two OSC effects libghostty does not expose as engine callbacks: OSC 52
   /// clipboard and OSC 1 icon-name. Title (OSC 0/2) is handled by the engine's
   /// title callback, so it is intentionally ignored here.
   ///
   /// This frames OSC sequences (<c>ESC ]</c> … <c>BEL</c>/<c>ST</c>) itself and
   /// parses the payload directly — simpler and sufficient for these two effects
   /// than round-tripping through the engine's OSC parser, which does not expose
   /// clipboard payload extraction.
   /// </summary>
   internal sealed class OscSidecar
   {
       private const int MaxPayload = 64 * 1024;
       private const byte Esc = 0x1B;
       private const byte Bel = 0x07;
       private const byte Can = 0x18;
       private const byte Sub = 0x1A;

       private enum State
       {
           Ground,
           Esc,
           Osc,
           OscEsc,
       }

       private State _state = State.Ground;
       private readonly List<byte> _payload = new(256);

       // Set when payload bytes were dropped at MaxPayload. A truncated sequence
       // is never dispatched: a clipped OSC 52 whose cut lands on a base64
       // 4-char boundary would otherwise decode cleanly and silently replace the
       // user's clipboard with a prefix of what the app sent.
       private bool _overflowed;

       public event Action<string>? IconNameChanged;
       public event Action<ClipboardRequest>? ClipboardRequested;
   ```

2. **Skip ground state with a vectorized search.** Bulk output is almost entirely ground state, so `Feed` jumps to the next ESC with `IndexOf` instead of walking every byte through the state machine:

   ```csharp
   // purrTTY.Terminal/Ghostty/OscSidecar.cs L45-L70
       public void Feed(ReadOnlySpan<byte> data)
       {
           int i = 0;
           while (i < data.Length)
           {
               // Ground is the overwhelmingly common state for bulk output;
               // vectorized IndexOf skips straight to the next ESC instead of
               // walking every byte through the state machine.
               if (_state == State.Ground)
               {
                   int esc = data[i..].IndexOf(Esc);
                   if (esc < 0)
                   {
                       return;
                   }

                   i += esc;
                   _state = State.Esc;
                   i++;
                   continue;
               }

               FeedByte(data[i]);
               i++;
           }
       }
   ```

3. **Collect the payload with a hard cap and abort rules.** Inside an OSC, collect bytes until `BEL` or `ESC \`. `CAN` and `SUB` abort the sequence, which the surface's inbox-overflow heal relies on. Past 64 KiB the payload is flagged and the whole sequence is dropped at dispatch, never delivered truncated, because a cut on a base64 boundary would decode cleanly and silently paste a prefix:

   ```csharp
   // purrTTY.Terminal/Ghostty/OscSidecar.cs L96-L137
               case State.Osc:
                   if (b == Bel)
                   {
                       Dispatch();
                       _state = State.Ground;
                   }
                   else if (b == Esc)
                   {
                       _state = State.OscEsc;
                   }
                   else if (b is Can or Sub)
                   {
                       // CAN/SUB abort the control string (DEC/xterm behavior).
                       // The inbox-overflow heal sequence (CAN + ST, gotcha 18)
                       // relies on this: a partial OSC interrupted by a drop must
                       // be abandoned, never dispatched with stray bytes inside.
                       AbandonSequence();
                   }
                   else if (_payload.Count < MaxPayload)
                   {
                       _payload.Add(b);
                   }
                   else
                   {
                       _overflowed = true;
                   }
                   break;

               case State.OscEsc:
                   if (b == (byte)'\\')
                   {
                       // ESC \ = ST terminator
                       Dispatch();
                       _state = State.Ground;
                   }
                   else
                   {
                       // Not a string terminator; abandon this OSC.
                       AbandonSequence();
                       _state = b == Esc ? State.Esc : State.Ground;
                   }
                   break;
   ```

4. **Decide interest on raw bytes before allocating.** Prompts emit many OSCs the sidecar does not care about (title, working directory, prompt marks), so `Dispatch` checks the first bytes for `1;` or `52;` and only then builds a string:

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

5. **Decode OSC 52.** The payload is `52 ; <targets> ; <base64>`. An empty target means the clipboard, `?` is a query and is surfaced with `Text == null`, anything else is base64-decoded as UTF-8 and ignored if it is not valid base64:

   ```csharp
   // purrTTY.Terminal/Ghostty/OscSidecar.cs L186-L215
       private void DispatchClipboard(string text, int firstSep)
       {
           // OSC 52 ; <target(s)> ; <base64 | ?>
           int secondSep = text.IndexOf(';', firstSep + 1);
           if (secondSep < 0)
           {
               return;
           }

           var target = text[(firstSep + 1)..secondSep];
           var data = text[(secondSep + 1)..];

           if (target.Length == 0)
           {
               target = "c";
           }

           if (data == "?")
           {
               // Clipboard read request (paste-from-clipboard). Text == null signals a query.
               ClipboardRequested?.Invoke(new ClipboardRequest { Target = target, Text = null });
               return;
           }

           string? decoded = TryDecodeBase64(data);
           if (decoded is not null)
           {
               ClipboardRequested?.Invoke(new ClipboardRequest { Target = target, Text = decoded });
           }
       }
   ```

6. **Tee exactly the bytes the engine will see.** Inside `BuildFrame` the sidecar is fed the drained span just before `VTWrite`, and the surface forwards its event as part of `ITerminalSurface`:

   ```csharp
   // purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs L295-L296, L777-L778
           _osc.IconNameChanged += name => IconNameChanged?.Invoke(name);
           _osc.ClipboardRequested += req => ClipboardRequested?.Invoke(req);
   // ...
               var span = _scratch.AsSpan(0, inputLen);
               _osc.Feed(span);
   ```

7. **Apply the request on the tick.** The window subscribes when the session is configured (before the session is published) and writes non-empty text with `ImGui.SetClipboardText`. A query would need a reply written back to the pty and is not implemented:

   ```csharp
   // purrTTY.Display/Ghostty/TerminalWindow.cs L279-L287, L289-L295
       private void WireSession(TerminalSession session)
       {
           session.Surface.SetTheme(_engineTheme);
           session.Surface.SetCursorStyle(Settings.CursorStyle, Settings.CursorBlink);
           // OSC 52: an app asked to set the system clipboard. (A clipboard *query*
           // — Text == null — would need an OSC 52 reply written back to the PTY;
           // that round-trip is deferred.)
           session.Surface.ClipboardRequested += OnClipboardRequested;
       }
   // ...
       private static void OnClipboardRequested(PurrTTY.Terminal.ClipboardRequest request)
       {
           if (!string.IsNullOrEmpty(request.Text))
           {
               ImGui.SetClipboardText(request.Text);
           }
       }
   ```

## Complete example

| File | Role |
|---|---|
| [purrTTY.Terminal/Ghostty/OscSidecar.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Ghostty/OscSidecar.cs) | Framing state machine, dispatch, base64 decode |
| [purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Ghostty/GhosttyTerminalSurface.cs#L777-L778) | Tee before `VTWrite` |
| [purrTTY.Display/Ghostty/TerminalWindow.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Display/Ghostty/TerminalWindow.cs#L279-L287) | Subscription and `SetClipboardText` |
| [purrTTY.Terminal/ITerminalSurface.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/ITerminalSurface.cs#L7-L14) | The `ClipboardRequest` payload type |

## Variations and gotchas

- **Any program in the terminal can overwrite the clipboard.** purrTTY applies OSC 52 without a prompt; a mod that cares can gate the event behind a setting or a confirmation before it calls `SetClipboardText`.
- **Only the set direction exists.** A read (`?`) is surfaced as a request with no text and nobody answers it.
- **Never dispatch a truncated sequence.** Oversize payloads are dropped whole.
- **Keep the scanner allocation-free in ground state.** It sees every byte of every session on the main thread.
- **The in-world renderer does not subscribe**, so only 2D windows apply OSC 52.
- **A cut escape heals through CAN.** The surface's drop-heal sequence includes CAN, which makes the sidecar abandon a half-received OSC instead of dispatching it.

## See also

- [Overview](overview.md)
- [OSC 52 clipboard sidecar](../../../../mods/purrtty/features/osc52-clipboard-sidecar.md)
- [Host a terminal with pty sessions and frame handoff](../host-a-terminal-with-pty-sessions-and-frame-handoff/overview.md)
- [Read typed text and key chords from ImGui](../../../ui/input/read-typed-text-and-key-chords-from-imgui/overview.md)
