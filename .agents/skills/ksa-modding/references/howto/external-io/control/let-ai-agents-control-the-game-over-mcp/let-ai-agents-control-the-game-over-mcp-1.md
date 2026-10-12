---
type: How-To
title: Stateless Streamable HTTP MCP endpoint inside the game process
description: Host the official MCP C# SDK behind a hand-rolled HTTP adapter in the mod, map tools to the command queue and reads to immutable snapshots, and shape the tool surface for a language-model client
category: external-io
group: control
topic: let-ai-agents-control-the-game-over-mcp
approach: 1
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/mcp-server-for-agents]
techniques: [protocol-server, background-thread, starmap-lifecycle]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapUnload]
lifecycle: [startup:all-mods-loaded, thread:background, event:unload, frame:after-on-frame]
game_types: [KSA.Program, KSA.Universe]
tags: [mcp, streamable-http, stateless, dns-rebinding, tools, resources, agents]
---

Outcome: an MCP client (Claude Code, any MCP-capable agent) connects to `http://127.0.0.1:4243/mcp` while the game runs, reads logical JSON documents (world, vessels, celestials) and calls control tools whose effects land on the game thread. Requires StarMap, the `ModelContextProtocol.Core` NuGet package (gatOS pins 2.2.0) shipped with the mod (see [Ship third-party DLLs with a mod](../../../getting-started/dependencies/ship-third-party-dlls-with-a-mod/overview.md)), and the command queue and snapshot store from [Marshal commands from background threads to the game thread](../marshal-commands-to-the-game-thread/overview.md) and [Export live game state to background threads](../../telemetry/export-game-state-to-other-threads/overview.md). Demonstrated by [MCP server for agents](../../../../mods/gatos/features/mcp-server-for-agents.md) in gatOS.

## Background: how the game does it

The game has no network API for mods and no hosted web stack worth depending on. gatOS therefore does not host ASP.NET: a `TcpListener` accepts connections, a small adapter parses each POST, and the SDK's low-level `StreamableHttpServerTransport` plus `McpServer` handle MCP itself. None of this touches KSA objects; the game-facing end is the two seams from the sibling topics.

## Lifecycle and timing

| Piece | Runs |
|---|---|
| `StartMcpServer` | In the `[StarMapAllModsLoaded]` initializer (`startup:all-mods-loaded`), with the other transports: it binds the listener and returns |
| Accept loop, per-connection `ServeAsync`, tool handlers | Thread-pool tasks (`thread:background`); a semaphore of 32 bounds concurrent connections |
| Reads (`gatos.get_*`, resources) | Request threads, from the immutable `SnapshotStore.Current` |
| Writes | Request threads submit to the command queue and await; the game thread executes them in its frame drain |
| `DisposeAsync` | `[StarMapUnload]` (`event:unload`), bounded to 5 s so a stuck listener cannot hold the game exit |

