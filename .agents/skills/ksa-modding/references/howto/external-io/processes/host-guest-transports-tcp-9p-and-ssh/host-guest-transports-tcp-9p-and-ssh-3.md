---
type: How-To
title: Sensor, StreamFile, EventsFile and CommandFile models under Linux netfslib rules
description: Choose the right read model for each synthetic file - snapshot-memoized sensors, growing logs, blocking event files, control files that return errno - so cat, watch and tail -f behave on a 9p mount in a modern Linux guest
category: external-io
group: processes
topic: host-guest-transports-tcp-9p-and-ssh
approach: 3
difficulty: advanced
prerequisites: [starmap]
demonstrated_by: [mods/gatos/features/sim-filesystem-file-models, mods/gatos/features/snapshot-diff-flight-events]
techniques: [virtual-filesystem, protocol-server]
hooks: []
lifecycle: [thread:background]
game_types: []
tags: [9p, netfslib, virtual-file, sizes, tail, blocking-read, errno]
---

Outcome: exposing game state as files only pays off if `cat`, `watch`, `tail -f`, shell redirection and exit codes behave the way Unix users expect. gatOS's spike against a real Linux 6.x guest found that the kernel's 9p client imposes strict rules and that a file must pick one of a few incompatible read models. This article lists the models and what each is used for. Builds on the server of [approach 2](host-guest-transports-tcp-9p-and-ssh-2.md). Demonstrated by [The /sim file models](../../../../mods/gatos/features/sim-filesystem-file-models.md) and [Snapshot-diff flight events](../../../../mods/gatos/features/snapshot-diff-flight-events.md) in gatOS.

## Background: how the game does it

KSA is not involved in the file semantics; the Linux guest's v9fs client is. On kernels 6.11 and later it uses netfslib, and the spike recorded the consequences ([spike/NOTES.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/spike/NOTES.md#L75-L106)):

1. The reported size must be truthful. A zero-byte reply inside the claimed size gives userspace `ENODATA`; a size of 0 makes the kernel return EOF without ever asking the server.
2. A `read()` completes only when the user buffer is full or after two consecutive zero-byte replies at the same offset.
3. Therefore a file is either a growing log or a blocking event source, never both.

## Lifecycle and timing

- All file I/O runs on 9p session tasks (`thread:background`). Sensor content comes from `SnapshotStore.Current`; blocking files await `SnapshotStore.WaitForNextAsync`, which completes when the game-thread sampler publishes.
- Control writes hold the `Twrite` open until the game thread drains the command (up to `command_timeout_ms`), so a script that writes then reads sees the effect, and a game thread that does not reach its drain (loading, a stalled frame) turns into `ETIMEDOUT` rather than a hang. A simulation pause does not stop the drain: `Program.OnFrame` keeps running while the sim is paused.
- The guest mounts with `cache=none`, so every `stat` and `open` reaches the server.

## Steps

1. **Pick a model per file.**

| Type | Used for | Read model |
|---|---|---|
| `SnapshotTextFile` | scalar sensors (`altitude/radar`, `orbit/apoapsis`) | one immutable byte image per open, formatted once per snapshot sequence and shared by all readers |
| `StreamFile` | `vessels/<id>/stream` NDJSON | growing log: never blocks, returns 0 at the frontier, size equals bytes produced; each handle owns a 256 KiB append buffer fed by a pump task per published snapshot |
| `EventsFile` | `/sim/events` | blocking event: size 1, a read parks until the next event, then two zero-byte replies complete the syscall |
| `AlarmFile` | `/sim/time/alarm` | write a target UT, a later read parks until sim time reaches it: the `/dev/rtc` of a time-warping ship |
| `CommandFile` subclasses | `ctl/*`, writable module files | read = current value; write = parse, submit, map result to errno |
| display stream | `/sim/display/stream` | continuous: huge size, never 0, so `cat` never EOFs |

2. **Sensor leaves: format once per snapshot.** The cache key is the snapshot sequence, captured before the provider runs: if a publish lands mid-format, the fresh content is tagged with the older sequence and the next access recomputes, so it is never stale forever. This is only correct for content that changes with a publish; live host state (transport ports) must use an uncached file type.

   ```csharp
   // gatOS.SimFs/SnapshotTextFile.cs L61-L80
       /// <inheritdoc />
       public override long Size => Content().Length;

       /// <inheritdoc />
       public override IVfsFileHandle Open() => new Handle(Content());

       private byte[] Content()
       {
           // Capture the sequence BEFORE running the provider: if a publish lands mid-format, the
           // fresh content gets tagged with the older sequence and the next access recomputes —
           // never stale-forever, at worst one redundant format.
           var sequence = _store.Current.Sequence;
           var cached = _cached;
           if (cached is not null && cached.Sequence == sequence)
               return cached.Utf8;

           var utf8 = _utf8Provider();
           _cached = new Cached(sequence, utf8);
           return utf8;
       }
   ```

