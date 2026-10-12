---
type: How-To
title: From-scratch 9P2000.L server with a VfsNode tree and HostDirectory confinement
description: Serve a synthetic filesystem and confined host folders to a Linux guest by implementing the 9P2000.L protocol over TCP, with one session per connection, pipelined parked reads and flush handling
category: external-io
group: processes
topic: host-guest-transports-tcp-9p-and-ssh
approach: 2
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/ninep-server-in-csharp]
techniques: [protocol-server, background-thread]
hooks: [starmap StarMapAllModsLoaded, starmap StarMapUnload]
lifecycle: [startup:all-mods-loaded, thread:background, event:unload]
game_types: []
tags: [9p, 9p2000.l, filesystem, vfs, confinement, tcp, session]
---

Outcome: `/sim` is not a directory on disk. It is a tree of C# objects served over the Linux kernel's own 9p client, so `cat`, `watch`, `tail -f` and `jq` become the game API without any custom guest binary. `gatOS.NineP` implements the 9P2000.L wire protocol, a session per TCP connection, and a small VFS abstraction whose nodes decide their own read and write semantics. A second instance serves host folders to the guest under a confinement rule. The library has no game dependency and is tested with a managed 9p client and against a real guest. Demonstrated by [9P2000.L server in C#](../../../../mods/gatos/features/ninep-server-in-csharp.md) in gatOS.

## Background: how the game does it

Nothing in KSA is involved. The server sits behind the immutable snapshot store and the command queue ([snapshots](../../telemetry/export-game-state-to-other-threads/overview.md), [command queue](../../control/marshal-commands-to-the-game-thread/overview.md)); transport reaches the guest through the slirp channel of [approach 1](host-guest-transports-tcp-9p-and-ssh-1.md).

## Lifecycle and timing

- `[StarMapAllModsLoaded]`: `StartSimServer(port: 0)` binds an ephemeral port synchronously before the VM exists, so the port can go on the guest kernel command line. The menu's "Restart SimFs" rebuilds the server on a background task and swaps a volatile reference.
- All protocol work runs on thread-pool tasks (`thread:background`). Reads come from the volatile `SnapshotStore.Current`; writes only enqueue a command and await it.
- At `[StarMapUnload]` the server is disposed after the VM, with a 5 s bounded wait ([gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L484-L544)).

## Steps

1. **Listen on loopback and run an accept loop on the thread pool.** One `Session` per connection, tracked in a `ConcurrentDictionary`; `ActiveSessions` feeds the sampler's "is anybody listening" gate:

   ```csharp
   // gatOS.NineP/Server/NinePServer.cs L51-L61, L76-L113
       public Task StartAsync(int port = 0)
       {
           if (_listener is not null)
               throw new InvalidOperationException("The server is already started.");
           _listener = new TcpListener(IPAddress.Loopback, port);
           _listener.Start();
           Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
           ModLog.Log.Info($"9p server listening on 127.0.0.1:{Port}");
           _acceptLoop = Task.Run(AcceptLoopAsync);
           return Task.CompletedTask;
       }
       // ...
       private async Task AcceptLoopAsync()
       {
           var listener = _listener!;
           var ct = _cts.Token;
           while (!ct.IsCancellationRequested)
           {
               TcpClient client;
               try
               {
                   client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
               }
               catch (OperationCanceledException)
               {
                   break;
               }
               catch (SocketException) when (ct.IsCancellationRequested)
               {
                   break;
               }

               client.NoDelay = true;
               var session = new Session(client, _root, _options, _attrTime, Stats, ct);
               _sessions.TryAdd(session, 0);
               _ = Task.Run(async () =>
               {
                   ModLog.Log.Debug("9p server: connection accepted");
                   try
                   {
                       await session.RunAsync().ConfigureAwait(false);
                   }
                   finally
                   {
                       _sessions.TryRemove(session, out _);
                       ModLog.Log.Debug("9p server: connection closed");
                   }
               }, CancellationToken.None);
           }
       }
   ```

