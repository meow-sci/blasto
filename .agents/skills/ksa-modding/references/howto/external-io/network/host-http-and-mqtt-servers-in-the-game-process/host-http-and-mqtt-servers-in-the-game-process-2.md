---
type: How-To
title: Embedded broker with subscription-gated changed-only publish and ProcessPublish=false command topic
description: Embed an MQTTnet broker that publishes retained changed-only values while subscribers exist and consumes command topics without relaying them
category: external-io
group: network
topic: host-http-and-mqtt-servers-in-the-game-process
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/embedded-mqtt-broker]
techniques: [protocol-server, background-thread, command-queue, immutable-snapshot]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapUnload]
lifecycle: [startup:all-mods-loaded, thread:background, event:unload]
game_types: []
tags: [mqtt, mqttnet, retained, telemetry, control, broker]
---

Outcome: Node-RED, Grafana, Home Assistant or `mosquitto_sub` can consume game telemetry and send commands without polling, from an MQTT broker embedded in the mod, so the player installs no broker. The design goal is to do no work for nobody: nothing is serialized unless a subscription matches the topic and nothing is published unless the bytes changed. Prerequisites: StarMap lifecycle hooks, an immutable snapshot store and a game-thread command queue (see [Export live game state to background threads](../../telemetry/export-game-state-to-other-threads/overview.md) and [Marshal commands from background threads to the game thread](../../control/marshal-commands-to-the-game-thread/overview.md)), and the MQTTnet NuGet package. Demonstrated by [Embedded MQTT broker](../../../../mods/gatos/features/embedded-mqtt-broker.md) in gatOS.

## Background: how the game does it

The game has no MQTT support and no event bus, so the broker only sees what the mod's sampler publishes. The same rules as the HTTP approach apply: the broker's pump tasks read `SnapshotStore.Current` and the immutable `SimSnapshot` it holds, and command publishes await the game-thread drain. Two things are different from HTTP:

- MQTT is push-based and retained, so a late subscriber must receive current values without the game being asked again. The broker relies on MQTTnet redelivering retained messages on subscribe.
- MQTTnet is a managed dependency that ships in the mod folder, so it loads through the mod's own `AssemblyLoadContext`; see [Ship third-party DLLs with a mod](../../../getting-started/dependencies/ship-third-party-dlls-with-a-mod/overview.md).

Timing facts come from the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md) and are not repeated here.

## Lifecycle and timing

| Work | Where |
|---|---|
| `StartAsync`, bind, create the pumps | `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`), `StartMqttBroker`, synchronously |
| World pump and field pump tasks, MQTTnet I/O | thread-pool tasks (`thread:background`) |
| Command publishes | the interceptor awaits `ICommandSink.SubmitAsync`, which completes after the game-thread drain |
| Dispose | `[StarMapUnload]` (`event:unload`), bounded 5 s wait |

## Steps

1. **Reference MQTTnet.** gatOS pins a 4.x package in its MQTT project; the game does not load it for you:

   ```xml
   <!-- gatOS.Mqtt/gatOS.Mqtt.csproj L18 -->
           <PackageReference Include="MQTTnet" Version="4.3.7.1207" />
   ```

2. **Build the server and attach four handlers.** The default endpoint is bound to a configured address (`MqttBindHost`, default `127.0.0.1`) and port (`MqttPreferredPort`, default 1883). The handlers keep a connected-client counter, a per-client subscription list and the command interceptor:

   ```csharp
   // gatOS.Mqtt/SimMqttBroker.cs L185-L199
       private MqttServer BuildServer(IPAddress address, int port)
       {
           var options = new MqttServerOptionsBuilder()
               .WithDefaultEndpoint()
               .WithDefaultEndpointBoundIPAddress(address)
               .WithDefaultEndpointPort(port)
               .Build();
           var server = new MqttFactory().CreateMqttServer(options);
           server.InterceptingPublishAsync += OnClientPublishAsync;
           server.ClientConnectedAsync += OnClientConnectedAsync;
           server.ClientDisconnectedAsync += OnClientDisconnectedAsync;
           server.ClientSubscribedTopicAsync += OnClientSubscribedAsync;
           server.ClientUnsubscribedTopicAsync += OnClientUnsubscribedAsync;
           return server;
       }
   ```

3. **Fall back to a free port on a clash.** `TryStartOnAsync` retries once on a probed free port when the preferred one is taken, and the bound port is passed to the guest on its kernel command line:

   ```csharp
   // gatOS.Mqtt/SimMqttBroker.cs L165-L183
       private async Task<int> TryStartOnAsync(IPAddress address, int preferredPort)
       {
           var port = preferredPort > 0 ? preferredPort : FreePort(address);
           try
           {
               _server = BuildServer(address, port);
               await _server.StartAsync().ConfigureAwait(false);
               return port;
           }
           catch (Exception) when (preferredPort > 0)
           {
               // Preferred port in use — retry once on a probed free port.
               _server?.Dispose();
               port = FreePort(address);
               _server = BuildServer(address, port);
               await _server.StartAsync().ConfigureAwait(false);
               return port;
           }
       }
   ```

