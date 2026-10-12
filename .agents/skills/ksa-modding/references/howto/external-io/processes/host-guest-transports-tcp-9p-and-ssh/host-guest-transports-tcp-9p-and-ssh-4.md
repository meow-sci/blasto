---
type: How-To
title: Pin the guest SSH host key to a manifest value
description: Open SSH.NET sessions into the guest only when the presented host key's SHA-256 equals the fingerprint in the installed guest manifest, with lazy boot, a single retry and a bounded input writer thread
category: external-io
group: processes
topic: host-guest-transports-tcp-9p-and-ssh
approach: 4
difficulty: intermediate
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/ssh-shell-session-into-guest, mods/gatos/features/alpine-guest-image]
techniques: [background-thread]
hooks: []
lifecycle: [thread:background]
game_types: []
tags: [ssh, host-key, pinning, sshnet, manifest, shell-stream]
---

Outcome: the player types into a terminal tab; the bytes reach a shell in the guest and the output comes back, without the terminal's render tick ever blocking on the network. The SSH session boots the VM if needed, connects with a key-only login, refuses any host key that is not the one the guest manifest pins, and opens an `xterm-256color` PTY sized to the terminal. Demonstrated by [SSH shell sessions into the guest](../../../../mods/gatos/features/ssh-shell-session-into-guest.md) and [Alpine guest image pipeline](../../../../mods/gatos/features/alpine-guest-image.md) in gatOS. The terminal side of the contract is in [Host a terminal emulator in the game](../../terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/overview.md).

## Background: how the game does it

KSA is not involved. The trust problem is a mod-level one: the guest image is built elsewhere and installed per version; the host should refuse to talk to a guest that is not the image its manifest describes. The guest image is built so its SSH host key never changes: the host key and the session key pair are committed under `guest/keys/` and baked into every guest version, so the fingerprint pinned in `manifest.toml` is a constant ([guest/README.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/README.md#L17-L30)). Access is only ever through the loopback port forward, so the pin protects against a mismatched image rather than a network attacker.

## Lifecycle and timing

- Registration of the shell happens once at `[StarMapAllModsLoaded]`.
- Connection, input writing and output callbacks all run off the main thread (`thread:background`): thread-pool tasks, a named writer thread and SSH.NET's receive threads. The game thread is never involved; telemetry reaches the shell only through `/sim` inside the guest.

## Steps

1. **Carry the pin in the endpoints.** After boot the VM host returns the SSH port, user, private key path and the manifest's host-key SHA-256, read from the *installed* manifest (not the shipped one), so a re-keyed rebuild of the same guest version cannot break an existing install ([gatOS.Vm/VmHost.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/VmHost.cs#L257-L263)).

2. **Build the guest manifest value in the image pipeline.** The build script hashes the raw `ssh-ed25519` host public key blob and writes the lowercase hex into `manifest.toml` as `host_key_sha256`; it then boots the image and checks that the fingerprint matches ([guest/build-image.sh](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/build-image.sh#L219-L225), [guest/build-image.sh](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/build-image.sh#L385-L394)).

3. **Compare in `HostKeyReceived`, not trust-on-first-use.** Hash the presented key with SHA-256 and set `CanTrust` only when it equals the pin. A mismatch becomes a typed exception with a message that says what to do:

   ```csharp
   // gatOS.Ssh/VmConnectionBroker.cs L79-L116
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
           }
           catch (Exception ex)
           {
               client.Dispose();
               if (mismatch is not null)
               {
                   throw new HostKeyMismatchException(
                       $"The gatOS guest presented an unexpected SSH host key (sha256 {mismatch}, "
                       + $"expected {endpoints.HostKeySha256}). The guest image and its manifest are "
                       + "out of sync — re-fetch or rebuild the guest artifacts.", ex);
               }

               throw;
           }
       }
   ```

4. **Boot lazily and retry once.** The broker awaits `EnsureStartedAsync`, then connects. The readiness probe saw the SSH banner, but sshd can drop one connection while it forks per client, so a single short retry covers a refused connection:

   ```csharp
   // gatOS.Ssh/VmConnectionBroker.cs L41-L73
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

       async Task<IShellChannel> IShellBroker.OpenShellAsync(
           string terminal, int columns, int rows, CancellationToken ct)
       {
           var client = await ConnectAsync(ct);
           try
           {
               var stream = client.CreateShellStream(
                   terminal, (uint)columns, (uint)rows, width: 0, height: 0, bufferSize: 8192);
               return new SshShellChannel(client, stream);
           }
           catch
           {
               client.Dispose();
               throw;
           }
       }
   ```

5. **Never write to the channel from the render tick.** `WriteInputAsync` is called from the terminal's tick and an SSH write can block when the channel window is full, so input goes through `ShellInputQueue`: a bounded queue drained by one dedicated background thread. Enqueue never blocks; overflow drops the chunk and logs once per episode ([gatOS.Ssh/ShellInputQueue.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Ssh/ShellInputQueue.cs#L14-L53)).

6. **Receive on the SSH library's threads.** `OutputReceived` fires on SSH.NET's threads; the terminal surface's write is its one thread-safe entry point, so no marshalling to the main thread is needed. Boot failures surface as a start exception carrying the VM's user-facing message, which the terminal shows in the tab ([gatOS.Ssh/SshShellSession.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Ssh/SshShellSession.cs#L63-L147)).

## Complete example

| File | Role |
|---|---|
| [gatOS.Ssh/VmConnectionBroker.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Ssh/VmConnectionBroker.cs) | Lazy boot, connect with retry, host-key pin, PTY open |
| [gatOS.Ssh/SshShellSession.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Ssh/SshShellSession.cs) | Custom shell implementation, state machine, events |
| [gatOS.Ssh/ShellInputQueue.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Ssh/ShellInputQueue.cs) | Bounded queue and writer thread |
| [gatOS.Ssh/SshShellChannel.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Ssh/SshShellChannel.cs) | SSH.NET shell stream wrapper |
| [gatOS.Vm/GuestManifest.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Vm/GuestManifest.cs) | The boot contract including `HostKeySha256` |

## Variations and gotchas

- **A static committed key is acceptable only because the forward binds `127.0.0.1`** and root login is key-only.
- **Never perform a potentially blocking network write on a caller that runs inside the game frame.**
- **SSH.NET pulls `Microsoft.Extensions.Logging.Abstractions`;** gatOS pins it directly to avoid a version conflict with the game's own assemblies (see [Ship third-party DLLs with a mod](../../../getting-started/dependencies/ship-third-party-dlls-with-a-mod/overview.md)).
- **A mismatch usually means the base image was installed from an older build;** re-fetching or rebuilding the guest artifacts fixes it.

## See also

- [Overview](overview.md)
- [Approach 3: file models](host-guest-transports-tcp-9p-and-ssh-3.md)
- [SSH shell sessions into the guest](../../../../mods/gatos/features/ssh-shell-session-into-guest.md), [purrTTY custom shell contract](../../../../mods/gatos/features/purrtty-custom-shell-contract.md)
- [Host and supervise an external process or virtual machine from a KSA mod](../host-and-supervise-an-external-process-from-a-mod/overview.md)
- [Host a terminal emulator in the game with pty sessions and frame handoff](../../terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/overview.md)
