---
type: Mod Feature
title: Custom shell registry, lifecycle and PTY bridge
description: An ICustomShell interface with a validated, reflection-discovered registry and line-buffered base classes, adapted to the PTY process-manager interface so in-process shells (game console, SSH into a VM) behave exactly like real shells in the terminal
mod: purrtty
category: external-io
techniques: [inter-mod-api, reflection, background-thread]
hooks: [starmap StarMapAllModsLoaded]
lifecycle: [startup:all-mods-loaded, frame:draw-ui, thread:background]
game_types: []
tags: [custom-shell, inter-mod, registry, reflection, plugin, terminal]
---

Not every terminal tab is an OS process. purrTTY's Game Console and gatOS's SSH session are
in-process shells that implement `ICustomShell`. The registry stores factories by id, the menus
list them, and `CustomShellPtyBridge` wraps a shell as an `IProcessManager`, so the session, the
engine and the frontend cannot tell a custom shell from ConPTY or a POSIX pty. This is a compact
pattern for any mod that wants to offer "a console for X" inside purrTTY.

## How it works

**Interface** (`ICustomShell : IDisposable`): `Metadata` (name, description, version, author,
features), `IsRunning`, events `OutputReceived` (bytes plus stdout/stderr) and `Terminated` (exit
code, reason), `StartAsync(CustomShellStartOptions)`, `StopAsync`, `WriteInputAsync(bytes)` (the
encoded terminal input, escape sequences included), `NotifyTerminalResize`, `RequestCancellation`
and `SendInitialOutput` (banner and first prompt after wiring).[^icustomshell]

**Registry** (`CustomShellRegistry.Instance`, process-wide singleton):

- `RegisterShell(id, factory)` creates one probe instance inside `using`, validates metadata (name,
  description, version, author non-empty; not already running), then `TryAdd`s the factory and
  stores the metadata; duplicates throw.[^registry]
- `DiscoverShells()` (idempotent, locked) force-loads the `purrTTY.CustomShells` assembly, scans its
  types for non-abstract `ICustomShell` classes with a parameterless constructor, and registers
  each under its type name unless an explicit registration already took the id. Other mods are
  not scanned; they register explicitly.
- `GetAvailableShells`, `CreateShell(id)`, `IsShellRegistered` all trigger discovery first.
- Logging goes through a `[MethodImpl(NoInlining)]` wrapper inside try/catch, so a missing logging
  assembly (test host) cannot break discovery at JIT time.

**Base classes**:

| Class | Adds |
|---|---|
| `BaseCustomShell` | Running flag under a lock, event raisers, virtual resize/cancel/initial output, dispose |
| `BaseChannelOutputShell` | Unbounded `Channel` of output chunks pumped to `OutputReceived` by a background task; `StopAsync` drains it |
| `BaseLineBufferedShell` | Line editing (cursor, word moves via `ESC b`/`ESC f`, CSI arrows), history, Ctrl+C/H/L/W handling, `ExecuteCommandLine`, `SendOutput`/`SendError`/`SendPrompt`; `WriteInputAsync` completes synchronously |

**Bridge** (`CustomShellPtyBridge : IProcessManager`): `StartAsync` maps launch options to
`CustomShellStartOptions` (size, working directory, env), starts the shell, calls
`SendInitialOutput`, and uses a fresh per-run exit-code source so stop-then-restart works.
`Write` forwards to `WriteInputAsync` and observes faults of async shells; `OutputReceived` becomes
`DataReceived`; `Terminated` becomes `ProcessExited`; `Resize` calls `NotifyTerminalResize`.[^bridge]
`TerminalSessionFactory` picks the bridge when `ShellType == CustomGame` and asks the registry for a
new instance by `CustomShellId`.[^session-factory]

**Configuration without coupling.** Shell settings (the Game Console prompt) travel as launch
environment variables named in `WellKnownShellEnvironment`, so the contract never references
purrTTY's display or config types.[^well-known-env]

## Lifecycle and timing

- Built-in discovery: `GhosttySessionManagerFactory.EnsureGameShellsDiscovered()` in
  `InitializeTerminal` (`startup:all-mods-loaded`).[^manager-factory]
- External registration: the other mod's own `StarMapAllModsLoaded` (gatOS) or any later time;
  the registry is a `ConcurrentDictionary` and the menus read it live.
- Shell start runs on the session-creation pool thread; `WriteInputAsync` runs on the main thread
  in `frame:draw-ui` (input encoding and engine replies), so it must not block; output events may
  come from any thread and are funneled through the surface inbox.

## Key code

