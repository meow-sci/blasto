---
type: How-To
title: Raw TcpListener server with SSE, long-poll, OpenAPI and a generic command endpoint
description: Serve JSON snapshots, Server-Sent Events, a sim-time long-poll and one generic command endpoint from a hand-written HTTP/1.1 server inside the game process
category: external-io
group: network
topic: host-http-and-mqtt-servers-in-the-game-process
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/http-v1-api-server]
techniques: [protocol-server, background-thread, command-queue, immutable-snapshot]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapUnload]
lifecycle: [startup:all-mods-loaded, thread:background, event:unload]
game_types: []
tags: [http, sse, long-poll, openapi, tcplistener, telemetry, control]
---

Outcome: a dashboard, a script or a guest VM can read JSON snapshots, follow Server-Sent Events (SSE), long-poll for a sim time and send commands to a running game over plain HTTP/1.1, with a server that lives in the mod and adds no web framework to the mod folder. Prerequisites: StarMap lifecycle hooks (`StarMapAllModsLoaded`, `StarMapUnload`), an immutable snapshot store ([Export live game state to background threads](../../telemetry/export-game-state-to-other-threads/overview.md)) and a game-thread command queue ([Marshal commands from background threads to the game thread](../../control/marshal-commands-to-the-game-thread/overview.md)). Demonstrated by [HTTP /v1 API server](../../../../mods/gatos/features/http-v1-api-server.md) in gatOS.

## Background: how the game does it

