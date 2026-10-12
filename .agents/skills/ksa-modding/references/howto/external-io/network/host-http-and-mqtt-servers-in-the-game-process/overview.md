---
type: Game Feature
title: Host HTTP and MQTT servers in the game process for telemetry and control
description: Expose game state and accept commands from outside the game with a raw-socket HTTP server or an embedded MQTT broker that both read snapshots and write through the game-thread queue
category: external-io
group: network
topic: host-http-and-mqtt-servers-in-the-game-process
demonstrated_by: [mods/gatos/features/http-v1-api-server, mods/gatos/features/embedded-mqtt-broker]
lifecycle: [startup:all-mods-loaded, thread:background, event:unload]
game_types: []
tags: [http, mqtt, sse, server, telemetry, control, threading]
---

A mod that wants to be controlled or observed from outside the game, by a dashboard, a script, a home-automation hub or a VM guest, needs a server inside the game process. KSA offers none, so the mod hosts one on background threads. This topic compares the two servers gatOS ships side by side: a raw-socket HTTP/1.1 server and an embedded MQTT broker. They are interchangeable transports over the same two seams: a published immutable snapshot for reads and a game-thread command queue for writes. Neither server ever touches a KSA object.

## Engine background

There is no game type to hook; the engine facts are about where game objects may be touched and when a mod can start and stop a server.

| Fact | Source | Consequence |
|---|---|---|
| `Program.OnFrame` runs on the main thread; `PrepareFrame` applies the finished physics step before the next one is dispatched | [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2238-L2334) and [current/decomp/KSA/Program.cs](https://github.com/meow-sci/ksa-game-assemblies/blob/935a32b4dbc34cbcd16e87d16cf9732eb90030c0/current/decomp/KSA/Program.cs#L2141-L2236) | Pool-thread handlers must not read `Vehicle` or `Universe`; they read a snapshot sampled on the game thread |
| `[StarMapAllModsLoaded]` runs once every mod is loaded and before the player is in a scene | [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) | Start the listener here, so the port is known before anything connects |
| `[StarMapUnload]` runs on a normal exit and not after `Environment.Exit` | [Clean up on game exit](../../../getting-started/lifecycle/clean-up-on-game-exit/overview.md) | Dispose the servers there with a bounded wait; a killed process closes its sockets anyway |

## Approaches

| Article | Mod | Technique | Lifecycle | Trade-off |
|---|---|---|---|---|
| [1: raw TcpListener server with SSE, long-poll and OpenAPI](host-http-and-mqtt-servers-in-the-game-process-1.md) | gatos | `protocol-server` | `startup:all-mods-loaded`, `thread:background`, `event:unload` | Works with `curl` and any language; request/response plus SSE, so clients poll or hold a stream; a parser to maintain |
| [2: embedded MQTT broker, changed-only retained publish](host-http-and-mqtt-servers-in-the-game-process-2.md) | gatos | `protocol-server` | `startup:all-mods-loaded`, `thread:background`, `event:unload` | Push-based with last-value caching for dashboards; adds the MQTTnet dependency and a topic scheme |

Both can run together: they share the same `SimJson` projections, so an HTTP read and an MQTT message carry the same bytes. Choose HTTP for scripts and tools, MQTT for always-on dashboards and automation hubs.

## Shared decisions

- **Loopback by default.** Both servers bind `127.0.0.1` unless the player edits the config, and neither has authentication.
- **Preferred port, ephemeral fallback.** Try the conventional port (4242 for HTTP, 1883 for MQTT), retry on a free port when it is taken, and publish the real port.
- **One command path.** Commands are parsed into the same `SimCommand`, validated by `CommandCatalog`, gated on the debug namespace and submitted to the same queue; failures carry an errno name that each transport maps to its own status.
- **Optional by design.** A failed start logs and leaves the transport off; the rest of the mod keeps working.

## See also

- [Export live game state to background threads](../../telemetry/export-game-state-to-other-threads/overview.md), [Marshal commands from background threads to the game thread](../../control/marshal-commands-to-the-game-thread/overview.md)
- [Let AI agents drive the game through an MCP server](../../control/let-ai-agents-control-the-game-over-mcp/overview.md), [Build an external SDK client or autopilot](../../control/build-external-clients-and-autopilots-with-a-typed-sdk/overview.md)
- [Bridge a serial port and bus protocols to the game](../bridge-a-serial-port-and-bus-protocols-to-the-game/overview.md)
- [Clean up on game exit](../../../getting-started/lifecycle/clean-up-on-game-exit/overview.md)