2. **Read frames into pooled buffers; run metadata inline, park the rest.** A video-rate mount moves hundreds of messages a second, so frame bodies come from `ArrayPool<byte>`. Walk, getattr, open, readdir and clunk cannot block and run inline on the read loop; `Tread`, `Twrite` and `Tflush` are dispatched as their own tasks because a read may park waiting for the next snapshot and a control write awaits the game thread:

   ```csharp
   // gatOS.NineP/Server/Session.cs L76-L110
               var header = new byte[4];
               while (!ct.IsCancellationRequested)
               {
                   await _stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
                   var size = BinaryPrimitives.ReadUInt32LittleEndian(header);
                   if (size is < HeaderBytes or > MaxFrameBytes)
                       throw new ProtocolException($"frame size {size} outside [{HeaderBytes}, {MaxFrameBytes}]");

                   // The frame body is rented (P4): a video-rate mount moves hundreds of messages a
                   // second and a fresh array per message was steady garbage. Ownership passes to the
                   // Dispatch task, whose finally returns it once the handler completes.
                   var frameLength = (int)size - 4;
                   var frame = ArrayPool<byte>.Shared.Rent(frameLength);
                   try
                   {
                       await _stream.ReadExactlyAsync(frame.AsMemory(0, frameLength), ct).ConfigureAwait(false);
                       var type = (MessageType)frame[0];
                       var tag = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1, 2));
                       if (CanRunInline(type))
                       {
                           // Fast path (GP4): metadata ops (walk/getattr/open/readdir/clunk/…) cannot
                           // park, so they run inline on the read loop — no Task.Run hop, no linked
                           // CTS, no in-flight registry entry (they are atomic wrt this loop, so a
                           // Tflush can only ever arrive after they finished — flush of a completed
                           // tag correctly answers a bare Rflush). Tread/Twrite/Tflush can block
                           // (parked stream reads, control writes awaiting the game thread, flush
                           // awaiting its victim) and keep the concurrent dispatch path.
                           await HandleAsync(type, tag, frame.AsMemory(3, frameLength - 3), inFlight: null,
                               _cts.Token).ConfigureAwait(false);
                           ArrayPool<byte>.Shared.Return(frame);
                       }
                       else
                       {
                           Dispatch(type, tag, frame, frameLength);
                       }
   ```

   Keep metadata handlers non-blocking or the inline fast path stalls the whole session ([gatOS.NineP/Server/Session.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.NineP/Server/Session.cs#L165-L166)).

3. **Negotiate version and message size.** `Tversion` resets the session (abandons all fids and in-flight requests), takes the minimum of the client's `msize` and the server ceiling (512 KiB) and accepts only `9P2000.L`:

   ```csharp
   // gatOS.NineP/Server/Session.cs L295-L323
           var reader = new NinePReader(body.Span);
           var clientMsize = reader.ReadUInt32();
           var version = reader.ReadString();
           if (clientMsize < 4096)
               throw new ProtocolException($"client msize {clientMsize} is unusably small");

           // Tversion resets the session: abandon all fids and in-flight requests (T7.3).
           foreach (var fid in _fids.Keys.ToArray())
               if (_fids.TryRemove(fid, out var entry))
                   entry.DisposeHandle();
           foreach (var (otherTag, other) in _inFlight)
           {
               if (otherTag == tag)
                   continue;
               try
               {
                   other.Cts.Cancel();
               }
               catch (ObjectDisposedException)
               {
               }
           }

           _msize = Math.Min(clientMsize, _options.MaxMsize);
           var accepted = version == "9P2000.L";
           return new NinePWriter().Begin(MessageType.Rversion, tag)
               .WriteUInt32(_msize)
               .WriteString(accepted ? "9P2000.L" : "unknown");
       }
   ```

4. **Serialize replies and honour flush.** Replies are serialized by a `SemaphoreSlim` write lock, and a flushed request's tag is never answered after `Rflush`, which is what makes Ctrl-C on a blocked `cat` return instantly ([gatOS.NineP/Server/Session.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.NineP/Server/Session.cs#L253-L275)).

5. **Define a small VFS.** `VfsDirectory` (`List`, `Lookup`, optional create, unlink and rename) and `VfsFile` (`Size`, `Open()` returning an `IVfsFileHandle`, optional `OpenWrite()`, `IsStreaming`). `ReadAsync(offset, count, ct)` returns `ValueTask<ReadOnlyMemory<byte>>`, so a handle can return cached bytes synchronously or park asynchronously. A scan helper walks the same tree so HTTP and MQTT can mirror every leaf ([gatOS.NineP/Vfs/VfsNode.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.NineP/Vfs/VfsNode.cs), [gatOS.NineP/Vfs/IVfsFileHandle.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.NineP/Vfs/IVfsFileHandle.cs), [gatOS.NineP/Vfs/VfsScan.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.NineP/Vfs/VfsScan.cs)).

6. **Walk with partial success.** `Twalk` caps names at 16, handles `..` by popping the path (root stays root), and returns the qids resolved so far when a later name is missing:

   ```csharp
   // gatOS.NineP/Server/Session.cs L338-L352, L363-L380
       private NinePWriter HandleWalk(ReadOnlyMemory<byte> body, ushort tag)
       {
           var reader = new NinePReader(body.Span);
           var fid = reader.ReadUInt32();
           var newfid = reader.ReadUInt32();
           var count = reader.ReadUInt16();
           if (count > 16)
               throw new VfsErrorException(LinuxErrno.EINVAL, $"walk: {count} names exceeds the limit of 16");
           var names = new string[count];
           for (var i = 0; i < count; i++)
               names[i] = reader.ReadString();

           var entry = RequireFid(fid);
           var path = new List<VfsNode>(entry.Path);
           var qids = new List<Qid>(count);
       // ...
               {
                   try
                   {
                       next = dir.Lookup(name);
                   }
                   catch (VfsErrorException) when (qids.Count > 0)
                   {
                       break; // partial success
                   }

                   if (next is null)
                   {
                       if (qids.Count == 0)
                           throw new VfsErrorException(LinuxErrno.ENOENT, $"walk: '{name}' not found");
                       break; // partial success
                   }

                   path.Add(next);
   ```

   The elided lines are the `foreach` loop head, the `..` branch (pop the path, root stays root) and the type test that introduces `dir`.

## The confined host-folder passthrough

A second `NinePServer` serves a mount tree built from `[[mounts]]` entries in the mod config; the guest's `mnt-mount` supervisor mounts it at `/mnt` and each mount appears as `/mnt/<name>`. The server is only created when at least one mount is configured ([gatOS.GameMod/Mod.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.GameMod/Mod.cs#L772-L797)).

`HostDirectory` and `HostFile` map nodes to real files. Read-only mounts reject mutations with `EROFS`. Every child path is resolved with `Path.GetFullPath` and must stay under the mount root, so a guest cannot escape with `..` or a crafted single component. The walk handler already rejects `..`; this is defence in depth:

   ```csharp
   // gatOS.NineP/Vfs/HostDirectory.cs L200-L226
       /// <summary>
       ///     Combines <paramref name="name"/> onto this directory and verifies the result stays inside
       ///     the mount root (defence in depth against traversal — the walk handler already rejects
       ///     <c>..</c>, but a crafted single component must not escape either). Null = invalid.
       /// </summary>
       private string? TryResolveChild(string name)
       {
           if (!IsValidComponent(name))
               return null;
           string full;
           try
           {
               full = Path.GetFullPath(Path.Combine(_path, name));
           }
           catch
           {
               return null;
           }

           var root = Path.TrimEndingDirectorySeparator(_path) + Path.DirectorySeparatorChar;
           var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
           return full.StartsWith(root, comparison) ? full : null;
       }

       private static bool IsValidComponent(string name)
           => name.Length > 0 && name != "." && name != ".."
              && name.AsSpan().IndexOfAny('/', '\\') < 0 && !name.Contains('\0');
   ```

## Complete example

| File | Role |
|---|---|
| [gatOS.NineP/Server/NinePServer.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.NineP/Server/NinePServer.cs) | Listener, accept loop, session registry |
| [gatOS.NineP/Server/Session.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.NineP/Server/Session.cs) | Framing, inline vs dispatched handlers, flush, msize |
| [gatOS.NineP/Protocol/MessageType.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.NineP/Protocol/MessageType.cs) | 9P2000.L message ids |
| [gatOS.NineP/Protocol/LinuxErrno.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.NineP/Protocol/LinuxErrno.cs) | errno values returned in `Rlerror` |
| [gatOS.NineP/Vfs/HostDirectory.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.NineP/Vfs/HostDirectory.cs) | Confined host directory and file nodes |
| [gatOS.NineP/Vfs/HostMount.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.NineP/Vfs/HostMount.cs) | Host folder mount tree |

## Variations and gotchas

- **No kernel-side reconnect.** A dropped connection gives `EIO` until remount, which is why the guest supervisor loops on `mountpoint -q`.
- **No inotify.** v9fs delivers no inotify events for host-side changes; GNU `tail -f` needs the guest's poll-mode shim ([see the file-model article](host-guest-transports-tcp-9p-and-ssh-3.md)).
- **Kernel 6.11 and later** use netfslib read semantics, which dictate the file models; see approach 3.
- **Tag reuse is a protocol error.** A tag may not be reused while outstanding; the session treats it as a broken peer.
- **Test with a managed client** so protocol behaviour (flush ordering, partial walk) is checked without a guest.

## See also

- [Overview](overview.md)
- [Approach 1: slirp TCP](host-guest-transports-tcp-9p-and-ssh-1.md), [Approach 3: file models](host-guest-transports-tcp-9p-and-ssh-3.md)
- [9P2000.L server in C#](../../../../mods/gatos/features/ninep-server-in-csharp.md)
- [Host HTTP and MQTT servers in the game process](../../network/host-http-and-mqtt-servers-in-the-game-process/overview.md)
