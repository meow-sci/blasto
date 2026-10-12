---
type: Game Feature
title: Host and supervise an external process or virtual machine from a KSA mod
description: Start a child process from the game without blocking the frame or leaking it, with output pumps, a queued writer, readiness probing and bounded shutdown at unload, shown for a QEMU microVM, a Windows pseudoconsole shell, a Unix pty shell and background shell detection
category: external-io
group: processes
topic: host-and-supervise-an-external-process-from-a-mod
demonstrated_by:
  - mods/gatos/features/qemu-microvm-host-process
  - mods/gatos/features/qcow2-overlay-guest-disks
  - mods/gatos/features/alpine-guest-image
  - mods/purrtty/features/conpty-shell-hosting
  - mods/purrtty/features/posix-pty-spawn-without-fork
  - mods/purrtty/features/shell-detection-and-command-resolution
lifecycle: [startup:all-mods-loaded, frame:draw-ui, thread:background, event:unload]
game_types: [KSA.Program]
tags: [process, qemu, vm, conpty, pty, supervision, shutdown, threading, qcow2]
---

Some mods need code that is not managed C# loaded into the game: a shell, a Linux guest, a compiler, a native tool. The reliable way to run it is as a child process, supervised from the mod. KSA gives no help here. The game is a large, heavily threaded process with a frame loop that must never wait on I/O, and the host (StarMap) decides when the mod is unloaded. This topic is about the supervision around the child:

- starting it off the frame, with a bounded readiness check;
- pumping its output on a thread that never sleeps, and writing its input from a queue so a stalled child cannot block the caller;
- tearing it down in a defined order, with timeouts at every wait, and refusing to close a handle another thread is blocked on;
- making sure the game exit is never held by a child that will not stop.

Four shapes are covered. A long-lived QEMU virtual machine (with its accelerator selection, copy-on-write disks and pinned guest image), a Windows ConPTY shell, a Unix pty shell started without `fork()`, and the detection of installed shells and launch command lines that feeds a menu.

## Engine background

KSA has no process-hosting API; the relevant facts are about when mod code may run and what the host does on exit.

| Fact | Where | Consequence |
|---|---|---|
| Mod initialisation runs in `StarMapAllModsLoaded` on the main thread | [the game loop reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) (`startup:all-mods-loaded`) | Create hosts and probes there, but start nothing that waits |
| The frame must not wait on I/O | `Program.OnFrame` is one main-thread call per frame | Spawn, probe, resize and teardown run on pool tasks; the UI reads a volatile status |
| `StarMapUnload` runs from `ModLoader.Dispose`, after the game's entry point returns | [StarMap/Program.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap/Program.cs#L21-L28), [StarMap.Core/ModRepository/ModLoader.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModLoader.cs#L143-L150) | A normal exit runs unload; `Environment.Exit` does not |
| StarMap's own restart paths call `Environment.Exit(0)` | [StarMap.Core/Patches/ModLibraryPatches.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/Patches/ModLibraryPatches.cs#L60-L73) | On those exits unload is skipped; a child that must die with the game needs its own safeguard |
| `StarMapCore.DeInit` unpatches every Harmony patch before the unload methods run | [StarMap.Core/StarMapCore.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/StarMapCore.cs#L31-L35) | Per-mod `UnpatchAll(id)` calls in unload are no-ops with this StarMap |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: QEMU microVM with async boot, accelerator ladder, qcow2 overlays](host-and-supervise-an-external-process-from-a-mod-1.md) | gatos | `process-hosting` | `thread:background`, `event:unload` | A whole Linux guest, portable across OSes; QEMU must be installed on Linux and macOS and boot takes seconds |
| [2: Windows ConPTY with a never-sleeping pump](host-and-supervise-an-external-process-from-a-mod-2.md) | purrtty | `native-interop` | `thread:background`, `frame:draw-ui` | Real shells on Windows with full-screen TUI support; Win32 handle discipline is subtle |
| [3: POSIX pty via posix_openpt and posix_spawnp](host-and-supervise-an-external-process-from-a-mod-3.md) | purrtty | `native-interop` | `thread:background`, `frame:draw-ui` | No fork in a large process; libc P/Invoke with per-OS constants and an Apple arm64 ABI trap |
| [4: Shell and WSL detection, command resolution, menu cache](host-and-supervise-an-external-process-from-a-mod-4.md) | purrtty | `background-thread` | `startup:all-mods-loaded`, `thread:background`, `frame:draw-ui` | Menus only offer shells that will start; detection is cached and may be stale |

## See also

- [Game loop and mod lifecycle](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)
- [Clean up on game exit](../../../getting-started/lifecycle/clean-up-on-game-exit/overview.md): the unload path in general
- [Bind and vendor native C libraries with layout-exact P/Invoke](../bind-and-vendor-native-c-libraries/overview.md): the Apple arm64 variadic pitfall and per-assembly native resolution
- [Host a terminal emulator in the game](../../terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/overview.md): how the pty output reaches the frame
- [Connect a VM guest to the game](../host-guest-transports-tcp-9p-and-ssh/overview.md), [Bridge a serial port and bus protocols to the game](../../network/bridge-a-serial-port-and-bus-protocols-to-the-game/overview.md)
- Choosing where a mod stores its files is covered in a separate topic.
- [Find where KSA keeps user data and choose where a mod stores its config and data files](../../../persistence/user-data/choose-where-a-mod-stores-its-files/overview.md) - related topic