KSA ships no server a mod can reuse and no thread-safe view of its state. `Program.OnFrame` runs on the main thread and `Program.PrepareFrame` is where the previous physics step is applied, so game objects are only safe to read there (see the [lifecycle reference](../../../getting-started/lifecycle/game-loop-and-lifecycle/overview.md#per-frame-sequence)). A server on thread-pool threads therefore follows two rules, both implemented by gatOS:

- **Reads** project the published snapshot (`SnapshotStore.Current`, a volatile reference swap) and never touch a KSA object.
- **Writes** become immutable `SimCommand` values that the game thread executes at a fixed point in the frame; the HTTP handler awaits the result.

The server itself is a `TcpListener` plus a small request parser. [gatOS.Http/HttpRequestLine.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Http/HttpRequestLine.cs#L6-L10) states the reason in its class comment:

```csharp
// gatOS.Http/HttpRequestLine.cs L6-L10
///     A minimally-parsed HTTP/1.1 request (method, path, decoded query, headers, body). The
///     server speaks raw HTTP over a <c>TcpListener</c> rather than <c>HttpListener</c>:
///     <c>HttpListener</c> rides http.sys on Windows and needs a URL-ACL reservation or admin,
///     which a game mod cannot assume. The default loopback bind also avoids a firewall prompt;
///     users may deliberately select another interface in <c>gatos.toml</c>.
```

## Lifecycle and timing

| Work | Where | Why |
|---|---|---|
| Create, bind and start | `[StarMapAllModsLoaded]` (`startup:all-mods-loaded`), synchronously | The port is known before any VM or client exists, and it can be handed to a guest on its kernel command line |
| Accept loop, parsing, routing, SSE and long-poll loops | thread-pool tasks (`thread:background`) | Nothing here may block the frame |
| Snapshot reads | any thread | One volatile read of an immutable object |
| Command execution | game thread, at the drain point of the command queue | `SubmitAsync` completes after the command ran, or answers 504 on timeout |
| Stop | `[StarMapUnload]` (`event:unload`), bounded 5 s wait | A stuck connection must not hold the game exit; see [Clean up on game exit](../../../getting-started/lifecycle/clean-up-on-game-exit/overview.md) |

## Steps

1. **Bind a `TcpListener` with a preferred port and an ephemeral fallback.** Try the configured port first (`GatOsConfig.HttpPreferredPort` defaults to 4242, `HttpBindHost` to `127.0.0.1`), fall back to port 0 on a clash, and publish the port that was really bound so clients and the guest can find it:

   ```csharp
   // gatOS.Http/SimHttpServer.cs L103-L122
       private static TcpListener BindAndStart(IPAddress address, int preferredPort)
       {
           if (preferredPort > 0)
           {
               try
               {
                   var preferred = new TcpListener(address, preferredPort);
                   preferred.Start();
                   return preferred;
               }
               catch (SocketException)
               {
                   // Port in use — fall through to an ephemeral port.
               }
           }

           var ephemeral = new TcpListener(address, 0);
           ephemeral.Start();
           return ephemeral;
       }
   ```

2. **Run one accept loop and one task per connection.** `StartAsync` stores the bound port and starts `AcceptLoopAsync` with `Task.Run`; the loop hands each client to `ServeAsync` on the thread pool. Connections are HTTP/1.1 keep-alive with a 30 s idle timeout and a cap of 1000 requests, and the idle timer is disarmed while a handler runs because SSE and long-poll handlers last until the client leaves:

   ```csharp
   // gatOS.Http/SimHttpServer.cs L143, L148-L151, L180-L183
               _ = Task.Run(() => ServeAsync(client, ct), ct);
   // ...
       private static readonly TimeSpan KeepAliveIdle = TimeSpan.FromSeconds(30);

       /// <summary>Requests served per connection before a polite close (a runaway-client backstop).</summary>
       private const int MaxRequestsPerConnection = 1000;
   // ...
                   // Disarm the idle timer for the handler's duration — SSE/long-poll handlers run
                   // until the client disconnects and must not be reaped as "idle".
                   idle.CancelAfter(Timeout.InfiniteTimeSpan);
                   await DispatchAsync(stream, request, ct).ConfigureAwait(false);
   ```

   Request bodies are buffered up to `HttpRequestLine.MaximumBody` (1 MiB); larger ones arrive empty, and the binary upload routes read `DeclaredContentLength` to answer 413 instead.

3. **Route by path segments.** Anything outside `/v1` is a JSON 404 with an errno name; `POST /v1/command` is the one write endpoint; `GET` requests fall through to the read table:

   ```csharp
   // gatOS.Http/SimHttpServer.cs L203-L217
       private async Task DispatchAsync(Stream stream, HttpRequestLine request, CancellationToken ct)
       {
           var seg = request.Segments;
           if (seg.Length == 0 || seg[0] != "v1")
           {
               await WriteJsonAsync(stream, 404, Error("ENOENT", "not found"), ct).ConfigureAwait(false);
               return;
           }

           // POST /v1/command — the one write endpoint (every control action).
           if (request.Method == "POST" && seg.Length == 2 && seg[1] == "command")
           {
               await HandleCommandAsync(stream, request, ct).ConfigureAwait(false);
               return;
           }
   ```

   After the write, field-mirror and upload routes, a non-GET method is answered 405, then `GET /v1/events`, `/v1/time/wait` and `/v1/vessels/{id}/stream` are matched, and everything else goes to `Read`.

4. **Serve reads from the snapshot only.** `Read` takes `_store.Current` once and projects it through the shared `SimJson`/`Formats` layer, so HTTP, MQTT and the file transports produce the same bytes:

   ```csharp
   // gatOS.Http/SimHttpServer.cs L277-L287
       private (int Status, string Json) Read(string[] seg)
       {
           var snapshot = _store.Current;
           switch (seg)
           {
               case ["v1", "snapshot"]:
                   return (200, Serialize(snapshot));
               case ["v1", "openapi.json"]:
                   return (200, OpenApi.Document);
               case ["v1", "time"]:
                   return (200, SimJson.Time(snapshot));
   ```

5. **Stream events with SSE and capture the baseline sequence first.** The handler reads `lastSeq` before it flushes the response head. A client may publish the moment its `GetStream()` returns; capturing the sequence afterwards would swallow that first event:

   ```csharp
   // gatOS.Http/SimHttpServer.cs L333-L358
       private async Task StreamEventsAsync(Stream stream, CancellationToken ct)
       {
           // Snapshot the baseline sequence BEFORE flushing headers. The client's GetStream()
           // completes once the headers arrive, after which it may publish immediately; capturing
           // lastSeq first guarantees such a publish has a sequence > lastSeq and is delivered
           // (WaitForNextAsync rechecks Current), instead of being swallowed by the header-flush gap.
           var lastSeq = _store.Current.Sequence;

           var head = Encoding.ASCII.GetBytes(
               "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\n"
               + "Connection: close\r\n\r\n");
           await stream.WriteAsync(head, ct).ConfigureAwait(false);
           await stream.FlushAsync(ct).ConfigureAwait(false);

           while (!ct.IsCancellationRequested)
           {
               var snapshot = await _store.WaitForNextAsync(lastSeq, ct).ConfigureAwait(false);
               lastSeq = snapshot.Sequence;
               foreach (var simEvent in snapshot.NewEvents)
               {
                   var line = "data: " + SimJson.Event(simEvent) + "\n\n";
                   await stream.WriteAsync(Encoding.UTF8.GetBytes(line), ct).ConfigureAwait(false);
                   await stream.FlushAsync(ct).ConfigureAwait(false);
               }
           }
       }
   ```

6. **Long-poll a sim time.** `GET /v1/time/wait?until=<ut>` loops on `WaitForNextAsync(snapshot.Sequence)` until the published `UtSeconds` reaches the target, so a client can sleep until a sim-time alarm without polling:

   ```csharp
   // gatOS.Http/SimHttpServer.cs L316-L331
       private async Task HandleTimeWaitAsync(Stream stream, HttpRequestLine request, CancellationToken ct)
       {
           if (!request.Query.TryGetValue("until", out var untilText)
               || !double.TryParse(untilText, NumberStyles.Float, CultureInfo.InvariantCulture, out var until))
           {
               await WriteJsonAsync(stream, 400, Error("EINVAL", "missing or invalid 'until' (sim time)"), ct)
                   .ConfigureAwait(false);
               return;
           }

           var snapshot = _store.Current;
           while (snapshot.UtSeconds < until)
               snapshot = await _store.WaitForNextAsync(snapshot.Sequence, ct).ConfigureAwait(false);
           await WriteRawJsonAsync(stream, 200, Serialize(new { reached_ut = snapshot.UtSeconds }), ct)
               .ConfigureAwait(false);
       }
   ```

7. **Accept commands and map outcomes to HTTP statuses.** The body is parsed into a `SimCommand`, validated against the shared `CommandCatalog`, gated (`debug.*` needs the debug namespace) and submitted. The handler returns only after the game thread ran it. Failure outcomes map to the same statuses every transport uses:

   ```csharp
   // gatOS.Http/SimHttpServer.cs L733-L749, L779-L788
           if (command.Action.StartsWith("debug.", StringComparison.Ordinal) && !sink.DebugEnabled)
           {
               await WriteJsonAsync(stream, 403, Error("EACCES", "debug namespace disabled"), ct).ConfigureAwait(false);
               return;
           }

           var result = await sink.SubmitAsync(command, ct).ConfigureAwait(false);
           if (result.IsSuccess)
           {
               await WriteRawJsonAsync(stream, 200, "{\"outcome\":\"ok\"}", ct).ConfigureAwait(false);
               return;
           }

           await WriteJsonAsync(stream, StatusFor(result.Outcome),
               Error(result.Outcome.ErrnoName(), result.Message ?? result.Outcome.ToString()), ct)
               .ConfigureAwait(false);
       }
   // ...
       private static int StatusFor(CommandOutcome outcome) => outcome switch
       {
           CommandOutcome.Invalid => 400,
           CommandOutcome.NotFound => 404,
           CommandOutcome.Denied => 403,
           CommandOutcome.Busy => 409,
           CommandOutcome.TimedOut => 504,
           CommandOutcome.Unsupported => 501,
           _ => 500,
       };
   ```

   `StatusForErrno` does the same for the field routes (`EINVAL` 400, `ENOENT` 404, `EACCES` and `EPERM` 403, `EBUSY` and `EEXIST` 409, `EFBIG` 413, `ENOSPC` 507, `ETIMEDOUT` 504, `EOPNOTSUPP` 501, otherwise 500).

8. **Start in `StarMapAllModsLoaded`, stop in `StarMapUnload`.** A failed start must only cost the optional transport, so the mod catches, logs and leaves `_httpServer` null:

   ```csharp
   // gatOS.GameMod/Mod.cs L674-L693
       private void StartHttpServer()
       {
           if (!_config.HttpEnabled || _simStore is not { } store)
               return;
           try
           {
               var server = new SimHttpServer(store, _commandQueue, SimTransportsStatus,
                   _config.HttpFieldEndpoints ? _simRoot : null, _audioStore, _textureStore);
               server.StartAsync(_config.HttpPreferredPort, _config.HttpBindHost).GetAwaiter().GetResult();
               _httpServer = server;
               ModLog.Log.Info($"gatOS HTTP API listening on {FormatEndpoint(server.BindHost, server.Port)} "
                               + "(guest: $GATOS_HTTP / http://sim:<port>/v1).");
           }
           catch (Exception ex)
           {
               // HTTP is an optional luxury like /sim: the VM, shells and 9p work without it.
               _httpServer = null;
               ModLog.Log.Error($"The magic HTTP server failed to start: {ex.Message}", ex);
           }
       }
   ```

   At unload the server is disposed with a bounded wait so a hung connection cannot delay the exit:

   ```csharp
   // gatOS.GameMod/Mod.cs L520-L523
               var httpServer = _httpServer;
               _httpServer = null;
               if (httpServer is not null && !httpServer.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)))
                   ModLog.Log.Warn("The HTTP server did not stop within 5 s at unload.");
   ```

## Complete example

| File | Role |
|---|---|
| [gatOS.Http/SimHttpServer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Http/SimHttpServer.cs) | Listener, routing, SSE, long-poll, command and upload endpoints |
| [gatOS.Http/HttpRequestLine.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Http/HttpRequestLine.cs) | Request-line, header and query parser with a 1 MiB body cap |
| [gatOS.Http/OpenApi.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Http/OpenApi.cs) | The OpenAPI document served at `GET /v1/openapi.json` |
| [gatOS.SimFs/SimJson.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/SimJson.cs) | The JSON projections shared with MQTT and MCP |
| [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L674-L693) | Start, status text and failure handling |

## Variations and gotchas

- **Loopback by default, no authentication.** A non-loopback `HttpBindHost` exposes every control endpoint to the network; gatOS documents this as intentional and offers no token. Keep the default unless the player chooses otherwise.
- **Capture the SSE baseline before the head.** The comment in `StreamEventsAsync` explains the lost-event race; the same ordering applies to any publish/subscribe handler.
- **Do not reuse the text field path for binary data.** Field writes are UTF-8 text capped at 1 MiB; audio and texture uploads have their own `PUT` routes that honour `DeclaredContentLength`.
- **HttpListener is not an option.** It rides http.sys on Windows and needs a URL reservation; a game mod cannot assume administrator rights, which is why the parser is hand-written.
- **Keep handlers off the game objects.** A route that needs a new field must add it to the snapshot or the command catalog, not read `Program` or `Universe` directly from a pool thread.
- **An open connection keeps the sampler awake.** gatOS samples telemetry only while the VM is starting or running or a transport has a client, and `ActiveSessions` of this server is one of those conditions ([gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L238-L246)); a polling script therefore costs sampling work only while it is connected. See [Export live game state to background threads](../../telemetry/export-game-state-to-other-threads/overview.md).
- **Few game-update risks.** No KSA member is called in this article; what a game update can break lives in the sampler and the command catalog.

## See also

- [Overview](overview.md), [Embedded MQTT broker](host-http-and-mqtt-servers-in-the-game-process-2.md)
- [HTTP /v1 API server](../../../../mods/gatos/features/http-v1-api-server.md)
- [Let AI agents drive the game through an MCP server](../../control/let-ai-agents-control-the-game-over-mcp/overview.md), [Build an external SDK client or autopilot](../../control/build-external-clients-and-autopilots-with-a-typed-sdk/overview.md)
- [Connect a VM guest with slirp TCP, a 9P filesystem and pinned SSH](../../processes/host-guest-transports-tcp-9p-and-ssh/overview.md)