| Path | Role |
|---|---|
| [purrTTY.CustomShellContract/ICustomShell.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShellContract/ICustomShell.cs) | The interface |
| [purrTTY.CustomShellContract/CustomShellRegistry.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShellContract/CustomShellRegistry.cs#L42-L77) | Validated registration |
| [purrTTY.CustomShellContract/CustomShellRegistry.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShellContract/CustomShellRegistry.cs#L156-L298) | Reflection discovery |
| [purrTTY.CustomShellContract/Base/BaseLineBufferedShell.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShellContract/Base/BaseLineBufferedShell.cs#L128-L194) | Input state machine |
| [purrTTY.CustomShellContract/Base/BaseChannelOutputShell.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShellContract/Base/BaseChannelOutputShell.cs) | Channel output pump |
| [purrTTY.Terminal/Pty/CustomShellPtyBridge.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/CustomShellPtyBridge.cs#L89-L160) | Start and initial output |
| [purrTTY.Terminal/Pty/CustomShellPtyBridge.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Pty/CustomShellPtyBridge.cs#L211-L251) | Write forwarding |
| [purrTTY.Terminal/Sessions/TerminalSessionFactory.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.Terminal/Sessions/TerminalSessionFactory.cs#L60-L87) | Choosing the bridge |
| [gatOS.Ssh/SshShellSession.cs](https://github.com/meow-sci/gatOS/blob/b88c28588e013841cfec58a1b3a09fcbd2cbc47e/gatOS.Ssh/SshShellSession.cs) | A third-party implementation (gatOS repo) |
| [purrTTY.CustomShellContract.Tests/Property/CustomShellRegistryProperties.cs](https://github.com/meow-sci/purrtty/blob/71da9efe9ed7b8ec533cad7a0bb15bea9f59a9a5/purrTTY.CustomShellContract.Tests/Property/CustomShellRegistryProperties.cs) | FsCheck property tests for the registry |

## Snippets

```csharp
// purrTTY.CustomShellContract/CustomShellRegistry.cs L42-L77
    public void RegisterShell<T>(string shellId, Func<T> factory) where T : class, ICustomShell
    {
        if (string.IsNullOrWhiteSpace(shellId))
        {
            throw new ArgumentException("Shell ID cannot be null or empty", nameof(shellId));
        }

        if (factory == null)
        {
            throw new ArgumentNullException(nameof(factory));
        }

        try
        {
            // Validate the shell implementation by creating a temporary instance
            using var testInstance = factory();
            ValidateShellImplementation(testInstance, shellId);

            // Store the factory atomically: TryAdd makes the duplicate check and
            // the insert one operation (a ContainsKey-then-set pair lets two
            // concurrent registrations of the same ID both pass the guard).
            // Func<T> is covariant, so the factory is stored directly — no
            // wrapping closure needed.
            if (!_shellFactories.TryAdd(shellId, factory))
            {
                throw new ArgumentException($"Shell with ID '{shellId}' is already registered", nameof(shellId));
            }

            // Store metadata from the test instance
            _shellMetadata[shellId] = testInstance.Metadata;
        }
        catch (Exception ex) when (!(ex is ArgumentException || ex is ArgumentNullException))
        {
            throw new CustomShellRegistrationException($"Failed to register shell '{shellId}': {ex.Message}", ex);
        }
    }
```

```csharp
// purrTTY.CustomShellContract/ICustomShell.cs L7-L75 (doc comments elided)
public interface ICustomShell : IDisposable
{
    // ...
    CustomShellMetadata Metadata { get; }
    // ...
    bool IsRunning { get; }
    // ...
    event EventHandler<ShellOutputEventArgs>? OutputReceived;
    // ...
    event EventHandler<ShellTerminatedEventArgs>? Terminated;
    // ...
    Task StartAsync(CustomShellStartOptions options, CancellationToken cancellationToken = default);
    // ...
    Task StopAsync(CancellationToken cancellationToken = default);
    // ...
    Task WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    // ...
    void NotifyTerminalResize(int width, int height);
    // ...
    void RequestCancellation();
    // ...
    void SendInitialOutput();
}
```

## Game APIs touched

None. The contract is game-agnostic by design; the Game Console implementation is where KSA types
appear (see [game console shell](./game-console-shell.md)).

## Pitfalls and version notes

- Registration instantiates a probe: no side effects in constructors, and `Dispose` must work on a
  never-started instance (docs/how-to.md).[^how-to]
- Discovery only scans `purrTTY.CustomShells`; another mod's shells appear only through explicit
  `RegisterShell`.
- `WriteInputAsync` is called from the render thread; a fully asynchronous shell gets no cross-call
  ordering guarantee from the bridge (comment in `Write`).
- Ids are the menu identity; the built-in `GameConsoleShell` id is skipped in the "other mods"
  section of the menu because it already has its own entry.

## See also

- [Inter-mod shell contract shared through StarMap imports](./custom-shell-contract-via-assembly-imports.md)
- [Game console shell](./game-console-shell.md)
- [PTY threads to frame tick handoff](./pty-threads-to-frame-tick-handoff.md)
- [Game menu with ModMenu fallback](./game-menu-with-modmenu-fallback.md)
- [Headless test suites](./headless-test-suites.md)
- [purrTTY overview](../overview.md)
- [BaseLineBufferedShell that executes lines through the game console](../../../howto/external-io/terminals/host-a-terminal-with-pty-sessions-and-frame-handoff/host-a-terminal-with-pty-sessions-and-frame-handoff-3.md) - how-to article demonstrating this feature
- [Share a contract assembly: the importer lists it in ImportedAssemblies](../../../howto/getting-started/dependencies/share-types-between-mods/share-types-between-mods-1.md) - how-to article demonstrating this feature

[^icustomshell]: purrTTY.CustomShellContract/ICustomShell.cs
[^registry]: purrTTY.CustomShellContract/CustomShellRegistry.cs
[^bridge]: purrTTY.Terminal/Pty/CustomShellPtyBridge.cs
[^session-factory]: purrTTY.Terminal/Sessions/TerminalSessionFactory.cs
[^well-known-env]: purrTTY.CustomShellContract/WellKnownShellEnvironment.cs
[^manager-factory]: purrTTY.Display/Ghostty/GhosttySessionManagerFactory.cs
[^how-to]: purrTTY docs/how-to.md
