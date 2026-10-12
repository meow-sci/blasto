---
type: How-To
title: SSH PTY session with lazy VM boot, bounded input writer thread and thread-agnostic output
description: Plug an SSH session into a guest VM into the terminal as a custom shell whose keystrokes never block the tick and whose output needs no marshalling to the main thread
category: external-io
group: terminals
topic: host-a-terminal-with-pty-sessions-and-frame-handoff
approach: 4
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/ssh-shell-session-into-guest, mods/gatos/features/purrtty-custom-shell-contract]
techniques: [background-thread, inter-mod-api]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [thread:background, startup:all-mods-loaded, frame:draw-ui]
game_types: []
tags: [terminal, ssh, custom-shell, vm, input-queue, inter-mod]
---

Outcome: a terminal tab that is an SSH session into a guest VM, where starting the tab boots the VM if needed, keystrokes never block the render tick, and output arrives from SSH threads without any marshalling to the main thread. gatOS implements the purrTTY custom-shell contract in a separate mod, so it also shows the inter-mod registration. Prerequisites: StarMap, the purrTTY contract assembly, SSH.NET and a hosted guest ([Host and supervise an external process or virtual machine](../../processes/host-and-supervise-an-external-process-from-a-mod/overview.md)). Demonstrated by [SSH shell sessions into the guest](../../../../mods/gatos/features/ssh-shell-session-into-guest.md) and [purrTTY custom shell contract](../../../../mods/gatos/features/purrtty-custom-shell-contract.md) in gatOS.

## Background: how the game does it

The game is not involved: the SSH session runs entirely on background threads and purrTTY's surface `Write` is the one thread-safe entry point. What matters is who calls whom. purrTTY's tick calls `WriteInputAsync` on the main thread (`CustomShellPtyBridge.Write`), so the session must never perform a potentially blocking SSH write there. Output events come from SSH.NET and pump threads and flow straight into `Surface.Write`, which copies into the inbox (see [the surface article](host-a-terminal-with-pty-sessions-and-frame-handoff-2.md)).

## Lifecycle and timing

| Work | Where |
|---|---|
| Register the shell | `[StarMapAllModsLoaded]` of gatOS (`startup:all-mods-loaded`); nothing boots at that point |
| `StartAsync`: lazy VM boot, SSH connect, open the pty | the session-creation pool thread; may take seconds |
| `WriteInputAsync` | the main thread, inside purrTTY's tick (`frame:draw-ui`); only enqueues |
| Input writer | one dedicated background thread per session (`thread:background`) |
| Output pump | one dedicated background thread per session, `gatOS-ssh-output` |
| Teardown | on the pool when triggered from a connection callback, otherwise inline |

## Steps

1. **Keep the constructor trivial.** The registry instantiates a probe and disposes it, so the session only stores the broker:

   ```csharp
   // gatOS.Ssh/SshShellSession.cs L42-L48
       /// <summary>Creates an idle session on the shared broker. Trivial by contract (T0.5).</summary>
       public SshShellSession(VmConnectionBroker broker)
           : this((IShellBroker)broker)
       {
       }

       internal SshShellSession(IShellBroker broker) => _broker = broker;
   ```

