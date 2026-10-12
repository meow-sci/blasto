---
type: Mod Feature
title: The /sim file models
description: How a synthetic 9p filesystem must behave so ordinary Unix tools work on live game data, covering snapshot-memoized sensor files, growing-log streams, blocking event and alarm files, and writable control files that return real errno values
mod: gatos
category: external-io
techniques: [virtual-filesystem, command-queue, background-thread]
hooks: [thread:background Session.HandleReadAsync]
lifecycle: [thread:background]
game_types: []
tags: [9p, filesystem, linux, tail, ndjson, errno, control-files, netfslib]
---

Exposing game state as files only pays off if `cat`, `watch`, `tail -f`, shell redirection and
exit codes behave the way Unix users expect. gatOS's M1 spike against a real Linux 6.x guest found
that the kernel's 9p client imposes strict rules, and that a file must pick one of a few
incompatible read models. `SimFsTree` builds the whole `/sim` tree from these file types.

## How it works

**Kernel rules (Linux v9fs with netfslib, 6.11 and later)** from [spike/NOTES.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/spike/NOTES.md#L75-L106):

1. The reported size must be truthful. A zero-byte reply inside the claimed size gives userspace
   `ENODATA`; a size of 0 makes the kernel return EOF without ever asking the server.
2. A `read()` completes only when the user buffer is full or after two consecutive zero-byte
   replies at the same offset.
3. Therefore a file is either a growing log or a blocking event source, never both.

**File types:**

| Type | Used for | Read model |
|---|---|---|
| `SnapshotTextFile` | every scalar sensor (`altitude/radar`, `orbit/apoapsis`) | one immutable byte image per open, formatted once per snapshot sequence and shared by all readers ([gatOS.SimFs/SnapshotTextFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/SnapshotTextFile.cs)) |
| `StreamFile` | `vessels/<id>/stream` NDJSON | growing log: never blocks, returns 0 at the frontier, size = bytes produced; each handle owns a 256 KiB append buffer fed by a pump task per published snapshot ([gatOS.SimFs/StreamFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/StreamFile.cs#L19-L56)) |
| `EventsFile` | `/sim/events` | blocking event: size 1, a read parks until the next event, then two zero-byte replies complete the syscall ([gatOS.SimFs/EventsFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/EventsFile.cs)) |
| `AlarmFile` | `/sim/time/alarm` | write a target UT, a later read parks until sim time reaches it: the `/dev/rtc` of a time-warping ship ([gatOS.SimFs/AlarmFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/AlarmFile.cs)) |
| `CommandFile` subclasses | `ctl/*`, writable module files | read = current value; write = parse, submit, map result to errno |
| display stream | `/sim/display/stream` | "continuous": huge size, never 0, so `cat` never EOFs |

**Control files and errno** ([gatOS.SimFs/Commands/CommandFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/CommandFile.cs#L57-L126)): a write is
buffered until the first newline, parsed by the subclass (`ControlFile` for flags, fractions and
ranged numbers, created through `SimFsTree` helpers such as `FlagControl`; `VectorControlFile`,
`EnumControlFile`, `TokenControlFile`, `LineControlFile`, `TriggerFile`), and submitted through `ICommandSink.SubmitAsync`. A parse failure throws
`VfsErrorException(EINVAL)`; a failed command throws with `result.ToErrno()`, which the session
returns as `Rlerror`, so `echo 2 > ctl/throttle` exits non-zero with "Invalid argument". A write
without a trailing newline is executed best-effort at clunk without blocking the clunk reply.

**Guest-side `tail` shim.** GNU `tail -f` follows via inotify, which v9fs never fires for
host-side appends. The guest ships `/usr/local/bin/tail`, which adds `---disable-inotify` whenever
follow mode is requested ([guest/rootfs-overlay/usr/local/bin/tail](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/rootfs-overlay/usr/local/bin/tail#L16-L25)).

## Lifecycle and timing

- All file I/O runs on 9p session tasks (`thread:background`). Sensor content comes from
  `SnapshotStore.Current`; blocking files await `SnapshotStore.WaitForNextAsync`, which completes
  when the game-thread sampler publishes.
- Control writes hold the `Twrite` open until the game thread drains the command (up to
  `command_timeout_ms`), so a script that writes then reads sees the effect, and a paused game turns
  into `ETIMEDOUT` rather than a hang.
- The guest mounts with `cache=none`, so every `stat` and `open` reaches the server.

## Key code

| Path | Role |
|---|---|
| [gatOS.SimFs/SimFsTree.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/SimFsTree.cs) | the whole `/sim` tree definition |
| [gatOS.SimFs/SnapshotTextFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/SnapshotTextFile.cs) | snapshot-memoized sensor leaf |
| [gatOS.SimFs/StreamFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/StreamFile.cs) | growing-log NDJSON stream |
| [gatOS.SimFs/EventsFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/EventsFile.cs) | blocking-event file with zeros-owed bookkeeping |
| [gatOS.SimFs/AlarmFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/AlarmFile.cs) | sim-time alarm |
| [gatOS.SimFs/Commands/CommandFile.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Commands/CommandFile.cs) | writable control base: parse, submit, errno |
| [gatOS.SimFs/Formats.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.SimFs/Formats.cs) | frozen value formats (G9 doubles, 0/1 flags, space-separated vectors) |
| [guest/rootfs-overlay/usr/local/bin/tail](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/guest/rootfs-overlay/usr/local/bin/tail) | inotify-free `tail -f` |

## Snippets

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

```sh
# guest/rootfs-overlay/usr/local/bin/tail L16-L25
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

## Game APIs touched

None directly; values are projections of the snapshot built by
[Telemetry sampler and snapshot store](telemetry-sampler-snapshot.md), and writes reach KSA through
[Game-thread command queue](game-thread-command-queue.md).

## Pitfalls and version notes

- Do not fake a large size for "live" files (old advice); on modern kernels it breaks reads.
- A blocking file must answer the two continuation reads with zero bytes or `cat` never prints.
- Continuous files deliver data only when the reader's whole buffer fills; low-rate consumers
  must read in small chunks (`dd bs=64`).
- `SnapshotTextFile` is only correct for content that changes with a publish; live host state
  (transport ports, display settings) uses an uncached file type.
- The `/sim` surface is a published API with its own specification in the repo
  ([SPEC_9P_FILESYSTEM.md](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/SPEC_9P_FILESYSTEM.md)); gatOS updates it in lockstep with code.

## See also

- [9P2000.L server in C#](ninep-server-in-csharp.md)
- [Snapshot-diff flight events](snapshot-diff-flight-events.md)
- [Game-thread command queue](game-thread-command-queue.md)
- [Screen stream frame capture](screen-stream-frame-capture.md)
- [gatOS overview](../overview.md)
- [Sensor, StreamFile, EventsFile and CommandFile models under Linux netfslib rules](../../../howto/external-io/processes/host-guest-transports-tcp-9p-and-ssh/host-guest-transports-tcp-9p-and-ssh-3.md) - how-to article demonstrating this feature

