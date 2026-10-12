# Game Feature

* [Let a terminal program set the system clipboard with OSC 52](overview.md) - Detect OSC 52 sequences in the terminal byte stream with a small scanner and set the system clipboard through ImGui from the main thread

# How-To

* [OscSidecar tee on the bytes about to reach the engine, clipboard write on the tick](let-a-terminal-program-set-the-system-clipboard.md) - Scan the terminal byte stream with an allocation-free OSC state machine, decode OSC 52 payloads and apply them with ImGui.SetClipboardText on the main thread
