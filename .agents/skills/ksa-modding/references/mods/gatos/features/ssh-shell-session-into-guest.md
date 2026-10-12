---
type: Mod Feature
title: SSH shell sessions into the guest
description: Each purrTTY terminal tab is an SSH.NET PTY channel into the guest over a loopback port forward, with lazy VM boot on first open, host-key pinning, a non-blocking input writer thread and thread-agnostic output events
mod: gatos
category: external-io
techniques: [background-thread, inter-mod-api]
hooks: [thread:background SshShellSession.StartAsync]
lifecycle: [thread:background]
game_types: []
tags: [ssh, terminal, pty, purrtty, threading, security]
---

The player types into a purrTTY terminal; the bytes need to reach a shell in the guest and the
output needs to come back, without the terminal's render tick ever blocking on the network.
`gatOS.Ssh` implements purrTTY's `ICustomShell` as an SSH session: starting a tab boots the VM if
needed, connects with a key-only login, opens an `xterm-256color` PTY sized to the terminal, and
then shuttles bytes. Many tabs share one VM; closing a tab never stops it.

## How it works

- **Trivial constructor.** purrTTY's registry instantiates a probe instance at registration to
  validate `Metadata` and disposes it, so all real work is in `StartAsync`
  ([gatOS.Ssh/SshShellSession.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Ssh/SshShellSession.cs#L63-L147)).
- **Lazy boot and connect.** `VmConnectionBroker.ConnectAsync` awaits
  `VmHost.EnsureStartedAsync`, then connects SSH.NET to `127.0.0.1:<hostfwd port>` with
  `PrivateKeyAuthenticationMethod` and the guest's committed session key; a single retry covers
  sshd refusing one connection right after the readiness probe
  ([gatOS.Ssh/VmConnectionBroker.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Ssh/VmConnectionBroker.cs#L41-L74)).
- **Host-key pinning.** `HostKeyReceived` hashes the presented key with SHA-256 and sets
  `CanTrust` only when it equals the fingerprint in the installed guest manifest; a mismatch
  becomes a `HostKeyMismatchException` with a "re-fetch or rebuild the guest artifacts" message
  ([gatOS.Ssh/VmConnectionBroker.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Ssh/VmConnectionBroker.cs#L79-L113)).
- **PTY.** `client.CreateShellStream("xterm-256color", columns, rows, 0, 0, 8192)`; a resize that
  arrives before the channel opens is remembered and applied after.
- **Input.** `WriteInputAsync` is called from purrTTY's render tick, and an SSH write can block
  when the channel window is full, so input goes through `ShellInputQueue`: a bounded queue
  drained by one dedicated background thread. Enqueue never blocks; overflow drops the chunk and
  logs once per episode ([gatOS.Ssh/ShellInputQueue.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Ssh/ShellInputQueue.cs#L14-L53)).
- **Output.** `OutputReceived` fires on SSH.NET's threads; purrTTY's surface write is its one
  thread-safe entry point, so no marshalling to the main thread is needed.
- **Errors.** Boot failures surface as `CustomShellStartException` carrying the VM's user-facing
  message, which purrTTY shows in the tab.

## Lifecycle and timing

- Registration happens once at `[StarMapAllModsLoaded]` (see
  [purrTTY custom shell contract](purrtty-custom-shell-contract.md)).
- `StartAsync`, connection, input writing and output callbacks all run off the main thread
  (`thread:background`): thread-pool tasks, a named writer thread, and SSH.NET's receive threads.
- The game thread is never involved; telemetry reaches the shell only through `/sim` inside the guest.

## Key code

| Path | Role |
|---|---|
| [gatOS.Ssh/SshShellSession.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Ssh/SshShellSession.cs) | `ICustomShell` implementation, state machine, events |
| [gatOS.Ssh/VmConnectionBroker.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Ssh/VmConnectionBroker.cs) | lazy boot, connect with retry, host-key pin, PTY open |
| [gatOS.Ssh/SshShellChannel.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Ssh/SshShellChannel.cs) | SSH.NET `ShellStream` wrapper |
| [gatOS.Ssh/ShellInputQueue.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Ssh/ShellInputQueue.cs) | bounded queue and writer thread |
| [gatOS.Ssh/IShellChannel.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Ssh/IShellChannel.cs) | channel seam used by tests |

## Snippets

```csharp
// gatOS.Ssh/VmConnectionBroker.cs L79-L101
    internal static async Task<SshClient> ConnectOnceAsync(VmEndpoints endpoints, CancellationToken ct)
    {
        var auth = new PrivateKeyAuthenticationMethod(
            endpoints.SshUser, new PrivateKeyFile(endpoints.PrivateKeyPath));
        var info = new ConnectionInfo("127.0.0.1", endpoints.SshPort, endpoints.SshUser, auth)
        {
            Timeout = ConnectTimeout,
        };

        var client = new SshClient(info);
        string? mismatch = null;
        client.HostKeyReceived += (_, e) =>
        {
            var actual = Convert.ToHexStringLower(SHA256.HashData(e.HostKey));
            e.CanTrust = actual == endpoints.HostKeySha256;
            if (!e.CanTrust)
                mismatch = actual;
        };

        try
        {
            await client.ConnectAsync(ct);
            return client;
```

## Game APIs touched

None. Only the purrTTY contract types (`ICustomShell`, `CustomShellMetadata`,
`CustomShellStartOptions`, `CustomShellStartException`) and SSH.NET are referenced.

## Pitfalls and version notes

- Never perform a potentially blocking network write on a caller that runs inside the game frame.
- A static committed key is acceptable here only because the port forward binds `127.0.0.1`;
  the pin protects against a mismatched guest image rather than a network attacker.
- SSH.NET pulls `Microsoft.Extensions.Logging.Abstractions`; gatOS pins it directly in the
  project to avoid a version conflict with the game's 10.x assemblies (repo AGENTS.md).

## See also

- [purrTTY custom shell contract](purrtty-custom-shell-contract.md)
- [QEMU microVM as a game child process](qemu-microvm-host-process.md)
- [Alpine guest image pipeline](alpine-guest-image.md)
- [Screen stream frame capture](screen-stream-frame-capture.md)
- [gatOS overview](../overview.md)
- [Pin the guest SSH host key to a manifest value](../../../howto/external-io/processes/host-guest-transports-tcp-9p-and-ssh/host-guest-transports-tcp-9p-and-ssh-4.md) - how-to article demonstrating this feature
- [SSH PTY session with lazy VM boot, bounded input writer thread and thread-agnostic output](../../../howto/external-io/terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/host-a-terminal-with-pty-sessions-and-frame-handoff-4.md) - how-to article demonstrating this feature