3. **Growing logs: never block, return 0 at the frontier.** `tail -f` needs that 0 to finish its initial drain and enter follow mode; a frontier-blocking file makes it buffer forever. A reader that fell behind a trimmed buffer resumes at the oldest retained byte, and on overflow whole lines are dropped from the front with a notice line:

   ```csharp
   // gatOS.SimFs/StreamFile.cs L82-L110
           public long Size
           {
               get
               {
                   lock (_lock)
                   {
                       return _bufferStart + _bufferLength;
                   }
               }
           }

           public ValueTask<ReadOnlyMemory<byte>> ReadAsync(ulong offset, uint count, CancellationToken ct)
           {
               lock (_lock)
               {
                   var produced = _bufferStart + _bufferLength;
                   if ((long)offset >= produced)
                       return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty); // frontier: 0, never block

                   // A reader that fell behind a trimmed buffer resumes at the oldest retained
                   // byte (cat/tail never rewind; documented in OS_PLAN.md T8.3).
                   var effective = Math.Max((long)offset, _bufferStart);
                   var available = (int)(produced - effective);
                   var length = (int)Math.Min(count, (uint)available);
                   var copy = new byte[length];
                   _buffer.AsSpan((int)(effective - _bufferStart), length).CopyTo(copy);
                   return ValueTask.FromResult<ReadOnlyMemory<byte>>(copy);
               }
           }
   ```

   The pump appends one line per published snapshot until the handle is disposed (clunk), and the formatted line is shared by every handle on that vessel through the per-snapshot memo ([gatOS.SimFs/StreamFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/StreamFile.cs#L134-L152), [snapshot memoization](../../telemetry/export-game-state-to-other-threads/overview.md)).

4. **Blocking events: park, deliver, owe two zeros.** The file reports size 1 (not 0), starts at the sequence current at open so only later events are delivered, parks on `WaitForNextAsync`, writes the NDJSON lines and then answers the kernel's two continuation reads with zero bytes so `cat` prints the line at once and Ctrl-C lands as `Tflush` on the parked read. The event list itself is produced by the differ in [Detect flight events when the game has no event bus](../../telemetry/detect-flight-events-from-snapshots/overview.md); the handle is in [gatOS.SimFs/EventsFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/EventsFile.cs#L78-L96).

5. **Control files: a write is a command, a failure is an errno.** A write is buffered until the first newline, parsed by the subclass, submitted through the command sink and awaited. A parse failure throws `EINVAL`; a failed command throws the mapped errno, which the session returns as `Rlerror`, so `echo 2 > ctl/throttle` exits non-zero with "Invalid argument":

   ```csharp
   // gatOS.SimFs/Commands/CommandFile.cs L57-L68
       private async ValueTask ExecuteLineAsync(string line, CancellationToken ct)
       {
           var token = line.Trim();
           var command = Parse(token);
           if (command is null)
               throw new VfsErrorException(LinuxErrno.EINVAL, $"control '{Name}': cannot parse '{token}'");

           var result = await _sink.SubmitAsync(command, ct).ConfigureAwait(false);
           if (!result.IsSuccess)
               throw new VfsErrorException(result.ToErrno(),
                   $"control '{Name}': {result.Outcome} ({result.Message ?? "no detail"})");
       }
   ```

   A write without a trailing newline is executed best-effort at clunk without blocking the clunk reply. The queue and outcome mapping are in [Marshal commands from background threads to the game thread](../../control/marshal-commands-to-the-game-thread/overview.md).

6. **Fix the guest's `tail`.** GNU `tail -f` follows via inotify, which v9fs never fires for host-side appends. The guest ships a wrapper that adds `---disable-inotify` whenever follow mode is requested:

   ```
   // guest/rootfs-overlay/usr/local/bin/tail L16-L25
   [ -x /usr/bin/tail ] || exec busybox tail "$@"
   for arg in "$@"; do
       case "$arg" in
           --)                   break ;;                                   # end of options
           --follow|--follow=*)  exec /usr/bin/tail ---disable-inotify "$@" ;;
           --*)                  : ;;                                       # other long option
           -*[fF]*)              exec /usr/bin/tail ---disable-inotify "$@" ;;  # short cluster w/ -f/-F
       esac
   done
   exec /usr/bin/tail "$@"
   ```

## Complete example

| File | Role |
|---|---|
| [gatOS.SimFs/SimFsTree.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/SimFsTree.cs) | The whole `/sim` tree definition |
| [gatOS.SimFs/SnapshotTextFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/SnapshotTextFile.cs) | Snapshot-memoized sensor leaf |
| [gatOS.SimFs/StreamFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/StreamFile.cs) | Growing-log NDJSON stream |
| [gatOS.SimFs/EventsFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/EventsFile.cs) | Blocking-event file with zeros-owed bookkeeping |
| [gatOS.SimFs/AlarmFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/AlarmFile.cs) | Sim-time alarm |
| [gatOS.SimFs/Commands/CommandFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/CommandFile.cs) | Writable control base: parse, submit, errno |
| [gatOS.SimFs/Formats.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Formats.cs) | Frozen value formats |

## Variations and gotchas

- **Do not fake a large size for live files.** That old advice breaks reads on modern kernels.
- **A blocking file must answer the two continuation reads with zero bytes** or `cat` never prints.
- **Continuous files deliver data only when the reader's whole buffer fills;** low-rate consumers must read in small chunks (`dd bs=64`).
- **The file surface is a published API** with its own specification in the repository ([SPEC_9P_FILESYSTEM.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/SPEC_9P_FILESYSTEM.md#L1054-L1056)); keep it in lockstep with code.

## See also

- [Overview](overview.md)
- [Approach 2: 9P2000.L server](host-guest-transports-tcp-9p-and-ssh-2.md), [Approach 4: pinned SSH](host-guest-transports-tcp-9p-and-ssh-4.md)
- [The /sim file models](../../../../mods/gatos/features/sim-filesystem-file-models.md)
- [Detect flight events when the game has no event bus](../../telemetry/detect-flight-events-from-snapshots/overview.md)
- [Marshal commands from background threads to the game thread](../../control/marshal-commands-to-the-game-thread/overview.md)