4. **Track subscriptions and force a baseline.** A subscription rebuilds a flat filter array and forces a full publish cycle, because topics nobody subscribed to were skipped and may never have been injected. `HasSubscriber` iterates that array without allocating:

   ```csharp
   // gatOS.Mqtt/SimMqttBroker.cs L220-L235, L248-L249, L265-L272
       private Task OnClientSubscribedAsync(ClientSubscribedTopicEventArgs e)
       {
           lock (_subLock)
           {
               if (!_subscriptions.TryGetValue(e.ClientId, out var filters))
                   _subscriptions[e.ClientId] = filters = [];
               if (!filters.Contains(e.TopicFilter.Topic))
                   filters.Add(e.TopicFilter.Topic);
               RebuildFiltersLocked();
           }

           // A topic the pumps skipped (no matching filter) may never have been injected, so the new
           // subscription forces a full cycle to lay its retained baseline down (GP2).
           ForceRepublish();
           return Task.CompletedTask;
       }
   // ...
       private void RebuildFiltersLocked()
           => _activeFilters = _subscriptions.Values.SelectMany(f => f).Distinct().ToArray();
   // ...
       private bool HasSubscriber(string topic)
       {
           var filters = _activeFilters;
           foreach (var filter in filters)
               if (MqttTopicFilterComparer.Compare(topic, filter) == MqttTopicFilterCompareResult.IsMatch)
                   return true;
           return false;
       }
   ```

5. **Pump only when someone listens, and only changed bytes.** The world pump waits for the next snapshot or a connect/subscribe wake and skips the whole cycle when no client is connected. `PublishChangedAsync` serializes a topic only if a filter matches it, and injects it only if its bytes differ from the previous publish:

   ```csharp
   // gatOS.Mqtt/SimMqttBroker.cs L395-L399, L495-L504
               // Nobody is listening and no forced refresh is pending: skip the whole serialize+inject.
               // The last-published retained messages stay valid, so a future subscriber still gets them
               // (MQTTnet redelivers retained topics on subscribe) — we just stop doing work for no one.
               if (!force && ConnectedClients <= 0)
                   continue;
   // ...
       private async Task PublishChangedAsync(string topic, Func<byte[]> payload, bool force)
       {
           if (!force && !HasSubscriber(topic))
               return;
           var bytes = payload();
           if (!force && _lastWorld.TryGetValue(topic, out var last) && last.AsSpan().SequenceEqual(bytes))
               return;
           _lastWorld[topic] = bytes;
           await PublishAsync(topic, bytes, retain: true).ConfigureAwait(false);
       }
   ```

   `system` and `bodies` are published by reference from the sampler when unchanged, so an identity check skips even the serialization:

   ```csharp
   // gatOS.Mqtt/SimMqttBroker.cs L415-L420
                   if (force || !ReferenceEquals(snapshot.System, _lastSystemSource))
                   {
                       await PublishChangedAsync("gatos/system", () => SimJson.SerializeToUtf8Bytes(snapshot.System),
                           force).ConfigureAwait(false);
                       _lastSystemSource = snapshot.System;
                   }
   ```

6. **Tombstone vanished vessels.** An empty retained payload clears a retained topic, so a destroyed vessel does not linger for new subscribers:

   ```csharp
   // gatOS.Mqtt/SimMqttBroker.cs L446-L459
                   // Clear the retained topics of vessels that vanished (GP2): an empty retained
                   // payload is the MQTT tombstone — mirrors the field mirror's behavior for
                   // disappeared leaves, so clients no longer see ghosts until a broker restart.
                   foreach (var id in _publishedVesselIds)
                   {
                       if (liveIds.Contains(id) || !_vesselTopics.TryRemove(id, out var topics))
                           continue;
                       _lastWorld.Remove(topics.Telemetry);
                       _lastWorld.Remove(topics.Snapshot);
                       await PublishAsync(topics.Telemetry, [], retain: true).ConfigureAwait(false);
                       await PublishAsync(topics.Snapshot, [], retain: true).ConfigureAwait(false);
                   }

                   (_publishedVesselIds, _scratchVesselIds) = (liveIds, _publishedVesselIds);
   ```

