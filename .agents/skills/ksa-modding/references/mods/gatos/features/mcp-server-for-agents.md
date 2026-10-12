---
type: Mod Feature
title: MCP server for AI agents
description: A stateless Streamable HTTP Model Context Protocol endpoint inside the game process, built on the official C# SDK with a hand-rolled HTTP adapter, that groups the simulation into logical JSON resources and control tools for AI agents
mod: gatos
category: external-io
techniques: [background-thread, protocol-server, command-queue, immutable-snapshot]
hooks: [starmap StarMapAllModsLoaded, thread:background SimMcpServer.HandleMcpAsync]
lifecycle: [startup:all-mods-loaded, thread:background, event:unload]
game_types: []
tags: [mcp, ai-agents, json-rpc, http, tools, resources, threading]
---

gatOS lets an AI agent (Claude, any MCP client) read and fly the game. Rather than exposing
hundreds of `/sim` leaves as tools, the MCP surface is deliberately logical: grouped JSON reads for
the world, celestials, vessels, kittens and runtime features, plus control tools such as
`gatos.ignite_engines`, `gatos.vessel_control`, `gatos.execute_batch` (same-tick) and
`gatos.schedule_batch` (timed). It reads the same snapshots and sends the same commands as every
other transport. The default endpoint is `http://127.0.0.1:4243/mcp`.

## How it works

**HTTP adapter over the SDK** ([gatOS.Mcp/SimMcpServer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Mcp/SimMcpServer.cs#L170-L227)): gatOS does not host
ASP.NET. A `TcpListener` accepts connections; each POST is parsed, validated, and handed to a fresh
`StreamableHttpServerTransport { Stateless = true }` and `McpServer.Create(...)` from
`ModelContextProtocol.Core` 2.2.0, whose output is streamed back through a chunked response. The SDK
owns discovery, negotiation, dispatch and schemas; the adapter owns framing and request security.

**Request security** ([gatOS.Mcp/SimMcpServer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Mcp/SimMcpServer.cs#L229-L250)): only `POST /mcp`; no
`Mcp-Session-Id` (stateless); `Content-Type: application/json`; the `Host` header must match the
configured endpoint and port and any `Origin` must too (DNS-rebinding protection); request bodies are
capped at 24 MiB (`MaximumRequestBytes`); each request has a 3 minute timeout. There is no bearer token, so a
non-loopback `mcp_bind_host` is unauthenticated exposure.

**Registry** ([gatOS.Mcp/McpRegistry.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Mcp/McpRegistry.cs#L73-L80)): resources `gatos://world`,
`gatos://celestials[/{id}]`, `gatos://vessels[/{id}]`, `gatos://kittens[/{id}]`,
`gatos://runtime/{feature}`, and tools including `gatos.get_capabilities`, `gatos.get_world`,
`gatos.get_vessel`, `gatos.list_vessels`, `gatos.vessel_control`, `gatos.module_control`,
`gatos.activate_stage`, `gatos.camera_control`, `gatos.paint_control`, `gatos.audio_control`,
`gatos.command` (canonical envelope), `gatos.execute_batch`, `gatos.schedule_batch` and `gatos.wait`
(block for a later snapshot, an event or a sim time).

**Shared model.** `McpPresenters` projects `SnapshotStore.Current` (and the audio, camera, schedule,
texture and sticker stores) into JSON; `McpToolHandlers` turns tool calls into `SimCommand`s submitted
through `ICommandSink` (batches through `SubmitBatchAsync`, schedules through `ScheduleStore`).
`/sim/display` is explicitly excluded from MCP v1.

## Lifecycle and timing

- Started in `[StarMapAllModsLoaded]` (`StartMcpServer`), disposed at `[StarMapUnload]`.
- All request handling runs on thread-pool tasks (`thread:background`); a request semaphore
  bounds concurrency. Reads never touch KSA; actions wait for the game-thread drain like any other
  transport and return the outcome and errno name.
- Active MCP requests also wake the idle telemetry sampler.

## Key code

| Path | Role |
|---|---|
| [gatOS.Mcp/SimMcpServer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Mcp/SimMcpServer.cs) | TCP listener, HTTP validation, per-request stateless SDK server |
| [gatOS.Mcp/McpRegistry.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Mcp/McpRegistry.cs) | resource and tool registration and options |
| [gatOS.Mcp/McpToolHandlers.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Mcp/McpToolHandlers.cs) | tool implementations over the command sink and stores |
| [gatOS.Mcp/McpPresenters.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Mcp/McpPresenters.cs) | logical JSON documents from snapshots |
| [gatOS.Mcp/ModLogLoggerFactory.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Mcp/ModLogLoggerFactory.cs) | routes SDK logging into the mod log |
| [SPEC_MCP.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/SPEC_MCP.md) | the published MCP contract |

## Snippets

```csharp
// gatOS.Mcp/SimMcpServer.cs L205-L226
        await using var transport = new StreamableHttpServerTransport(ModLogLoggerFactory.Instance)
        {
            Stateless = true,
            FlowExecutionContextFromRequests = false,
        };
        await using var server = McpServer.Create(transport, _registry.CreateOptions(_version), ModLogLoggerFactory.Instance, null);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var serverTask = server.RunAsync(run.Token);
        await using var response = new ChunkedResponseStream(network, keepAlive);
        try
        {
            var wrote = await transport.HandlePostRequestAsync(message, response, ct).ConfigureAwait(false);
            if (!wrote) await response.CompleteAsync(202, ct).ConfigureAwait(false);
            else await response.CompleteAsync(200, ct).ConfigureAwait(false);
        }
        finally
        {
            run.Cancel();
            try { await serverTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

```

## Game APIs touched

None.

## Pitfalls and version notes

- Hosting ASP.NET Core inside the game is heavy; the SDK's low-level transport plus a small HTTP
  adapter keeps the dependency to `ModelContextProtocol.Core`.
- Validate `Host` and `Origin` on any local HTTP endpoint that can take actions.
- The adapter pins the SDK to exactly 2.2.0 and uses an experimental routing API (`MCPEXP002`
  suppressed in code); SDK upgrades need re-checking.
- The repo marks the live in-game MCP validation pass as pending.

## See also

- [HTTP /v1 API server](http-v1-api-server.md)
- [Game-thread command queue](game-thread-command-queue.md)
- [Timed command scheduler](timed-command-scheduler.md)
- [External API clients and SDK](external-api-clients.md)
- [gatOS overview](../overview.md)
- [Stateless Streamable HTTP MCP endpoint inside the game process](../../../howto/external-io/control/let-ai-agents-control-the-game-over-mcp/let-ai-agents-control-the-game-over-mcp-1.md) - how-to article demonstrating this feature

