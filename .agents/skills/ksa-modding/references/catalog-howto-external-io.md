# External I/O

Everything that connects a KSA mod to the world outside the frame: sampling, exporting and recording telemetry, command queues drained on the game thread, timed scripts and AI agents, in-process HTTP/MQTT/MCP/9p/serial servers, hosted child processes, VMs and terminals, native libraries, HTTP clients and external SDKs.

## Control

Getting commands in: game-thread command queues, timed scripts, AI agents over MCP and typed external clients.

### [Build an external SDK client or autopilot for the game over files and HTTP](howto/external-io/control/build-external-clients-and-autopilots-with-a-typed-sdk/overview.md)

Wrap a mod's external API in a typed client with one Transport interface over files and HTTP, then fly a vessel from a separate process with sim-time pacing, telemetry freshness gates and atomic batch writes

- [Typed SDK with a Transport interface, plus example autopilot programs](howto/external-io/control/build-external-clients-and-autopilots-with-a-typed-sdk/build-external-clients-and-autopilots-with-a-typed-sdk.md) — intermediate

### [Let AI agents drive the game through an MCP server](howto/external-io/control/let-ai-agents-control-the-game-over-mcp/overview.md)

Expose a running KSA game to a coding agent as Model Context Protocol tools, either from an in-process Streamable HTTP endpoint or from a small stdio server that wraps a file bridge and returns screenshots inline

- [Stateless Streamable HTTP MCP endpoint inside the game process](howto/external-io/control/let-ai-agents-control-the-game-over-mcp/let-ai-agents-control-the-game-over-mcp-1.md) — advanced
- [Stdlib Python stdio MCP server wrapping the file bridge, with image tools](howto/external-io/control/let-ai-agents-control-the-game-over-mcp/let-ai-agents-control-the-game-over-mcp-2.md) — intermediate

### [Marshal commands from background threads to the game thread](howto/external-io/control/marshal-commands-to-the-game-thread/overview.md)

Let transport threads submit immutable commands and await a result while every KSA mutation happens at one fixed point in the frame, with a second drain point for writes the vehicle solver would otherwise overwrite

- [Two ConcurrentQueues with TaskCompletionSource, timeout and abandon](howto/external-io/control/marshal-commands-to-the-game-thread/marshal-commands-to-the-game-thread-1.md) — intermediate
- [Solver-window drain in a Priority.First prefix on ExecuteNextVehicleSolvers](howto/external-io/control/marshal-commands-to-the-game-thread/marshal-commands-to-the-game-thread-2.md) — advanced

### [Schedule timed command sequences on render, wall or sim time](howto/external-io/control/schedule-timed-commands/overview.md)

Play back a script of commands at millisecond offsets against a chosen clock, ticked on the game thread just before the command drain

- [Scheduler posting SimCommands before the drain, with render, wall and ut clocks](howto/external-io/control/schedule-timed-commands/schedule-timed-commands.md) — advanced

## Telemetry

Getting game state out safely: immutable snapshots for other threads, derived flight events and recording to files.

### [Detect flight events when the game has no event bus](howto/external-io/telemetry/detect-flight-events-from-snapshots/overview.md)

Derive liftoff, staging, flameout, SOI change, docking and similar events by diffing consecutive samples with allocation-free edge detection, and deliver them to consumers as an ordered stream

- [EventDiffer over consecutive immutable SimSnapshots](howto/external-io/telemetry/detect-flight-events-from-snapshots/detect-flight-events-from-snapshots-1.md) — intermediate
- [Debounced heuristics on burning-engine and part counts for webcast callouts](howto/external-io/telemetry/detect-flight-events-from-snapshots/detect-flight-events-from-snapshots-2.md) — intermediate

### [Export live game state to background threads with immutable snapshots and a fixed sample clock](howto/external-io/telemetry/export-game-state-to-other-threads/overview.md)

Keep transport threads away from KSA objects by building one immutable snapshot on the game thread and publishing it with a single volatile swap, sampled at a drift-free fixed cadence

- [Sampler builds an immutable snapshot, volatile swap, TaskCompletionSource wake](howto/external-io/telemetry/export-game-state-to-other-threads/export-game-state-to-other-threads-1.md) — advanced
- [Plain-struct snapshot sampled after the frame, drawn next frame](howto/external-io/telemetry/export-game-state-to-other-threads/export-game-state-to-other-threads-2.md) — beginner

### [Record telemetry and diagnostics to files without stalling the frame](howto/external-io/telemetry/record-telemetry-and-diagnostics-to-files/overview.md)