2. **Register with purrTTY and detect a missing host.** The registration passes a factory; gatOS then checks where the contract assembly was loaded from. If it came from gatOS's own folder, purrTTY is absent and nobody consumes the registry:

   ```csharp
   // gatOS.GameMod/Mod.cs L989-L1024
       private static void RegisterShell(VmConnectionBroker broker)
       {
           try
           {
               // The registry probe-instantiates (and disposes) one session to validate Metadata —
               // SshShellSession's ctor is trivial by contract (T0.5), so this never touches the VM.
               CustomShellRegistry.Instance.RegisterShell(ShellId, () => new SshShellSession(broker));
           }
           catch (Exception ex)
           {
               // Duplicate id or probe failure — only the terminal UI is lost; VM management stays up.
               ModLog.Log.Error($"Could not register the '{ShellId}' shell: {ex.Message}", ex);
               return;
           }

           // D6: with purrTTY installed, the contract assembly resolved from purrTTY's ALC and the
           // registration above landed in the registry purrTTY's menus enumerate. If it loaded from
           // our own mod folder instead, purrTTY is absent and nobody consumes the registry.
           var contractDir = SafeAssemblyDir(typeof(CustomShellRegistry));
           var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
           var ownCopy = contractDir is not null && GatOsPaths.ModDir is { } modDir
                         && string.Equals(
                             Path.TrimEndingDirectorySeparator(contractDir),
                             Path.TrimEndingDirectorySeparator(modDir),
                             comparison);
           if (ownCopy)
           {
               ModLog.Log.Warn(
                   "purrTTY not detected: terminal UI unavailable. Install purrTTY to open gatOS "
                   + "sessions (the VM can still be managed from the gatOS menu).");
           }
           else
           {
               ModLog.Log.Info($"Registered shell '{ShellId}' with purrTTY (contract: {contractDir ?? "unknown"}).");
           }
       }
   ```

