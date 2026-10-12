# Game Feature

* [Host a terminal emulator in the game with pty sessions, custom shells and thread-to-frame handoff](overview.md) - Run a terminal tab in the game with blocking pty I/O on background threads and every main-thread API on one StarMapAfterGui tick, with bounded queues, custom shells and SSH sessions

# How-To

* [BaseLineBufferedShell that executes lines through the game console](host-a-terminal-with-pty-sessions-and-frame-handoff-3.md) - Add a custom terminal shell that runs lines through the KSA developer console and captures the coloured output with a gated Harmony postfix on ConsoleWindow.Print
* [Bounded byte inbox with backpressure and dirty-aware frame snapshots](host-a-terminal-with-pty-sessions-and-frame-handoff-2.md) - Post terminal bytes from any thread into a bounded inbox and build a dirty-aware frame snapshot on the tick without KSA or ImGui types in the backend
* [Map every thread to one StarMapAfterGui tick with configure-before-publish sessions](host-a-terminal-with-pty-sessions-and-frame-handoff-1.md) - Keep blocking pty reads, writes and spawns on background threads while the terminal engine, ImGui and the clipboard are touched only from one StarMapAfterGui tick
* [SSH PTY session with lazy VM boot, bounded input writer thread and thread-agnostic output](host-a-terminal-with-pty-sessions-and-frame-handoff-4.md) - Plug an SSH session into a guest VM into the terminal as a custom shell whose keystrokes never block the tick and whose output needs no marshalling to the main thread