Write flight telemetry to files for later analysis, as an on-demand export of a history buffer, a continuous per-step recorder that never throws and never waits on the disk, or one-shot latched validation dumps

- [Wall-clock player-time interval feeding a fixed-length history array](howto/external-io/telemetry/record-telemetry-and-diagnostics-to-files/record-telemetry-and-diagnostics-to-files-1.md) — beginner
- [Static single-owner buffered sink with per-step CSV, plan snapshots and event log](howto/external-io/telemetry/record-telemetry-and-diagnostics-to-files/record-telemetry-and-diagnostics-to-files-2.md) — intermediate
- [Latched dumps from StarMapAfterGui behind a DebugConfig flag](howto/external-io/telemetry/record-telemetry-and-diagnostics-to-files/record-telemetry-and-diagnostics-to-files-3.md) — intermediate

## Processes

Running and binding code that is not managed C#: child processes and virtual machines, host-guest transports and native C libraries.

### [Bind and vendor native C libraries with layout-exact P/Invoke](howto/external-io/processes/bind-and-vendor-native-c-libraries/overview.md)

Call a C or Rust library from a KSA mod safely with layout-exact structs checked against the headers, a startup layout self-check, thread ownership rules, per-RID builds, a vendored and patched upstream binding and the Apple arm64 variadic-call pitfall

- [Vendored libghostty-vt binding with managed packed-cell decoding and a startup self-check](howto/external-io/processes/bind-and-vendor-native-c-libraries/bind-and-vendor-native-c-libraries-1.md) — advanced
- [Clarabel and SCS solvers with layout-exact structs, reused sparse CSC patterns and single-thread ownership](howto/external-io/processes/bind-and-vendor-native-c-libraries/bind-and-vendor-native-c-libraries-2.md) — advanced
- [Pad register arguments for variadic C calls such as ioctl on Apple arm64](howto/external-io/processes/bind-and-vendor-native-c-libraries/bind-and-vendor-native-c-libraries-3.md) — advanced

### [Host and supervise an external process or virtual machine from a KSA mod](howto/external-io/processes/host-and-supervise-an-external-process-from-a-mod/overview.md)

Start a child process from the game without blocking the frame or leaking it, with output pumps, a queued writer, readiness probing and bounded shutdown at unload, shown for a QEMU microVM, a Windows pseudoconsole shell, a Unix pty shell and background shell detection

- [QEMU microVM with async boot state machine, accelerator ladder, qcow2 overlays and a shutdown ladder](howto/external-io/processes/host-and-supervise-an-external-process-from-a-mod/host-and-supervise-an-external-process-from-a-mod-1.md) — advanced
- [Windows ConPTY with a never-sleeping ReadFile pump, queued writer and close-first teardown](howto/external-io/processes/host-and-supervise-an-external-process-from-a-mod/host-and-supervise-an-external-process-from-a-mod-2.md) — advanced
- [POSIX pty via posix_openpt and posix_spawnp with a poll reader and a waitpid thread](howto/external-io/processes/host-and-supervise-an-external-process-from-a-mod/host-and-supervise-an-external-process-from-a-mod-3.md) — advanced
- [ShellCommandResolver, WslDistributionDetector and UnixShellDetector feeding a menu cache](howto/external-io/processes/host-and-supervise-an-external-process-from-a-mod/host-and-supervise-an-external-process-from-a-mod-4.md) — intermediate

### [Connect a VM guest to the game with slirp TCP, a 9P filesystem and pinned SSH](howto/external-io/processes/host-guest-transports-tcp-9p-and-ssh/overview.md)

The channels between a QEMU guest and the game process that work on Windows, Linux and macOS - slirp TCP with pre-allocated host ports, a from-scratch 9P2000.L server with synthetic and confined host files, and SSH sessions pinned to the guest manifest's host key

- [slirp hostfwd and gateway TCP with a PortAllocator and guest-side supervisors](howto/external-io/processes/host-guest-transports-tcp-9p-and-ssh/host-guest-transports-tcp-9p-and-ssh-1.md) — advanced
- [From-scratch 9P2000.L server with a VfsNode tree and HostDirectory confinement](howto/external-io/processes/host-guest-transports-tcp-9p-and-ssh/host-guest-transports-tcp-9p-and-ssh-2.md) — advanced
- [Sensor, StreamFile, EventsFile and CommandFile models under Linux netfslib rules](howto/external-io/processes/host-guest-transports-tcp-9p-and-ssh/host-guest-transports-tcp-9p-and-ssh-3.md) — advanced
- [Pin the guest SSH host key to a manifest value](howto/external-io/processes/host-guest-transports-tcp-9p-and-ssh/host-guest-transports-tcp-9p-and-ssh-4.md) — intermediate