7. **Intercept command topics and refuse to relay them.** Setting `ProcessPublish = false` consumes the publish at the broker, so one client's command never reaches the other `gatos/#` subscribers and a client-set retain flag cannot make it stick. Validate, gate `debug.*`, submit, then publish the outcome on `gatos/command/result`:

   ```csharp
   // gatOS.Mqtt/SimMqttBroker.cs L303-L315, L320-L343
           if (topic != CommandTopic)
               return;

           // The broker injects its own retained messages with an empty client id; ignore those so we
           // only handle genuine client publishes to the command topic.
           if (string.IsNullOrEmpty(args.ClientId))
               return;

           // A command is consumed by the broker, not relayed: stop MQTTnet from re-broadcasting the
           // raw command payload to every other gatos/# subscriber (it would leak one client's
           // commands to all, and a client-set retain flag would make it stick). The reply goes out
           // on gatos/command/result instead.
           args.ProcessPublish = false;
   // ...
           var payload = args.ApplicationMessage.PayloadSegment.ToArray();
           string resultJson;
           try
           {
               var command = ParseCommand(payload);
               var validation = CommandCatalog.Validate(command);
               if (!validation.IsValid)
                   throw new ArgumentException(validation.Error);
               if (command.Action.StartsWith("debug.", StringComparison.Ordinal) && !sink.DebugEnabled)
                   resultJson = Result("EACCES", "debug namespace disabled");
               else
               {
                   var result = await sink.SubmitAsync(command, _cts.Token).ConfigureAwait(false);
                   resultJson = result.IsSuccess
                       ? "{\"outcome\":\"ok\"}"
                       : Result(result.Outcome.ErrnoName(), result.Message ?? result.Outcome.ToString());
               }
           }
           catch (Exception ex)
           {
               resultJson = Result("EINVAL", ex.Message);
           }

           await PublishAsync(CommandResultTopic, resultJson, retain: false).ConfigureAwait(false);
   ```

   The field pump (`FieldFeedHz`, default 4) mirrors every `/sim` leaf to `gatos/sim/<path>` and accepts writes on `gatos/sim/<path>/set` through the same interception.

8. **Start and stop with the mod.** `StartMqttBroker` mirrors the HTTP start (catch, log, keep going), and unload disposes the broker with a bounded wait:

   ```csharp
   // gatOS.GameMod/Mod.cs L704-L722
       private void StartMqttBroker()
       {
           if (!_config.MqttEnabled || _simStore is not { } store)
               return;
           try
           {
               var broker = new SimMqttBroker(store, _commandQueue, SimTransportsStatus,
                   _config.MqttFieldTopics ? _simRoot : null, _config.FieldFeedHz, _config.MqttPublishHz);
               broker.StartAsync(_config.MqttPreferredPort, _config.MqttBindHost).GetAwaiter().GetResult();
               _mqttBroker = broker;
               ModLog.Log.Info($"gatOS MQTT broker listening on {FormatEndpoint(broker.BindHost, broker.Port)} "
                               + "(guest: $GATOS_MQTT / 10.0.2.2; topics under gatos/).");
           }
           catch (Exception ex)
           {
               _mqttBroker = null;
               ModLog.Log.Error($"The MQTT broker failed to start: {ex.Message}", ex);
           }
       }
   ```

   At unload the broker is disposed with the same bounded wait:

   ```csharp
   // gatOS.GameMod/Mod.cs L525-L528
               var mqttBroker = _mqttBroker;
               _mqttBroker = null;
               if (mqttBroker is not null && !mqttBroker.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)))
                   ModLog.Log.Warn("The MQTT broker did not stop within 5 s at unload.");
   ```

## Complete example

| File | Role |
|---|---|
| [gatOS.Mqtt/SimMqttBroker.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Mqtt/SimMqttBroker.cs) | Broker setup, world pump, field pump, command interceptor |
| [gatOS.Mqtt/gatOS.Mqtt.csproj](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Mqtt/gatOS.Mqtt.csproj) | The MQTTnet package reference |
| [gatOS.SimFs/SimJson.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/SimJson.cs) | Shared JSON projections, with UTF-8 byte variants for MQTT |
| [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L704-L722) | Start and failure handling |

## Variations and gotchas

- **Loopback by default, no authentication.** Binding `0.0.0.0` exposes unauthenticated control to the network; MQTT has no token in this design.
- **Relay your commands to nobody.** Without `ProcessPublish = false` a command topic behaves like any other topic and is rebroadcast.
- **Tombstone retained topics.** Retained messages outlive the thing they describe; remove them when the vessel goes away.
- **Cap the cadence when needed.** `MqttPublishHz` (0 means every snapshot, up to 120) limits the world pump, and the store keeps only the newest snapshot, so a slow cycle never queues a backlog.
- **Mind dependency versions.** MQTTnet is a mod-folder assembly; keep it clear of versions the game or another mod already loaded.
- **Keep the broker away from game objects.** Both pumps read snapshots only; a new topic means a new snapshot field, not a call into `Universe`.

## See also

- [Overview](overview.md), [Raw TcpListener HTTP server](host-http-and-mqtt-servers-in-the-game-process-1.md)
- [Embedded MQTT broker](../../../../mods/gatos/features/embedded-mqtt-broker.md)
- [Let AI agents drive the game through an MCP server](../../control/let-ai-agents-control-the-game-over-mcp/overview.md)
- [Clean up on game exit](../../../getting-started/lifecycle/clean-up-on-game-exit/overview.md)
