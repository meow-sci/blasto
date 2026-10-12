---
type: Mod Feature
title: Embedded MQTT broker
description: An MQTTnet broker hosted inside the game process publishes retained telemetry topics and a leaf-per-topic mirror of /sim, gated by connected clients, live subscriptions and byte changes, and turns publishes on a command topic into game-thread commands
mod: gatos
category: external-io
techniques: [background-thread, protocol-server, command-queue, immutable-snapshot]
hooks: [starmap StarMapAllModsLoaded, thread:background SimMqttBroker.PublishPumpAsync]
lifecycle: [startup:all-mods-loaded, thread:background, event:unload]
game_types: []
tags: [mqtt, mqttnet, pubsub, telemetry, home-automation, threading]
---

MQTT lets Node-RED, Grafana, Home Assistant or `mosquitto_sub` consume KSA telemetry without
polling. gatOS embeds an MQTTnet broker in the mod (no external broker) bound to
`127.0.0.1:1883` by default and reachable from the guest at `$GATOS_MQTT`. MQTT is the one eager,
push-based transport in gatOS, so most of its design is about not doing work for nobody.

## How it works

**Server setup** ([gatOS.Mqtt/SimMqttBroker.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Mqtt/SimMqttBroker.cs#L184-L200)): `new MqttFactory().CreateMqttServer(options)`
with `WithDefaultEndpointBoundIPAddress` and port (preferred port with ephemeral fallback), plus three
handlers: `InterceptingPublishAsync` (commands), client connect/disconnect counters, and
`ClientSubscribedTopicAsync` (to force a baseline publish).

**Topics**:

| Topic | Retained | Content |
|---|---|---|
| `gatos/time`, `gatos/status`, `gatos/system`, `gatos/bodies`, `gatos/snapshot` | yes | `SimJson` projections, same bytes as HTTP |
| `gatos/vessels/<id>/telemetry`, `gatos/vessels/<id>/snapshot` | yes | per-vessel compact and full records; tombstoned with an empty retained payload when the vessel vanishes |
| `gatos/events` | no | one message per flight event |
| `gatos/sim/<path>` | yes | one topic per `/sim` leaf from the field mirror pump (`field_feed_hz`, default 4 Hz) |
| `gatos/command` (client to broker) | consumed | JSON `SimCommand`; outcome on `gatos/command/result` |
| `gatos/sim/<path>/set` (client to broker) | consumed | write one field, same path as a 9p `echo` |

**Doing nothing when idle** ([gatOS.Mqtt/SimMqttBroker.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Mqtt/SimMqttBroker.cs#L372-L420)):

1. The world pump awaits the next snapshot (or a connect/subscribe wake) and skips everything when
   `ConnectedClients` is 0; retained messages already on the broker stay valid.
2. `PublishChangedAsync` serializes a topic only if some live subscription filter matches it, and
   injects it only if the bytes differ from the last publish ([gatOS.Mqtt/SimMqttBroker.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Mqtt/SimMqttBroker.cs#L494-L503)).
3. `system` and `bodies` are re-published by reference by the sampler when unchanged, so an
   identity check short-circuits even serialization.
4. `mqtt_publish_hz` can cap the world cadence; the store coalesces to the newest snapshot.

**Commands.** The publish interceptor sets `args.ProcessPublish = false` so one client's command is
not rebroadcast to every `gatos/#` subscriber, validates it with `CommandCatalog.Validate`, refuses
`debug.*` when the debug namespace is off, submits it through `ICommandSink`, and publishes the
outcome with its errno name ([gatOS.Mqtt/SimMqttBroker.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Mqtt/SimMqttBroker.cs#L300-L345)).

## Lifecycle and timing

- Started in `[StarMapAllModsLoaded]` (`StartMqttBroker`) and disposed at `[StarMapUnload]`
  with a 5 s bounded wait.
- Two pump tasks (world and field) and MQTTnet's own I/O run on the thread pool
  (`thread:background`). They read only published snapshots; command publishes await the game
  thread through the command queue.
- Publish time per cycle is measured with `PerfStat` and shown in the status window.

## Key code

| Path | Role |
|---|---|
| [gatOS.Mqtt/SimMqttBroker.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Mqtt/SimMqttBroker.cs) | broker setup, world pump, field pump, command interceptor |
| [gatOS.SimFs/SimJson.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/SimJson.cs) | shared JSON projections (UTF-8 byte variants for MQTT) |
| [gatOS.NineP/Vfs/VfsScan.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.NineP/Vfs/VfsScan.cs) | leaf enumeration for `gatos/sim/<path>` |
| [gatOS.Mqtt/gatOS.Mqtt.csproj](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Mqtt/gatOS.Mqtt.csproj) | MQTTnet 4.3.7 dependency |

## Snippets

```csharp
// gatOS.Mqtt/SimMqttBroker.cs L494-L503
    /// </summary>
    private async Task PublishChangedAsync(string topic, Func<byte[]> payload, bool force)
    {
        if (!force && !HasSubscriber(topic))
            return;
        var bytes = payload();
        if (!force && _lastWorld.TryGetValue(topic, out var last) && last.AsSpan().SequenceEqual(bytes))
            return;
        _lastWorld[topic] = bytes;
        await PublishAsync(topic, bytes, retain: true).ConfigureAwait(false);
```

## Game APIs touched

None.

## Pitfalls and version notes

- An embedded broker must stop relaying command topics (`ProcessPublish = false`), or commands leak
  to other subscribers and a retained flag would replay them.
- Tombstone retained topics of vanished vessels, or new subscribers see ghosts.
- Binding to `0.0.0.0` exposes unauthenticated control to the network.
- MQTTnet is a NuGet dependency shipped in the mod folder; mind version conflicts with assemblies
  already loaded by the game or other mods.

## See also

- [HTTP /v1 API server](http-v1-api-server.md)
- [MCP server for AI agents](mcp-server-for-agents.md)
- [Telemetry sampler and snapshot store](telemetry-sampler-snapshot.md)
- [Game-thread command queue](game-thread-command-queue.md)
- [gatOS overview](../overview.md)
- [Embedded broker with subscription-gated changed-only publish and ProcessPublish=false command topic](../../../howto/external-io/network/host-http-and-mqtt-servers-in-the-game-process/host-http-and-mqtt-servers-in-the-game-process-2.md) - how-to article demonstrating this feature

