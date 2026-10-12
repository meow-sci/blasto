---
type: Game Feature
title: Host a terminal emulator in the game with pty sessions, custom shells and thread-to-frame handoff
description: Run a terminal tab in the game with blocking pty I/O on background threads and every main-thread API on one StarMapAfterGui tick, with bounded queues, custom shells and SSH sessions
category: external-io
group: terminals
topic: host-a-terminal-with-pty-sessions-and-frame-handoff
demonstrated_by:
  - mods/purrtty/features/pty-threads-to-frame-tick-handoff
  - mods/purrtty/features/headless-terminal-surface-and-frame-snapshot
  - mods/purrtty/features/custom-shell-registry-and-lifecycle
  - mods/purrtty/features/game-console-shell
  - mods/gatos/features/ssh-shell-session-into-guest
  - mods/gatos/features/purrtty-custom-shell-contract
lifecycle: [frame:draw-ui, thread:background, startup:all-mods-loaded, frame:input]
game_types:
  - KSA.Program
  - Brutal.ImGuiApi.Abstractions.TerminalInterface
  - Brutal.ImGuiApi.Abstractions.ConsoleWindow
  - Brutal.ImGuiApi.ImColor8
tags: [terminal, pty, threading, custom-shell, ssh, imgui, starmap]
---

A terminal inside the game is a threading problem first. The things it needs are blocking by nature (a pty read, a process wait, an SSH write, a slow shell spawn) and the things that draw it (the native terminal engine, ImGui, the system clipboard) may be touched from one thread only. This topic is the reference design from purrTTY and gatOS: all blocking work lives on background threads, a single `StarMapAfterGui` tick owns the engine and the UI, and two bounded queues, one per direction, are the only seams between them.

## Engine background

| Type or member | File | Why it matters |
|---|---|---|
| `Program.OnFrame` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2334) | The UI block runs only `if (DrawUI)`; `OnDrawUiViewports` sits in it, between `ImGui.NewFrame` and `ImGui.Render` |
| `Program.OnDrawUiViewports` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L3162-L3176) | StarMap postfixes it to make `[StarMapAfterGui]`, so a hidden HUD means no tick |
| `Program.TerminalInterface` | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L500) | `=> ConsoleWindow.Terminal`; the entry point for the game-console shell |
| `TerminalInterface.Execute` | [current/decomp/Brutal.ImGuiApi.Abstractions/TerminalInterface.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi.Abstractions/TerminalInterface.cs#L41-L56) | Runs a command line and prints errors rather than throwing |
| `ConsoleWindow.Print` | [current/decomp/Brutal.ImGuiApi.Abstractions/ConsoleWindow.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/Brutal.ImGuiApi.Abstractions/ConsoleWindow.cs#L600-L603) | The sink the output-capture postfix targets |

Frame structure and hook timing are in the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md).

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: map every thread to one tick](host-a-terminal-with-pty-sessions-and-frame-handoff-1.md) | purrtty | `background-thread` | `frame:draw-ui`, `thread:background` | The rule set for everything else; nothing ticks while the HUD is hidden |
| [2: bounded inbox, dirty-aware snapshots](host-a-terminal-with-pty-sessions-and-frame-handoff-2.md) | purrtty | `background-thread` | `frame:draw-ui`, `thread:background` | Lossless backpressure and cheap quiet frames; a renderer-neutral backend to maintain |
| [3: custom shell over the game console](host-a-terminal-with-pty-sessions-and-frame-handoff-3.md) | purrtty | `inter-mod-api`, `harmony-postfix` | `frame:draw-ui`, `startup:all-mods-loaded` | Simplest shell; commands run at the draw point, not where the stock console runs them |
| [4: SSH session with lazy VM boot](host-a-terminal-with-pty-sessions-and-frame-handoff-4.md) | gatos | `background-thread`, `inter-mod-api` | `thread:background`, `startup:all-mods-loaded` | A real Linux shell; needs a hosted guest and SSH.NET |

Read article 1 first. Articles 3 and 4 are alternatives for the shell behind a tab; article 2 is what receives their bytes.

## See also

- [Host and supervise an external process or virtual machine](../../processes/host-and-supervise-an-external-process-from-a-mod/overview.md), [Connect a VM guest with slirp TCP, a 9P filesystem and pinned SSH](../../processes/host-guest-transports-tcp-9p-and-ssh/overview.md)
- [Let a terminal program set the system clipboard](../let-a-terminal-program-set-the-system-clipboard/overview.md), [Stream the game screen to a terminal](../stream-the-game-screen-to-a-terminal/overview.md)
- [Marshal commands from background threads to the game thread](../../control/marshal-commands-to-the-game-thread/overview.md), [Export live game state to background threads](../../telemetry/export-game-state-to-other-threads/overview.md)
- [Draw UI while the HUD is hidden](../../../ui/overlays/draw-ui-while-the-hud-is-hidden/overview.md), [Share types between mods](../../../getting-started/dependencies/share-types-between-mods/overview.md), [Write a per-mod log file](../../../getting-started/lifecycle/write-a-per-mod-log-file/overview.md)
- [Make an HTTP request from a mod](../../network/make-an-http-request-from-a-mod-without-blocking-the-frame/overview.md)
- [Unit-test mod logic without launching the game](../../../testing-and-tooling/offline-tests/unit-test-mod-logic-without-game/overview.md) - related topic
- [Take clean screenshots from code, with a composed camera shot held for the capture](../../../testing-and-tooling/in-game-testing/take-screenshots-from-code/overview.md) - related topic

