---
type: Game Feature
title: Bridge a serial port and bus protocols (NDJSON, NMEA, CCSDS, SCPI) to the game
description: Connect a virtio-serial port to the game with NDJSON, NMEA or CCSDS telemetry frames and SCPI-style command lines answered with OK or ERR plus an errno name
category: external-io
group: network
topic: bridge-a-serial-port-and-bus-protocols-to-the-game
demonstrated_by: [mods/gatos/features/serial-bus-ccsds-nmea-scpi]
lifecycle: [thread:background, event:unload]
game_types: [KSA.Program]
tags: [serial, ndjson, nmea, ccsds, scpi, qemu, telemetry]
---

A guest VM or an instrument-style tool that expects a serial port speaks a very different language from a JSON API: newline-delimited frames, checksummed sentences, binary packets and command lines that answer `OK`. This topic shows how gatOS exposes the game over a QEMU `virtio-serial` port so that `cat`, a terminal program or real ground-segment code can consume telemetry and issue commands. It is a transport and codec exercise: nothing here calls a KSA member except through the published snapshot.

## Engine background

| Fact | Source | Consequence |
|---|---|---|
| The active vessel is `Program.ControlledVehicle`, a plain static property backed by a field with no locking | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L502-L516) | The sampler records its id as `ActiveVesselId` in each snapshot; the bridge never reads the property itself |
| `Program.OnFrame` is the only place game objects are touched safely | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2334) | Bridge threads read the snapshot and submit commands to the queue ([lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md)) |
| `[StarMapUnload]` runs on a normal exit | [Clean up on game exit](../../../getting-started/lifecycle/clean-up-on-game-exit/overview.md) | The connector is stopped first, because it follows the VM lifecycle |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [SerialBridge with a connector per boot, encoders and a SCPI port](bridge-a-serial-port-and-bus-protocols-to-the-game.md) | gatos | `protocol-server` | `thread:background`, `event:unload` | Authentic wire formats a real tool can parse; active vessel only, one cadence, and it needs a QEMU guest to host the port |

## See also

- [Host and supervise an external process or virtual machine](../../processes/host-and-supervise-an-external-process-from-a-mod/overview.md)
- [Export live game state to background threads](../../telemetry/export-game-state-to-other-threads/overview.md), [Marshal commands from background threads to the game thread](../../control/marshal-commands-to-the-game-thread/overview.md)
- [Host HTTP and MQTT servers in the game process](../host-http-and-mqtt-servers-in-the-game-process/overview.md)