Hidden-HUD note: nothing in this path depends on the GUI hooks. The command drain lives in the main-viewport `GameViewport.OnFrame` prefix (with a fallback to `StarMapAfterOnFrame`), so a hidden HUD does not stop MCP writes; see [approach 1 of the marshalling topic](../marshal-commands-to-the-game-thread/overview.md). Active MCP requests also wake the telemetry sampler, which otherwise idles when no client is connected ([gatOS.GameMod/Game/Mod.Game.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Game/Mod.Game.cs#L238-L247)).

## Steps

1. **Start the server with a preferred port that falls back to an ephemeral one.** `StartAsync` binds a `TcpListener`, records the actual port and launches the accept loop with `Task.Run`.

   ```csharp
   // gatOS.Mcp/SimMcpServer.cs L56-L86
       /// <summary>Bind the configured address and preferred port, falling back to an ephemeral port.</summary>
       public Task StartAsync(int preferredPort = 4243, string bindHost = "127.0.0.1")
       {
           ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
           var address = IPAddress.TryParse(bindHost, out var parsed)
               ? parsed
               : throw new ArgumentException("Bind host must be an IPv4 or IPv6 address.", nameof(bindHost));
           _listener = Bind(address, preferredPort);
           var endpoint = (IPEndPoint)_listener.LocalEndpoint;
           BindHost = endpoint.Address.ToString();
           Port = endpoint.Port;
           _acceptLoop = Task.Run(() => AcceptLoopAsync(_stop.Token));
           return Task.CompletedTask;
       }

       private static TcpListener Bind(IPAddress address, int preferredPort)
       {
           if (preferredPort > 0)
           {
               try
               {
                   var preferred = new TcpListener(address, preferredPort);
                   preferred.Start();
                   return preferred;
               }
               catch (SocketException) { }
           }
           var ephemeral = new TcpListener(address, 0);
           ephemeral.Start();
           return ephemeral;
       }
   ```

2. **Run the accept and per-connection loops off the game thread, and shed load.** When all 32 slots are taken the connection is rejected immediately instead of queuing.

   ```csharp
   // gatOS.Mcp/SimMcpServer.cs L88-L110
       private async Task AcceptLoopAsync(CancellationToken ct)
       {
           while (!ct.IsCancellationRequested)
           {
               TcpClient client;
               try { client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
               catch (OperationCanceledException) { return; }
               catch (Exception ex)
               {
                   Interlocked.Increment(ref _errorCount);
                   ModLog.Log.Debug($"mcp: accept failed: {ex.Message}");
                   continue;
               }

               if (!await _requestSlots.WaitAsync(0, ct).ConfigureAwait(false))
               {
                   await RejectBusyAsync(client, ct).ConfigureAwait(false);
                   continue;
               }
               Interlocked.Increment(ref _activeConnections);
               _ = Task.Run(() => ServeAsync(client, ct), CancellationToken.None);
           }
       }
   ```

3. **Validate every request before the SDK sees it.** Only `POST /mcp`; no `Mcp-Session-Id`; JSON content type; and the `Host` and any `Origin` header must name the configured endpoint, which defends a loopback server against DNS rebinding from a web page. Bodies are capped at 24 MiB and each request at 3 minutes (constants at the top of [gatOS.Mcp/SimMcpServer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Mcp/SimMcpServer.cs#L21-L24)).

   ```csharp
   // gatOS.Mcp/SimMcpServer.cs L227-L251
       private bool ValidateHttp(HttpRequest r, out int status, out string error)
       {
           status = 400;
           error = "bad request";
           if (r.Method != "POST") { status = 405; error = "only POST /mcp is supported"; return false; }
           if (r.Target != "/mcp") { status = 404; error = "not found"; return false; }
           if (r.Header("mcp-session-id") is not null) { error = "MCP sessions are not supported"; return false; }
           var contentType = r.Header("content-type");
           if (contentType is null || !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
           { status = 415; error = "Content-Type must be application/json"; return false; }
           var accept = r.Header("accept");
           if (accept is not null && !accept.Contains("application/json", StringComparison.OrdinalIgnoreCase) && !accept.Contains("*/*", StringComparison.Ordinal))
           { status = 406; error = "Accept must allow application/json"; return false; }
           var expectedPort = Port.ToString(CultureInfo.InvariantCulture);
           var host = r.Header("host");
           if (!IsAllowedAuthority(host, expectedPort))
           { status = 403; error = "Host is not the configured endpoint"; return false; }
           var origin = r.Header("origin");
           if (origin is not null && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                                      || uri.Scheme != Uri.UriSchemeHttp
                                      || !IsAllowedHost(uri.Host)
                                      || uri.Port != Port))
           { status = 403; error = "Origin is not the configured endpoint"; return false; }
           return true;
       }
   ```

4. **Serve each POST with a fresh stateless transport and server.** Statelessness means no session table and nothing to clean up per client; the SDK owns discovery, negotiation, dispatch and schemas, the adapter owns framing.

   ```csharp
   // gatOS.Mcp/SimMcpServer.cs L205-L225
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

5. **Register tools and resources once, from an immutable registry** reused by every request. Reads are marked `ReadOnly` and `Idempotent`; writes are not, and one-shot triggers are marked `Destructive`. Wrap every tool so a result whose envelope says `ok: false` is flagged `IsError` for the client:

   ```csharp
   // gatOS.Mcp/McpRegistry.cs L102-L132
       private void AddRead(string name, Delegate method, string description) => Tools.Add(new ErrorMarkingTool(McpServerTool.Create(method, new()
       {
           Name = name, Description = description, ReadOnly = true, Idempotent = true, Destructive = false,
           OpenWorld = false, UseStructuredContent = true, SerializerOptions = RegistryJson,
       })));

       private void AddWrite(string name, Delegate method, string description, bool destructive = false, JsonElement? outputSchema = null) => Tools.Add(new ErrorMarkingTool(McpServerTool.Create(method, new()
       {
           Name = name, Description = description, ReadOnly = false, Idempotent = false, Destructive = destructive,
           OpenWorld = false, UseStructuredContent = true, OutputSchema = outputSchema, SerializerOptions = RegistryJson,
       })));

       private void AddResource(string name, string uri, Delegate method, string description) => Resources.Add(McpServerResource.Create(method, new()
       {
           Name = name, UriTemplate = uri, Description = description, MimeType = "application/json", SerializerOptions = RegistryJson,
       }));

       private sealed class ErrorMarkingTool(McpServerTool inner) : DelegatingMcpServerTool(inner)
       {
           public override async ValueTask<CallToolResult> InvokeAsync(
               RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
           {
               var result = await base.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
               if (result.StructuredContent is JsonElement structured
                   && structured.ValueKind == JsonValueKind.Object
                   && structured.TryGetProperty("ok", out var ok)
                   && ok.ValueKind == JsonValueKind.False)
                   result.IsError = true;
               return result;
           }
       }
   ```

6. **Return one result envelope everywhere.** It carries the snapshot sequence and universal time the answer was derived from, and on failure the outcome, the errno name from the shared mapping, a message and whether a retry is sensible (`TimedOut` and `Busy` only):

   ```csharp
   // gatOS.Mcp/McpContracts.cs L30-L46
   /// <summary>The common structured result envelope returned by ordinary gatOS MCP tools.</summary>
   public sealed record McpEnvelope(
       bool Ok,
       object? Data,
       long SnapshotSequence,
       double Ut,
       string? Outcome = null,
       string? Errno = null,
       string? Message = null,
       bool Retryable = false)
   {
       internal static McpEnvelope Success(object? data, long sequence, double ut) => new(true, data, sequence, ut);

       internal static McpEnvelope Failure(CommandResult result, long sequence, double ut) => new(
           false, null, sequence, ut, result.Outcome.ToString().ToLowerInvariant(), result.Outcome.ErrnoName(),
           result.Message, result.Outcome is CommandOutcome.TimedOut or CommandOutcome.Busy);
   }
   ```

7. **Map writes to the queue.** A single tool call validates against the action catalog, submits through `ICommandSink.SubmitAsync` and awaits the game thread; a batch goes through `SubmitBatchAsync` so it executes in one drain (1 to 64 entries, one phase):

   ```csharp
   // gatOS.Mcp/McpPresenters.cs L250-L277
       public async Task<McpEnvelope> SubmitAsync(McpCommandEnvelope envelope, CancellationToken ct)
       {
           var s = Current;
           if (_commands is null) return new(false, null, s.Sequence, s.UtSeconds, "denied", "EACCES", "MCP is read-only", false);
           var command = envelope.ToCommand();
           var validation = CommandCatalog.Validate(command);
           if (!validation.IsValid)
               return Invalid(validation.Error ?? "invalid command", s);
           var result = await _commands.SubmitAsync(command, ct).ConfigureAwait(false);
           return result.IsSuccess ? Ok(new { accepted = 1, action = command.Action, phase = command.Phase.ToString().ToLowerInvariant() }, Current) : McpEnvelope.Failure(result, Current.Sequence, Current.UtSeconds);
       }

       public async Task<McpEnvelope> SubmitBatchAsync(IReadOnlyList<McpCommandEnvelope> envelopes, CancellationToken ct)
       {
           var s = Current;
           if (_commands is null) return new(false, null, s.Sequence, s.UtSeconds, "denied", "EACCES", "MCP is read-only", false);
           if (envelopes.Count is < 1 or > 64) return Invalid("commands must contain 1..64 entries", s);
           var commands = envelopes.Select(x => x.ToCommand()).ToArray();
           var build = CommandBatchBuilder.Build(commands);
           if (!build.IsValid)
               return Invalid(build.Validation.Error ?? "invalid batch", s);
           var batch = build.Batch!;
           var phase = batch.Phase;
           var result = await _commands.SubmitBatchAsync(batch.Commands, ct).ConfigureAwait(false);
           return result.IsSuccess
               ? Ok(new { accepted = commands.Length, phase = phase.ToString().ToLowerInvariant(), outcome = "ok" }, Current)
               : McpEnvelope.Failure(result, Current.Sequence, Current.UtSeconds);
       }
   ```

8. **Give the agent a way to wait.** `gatos.wait` blocks until a newer snapshot, a named event or a sim time, so an agent can act and then observe the effect without polling; its timeout returns a retryable `ETIMEDOUT` envelope instead of throwing ([gatOS.Mcp/McpPresenters.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Mcp/McpPresenters.cs#L221-L248)).

9. **Start and stop with the mod.** Startup failure is logged and leaves the rest of the mod usable; unload waits at most 5 seconds.

   ```csharp
   // gatOS.GameMod/Mod.cs L733-L752
       private void StartMcpServer()
       {
           if (!_config.McpEnabled || _simStore is not { } store)
               return;
           try
           {
               var registry = new McpRegistry(store, _commandQueue, SimTransportsStatus,
                   _audioStore, _cameraStore, _scheduleStore, _paintStore, _textureStore, _stickerStore);
               var version = typeof(Mod).Assembly.GetName().Version?.ToString() ?? "1.0.0";
               var server = new SimMcpServer(registry, version);
               server.StartAsync(_config.McpPreferredPort, _config.McpBindHost).GetAwaiter().GetResult();
               _mcpServer = server;
               ModLog.Log.Info($"gatOS MCP listening on http://{FormatEndpoint(server.BindHost, server.Port)}/mcp.");
           }
           catch (Exception ex)
           {
               _mcpServer = null;
               ModLog.Log.Error($"The MCP server failed to start: {ex.Message}", ex);
           }
       }
   ```

   ```csharp
   // gatOS.GameMod/Mod.cs L530-L533
               var mcpServer = _mcpServer;
               _mcpServer = null;
               if (mcpServer is not null && !mcpServer.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)))
                   ModLog.Log.Warn("The MCP server did not stop within 5 s at unload.");
   ```

## Designing the tool surface for an agent

A language-model client pays for every round trip and every ambiguous parameter, so gatOS shapes the MCP surface differently from its 9p file tree (the contract in [SPEC_MCP.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/SPEC_MCP.md#L1-L12) says it is not a path-for-path mirror of `/sim`):

- **Logical, grouped reads.** `gatos://world`, `gatos://vessels/{id}`, `gatos://celestials/{id}`, `gatos://kittens/{id}`, `gatos://runtime/{feature}` and `gatos://capabilities` return whole documents; `gatos.get_vessel` takes a list of sections. List tools paginate with an opaque cursor (default 50, maximum 1000). This replaces hundreds of per-leaf reads.
- **Batch and scheduled actions.** `gatos.execute_batch` is the same-tick, single-phase group; `gatos.schedule_batch` takes `{at_ms, command}` entries on a `render`, `wall` or `ut` clock and may mix phases. See [Schedule timed command sequences](../schedule-timed-commands/overview.md).
- **Stable action keys.** `gatos.command` takes the canonical envelope (`action`, `vessel_id`, `ordinal`, `value`, `values`, `token`, `aux`) and `gatos.get_capabilities` lists every action with its limits, gates, phase and safety note, so the agent discovers rather than guesses.
- **Narrow tools for common cases, multipurpose tools discriminated by `operation`.** `gatos.ignite_engines`, `gatos.activate_stage` and friends are single-purpose; `vessel_control` and `module_control` take an `operation` and the parameter descriptions say which slot (`value`, `values`, `token`, `aux`) it uses.
- **Actionable errors.** Failures are tool errors (`isError`) with `errno`, `outcome`, `message` and `retryable`. The server instructions also tell the agent not to retry one-shot triggers (stage, ignite, undock, decoupler fire):

   ```csharp
   // gatOS.Mcp/McpRegistry.cs L88-L92
       public McpServerOptions CreateOptions(string version) => new()
       {
           ServerInfo = new Implementation { Name = "gatOS", Version = version },
           ServerInstructions = "Operating loop: call gatos.get_capabilities, discover raw ids, read the target and its controllable/gate state, call the narrowest tool, then gatos.wait(after_sequence=previous) and re-read before planning again. Multipurpose control tools are discriminated by operation: read each parameter description and the matching capability action before filling value/values/token/aux; omit irrelevant slots. KSA vectors use documented CCI/body frames. execute_batch is same-tick and single-phase; schedule_batch uses absolute at_ms offsets and may mix phases. Never blindly retry one-shot triggers such as stage, ignite, undock, decoupler fire, release, clear, or remove. Expected failures return isError plus structured errno/retryable fields. /sim/display is intentionally absent because it is a TTY-only visual stream.",
           ScopeRequests = false,
   ```

- **An operating loop in the instructions.** `ServerInstructions` states the loop the agent should follow: capabilities, discover raw ids, read target and gate state, call the narrowest tool, then `gatos.wait(after_sequence=...)` and re-read.
- **Raw ids, not display names.** MCP uses raw KSA ids so an agent never has to learn the sanitised names that exist only for file paths.

## Complete example

| File | Role |
|---|---|
| [gatOS.Mcp/SimMcpServer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Mcp/SimMcpServer.cs) | TCP listener, HTTP validation, per-request stateless SDK server |
| [gatOS.Mcp/McpRegistry.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Mcp/McpRegistry.cs) | Resource and tool registration, options, server instructions |
| [gatOS.Mcp/McpToolHandlers.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Mcp/McpToolHandlers.cs) | Tool implementations over the command sink and stores |
| [gatOS.Mcp/McpPresenters.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Mcp/McpPresenters.cs) | Logical JSON documents, wait, submit and batch submit |
| [gatOS.Mcp/McpContracts.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Mcp/McpContracts.cs) | Command envelope, schedule entry, result envelope |
| [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L733-L752) | `StartMcpServer` |

## Variations and gotchas

- **There is no bearer token.** The default bind is `127.0.0.1`; a non-loopback `mcp_bind_host` is unauthenticated remote control of the game. Keep it on loopback.
- **Hosting ASP.NET Core inside the game is heavy**; the SDK's low-level transport plus a small adapter keeps the dependency to `ModelContextProtocol.Core`.
- **SDK pinning.** The adapter pins the SDK to exactly 2.2.0 and suppresses `MCPEXP002` for an experimental routing API; upgrades need re-checking. The repository marks the live in-game MCP validation pass as pending.
- **Timeouts.** When the game thread does not reach the drain (loading, a stalled frame; a simulation pause alone does not stop it) write tools return `ETIMEDOUT` with `retryable: true` rather than hanging.
- **Mixed phases.** `execute_batch` rejects batches that mix Frame and Solver phases.
- **Stdio alternative.** If you cannot host a server in the game, [approach 2](let-ai-agents-control-the-game-over-mcp-2.md) wraps a file bridge instead.

## See also

- [Overview](overview.md)
- [Approach 2: stdlib Python stdio server wrapping a file bridge](let-ai-agents-control-the-game-over-mcp-2.md)
- [MCP server for agents](../../../../mods/gatos/features/mcp-server-for-agents.md), [HTTP /v1 API server](../../../../mods/gatos/features/http-v1-api-server.md)
- [Host HTTP and MQTT servers in the game process](../../network/host-http-and-mqtt-servers-in-the-game-process/overview.md)
- [Marshal commands from background threads to the game thread](../marshal-commands-to-the-game-thread/overview.md), [Schedule timed command sequences](../schedule-timed-commands/overview.md)
- [Reload a mod shader in a running game](../../../shaders/glsl-workflow/reload-a-mod-shader-in-a-running-game/overview.md)