3. **Share the contract assembly through StarMap.** gatOS declares purrTTY as an optional dependency and imports the two contract assemblies, so both mods see one `CustomShellRegistry.Instance`; the vendored copies in the mod folder load only when purrTTY is missing:

   ```toml
   # gatOS.GameMod/mod.toml L9-L17
   # D6: with purrTTY installed, StarMap resolves these two assemblies from purrTTY's ALC
   # (it exports them via ExportedAssemblies) — one Assembly, one Type identity, one shared
   # CustomShellRegistry.Instance that purrTTY's menus read. Optional: without purrTTY the
   # vendored copies in this mod folder load instead and gatOS idles (registration lands in
   # a registry nobody consumes; the VM stays manageable from the gatOS menu).
   [[StarMap.ModDependencies]]
   ModId = "purrTTY"
   Optional = true
   ImportedAssemblies = ["purrTTY.CustomShellContract", "purrTTY.Logging"]
   ```

   Only the importer's `ImportedAssemblies` list controls what StarMap shares at this pin. The `ExportedAssemblies` line in purrTTY's own `mod.toml` (the comment above calls it the export) is parsed into `StarMapConfig` but never copied into `RuntimeMod.ExportedAssemblies`, so it has no effect: sharing works because gatOS imports the two names ([StarMap.Core/ModRepository/RuntimeMod.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/RuntimeMod.cs#L109-L126), [StarMap.Core/ModRepository/ModAssemblyLoadContext.cs](https://github.com/StarMapLoader/StarMap/blob/4dc60f8dfeb1d9b035d44af2601d09115c16d2aa/StarMap.Core/ModRepository/ModAssemblyLoadContext.cs#L49-L63)). Keep the import list exact and do not rely on the exporter's list.

   See [Share types between mods](../../../getting-started/dependencies/share-types-between-mods/overview.md) for the mechanism and its caveats.

4. **Boot lazily and connect with host-key pinning.** Each session asks the broker for its own `SshClient`; the broker starts the VM if needed, then connects with a key and retries once if sshd refuses right after readiness. Host-key verification against the guest manifest is covered in [pin the guest SSH host key](../../processes/host-guest-transports-tcp-9p-and-ssh/host-guest-transports-tcp-9p-and-ssh-4.md):

   ```csharp
   // gatOS.Ssh/VmConnectionBroker.cs L41-L56
       public async Task<SshClient> ConnectAsync(CancellationToken ct)
       {
           var endpoints = await vmHost.EnsureStartedAsync(ct);
           try
           {
               return await ConnectOnceAsync(endpoints, ct);
           }
           catch (Exception ex) when (IsConnectionRefused(ex))
           {
               // The readiness probe saw the SSH banner, but sshd can drop one connection
               // while it forks per-client — a single short retry covers it.
               ModLog.Log.Warn("SSH connection refused right after readiness; retrying once.");
               await Task.Delay(RefusedRetryDelay, ct);
               return await ConnectOnceAsync(endpoints, ct);
           }
       }
   ```

5. **Open the pty and publish the session safely.** `StartAsync` opens an `xterm-256color` shell sized to the terminal, turns every failure into a `CustomShellStartException` with a user-facing message, hooks the channel events before publishing the session, and backs out if the tab was closed or the channel died meanwhile:

   ```csharp
   // gatOS.Ssh/SshShellSession.cs L76-L102, L104-L114
           IShellChannel channel;
           try
           {
               // Boots the VM lazily — may take seconds; purrTTY tolerates slow shell starts.
               channel = await _broker.OpenShellAsync(TerminalType, columns, rows, cancellationToken);
           }
           catch (OperationCanceledException)
           {
               lock (_lock)
               {
                   _startRequested = false;
               }

               throw;
           }
           catch (Exception ex)
           {
               lock (_lock)
               {
                   _startRequested = false;
               }

               var userMessage = ex is VmStartException vmEx
                   ? vmEx.UserMessage
                   : $"Could not open an SSH session into the gatOS VM: {ex.Message}";
               throw new CustomShellStartException(userMessage, ex, ShellId);
           }
   // ...
           var input = new ShellInputQueue(
               "gatOS-ssh-input",
               channel.Write,
               message => ModLog.Log.Warn(message),
               ex => Terminate(1, $"SSH input write failed: {ex.Message}", deferDisposal: false));

           // Hook before publishing so a channel that dies instantly is not missed; a resulting
           // early Terminate() marks the session terminated and the publish below backs out.
           channel.DataReceived += OnChannelData;
           channel.ErrorOccurred += OnChannelError;
           channel.Closed += OnChannelClosed;
   ```

6. **Never block the tick on an SSH write.** `WriteInputAsync` enqueues into a bounded queue drained by one dedicated writer thread; an enqueue never blocks, overflow drops the chunk and reports once per episode, and the first write failure terminates the session. This is purrTTY's `PtyInputQueue` discipline applied to SSH:

   ```csharp
   // gatOS.Ssh/ShellInputQueue.cs L52-L82
       /// <summary>Queues a copy of <paramref name="data"/> for the writer thread. Never blocks.</summary>
       internal void Write(ReadOnlySpan<byte> data)
       {
           if (data.IsEmpty || _failed)
               return;

           if (Volatile.Read(ref _pendingBytes) + data.Length > MaxPendingBytes)
           {
               if (!_overflowReported)
               {
                   _overflowReported = true;
                   _onOverflow($"SSH input queue overflow (> {MaxPendingBytes} bytes pending); "
                               + "dropping input — the guest is not reading");
               }

               return;
           }

           _overflowReported = false;
           Interlocked.Add(ref _pendingBytes, data.Length);
           try
           {
               _queue.Add(data.ToArray());
           }
           catch (InvalidOperationException)
           {
               // CompleteAdding raced this enqueue during teardown — the session is going
               // away, dropping the chunk is fine.
               Interlocked.Add(ref _pendingBytes, -data.Length);
           }
       }
   ```

   [gatOS.Ssh/SshShellSession.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Ssh/SshShellSession.cs#L157-L171) shows the caller side.

7. **Pump output on your own thread.** SSH.NET's `ShellStream` buffers inbound data whether or not an event handler is attached, so an event-only consumer leaks at wire rate. A dedicated reader thread drains it, and a slow consumer (the terminal inbox's backpressure) parks this thread instead of SSH.NET's message loop. The event hands out a view into the reused buffer, valid only for the duration of the call, which is safe because `Surface.Write` copies before returning:

   ```csharp
   // gatOS.Ssh/SshShellChannel.cs L96-L115
       private void PumpLoop()
       {
           var buffer = new byte[PumpBufferBytes];
           try
           {
               while (true)
               {
                   var read = _stream.Read(buffer, 0, buffer.Length);
                   if (read <= 0)
                       return; // stream disposed/closed and drained; Closed/ErrorOccurred drive teardown
                   DataReceived?.Invoke(this, buffer.AsMemory(0, read));
               }
           }
           catch (Exception ex)
           {
               // Read paths only throw for connection-level failures (a plain dispose returns 0).
               if (Volatile.Read(ref _disposed) == 0)
                   ErrorOccurred?.Invoke(this, ex);
           }
       }
   ```

   [gatOS.Ssh/SshShellSession.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Ssh/SshShellSession.cs#L276-L281) documents the lifetime contract.

8. **Terminate once, tear down off the callback.** Disposing SSH.NET objects from inside their own events risks re-entrancy, so teardown that starts in a connection callback moves to the pool; teardown triggered by the tab closing runs inline and joins the writer thread for at most two seconds. Closing a tab never stops the VM, because other tabs share it:

   ```csharp
   // gatOS.Ssh/SshShellSession.cs L220-L262
       private void Terminate(int exitCode, string? reason, bool deferDisposal)
       {
           IShellChannel? channel;
           ShellInputQueue? input;
           lock (_lock)
           {
               if (_terminated || !_startRequested)
                   return;
               _terminated = true;
               channel = _channel;
               input = _input;
               _channel = null;
               _input = null;
               _isRunning = false;
           }

           _broker.VmStatusChanged -= OnVmStatusChanged;
           if (channel is not null)
           {
               channel.DataReceived -= OnChannelData;
               channel.ErrorOccurred -= OnChannelError;
               channel.Closed -= OnChannelClosed;
           }

           if (channel is not null || input is not null)
           {
               if (deferDisposal)
               {
                   _ = Task.Run(() =>
                   {
                       input?.Dispose();
                       channel?.Dispose();
                   });
               }
               else
               {
                   input?.Dispose(); // joins the writer thread ≤2 s (drains pending input first)
                   channel?.Dispose();
               }
           }

           Terminated?.Invoke(this, new ShellTerminatedEventArgs(exitCode, reason));
       }
   ```

## Complete example

| File | Role |
|---|---|
| [gatOS.Ssh/SshShellSession.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Ssh/SshShellSession.cs) | `ICustomShell` implementation, state machine, events |
| [gatOS.Ssh/VmConnectionBroker.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Ssh/VmConnectionBroker.cs) | Lazy boot, connect with retry, host-key pin, pty open |
| [gatOS.Ssh/SshShellChannel.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Ssh/SshShellChannel.cs) | `ShellStream` wrapper with the output pump |
| [gatOS.Ssh/ShellInputQueue.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Ssh/ShellInputQueue.cs) | Bounded queue and writer thread |
| [gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L989-L1024) | Registration and own-copy detection |
| [gatOS.GameMod/mod.toml](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/mod.toml) | Optional dependency and imported assemblies |

## Variations and gotchas

- **Never perform a potentially blocking network write on a caller inside the game frame.**
- **A static committed key is acceptable only because the forward binds loopback.** The pin protects against a mismatched guest image, not against a network attacker.
- **Pin shared dependencies.** SSH.NET pulls `Microsoft.Extensions.Logging.Abstractions`; gatOS references it directly in the project to avoid a version conflict with the game's assemblies (repository notes).
- **Without purrTTY the registration lands in a registry nobody reads**; the VM stays manageable from gatOS's own menu.
- **Large streams stress this path.** The screen stream in [Stream the game screen to a terminal](../stream-the-game-screen-to-a-terminal/overview.md) reaches tens of MB/s through this exact output pump and the surface inbox.

## See also

- [Overview](overview.md); previous: [the game-console shell](host-a-terminal-with-pty-sessions-and-frame-handoff-3.md)
- [SSH shell sessions into the guest](../../../../mods/gatos/features/ssh-shell-session-into-guest.md), [purrTTY custom shell contract](../../../../mods/gatos/features/purrtty-custom-shell-contract.md)
- [Connect a VM guest with slirp TCP, a 9P filesystem and pinned SSH](../../processes/host-guest-transports-tcp-9p-and-ssh/overview.md)
- [Host and supervise an external process or virtual machine](../../processes/host-and-supervise-an-external-process-from-a-mod/overview.md)
