---
type: Mod Feature
title: HTTP /v1 API server
description: A dependency-free HTTP/1.1 server on a raw TcpListener inside the game process that serves JSON snapshots, Server-Sent Events, a long-poll sim-time wait, an OpenAPI document, a per-leaf mirror of /sim and one generic command endpoint
mod: gatos
category: external-io
techniques: [background-thread, protocol-server, command-queue, immutable-snapshot]
hooks: [starmap StarMapAllModsLoaded, thread:background SimHttpServer.AcceptLoopAsync]
lifecycle: [startup:all-mods-loaded, thread:background, event:unload]
game_types: []
tags: [http, rest, sse, openapi, json, telemetry, threading]
---

`$GATOS_HTTP` inside the guest (default `http://127.0.0.1:4242/v1` on the host) gives dashboards,
scripts and any language with an HTTP client the same data and controls as `/sim`. The server is
written directly on `TcpListener` and a small request parser, so the mod carries no web framework
into the game's process, and it never touches a KSA object: reads project the published snapshot
and writes go through the shared command queue.

## How it works

**Binding** ([gatOS.Http/SimHttpServer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Http/SimHttpServer.cs#L103-L122)): try the preferred port
(`http_preferred_port`, 4242) on `http_bind_host` (default `127.0.0.1`), fall back to an
ephemeral port on a clash; `0` always chooses ephemeral. The bound port is passed to the guest as
`gatos.httpport=` on the kernel command line.

**Connections**: an accept loop on the thread pool spawns `ServeAsync` per client; HTTP/1.1
keep-alive with an idle timeout; request bodies capped at 1 MiB except the dedicated binary upload
routes.

**Routes** ([gatOS.Http/SimHttpServer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Http/SimHttpServer.cs#L276-L305)):

| Route | Source |
|---|---|
| `GET /v1/snapshot`, `/time`, `/status`, `/system`, `/bodies[/{id}]`, `/vessels[/{id}[/telemetry]]` | `SnapshotStore.Current` through the shared `SimJson`/`Formats` projections |
| `GET /v1/events`, `/v1/vessels/{id}/stream` | SSE loops on `SnapshotStore.WaitForNextAsync` |
| `GET /v1/time/wait?until=<ut>` | long-poll until published sim time reaches the target |
| `GET /v1/fs/<path>` (`?stream=1` for SSE), `POST /v1/fs/<path>` | the `/sim` VFS tree itself, via `VfsScan.Resolve` |
| `POST /v1/command` | generic `SimCommand` JSON into `ICommandSink.SubmitAsync` |
| `PUT /v1/audio/file/<name>`, `/v1/paint/texture/file/<name>` | binary uploads into the audio and texture stores |
| `GET /v1/openapi.json` | a hand-written OpenAPI 3.1 JSON string in `OpenApi.cs` (the fuller contract is `sim_openapi.yml` in the repo) |

**SSE without lost events.** The handler captures `lastSeq = store.Current.Sequence` before
flushing headers, so a publish racing the header write is still delivered, then loops on
`WaitForNextAsync(lastSeq)` writing `data: <json>\n\n` per event
([gatOS.Http/SimHttpServer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Http/SimHttpServer.cs#L333-L356)).

**Errors.** A command outcome maps to an HTTP status and `{"errno","message"}` body: EINVAL 400,
ENOENT 404, EACCES 403, EBUSY 409, EFBIG 413, ETIMEDOUT 504 and so on; `debug.*` actions return 403
when `debug_namespace` is off.

**Field mirror.** `/v1/fs/<path>` resolves a leaf in the very VFS tree the 9p server serves and
reads or writes it as text, so adding a `/sim` node lights up an HTTP endpoint with no new code;
streaming files are excluded from the bulk walk ([gatOS.NineP/Vfs/VfsScan.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.NineP/Vfs/VfsScan.cs)).

## Lifecycle and timing

- Started synchronously in `[StarMapAllModsLoaded]` (`StartHttpServer`), before any VM boots,
  and disposed at `[StarMapUnload]` with a 5 s bounded wait ([gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L676-L695)).
- Every handler runs on thread-pool tasks (`thread:background`). Reads are lock-free volatile
  reads of the immutable snapshot; writes await the game thread through the command queue, so a
  `POST /v1/command` returns after the command actually executed (or 504 on timeout).
- An open HTTP session also wakes the telemetry sampler, which otherwise idles when nobody listens.

## Key code

| Path | Role |
|---|---|
| [gatOS.Http/SimHttpServer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Http/SimHttpServer.cs) | listener, routing, SSE, long-poll, command and upload endpoints |
| [gatOS.Http/HttpRequestLine.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Http/HttpRequestLine.cs) | minimal HTTP/1.1 request parser with size limits |
| [gatOS.Http/OpenApi.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Http/OpenApi.cs) | serves the OpenAPI document |
| [gatOS.SimFs/SimJson.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/SimJson.cs) | the shared JSON projection used by HTTP, MQTT and MCP |
| [sim_openapi.yml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/sim_openapi.yml) | the published HTTP contract |

## Snippets

```csharp
// gatOS.Http/SimHttpServer.cs L333-L356
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
```

## Game APIs touched

None; see [Telemetry sampler and snapshot store](telemetry-sampler-snapshot.md) for where the data
comes from and [Game-thread command queue](game-thread-command-queue.md) for how writes are executed.

## Pitfalls and version notes

- A non-loopback bind host exposes control endpoints without authentication; gatOS documents this
  as intentional, unauthenticated exposure.
- Capture the SSE baseline sequence before writing headers or the first event after connect can be lost.
- Keep binary uploads off the text field-write path (UTF-8, 1 MiB) and give them their own routes.

## See also

- [Embedded MQTT broker](embedded-mqtt-broker.md)
- [MCP server for AI agents](mcp-server-for-agents.md)
- [External API clients and SDK](external-api-clients.md)
- [The /sim file models](sim-filesystem-file-models.md)
- [gatOS overview](../overview.md)
- [Raw TcpListener server with SSE, long-poll, OpenAPI and a generic command endpoint](../../../howto/external-io/network/host-http-and-mqtt-servers-in-the-game-process/host-http-and-mqtt-servers-in-the-game-process-1.md) - how-to article demonstrating this feature

