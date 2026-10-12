---
type: Game Feature
title: Connect a VM guest to the game with slirp TCP, a 9P filesystem and pinned SSH
description: The channels between a QEMU guest and the game process that work on Windows, Linux and macOS - slirp TCP with pre-allocated host ports, a from-scratch 9P2000.L server with synthetic and confined host files, and SSH sessions pinned to the guest manifest's host key
category: external-io
group: processes
topic: host-guest-transports-tcp-9p-and-ssh
demonstrated_by:
  - mods/gatos/features/slirp-tcp-host-guest-transport
  - mods/gatos/features/ninep-server-in-csharp
  - mods/gatos/features/sim-filesystem-file-models
  - mods/gatos/features/snapshot-diff-flight-events
  - mods/gatos/features/ssh-shell-session-into-guest
  - mods/gatos/features/alpine-guest-image
lifecycle: [startup:all-mods-loaded, thread:background]
game_types: []
tags: [qemu, slirp, 9p, ssh, virtual-filesystem, host-key, transport, vm]
---

A VM next to the game is only useful if data crosses the boundary. A mod that embeds a guest has to answer how, on all three desktop OSes, without a driver or a kernel module in the host. gatOS answers with TCP over QEMU's user-mode network (slirp) for everything: the guest mounts a 9P2000.L filesystem served from inside the game process, reaches HTTP and MQTT at the slirp gateway address, and the host reaches the guest's SSH server through one loopback port forward.

Nothing here touches KSA objects. Every channel is a thread-pool socket server or client; game state reaches it through the immutable snapshot of [Export live game state to background threads](../../telemetry/export-game-state-to-other-threads/overview.md) and the command queue of [Marshal commands from background threads to the game thread](../../control/marshal-commands-to-the-game-thread/overview.md). The child process that hosts the guest is the subject of [Host and supervise an external process from a KSA mod](../host-and-supervise-an-external-process-from-a-mod/overview.md).

## Engine background

There is no game type to hook; the relevant facts are about threading and the host servers' start order.

| Fact | Consequence |
|---|---|
| Mod initialisation runs at `startup:all-mods-loaded` ([lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)) | The host servers (9p, HTTP, MQTT, mounts) start there, before any VM exists, so a VM booted later finds valid ports |
| All sockets are served on thread-pool tasks | The game thread is never involved in transport |
| Reads come from the published snapshot; writes enqueue a command and await it | A game thread that is not reaching its drain (loading, a stalled frame, a suspended process) turns writes into `ETIMEDOUT` rather than a hang; a simulation pause alone does not, because `Program.OnFrame` keeps running |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: slirp TCP with a PortAllocator and guest-side supervisors](host-guest-transports-tcp-9p-and-ssh-1.md) | gatos | `process-hosting` | `startup:all-mods-loaded`, `thread:background` | One transport that behaves the same on every OS; TCP-only and the slirp gateway address is part of the contract |
| [2: from-scratch 9P2000.L server with a VfsNode tree](host-guest-transports-tcp-9p-and-ssh-2.md) | gatos | `protocol-server` | `thread:background` | Lets `cat`, `watch` and `jq` be the API without a guest binary; a protocol to implement and test |
| [3: sensor, stream, events and command file models](host-guest-transports-tcp-9p-and-ssh-3.md) | gatos | `virtual-filesystem` | `thread:background` | Files that behave under the Linux 6.11+ page cache; a file must choose one read model |
| [4: pin the guest SSH host key to a manifest value](host-guest-transports-tcp-9p-and-ssh-4.md) | gatos | `background-thread` | `thread:background` | Protects against a mismatched guest image; relies on a static key and loopback-only access |

## See also

- [Host and supervise an external process or virtual machine from a KSA mod](../host-and-supervise-an-external-process-from-a-mod/overview.md)
- [Bridge a serial port and bus protocols to the game](../../network/bridge-a-serial-port-and-bus-protocols-to-the-game/overview.md)
- [Export live game state to background threads](../../telemetry/export-game-state-to-other-threads/overview.md), [Marshal commands from background threads to the game thread](../../control/marshal-commands-to-the-game-thread/overview.md)
- [Detect flight events when the game has no event bus](../../telemetry/detect-flight-events-from-snapshots/overview.md)
- [Host a terminal emulator in the game](../../terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/overview.md)
- [Build an external SDK client or autopilot](../../control/build-external-clients-and-autopilots-with-a-typed-sdk/overview.md)