## Network

Talking to networks and devices from inside the game: HTTP and MQTT servers, serial and bus protocols, and outgoing HTTP requests.

### [Bridge a serial port and bus protocols (NDJSON, NMEA, CCSDS, SCPI) to the game](howto/external-io/network/bridge-a-serial-port-and-bus-protocols-to-the-game/overview.md)

Connect a virtio-serial port to the game with NDJSON, NMEA or CCSDS telemetry frames and SCPI-style command lines answered with OK or ERR plus an errno name

- [SerialBridge with a connector per boot, protocol encoders and a SCPI command port](howto/external-io/network/bridge-a-serial-port-and-bus-protocols-to-the-game/bridge-a-serial-port-and-bus-protocols-to-the-game.md) — advanced

### [Host HTTP and MQTT servers in the game process for telemetry and control](howto/external-io/network/host-http-and-mqtt-servers-in-the-game-process/overview.md)

Expose game state and accept commands from outside the game with a raw-socket HTTP server or an embedded MQTT broker that both read snapshots and write through the game-thread queue

- [Raw TcpListener server with SSE, long-poll, OpenAPI and a generic command endpoint](howto/external-io/network/host-http-and-mqtt-servers-in-the-game-process/host-http-and-mqtt-servers-in-the-game-process-1.md) — advanced
- [Embedded broker with subscription-gated changed-only publish and ProcessPublish=false command topic](howto/external-io/network/host-http-and-mqtt-servers-in-the-game-process/host-http-and-mqtt-servers-in-the-game-process-2.md) — advanced

### [Make an HTTP request from a mod and add an in-game bug report form](howto/external-io/network/make-an-http-request-from-a-mod-without-blocking-the-frame/overview.md)

Send an HTTP request from a visible click on a background task, poll plain status fields from the draw each frame and handle load-context quirks of the game runtime

- [FeedbackClient posting JSON with polled Sending/Status fields and reflection for StatusCode](howto/external-io/network/make-an-http-request-from-a-mod-without-blocking-the-frame/make-an-http-request-from-a-mod-without-blocking-the-frame.md) — intermediate

## Terminals

Terminals inside the game: pty sessions handed to the frame, clipboard escape codes and streaming the game screen into a terminal.

### [Host a terminal emulator in the game with pty sessions, custom shells and thread-to-frame handoff](howto/external-io/terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/overview.md)

Run a terminal tab in the game with blocking pty I/O on background threads and every main-thread API on one StarMapAfterGui tick, with bounded queues, custom shells and SSH sessions

- [Map every thread to one StarMapAfterGui tick with configure-before-publish sessions](howto/external-io/terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/host-a-terminal-with-pty-sessions-and-frame-handoff-1.md) — advanced
- [Bounded byte inbox with backpressure and dirty-aware frame snapshots](howto/external-io/terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/host-a-terminal-with-pty-sessions-and-frame-handoff-2.md) — advanced
- [BaseLineBufferedShell that executes lines through the game console](howto/external-io/terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/host-a-terminal-with-pty-sessions-and-frame-handoff-3.md) — intermediate
- [SSH PTY session with lazy VM boot, bounded input writer thread and thread-agnostic output](howto/external-io/terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/host-a-terminal-with-pty-sessions-and-frame-handoff-4.md) — advanced

### [Let a terminal program set the system clipboard with OSC 52](howto/external-io/terminals/let-a-terminal-program-set-the-system-clipboard/overview.md)

Detect OSC 52 sequences in the terminal byte stream with a small scanner and set the system clipboard through ImGui from the main thread

- [OscSidecar tee on the bytes about to reach the engine, clipboard write on the tick](howto/external-io/terminals/let-a-terminal-program-set-the-system-clipboard/let-a-terminal-program-set-the-system-clipboard.md) — intermediate

### [Stream the game screen to a terminal with the Kitty graphics protocol](howto/external-io/terminals/stream-the-game-screen-to-a-terminal/overview.md)

Deliver captured game frames to a terminal session as Kitty graphics images that replace one image id, encoded on a worker task and paced to the terminal

- [Staging-buffer readback on slot reuse, background KittyEncoder, fixed image id](howto/external-io/terminals/stream-the-game-screen-to-a-terminal/stream-the-game-screen-to-a-terminal.md) — advanced
